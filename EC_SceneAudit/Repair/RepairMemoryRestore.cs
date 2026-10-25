using System;
using System.Collections.Generic;

namespace EC_SceneAudit.Repair
{
    internal static class RepairMemoryRestore
    {
        public static bool TryRestore(RepairMemorySnapshot snapshot, IRepairMemoryHost host,
            out string error, out string rollback)
        {
            error = null;
            rollback = null;
            if (snapshot == null || host == null)
            {
                error = "没有可用的内存快照。";
                return false;
            }
            if (!snapshot.MatchesOwners(host.SceneOwner, host.NodeOwner, host.GenerationOwner))
            {
                error = "Scene 已切换或重新加载，旧快照不能用于新 Scene。";
                return false;
            }
            if (!host.TryValidateTargets(new string[0], out error))
                return false;

            var failures = new List<string>();
            foreach (var part in snapshot.Parts)
            {
                if (!host.TryReadPart(part.Uid, out var current, out _) || current == null)
                    continue;
                RestoreItem(() => host.TryWritePart(part, out var issue) ? null : issue ?? "写入失败", part.Uid, failures);
            }
            foreach (var node in snapshot.TargetNodes)
            {
                if (!host.TryReadNode(node.Uid, out var current, out _) || current == null)
                    continue;
                RestoreItem(() => host.TryWriteNode(node, out var issue) ? null : issue ?? "写入失败", node.Uid, failures);
            }
            var currentUids = new HashSet<string>(host.NodeUids, StringComparer.Ordinal);
            foreach (var slot in snapshot.InboundSlots)
            {
                if (!currentUids.Contains(slot.SourceUid)
                    || (!string.IsNullOrEmpty(slot.Value.ChildUid) && !currentUids.Contains(slot.Value.ChildUid)))
                    continue;
                RestoreItem(() => host.TryWriteSlot(slot.SourceUid, slot.Index, slot.Value, out var issue)
                    ? null : issue ?? "写入失败", slot.SourceUid + "/slot:" + slot.Index, failures);
            }
            error = failures.Count == 0 ? null : string.Join("；", failures.ToArray());
            return failures.Count == 0;
        }

        private static void RestoreItem(Func<string> write, string label, List<string> failures)
        {
            try
            {
                var issue = write();
                if (issue != null)
                    failures.Add(label + "：" + issue);
            }
            catch (Exception ex) { failures.Add(label + "：" + ex.Message); }
        }
    }
}
