using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EC_LightingEnhance
{
    [BepInProcess("EmotionCreators")]
    [BepInPlugin(GUID, PluginName, Version)]
    public class LightingEnhancePlugin : BaseUnityPlugin
    {
        public const string GUID = "EC_LightingEnhance";
        public const string PluginName = "Lighting Enhance";
        public const string Version = "1.2.0";

        internal static ManualLogSource Log;
        private static bool _pendingShadowApply = true;
        private static int _pendingRetryCount = 0;
        private const int MaxRetryFrames = 5;

        // === Sunlight Sync 配置 ===
        internal static ConfigEntry<float> SunIntensityMultiplier;

        // === Shadow Quality 配置（默认关闭） ===
        internal static ConfigEntry<bool> ShadowQualityEnabled;
        internal static ConfigEntry<bool> CullingMaskAddChara;
        internal static ConfigEntry<string> ShadowResolution;
        internal static ConfigEntry<int> ShadowCustomResolution;
        internal static ConfigEntry<float> ShadowStrength;
        internal static ConfigEntry<float> ShadowBias;
        internal static ConfigEntry<float> ShadowNormalBias;
        internal static ConfigEntry<float> ShadowNearPlane;
        internal static ConfigEntry<bool> UseShadowProxies;

        // === Character self shadow config ===
        internal static ConfigEntry<bool> SyncCameraDirectionalLightEulerAngles;

        // === Render mode fallback config ===
        internal static ConfigEntry<bool> CharaLightPriorityEnabled;
        internal static ConfigEntry<string> CharaLightRenderMode;
        internal static ConfigEntry<string> MapLightRenderMode;

        internal void Awake()
        {
            Log = Logger;

            SunIntensityMultiplier = Config.Bind(
                "Sunlight Sync", "SunIntensityMultiplier", 0f,
                new ConfigDescription(
                    "Sunlight intensity multiplier (0.0 ~ 3.0). 0 = no sync. Re-adjust in-game light for it to take effect.",
                    new AcceptableValueRange<float>(0f, 3f)));

            // 数字前缀控制 BepInEx 配置管理器的 UI 排序
            ShadowQualityEnabled = Config.Bind(
                "Shadow Quality", "00_Enabled", false,
                "Master switch. Enable shadow quality modifications for map sunlight.");

            CullingMaskAddChara = Config.Bind(
                "Shadow Quality", "01_CullingMaskAddChara", true,
                "Enable detailed ground shadows. With shadow proxies enabled, map lights keep Map layer and use invisible proxy renderers.");

            UseShadowProxies = Config.Bind(
                "Shadow Quality", "02_UseShadowProxies", true,
                "Use invisible Map-layer shadow proxy renderers instead of adding Chara layer directly to map lights. Prevents map lights from overriding character lighting.");

            ShadowResolution = Config.Bind(
                "Shadow Quality", "03_ShadowResolution", "VeryHigh",
                new ConfigDescription(
                    "Shadow resolution quality level. FromQualitySettings = no change.",
                    new AcceptableValueList<string>(new[] { "FromQualitySettings", "Low", "Medium", "High", "VeryHigh" })));

            ShadowCustomResolution = Config.Bind(
                "Shadow Quality", "04_ShadowCustomResolution", -1,
                new ConfigDescription(
                    "Custom shadow resolution (-1 = disabled, use this value directly otherwise).",
                    new AcceptableValueList<int>(new[] { -1, 512, 1024, 2048, 4096, 8192 })));

            ShadowStrength = Config.Bind(
                "Shadow Quality", "05_ShadowStrength", 1.0f,
                new ConfigDescription("Shadow strength (0.0 ~ 1.0).", new AcceptableValueRange<float>(0f, 1f)));

            ShadowBias = Config.Bind(
                "Shadow Quality", "06_ShadowBias", 0.05f,
                new ConfigDescription("Shadow bias (-0.1 ~ 0.1). Lower = more detail, may cause acne.", new AcceptableValueRange<float>(-0.1f, 0.1f)));

            ShadowNormalBias = Config.Bind(
                "Shadow Quality", "07_ShadowNormalBias", 0.4f,
                new ConfigDescription("Shadow normal bias (0.0 ~ 1.0). Lower = tighter fit.", new AcceptableValueRange<float>(0f, 1f)));

            ShadowNearPlane = Config.Bind(
                "Shadow Quality", "08_ShadowNearPlane", 0.2f,
                new ConfigDescription("Shadow near plane (0.0 ~ 1.0).", new AcceptableValueRange<float>(0f, 1f)));


            SyncCameraDirectionalLightEulerAngles = Config.Bind(
                "Character Self Shadow", "SyncCameraDirectionalLightEulerAngles", true,
                "Sync Camera/Main Camera/Directional Light world eulerAngles to HEditGlobal.lightChara. Rotation only; color, intensity and shadow settings are not changed.");

            CharaLightPriorityEnabled = Config.Bind(
                "Light Priority", "00_Enabled", true,
                new ConfigDescription(
                    "Fallback for direct Chara culling-mask mode. Shadow proxies normally avoid the map-light override without changing character light render mode.",
                    null,
                    ConfigurationManagerAttributes.Advanced));

            CharaLightRenderMode = Config.Bind(
                "Light Priority", "01_CharaLightRenderMode", "ForcePixel",
                new ConfigDescription(
                    "Render mode for HEditGlobal.lightChara. ForcePixel matches Unity's Important mode.",
                    new AcceptableValueList<string>(new[] { "Keep", "Auto", "ForcePixel", "ForceVertex" }),
                    ConfigurationManagerAttributes.Advanced));

            MapLightRenderMode = Config.Bind(
                "Light Priority", "02_MapLightRenderMode", "Keep",
                new ConfigDescription(
                    "Render mode for map directional lights. With shadow proxies enabled, Keep/ForceVertex are applied as Auto because ForceVertex can disable realtime map shadows.",
                    new AcceptableValueList<string>(new[] { "Keep", "Auto", "ForcePixel", "ForceVertex" }),
                    ConfigurationManagerAttributes.Advanced));

            // 任意 Shadow Quality 配置修改后即时生效，无需重启
            ShadowQualityEnabled.SettingChanged += OnShadowQualitySettingChanged;
            ShadowResolution.SettingChanged += OnShadowQualitySettingChanged;
            ShadowCustomResolution.SettingChanged += OnShadowQualitySettingChanged;
            ShadowStrength.SettingChanged += OnShadowQualitySettingChanged;
            ShadowBias.SettingChanged += OnShadowQualitySettingChanged;
            ShadowNormalBias.SettingChanged += OnShadowQualitySettingChanged;
            ShadowNearPlane.SettingChanged += OnShadowQualitySettingChanged;
            CullingMaskAddChara.SettingChanged += OnShadowQualitySettingChanged;
            UseShadowProxies.SettingChanged += OnShadowQualitySettingChanged;
            CharaLightPriorityEnabled.SettingChanged += OnShadowQualitySettingChanged;
            CharaLightRenderMode.SettingChanged += OnShadowQualitySettingChanged;
            MapLightRenderMode.SettingChanged += OnShadowQualitySettingChanged;
            SyncCameraDirectionalLightEulerAngles.SettingChanged += OnCameraCharaLightSettingChanged;

            var harmony = new Harmony(GUID);
            harmony.PatchAll(typeof(Hooks.MapLightCtrlHooks));
            harmony.PatchAll(typeof(Hooks.MapHooks));
            harmony.PatchAll(typeof(Hooks.ADVLightUIHooks));
            harmony.PatchAll(typeof(Hooks.HEditLightUIHooks));
            harmony.PatchAll(typeof(Hooks.HPlayLightUIHooks));

            Camera.onPreCull -= OnCameraPreCull;
            Camera.onPreCull += OnCameraPreCull;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;

            Log.LogInfo($"Lighting Enhance v{Version} started.");
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            LightingEnhanceCore.MarkShadowProxiesDirty();
            LightingEnhanceCore.MarkCameraCharaLightDirty();
            _pendingShadowApply = true;
            _pendingRetryCount = 0;
        }

        private static void OnShadowQualitySettingChanged(object sender, System.EventArgs e)
        {
            LightingEnhanceCore.MarkShadowProxiesDirty();
            _pendingShadowApply = true;
        }

        private static void OnCameraCharaLightSettingChanged(object sender, System.EventArgs e)
        {
            LightingEnhanceCore.MarkCameraCharaLightDirty();
        }

        internal void LateUpdate()
        {
            LightingEnhanceCore.ApplyCameraCharaLightEulerSync(allowResolve: true);
            LightingEnhanceCore.SyncShadowProxyStates();
            LightingEnhanceCore.RetryMapLightShadowApplyIfReady();
            ApplyPendingShadowSettings();
        }

        private static void OnCameraPreCull(Camera camera)
        {
            LightingEnhanceCore.ApplyCameraCharaLightEulerSync(allowResolve: false);
        }

        private void OnDestroy()
        {
            Camera.onPreCull -= OnCameraPreCull;
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private static void ApplyPendingShadowSettings()
        {
            if (!_pendingShadowApply) return;

            if (LightingEnhanceCore.ForceApply())
            {
                _pendingShadowApply = false;
                _pendingRetryCount = 0;
                return;
            }

            // 连续失败超过 MaxRetryFrames 帧则放弃本轮重试，避免在无地图场景中持续产生 GC 开销。
            // 下次进入地图/光照 UI 操作时 Hook 会重置 _pendingShadowApply = true。
            if (++_pendingRetryCount >= MaxRetryFrames)
            {
                _pendingShadowApply = false;
                _pendingRetryCount = 0;
            }
        }
    }

    public sealed class ConfigurationManagerAttributes
    {
        internal static readonly ConfigurationManagerAttributes Advanced = new ConfigurationManagerAttributes
        {
            IsAdvanced = true
        };

        public bool IsAdvanced;
    }
}
