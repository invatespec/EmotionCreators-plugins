using System.Collections.Generic;

namespace EC_SceneAudit.Repair
{
    // ec-scene-repair-plan version 1，字段与网页 repairs.mjs 完全一致。
    internal sealed class RepairPlan
    {
        public const string Format = "ec-scene-repair-plan";
        public const int Version = 1;

        public string SourcePath;
        public RepairPlanSource Source;
        public List<RepairChange> Changes = new List<RepairChange>();
    }

    internal sealed class RepairPlanSource
    {
        public string BundleId;
        public string ManifestSha256;
    }

    internal sealed class RepairPlanReference
    {
        public string FileId;
        public string Kind;
        public string Pointer;
    }

    internal sealed class RepairPlanTarget
    {
        public RepairPlanReference Reference;
        public string NodeUid;
        public int CutSourceIndex;
        public int TextBoxSourceIndex;
        public int LayoutIndex;
    }

    internal sealed class RepairChange
    {
        public RepairPlanTarget Target;
        public string Before;
        public string After;
    }
}
