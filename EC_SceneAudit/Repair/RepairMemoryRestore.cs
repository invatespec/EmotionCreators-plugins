using System;
using System.Collections.Generic;

namespace EC_SceneAudit.Repair
{
    // 恢复事务：先核验身份与全部目标/端点并准备回滚数据，再替换目标 Part、目标 NodeBase 和相关入线槽位；
    // 任一步失败都按逆序回滚，并如实报告回滚结果。
    internal static class RepairMemoryRestore
    {
        public static bool TryRestore(
            RepairMemorySnapshot snapshot,
            IRepairMemoryHost host,
            out string error,
            out string rollback)
        {
            error = null;
            rollback = null;
            if (snapshot == null)
            {
                error = "没有可用的内存快照。";
                return false;
            }
            if (host == null)
            {
                error = "缺少内存快照网关。";
                return false;
            }
            if (!snapshot.MatchesOwners(host.SceneOwner, host.NodeOwner, host.GenerationOwner))
            {
                error = "Scene 已切换或重新加载，旧内存快照已失效，不能恢复。";
                return false;
            }
            if (host.SlotCount != snapshot.SlotCount)
            {
                error = "节点连线结构已变化，旧内存快照已失效。";
                return false;
            }

            var targetUids = new List<string>(snapshot.Parts.Count);
            foreach (var part in snapshot.Parts)
            {
                if (part == null || string.IsNullOrEmpty(part.Uid))
                {
                    error = "快照中存在无效的目标 Part 记录。";
                    return false;
                }
                targetUids.Add(part.Uid);
            }
            if (!host.TryValidateTargets(targetUids, out error))
                return false;

            var rollbackParts = new List<RepairPartValue>(snapshot.Parts.Count);
            foreach (var part in snapshot.Parts)
            {
                RepairPartValue current;
                if (!host.TryReadPart(part.Uid, out current, out error) || current == null)
                {
                    error = "恢复前校验失败（目标 Part " + part.Uid + "）：" + error;
                    return false;
                }
                rollbackParts.Add(current);
            }
            var rollbackNodes = new List<RepairNodeValue>(snapshot.TargetNodes.Count);
            foreach (var node in snapshot.TargetNodes)
            {
                RepairNodeValue current;
                if (!host.TryReadNode(node.Uid, out current, out error) || current == null)
                {
                    error = "恢复前校验失败（目标节点 " + node.Uid + "）：" + error;
                    return false;
                }
                rollbackNodes.Add(current);
            }
            var rollbackSlots = new List<RepairSlotValue>(snapshot.InboundSlots.Count);
            foreach (var slot in snapshot.InboundSlots)
            {
                RepairSlotValue current;
                if (!host.TryReadSlot(slot.SourceUid, slot.Index, out current, out error))
                {
                    error = "恢复前校验失败（上游 " + slot.SourceUid + "/slot:" + slot.Index + "）：" + error;
                    return false;
                }
                rollbackSlots.Add(RepairMemoryCopy.Slot(current));
            }

            var partsAttempted = 0;
            var nodesAttempted = 0;
            var slotsAttempted = 0;
            // TryWrite 返回 false 不代表未写入（节点 Copy 可能已部分改动后由外层 catch 返回 false）；
            // 因此调用前登记 attempted，回滚范围始终覆盖当前项。
            try
            {
                foreach (var part in snapshot.Parts)
                {
                    string stepError;
                    partsAttempted++;
                    if (!host.TryWritePart(part, out stepError))
                        throw new InvalidOperationException("替换目标 Part 失败（" + part.Uid + "）：" + stepError);
                }
                foreach (var node in snapshot.TargetNodes)
                {
                    string stepError;
                    nodesAttempted++;
                    if (!host.TryWriteNode(node, out stepError))
                        throw new InvalidOperationException("恢复目标节点失败（" + node.Uid + "）：" + stepError);
                }
                for (var index = 0; index < snapshot.InboundSlots.Count; index++)
                {
                    var slot = snapshot.InboundSlots[index];
                    string stepError;
                    slotsAttempted++;
                    if (!host.TryWriteSlot(slot.SourceUid, slot.Index, slot.Value, out stepError))
                        throw new InvalidOperationException(
                            "恢复入线槽位失败（" + slot.SourceUid + "/slot:" + slot.Index + "）：" + stepError);
                }
                return true;
            }
            catch (Exception ex)
            {
                var failures = new List<string>();
                for (var index = slotsAttempted - 1; index >= 0; index--)
                {
                    var slot = snapshot.InboundSlots[index];
                    if (!TryWriteSlotQuiet(host, slot, rollbackSlots[index]))
                        failures.Add(slot.SourceUid + "/slot:" + slot.Index);
                }
                for (var index = nodesAttempted - 1; index >= 0; index--)
                {
                    if (!TryWriteNodeQuiet(host, snapshot.TargetNodes[index], rollbackNodes[index]))
                        failures.Add("node:" + snapshot.TargetNodes[index].Uid);
                }
                for (var index = partsAttempted - 1; index >= 0; index--)
                {
                    if (!TryWritePartQuiet(host, rollbackParts[index]))
                        failures.Add("part:" + snapshot.Parts[index].Uid);
                }

                rollback = failures.Count == 0
                    ? "已回滚到恢复前状态"
                    : "回滚失败 " + failures.Count + " 处（" + failures[0] + "）";
                error = "恢复失败：" + ex.Message + "；" + rollback;
                return false;
            }
        }

        private static bool TryWriteSlotQuiet(IRepairMemoryHost host, RepairInboundSlot slot, RepairSlotValue value)
        {
            try
            {
                string ignored;
                return host.TryWriteSlot(slot.SourceUid, slot.Index, value, out ignored);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryWriteNodeQuiet(IRepairMemoryHost host, RepairNodeValue node, RepairNodeValue value)
        {
            try
            {
                string ignored;
                return host.TryWriteNode(value, out ignored);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryWritePartQuiet(IRepairMemoryHost host, RepairPartValue value)
        {
            try
            {
                string ignored;
                return host.TryWritePart(value, out ignored);
            }
            catch
            {
                return false;
            }
        }
    }
}
