using UnityEngine;

namespace EC_ADVBatchTransform
{
    // 数值输入框聚焦时给键盘手势取号：聚焦后 IsTyping 会停掉面板比对，
    // 期间的改动要等到之后才发现，序号必须代表聚焦（开始编辑）的那一刻。
    // 槽位面板打开时本体面板仍可见，所以不能只看 ActiveSlot 判断作用域
    internal static class EditFocus
    {
        private static GameObject owner;

        internal static void Set(GameObject go)
        {
            if (owner == go) return;
            owner = go;
            if (go == null || !EditorContext.InTextEffectPage) return;
            ActionClock.Begin(IsSlotPanel(go) ? ActionScope.Slot : ActionScope.Root);
        }

        // 失焦时原生已把新值写进数据，立刻比对一次；没改过值就顺手丢掉取的号
        internal static void Clear(GameObject go)
        {
            if (owner != go) return;
            owner = null;
            if (EditorContext.InTextEffectPage) PanelWatcher.Flush();
        }

        internal static void Reset() => owner = null;

        private static bool IsSlotPanel(GameObject go)
        {
            var layout = EditorContext.LayoutPanel;
            return layout != null && go.transform.IsChildOf(layout.transform);
        }
    }
}
