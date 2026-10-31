using ADVPart.Manipulate;

namespace EC_ADVBatchTransform
{
    using SpeechBubbles = HEdit.ADVPart.SpeechBubbles;

    // 槽位（Text1/Text2）旋转。只作用于主气泡当前点开的槽位
    internal static class SlotOps
    {
        private static SpeechBubbles bubble;
        private static int slot = -1;
        private static float startRot;
        private static bool changed;

        internal static bool Active => bubble != null;

        internal static void Cancel() => bubble = null;

        internal static void Begin(SpeechBubbles target, int index)
        {
            if (bubble == target && slot == index) return;
            End();
            bubble = target;
            slot = index;
            startRot = target.textLayouts[index].rot;
            changed = false;
        }

        internal static void End()
        {
            if (bubble == null) return;
            if (changed) UndoStack.Push(new SlotRotEntry(bubble, slot, startRot));
            bubble = null;
        }

        internal static void Set(float rot)
        {
            if (bubble == null) return;
            WriteRot(bubble, slot, rot);
            changed = true;
        }

        internal static void KeyRotate(float step)
        {
            var target = EditorContext.Primary as SpeechBubbles;
            int index = EditorContext.ActiveSlot;
            if (target == null || index < 0) return;
            Begin(target, index);
            Set(target.textLayouts[index].rot + step);
        }

        // 槽位 rot 是相对气泡的 localRotation，与 TextLayoutUICtrl 输入框的写法一致
        internal static void WriteRot(SpeechBubbles target, int index, float rot)
        {
            if (index < 0 || index >= target.textLayouts.Length) return;
            PanelWatcher.Invalidate();
            target.textLayouts[index].rot = rot % 360f;
            var info = SlotInfo(target, index);
            if (info != null) info.rot = target.textLayouts[index].rot;
            // UpdateRotUI 读的是面板当前槽位，只在面板正显示这个槽位时刷新
            var layout = EditorContext.LayoutPanel;
            if (layout != null && Items.IsPrimary(target) && EditorContext.ActiveSlot == index)
                layout.UpdateRotUI();
        }

        private static ADV.TextComponent.TextInfo SlotInfo(SpeechBubbles target, int index)
        {
            var tc = EditorContext.GetCtrl(target) as TextUICtrl.TCUICtrl;
            if (tc == null || tc.textComponent == null) return null;
            var infos = tc.textComponent.textInfos;
            return infos != null && index < infos.Length ? infos[index] : null;
        }
    }
}
