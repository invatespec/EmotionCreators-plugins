using ADVPart;
using HarmonyLib;

namespace EC_ADVCameraViewport
{
    /// <summary>
    /// Harmony 补丁 - 在 ADV 场景初始化时应用 UI 布局调整
    /// </summary>
    internal static class Hooks
    {
        /// <summary>
        /// ManipulateUICtrl.Init 后置补丁
        /// 在 ADV Manipulate UI 初始化完成后应用 UI 布局调整
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ManipulateUICtrl), "Init")]
        private static void ManipulateUICtrl_Init_Postfix()
        {
            ADVCameraViewportPlugin.Log.LogInfo("ManipulateUICtrl 初始化完成，开始应用 UI 布局调整。");

            UILayoutController.ApplyUIAdjustments();
        }
    }
}
