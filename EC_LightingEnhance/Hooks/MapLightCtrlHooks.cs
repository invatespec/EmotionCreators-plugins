using HarmonyLib;
using Map;
using UnityEngine;

namespace EC_LightingEnhance.Hooks
{
    internal static class MapLightCtrlHooks
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(LightCtrl), "Reflect")]
        internal static void ReflectPostfix(LightCtrl __instance)
        {
            var lightMap = Traverse.Create(__instance).Field("lightMap").GetValue<Light>();
            if (lightMap == null) return;

            LightingEnhanceCore.SyncToSunlight(lightMap);
            LightingEnhanceCore.OnLightUpdate();
        }
    }
}
