using System.Collections;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using HEdit;

namespace EC_SceneAutoSave.Hooks
{
    // 轮询看不到的改动来源：打开 Part 前的基准内容、换卡、EC_BatchTextReplace 的批量替换。
    internal static class ChangeHooks
    {
        private const string BatchReplaceGuid = "EC_BatchTextReplace";

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NodeSettingCanvas), "Edit")]
        private static void EditPrefix(string _s)
        {
            SceneIoHooks.Guard(() => SceneAutoSavePlugin.Controller.OnBeforePartEdit(_s));
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BaseSettingCharacterUI), "OnSelect")]
        private static void CharaSelectPostfix(BaseSettingCharacterUI __instance, BaseSettingCharaCardInfo _cardInfo)
        {
            SceneIoHooks.Guard(() =>
            {
                var cards = Traverse.Create(__instance).Field("infoCharaCards").GetValue<IList>();
                SceneAutoSavePlugin.Controller.OnCharaSwapped(cards?.IndexOf(_cardInfo) ?? -1);
            });
        }

        // 软依赖：没装该插件时静默跳过；装了但找不到方法时警告一次。
        public static void PatchBatchTextReplace(Harmony harmony)
        {
            if (!Chainloader.PluginInfos.TryGetValue(BatchReplaceGuid, out var info) || info.Instance == null)
                return;
            var method = AccessTools.Method(info.Instance.GetType(), "ReplaceInPart");
            if (method == null)
            {
                SceneAutoSavePlugin.Log.LogWarning("EC_BatchTextReplace.ReplaceInPart not found; batch replace edits rely on part polling.");
                return;
            }
            harmony.Patch(method, postfix: new HarmonyMethod(typeof(ChangeHooks).GetMethod(nameof(BatchReplacePostfix),
                BindingFlags.Static | BindingFlags.NonPublic)));
        }

        private static void BatchReplacePostfix(HEdit.ADVPart advPart, int __result)
        {
            if (__result > 0 && advPart != null)
                SceneIoHooks.Guard(() => SceneAutoSavePlugin.Controller.OnBatchReplaced(advPart.uuId));
        }
    }
}
