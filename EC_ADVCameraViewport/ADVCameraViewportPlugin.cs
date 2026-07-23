using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace EC_ADVCameraViewport
{
    [BepInProcess("EmotionCreators")]
    [BepInPlugin(GUID, PluginName, Version)]
    public class ADVCameraViewportPlugin : BaseUnityPlugin
    {
        public const string GUID = "EC_ADVCameraViewport";
        public const string PluginName = "ADV Camera Viewport";
        public const string Version = "2.0.0";

        internal static ManualLogSource Log;

        // 摄像机视口配置
        internal static ConfigEntry<bool> EnableViewportScale;
        internal static ConfigEntry<float> ViewportScale;
        internal static ConfigEntry<float> ViewportOffsetX;
        internal static ConfigEntry<float> ViewportOffsetY;
        internal static ConfigEntry<KeyboardShortcut> ToggleKey;

        // UI 布局配置
        internal static ConfigEntry<bool> AutoAdjustCharaState;
        internal static ConfigEntry<float> CharaStatePosX;
        internal static ConfigEntry<float> CharaStatePosY;
        internal static ConfigEntry<float> ListWidthScale;
        internal static ConfigEntry<bool> ScaleADVCanvas;

        private ViewportController _controller;

        internal void Awake()
        {
            Log = Logger;

            // 摄像机视口配置
            EnableViewportScale = Config.Bind(
                "Viewport",
                "ViewportScaleState",
                true,
                "视口缩放状态");

            ViewportScale = Config.Bind(
                "Viewport",
                "Scale",
                0.75f,
                new ConfigDescription(
                    "视口缩放比例（0.5 ~ 1.0）。1.0 = 全屏，0.75 = 缩小到 75%",
                    new AcceptableValueRange<float>(0.5f, 1.0f)));

            ViewportOffsetX = Config.Bind(
                "Viewport",
                "OffsetX",
                0f,
                new ConfigDescription(
                    "视口水平偏移（-0.5 ~ 0.5）。负值向左，正值向右",
                    new AcceptableValueRange<float>(-0.5f, 0.5f)));

            ViewportOffsetY = Config.Bind(
                "Viewport",
                "OffsetY",
                0f,
                new ConfigDescription(
                    "视口垂直偏移（-0.5 ~ 0.5）。负值向下，正值向上",
                    new AcceptableValueRange<float>(-0.5f, 0.5f)));

            ToggleKey = Config.Bind(
                "Hotkeys",
                "ToggleViewport",
                new KeyboardShortcut(KeyCode.F8),
                "切换视口缩放的快捷键");

            // UI 布局配置
            AutoAdjustCharaState = Config.Bind(
                "UILayout",
                "AutoAdjustCharaState",
                true,
                "自动调整 Chara State 面板位置（监测 Button Chara toggle 状态）");

            CharaStatePosX = Config.Bind(
                "UILayout",
                "CharaStatePosX",
                0.3f,
                new ConfigDescription(
                    "Chara State 面板的目标 X 位置（屏幕宽度百分比，0 ~ 100）",
                    new AcceptableValueRange<float>(0f, 100f)));

            CharaStatePosY = Config.Bind(
                "UILayout",
                "CharaStatePosY",
                66.7f,
                new ConfigDescription(
                    "Chara State 面板的目标 Y 位置（屏幕高度百分比，0 ~ 100）",
                    new AcceptableValueRange<float>(0f, 100f)));

            ListWidthScale = Config.Bind(
                "UILayout",
                "ListWidthScale",
                1f,
                new ConfigDescription(
                    "List 面板宽度缩放比例（0.5 ~ 1.0），1.0 = 原始大小",
                    new AcceptableValueRange<float>(0.5f, 1.0f)));

            ScaleADVCanvas = Config.Bind(
                "UILayout",
                "ScaleADVCanvas",
                true,
                "同步缩放 ADV Canvas（屏幕效果贴图和对话框）以匹配摄像机视口");

            _controller = gameObject.AddComponent<ViewportController>();

            // 配置变更时重新应用（无需重启游戏）
            ListWidthScale.SettingChanged += OnUILayoutSettingChanged;
            EnableViewportScale.SettingChanged += OnUILayoutSettingChanged;
            ViewportScale.SettingChanged += OnUILayoutSettingChanged;
            ViewportOffsetX.SettingChanged += OnUILayoutSettingChanged;
            ViewportOffsetY.SettingChanged += OnUILayoutSettingChanged;
            AutoAdjustCharaState.SettingChanged += OnAutoAdjustCharaStateChanged;

            // 应用 Harmony 补丁
            Harmony.CreateAndPatchAll(typeof(Hooks), GUID);

            Log.LogInfo($"{PluginName} v{Version} 已加载。");
        }

        internal void OnDestroy()
        {
            if (_controller != null)
            {
                Destroy(_controller);
            }
        }

        private void OnUILayoutSettingChanged(object sender, System.EventArgs e)
        {
            UILayoutController.ReapplyUIAdjustments();
            _controller.OnSettingChanged();
        }

        private void OnAutoAdjustCharaStateChanged(object sender, System.EventArgs e)
        {
            if (AutoAdjustCharaState.Value)
            {
                if (UILayoutController.AdvPartRoot != null)
                    UILayoutController.SetupCharaStateAutoAdjust();
            }
            else
            {
                UILayoutController.RestoreCharaState();
            }
        }
    }
}
