using System;
using System.Collections.Generic;

namespace EC_SceneAudit.Repair
{
    // 出线槽位的独立值：只含数据，不持有任何游戏对象引用。
    internal struct RepairSlotValue
    {
        public string ChildUid;
        public int EndConditionType;
        public int EndConditionCount;
        public int FadeType;
        public float FadeTime;
    }

    // Part 深拷贝：Data 由原生 Part.Save 得到，与内存对象完全独立。
    internal sealed class RepairPartValue
    {
        public string Uid;
        public byte[] Data;
    }

    // 目标 NodeBase 的完整快照：恢复时用原生 Load + Copy 写回既有 NodeBase 实例。
    internal sealed class RepairNodeValue
    {
        public string Uid;
        public byte[] Data;
    }

    // 非目标来源的相关入线槽位：只记录指向目标的单个槽位，来源节点其它槽位不动。
    internal struct RepairInboundSlot
    {
        public string SourceUid;
        public int Index;
        public RepairSlotValue Value;
    }

    // 游戏对象适配层：Part/NodeBase 以独立字节读写，槽位以独立值读写；写槽位时由实现同步连线。
    internal interface IRepairMemoryHost
    {
        int SlotCount { get; }

        object SceneOwner { get; }

        object NodeOwner { get; }

        // 加载世代标识：同路径重新加载 Scene 后必须变化，避免旧快照误套到新数据上。
        object GenerationOwner { get; }

        IEnumerable<string> NodeUids { get; }

        bool TryReadPart(string uid, out RepairPartValue value, out string error);

        bool TryWritePart(RepairPartValue value, out string error);

        bool TryReadNode(string uid, out RepairNodeValue value, out string error);

        bool TryWriteNode(RepairNodeValue value, out string error);

        bool TryReadSlot(string sourceUid, int index, out RepairSlotValue value, out string error);

        bool TryWriteSlot(string sourceUid, int index, RepairSlotValue value, out string error);

        // 修改前门禁：Scene 身份可用、没有活动 ADV 编辑引用、目标节点齐全。
        bool TryValidateTargets(ICollection<string> targetUids, out string error);
    }

    // 只记录受影响内容，不做整份 Scene 快照。
    internal sealed class RepairMemorySnapshot
    {
        public object SceneOwner { get; private set; }

        public object NodeOwner { get; private set; }

        public object GenerationOwner { get; private set; }

        public int SlotCount { get; private set; }

        public string SceneDisplay { get; set; }

        public readonly List<RepairPartValue> Parts = new List<RepairPartValue>();

        public readonly List<RepairNodeValue> TargetNodes = new List<RepairNodeValue>();

        public readonly List<RepairInboundSlot> InboundSlots = new List<RepairInboundSlot>();

        public RepairMemorySnapshot(object sceneOwner, object nodeOwner, object generationOwner, int slotCount)
        {
            SceneOwner = sceneOwner;
            NodeOwner = nodeOwner;
            GenerationOwner = generationOwner;
            SlotCount = slotCount;
        }

        public int PartCount => Parts.Count;

        public int ConnectionCount => TargetNodes.Count * SlotCount + InboundSlots.Count;

        public bool MatchesOwners(object sceneOwner, object nodeOwner, object generationOwner)
        {
            return ReferenceEquals(SceneOwner, sceneOwner)
                && ReferenceEquals(NodeOwner, nodeOwner)
                && ReferenceEquals(GenerationOwner, generationOwner);
        }

        public bool HasPart(string uid)
        {
            foreach (var part in Parts)
            {
                if (string.Equals(part.Uid, uid, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        public string Describe()
        {
            return Parts.Count + " 个 Part，" + ConnectionCount + " 条相关连线";
        }
    }

    // 拷贝辅助：快照必须与内存对象完全独立，字节数组不能与来源共享同一实例。
    internal static class RepairMemoryCopy
    {
        public static byte[] Bytes(byte[] source)
        {
            if (source == null || source.Length == 0)
                return new byte[0];
            var copy = new byte[source.Length];
            Buffer.BlockCopy(source, 0, copy, 0, source.Length);
            return copy;
        }

        public static RepairPartValue Part(RepairPartValue source)
        {
            if (source == null)
                return null;
            return new RepairPartValue { Uid = source.Uid, Data = Bytes(source.Data) };
        }

        public static RepairNodeValue Node(RepairNodeValue source)
        {
            if (source == null)
                return null;
            return new RepairNodeValue { Uid = source.Uid, Data = Bytes(source.Data) };
        }

        public static RepairSlotValue Slot(RepairSlotValue source)
        {
            source.ChildUid = source.ChildUid ?? string.Empty;
            return source;
        }
    }
}
