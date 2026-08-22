using HarmonyLib;
using HEdit;
using UnityEngine;

namespace EC_LightingEnhance.Hooks
{
    /// <summary>
    /// 修游戏原生 bug：全局「セルフシャドウ」开关与逐 cut/part 存档值失同步。
    /// 根因与证据见 .trellis/tasks/08-04-face-sdf-shadow/research/2026-08-15-selfshadow-toggle-desync.md
    ///
    /// 机制：全局 Config.EtcData.SelfShadow 只写 QualitySettings 质量等级奇偶位
    /// （偶数=有阴影 / 奇数=无），且只在启动与手动拨 toggle 两个瞬间各写一次；
    /// SetImageEffectSelfShadow 是逐 cut/part 存档值（默认 true）写 QualitySettings 的
    /// 唯一通道，切 cut/选 part 都会把质量等级覆盖回偶数。Postfix 在覆盖发生后把
    /// 全局值重新推回：全局关 → 强制奇档。全局开时不干预（尊重 cut 自己的选择）。
    ///
    /// 只修 QualitySettings 路，不碰 Light.shadows。
    /// 实测（群主 2026-08-15）确认单点足够：奇数档名 "Quality No Shadows"、
    /// `QualitySettings.shadows=Disable`，阴影立即消失；即便 lightChara/lightMap 残留
    /// `Soft`（Light.shadows 路被 cut 存档值覆盖）也不影响视觉效果。
    /// 结论已沉淀到 research/2026-08-15-selfshadow-toggle-desync.md「五、待验证」。
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
