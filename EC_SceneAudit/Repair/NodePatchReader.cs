using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace EC_SceneAudit.Repair
{
    // 版本、字段、槽位、重复项和数值范围在入口一次拒绝，UI 与事务不再各自解释。
    internal static class NodePatchReader
    {
        public const int MaxBytes = 64 * 1024 * 1024;
        public const int MaxConnections = 1000;
        public const int MaxLayoutNodes = 1000;

        private static readonly Regex Sha256Pattern = new Regex(
            "^[0-9a-fA-F]{64}$",
            RegexOptions.CultureInvariant);

        // 正式格式直接来自 F8 导出的 EndConditionType.ToString()（Assembly-CSharp/YS_Node/EndConditionType.cs）；
        // 这里是跨语言契约枚举名，不能用 none/数字等别名替代。
        private static readonly HashSet<string> ConditionTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "NotUsed", "ConnectPart",
            "NextPart_Root01", "NextPart_Root02", "NextPart_Root03", "NextPart_Root04",
            "Select_Root01", "Select_Root02", "Select_Root03", "Select_Root04",
            "AllRoot"
        };

        public static NodePatchPlan ReadFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new RepairInputException("节点补丁路径为空。");
            var full = Path.GetFullPath(path);
            if (!File.Exists(full))
                throw new RepairInputException("找不到节点补丁文件：" + full);
            var bytes = RepairPlanReader.ReadBounded(full, MaxBytes, "节点补丁");
            var plan = Parse(RepairPlanReader.DecodeUtf8(bytes, "节点补丁"));
            plan.SourcePath = full;
            return plan;
        }

        public static NodePatchPlan Parse(string json)
        {
            var root = RepairJson.Parse(json);
            if (!root.IsObject)
                throw new RepairInputException("节点补丁必须是 JSON 对象。");
            var format = RequiredString(root, "format", "节点补丁");
            if (format != NodePatchPlan.Format)
                throw new RepairInputException("不支持的节点补丁格式：" + format);
            RejectUnknown(root, "节点补丁", "format", "version", "source", "connections", "layout");
            if (!TryInteger(root, "version", out var version))
                throw new RepairInputException("节点补丁缺少整数 version。");
            if (version != NodePatchPlan.Version)
                throw new RepairInputException("不支持的节点补丁版本：" + version);

            var sourceValue = RequiredObject(root, "source", "节点补丁");
            RejectUnknown(sourceValue, "节点补丁 source", "bundleId", "manifestSha256");
            var source = new NodePatchSource
            {
                BundleId = RequiredString(sourceValue, "bundleId", "节点补丁 source"),
                ManifestSha256 = RequiredString(sourceValue, "manifestSha256", "节点补丁 source")
            };
            if (source.BundleId.Length == 0)
                throw new RepairInputException("节点补丁 source.bundleId 不能为空。");
            if (!Sha256Pattern.IsMatch(source.ManifestSha256))
                throw new RepairInputException("节点补丁 source.manifestSha256 必须是 64 位十六进制。");

            var plan = new NodePatchPlan { Source = source };
            ReadConnections(root, plan);
            ReadLayout(root, plan);
            if (plan.Connections.Count == 0 && (plan.Layout == null || plan.Layout.Nodes.Count == 0))
                throw new RepairInputException("节点补丁至少需要一项连接或布局修改。");
            return plan;
        }

        private static void ReadConnections(RepairJsonValue root, NodePatchPlan plan)
        {
            var value = root.Member("connections");
            if (value == null || value.IsNull)
                return;
            if (!value.IsArray)
                throw new RepairInputException("节点补丁 connections 必须是数组。");
            if (value.Count > MaxConnections)
                throw new RepairInputException("节点补丁连接项最多 " + MaxConnections + " 条。");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < value.Count; index++)
            {
                var path = "节点补丁 connections[" + index + "]";
                var item = RequiredObjectValue(value.Items[index], path);
                RejectUnknown(item, path, "sourceUid", "slot", "before", "after");
                var sourceUid = RequiredString(item, "sourceUid", path);
                if (sourceUid.Length == 0)
                    throw new RepairInputException(path + ".sourceUid 不能为空。");
                var slot = RequiredInteger(item, "slot", path);
                if (slot < 0 || slot > 4)
                    throw new RepairInputException(path + ".slot 必须在 0..4。");
                if (!seen.Add(sourceUid + "|" + slot))
                    throw new RepairInputException(path + " 重复修改同一来源槽位。");

                var beforeValue = RequiredObject(item, "before", path);
                RejectUnknown(beforeValue, path + ".before", "targetUid", "conditionType", "conditionCount", "fadeType", "fadeTime");
                // before.targetUid 为空表示当前槽未连接，本补丁为该槽创建目标。
                var targetUid = RequiredString(beforeValue, "targetUid", path + ".before");
                var afterValue = RequiredObject(item, "after", path);
                RejectUnknown(afterValue, path + ".after", "targetUid");
                var afterUid = RequiredString(afterValue, "targetUid", path + ".after");
                // after.targetUid 为空表示断开该槽；此时 before 必须有目标，否则是无变化项。
                if (afterUid.Length == 0 && targetUid.Length == 0)
                    throw new RepairInputException(path + " 的 before 与 after 都为空，属于无变化项。");
                if (afterUid.Length > 0 && string.Equals(targetUid, afterUid, StringComparison.Ordinal))
                    throw new RepairInputException(path + " 的 after.targetUid 与 before 相同，属于无变化项。");
                if (afterUid.Length > 0 && string.Equals(sourceUid, afterUid, StringComparison.Ordinal))
                    throw new RepairInputException(path + ".after.targetUid 不能指向来源节点自身。");

                var conditionType = RequiredString(beforeValue, "conditionType", path + ".before");
                if (!ConditionTypes.Contains(conditionType))
                    throw new RepairInputException(path + ".before.conditionType 不是 F8 导出的枚举名：" + conditionType + "。");
                plan.Connections.Add(new NodeConnectionPatch
                {
                    SourceUid = sourceUid,
                    Slot = (int)slot,
                    Before = new NodeSlotValue
                    {
                        TargetUid = targetUid,
                        ConditionType = conditionType,
                        ConditionCount = RequiredInt32(beforeValue, "conditionCount", path + ".before"),
                        FadeType = RequiredInt32(beforeValue, "fadeType", path + ".before"),
                        FadeTime = RequiredFloat(beforeValue, "fadeTime", path + ".before")
                    },
                    After = new NodeConnectionTarget { TargetUid = afterUid }
                });
            }
        }

        private static void ReadLayout(RepairJsonValue root, NodePatchPlan plan)
        {
            var value = root.Member("layout");
            if (value == null || value.IsNull)
                return;
            if (!value.IsObject)
                throw new RepairInputException("节点补丁 layout 必须是 JSON 对象或 null。");
            RejectUnknown(value, "节点补丁 layout", "canvas", "nodes");
            var canvasValue = RequiredObject(value, "canvas", "节点补丁 layout");
            RejectUnknown(canvasValue, "节点补丁 layout.canvas", "width", "height");
            var width = RequiredFloat(canvasValue, "width", "节点补丁 layout.canvas");
            var height = RequiredFloat(canvasValue, "height", "节点补丁 layout.canvas");
            if (width <= 0f || height <= 0f)
                throw new RepairInputException("节点补丁 layout.canvas 宽高必须是正有限数。");

            var nodesValue = value.Member("nodes");
            if (nodesValue == null || !nodesValue.IsArray)
                throw new RepairInputException("节点补丁 layout.nodes 必须是数组。");
            if (nodesValue.Count == 0 || nodesValue.Count > MaxLayoutNodes)
                throw new RepairInputException("节点补丁 layout.nodes 数量必须在 1.." + MaxLayoutNodes + "。");

            var layout = new NodeLayoutPatch
            {
                Canvas = new NodeCanvasValue { Width = width, Height = height }
            };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < nodesValue.Count; index++)
            {
                var path = "节点补丁 layout.nodes[" + index + "]";
                var item = RequiredObjectValue(nodesValue.Items[index], path);
                RejectUnknown(item, path, "uid", "before", "after");
                var uid = RequiredString(item, "uid", path);
                if (uid.Length == 0)
                    throw new RepairInputException(path + ".uid 不能为空。");
                if (!seen.Add(uid))
                    throw new RepairInputException(path + ".uid 重复：" + uid + "。");
                var beforeValue = RequiredObject(item, "before", path);
                var afterValue = RequiredObject(item, "after", path);
                RejectUnknown(beforeValue, path + ".before", "x", "y");
                RejectUnknown(afterValue, path + ".after", "x", "y");
                var before = ReadPosition(beforeValue, path + ".before");
                var after = ReadPosition(afterValue, path + ".after");
                if (NearlyEqual(before.X, after.X) && NearlyEqual(before.Y, after.Y))
                    throw new RepairInputException(path + " 的位置 before/after 相同，属于无变化项。");
                layout.Nodes.Add(new NodePlacementPatch { Uid = uid, Before = before, After = after });
            }
            plan.Layout = layout;
        }

        private static NodePositionValue ReadPosition(RepairJsonValue value, string path)
        {
            return new NodePositionValue
            {
                X = RequiredFloat(value, "x", path),
                Y = RequiredFloat(value, "y", path)
            };
        }

        private static bool NearlyEqual(float left, float right)
        {
            return Math.Abs(left - right) <= 0.0001f;
        }

        // 严格 reader：未知字段一律拒绝，避免未来字段被旧版本静默忽略后产生误应用。
        private static void RejectUnknown(RepairJsonValue value, string path, params string[] allowed)
        {
            if (value?.Members == null)
                return;
            var names = new HashSet<string>(allowed, StringComparer.Ordinal);
            foreach (var key in value.Members.Keys)
            {
                if (!names.Contains(key))
                    throw new RepairInputException(path + " 包含未知字段：" + key + "。");
            }
        }

        private static RepairJsonValue RequiredObject(RepairJsonValue parent, string name, string path)
        {
            var value = parent.Member(name);
            if (value == null || !value.IsObject)
                throw new RepairInputException(path + "." + name + " 必须是 JSON 对象。");
            return value;
        }

        private static RepairJsonValue RequiredObjectValue(RepairJsonValue value, string path)
        {
            if (value == null || !value.IsObject)
                throw new RepairInputException(path + " 必须是 JSON 对象。");
            return value;
        }

        private static string RequiredString(RepairJsonValue parent, string name, string path)
        {
            var value = parent.Member(name);
            if (value == null || !value.IsString)
                throw new RepairInputException(path + "." + name + " 必须是文本。");
            return value.Text;
        }

        private static int RequiredInt32(RepairJsonValue parent, string name, string path)
        {
            var value = RequiredInteger(parent, name, path);
            if (value < int.MinValue || value > int.MaxValue)
                throw new RepairInputException(path + "." + name + " 超出 int 范围。");
            return (int)value;
        }

        private static long RequiredInteger(RepairJsonValue parent, string name, string path)
        {
            var value = parent.Member(name);
            if (value == null || !value.IsNumber || !value.TryGetInteger(out var number))
                throw new RepairInputException(path + "." + name + " 必须是整数。");
            return number;
        }

        private static bool TryInteger(RepairJsonValue parent, string name, out long value)
        {
            value = 0;
            var member = parent.Member(name);
            return member != null && member.IsNumber && member.TryGetInteger(out value);
        }

        private static float RequiredFloat(RepairJsonValue parent, string name, string path)
        {
            var value = parent.Member(name);
            if (value == null || !value.IsNumber || value.Text == null
                || !float.TryParse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                || float.IsNaN(parsed) || float.IsInfinity(parsed))
                throw new RepairInputException(path + "." + name + " 必须是有限数字。");
            return parsed;
        }
    }
}
