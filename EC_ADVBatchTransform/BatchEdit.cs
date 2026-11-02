using System;
using System.Collections.Generic;
using ADVPart.List;

namespace EC_ADVBatchTransform
{
    using SortOrder = HEdit.ADVPart.SortOrder;
    using SpeechBubbles = HEdit.ADVPart.SpeechBubbles;
    using ScreenEffect = HEdit.ADVPart.ScreenEffect;

    // 原生列表的复制/删除按钮在多选时整组执行；批量期间屏蔽逐条记录，由这里合并成一条撤销
    internal static class BatchEdit
    {
        internal static bool SuppressRecords { get; private set; }

        // 返回 true 表示已接管，调用方跳过原生单选逻辑。
        // 一旦决定接管就不再回退给原生：原生只处理主对象，会留下半套结果
        internal static bool TryDuplicate(TextAndEffectListUICtrl list)
        {
            var targets = SelectedTargets(list);
            if (targets == null) return false;
            var copies = new List<SortOrder>(targets.Count);
            SuppressRecords = true;
            try
            {
                // 先补齐序号，否则副本序号（dicObject.Count）可能和现存对象撞车
                Renumber(list);
                foreach (var so in targets)
                    if (CopyOf(so, out var copy)) copies.Add(copy);
                if (copies.Count == 0) return true;
                UndoStack.Push(new BatchSpawnEntry(copies, targets));
                list.UpdateUI();
                MultiSelection.SelectGroup(copies);
                list.UpdateInteractable();
            }
            catch (Exception e)
            {
                ADVBatchTransformPlugin.Log.LogError(e);
            }
            finally
            {
                SuppressRecords = false;
            }
            return true;
        }

        internal static bool TryDelete(TextAndEffectListUICtrl list)
        {
            var targets = SelectedTargets(list);
            if (targets == null) return false;
            // 删除会不断改 lstUI，先留一份完整快照供撤销统一重编号
            var order = new List<SortOrder>(list.lstUI);
            // 先入栈再删：中途抛异常时撤销仍能补齐已删的部分，未删的会被 HasCtrl 跳过
            UndoStack.Push(new BatchDeleteEntry(targets, order));
            // 先清主对象与选中集合：原生 sortOrder 订阅在删除期间会把主对象重设到残留对象上
            var ui = EditorContext.UI;
            if (ui != null) ui.sortOrder = null;
            MultiSelection.Clear();
            SuppressRecords = true;
            try
            {
                foreach (var so in targets) SpawnEntry.DeleteNative(so);
                // 删除后 lstUI 已被原生刷新，留下的序号空洞会让之后新增撞号（Sort() 遇重复键抛异常）
                Renumber(list);
                list.UpdateUI();
                list.UpdateInteractable();
            }
            catch (Exception e)
            {
                ADVBatchTransformPlugin.Log.LogError(e);
            }
            finally
            {
                SuppressRecords = false;
            }
            return true;
        }

        // 按列表顺序取有效成员；非多选、不在文本/特效页时交还原生
        private static List<SortOrder> SelectedTargets(TextAndEffectListUICtrl list)
        {
            if (list == null || !EditorContext.InTextEffectPage || !MultiSelection.IsMulti) return null;
            var targets = new List<SortOrder>();
            foreach (var so in list.lstUI)
                if (MultiSelection.Contains(so) && EditorContext.HasCtrl(so)) targets.Add(so);
            return targets.Count > 1 ? targets : null;
        }

        // 只写序号不调 Sort()：新增用 dicObject.Count 当序号，列表有空洞会撞号，
        // 而 Sort() 内部的 SortedList 遇重复键直接抛异常。调用方负责刷新列表
        internal static void Renumber(TextAndEffectListUICtrl list)
        {
            var order = list.lstUI;
            for (int i = 0; i < order.Count; i++) order[i].sortOrder = i;
        }

        private static bool CopyOf(SortOrder so, out SortOrder copy)
        {
            copy = null;
            if (so is SpeechBubbles sb)
            {
                var panel = EditorContext.TextPanel;
                if (panel == null) return false;
                var bubbles = new SpeechBubbles(sb);
                panel.AddText(bubbles, false);
                copy = bubbles;
                return true;
            }
            var effectPanel = EditorContext.EffectPanel;
            if (effectPanel == null) return false;
            var effect = new ScreenEffect((ScreenEffect)so);
            effectPanel.AddEffect(effect, false);
            copy = effect;
            return true;
        }
    }
}
