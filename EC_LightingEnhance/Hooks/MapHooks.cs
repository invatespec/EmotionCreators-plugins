using HarmonyLib;
using Map;
using UnityEngine;

namespace EC_LightingEnhance.Hooks
{
    internal static class MapHooks
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Map.Map), "SetSunType")]
        internal static void SetSunTypePostfix()
        {
            if (!Singleton<Map.Map>.IsInstance()) return;

            LightingEnhanceCore.ForceApply();
        }
    }
}
