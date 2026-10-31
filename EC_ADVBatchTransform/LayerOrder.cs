using System.Collections.Generic;
using ADVPart.List;

namespace EC_ADVBatchTransform
{
    using SortOrder = HEdit.ADVPart.SortOrder;

    // 多选时点任一选中行的上下箭头，整组移动一位，组内相对顺序不变
    internal static class LayerOrder
    {
        // 不满足多选条件时返回 false，交给原生单行交换
        internal static bool TryMoveGroup(TextAndEffectListUICtrl list, SortOrder clicked, bool up)
        {
            if (list == null || !MultiSelection.IsMulti || !MultiSelection.Contains(clicked)) return false;
            var order = list.lstUI;
            var before = new List<SortOrder>(order);
            bool moved = up ? MoveUp(order) : MoveDown(order);
            if (!moved) return true;
            UndoStack.Push(new OrderEntry(list, before));
            list.Sort();
            list.UpdateUI();
            return true;
        }

        internal static void SwapNative(TextAndEffectListUICtrl list, int index, int other)
        {
            var order = list.lstUI;
            if (index < 0 || other < 0 || index >= order.Count || other >= order.Count) return;
            Swap(order, index, other);
            list.Sort();
            list.UpdateUI();
        }

        // 升序遍历：上一行不是成员就交换；整组到顶时顶部成员不动
        private static bool MoveUp(List<SortOrder> order)
        {
            bool moved = false;
            for (int i = 1; i < order.Count; i++)
            {
                if (!MultiSelection.Contains(order[i]) || MultiSelection.Contains(order[i - 1])) continue;
                Swap(order, i, i - 1);
                moved = true;
            }
            return moved;
        }

        private static bool MoveDown(List<SortOrder> order)
        {
            bool moved = false;
            for (int i = order.Count - 2; i >= 0; i--)
            {
                if (!MultiSelection.Contains(order[i]) || MultiSelection.Contains(order[i + 1])) continue;
                Swap(order, i, i + 1);
                moved = true;
            }
            return moved;
        }

        private static void Swap(List<SortOrder> order, int a, int b)
        {
            var tmp = order[a];
            order[a] = order[b];
            order[b] = tmp;
        }
    }
}
