using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EC_ADVCutSync
{
    [BepInProcess("EmotionCreators")]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class CutSyncPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.rainbowing.ec.advcutsync";
        public const string PluginName = "EC_ADVCutSync";
        public const string PluginVersion = "1.1.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<float> WindowX;
        internal static ConfigEntry<float> WindowY;
        internal static ConfigEntry<bool> FilterOpen;
        internal static ConfigEntry<int> FilterSelection;

        private void Awake()
        {
            Log = Logger;
            WindowX = Config.Bind("Window", "X", 0.02f,
                new ConfigDescription("窗口位置（可用宽度的比例）", new AcceptableValueRange<float>(0f, 1f)));
            WindowY = Config.Bind("Window", "Y", 0.05f,
                new ConfigDescription("窗口位置（可用高度的比例）", new AcceptableValueRange<float>(0f, 1f)));
            FilterOpen = Config.Bind("Window", "FilterOpen", false, "变化点筛选面板是否展开");
            FilterSelection = Config.Bind("Window", "FilterSelection", -2,
                "变化点筛选：-2 全部，-1 仅 BGM，>=0 仅对应角色索引");

            var harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(AdvPartButtonHook));
            harmony.PatchAll(typeof(WindowInputGuard));

            // 插件可能在目标场景已加载后才初始化，不能只等下一次 sceneLoaded
            enabled = IsHEditSceneLoaded();
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            Log.LogInfo(PluginName + " " + PluginVersion + " started");
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name == "HEditScene")
            {
                CutStateModel.InvalidateBgmCache();
                enabled = true;
            }
        }

        private void OnSceneUnloaded(Scene scene)
        {
            if (scene.name != "HEditScene")
                return;
            CutSyncWindow.Reset();
            WindowInputGuard.Reset();
            enabled = false;
        }

        private static bool IsHEditSceneLoaded()
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                if (SceneManager.GetSceneAt(i).name == "HEditScene")
                    return true;
            }
            return false;
        }

        private string lastGuiError;

        private void Update()
        {
            try
            {
                CutSyncWindow.Tick();
            }
            catch (Exception e)
            {
                Log.LogError(e);
            }
        }

        private void OnGUI()
        {
            if (!enabled)
                return;
            try
            {
                CutSyncWindow.DrawGui();
            }
            catch (Exception e)
            {
                // 同一异常每帧都抛，只在内容变化时记一次
                if (e.Message == lastGuiError)
                    return;
                lastGuiError = e.Message;
                Log.LogError(e);
            }
        }
    }
}
