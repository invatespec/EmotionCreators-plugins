using HarmonyLib;
using HEdit;
using UnityEngine;

namespace EC_LightingEnhance.Hooks
{
    /// <summary>
    /// 修游戏原生 bug：全局「セルフシャドウ」开关与逐 cut/part 存档值失同步。
    /// </summary>
    internal static class SelfShadowSyncHooks
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(HEditGlobal), "SetImageEffectSelfShadow")]
        internal static void SetImageEffectSelfShadowPostfix(bool _isShadow)
        {
            if (!LightingEnhancePlugin.SyncGlobalSelfShadow.Value)
                return;

            // EtcData 在 Manager.Config.Start() 里才赋值，早期可能为 null
            var etc = Manager.Config.EtcData;
            if (etc == null) return;

            // 全局开 → 逐 cut 值写入偶数档符合全局语义，不干预
            if (etc.SelfShadow)
                return;

            // 全局关 → 偶档（有阴影）必须拉回奇档；游戏刚好写了奇档时无需再写
            int current = QualitySettings.GetQualityLevel();
            int target = current / 2 * 2 + 1;
            if (target == current)
                return;

            QualitySettings.SetQualityLevel(target);
        }
    }
}
