using System.Collections.Generic;

namespace EC_SceneAudit.Repair
{
    internal enum RepairApplyPhase { Idle, AppliedUnsaved, Failed, Restored }

    internal sealed class RepairApplyItem
    {
        public string Label;
        public bool Written;
        public string Issue;
    }

    internal sealed class RepairApplyReport
    {
        public RepairApplyPhase Phase;
        public RepairMemorySnapshot Snapshot;
        public readonly List<RepairApplyItem> Items = new List<RepairApplyItem>();
        public readonly List<string> Issues = new List<string>();
        public bool HasSnapshot => Snapshot != null;
        public int AppliedCount => Items.FindAll(item => item.Written).Count;
        public string FirstIssue => Issues.Count > 0 ? Issues[0] : string.Empty;
        public string StageText => Phase == RepairApplyPhase.Restored ? "恢复完成"
            : Phase == RepairApplyPhase.AppliedUnsaved ? "已应用 " + AppliedCount + " 项，未保存"
            : Phase == RepairApplyPhase.Failed ? "应用失败" : "未应用";
    }
}
