using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using EC_SceneAudit.Export;

namespace EC_SceneAudit.Repair
{
    internal sealed class NodePatchVerificationItem
    {
        public string Label;
        public readonly List<string> Issues = new List<string>();
        public bool Passed => Issues.Count == 0;
    }

    internal sealed class NodePatchVerificationReport
    {
        public string PatchPath = string.Empty;
        public string OriginalBundlePath = string.Empty;
        public string NewBundlePath = string.Empty;
        public string NewBundleId = string.Empty;
        public long ElapsedMilliseconds;
        public readonly List<string> GlobalIssues = new List<string>();
        public readonly List<NodePatchVerificationItem> Items = new List<NodePatchVerificationItem>();

        public bool Passed
        {
            get
            {
                if (GlobalIssues.Count > 0 || Items.Count == 0)
                    return false;
                foreach (var item in Items)
                {
                    if (!item.Passed)
                        return false;
                }
                return true;
            }
        }

        public int PassedCount
        {
            get
            {
                var count = 0;
                foreach (var item in Items)
                {
                    if (item.Passed)
                        count++;
                }
                return count;
            }
        }

        public string Verdict
        {
            get
            {
                if (GlobalIssues.Count > 0)
                    return "回验未通过：" + GlobalIssues[0];
                if (Items.Count == 0)
                    return "回验未通过：补丁没有目标";
                return Passed
                    ? "回验通过 " + Items.Count + "/" + Items.Count
                    : "回验未通过：一致 " + PassedCount + "/" + Items.Count;
            }
        }
    }

    internal sealed class NodePatchVerifier
    {
        public NodePatchVerificationReport Verify(string patchPath, string originalBundlePath, string newBundlePath)
        {
            var timer = Stopwatch.StartNew();
            var report = new NodePatchVerificationReport
            {
                PatchPath = patchPath ?? string.Empty,
                OriginalBundlePath = originalBundlePath ?? string.Empty,
                NewBundlePath = newBundlePath ?? string.Empty
            };
            NodePatchPlan plan;
            RepairBundleSource original;
            RepairBundleSource latest;
            try
            {
                plan = NodePatchReader.ReadFile(patchPath);
                original = RepairBundleReader.Read(originalBundlePath);
                latest = RepairBundleReader.Read(newBundlePath);
            }
            catch (RepairInputException ex)
            {
                AddIssue(report, "回验输入读取失败：" + ex.Message);
                return Finish(report, timer);
            }
            report.NewBundleId = latest.BundleId;
            if (SameDirectory(originalBundlePath, newBundlePath))
            {
                AddIssue(report, "新 F8 与来源包是同一目录。");
                return Finish(report, timer);
            }
            if (string.Equals(original.BundleId, latest.BundleId, StringComparison.Ordinal))
            {
                AddIssue(report, "新 F8 的 bundleId 与来源包相同，不是应用后的新导出。");
                return Finish(report, timer);
            }
            if (!string.Equals(plan.Source.BundleId, original.BundleId, StringComparison.Ordinal)
                || !string.Equals(plan.Source.ManifestSha256, original.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            {
                AddIssue(report, "补丁来源与提交回验的原始资料包不一致。");
                return Finish(report, timer);
            }

            VerifyConnections(plan, original.Scene, latest.Scene, report);
            VerifyLayout(plan, original.Scene, latest.Scene, report);
            CompareUntouched(plan, original.Scene, latest.Scene, report);
            return Finish(report, timer);
        }

        private static void CompareUntouched(
            NodePatchPlan plan,
            SceneSnapshot source,
            SceneSnapshot latest,
            NodePatchVerificationReport report)
        {
            var sourceNodes = BuildNodeMap(source, report);
            var latestNodes = BuildNodeMap(latest, report);
            if (sourceNodes.Count != latestNodes.Count)
            {
                AddIssue(report, "节点数量变化。");
                return;
            }
            var changedSlots = new HashSet<string>(StringComparer.Ordinal);
            foreach (var connection in plan.Connections)
                changedSlots.Add(connection.SourceUid + "|" + connection.Slot);
            var changedNodes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var placement in plan.Layout?.Nodes ?? new List<NodePlacementPatch>())
                changedNodes.Add(placement.Uid);

            foreach (var pair in sourceNodes)
            {
                NodeSnapshot current;
                if (!latestNodes.TryGetValue(pair.Key, out current))
                {
                    AddIssue(report, "新 F8 缺少节点：" + pair.Key);
                    continue;
                }
                if (!string.Equals(pair.Value.kind ?? string.Empty, current.kind ?? string.Empty, StringComparison.Ordinal))
                    AddIssue(report, "节点类型变化：" + pair.Key);
                if (!changedNodes.Contains(pair.Key)
                    && (pair.Value.position == null || current.position == null
                        || !NodePatchNumbers.NearlyEqual(pair.Value.position.x, current.position.x)
                        || !NodePatchNumbers.NearlyEqual(pair.Value.position.y, current.position.y)))
                    AddIssue(report, "未目标节点位置变化：" + pair.Key);

                foreach (var output in pair.Value.outputs ?? new OutputSlotSnapshot[0])
                {
                    if (output == null || changedSlots.Contains(pair.Key + "|" + output.slot))
                        continue;
                    var after = FindOutput(current, output.slot);
                    if (after == null || !SameOutput(output, after))
                        AddIssue(report, "未目标槽位变化：" + pair.Key + "/slot:" + output.slot);
                }

                var beforeGeometry = FindGeometry(source, pair.Key);
                var afterGeometry = FindGeometry(latest, pair.Key);
                if (beforeGeometry?.status == "exact" || afterGeometry?.status == "exact")
                {
                    if (beforeGeometry?.status != afterGeometry?.status
                        || beforeGeometry?.frame == null || afterGeometry?.frame == null
                        || !NodePatchNumbers.NearlyEqual(beforeGeometry.frame.width, afterGeometry.frame.width)
                        || !NodePatchNumbers.NearlyEqual(beforeGeometry.frame.height, afterGeometry.frame.height)
                        || beforeGeometry.positionOffset == null || afterGeometry.positionOffset == null
                        || !NodePatchNumbers.NearlyEqual(beforeGeometry.positionOffset.x, afterGeometry.positionOffset.x)
                        || !NodePatchNumbers.NearlyEqual(beforeGeometry.positionOffset.y, afterGeometry.positionOffset.y)
                        || (beforeGeometry.input?.enabled ?? false) != (afterGeometry.input?.enabled ?? false))
                        AddIssue(report, "未目标节点几何事实变化：" + pair.Key);
                }
            }
        }

        private static void VerifyConnections(
            NodePatchPlan plan,
            SceneSnapshot source,
            SceneSnapshot latest,
            NodePatchVerificationReport report)
        {
            foreach (var connection in plan.Connections)
            {
                var item = new NodePatchVerificationItem
                {
                    Label = connection.SourceUid + " · slot " + connection.Slot + " -> " + connection.After.TargetUid
                };
                var sourceNode = FindNode(source, connection.SourceUid);
                var latestNode = FindNode(latest, connection.SourceUid);
                var sourceOutput = FindOutput(sourceNode, connection.Slot);
                var latestOutput = FindOutput(latestNode, connection.Slot);
                if (sourceOutput == null || latestOutput == null)
                {
                    item.Issues.Add("来源槽位缺失。");
                }
                else
                {
                    if (!SameOutput(sourceOutput, connection.Before))
                        item.Issues.Add("原 F8 的来源槽位不是补丁 before。");
                    if (!string.Equals(latestOutput.targetUid, connection.After.TargetUid, StringComparison.Ordinal))
                        item.Issues.Add("新 F8 的目标 UID 未变成 after。");
                    if (!string.Equals(latestOutput.conditionType, connection.Before.ConditionType, StringComparison.Ordinal)
                        || latestOutput.conditionCount != connection.Before.ConditionCount
                        || latestOutput.fadeType != connection.Before.FadeType
                        || !NodePatchNumbers.NearlyEqual(latestOutput.fadeTime, connection.Before.FadeTime))
                        item.Issues.Add("连接条件或淡入淡出参数发生变化。");
                }
                if (item.Issues.Count == 0)
                    CompareUntouchedOutputs(sourceNode, latestNode, connection.Slot, item);
                report.Items.Add(item);
            }
        }

        private static void VerifyLayout(
            NodePatchPlan plan,
            SceneSnapshot source,
            SceneSnapshot latest,
            NodePatchVerificationReport report)
        {
            if (plan.Layout == null)
                return;
            var item = new NodePatchVerificationItem
            {
                Label = "布局 · " + plan.Layout.Nodes.Count + " 个节点"
            };
            var sourceNodes = BuildNodeMap(source, report);
            var latestNodes = BuildNodeMap(latest, report);
            if (sourceNodes.Count != latestNodes.Count)
                item.Issues.Add("节点数量变化。");
            var changed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var placement in plan.Layout.Nodes)
                changed.Add(placement.Uid);

            var requiredWidth = Math.Max(source.geometry?.canvas?.width ?? 0f, plan.Layout.Canvas.Width);
            var requiredHeight = Math.Max(source.geometry?.canvas?.height ?? 0f, plan.Layout.Canvas.Height);
            foreach (var pair in latestNodes)
            {
                var sourceNode = sourceNodes.ContainsKey(pair.Key) ? sourceNodes[pair.Key] : null;
                var sourceGeometry = FindGeometry(source, pair.Key);
                var latestGeometry = FindGeometry(latest, pair.Key);
                if (sourceNode?.position == null || pair.Value.position == null || latestGeometry?.status != "exact")
                {
                    item.Issues.Add("节点几何不可用：" + pair.Key);
                    continue;
                }
                if (changed.Contains(pair.Key))
                {
                    var placement = FindPlacement(plan.Layout, pair.Key);
                    if (placement == null
                        || !NodePatchNumbers.NearlyEqual(pair.Value.position.x, placement.After.X)
                        || !NodePatchNumbers.NearlyEqual(pair.Value.position.y, placement.After.Y))
                        item.Issues.Add("节点新位置与 after 不一致：" + pair.Key);
                }
                else if (!NodePatchNumbers.NearlyEqual(pair.Value.position.x, sourceNode.position.x)
                    || !NodePatchNumbers.NearlyEqual(pair.Value.position.y, sourceNode.position.y))
                {
                    item.Issues.Add("未目标节点位置变化：" + pair.Key);
                }
                var frame = latestGeometry.frame;
                if (frame == null || frame.width <= 0f || frame.height <= 0f
                    || frame.x < -NodePatchNumbers.Tolerance || frame.y < -NodePatchNumbers.Tolerance
                    || frame.x + frame.width > requiredWidth + NodePatchNumbers.Tolerance
                    || frame.y + frame.height > requiredHeight + NodePatchNumbers.Tolerance)
                    item.Issues.Add("节点完整矩形超出回验画布：" + pair.Key);
            }
            report.Items.Add(item);
        }

        private static Dictionary<string, NodeSnapshot> BuildNodeMap(SceneSnapshot scene, NodePatchVerificationReport report)
        {
            var result = new Dictionary<string, NodeSnapshot>(StringComparer.Ordinal);
            foreach (var node in scene?.nodes ?? new NodeSnapshot[0])
            {
                if (node == null || string.IsNullOrEmpty(node.uid))
                    continue;
                if (result.ContainsKey(node.uid))
                    AddIssue(report, "节点 UID 重复：" + node.uid);
                else
                    result.Add(node.uid, node);
            }
            return result;
        }

        private static void CompareUntouchedOutputs(
            NodeSnapshot source,
            NodeSnapshot latest,
            int changedSlot,
            NodePatchVerificationItem item)
        {
            foreach (var output in source?.outputs ?? new OutputSlotSnapshot[0])
            {
                if (output == null || output.slot == changedSlot)
                    continue;
                var other = FindOutput(latest, output.slot);
                if (other == null || !SameOutput(output, other))
                    item.Issues.Add("未目标槽位变化：" + output.slot);
            }
        }

        private static NodeSnapshot FindNode(SceneSnapshot scene, string uid)
        {
            return RepairTargetLookup.FindNode(scene, uid);
        }

        private static OutputSlotSnapshot FindOutput(NodeSnapshot node, int slot)
        {
            OutputSlotSnapshot found = null;
            foreach (var output in node?.outputs ?? new OutputSlotSnapshot[0])
            {
                if (output == null || output.slot != slot)
                    continue;
                if (found != null)
                    return null;
                found = output;
            }
            return found;
        }

        private static GeometryNodeSnapshot FindGeometry(SceneSnapshot scene, string uid)
        {
            GeometryNodeSnapshot found = null;
            foreach (var geometry in scene?.geometry?.nodes ?? new GeometryNodeSnapshot[0])
            {
                if (geometry == null || !string.Equals(geometry.uid, uid, StringComparison.Ordinal))
                    continue;
                if (found != null)
                    return null;
                found = geometry;
            }
            return found;
        }

        private static NodePlacementPatch FindPlacement(NodeLayoutPatch layout, string uid)
        {
            foreach (var placement in layout?.Nodes ?? new List<NodePlacementPatch>())
            {
                if (placement != null && string.Equals(placement.Uid, uid, StringComparison.Ordinal))
                    return placement;
            }
            return null;
        }

        private static bool SameOutput(OutputSlotSnapshot left, OutputSlotSnapshot right)
        {
            return left != null && right != null
                && string.Equals(left.targetUid ?? string.Empty, right.targetUid ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(left.conditionType ?? string.Empty, right.conditionType ?? string.Empty, StringComparison.Ordinal)
                && left.conditionCount == right.conditionCount
                && left.fadeType == right.fadeType
                && NodePatchNumbers.NearlyEqual(left.fadeTime, right.fadeTime);
        }

        private static bool SameOutput(OutputSlotSnapshot output, NodeSlotValue expected)
        {
            return output != null && expected != null
                && string.Equals(output.targetUid ?? string.Empty, expected.TargetUid ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(output.conditionType ?? string.Empty, expected.ConditionType ?? string.Empty, StringComparison.Ordinal)
                && output.conditionCount == expected.ConditionCount
                && output.fadeType == expected.FadeType
                && NodePatchNumbers.NearlyEqual(output.fadeTime, expected.FadeTime);
        }

        private static bool SameDirectory(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;
            try
            {
                var a = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var b = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static void AddIssue(NodePatchVerificationReport report, string message)
        {
            report.GlobalIssues.Add(message);
        }

        private static NodePatchVerificationReport Finish(NodePatchVerificationReport report, Stopwatch timer)
        {
            report.ElapsedMilliseconds = timer.ElapsedMilliseconds;
            return report;
        }
    }
}
