using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using EC_SceneAudit.Export;
using EC_SceneAudit.Repair;
using HEdit;
using HarmonyLib;
using UnityEngine;

namespace EC_SceneAudit
{
    [BepInProcess("EmotionCreators")]
    [BepInPlugin(GUID, PluginName, Version)]
    public sealed class SceneAuditPlugin : BaseUnityPlugin
    {
        public const string GUID = "EC_SceneAudit";
        public const string PluginName = "EC Scene Audit";
        public const string Version = "1.2.0";

        private const string HEditSceneName = "HEditScene";

        internal static ConfigEntry<KeyboardShortcut> ExportHotkey { get; private set; }
        internal static ConfigEntry<KeyboardShortcut> RepairHotkey { get; private set; }

        private RepairWindow repairWindow;
        private Harmony harmony;
        private bool inputHooksReady;

        private void Awake()
        {
            ExportHotkey = Config.Bind(
                "Config",
                "Export Scene Evidence",
                new KeyboardShortcut(KeyCode.F8),
                "Export a read-only Scene review bundle with referenced external scripts.");

            RepairHotkey = Config.Bind(
                "Config",
                "Repair Preflight",
                new KeyboardShortcut(KeyCode.F8, KeyCode.LeftControl),
                "Show or hide the repair and node patch window in the HEdit node overview.");

            var planPath = Config.Bind("Repair", "Plan Path", "", "Path to the exported repair plan JSON.");
            Config.Bind("Repair", "Bundle Directory", "", "Review bundle directory.");
            var nodePatchPath = Config.Bind("Repair", "Node Patch Path", "", "Path to the exported ec-scene-node-patch JSON.");
            repairWindow = new RepairWindow(Logger, planPath, nodePatchPath);
            RepairWindow.Instance = repairWindow;
            try
            {
                harmony = new Harmony(GUID);
                harmony.PatchAll(typeof(RepairWindowHooks));
                inputHooksReady = true;
            }
            catch (Exception ex)
            {
                Logger.LogError("Repair window input hooks failed: " + ex);
            }

            // 游戏使用自定义 Scene 管理器，不能用 Unity active scene 决定插件组件是否运行。
            enabled = true;
            Logger.LogMessage(
                PluginName + " v" + Version + " loaded. Export hotkey: " + ExportHotkey.Value
                + ", repair window hotkey: " + RepairHotkey.Value);
        }

        private void Update()
        {
            // 鼠标键归属每帧只采样一次；Update 早于相机的 LateUpdate，保证本帧屏蔽判定及时。
            RepairWindowInputGuard.Sample();
            // Ctrl+F8 只控制修复窗口，避免同时触发普通 F8 导出。
            if (RepairHotkey != null && ShortcutDown(RepairHotkey.Value))
            {
                if (!inputHooksReady)
                {
                    Logger.LogWarning("Repair window input hooks unavailable; window disabled. See the startup error.");
                    return;
                }
                if (repairWindow.Visible)
                {
                    repairWindow.Close();
                    return;
                }
                if (!RepairEditorContext.IsNodeOverviewActive())
                {
                    Logger.LogWarning("Repair window hotkey detected, but HEdit node overview is not active.");
                    return;
                }

                Logger.LogMessage("Repair window hotkey detected; opening window.");
                repairWindow.Open();
                return;
            }

            if (RepairWindow.HasInputFocus || ExportHotkey == null || !ShortcutDown(ExportHotkey.Value))
                return;

            if (!IsHEditContextAvailable())
            {
                Logger.LogWarning("Export hotkey detected, but HEdit Scene is not ready.");
                return;
            }

            Logger.LogMessage("Export hotkey detected; starting Scene Audit export.");
            ExportSnapshotSafely();
        }

        private void OnGUI()
        {
            repairWindow?.OnGUI();
        }

        // 失焦时 Unity 不投递松开边沿，按键归属会残留并持续屏蔽节点输入。
        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus)
                repairWindow?.ReleaseInput();
        }

        private void OnDestroy()
        {
            repairWindow?.Close();
            repairWindow?.Dispose();
            RepairWindow.Instance = null;
            try
            {
                harmony?.UnpatchSelf();
            }
            catch (Exception ex)
            {
                Logger.LogError("Repair window input hooks unload failed: " + ex);
            }
        }

        private static bool ShortcutDown(KeyboardShortcut shortcut)
        {
            if (shortcut.MainKey == KeyCode.None || !Input.GetKeyDown(shortcut.MainKey))
                return false;
            foreach (var modifier in shortcut.Modifiers)
            {
                if (!Input.GetKey(modifier))
                    return false;
            }
            return true;
        }

        private static bool IsHEditContextAvailable()
        {
            try
            {
                if (Singleton<Manager.Scene>.IsInstance())
                {
                    var scene = Singleton<Manager.Scene>.Instance;
                    if (scene != null && scene.NowSceneNames != null)
                    {
                        foreach (var sceneName in scene.NowSceneNames)
                        {
                            if (sceneName == HEditSceneName)
                                return true;
                        }
                    }
                }

                return GameObject.FindObjectOfType<NodeSettingCanvas>() != null;
            }
            catch
            {
                return false;
            }
        }

        private void ExportSnapshotSafely()
        {
            try
            {
                var externalScriptRoot = Path.Combine(Paths.GameRootPath, "UserData");
                var builder = new SceneSnapshotBuilder(externalScriptRoot);
                var snapshot = builder.Build(
                    out var nodeCount,
                    out var partCount,
                    out var cutCount,
                    out var textBoxCount);
                var path = new ReviewBundleWriter().Write(snapshot, externalScriptRoot);
                Logger.LogMessage(
                    "Scene audit exported: " + path
                    + " (nodes=" + nodeCount
                    + ", parts=" + partCount
                    + ", cuts=" + cutCount
                    + ", textBoxes=" + textBoxCount
                    + ", warnings=" + snapshot.warnings.Length + ")");
            }
            catch (Exception ex)
            {
                Logger.LogError("Scene audit export failed: " + ex);
            }
        }
    }
}
