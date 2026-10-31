using UnityEngine;
using UnityEngine.EventSystems;

namespace EC_ADVBatchTransform
{
    using SpeechBubbles = HEdit.ADVPart.SpeechBubbles;

    // 原生 DragMove 的旋转 pivot 用 anchoredPosition 换算，对拉伸的槽位不对，且把世界角写进本地 rot。
    // 挂在槽位文字上，与原生 DragMove（槽位上恒为 None）并存，按住 D 拖动时旋转当前槽位
    internal sealed class SlotRotateDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public SpeechBubbles Bubble;
        public int Index;

        private bool dragging;
        private Vector2 pivot;
        private Vector2 pressDir;
        private float dragStartRot;

        internal static void Attach(GameObject go, SpeechBubbles bubble, int index)
        {
            var comp = go.GetComponent<SlotRotateDrag>();
            if (comp == null) comp = go.AddComponent<SlotRotateDrag>();
            comp.Bubble = bubble;
            comp.Index = index;
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            dragging = false;
            if (!Input.GetKey(KeyCode.D) || !IsOpenSlot()) return;
            var cam = eventData.pressEventCamera;
            pivot = RectTransformUtility.WorldToScreenPoint(cam, transform.position);
            pressDir = eventData.pressPosition - pivot;
            dragStartRot = Bubble.textLayouts[Index].rot;
            SlotOps.Begin(Bubble, Index);
            dragging = true;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!dragging || !Input.GetKey(KeyCode.D)) return;
            float delta = Vector2.SignedAngle(pressDir, eventData.position - pivot);
            SlotOps.Set(dragStartRot + delta);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (!dragging) return;
            dragging = false;
            SlotOps.End();
        }

        private bool IsOpenSlot()
        {
            return Bubble != null && EditorContext.CanOperate && Items.IsPrimary(Bubble) && EditorContext.ActiveSlot == Index;
        }
    }
}
