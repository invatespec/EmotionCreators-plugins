using System;
using ADVPart.Manipulate;
using HarmonyLib;
using UnityEngine;

namespace EC_ADVBatchTransform.Hooks
{
    using SortOrder = HEdit.ADVPart.SortOrder;
    using SpeechBubbles = HEdit.ADVPart.SpeechBubbles;
    using ScreenEffect = HEdit.ADVPart.ScreenEffect;

    // 复制、新增、删除、换样式都走 AddText/AddEffect 与 DeleteText/DeleteEffect。
    // 换样式不经过 DeleteText（直接删 GameObject 再 Add 同一实例），所以新增时“原本已在列表”即可排除
    internal static class LifecycleHooks
    {
        private static bool wasInCut;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(TextUICtrl), nameof(TextUICtrl.DeleteText))]
        private static void BeforeDeleteText(SpeechBubbles _speechBubbles)
        {
            Safe(() => OnDelete(_speechBubbles));
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EffectUICtrl), nameof(EffectUICtrl.DeleteEffect))]
        private static void BeforeDeleteEffect(ScreenEffect _screenEffect)
        {
            Safe(() => OnDelete(_screenEffect));
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(TextUICtrl), nameof(TextUICtrl.AddText))]
        private static void BeforeAddText(SpeechBubbles _speechBubbles)
        {
            Safe(() => wasInCut = InCut(_speechBubbles));
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EffectUICtrl), nameof(EffectUICtrl.AddEffect))]
        private static void BeforeAddEffect(ScreenEffect _screenEffect)
        {
            Safe(() => wasInCut = InCut(_screenEffect));
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TextUICtrl), nameof(TextUICtrl.AddText))]
        private static void AfterAddText(SpeechBubbles _speechBubbles, Transform __result)
        {
            Safe(() => OnAdd(_speechBubbles, __result));
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EffectUICtrl), nameof(EffectUICtrl.AddEffect))]
        private static void AfterAddEffect(ScreenEffect _screenEffect, Transform __result)
        {
            Safe(() => OnAdd(_screenEffect, __result));
        }

        [HarmonyPatch(typeof(TextUICtrl), nameof(TextUICtrl.ChangeText))]
        private static class ChangeTextHooks
        {
            private static int oldId;

            [HarmonyPrefix]
            private static void Prefix(SpeechBubbles _speechBubbles)
            {
                Safe(() => oldId = _speechBubbles != null ? _speechBubbles.id : 0);
            }

            [HarmonyPostfix]
            private static void Postfix(SpeechBubbles _speechBubbles)
            {
                Safe(() => OnChange(_speechBubbles, oldId));
            }
        }

        [HarmonyPatch(typeof(EffectUICtrl), nameof(EffectUICtrl.ChangeEffect))]
        private static class ChangeEffectHooks
        {
            private static int oldId;

            [HarmonyPrefix]
            private static void Prefix(ScreenEffect _screenEffect)
            {
                Safe(() => oldId = _screenEffect != null ? _screenEffect.id : 0);
            }

            [HarmonyPostfix]
            private static void Postfix(ScreenEffect _screenEffect)
            {
                Safe(() => OnChange(_screenEffect, oldId));
            }
        }

        private static void OnChange(SortOrder so, int oldId)
        {
            if (UndoStack.Executing || !EditorContext.InTextEffectPage || so == null) return;
            int now;
            if (so is SpeechBubbles sb) now = sb.id;
            else if (so is ScreenEffect se) now = se.id;
            else return;
            if (now == oldId) return;
            UndoStack.Push(new StyleEntry(so, oldId));
        }

        // 必须在原生把对象移出列表之前记，删除后才判不出旧图层
        private static void OnDelete(SortOrder so)
        {
            if (UndoStack.Executing || !EditorContext.InTextEffectPage || so == null) return;
            if (!InCut(so)) return;
            var list = EditorContext.List;
            if (list == null) return;
            UndoStack.Push(new DeleteEntry(so, so.sortOrder, list.lstUI));
        }

        private static void OnAdd(SortOrder so, Transform result)
        {
            // 换样式：同一实例原本就在列表里，不算新增
            if (wasInCut || result == null || UndoStack.Executing || !EditorContext.InTextEffectPage) return;
            UndoStack.Push(new SpawnEntry(so));
        }

        private static bool InCut(SortOrder so)
        {
            var cut = EditorContext.Cut;
            if (cut == null) return false;
            if (so is SpeechBubbles sb) return cut.speechBubbles.Contains(sb);
            return so is ScreenEffect se && cut.screenEffects.Contains(se);
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
