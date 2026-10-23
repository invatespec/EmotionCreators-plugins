using System;
using System.Collections.Generic;

namespace EC_SceneAudit.Repair
{
    // 快照只覆盖清单目标：每个目标 UID 一份 Part 深拷贝与完整 NodeBase，
    // 另加所有指向目标的非目标来源入线槽位；目标之间互连由完整 NodeBase 覆盖，不重复记录。
    internal static class RepairMemoryCapture
    {
        public static bool TryCapture(
            RepairPlan plan,
            IRepairMemoryHost host,
            out RepairMemorySnapshot snapshot,
            out string error)
        {
            snapshot = null;
            error = null;
            if (host == null)
            {
                error = "缺少内存快照网关。";
                return false;
            }
            if (host.SlotCount <= 0)
            {
                error = "节点连线槽位数量无效。";
                return false;
            }

            var order = new List<string>();
            var targetUids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var change in plan?.Changes ?? new List<RepairChange>())
            {
                var uid = change?.Target?.NodeUid;
                if (string.IsNullOrEmpty(uid))
                {
                    error = "清单存在缺少目标 UID 的条目。";
                    return false;
                }
                if (targetUids.Add(uid))
                    order.Add(uid);
            }
            if (order.Count == 0)
            {
                error = "清单没有可快照的目标 Part。";
                return false;
            }
            if (!host.TryValidateTargets(order, out error))
                return false;

            var result = new RepairMemorySnapshot(host.SceneOwner, host.NodeOwner, host.GenerationOwner, host.SlotCount);
            foreach (var uid in order)
            {
                RepairPartValue part;
                if (!host.TryReadPart(uid, out part, out error) || part == null)
                {
                    error = "快照目标 Part 失败（" + uid + "）：" + error;
                    return false;
                }
                RepairNodeValue node;
                if (!host.TryReadNode(uid, out node, out error) || node == null)
                {
                    error = "快照目标节点失败（" + uid + "）：" + error;
                    return false;
                }
                // 复制成独立字节，避免后续内存改动穿透到快照。
                result.Parts.Add(RepairMemoryCopy.Part(part));
                result.TargetNodes.Add(RepairMemoryCopy.Node(node));
            }

            try
            {
                foreach (var sourceUid in host.NodeUids)
                {
                    if (string.IsNullOrEmpty(sourceUid) || targetUids.Contains(sourceUid))
                        continue;
                    for (var index = 0; index < host.SlotCount; index++)
                    {
                        RepairSlotValue slot;
                        if (!host.TryReadSlot(sourceUid, index, out slot, out error))
                        {
                            error = "读取连线槽位失败（" + sourceUid + "/slot:" + index + "）：" + error;
                            return false;
                        }
                        if (string.IsNullOrEmpty(slot.ChildUid) || !targetUids.Contains(slot.ChildUid))
                            continue;
                        result.InboundSlots.Add(new RepairInboundSlot
                        {
                            SourceUid = sourceUid,
                            Index = index,
                            Value = RepairMemoryCopy.Slot(slot)
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                error = "枚举节点连线失败：" + ex.Message;
                return false;
            }

            snapshot = result;
            return true;
        }
    }
}
