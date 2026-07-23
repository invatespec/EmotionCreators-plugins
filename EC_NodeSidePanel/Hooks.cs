using System;
using System.Collections.Generic;
using HarmonyLib;
using HEdit;
using UnityEngine;
using UnityEngine.EventSystems;
using YS_Node;

namespace EC_NodeSidePanel
{
    internal static class Hooks
    {
        internal static NodeSidePanelPlugin Plugin;

        // isInputNow 只读（看 uGUI InputField 焦点）。IMGUI TextField 聚焦时 OR 为 true，
        // 让 NodeControl 的 R/A 等快捷键与 HEdit 相机键条件跳过。
        [HarmonyPostfix]
        [HarmonyPatch(typeof(HEditGlobal), "get_isInputNow")]
        private static void IsInputNowPostfix(ref bool __result)
        {
            if (!__result && NodeSidePanelPlugin.IsTextInputFocused)
                __result = true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(NodeControl), "Awake")]
        private static void NodeControlAwakePostfix(NodeControl __instance)
        {
            try
            {
                CanvasExpandService.TryExpand(__instance);
            }
            catch (Exception ex)
            {
                NodeSidePanelPlugin.Log?.LogError($"TryExpand failed: {ex}");
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NodeControl), "GridDragFunc")]
        private static bool GridDragFuncPrefix(NodeControl __instance, Vector2 move)
            => CanvasExpandService.GridDragFuncPrefix(__instance, move);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NodeControl), "Update")]
        private static bool NodeControlUpdatePrefix(NodeControl __instance)
            => CanvasExpandService.NodeControlUpdatePrefix(__instance);

        // 面板命中区阻断网格/节点/连线拖拽，避免点穿到画布
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GridDrag), "OnPointerDown")]
        private static bool GridDragPointerDownPrefix() => !NodeSidePanelPlugin.IsMouseOverPanel();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GridDrag), "OnBeginDrag")]
        private static bool GridDragBeginPrefix() => !NodeSidePanelPlugin.IsMouseOverPanel();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GridDrag), "OnDrag")]
        private static bool GridDragDragPrefix() => !NodeSidePanelPlugin.IsMouseOverPanel();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GridDrag), "OnEndDrag")]
        private static bool GridDragEndPrefix() => !NodeSidePanelPlugin.IsMouseOverPanel();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NodeDrag), "OnPointerDown")]
        private static bool NodeDragPointerDownPrefix() => !NodeSidePanelPlugin.IsMouseOverPanel();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NodeDrag), "OnBeginDrag")]
        private static bool NodeDragBeginPrefix() => !NodeSidePanelPlugin.IsMouseOverPanel();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NodeDrag), "OnDrag")]
        private static bool NodeDragDragPrefix() => !NodeSidePanelPlugin.IsMouseOverPanel();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NodeDrag), "OnEndDrag")]
        private static bool NodeDragEndPrefix() => !NodeSidePanelPlugin.IsMouseOverPanel();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NodeConnectDrag), "OnPointerDown")]
        private static bool NodeConnectPointerDownPrefix() => !NodeSidePanelPlugin.IsMouseOverPanel();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NodeConnectDrag), "OnBeginDrag")]
        private static bool NodeConnectBeginPrefix() => !NodeSidePanelPlugin.IsMouseOverPanel();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NodeConnectDrag), "OnDrag")]
        private static bool NodeConnectDragPrefix() => !NodeSidePanelPlugin.IsMouseOverPanel();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NodeConnectDrag), "OnEndDrag")]
        private static bool NodeConnectEndPrefix() => !NodeSidePanelPlugin.IsMouseOverPanel();

        // 清空 uGUI 射线结果，面板下的按钮/节点不再被点到
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EventSystem), "RaycastAll")]
        private static void EventSystemRaycastAllPostfix(List<RaycastResult> raycastResults)
        {
            if (raycastResults == null || raycastResults.Count == 0)
                return;
            if (!NodeSidePanelPlugin.IsMouseOverPanel())
                return;
            raycastResults.Clear();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(NodeControl), nameof(NodeControl.Create))]
        private static void CreatePostfix(NodeBase __result)
        {
            if (__result == null || Plugin == null)
                return;
            // OnCreated 已 MarkDirty 列表
            Plugin.ListModel.OnCreated(__result.uid);
            Plugin.ConfigStore.MarkDirty();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(NodeControl), nameof(NodeControl.NodeDelete))]
        private static void NodeDeletePostfix(NodeUI nodeUI)
        {
            if (nodeUI?.nodeBase == null || Plugin == null)
                return;
            string uid = nodeUI.nodeBase.uid;
            // OnDeleted 已 MarkDirty 列表
            Plugin.ListModel.OnDeleted(uid);
            Plugin.Folders.MoveNodesOut(new[] { uid });
            Plugin.ConfigStore.MarkDirty();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(NodeControl), nameof(NodeControl.NodeCopy))]
        private static void NodeCopyPostfix()
        {
            if (Plugin == null)
                return;
            Plugin.ListModel.MarkDirty();
            Plugin.ConfigStore.MarkDirty();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(NodeControl), nameof(NodeControl.RebuildNodeUI))]
        private static void RebuildNodeUIPostfix(bool __result)
        {
            if (!__result || Plugin == null)
                return;
            Plugin.OnSceneNodesRebuilt();
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(HEditGlobal), nameof(HEditGlobal.Save))]
        private static void SavePrefix()
        {
            Plugin?.RebindConfigKeyAndFlush();
        }
    }
}
