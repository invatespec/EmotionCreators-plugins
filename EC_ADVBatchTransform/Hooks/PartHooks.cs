using System;
using ADVCreate;
using HarmonyLib;

namespace EC_ADVBatchTransform.Hooks
{
    // 进出 ADV Part 编辑器的信号。SelectPart 提前赋值、cut 引用重进时不变，都不能单独作为信号
    internal static class PartHooks
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ADVPartUICtrl), nameof(ADVPartUICtrl.Active))]
        private static void ActivePostfix()
        {
            try
            {
                EditorDriver.OnPartActive();
            }
            catch (Exception e)
            {
                ADVBatchTransformPlugin.Log.LogError(e);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ADVPartUICtrl), nameof(ADVPartUICtrl.Deactive))]
        private static void DeactivePostfix()
        {
            try
            {
                EditorDriver.OnPartDeactive();
            }
            catch (Exception e)
            {
                ADVBatchTransformPlugin.Log.LogError(e);
            }
        }
    }
}
