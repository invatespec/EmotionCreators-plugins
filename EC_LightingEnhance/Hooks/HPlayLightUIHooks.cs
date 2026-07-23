using HarmonyLib;
using HEdit;
using HPlay;
using Map;
using UnityEngine;

namespace EC_LightingEnhance.Hooks
{
    internal static class HPlayLightUIHooks
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(HPlayHPartLightUI), "Start")]
        internal static void StartPostfix(HPlayHPartLightUI __instance)
        {
            var lightItems = Traverse.Create(__instance).Field("lightItems").GetValue<HPlayHPartUI.LightItemUI[]>();
            if (!HasRotationSliders(lightItems)) return;

            lightItems[3].sl.onValueChanged.AddListener(_ => SyncFromSliders(lightItems));
            lightItems[4].sl.onValueChanged.AddListener(_ => SyncFromSliders(lightItems));
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HPlayHPartLightUI), "SetInit")]
        internal static void SetInitPostfix(CameraImageEffectInfo.LightInfo _info)
        {
            LightingEnhanceCore.OnLightUpdate(refreshShadowProxies: true);
            LightingEnhanceCore.SetCameraCharaLightEuler(_info);
        }

        private static bool HasRotationSliders(HPlayHPartUI.LightItemUI[] lightItems)
        {
            return lightItems != null
                   && lightItems.Length > 4
                   && lightItems[3]?.sl != null
                   && lightItems[4]?.sl != null;
        }

        private static void SyncFromSliders(HPlayHPartUI.LightItemUI[] lightItems)
        {
            if (!HasRotationSliders(lightItems)) return;

            LightingEnhanceCore.SetCameraCharaLightEuler(
                new Vector2(lightItems[3].sl.value, lightItems[4].sl.value));
        }
    }
}
