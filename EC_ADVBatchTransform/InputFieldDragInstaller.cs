using System;
using System.Collections.Generic;
using System.Reflection;
using ADVPart.Manipulate;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EC_ADVBatchTransform
{
    // 给 ADV Part 编辑器的数值输入框挂 InputFieldDrag：面板的 InputUI 是私有嵌套类，
    // 只能反射取 inputField / dragValue / slider 三个字段再转接
    internal static class InputFieldDragInstaller
    {
        private static bool resolved;
        private static FieldInfo sliderField;
        private static FieldInfo inputFieldField;
        private static FieldInfo dragValueField;

        internal static void Install(ManipulateBaseUICtrl panel)
        {
            if (panel == null) return;
            Resolve();
            if (inputFieldField == null) return;
            switch (panel)
            {
                case TextUICtrl text:
                    Bind(text, "inputPos", true);
                    Bind(text, "inputSize", true);
                    Bind(text, "inputRot", false);
                    Bind(text, "inputWidth", false);
                    break;
                case EffectUICtrl effect:
                    Bind(effect, "inputPos", true);
                    Bind(effect, "inputSize", true);
                    Bind(effect, "inputRot", false);
                    Bind(effect, "inputWidth", false);
                    break;
                case TextLayoutUICtrl layout:
                    Bind(layout, "inputFontSize", false);
                    Bind(layout, "inputLineSpacing", false);
                    Bind(layout, "inputOutlineSpread", false);
                    Bind(layout, "inputRot", false);
                    BindRange(layout);
                    break;
            }
        }

        // 数组字段（Pos/Size）逐个 InputUI 处理，单字段直接处理
        private static void Bind(object owner, string fieldName, bool isArray)
        {
            var field = AccessTools.Field(owner.GetType(), fieldName);
            var value = field?.GetValue(owner);
            if (value == null) return;
            if (isArray)
            {
                if (value is Array arr)
                    foreach (var item in arr) BindOne(item);
                return;
            }
            BindOne(value);
        }

        private static void BindOne(object inputUI)
        {
            if (inputUI == null || inputFieldField == null) return;
            var field = inputFieldField.GetValue(inputUI) as TMP_InputField;
            if (field == null) return;
            var dragValue = dragValueField?.GetValue(inputUI) as ADVCreate.DragValue;
            var slider = sliderField?.GetValue(inputUI) as Slider;
            if (dragValue == null && slider == null) return;
            Attach(field, dragValue, slider);
        }

        // 文字范围四边（Top/Bottom/Left/Right）在 InputRangeUI 里成对存放
        private static void BindRange(TextLayoutUICtrl layout)
        {
            var range = AccessTools.Field(typeof(TextLayoutUICtrl), "inputRange")?.GetValue(layout);
            if (range == null) return;
            var fields = AccessTools.Field(range.GetType(), "inputField")?.GetValue(range) as TMP_InputField[];
            var drags = AccessTools.Field(range.GetType(), "dragValues")?.GetValue(range) as ADVCreate.DragValue[];
            if (fields == null) return;
            for (int i = 0; i < fields.Length; i++)
            {
                var drag = drags != null && i < drags.Length ? drags[i] : null;
                if (fields[i] != null && drag != null) Attach(fields[i], drag, null);
            }
        }

        private static void Attach(TMP_InputField field, ADVCreate.DragValue dragValue, Slider slider)
        {
            var comp = field.GetComponent<InputFieldDrag>();
            if (comp == null) comp = field.gameObject.AddComponent<InputFieldDrag>();
            comp.Bind(dragValue, slider, field);
        }

        // 槽位面板的文本框是旧版 InputField（不是 TMP），文本历史要单独接管
        internal static InputField TextInputOf(TextLayoutUICtrl layout)
        {
            return AccessTools.Field(typeof(TextLayoutUICtrl), "inputText")?.GetValue(layout) as InputField;
        }

        private static void Resolve()
        {
            if (resolved) return;
            resolved = true;
            // InputUI 是 ManipulateBaseUICtrl 的 protected 嵌套类，字段名固定
            var nested = typeof(ManipulateBaseUICtrl).GetNestedType("InputUI", BindingFlags.NonPublic);
            if (nested == null)
            {
                ADVBatchTransformPlugin.Log.LogWarning("未找到 InputUI 类型，输入框拖动改值停用");
                return;
            }
            sliderField = AccessTools.Field(nested, "slider");
            inputFieldField = AccessTools.Field(nested, "inputField");
            dragValueField = AccessTools.Field(nested, "dragValue");
        }
    }
}
