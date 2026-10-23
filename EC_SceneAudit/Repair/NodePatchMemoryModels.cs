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

    internal static class NodePatchMemoryCapture
    {
        public static bool TryCapture(
            NodePatchPlan plan,
            INodePatchMemoryHost host,
            out NodePatchSnapshot snapshot,
            out string error)
        {
            snapshot = null;
            error = null;
            if (plan == null)
            {
                error = "节点补丁为空。";
                return false;
            }
            if (host == null)
            {
                error = "缺少节点内存网关。";
                return false;
            }

            var targetUids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var placement in plan.Layout?.Nodes ?? new List<NodePlacementPatch>())
            {
                if (string.IsNullOrEmpty(placement?.Uid))
                {
                    error = "布局项缺少节点 UID。";
                    return false;
                }
                targetUids.Add(placement.Uid);
            }
            foreach (var connection in plan.Connections)
            {
                if (connection == null || string.IsNullOrEmpty(connection.SourceUid)
                    || string.IsNullOrEmpty(connection.Before?.TargetUid)
                    || string.IsNullOrEmpty(connection.After?.TargetUid))
                {
                    error = "连接项缺少来源或目标 UID。";
                    return false;
                }
                targetUids.Add(connection.SourceUid);
                targetUids.Add(connection.Before.TargetUid);
                targetUids.Add(connection.After.TargetUid);
            }
            if (!host.TryValidateTargets(targetUids, out error))
                return false;

            var result = new NodePatchSnapshot(host.SceneOwner, host.NodeOwner, host.GenerationOwner);
            foreach (var placement in plan.Layout?.Nodes ?? new List<NodePlacementPatch>())
            {
                NodePatchPlacementValue before;
                if (!host.TryReadPlacement(placement.Uid, out before, out error))
                {
                    error = "读取节点位置失败（" + placement.Uid + "）：" + error;
                    return false;
                }
                if (!NodePatchNumbers.NearlyEqual(before.X, placement.Before.X)
                    || !NodePatchNumbers.NearlyEqual(before.Y, placement.Before.Y))
                {
                    error = "节点当前位置与补丁 before 不一致：" + placement.Uid;
                    return false;
                }
                result.Placements.Add(new NodePatchPlacementSnapshot { Uid = placement.Uid, Before = before });
            }
            foreach (var connection in plan.Connections)
            {
                NodePatchSlotValue before;
                if (!host.TryReadSlot(connection.SourceUid, connection.Slot, out before, out error))
                {
                    error = "读取来源槽位失败（" + connection.SourceUid + "/slot:" + connection.Slot + "）：" + error;
                    return false;
                }
                if (!NodePatchNumbers.SlotEquals(before, connection.Before))
                {
                    error = "来源槽位当前值与补丁 before 不一致：" + connection.SourceUid + "/slot:" + connection.Slot;
                    return false;
                }
                result.Slots.Add(new NodePatchSlotSnapshot
                {
                    SourceUid = connection.SourceUid,
                    Slot = connection.Slot,
                    Before = before
                });
            }
            snapshot = result;
            return true;
        }
    }

    internal static class NodePatchNumbers
    {
        public const float Tolerance = 0.001f;

        public static bool NearlyEqual(float left, float right)
        {
            return Math.Abs(left - right) <= Tolerance;
        }

        public static bool SlotEquals(NodePatchSlotValue left, NodeSlotValue right)
        {
            return right != null
                && string.Equals(left.TargetUid ?? string.Empty, right.TargetUid ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(left.ConditionType ?? string.Empty, right.ConditionType ?? string.Empty, StringComparison.Ordinal)
                && left.ConditionCount == right.ConditionCount
                && left.FadeType == right.FadeType
                && NearlyEqual(left.FadeTime, right.FadeTime);
        }

        public static bool SlotEquals(NodePatchSlotValue left, NodePatchSlotValue right)
        {
            return string.Equals(left.TargetUid ?? string.Empty, right.TargetUid ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(left.ConditionType ?? string.Empty, right.ConditionType ?? string.Empty, StringComparison.Ordinal)
                && left.ConditionCount == right.ConditionCount
                && left.FadeType == right.FadeType
                && NearlyEqual(left.FadeTime, right.FadeTime);
        }
    }
}
