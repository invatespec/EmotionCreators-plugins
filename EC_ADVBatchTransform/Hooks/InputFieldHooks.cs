using System;
using ADVPart.Manipulate;
using HarmonyLib;
using TMPro;
using UnityEngine.EventSystems;

namespace EC_ADVBatchTransform.Hooks
{
    using SpeechBubbles = HEdit.ADVPart.SpeechBubbles;

    // 面板初始化完成后给每个数值输入框挂拖动改值；槽位面板打开时接管文本历史
    internal static class InputFieldHooks
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(TextUICtrl), nameof(TextUICtrl.Init))]
        private static void TextInitPostfix(TextUICtrl __instance)
        {
            Safe(() => InputFieldDragInstaller.Install(__instance));
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EffectUICtrl), nameof(EffectUICtrl.Init))]
        private static void EffectInitPostfix(EffectUICtrl __instance)
        {
            Safe(() => InputFieldDragInstaller.Install(__instance));
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TextLayoutUICtrl), nameof(TextLayoutUICtrl.Init))]
        private static void LayoutInitPostfix(TextLayoutUICtrl __instance)
        {
            Safe(() =>
            {
                InputFieldDragInstaller.Install(__instance);
                // 面板重建会重订原生监听；这里每次都补挂，避免我们的监听被清掉后无人察觉
                TextHistory.Attach(InputFieldDragInstaller.TextInputOf(__instance));
            });
        }

        // 面板数值框的聚焦/失焦：给键盘手势提供「开始编辑」时刻，序号才不会取晚
        [HarmonyPostfix]
        [HarmonyPatch(typeof(TMP_InputField), nameof(TMP_InputField.OnSelect))]
        private static void SelectPostfix(TMP_InputField __instance)
        {
            Safe(() => EditFocus.Set(__instance.gameObject));
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TMP_InputField), nameof(TMP_InputField.OnDeselect))]
        private static void DeselectPostfix(TMP_InputField __instance)
        {
            Safe(() => EditFocus.Clear(__instance.gameObject));
        }

        // 槽位面板打开时记住起始文本，并接管槽位文本框的变化事件
        [HarmonyPostfix]
        [HarmonyPatch(typeof(TextLayoutUICtrl), nameof(TextLayoutUICtrl.Active))]
        private static void LayoutActivePostfix(TextLayoutUICtrl __instance, int _index)
        {
            Safe(() =>
            {
                var bubble = EditorContext.Primary as SpeechBubbles;
                TextHistory.Prime(bubble, _index);
                TextHistory.Attach(InputFieldDragInstaller.TextInputOf(__instance));
            });
        }

        // 关闭槽位时把最后一段输入立刻结清，不等停顿计时
        [HarmonyPrefix]
        [HarmonyPatch(typeof(TextLayoutUICtrl), nameof(TextLayoutUICtrl.Deactivate))]
        private static void LayoutDeactivatePrefix()
        {
            Safe(TextHistory.CommitPending);
        }

        // 插件接管时跳过原生的选字处理，否则选区跟着鼠标跑、还会打断数值写入。
        // 拖动后 EventSystem 不会再派发 Click（eligibleForClick 已置 false），不用管聚焦
        [HarmonyPrefix]
        [HarmonyPatch(typeof(TMP_InputField), nameof(TMP_InputField.OnDrag))]
        private static bool DragPrefix(TMP_InputField __instance)
        {
            return !Takeover(__instance);
        }

        private static bool Takeover(TMP_InputField field)
        {
            var drag = field != null ? field.GetComponent<InputFieldDrag>() : null;
            return drag != null && drag.Takeover;
        }

        private static void Safe(Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                ADVBatchTransformPlugin.Log.LogError(e);
            }
        }
    }
}
