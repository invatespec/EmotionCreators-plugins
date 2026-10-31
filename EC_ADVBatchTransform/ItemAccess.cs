using ADVPart.List;
using ADVPart.Manipulate;
using UnityEngine;

namespace EC_ADVBatchTransform
{
    using ScreenEffect = HEdit.ADVPart.ScreenEffect;
    using SortOrder = HEdit.ADVPart.SortOrder;
    using SpeechBubbles = HEdit.ADVPart.SpeechBubbles;

    // 气泡与特效的 pos/rot/size 同名但没有共同基类，在这里统一读写
    internal struct ItemState
    {
        public SortOrder Target;
        public Vector2 Pos;
        public float Rot;
        public Vector2 Size;
        // 画布下的 localPosition。两类 prefab 的 anchor 可能不同，跨对象计算只能用它
        public Vector2 Local;
    }

    internal static class Items
    {
        internal static ItemState Read(SortOrder so)
        {
            var state = new ItemState { Target = so };
            if (so is SpeechBubbles sb)
            {
                state.Pos = sb.pos;
                state.Rot = sb.rot;
                state.Size = sb.size;
            }
            else if (so is ScreenEffect se)
            {
                state.Pos = se.pos;
                state.Rot = se.rot;
                state.Size = se.size;
            }
            var rect = RootRect(so);
            state.Local = rect != null ? (Vector2)rect.localPosition : state.Pos;
            return state;
        }

        // 只写数据字段，用于原生已经改好画面的拖动源
        internal static void WriteData(ItemState s)
        {
            // 插件写入不进面板记录
            PanelWatcher.Invalidate();
            if (s.Target is SpeechBubbles sb)
            {
                sb.pos = s.Pos;
                sb.rot = s.Rot;
                sb.size = s.Size;
            }
            else if (s.Target is ScreenEffect se)
            {
                se.pos = s.Pos;
                se.rot = s.Rot;
                se.size = s.Size;
            }
        }

        internal static void Write(ItemState s)
        {
            PanelWatcher.Invalidate();
            WriteData(s);
            var ctrl = EditorContext.GetCtrl(s.Target);
            if (ctrl == null) return;
            if (ctrl is TextUICtrl.TCUICtrl tc && tc.textComponent != null)
            {
                tc.textComponent.position = s.Pos;
                tc.textComponent.rot = s.Rot;
                tc.textComponent.sizeDelta = s.Size;
            }
            else if (ctrl.effectComponent != null)
            {
                ctrl.effectComponent.position = s.Pos;
                ctrl.effectComponent.rot = s.Rot;
                ctrl.effectComponent.sizeDelta = s.Size;
            }
        }

        internal static RectTransform RootRect(SortOrder so)
        {
            var ctrl = EditorContext.GetCtrl(so);
            if (ctrl == null || ctrl.gameObject == null) return null;
            return ctrl.gameObject.transform as RectTransform;
        }

        internal static bool IsPrimary(SortOrder so) => so != null && so == EditorContext.Primary;

        // 属性面板只显示主对象，按主对象类型刷新
        internal static void RefreshPrimaryPanel()
        {
            var primary = EditorContext.Primary;
            if (primary is SpeechBubbles)
            {
                var panel = EditorContext.TextPanel;
                if (panel != null) Refresh(panel);
            }
            else if (primary is ScreenEffect)
            {
                var panel = EditorContext.EffectPanel;
                if (panel != null) Refresh(panel);
            }
            RefreshSlotPanel();
        }

        private static void Refresh(TextUICtrl panel)
        {
            panel.UpdatePosUI();
            panel.UpdateRotUI();
            panel.UpdateSizeUI();
        }

        private static void Refresh(EffectUICtrl panel)
        {
            panel.UpdatePosUI();
            panel.UpdateRotUI();
            panel.UpdateSizeUI();
        }

        // 撤销、批量写入后连颜色、描边、槽位一起刷新，否则面板还显示旧值
        internal static void RefreshPrimaryFullPanel()
        {
            var primary = EditorContext.Primary;
            if (primary is SpeechBubbles)
            {
                var panel = EditorContext.TextPanel;
                if (panel != null) panel.UpdateUI();
            }
            else if (primary is ScreenEffect)
            {
                var panel = EditorContext.EffectPanel;
                if (panel != null) panel.UpdateUI();
            }
            RefreshSlotPanel();
        }

        // 槽位面板没有主对象时不能刷（UpdateUI 会读空 textLayout）
        private static void RefreshSlotPanel()
        {
            if (EditorContext.ActiveSlot < 0) return;
            EditorContext.LayoutPanel?.UpdateUI();
        }
    }
}
