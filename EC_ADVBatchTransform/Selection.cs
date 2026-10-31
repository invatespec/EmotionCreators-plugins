using System.Collections.Generic;
using UnityEngine;

namespace EC_ADVBatchTransform
{
    using SortOrder = HEdit.ADVPart.SortOrder;

    // 选中集合。主对象就是原生 sortOrder，集合里总包含主对象
    internal static class MultiSelection
    {
        private static readonly List<SortOrder> members = new List<SortOrder>();
        private static SortOrder lastPrimary;
        private static SortOrder anchor;
        private static bool listDirty;

        internal static IReadOnlyList<SortOrder> Members => members;

        internal static int Count => members.Count;

        internal static bool IsMulti => members.Count > 1;

        internal static bool Contains(SortOrder so) => so != null && members.Contains(so);

        internal static void Clear()
        {
            members.Clear();
            lastPrimary = null;
            anchor = null;
            listDirty = false;
        }

        internal static void Tick()
        {
            Prune();
            var primary = EditorContext.Primary;
            // 新增、复制、删除、切 cut 都会改 sortOrder，按原生单选处理
            if (primary != lastPrimary) ResetTo(primary);
            if (EditorContext.CanOperate) KeepVisuals();
            if (listDirty) RefreshList();
        }

        internal static void PlainClick(SortOrder so)
        {
            var ui = EditorContext.UI;
            if (ui == null || so == null) return;
            // 单选时再点主对象是取消选中；多选时点主对象只保留它
            var next = so == ui.sortOrder && !IsMulti ? null : so;
            DeselectAllExcept(next);
            ui.sortOrder = next;
            ResetTo(next);
            anchor = so;
        }

        // Ctrl：只切换这一行，不动起点（起点为空时补上）
        internal static void CtrlClick(SortOrder so)
        {
            if (so == null) return;
            if (members.Contains(so))
            {
                Remove(so);
            }
            else
            {
                Add(so);
            }
            if (anchor == null) anchor = so;
            listDirty = true;
        }

        // Shift：以第一次点击的行为起点，到当前行整段覆盖旧区间；起点本身不跟着移动
        internal static void ShiftClick(SortOrder so)
        {
            if (so == null) return;
            var order = EditorContext.List?.lstUI;
            int a = order != null && anchor != null ? order.IndexOf(anchor) : -1;
            int b = order != null ? order.IndexOf(so) : -1;
            if (a < 0 || b < 0)
            {
                CtrlClick(so);
                return;
            }
            int from = Mathf.Min(a, b);
            int to = Mathf.Max(a, b);
            var keep = new HashSet<SortOrder>();
            for (int i = from; i <= to; i++) keep.Add(order[i]);
            foreach (var m in members.ToArray())
                if (!keep.Contains(m)) Remove(m);
            AddRange(order, from, to, so);
            listDirty = true;
        }

        // 区间内全部选中，最后点击的行成为主对象；起点保持不变
        private static void AddRange(List<SortOrder> order, int from, int to, SortOrder so)
        {
            var primary = EditorContext.Primary;
            if (primary != null && !members.Contains(primary) && EditorContext.HasCtrl(primary))
                members.Add(primary);
            for (int i = from; i <= to; i++)
                if (!members.Contains(order[i])) members.Add(order[i]);
            SetPrimary(so);
        }

        // Tab / Shift+Tab：按列表顺序切到上一行或下一行，并回到单选。
        // 没有主对象时正序取首行、逆序取末行；到两端循环
        internal static void Step(int dir)
        {
            var ui = EditorContext.UI;
            var order = EditorContext.List?.lstUI;
            if (ui == null || order == null || order.Count == 0) return;
            int index = ui.sortOrder != null ? order.IndexOf(ui.sortOrder) : -1;
            int next = index < 0
                ? (dir > 0 ? 0 : order.Count - 1)
                : (index + dir + order.Count) % order.Count;
            var so = order[next];
            DeselectAllExcept(so);
            ui.sortOrder = so;
            ResetTo(so);
            anchor = so;
        }

        // 新建对象的撤销回放用
        internal static void Drop(SortOrder so)
        {
            if (so != null && members.Remove(so)) listDirty = true;
        }

        private static void Add(SortOrder so)
        {
            var primary = EditorContext.Primary;
            if (primary != null && !members.Contains(primary) && EditorContext.HasCtrl(primary))
                members.Add(primary);
            members.Add(so);
            SetPrimary(so);
        }

        private static void Remove(SortOrder so)
        {
            members.Remove(so);
            if (so != EditorContext.Primary)
            {
                EditorContext.GetCtrl(so)?.Deselected();
                return;
            }
            // 主对象被移除时，最后加入的成员接任
            SetPrimary(members.Count > 0 ? members[members.Count - 1] : null);
        }

        private static void SetPrimary(SortOrder so)
        {
            var ui = EditorContext.UI;
            if (ui == null) return;
            ui.sortOrder = so;
            lastPrimary = so;
        }

        private static void ResetTo(SortOrder primary)
        {
            DeselectAllExcept(primary);
            members.Clear();
            anchor = null;
            if (primary != null && EditorContext.HasCtrl(primary)) members.Add(primary);
            lastPrimary = primary;
            listDirty = true;
        }

        private static void DeselectAllExcept(SortOrder keep)
        {
            foreach (var so in members)
            {
                if (so == keep) continue;
                EditorContext.GetCtrl(so)?.Deselected();
            }
        }

        private static void Prune()
        {
            for (int i = members.Count - 1; i >= 0; i--)
            {
                if (!EditorContext.HasCtrl(members[i])) members.RemoveAt(i);
            }
        }

        // 原生只对主对象调用 Selected/Deselected，非主成员的红框靠这里补。
        // 离开文本/特效页时原生全体 Deselected，切回时不恢复主对象；多选且没点开槽位时连主对象一起补
        private static void KeepVisuals()
        {
            var primary = EditorContext.Primary;
            bool restorePrimary = IsMulti && EditorContext.ActiveSlot < 0;
            foreach (var so in members)
            {
                // 槽位模式下 Selected 会把槽位关掉
                if (so == primary && !restorePrimary) continue;
                var ctrl = EditorContext.GetCtrl(so);
                if (ctrl != null && ctrl.dragRect != null && !ctrl.dragRect.active) ctrl.Selected();
            }
        }

        private static void RefreshList()
        {
            listDirty = false;
            EditorContext.List?.UpdateUI();
        }
    }
}
