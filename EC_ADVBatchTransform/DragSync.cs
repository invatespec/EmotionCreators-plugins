using System;
using System.Collections.Generic;
using ADVPart.List;
using ADVPart.Manipulate;
using UnityEngine;

namespace EC_ADVBatchTransform
{
    using SortOrder = HEdit.ADVPart.SortOrder;
    using SpeechBubbles = HEdit.ADVPart.SpeechBubbles;
    using UICtrl = TextAndEffectListUICtrl.UICtrl;

    // 包装每个 UICtrl 的拖动回调，把拖动同步给其他选中对象
    internal static class DragSync
    {
        // 切 cut、换气泡样式都会重建 UICtrl，按引用记录，不在 dicObject 里的自然丢弃
        private static HashSet<UICtrl> wrapped = new HashSet<UICtrl>();
        private static HashSet<UICtrl> seen = new HashSet<UICtrl>();

        internal static void Scan()
        {
            var objects = EditorContext.Objects;
            if (objects == null) return;
            seen.Clear();
            foreach (var pair in objects)
            {
                if (pair.Value == null) continue;
                if (!wrapped.Contains(pair.Value)) Wrap(pair.Key, pair.Value);
                seen.Add(pair.Value);
            }
            var swap = wrapped;
            wrapped = seen;
            seen = swap;
        }

        private static void Wrap(SortOrder so, UICtrl ctrl)
        {
            if (ctrl.dragMove != null)
            {
                Action<Vector2> move = ctrl.dragMove.onMove;
                Action<float> rot = ctrl.dragMove.onRotation;
                ctrl.dragMove.onMove = v => OnMove(so, v, move);
                ctrl.dragMove.onRotation = f => OnRotate(so, f, rot);
            }
            if (ctrl.dragRect != null)
            {
                Action<Vector2> size = ctrl.dragRect.onMove;
                ctrl.dragRect.onMove = v => OnResize(so, v, size);
            }
            if (ctrl is TextUICtrl.TCUICtrl tc && so is SpeechBubbles sb) AttachSlotDrags(tc, sb);
        }

        private static void AttachSlotDrags(TextUICtrl.TCUICtrl tc, SpeechBubbles sb)
        {
            if (tc.textComponent == null || tc.textComponent.textInfos == null) return;
            var infos = tc.textComponent.textInfos;
            for (int i = 0; i < infos.Length && i < 2; i++)
            {
                if (infos[i] != null && infos[i].text != null) SlotRotateDrag.Attach(infos[i].gameObject, sb, i);
            }
        }

        // 只有多选、拖的是成员、且没点开槽位时才同步
        private static bool ShouldSync(SortOrder so)
        {
            return MultiSelection.IsMulti && MultiSelection.Contains(so)
                && EditorContext.CanOperate && EditorContext.ActiveSlot < 0;
        }

        // 原回调按主对象刷新面板，拖的是非主成员时调用会取错对象，所以只写该对象的数据字段
        private static void OnMove(SortOrder so, Vector2 v, Action<Vector2> orig)
        {
            bool sync = ShouldSync(so);
            if (sync) BatchOps.BeginDrag(GestureKind.Move, so);
            if (Items.IsPrimary(so))
            {
                orig?.Invoke(v);
            }
            else
            {
                var s = Items.Read(so);
                s.Pos = v;
                Items.WriteData(s);
            }
            if (sync) BatchOps.DragMoved(v);
        }

        private static void OnRotate(SortOrder so, float f, Action<float> orig)
        {
            bool sync = ShouldSync(so);
            if (sync) BatchOps.BeginDrag(GestureKind.Rotate, so);
            if (Items.IsPrimary(so))
            {
                orig?.Invoke(f);
            }
            else
            {
                var s = Items.Read(so);
                s.Rot = f;
                Items.WriteData(s);
            }
            if (sync) BatchOps.DragRotated(f);
        }

        private static void OnResize(SortOrder so, Vector2 v, Action<Vector2> orig)
        {
            bool sync = ShouldSync(so);
            if (sync) BatchOps.BeginDrag(GestureKind.Resize, so);
            if (Items.IsPrimary(so))
            {
                orig?.Invoke(v);
            }
            else
            {
                var s = Items.Read(so);
                s.Size = v;
                Items.WriteData(s);
            }
            if (sync) BatchOps.DragResized(v);
        }
    }
}
