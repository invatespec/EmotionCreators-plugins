using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using EC_SceneAudit.Export;

namespace EC_SceneAudit.Repair
{
    internal enum NodePatchApplyPhase
    {
        Idle,
        AppliedUnsaved,
        Verified,
        VerifyFailed,
        Failed,
        FailedRollbackHeld,
        Restored
    }

    internal static class NodePatchApplyPhaseText
    {
        public static string Describe(NodePatchApplyPhase phase)
        {
            switch (phase)
            {
                case NodePatchApplyPhase.AppliedUnsaved:
                    return "已应用，未保存，待 F8 回验";
                case NodePatchApplyPhase.Verified:
                    return "回验通过";
                case NodePatchApplyPhase.VerifyFailed:
                    return "回验未通过，可恢复";
                case NodePatchApplyPhase.Failed:
                    return "应用失败，已补偿";
                case NodePatchApplyPhase.FailedRollbackHeld:
                    return "补偿失败，保留快照与写回锁";
                case NodePatchApplyPhase.Restored:
                    return "已恢复节点位置与连接";
                default:
                    return "未开始";
            }
        }

        public static bool HoldsSession(NodePatchApplyPhase phase)
        {
            return phase == NodePatchApplyPhase.AppliedUnsaved
                || phase == NodePatchApplyPhase.Verified
                || phase == NodePatchApplyPhase.VerifyFailed
                || phase == NodePatchApplyPhase.FailedRollbackHeld;
        }
    }

    internal sealed class NodePatchApplyReport
    {
        public NodePatchApplyPhase Phase = NodePatchApplyPhase.Idle;
        public string PatchPath = string.Empty;
        public string BundlePath = string.Empty;
        public string SceneDisplay = string.Empty;
        public DateTime? AppliedAtUtc;
        public NodePatchSnapshot Snapshot;
        public int ConnectionCount;
        public int PlacementCount;
        public float CanvasBeforeWidth;
        public float CanvasBeforeHeight;
        public float CanvasAfterWidth;
        public float CanvasAfterHeight;
        public float RequiredCanvasWidth;
        public float RequiredCanvasHeight;
        public long ElapsedMilliseconds;
        public NodePatchPreflightReport Preflight;
        public NodePatchVerificationReport Verification { get; set; }
        public readonly List<string> Steps = new List<string>();
        public readonly List<string> Issues = new List<string>();

        public bool HasSnapshot => Snapshot != null;
        public bool HasSession => HasSnapshot && NodePatchApplyPhaseText.HoldsSession(Phase);
        public string StageText => NodePatchApplyPhaseText.Describe(Phase);
        public string FirstIssue => Issues.Count > 0 ? Issues[0] : string.Empty;
    }

    // NodePatch 应用链：只读预检 -> 状态复核 -> 小快照 -> 可补偿写入；不替换 Part，不重建节点图。
    internal sealed class NodePatchEngine
    {
        private readonly NodePatchPreflight preflight = new NodePatchPreflight();
        private readonly NodePatchVerifier verifier = new NodePatchVerifier();

        public NodePatchApplyReport Apply(
            string patchPath,
            string bundlePath,
            string sceneDisplay,
            Func<SceneSnapshot> readCurrentScene,
            INodePatchMemoryHost memory,
            NodePatchApplyReport previousSession)
        {
            var timer = Stopwatch.StartNew();
            var report = NewReport(patchPath, bundlePath, sceneDisplay);
            if (previousSession != null && previousSession.HasSession)
            {
                report.Phase = NodePatchApplyPhase.Failed;
                report.Issues.Add("场景已有未处理的文本或节点写回会话，不能再次应用。");
                return Finish(report, timer);
            }
            if (memory == null)
            {
                report.Phase = NodePatchApplyPhase.Failed;
                report.Issues.Add("缺少节点内存网关。");
                return Finish(report, timer);
            }

            NodePatchPlan plan;
            RepairBundleSource bundle;
            try
            {
                plan = NodePatchReader.ReadFile(patchPath);
                bundle = RepairBundleReader.Read(bundlePath);
            }
            catch (RepairInputException ex)
            {
                report.Phase = NodePatchApplyPhase.Failed;
                report.Issues.Add("读取补丁或资料包失败：" + ex.Message);
                return Finish(report, timer);
            }

            float currentWidth;
            float currentHeight;
            bool supportsResize;
            string memoryError;
            if (!memory.TryGetCanvas(out currentWidth, out currentHeight, out supportsResize, out memoryError))
            {
                report.Phase = NodePatchApplyPhase.Failed;
                report.Issues.Add("读取当前画布失败：" + memoryError);
                return Finish(report, timer);
            }

            NodePatchPreflightReport check;
            try
            {
                check = preflight.Evaluate(plan, patchPath, bundlePath, bundle, readCurrentScene(), supportsResize);
            }
            catch (Exception ex)
            {
                report.Phase = NodePatchApplyPhase.Failed;
                report.Issues.Add("核验当前 Scene 失败：" + ex.Message);
                return Finish(report, timer);
            }
            report.Preflight = check;
            if (!check.Passed)
            {
                report.Phase = NodePatchApplyPhase.Failed;
                report.Issues.Add("应用前核验未通过：" + check.Verdict);
                return Finish(report, timer);
            }

            NodePatchSnapshot snapshot;
            string snapshotError;
            if (!NodePatchMemoryCapture.TryCapture(plan, memory, out snapshot, out snapshotError))
            {
                report.Phase = NodePatchApplyPhase.Failed;
                report.Issues.Add("创建小快照失败：" + snapshotError);
                return Finish(report, timer);
            }

            float actualWidth;
            float actualHeight;
            string transactionError;
            bool rollbackFailed;
            var applied = NodePatchTransaction.TryApply(
                plan,
                snapshot,
                memory,
                check.RequiresCanvasExpansion,
                check.RequiredCanvasWidth,
                check.RequiredCanvasHeight,
                out actualWidth,
                out actualHeight,
                out transactionError,
                out rollbackFailed);

            report.Snapshot = snapshot;
            report.ConnectionCount = plan.Connections.Count;
            report.PlacementCount = plan.Layout?.Nodes.Count ?? 0;
            report.CanvasBeforeWidth = currentWidth;
            report.CanvasBeforeHeight = currentHeight;
            report.CanvasAfterWidth = actualWidth;
            report.CanvasAfterHeight = actualHeight;
            report.RequiredCanvasWidth = check.RequiredCanvasWidth;
            report.RequiredCanvasHeight = check.RequiredCanvasHeight;
            if (!applied)
            {
                report.Phase = rollbackFailed
                    ? NodePatchApplyPhase.FailedRollbackHeld
                    : NodePatchApplyPhase.Failed;
                report.Issues.Add(transactionError);
                if (rollbackFailed)
                    report.Steps.Add("补偿未全部确认成功；保留快照并继续持有场景写回锁。");
                return Finish(report, timer);
            }

            report.Phase = NodePatchApplyPhase.AppliedUnsaved;
            report.AppliedAtUtc = DateTime.UtcNow;
            report.Steps.Add("已应用 " + report.ConnectionCount + " 条连接和 "
                + report.PlacementCount + " 个节点位置（仅内存，未保存文件）");
            report.Steps.Add("内存快照：" + snapshot.Describe());
            return Finish(report, timer);
        }

        public bool Restore(NodePatchApplyReport report, INodePatchMemoryHost memory)
        {
            // 已恢复会话的快照仅供展示，不能再次回写覆盖后续编辑；补偿失败会话仍可重试恢复。
            if (report == null || !report.HasSession || memory == null)
                return false;
            string error;
            bool rollbackFailed;
            if (!NodePatchTransaction.TryRestore(report.Snapshot, memory, out error, out rollbackFailed))
            {
                report.Issues.Add(error);
                report.Phase = rollbackFailed
                    ? NodePatchApplyPhase.FailedRollbackHeld
                    : NodePatchApplyPhase.Failed;
                return false;
            }
            report.Phase = NodePatchApplyPhase.Restored;
            report.Steps.Add("已恢复节点位置与连接：" + report.Snapshot.Describe());
            return true;
        }

        public NodePatchVerificationReport Verify(string patchPath, string originalBundlePath, string newBundlePath)
        {
            return verifier.Verify(patchPath, originalBundlePath, newBundlePath);
        }

        public static string FindNewestBundleDirectory(string root, DateTime notBeforeUtc)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return null;
            var newest = null as string;
            var newestTime = notBeforeUtc;
            try
            {
                foreach (var titleDir in Directory.GetDirectories(root))
                foreach (var dateDir in Directory.GetDirectories(titleDir))
                foreach (var exportDir in Directory.GetDirectories(dateDir))
                {
                    if (!File.Exists(Path.Combine(exportDir, "bundle.json"))
                        || !File.Exists(Path.Combine(exportDir, "bundle.sha256")))
                        continue;
                    var time = Directory.GetLastWriteTimeUtc(exportDir);
                    if (time > newestTime)
                    {
                        newest = exportDir;
                        newestTime = time;
                    }
                }
            }
            catch (Exception)
            {
                // 返回已找到的结果，由 UI 显示找不到新导出。
            }
            return newest;
        }

        private static NodePatchApplyReport NewReport(string patchPath, string bundlePath, string sceneDisplay)
        {
            return new NodePatchApplyReport
            {
                PatchPath = patchPath ?? string.Empty,
                BundlePath = bundlePath ?? string.Empty,
                SceneDisplay = sceneDisplay ?? string.Empty
            };
        }

        private static NodePatchApplyReport Finish(NodePatchApplyReport report, Stopwatch timer)
        {
            report.ElapsedMilliseconds = timer.ElapsedMilliseconds;
            return report;
        }
    }

    internal static class NodePatchTransaction
    {
        public static bool TryApply(
            NodePatchPlan plan,
            NodePatchSnapshot snapshot,
            INodePatchMemoryHost host,
            bool expandCanvas,
            float requiredWidth,
            float requiredHeight,
            out float actualCanvasWidth,
            out float actualCanvasHeight,
            out string error,
            out bool rollbackFailed)
        {
            error = null;
            rollbackFailed = false;
            actualCanvasWidth = 0f;
            actualCanvasHeight = 0f;
            if (plan == null || snapshot == null || host == null)
            {
                error = "节点补丁事务参数不完整。";
                return false;
            }
            if (!snapshot.MatchesOwners(host.SceneOwner, host.NodeOwner, host.GenerationOwner))
            {
                error = "Scene 已切换或重新加载，节点快照失效。";
                return false;
            }

            var placements = plan.Layout?.Nodes ?? new List<NodePlacementPatch>();
            var placementsAttempted = 0;
            var slotsAttempted = 0;
            try
            {
                float currentWidth;
                float currentHeight;
                bool supportsResize;
                string canvasError;
                if (!host.TryGetCanvas(out currentWidth, out currentHeight, out supportsResize, out canvasError))
                    throw new InvalidOperationException("读取当前画布失败：" + canvasError);
                actualCanvasWidth = currentWidth;
                actualCanvasHeight = currentHeight;
                if (expandCanvas)
                {
                    if (!supportsResize)
                        throw new InvalidOperationException("当前版本不支持绝对画布扩展。");
                    float resizedWidth;
                    float resizedHeight;
                    if (!host.TryResizeAbsolute(requiredWidth, requiredHeight, out resizedWidth, out resizedHeight, out canvasError))
                    {
                        // 扩展 API 可能已改动画布后才失败；重新读取实际尺寸，失败报告不能谎称画布未变。
                        float reportedWidth;
                        float reportedHeight;
                        bool reportedSupport;
                        string reportedError;
                        if (host.TryGetCanvas(out reportedWidth, out reportedHeight, out reportedSupport, out reportedError))
                        {
                            actualCanvasWidth = reportedWidth;
                            actualCanvasHeight = reportedHeight;
                        }
                        throw new InvalidOperationException("扩展画布失败：" + canvasError
                            + "（当前实际画布 " + actualCanvasWidth.ToString("0.##") + "×"
                            + actualCanvasHeight.ToString("0.##") + "；已扩大的画布保留，不自动缩回）");
                    }
                    actualCanvasWidth = resizedWidth;
                    actualCanvasHeight = resizedHeight;
                    if (resizedWidth + NodePatchNumbers.Tolerance < requiredWidth
                        || resizedHeight + NodePatchNumbers.Tolerance < requiredHeight)
                        throw new InvalidOperationException("画布扩展结果小于补丁要求。");
                }

                foreach (var placement in placements)
                {
                    placementsAttempted++;
                    string stepError;
                    if (!host.TryWritePlacement(placement.Uid,
                        new NodePatchPlacementValue { X = placement.After.X, Y = placement.After.Y }, out stepError))
                        throw new InvalidOperationException("写节点位置失败（" + placement.Uid + "）：" + stepError);
                    NodePatchPlacementValue readBack;
                    if (!host.TryReadPlacement(placement.Uid, out readBack, out stepError)
                        || !NodePatchNumbers.NearlyEqual(readBack.X, placement.After.X)
                        || !NodePatchNumbers.NearlyEqual(readBack.Y, placement.After.Y))
                        throw new InvalidOperationException("节点位置读回不一致：" + placement.Uid);
                }

                foreach (var connection in plan.Connections)
                {
                    slotsAttempted++;
                    string stepError;
                    if (!host.TryWriteSlotTarget(connection.SourceUid, connection.Slot,
                        connection.After.TargetUid, out stepError))
                        throw new InvalidOperationException("写连接目标失败（" + connection.SourceUid + "/slot:" + connection.Slot + "）：" + stepError);
                    NodePatchSlotValue readBack;
                    if (!host.TryReadSlot(connection.SourceUid, connection.Slot, out readBack, out stepError)
                        || !string.Equals(readBack.TargetUid ?? string.Empty, connection.After.TargetUid, StringComparison.Ordinal)
                        || !string.Equals(readBack.ConditionType ?? string.Empty, connection.Before.ConditionType ?? string.Empty, StringComparison.Ordinal)
                        || readBack.ConditionCount != connection.Before.ConditionCount
                        || readBack.FadeType != connection.Before.FadeType
                        || !NodePatchNumbers.NearlyEqual(readBack.FadeTime, connection.Before.FadeTime))
                        throw new InvalidOperationException("连接目标读回不一致：" + connection.SourceUid + "/slot:" + connection.Slot);
                }
                return true;
            }
            catch (Exception ex)
            {
                var failures = Compensate(host, snapshot, placementsAttempted, slotsAttempted);
                rollbackFailed = failures.Count > 0;
                error = ex.Message + (rollbackFailed
                    ? "；补偿未全部确认，保留快照与写回锁。"
                    : "；本次写入已补偿。");
                return false;
            }
        }

        public static bool TryRestore(
            NodePatchSnapshot snapshot,
            INodePatchMemoryHost host,
            out string error,
            out bool rollbackFailed)
        {
            error = null;
            rollbackFailed = false;
            if (snapshot == null || host == null)
            {
                error = "节点快照或内存网关不可用。";
                return false;
            }
            if (!snapshot.MatchesOwners(host.SceneOwner, host.NodeOwner, host.GenerationOwner))
            {
                error = "Scene 已切换或重新加载，旧节点快照失效。";
                return false;
            }

            var failures = Compensate(host, snapshot, snapshot.Placements.Count, snapshot.Slots.Count);
            if (failures.Count > 0)
            {
                error = "恢复节点位置或连接失败：" + failures[0];
                rollbackFailed = true;
                return false;
            }
            return true;
        }

        private static List<string> Compensate(
            INodePatchMemoryHost host,
            NodePatchSnapshot snapshot,
            int placementsAttempted,
            int slotsAttempted)
        {
            var failures = new List<string>();
            for (var index = Math.Min(slotsAttempted, snapshot.Slots.Count) - 1; index >= 0; index--)
            {
                var slot = snapshot.Slots[index];
                string error;
                NodePatchSlotValue readBack;
                if (!host.TryWriteSlotTarget(slot.SourceUid, slot.Slot, slot.Before.TargetUid, out error)
                    || !host.TryReadSlot(slot.SourceUid, slot.Slot, out readBack, out error)
                    || !NodePatchNumbers.SlotEquals(readBack, slot.Before))
                    failures.Add(slot.SourceUid + "/slot:" + slot.Slot);
            }
            for (var index = Math.Min(placementsAttempted, snapshot.Placements.Count) - 1; index >= 0; index--)
            {
                var placement = snapshot.Placements[index];
                string error;
                NodePatchPlacementValue readBack;
                if (!host.TryWritePlacement(placement.Uid, placement.Before, out error)
                    || !host.TryReadPlacement(placement.Uid, out readBack, out error)
                    || !NodePatchNumbers.NearlyEqual(readBack.X, placement.Before.X)
                    || !NodePatchNumbers.NearlyEqual(readBack.Y, placement.Before.Y))
                    failures.Add("node:" + placement.Uid);
            }
            return failures;
        }
    }
}
