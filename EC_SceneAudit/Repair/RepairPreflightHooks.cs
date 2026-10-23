using System.Collections.Generic;
using HarmonyLib;
using HEdit;
using UnityEngine.EventSystems;
using YS_Node;

namespace EC_SceneAudit.Repair
{
    // IMGUI 窗口和 uGUI 各自处理同一份输入；窗口命中区要阻断节点画布的拖拽与射线。
    internal static class RepairPreflightHooks
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(HEditScene), "ShortcutKey")]
        private static bool ShortcutKeyPrefix() => !RepairPreflightWindow.HasInputFocus;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HEditGlobal), "isInputNow", MethodType.Getter)]
        private static void IsInputNowPostfix(ref bool __result)
        {
            __result = __result || RepairPreflightWindow.HasInputFocus;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NodeControl), "Update")]
        private static bool NodeUpdatePrefix() => !RepairWindowInputGuard.BlocksGameInput();

        // 只屏蔽窗口内起始的手势与悬停；窗口外起始的按住必须放行，否则 uGUI 收不到松开、
        // 节点的拖拽/平移/连线状态会一直挂着。
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EventSystem), "RaycastAll")]
        private static void EventSystemRaycastAllPostfix(List<RaycastResult> raycastResults)
        {
            try
            {
                if (raycastResults == null || raycastResults.Count == 0)
                    return;
                if (!RepairWindowInputGuard.BlocksGameInput())
                    return;
                raycastResults.Clear();
            }
            catch
            {
                // 钩子内异常不能打断 EventSystem，未命中即不屏蔽。
            }
        }
    }
}
