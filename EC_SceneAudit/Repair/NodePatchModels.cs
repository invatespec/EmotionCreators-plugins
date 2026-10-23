using System.Collections.Generic;

namespace EC_SceneAudit.Repair
{
    // 网页与游戏共享的节点补丁契约：只记录实际改变的槽位目标和节点位置。
    internal sealed class NodePatchPlan
    {
        public const string Format = "ec-scene-node-patch";
        public const int Version = 1;

        public string SourcePath;
        public NodePatchSource Source;
        public readonly List<NodeConnectionPatch> Connections = new List<NodeConnectionPatch>();
        public NodeLayoutPatch Layout;
    }

    internal sealed class NodePatchSource
    {
        public string BundleId;
        public string ManifestSha256;
    }

    internal sealed class NodeConnectionPatch
    {
        public string SourceUid;
        public int Slot;
        public NodeSlotValue Before;
        public NodeConnectionTarget After;
    }

    internal sealed class NodeConnectionTarget
    {
        public string TargetUid;
    }

    internal sealed class NodeSlotValue
    {
        public string TargetUid;
        public string ConditionType;
        public int ConditionCount;
        public int FadeType;
        public float FadeTime;
    }

    internal sealed class NodeLayoutPatch
    {
        public NodeCanvasValue Canvas;
        public readonly List<NodePlacementPatch> Nodes = new List<NodePlacementPatch>();
    }

    internal sealed class NodeCanvasValue
    {
        public float Width;
        public float Height;
    }

    internal sealed class NodePlacementPatch
    {
        public string Uid;
        public NodePositionValue Before;
        public NodePositionValue After;
    }

    internal sealed class NodePositionValue
    {
        public float X;
        public float Y;
    }
}
