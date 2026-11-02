using System;
using ADVPart.List;
using HarmonyLib;

namespace EC_ADVBatchTransform.Hooks
{
    // 列表上方的复制/删除按钮走 TextAndEffectListUICtrl 的私有方法。
    // 多选时由插件接管整组执行，单选仍交给原生
    internal static class ListButtonHooks
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(TextAndEffectListUICtrl), "Duplicate")]
        private static bool DuplicatePrefix(TextAndEffectListUICtrl __instance)
        {
            return !Safe(() => BatchEdit.TryDuplicate(__instance));
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(TextAndEffectListUICtrl), "Delete")]
        private static bool DeletePrefix(TextAndEffectListUICtrl __instance)
        {
            return !Safe(() => BatchEdit.TryDelete(__instance));
        }

        // 出错时放行原生：插件可能什么都没改，拦下来反而让按钮失灵。批量已动过列表的情况
        // 由 BatchEdit 自己的 try/finally 收尾，异常不会漏到这里
        private static bool Safe(Func<bool> action)
        {
            try
            {
                return action();
            }
            catch (Exception e)
            {
                ADVBatchTransformPlugin.Log.LogError(e);
                return false;
            }
        }
    }
}
