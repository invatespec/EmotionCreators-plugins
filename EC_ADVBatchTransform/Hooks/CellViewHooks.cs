using System;
using System.Reflection;
using ADVPart.List;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace EC_ADVBatchTransform.Hooks
{
    using SortOrder = HEdit.ADVPart.SortOrder;

    // 已替换过监听的行。列表行是对象池复用的，靠它避免重复替换
    internal sealed class RowButtonsMarker : MonoBehaviour
    {
    }

    internal static class CellViewHooks
    {
        private static readonly FieldInfo ButtonField = AccessTools.Field(typeof(TextAndEffectCellView), "button");
        private static readonly FieldInfo ToggleField = AccessTools.Field(typeof(TextAndEffectCellView), "toggle");
        private static readonly FieldInfo UpField = AccessTools.Field(typeof(TextAndEffectCellView), "buttonUp");
        private static readonly FieldInfo DownField = AccessTools.Field(typeof(TextAndEffectCellView), "buttonDown");
        private static readonly FieldInfo SortOrderField = AccessTools.Field(typeof(TextAndEffectCellView), "sortOrder");
        private static readonly FieldInfo IndexField = AccessTools.Field(typeof(TextAndEffectCellView), "index");
        private static bool disabled;
        private static bool errorLogged;

        // 原生 Awake 订阅完之后才能替换，所以只处理已激活的行；SetData 每次刷新列表都会调用
        [HarmonyPostfix]
        [HarmonyPatch(typeof(TextAndEffectCellView), nameof(TextAndEffectCellView.SetData))]
        private static void SetDataPostfix(TextAndEffectCellView __instance, SortOrder _sortOrder)
        {
            if (__instance == null || _sortOrder == null) return;
            try
            {
                if (__instance.gameObject.activeInHierarchy) Install(__instance);
                Highlight(__instance, _sortOrder);
            }
            catch (Exception e)
            {
                if (errorLogged) return;
                errorLogged = true;
                ADVBatchTransformPlugin.Log.LogError(e);
            }
        }

        private static void Install(TextAndEffectCellView cell)
        {
            if (disabled || cell.GetComponent<RowButtonsMarker>() != null) return;
            var button = ButtonField?.GetValue(cell) as Button;
            var up = UpField?.GetValue(cell) as Button;
            var down = DownField?.GetValue(cell) as Button;
            if (button == null || up == null || down == null || SortOrderField == null || IndexField == null)
            {
                disabled = true;
                ADVBatchTransformPlugin.Log.LogWarning("未找到列表行按钮字段，多选与整组图层移动停用");
                return;
            }
            Replace(button, () => OnRowClick(cell));
            Replace(up, () => OnArrow(cell, true));
            Replace(down, () => OnArrow(cell, false));
            cell.gameObject.AddComponent<RowButtonsMarker>();
        }

        // 原生用 UniRx 订阅 onClick，本质是运行时监听；面板上配置的持久监听不受影响
        private static void Replace(Button button, UnityAction action)
        {
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(action);
        }

        // 高亮由集合决定，每次集合变化都会刷新列表；ToggleGroup 互斥会把多余的行关掉，所以解除。
        // Toggle.group 的 setter 先改字段再注销，不会从旧组移除，必须先手动 UnregisterToggle。
        // 行是对象池复用的，取消选中时也要显式写 false，否则旧高亮会残留
        private static void Highlight(TextAndEffectCellView cell, SortOrder so)
        {
            var toggle = ToggleField?.GetValue(cell) as Toggle;
            if (toggle == null) return;
            var group = toggle.group;
            if (group != null)
            {
                group.UnregisterToggle(toggle);
                toggle.group = null;
            }
            toggle.isOn = MultiSelection.Contains(so);
        }

        private static void OnRowClick(TextAndEffectCellView cell)
        {
            var so = SortOrderField.GetValue(cell) as SortOrder;
            if (so == null) return;
            // Ctrl 优先：按住 Ctrl 时 Shift 也可能是按数字键带的
            if (CtrlHeld()) MultiSelection.CtrlClick(so);
            else if (ShiftHeld()) MultiSelection.ShiftClick(so);
            else MultiSelection.PlainClick(so);
        }

        private static void OnArrow(TextAndEffectCellView cell, bool up)
        {
            var list = cell.textAndEffect;
            if (list == null) return;
            var so = SortOrderField.GetValue(cell) as SortOrder;
            if (LayerOrder.TryMoveGroup(list, so, up)) return;
            // 单选交换也进撤销时间线
            UndoStack.Push(new OrderEntry(list, list.lstUI));
            int index = (int)IndexField.GetValue(cell);
            LayerOrder.SwapNative(list, index, up ? index - 1 : index + 1);
        }

        private static bool ShiftHeld() => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        private static bool CtrlHeld() => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
    }
}
