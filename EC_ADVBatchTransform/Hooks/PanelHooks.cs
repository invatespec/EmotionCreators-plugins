using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using DragMove = ADVPart.DragMove;
using DragRectHandle = ADVPart.DragRectHandle;

namespace EC_ADVBatchTransform.Hooks
{
    // 对画面拖动的起点。指针落在面板控件上时原生 DragMove 也会收到按下，
    // 用 EventSystem 的统一射线取最上层目标，落在对象根节点内才算拖动对象
    internal static class PanelHooks
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DragMove), "OnPointerDown")]
        private static void DragMoveDown(DragMove __instance)
        {
            Safe(() => RecordObject(__instance.transform));
        }

        // 手柄挂在对象根节点下的 Rect Root 上，沿父链找所属对象
        [HarmonyPostfix]
        [HarmonyPatch(typeof(DragRectHandle), "OnPointerDown")]
        private static void HandleDown(DragRectHandle __instance)
        {
            Safe(() => RecordObject(__instance.transform));
        }

        private static void RecordObject(Transform t)
        {
            var objects = EditorContext.Objects;
            if (objects == null || t == null) return;
            foreach (var pair in objects)
            {
                var ctrl = pair.Value;
                if (ctrl == null || ctrl.gameObject == null) continue;
                var root = ctrl.gameObject.transform;
                if (t != root && !t.IsChildOf(root)) continue;
                if (OnTop(root)) EditRecorder.BeginDrag(pair.Key);
                return;
            }
        }

        private static bool OnTop(Transform root)
        {
            var es = EventSystem.current;
            if (es == null) return true;
            var results = new List<RaycastResult>();
            es.RaycastAll(new PointerEventData(es) { position = Input.mousePosition }, results);
            if (results.Count == 0) return false;
            var top = results[0].gameObject.transform;
            return top == root || top.IsChildOf(root);
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
