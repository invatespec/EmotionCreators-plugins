using System;
using System.Collections.Generic;
using ADVPart.Manipulate;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace EC_ADVBatchTransform
{
    using SpeechBubbles = HEdit.ADVPart.SpeechBubbles;

    // 文本内容的独立撤销/恢复，与对象变换时间线分开：
    // 对象 Ctrl+Z 管位置角度尺寸等，这里管输入框里的文字本身。
    // 粒度按输入停顿分段，Text1/Text2 共用一条历史
    internal static class TextHistory
    {
        // 输入停顿阈值：连续打字合并成一步，停手约一秒后才提交
        private const float IdleSeconds = 1f;

        // 上限比对象撤销小：文本记录保存的是整段字符串，占内存
        private const int Limit = 50;

        // 只持有数据对象与槽位号，回放时再按当前 UICtrl 取值，切 cut 重建后不会指向旧组件
        private sealed class Record
        {
            internal SpeechBubbles Bubble;
            internal int Slot;
            internal string Before;
            internal string After;
        }

        private static readonly List<Record> records = new List<Record>();
        private static readonly List<Record> redone = new List<Record>();

        private static SpeechBubbles currentBubble;
        private static int currentSlot = -1;
        // 当前槽位已提交的文本，下一次变化与它比较得到记录
        private static string baseline;

        private static float changedAt = -1f;

        private static InputField watched;
        private static bool suppress;
        // 同一帧里轮询和 OnGUI 可能都收到同一次按键，只让先到的那个生效
        private static int handledFrame = -1;

        // 面板比对据此决定要不要兜底记录文本字段
        internal static bool Attached => watched != null;

        internal static void Clear()
        {
            records.Clear();
            redone.Clear();
            Abort();
            Detach();
            currentBubble = null;
            currentSlot = -1;
            baseline = null;
            handledFrame = -1;
        }

        // 槽位面板打开时记住起始文本，否则第一次按键时数据已经被原生改写，拿不到旧值
        internal static void Prime(SpeechBubbles bubble, int slot)
        {
            if (bubble == null || slot < 0) return;
            if (currentBubble == bubble && currentSlot == slot) return;
            Commit();
            currentBubble = bubble;
            currentSlot = slot;
            baseline = Read(bubble, slot);
            changedAt = -1f;
        }

        // 文本输入框是旧版 InputField，onValueChanged 每次按键都触发；
        // 用 AddListener 追加，不覆盖原生监听
        internal static void Attach(InputField input)
        {
            if (input == null || input == watched) return;
            Detach();
            watched = input;
            input.onValueChanged.AddListener(OnTextChanged);
            input.onEndEdit.AddListener(OnTextEndEdit);
        }

        private static void Detach()
        {
            if (watched == null) return;
            watched.onValueChanged.RemoveListener(OnTextChanged);
            watched.onEndEdit.RemoveListener(OnTextEndEdit);
            watched = null;
        }

        private static void OnTextChanged(string text)
        {
            if (suppress) return;
            // 没有 Prime 过就不知道旧值，记下来只会得到一条「旧值=新值」的空记录
            if (currentBubble == null || currentSlot < 0) return;
            changedAt = Time.unscaledTime;
        }

        private static void OnTextEndEdit(string text) => Commit();

        // 关闭槽位时立刻结清，不等停顿计时
        internal static void CommitPending() => Commit();

        internal static void Tick()
        {
            if (changedAt < 0f) return;
            if (Time.unscaledTime - changedAt < IdleSeconds) return;
            Commit();
        }

        private static void Commit()
        {
            if (changedAt < 0f) return;
            changedAt = -1f;
            var bubble = currentBubble;
            int slot = currentSlot;
            if (bubble == null || slot < 0) return;
            string after = Read(bubble, slot);
            string before = baseline;
            baseline = after;
            if (after == null || after == before) return;
            records.Add(new Record
            {
                Bubble = bubble,
                Slot = slot,
                Before = before ?? string.Empty,
                After = after
            });
            redone.Clear();
            if (records.Count > Limit) records.RemoveRange(0, records.Count - Limit);
        }

        private static void Abort()
        {
            changedAt = -1f;
        }

        // 框内 Ctrl+Z / Ctrl+Y 只撤销/恢复文字；框外一律不接管，交给对象时间线。
        // 轮询 Input 在文本框聚焦时可能收不到组合键（见 OnGUI 那条路径）
        internal static bool HandleInputKeys()
        {
            if (!IsFocused()) return false;
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (!ctrl) return false;
            bool undo = Input.GetKeyDown(KeyCode.Z);
            bool redo = Input.GetKeyDown(KeyCode.Y);
            if (!undo && !redo) return false;
            if (Time.frameCount == handledFrame) return false;
            if (!(undo ? Undo() : Redo())) return false;
            handledFrame = Time.frameCount;
            return true;
        }

        // 聚焦时按键可能只到 UI 事件队列，到不了 Input 轮询；OnGUI 里再兜一层
        internal static void HandleGuiKeys()
        {
            var e = Event.current;
            if (e == null || e.type != EventType.KeyDown) return;
            if (!e.control) return;
            bool undo = e.keyCode == KeyCode.Z;
            bool redo = e.keyCode == KeyCode.Y;
            if (!undo && !redo) return;
            if (!IsFocused() || Time.frameCount == handledFrame) return;
            if (!(undo ? Undo() : Redo())) return;
            handledFrame = Time.frameCount;
            e.Use();
        }

        // EventSystem 的当前选中项兜底：InputField.isFocused 在部分时序下会晚一帧
        private static bool IsFocused()
        {
            if (watched == null) return false;
            if (watched.isFocused) return true;
            var es = EventSystem.current;
            return es != null && es.currentSelectedGameObject == watched.gameObject;
        }

        // 气泡已被删除或切了 cut 的记录直接丢掉，否则会永远卡在栈顶，之后每次撤销都空转
        private static bool Undo()
        {
            Commit();
            while (records.Count > 0)
            {
                var record = records[records.Count - 1];
                records.RemoveAt(records.Count - 1);
                if (!Apply(record, record.Before)) continue;
                redone.Add(record);
                return true;
            }
            return false;
        }

        private static bool Redo()
        {
            Commit();
            while (redone.Count > 0)
            {
                var record = redone[redone.Count - 1];
                redone.RemoveAt(redone.Count - 1);
                if (!Apply(record, record.After)) continue;
                records.Add(record);
                return true;
            }
            return false;
        }

        // 回放只改数据字段，再让面板与列表跟着刷新
        private static bool Apply(Record record, string text)
        {
            var bubble = record.Bubble;
            var layouts = bubble?.textLayouts;
            if (bubble == null || !EditorContext.HasCtrl(bubble)) return false;
            if (layouts == null || record.Slot < 0 || record.Slot >= layouts.Length) return false;
            bool tracing = bubble == currentBubble && record.Slot == currentSlot;
            // suppress 包住刷新：UpdateUI 会回写输入框文本并触发 onValueChanged，
            // 漏在外面会被记成一次新编辑
            suppress = true;
            try
            {
                layouts[record.Slot].msg = text ?? string.Empty;
                var info = SlotInfo(bubble, record.Slot);
                if (info != null) info.msg = layouts[record.Slot].msg;
                if (tracing)
                {
                    baseline = layouts[record.Slot].msg;
                    changedAt = -1f;
                    if (watched != null) watched.text = layouts[record.Slot].msg;
                }
                PanelWatcher.Invalidate();
                // 槽位面板关着时不能刷（UpdateUI 会读空的 textLayout）
                if (EditorContext.ActiveSlot >= 0) EditorContext.LayoutPanel?.UpdateUI();
                EditorContext.List?.UpdateUI();
            }
            finally
            {
                suppress = false;
            }
            return true;
        }

        private static string Read(SpeechBubbles bubble, int slot)
        {
            var layouts = bubble?.textLayouts;
            if (layouts == null || slot < 0 || slot >= layouts.Length) return null;
            return layouts[slot].msg;
        }

        private static ADV.TextComponent.TextInfo SlotInfo(SpeechBubbles bubble, int slot)
        {
            var tc = EditorContext.GetCtrl(bubble) as TextUICtrl.TCUICtrl;
            var infos = tc?.textComponent?.textInfos;
            return infos != null && slot >= 0 && slot < infos.Length ? infos[slot] : null;
        }
    }
}
