using System;
using System.Collections.Generic;

namespace EC_SceneAudit.Repair
{
    internal static class RepairApplyTransaction
    {
        public static bool TryRun(RepairPlan plan, IRepairTargetSink sink,
            out List<RepairApplyItem> items, out string error)
        {
            items = new List<RepairApplyItem>();
            error = null;
            foreach (var change in plan.Changes)
            {
                if (!sink.TryLocate(change.Target, out var slot, out _) || slot == null)
                    continue;
                var target = change.Target;
                var item = new RepairApplyItem
                {
                    Label = target.NodeUid + " / CUT " + (target.CutSourceIndex + 1)
                        + " / 文本框 " + (target.TextBoxSourceIndex + 1) + " / 布局 " + target.LayoutIndex
                };
                items.Add(item);
                WriteItem(slot, change.After, item);
            }
            return items.Exists(item => item.Written);
        }

        private static void WriteItem(IRepairTextSlot slot, string text, RepairApplyItem item)
        {
            string original;
            try { original = slot.Read(); }
            catch (Exception ex) { item.Issue = ex.Message; return; }
            try
            {
                slot.Write(text);
                item.Written = true;
            }
            catch (Exception ex)
            {
                item.Issue = ex.Message;
                // 只撤销失败的这一项；其它已经写入的目标继续保留。
                try { slot.Write(original); }
                catch (Exception restoreError) { item.Issue += "；该项撤销失败：" + restoreError.Message; }
            }
        }
    }
}
