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
        public const string Version = "1.1.0";

        private const string HEditSceneName = "HEditScene";

        internal static ConfigEntry<KeyboardShortcut> ExportHotkey { get; private set; }
        internal static ConfigEntry<KeyboardShortcut> PreflightHotkey { get; private set; }

        private RepairPreflightWindow preflightWindow;
        private Harmony harmony;
        private bool inputHooksReady;

        private void Awake()
        {
            ExportHotkey = Config.Bind(
                "Config",
                "Export Scene Evidence",
                new KeyboardShortcut(KeyCode.F8),
                "Export a read-only Scene review bundle with referenced external scripts.");

            PreflightHotkey = Config.Bind(
                "Config",
                "Repair Preflight",
                new KeyboardShortcut(KeyCode.F8, KeyCode.LeftControl),
                "Open the read-only repair preflight window in the HEdit node overview.");

            var planPath = Config.Bind("Repair", "Plan Path", "", "Path to the exported repair plan JSON.");
            var bundlePath = Config.Bind("Repair", "Bundle Directory", "", "Directory of the original F8 review bundle.");
            var nodePatchPath = Config.Bind("Repair", "Node Patch Path", "", "Path to the exported ec-scene-node-patch JSON.");
            preflightWindow = new RepairPreflightWindow(Logger, planPath, bundlePath, nodePatchPath);
            RepairPreflightWindow.Instance = preflightWindow;
            try
            {
                harmony = new Harmony(GUID);
                harmony.PatchAll(typeof(RepairPreflightHooks));
                inputHooksReady = true;
            }
            catch (Exception ex)
            {
                Logger.LogError("Repair preflight input hooks failed: " + ex);
            }

            // 游戏使用自定义 Scene 管理器，不能用 Unity active scene 决定插件组件是否运行。
            enabled = true;
            Logger.LogMessage(
                PluginName + " v" + Version + " loaded. Export hotkey: " + ExportHotkey.Value
                + ", repair preflight hotkey: " + PreflightHotkey.Value);
        }

        private void Update()
        {
            // 鼠标键归属每帧只采样一次；Update 早于相机的 LateUpdate，保证本帧屏蔽判定及时。
            RepairWindowInputGuard.Sample();
            // 有未清理会话（内存快照）时保留窗口：离开总览只禁止新的写回，恢复入口不能消失。
            if (preflightWindow != null && preflightWindow.Visible && !preflightWindow.KeepsWindowOpen
                && !RepairPreflightContext.IsNodeOverviewActive())
                preflightWindow.Close();
            // 先处理预检组合键并 return，避免 Ctrl+F8 同时触发普通 F8 导出。
            if (PreflightHotkey != null && ShortcutDown(PreflightHotkey.Value))
            {
                if (!inputHooksReady)
                {
                    Logger.LogWarning("Repair preflight input hooks unavailable; window disabled. See the startup error.");
                    return;
                }
                if (!RepairPreflightContext.IsNodeOverviewActive())
                {
                    Logger.LogWarning("Repair preflight hotkey detected, but HEdit node overview is not active.");
                    return;
                }

                Logger.LogMessage("Repair preflight hotkey detected; opening window.");
                preflightWindow.Open();
                return;
            }

            if (RepairPreflightWindow.HasInputFocus || ExportHotkey == null || !ShortcutDown(ExportHotkey.Value))
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
            preflightWindow?.OnGUI();
        }

        // 失焦时 Unity 不投递松开边沿，按键归属会残留并持续屏蔽节点输入。
        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus)
                RepairWindowInputGuard.Reset();
        }

        private void OnDestroy()
        {
            preflightWindow?.Close();
            preflightWindow?.Dispose();
            RepairPreflightWindow.Instance = null;
            try
            {
                harmony?.UnpatchSelf();
            }
            catch (Exception ex)
            {
                Logger.LogError("Repair preflight input hooks unload failed: " + ex);
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
