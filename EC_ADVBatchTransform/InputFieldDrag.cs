using ADVCreate;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace EC_ADVBatchTransform
{
    // 原生只有输入框左边的字母标签能左右拖改值，点输入框本体只会进文字编辑态。
    // 这里让输入框本身也能按住左右拖改值：有 DragValue 就转发它的回调（与标签拖动一致），
    // 只有滑条的字段按值域换算后写 slider。单击不拖仍是原生聚焦输入
    internal sealed class InputFieldDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        // 累计到该像素数才转发：原生 fontSize/lineSpacing 回调会丢弃绝对值小于 2 的增量
        private const float Step = 2f;

        // 滑条按像素换算：200px 走完整个值域
        private const float SliderPixels = 200f;

        private DragValue value;
        private Slider slider;
        private TMP_InputField field;

        private float accum;

        // 本次拖动由插件接管；原生 TMP_InputField.OnDrag（改文字选区）据此跳过
        internal bool Takeover { get; private set; }

        internal void Bind(DragValue dragValue, Slider valueSlider, TMP_InputField inputField)
        {
            value = dragValue;
            slider = valueSlider;
            field = inputField;
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            accum = 0f;
            Takeover = EditorContext.InTextEffectPage && (value != null || slider != null);
            if (!Takeover) return;
            // 取号代表按下的那一刻：拖动期间面板比对被暂停，改动会在松手后才入栈。
            // 槽位面板与本体面板同时可见，按控件所属面板区分作用域
            var layout = EditorContext.LayoutPanel;
            ActionClock.Begin(layout != null && field != null && field.transform.IsChildOf(layout.transform)
                ? ActionScope.Slot
                : ActionScope.Root);
            // 按下时原生已把输入框聚焦；拖动会置 eligibleForClick=false，松手后不会再派发
            // Click 重新聚焦，所以在这里退出编辑态即可：否则焦点一直占着，
            // IsTyping 会挡掉 Ctrl+Z 和方向键，输入框自身的文本回写也会盖掉拖动结果
            if (field != null && field.isFocused) field.DeactivateInputField();
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!Takeover) return;
            if (!EditorContext.InTextEffectPage)
            {
                Takeover = false;
                return;
            }
            accum += eventData.delta.x;
            if (Mathf.Abs(accum) < Step) return;
            Apply(accum);
            accum = 0f;
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            accum = 0f;
            Takeover = false;
        }

        private void Apply(float delta)
        {
            if (value != null)
            {
                if (value.onDrag != null) value.onDrag(delta);
                return;
            }
            float span = slider.maxValue - slider.minValue;
            slider.value += delta * (span / SliderPixels);
        }
    }
}
