using System;
using System.Collections.Generic;
using System.Globalization;
using EC_SceneAudit.Export;

namespace EC_SceneAudit.Repair
{
    internal sealed class NodePatchPreflightItem
    {
        public int Index;
        public string Kind;
        public string Label;
        public readonly List<string> Issues = new List<string>();

        public bool Passed => Issues.Count == 0;
    }

    internal sealed class NodePatchPreflightReport
    {
        public string PatchPath = string.Empty;
        public string BundlePath = string.Empty;
        public string BundleId = string.Empty;
        public string ManifestSha256 = string.Empty;
        public string SceneTitle = string.Empty;
        public int ConnectionCount;
        public int LayoutCount;
        public bool CanvasSupportsAbsoluteResize;
        public bool RequiresCanvasExpansion;
        public float CurrentCanvasWidth;
        public float CurrentCanvasHeight;
        public float RequiredCanvasWidth;
        public float RequiredCanvasHeight;
        public readonly List<string> GlobalIssues = new List<string>();
        public readonly List<NodePatchPreflightItem> Items = new List<NodePatchPreflightItem>();

        public bool Passed
        {
            get
            {
                if (GlobalIssues.Count > 0)
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
                    return "预检未通过：" + GlobalIssues[0];
                if (Items.Count == 0)
                    return "补丁没有可核验的修改";
                return Passed
                    ? "通过 " + Items.Count + "/" + Items.Count
                    : "冲突 " + (Items.Count - PassedCount) + "/" + Items.Count;
            }
        }
    }

    // 只读节点补丁预检：来源包、当前 Scene、几何和槽位都按 UID/序号重新定位。
    internal sealed class NodePatchPreflight
    {
        private const float Tolerance = NodePatchNumbers.Tolerance;

        public NodePatchPreflightReport Evaluate(
            NodePatchPlan plan,
            string patchPath,
            string bundlePath,
            RepairBundleSource bundle,
            SceneSnapshot current,
            bool canvasSupportsAbsoluteResize)
        {
            var report = new NodePatchPreflightReport
            {
                PatchPath = patchPath ?? string.Empty,
                BundlePath = bundlePath ?? string.Empty,
                BundleId = bundle?.BundleId ?? string.Empty,
                ManifestSha256 = bundle?.ManifestSha256 ?? string.Empty,
                SceneTitle = bundle?.Scene?.scene?.title ?? string.Empty,
                ConnectionCount = plan?.Connections.Count ?? 0,
                LayoutCount = plan?.Layout?.Nodes.Count ?? 0,
                CanvasSupportsAbsoluteResize = canvasSupportsAbsoluteResize
            };
            if (plan == null || bundle?.Scene == null || current == null)
            {
                report.GlobalIssues.Add("补丁、来源资料包或当前 Scene 不可用。");
                return report;
            }
            if (!string.Equals(plan.Source.BundleId, bundle.BundleId, StringComparison.Ordinal))
                report.GlobalIssues.Add("补丁属于另一批资料：补丁 bundleId 与来源包不一致。");
            if (!string.Equals(plan.Source.ManifestSha256, bundle.ManifestSha256, StringComparison.OrdinalIgnoreCase))
                report.GlobalIssues.Add("补丁 manifestSha256 与来源包不一致。");
            if (report.GlobalIssues.Count > 0)
                return report;

            var source = bundle.Scene;
            if (!CheckSceneStructure(source, current, report))
                return report;
            ValidateNodeState(source, current, plan, report);

            var itemIndex = 0;
            foreach (var connection in plan.Connections)
            {
                var item = new NodePatchPreflightItem
                {
                    Index = itemIndex++,
                    Kind = "connection",
                    Label = connection.SourceUid + " · slot " + connection.Slot
                };
                ValidateConnection(source, current, connection, item);
                report.Items.Add(item);
            }

            if (plan.Layout != null)
            {
                var layoutItem = new NodePatchPreflightItem
                {
                    Index = itemIndex,
                    Kind = "layout",
                    Label = "布局 · " + plan.Layout.Nodes.Count + " 个节点"
                };
                ValidateLayout(source, current, plan.Layout, report, layoutItem);
                report.Items.Add(layoutItem);
            }
            else if (source.geometry?.canvas != null)
            {
                report.CurrentCanvasWidth = current.geometry?.canvas?.width ?? 0f;
                report.CurrentCanvasHeight = current.geometry?.canvas?.height ?? 0f;
                report.RequiredCanvasWidth = source.geometry.canvas.width;
                report.RequiredCanvasHeight = source.geometry.canvas.height;
            }
            return report;
        }

        // 当前节点集合、类型与位置必须与来源包一致；逐项 before 只覆盖被修改的槽，
        // 这里补上连接-only 补丁的全部节点位置与未目标五槽。
        private static void ValidateNodeState(
            SceneSnapshot source,
            SceneSnapshot current,
            NodePatchPlan plan,
            NodePatchPreflightReport report)
        {
            var sourceNodes = BuildNodeMap(source, report, "来源");
            var currentNodes = BuildNodeMap(current, report, "当前");
            var targetedSlots = new HashSet<string>(StringComparer.Ordinal);
            foreach (var connection in plan.Connections)
                targetedSlots.Add(connection.SourceUid + "|" + connection.Slot);
            foreach (var pair in sourceNodes)
            {
                NodeSnapshot now;
                if (!currentNodes.TryGetValue(pair.Key, out now))
                    continue;
                // 连接-only 补丁不动位置，其余节点位置由布局核验负责。
                if (plan.Layout == null && !SamePosition(pair.Value.position, now.position))
                    report.GlobalIssues.Add("当前节点位置与来源包不一致：" + pair.Key);
                foreach (var output in pair.Value.outputs ?? new OutputSlotSnapshot[0])
                {
                    if (output == null)
                        continue;
                    var key = pair.Key + "|" + output.slot;
                    if (targetedSlots.Contains(key))
                        continue;
                    if (!SameOutput(FindOutput(now, output.slot), output))
                        report.GlobalIssues.Add("当前 Scene 的未目标槽位与来源包不一致：" + key);
                }
                foreach (var output in now.outputs ?? new OutputSlotSnapshot[0])
                {
                    if (output == null)
                        continue;
                    if (targetedSlots.Contains(pair.Key + "|" + output.slot))
                        continue;
                    if (FindOutput(pair.Value, output.slot) == null)
                        report.GlobalIssues.Add("当前 Scene 新增了来源包没有的槽位：" + pair.Key + "|" + output.slot);
                }
            }
        }

        private static bool SamePosition(Vector2Snapshot left, Vector2Snapshot right)
        {
            return left != null && right != null
                && NearlyEqual(left.x, right.x) && NearlyEqual(left.y, right.y);
        }

        private static bool SameOutput(OutputSlotSnapshot left, OutputSlotSnapshot right)
        {
            return left != null && right != null
                && string.Equals(left.targetUid ?? string.Empty, right.targetUid ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(left.conditionType ?? string.Empty, right.conditionType ?? string.Empty, StringComparison.Ordinal)
                && left.conditionCount == right.conditionCount
                && left.fadeType == right.fadeType
                && NearlyEqual(left.fadeTime, right.fadeTime);
        }

        private static bool CheckSceneStructure(
            SceneSnapshot source,
            SceneSnapshot current,
            NodePatchPreflightReport report)
        {
            if (source.nodes == null || current.nodes == null)
            {
                report.GlobalIssues.Add("来源或当前 Scene 缺少 nodes 数组。");
                return false;
            }
            if (!string.Equals(source.scene?.title ?? string.Empty, current.scene?.title ?? string.Empty, StringComparison.Ordinal))
                report.GlobalIssues.Add("当前 Scene 标题与来源资料包不一致。");

            var sourceNodes = BuildNodeMap(source, report, "来源");
            var currentNodes = BuildNodeMap(current, report, "当前");
            if (sourceNodes.Count != source.nodes.Length || currentNodes.Count != current.nodes.Length)
                report.GlobalIssues.Add("来源或当前 Scene 的节点 UID 缺失或重复。");
            foreach (var pair in sourceNodes)
            {
                NodeSnapshot currentNode;
                if (!currentNodes.TryGetValue(pair.Key, out currentNode))
                {
                    report.GlobalIssues.Add("当前 Scene 缺少节点：" + pair.Key);
                    continue;
                }
                if (!string.Equals(pair.Value.kind ?? string.Empty, currentNode.kind ?? string.Empty, StringComparison.Ordinal))
                    report.GlobalIssues.Add("节点类型变化：" + pair.Key);
            }
            foreach (var pair in currentNodes)
            {
                if (!sourceNodes.ContainsKey(pair.Key))
                    report.GlobalIssues.Add("当前 Scene 新增节点：" + pair.Key);
            }
            return report.GlobalIssues.Count == 0;
        }

        private static void ValidateConnection(
            SceneSnapshot source,
            SceneSnapshot current,
            NodeConnectionPatch connection,
            NodePatchPreflightItem item)
        {
            var sourceNode = FindNode(source, connection.SourceUid);
            var currentNode = FindNode(current, connection.SourceUid);
            if (sourceNode == null || currentNode == null)
            {
                item.Issues.Add("找不到来源节点：" + connection.SourceUid);
                return;
            }
            var sourceOutput = FindOutput(sourceNode, connection.Slot);
            var currentOutput = FindOutput(currentNode, connection.Slot);
            if (sourceOutput == null || currentOutput == null)
            {
                item.Issues.Add("来源节点缺少槽位 " + connection.Slot + "。");
                return;
            }
            if (!SameOutput(sourceOutput, connection.Before))
                item.Issues.Add("来源包的槽位值与补丁 before 不一致。");
            if (!SameOutput(currentOutput, connection.Before))
                item.Issues.Add("当前 Scene 的槽位值与补丁 before 不一致。");

            var sourceGeometry = FindGeometry(source, connection.SourceUid);
            var currentGeometry = FindGeometry(current, connection.SourceUid);
            var sourcePort = FindGeometryOutput(sourceGeometry, connection.Slot);
            var currentPort = FindGeometryOutput(currentGeometry, connection.Slot);
            if (sourceGeometry?.status != "exact" || currentGeometry?.status != "exact"
                || sourcePort == null || currentPort == null
                || !sourcePort.enabled || !currentPort.enabled)
                item.Issues.Add("来源槽位在运行时不是可用的 enabled 状态。");

            if (!HasAcceptingInput(source, connection.Before.TargetUid)
                || !HasAcceptingInput(current, connection.Before.TargetUid))
                item.Issues.Add("原目标节点缺少可用的输入端口：" + connection.Before.TargetUid);
            if (!HasNode(source, connection.After.TargetUid)
                || !HasNode(current, connection.After.TargetUid))
                item.Issues.Add("新目标节点不存在：" + connection.After.TargetUid);
            else if (string.Equals(connection.After.TargetUid, connection.SourceUid, StringComparison.Ordinal))
                item.Issues.Add("新目标不能是来源节点自身：" + connection.SourceUid);
            else if (!HasAcceptingInput(current, connection.After.TargetUid))
                item.Issues.Add("新目标节点缺少可用的输入端口：" + connection.After.TargetUid);
        }

        private static void ValidateLayout(
            SceneSnapshot source,
            SceneSnapshot current,
            NodeLayoutPatch layout,
            NodePatchPreflightReport report,
            NodePatchPreflightItem item)
        {
            var sourceCanvas = source.geometry?.canvas;
            var currentCanvas = current.geometry?.canvas;
            if (sourceCanvas == null || currentCanvas == null)
            {
                item.Issues.Add("来源或当前 Scene 缺少 geometry.canvas。");
                return;
            }
            report.CurrentCanvasWidth = currentCanvas.width;
            report.CurrentCanvasHeight = currentCanvas.height;
            report.RequiredCanvasWidth = Math.Max(currentCanvas.width, layout.Canvas.Width);
            report.RequiredCanvasHeight = Math.Max(currentCanvas.height, layout.Canvas.Height);
            report.RequiresCanvasExpansion =
                report.RequiredCanvasWidth > currentCanvas.width + Tolerance
                || report.RequiredCanvasHeight > currentCanvas.height + Tolerance;
            if (report.RequiresCanvasExpansion && !report.CanvasSupportsAbsoluteResize)
                item.Issues.Add("目标画布大于当前画布，但未找到可用的绝对尺寸扩展 API。");

            var sourceNodes = BuildNodeMap(source, report, "来源");
            var currentNodes = BuildNodeMap(current, report, "当前");
            var changedUids = new HashSet<string>(StringComparer.Ordinal);
            var expectedFrames = new Dictionary<string, GeometryFrameSnapshot>(StringComparer.Ordinal);
            foreach (var placement in layout.Nodes)
                changedUids.Add(placement.Uid);
            foreach (var placement in layout.Nodes)
            {
                if (string.IsNullOrEmpty(placement.Uid) || !sourceNodes.ContainsKey(placement.Uid)
                    || !currentNodes.ContainsKey(placement.Uid))
                    item.Issues.Add("布局项指向不存在的节点：" + placement.Uid);
            }

            foreach (var pair in currentNodes)
            {
                var sourceNode = sourceNodes.ContainsKey(pair.Key) ? sourceNodes[pair.Key] : null;
                var sourceGeometry = FindGeometry(source, pair.Key);
                var currentGeometry = FindGeometry(current, pair.Key);
                if (sourceNode == null || sourceGeometry?.status != "exact" || currentGeometry?.status != "exact"
                    || sourceNode.position == null || pair.Value.position == null)
                {
                    item.Issues.Add("节点几何不完整：" + pair.Key);
                    continue;
                }

                var expectedPosition = pair.Value.position;
                var expectedOffset = sourceGeometry.positionOffset;
                if (changedUids.Contains(pair.Key))
                {
                    var placement = FindPlacement(layout, pair.Key);
                    if (placement == null)
                    {
                        item.Issues.Add("缺少布局项：" + pair.Key);
                        continue;
                    }
                    if (!NearlyEqual(sourceNode.position.x, placement.Before.X)
                        || !NearlyEqual(sourceNode.position.y, placement.Before.Y)
                        || !NearlyEqual(pair.Value.position.x, placement.Before.X)
                        || !NearlyEqual(pair.Value.position.y, placement.Before.Y))
                    {
                        item.Issues.Add("节点 before 与来源或当前位置不一致：" + pair.Key);
                        continue;
                    }
                    expectedPosition = new Vector2Snapshot { x = placement.After.X, y = placement.After.Y };
                    expectedOffset = sourceGeometry.positionOffset;
                }
                else if (!NearlyEqual(sourceNode.position.x, pair.Value.position.x)
                    || !NearlyEqual(sourceNode.position.y, pair.Value.position.y))
                {
                    item.Issues.Add("未目标节点位置已变化：" + pair.Key);
                }

                if (!NearlyEqual(sourceGeometry.frame.width, currentGeometry.frame.width)
                    || !NearlyEqual(sourceGeometry.frame.height, currentGeometry.frame.height)
                    || !NearlyEqual(sourceGeometry.positionOffset.x, currentGeometry.positionOffset.x)
                    || !NearlyEqual(sourceGeometry.positionOffset.y, currentGeometry.positionOffset.y))
                {
                    item.Issues.Add("节点尺寸或原生偏移发生变化：" + pair.Key);
                }
                if (!NearlyEqual(currentGeometry.frame.x, pair.Value.position.x + currentGeometry.positionOffset.x)
                    || !NearlyEqual(currentGeometry.frame.y, -pair.Value.position.y + currentGeometry.positionOffset.y))
                {
                    item.Issues.Add("当前节点位置与 frame/offset 不一致：" + pair.Key);
                }
                var expectedFrame = new GeometryFrameSnapshot
                {
                    x = expectedPosition.x + expectedOffset.x,
                    y = -expectedPosition.y + expectedOffset.y,
                    width = sourceGeometry.frame.width,
                    height = sourceGeometry.frame.height
                };
                if (!IsFinite(expectedFrame.x) || !IsFinite(expectedFrame.y)
                    || !(expectedFrame.width > 0f) || !(expectedFrame.height > 0f))
                {
                    item.Issues.Add("节点目标矩形不是有效有限值：" + pair.Key);
                    continue;
                }
                expectedFrames[pair.Key] = expectedFrame;
                if (expectedFrame.x < -Tolerance || expectedFrame.y < -Tolerance
                    || expectedFrame.x + expectedFrame.width > report.RequiredCanvasWidth + Tolerance
                    || expectedFrame.y + expectedFrame.height > report.RequiredCanvasHeight + Tolerance)
                    item.Issues.Add("节点完整矩形超出目标画布：" + pair.Key);
            }
        }

        private static Dictionary<string, NodeSnapshot> BuildNodeMap(
            SceneSnapshot scene,
            NodePatchPreflightReport report,
            string label)
        {
            var result = new Dictionary<string, NodeSnapshot>(StringComparer.Ordinal);
            foreach (var node in scene?.nodes ?? new NodeSnapshot[0])
            {
                if (node == null || string.IsNullOrEmpty(node.uid))
                    continue;
                if (result.ContainsKey(node.uid))
                    report.GlobalIssues.Add(label + " Scene 节点 UID 重复：" + node.uid);
                else
                    result.Add(node.uid, node);
            }
            return result;
        }

        private static NodeSnapshot FindNode(SceneSnapshot scene, string uid)
        {
            return RepairTargetLookup.FindNode(scene, uid);
        }

        private static bool HasNode(SceneSnapshot scene, string uid)
        {
            return FindNode(scene, uid) != null && FindGeometry(scene, uid) != null;
        }

        private static GeometryNodeSnapshot FindGeometry(SceneSnapshot scene, string uid)
        {
            var found = null as GeometryNodeSnapshot;
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

        private static GeometryOutputPointSnapshot FindGeometryOutput(GeometryNodeSnapshot geometry, int slot)
        {
            GeometryOutputPointSnapshot found = null;
            foreach (var output in geometry?.outputs ?? new GeometryOutputPointSnapshot[0])
            {
                if (output == null || output.slot != slot)
                    continue;
                if (found != null)
                    return null;
                found = output;
            }
            return found;
        }

        private static NodePlacementPatch FindPlacement(NodeLayoutPatch layout, string uid)
        {
            NodePlacementPatch found = null;
            foreach (var placement in layout?.Nodes ?? new List<NodePlacementPatch>())
            {
                if (placement == null || !string.Equals(placement.Uid, uid, StringComparison.Ordinal))
                    continue;
                if (found != null)
                    return null;
                found = placement;
            }
            return found;
        }

        private static bool HasAcceptingInput(SceneSnapshot scene, string uid)
        {
            var geometry = FindGeometry(scene, uid);
            return geometry?.status == "exact" && geometry.input != null && geometry.input.enabled;
        }

        private static bool SameOutput(OutputSlotSnapshot output, NodeSlotValue expected)
        {
            return output != null && expected != null
                && string.Equals(output.targetUid ?? string.Empty, expected.TargetUid ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(output.conditionType ?? string.Empty, expected.ConditionType ?? string.Empty, StringComparison.Ordinal)
                && output.conditionCount == expected.ConditionCount
                && output.fadeType == expected.FadeType
                && NearlyEqual(output.fadeTime, expected.FadeTime);
        }

        private static bool NearlyEqual(float left, float right)
        {
            return Math.Abs(left - right) <= Tolerance;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
