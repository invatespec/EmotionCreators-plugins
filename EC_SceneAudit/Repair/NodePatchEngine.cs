using System;
using System.Collections.Generic;

namespace EC_SceneAudit.Repair
{
    internal enum NodePatchApplyPhase { Idle, AppliedUnsaved, Failed, Restored }

    internal sealed class NodePatchApplyReport
    {
        public NodePatchApplyPhase Phase;
        public NodePatchSnapshot Snapshot;
        public int ConnectionCount;
        public int PlacementCount;
        public readonly List<string> Issues = new List<string>();
        public bool HasSnapshot => Snapshot != null;
        public string FirstIssue => Issues.Count > 0 ? Issues[0] : string.Empty;
        public string StageText => Phase == NodePatchApplyPhase.Restored ? "恢复完成"
            : Phase == NodePatchApplyPhase.AppliedUnsaved
                ? "已应用 " + PlacementCount + " 个位置、" + ConnectionCount + " 条连接，未保存"
            : Phase == NodePatchApplyPhase.Failed ? "应用失败" : "未应用";
    }

    internal sealed class NodePatchEngine
    {
        public NodePatchApplyReport Apply(string patchPath, INodePatchMemoryHost memory)
        {
            return Apply(NodePatchReader.ReadFile(patchPath), memory);
        }

        public NodePatchApplyReport Apply(NodePatchPlan plan, INodePatchMemoryHost memory)
        {
            if (memory == null || !memory.TryValidateTargets(new string[0], out _))
                throw new RepairInputException("当前 Scene 不可用。");
            var report = new NodePatchApplyReport();
            var snapshot = new NodePatchSnapshot(memory.SceneOwner, memory.NodeOwner, memory.GenerationOwner);
            var placements = new List<NodePlacementPatch>();
            foreach (var placement in plan.Layout?.Nodes ?? new List<NodePlacementPatch>())
            {
                if (memory.TryReadPlacement(placement.Uid, out _, out _))
                    placements.Add(placement);
            }
            if (placements.Count > 0)
                ExpandCanvas(plan.Layout.Canvas, memory, report);
            foreach (var placement in placements)
                ApplyPlacement(placement, memory, snapshot, report);
            foreach (var connection in plan.Connections)
                ApplyConnection(connection, memory, snapshot, report);

            if (snapshot.Placements.Count > 0 || snapshot.Slots.Count > 0)
                report.Snapshot = snapshot;
            report.Phase = report.PlacementCount + report.ConnectionCount > 0
                ? NodePatchApplyPhase.AppliedUnsaved
                : report.Issues.Count > 0 ? NodePatchApplyPhase.Failed : NodePatchApplyPhase.Idle;
            return report;
        }

        private static void ExpandCanvas(NodeCanvasValue canvas, INodePatchMemoryHost memory, NodePatchApplyReport report)
        {
            if (!memory.TryGetCanvas(out var width, out var height, out var supportsResize, out _) || !supportsResize)
                return;
            if (canvas.Width <= width && canvas.Height <= height)
                return;
            if (!memory.TryResizeAbsolute(Math.Max(width, canvas.Width), Math.Max(height, canvas.Height),
                out _, out _, out var error))
                report.Issues.Add("画布扩展失败：" + error);
        }

        private static void ApplyPlacement(NodePlacementPatch placement, INodePatchMemoryHost memory,
            NodePatchSnapshot snapshot, NodePatchApplyReport report)
        {
            if (!memory.TryReadPlacement(placement.Uid, out var before, out _))
                return;
            snapshot.Placements.Add(new NodePatchPlacementSnapshot { Uid = placement.Uid, Before = before });
            try
            {
                if (memory.TryWritePlacement(placement.Uid,
                    new NodePatchPlacementValue { X = placement.After.X, Y = placement.After.Y }, out var error))
                    report.PlacementCount++;
                else
                    report.Issues.Add(placement.Uid + "：" + error);
            }
            catch (Exception ex) { report.Issues.Add(placement.Uid + "：" + ex.Message); }
        }

        private static void ApplyConnection(NodeConnectionPatch connection, INodePatchMemoryHost memory,
            NodePatchSnapshot snapshot, NodePatchApplyReport report)
        {
            if (!memory.TryReadPlacement(connection.After.TargetUid, out _, out _)
                || !memory.TryReadSlot(connection.SourceUid, connection.Slot, out var before, out _))
                return;
            snapshot.Slots.Add(new NodePatchSlotSnapshot
                { SourceUid = connection.SourceUid, Slot = connection.Slot, Before = before });
            try
            {
                if (memory.TryWriteSlotTarget(connection.SourceUid, connection.Slot, connection.After.TargetUid, out var error))
                    report.ConnectionCount++;
                else
                    report.Issues.Add(connection.SourceUid + "/slot:" + connection.Slot + "：" + error);
            }
            catch (Exception ex) { report.Issues.Add(connection.SourceUid + "：" + ex.Message); }
        }

        public bool Restore(NodePatchApplyReport report, INodePatchMemoryHost memory)
        {
            if (report == null || !report.HasSnapshot || memory == null)
                return false;
            report.Issues.Clear();
            if (!report.Snapshot.MatchesOwners(memory.SceneOwner, memory.NodeOwner, memory.GenerationOwner))
            {
                report.Issues.Add("Scene 已切换或重新加载，旧快照不能用于新 Scene。");
                return false;
            }
            if (!memory.TryValidateTargets(new string[0], out var contextError))
            {
                report.Issues.Add(contextError);
                return false;
            }
            foreach (var placement in report.Snapshot.Placements)
                RestorePlacement(placement, memory, report);
            foreach (var slot in report.Snapshot.Slots)
                RestoreSlot(slot, memory, report);
            if (report.Issues.Count > 0)
                return false;
            report.Phase = NodePatchApplyPhase.Restored;
            return true;
        }

        private static void RestorePlacement(NodePatchPlacementSnapshot placement, INodePatchMemoryHost memory,
            NodePatchApplyReport report)
        {
            if (!memory.TryReadPlacement(placement.Uid, out _, out _))
                return;
            try
            {
                if (!memory.TryWritePlacement(placement.Uid, placement.Before, out var error))
                    report.Issues.Add(placement.Uid + "：" + error);
            }
            catch (Exception ex) { report.Issues.Add(placement.Uid + "：" + ex.Message); }
        }

        private static void RestoreSlot(NodePatchSlotSnapshot slot, INodePatchMemoryHost memory, NodePatchApplyReport report)
        {
            if (!memory.TryReadSlot(slot.SourceUid, slot.Slot, out _, out _)
                || (!string.IsNullOrEmpty(slot.Before.TargetUid)
                    && !memory.TryReadPlacement(slot.Before.TargetUid, out _, out _)))
                return;
            try
            {
                if (!memory.TryWriteSlotTarget(slot.SourceUid, slot.Slot, slot.Before.TargetUid, out var error))
                    report.Issues.Add(slot.SourceUid + "/slot:" + slot.Slot + "：" + error);
            }
            catch (Exception ex) { report.Issues.Add(slot.SourceUid + "：" + ex.Message); }
        }
    }
}
