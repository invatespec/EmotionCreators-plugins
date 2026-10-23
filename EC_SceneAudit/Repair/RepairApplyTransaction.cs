using System;
using System.Collections.Generic;

namespace EC_SceneAudit.Repair
{
    // 整批内存事务：全部目标唯一命中且 before 逐字符相等后才写入；异常回滚已写项，不触发保存。
    internal static class RepairApplyTransaction
    {
        public static bool TryRun(
            RepairPlan plan,
            IRepairTargetSink sink,
            out List<RepairApplyItem> items,
            out string error)
        {
            items = new List<RepairApplyItem>();
            error = null;
            if (plan == null || plan.Changes.Count == 0)
            {
                error = "清单没有需要应用的修改。";
                return false;
            }
            if (sink == null)
            {
                error = "没有可用的目标定位器。";
                return false;
            }

            var slots = new List<IRepairTextSlot>(plan.Changes.Count);
            var originals = new List<string>(plan.Changes.Count);
            for (var index = 0; index < plan.Changes.Count; index++)
            {
                var change = plan.Changes[index];
                var target = change.Target;
                items.Add(new RepairApplyItem
                {
                    Index = index,
                    Pointer = target.Reference.Pointer,
                    Label = target.NodeUid + " · CUT " + (target.CutSourceIndex + 1)
                        + " · 文本框 " + (target.TextBoxSourceIndex + 1) + " · 布局 " + target.LayoutIndex,
                    Before = change.Before,
                    After = change.After
                });

                IRepairTextSlot slot;
                string issue;
                if (!sink.TryLocate(target, out slot, out issue) || slot == null)
                {
                    items[index].Issue = string.IsNullOrEmpty(issue) ? "无法唯一定位目标" : issue;
                    error = "第 " + (index + 1) + " 项无法唯一定位：" + items[index].Issue
                        + "；整批取消，未写入任何目标。";
                    return false;
                }

                var current = slot.Read();
                if (!string.Equals(current, change.Before, StringComparison.Ordinal))
                {
                    items[index].Issue = "当前原文与清单不一致";
                    error = "第 " + (index + 1) + " 项当前原文与清单不一致；整批取消，未写入任何目标。";
                    return false;
                }
                slots.Add(slot);
                originals.Add(current);
            }

            var written = 0;
            try
            {
                for (var index = 0; index < slots.Count; index++)
                {
                    slots[index].Write(plan.Changes[index].After);
                    written++;
                    var readBack = slots[index].Read();
                    if (!string.Equals(readBack, plan.Changes[index].After, StringComparison.Ordinal))
                        throw new InvalidOperationException("第 " + (index + 1) + " 项写入后读回不一致。");
                    items[index].Written = true;
                    items[index].BeforeDigest = RepairDigest.Describe(originals[index]);
                    items[index].AfterDigest = RepairDigest.Describe(readBack);
                }
                return true;
            }
            catch (Exception ex)
            {
                for (var index = written - 1; index >= 0; index--)
                {
                    try
                    {
                        slots[index].Write(originals[index]);
                    }
                    catch
                    {
                        // 回滚失败不回退整体结论：快照仍在，由恢复入口兜底。
                    }
                }
                foreach (var item in items)
                {
                    item.Written = false;
                    if (string.IsNullOrEmpty(item.Issue))
                        item.Issue = "已回滚";
                }
                error = ex.Message + "；已撤销本次内存改动，未保存。";
                return false;
            }
        }
    }
}
