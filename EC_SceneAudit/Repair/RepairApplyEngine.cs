using System;

namespace EC_SceneAudit.Repair
{
    internal sealed class RepairApplyEngine
    {
        public RepairApplyReport Apply(string planPath, IRepairTargetSink sink, IRepairMemoryHost memory)
        {
            return Apply(RepairPlanReader.ReadFile(planPath), sink, memory);
        }

        public RepairApplyReport Apply(RepairPlan plan, IRepairTargetSink sink, IRepairMemoryHost memory)
        {
            var report = new RepairApplyReport();
            if (sink == null || memory == null)
                throw new RepairInputException("当前 Scene 不可用。");
            if (!memory.TryValidateTargets(new string[0], out var contextError))
                throw new RepairInputException(contextError);

            var matched = new RepairPlan();
            foreach (var change in plan.Changes)
            {
                if (sink.TryLocate(change.Target, out var slot, out _) && slot != null)
                    matched.Changes.Add(change);
            }
            if (matched.Changes.Count == 0)
                return report;

            if (!RepairMemoryCapture.TryCapture(matched, memory, out var snapshot, out var error))
                throw new RepairInputException(error);
            matched.Changes.RemoveAll(change => !snapshot.HasPart(change.Target.NodeUid));
            if (matched.Changes.Count == 0)
                return report;

            RepairApplyTransaction.TryRun(matched, sink, out var items, out _);
            report.Items.AddRange(items);
            foreach (var item in items)
            {
                if (!string.IsNullOrEmpty(item.Issue))
                    report.Issues.Add(item.Label + "：" + item.Issue);
            }
            // 写入发生过异常时也保留本次快照，供手动恢复，不用旧会话阻止新的应用。
            if (report.AppliedCount > 0 || report.Issues.Count > 0)
                report.Snapshot = snapshot;
            report.Phase = report.AppliedCount > 0 ? RepairApplyPhase.AppliedUnsaved
                : report.Issues.Count > 0 ? RepairApplyPhase.Failed : RepairApplyPhase.Idle;
            return report;
        }

        public bool Restore(RepairApplyReport report, IRepairMemoryHost memory)
        {
            if (report == null || !report.HasSnapshot)
                return false;
            report.Issues.Clear();
            if (!RepairMemoryRestore.TryRestore(report.Snapshot, memory, out var error, out _))
            {
                report.Issues.Add(error);
                return false;
            }
            report.Phase = RepairApplyPhase.Restored;
            return true;
        }
    }
}
