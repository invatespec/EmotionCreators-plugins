using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace EC_ADVBatchTransform
{
    internal enum PivotMode
    {
        Individual,
        Group
    }

    [BepInProcess("EmotionCreators")]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class ADVBatchTransformPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.rainbowing.ec.advbatchtransform";
        public const string PluginName = "EC_ADVBatchTransform";
        public const string PluginVersion = "1.1.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<KeyboardShortcut> PivotToggleKey;
        internal static ConfigEntry<KeyboardShortcut> UndoKey;
        internal static ConfigEntry<KeyboardShortcut> CycleNextKey;
        internal static ConfigEntry<KeyboardShortcut> CyclePrevKey;
        internal static ConfigEntry<float> MoveStep;
        internal static ConfigEntry<float> MoveStepLarge;
        internal static ConfigEntry<float> RotateStep;
        internal static ConfigEntry<float> RotateStepLarge;
        internal static ConfigEntry<int> UndoLimit;
        internal static ConfigEntry<PivotMode> Pivot;

        private string lastError;

        private void Awake()
        {
            Log = Logger;
            BindConfig();

            var harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(Hooks.CellViewHooks));
            harmony.PatchAll(typeof(Hooks.PartHooks));
            harmony.PatchAll(typeof(Hooks.PanelHooks));
            harmony.PatchAll(typeof(Hooks.LifecycleHooks));

            Log.LogInfo(PluginName + " " + PluginVersion + " 已启动");
        }

        private void BindConfig()
        {
            PivotToggleKey = Config.Bind("Keys", "PivotToggle", new KeyboardShortcut(KeyCode.G),
                "切换旋转/缩放中心：各自中心或整组中心");
            UndoKey = Config.Bind("Keys", "Undo", new KeyboardShortcut(KeyCode.Z, KeyCode.LeftControl),
                "撤销最近一次调整，含原生操作");
            CycleNextKey = Config.Bind("Keys", "CycleNext", new KeyboardShortcut(KeyCode.Tab),
                "选中列表里的下一个对象；多选时不生效");
            CyclePrevKey = Config.Bind("Keys", "CyclePrev", new KeyboardShortcut(KeyCode.Tab, KeyCode.LeftShift),
                "选中列表里的上一个对象；多选时不生效");

            MoveStep = Config.Bind("Step", "Move", 1f,
                new ConfigDescription("方向键每步移动距离", new AcceptableValueRange<float>(0.1f, 100f)));
            MoveStepLarge = Config.Bind("Step", "MoveWithShift", 10f,
                new ConfigDescription("按住 Shift 时方向键每步移动距离", new AcceptableValueRange<float>(0.1f, 500f)));
            RotateStep = Config.Bind("Step", "Rotate", 1f,
                new ConfigDescription("Alt+←/→ 每步旋转角度", new AcceptableValueRange<float>(0.1f, 90f)));
            RotateStepLarge = Config.Bind("Step", "RotateWithShift", 15f,
                new ConfigDescription("按住 Shift 时 Alt+←/→ 每步旋转角度", new AcceptableValueRange<float>(0.1f, 180f)));

            UndoLimit = Config.Bind("Undo", "Limit", 50,
                new ConfigDescription("撤销记录上限", new AcceptableValueRange<int>(1, 500)));
            Pivot = Config.Bind("Pivot", "Mode", PivotMode.Individual,
                "旋转/缩放中心。Individual：各自中心；Group：整组中心");
        }

        private void Update()
        {
            try
            {
                EditorDriver.Tick();
            }
            catch (Exception e)
            {
                // 同一异常每帧都会抛，只在内容变化时记一次
                if (e.Message == lastError) return;
                lastError = e.Message;
                Log.LogError(e);
            }
        }
    }
}
