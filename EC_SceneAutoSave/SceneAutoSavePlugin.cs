using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using EC_SceneAutoSave.Core;
using EC_SceneAutoSave.Hooks;
using EC_SceneAutoSave.UI;
using HarmonyLib;
using UnityEngine;

namespace EC_SceneAutoSave
{
    [BepInProcess("EmotionCreators")]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency("EC_BatchTextReplace", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class SceneAutoSavePlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.rainbowing.ec.sceneautosave";
        public const string PluginName = "EC_SceneAutoSave";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;
        internal static AutoSaveController Controller;

        private AutoSaveSettings settings;
        private AutoSaveWindow window;
        private Harmony harmony;
        private string lastError;

        private void Awake()
        {
            Log = Logger;
            settings = new AutoSaveSettings(Config);
            // 原生场景列表只扫描 edit/scene 一层，这里的文件不会出现在读档窗口。
            var root = Path.Combine(Path.Combine(Paths.GameRootPath, "UserData"), Path.Combine("edit", "autosave"));
            Controller = new AutoSaveController(Log, settings, root);
            window = new AutoSaveWindow(Controller, settings);

            harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(SceneIoHooks));
            harmony.PatchAll(typeof(ChangeHooks));
            harmony.PatchAll(typeof(WindowInputGuard));
            try
            {
                ChangeHooks.PatchBatchTextReplace(harmony);
            }
            catch (Exception ex)
            {
                Log.LogWarning("EC_BatchTextReplace hook failed: " + ex.Message);
            }
            Log.LogInfo(PluginName + " " + PluginVersion + " 已启动，自动保存目录：" + root);
        }

        private void Update()
        {
            try
            {
                Controller.Tick(window.Visible);
                if (!EditorContext.IsInHEdit())
                {
                    if (window.Visible)
                        window.Close();
                    return;
                }
                if (settings.ToggleKey.Value != KeyCode.None && Input.GetKeyDown(settings.ToggleKey.Value))
                    window.Toggle();
                if (Controller.ConsumeOpenRequest() && !window.Visible)
                    window.Open();
            }
            catch (Exception ex)
            {
                // Update 每帧执行，同一错误只记一次。
                if (ex.Message != lastError)
                {
                    lastError = ex.Message;
                    Log.LogError("Autosave update failed: " + ex);
                }
            }
        }

        private void OnGUI()
        {
            window.OnGUI();
        }

        private void OnDestroy()
        {
            harmony?.UnpatchSelf();
            window?.Dispose();
        }
    }
}
