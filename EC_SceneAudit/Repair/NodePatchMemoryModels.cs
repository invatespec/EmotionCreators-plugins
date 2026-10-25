using System;
using System.Collections.Generic;

namespace EC_SceneAudit.Repair
{
    internal struct NodePatchSlotValue
    {
        public string TargetUid;
        public string ConditionType;
        public int ConditionCount;
        public int FadeType;
        public float FadeTime;

        public NodePatchSlotValue(string targetUid, string conditionType, int conditionCount, int fadeType, float fadeTime)
        {
            TargetUid = targetUid;
            ConditionType = conditionType;
            ConditionCount = conditionCount;
            FadeType = fadeType;
            FadeTime = fadeTime;
        }
    }

    internal struct NodePatchPlacementValue
    {
        public float X;
        public float Y;
    }

    internal interface INodePatchMemoryHost
    {
        object SceneOwner { get; }
        object NodeOwner { get; }
        object GenerationOwner { get; }

        bool TryValidateTargets(ICollection<string> targetUids, out string error);
        bool TryGetCanvas(out float width, out float height, out bool supportsAbsoluteResize, out string error);
        bool TryResizeAbsolute(float width, float height, out float actualWidth, out float actualHeight, out string error);
        bool TryReadPlacement(string uid, out NodePatchPlacementValue value, out string error);
        bool TryWritePlacement(string uid, NodePatchPlacementValue value, out string error);
        bool TryReadSlot(string sourceUid, int slot, out NodePatchSlotValue value, out string error);
        bool TryWriteSlotTarget(string sourceUid, int slot, string targetUid, out string error);
    }

    internal sealed class NodePatchSnapshot
    {
        public object SceneOwner { get; private set; }
        public object NodeOwner { get; private set; }
        public object GenerationOwner { get; private set; }

        public readonly List<NodePatchPlacementSnapshot> Placements = new List<NodePatchPlacementSnapshot>();
        public readonly List<NodePatchSlotSnapshot> Slots = new List<NodePatchSlotSnapshot>();

        public NodePatchSnapshot(object sceneOwner, object nodeOwner, object generationOwner)
        {
            SceneOwner = sceneOwner;
            NodeOwner = nodeOwner;
            GenerationOwner = generationOwner;
        }

        public bool MatchesOwners(object sceneOwner, object nodeOwner, object generationOwner)
        {
            return ReferenceEquals(SceneOwner, sceneOwner)
                && ReferenceEquals(NodeOwner, nodeOwner)
                && ReferenceEquals(GenerationOwner, generationOwner);
        }

        public string Describe()
        {
            return Placements.Count + " 个节点位置，" + Slots.Count + " 个来源槽位";
        }
    }

    internal sealed class NodePatchPlacementSnapshot
    {
        public string Uid;
        public NodePatchPlacementValue Before;
    }

    internal sealed class NodePatchSlotSnapshot
    {
        public string SourceUid;
        public int Slot;
        public NodePatchSlotValue Before;
    }
}
