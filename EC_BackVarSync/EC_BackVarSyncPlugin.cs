using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EC_BackVarSync
{
    [BepInProcess("EmotionCreators")]
    [BepInPlugin(GUID, PluginName, Version)]
    [BepInDependency("EC_VariableMod", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("EC_back", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class EC_BackVarSyncPlugin : BaseUnityPlugin
    {
        public const string GUID = "EC_BackVarSync";
        public const string PluginName = "EC_BackVarSync";
        public const string Version = "1.0.0";

        private static ManualLogSource _log;
        private static VarStore _store;
        private static AutoSaveManager _autoSave;
        private static HPlayAdvanceManager _advance;
        private static ChapterPanel _panel;
        private static ECBackBridge _bridge;
        private static string _lastSceneTitle = "";

        private void Start()
        {
            _log = Logger;
            _store = new VarStore(Logger);

            var cfgPanelKey = Config.Bind("Panel", "ToggleKey", KeyCode.F9,
                "打开/关闭剧本/章节存档面板的按键");
            var cfgFollow = Config.Bind("Panel", "FollowECBack", true,
                "面板显隐是否联动 EC_back：EC_back 展开时面板显示、收起时隐藏。面板位置不受影响（默认在 EC_back 下方，可拖动）");
            var cfgDock = Config.Bind("Panel", "DockUnderECBack", true,
                "面板出现时是否自动放到 EC_back 下方。拖动面板后自动关闭并永久记住手动位置（跨游戏重启）；要恢复停靠，在 F1 插件管理里把本项重置/改回 true");
            var cfgX = Config.Bind("Panel", "WindowX", 0.03f,
                new ConfigDescription("面板窗口水平位置（0~1，相对屏幕可用区域的百分比，分辨率变化自动适应）", new AcceptableValueRange<float>(0f, 1f)));
            var cfgY = Config.Bind("Panel", "WindowY", 0.18f,
                new ConfigDescription("面板窗口垂直位置（0~1，相对屏幕可用区域的百分比，分辨率变化自动适应）", new AcceptableValueRange<float>(0f, 1f)));

            var cfgLink = Config.Bind("ECBack", "EnableLink", true,
                "是否启用 EC_back 书签联动（存/读书签时同步保存/恢复变量）");

            var cfgAutoEnabled = Config.Bind("AutoSave", "Enabled", false,
                "是否开启自动存档（按场景区分，各剧本场景独立一份）");
            var cfgAutoInterval = Config.Bind("AutoSave", "IntervalCuts", 10,
                new ConfigDescription("每隔多少个 CUT 自动存档一次（最小 5）", new AcceptableValueRange<int>(5, 1000)));
            var cfgAutoMinSeconds = Config.Bind("AutoSave", "MinSaveIntervalSeconds", 3f,
                new ConfigDescription("两次自动存档写盘之间的最短真实秒数。快进时多次触发会被合并，只在间隔过后写一次",
                    new AcceptableValueRange<float>(0.5f, 60f)));

            var cfgAdvanceKey = Config.Bind("Advance", "Key", KeyCode.Return,
                "HPlay 中自定义前进(切到下一个 CUT/PART)的按键");
            var cfgAutoToggleKey = Config.Bind("Advance", "AutoToggleKey", KeyCode.Backspace,
                "切换自动前进开启状态的快捷键");
            var cfgAutoAdvanceEnabled = Config.Bind("Advance", "AutoEnabled", false,
                "是否开启自动前进");
            var cfgAutoAdvanceInterval = Config.Bind("Advance", "AutoIntervalSeconds", 2.5f,
                new ConfigDescription("自动前进的时间间隔(秒)", new AcceptableValueRange<float>(0.1f, 60f)));

            _bridge = new ECBackBridge(Logger, _store);
            _panel = new ChapterPanel(_store, Logger, () => cfgPanelKey.Value,
                () =>
                {
                    if (!cfgFollow.Value || !_bridge.TryGetFollowAnchor(out var rect))
                        return null;
                    return rect;
                },
                cfgX, cfgY, cfgDock);
            _autoSave = new AutoSaveManager(_store, Logger,
                () => cfgAutoEnabled.Value, () => cfgAutoInterval.Value, () => cfgAutoMinSeconds.Value);
            _advance = new HPlayAdvanceManager(Logger,
                () => cfgAdvanceKey.Value,
                () => cfgAutoToggleKey.Value,
                () => cfgAutoAdvanceEnabled.Value,
                () => cfgAutoAdvanceInterval.Value,
                v => cfgAutoAdvanceEnabled.Value = v);

            var harmony = new Harmony(GUID);
            harmony.PatchAll(typeof(Hooks));
            harmony.PatchAll(typeof(IMGUIInputBlocker));

            if (cfgLink.Value)
                _bridge.TryLink(harmony);
            else
                Logger.LogInfo("EC_back 联动已在配置中关闭");

            Logger.LogInfo($"EC_BackVarSync v{Version} started");
        }

        private void OnGUI()
        {
            if (_panel == null || !IsPlayScene() || !_panel.OnGUI())
                IMGUIInputBlocker.ClearArea();
        }

        private static bool IsPlayScene()
        {
            string name = SceneManager.GetActiveScene().name;
            return name != "Title" && name != "HEditScene" && !name.Contains("HPlaySceneSelect");
        }

        private static class Hooks
        {
            [HarmonyPostfix]
            [HarmonyAfter("EC_VariableMod")]
            [HarmonyPriority(Priority.Last)]
            [HarmonyPatch(typeof(ADV.ADVPlay), "LoadCut", new Type[] { typeof(HEdit.ADVPart.Cut) })]
            public static void LoadCut_Postfix(HEdit.ADVPart.Cut _cut, ref HEdit.ADVPart ___part, ref int ___cutIndex)
            {
                string sceneTitle = VarStore.GetSceneTitle();

                // 场景切换时重置自动存档/前进状态，避免上一个剧本的残留
                if (!string.IsNullOrEmpty(_lastSceneTitle) && _lastSceneTitle != sceneTitle)
                {
                    _autoSave?.Reset();
                    _advance?.Reset();
                    _log?.LogMessage($"检测到场景切换（{_lastSceneTitle} → {sceneTitle}），已重置自动存档状态。");
                }
                _lastSceneTitle = sceneTitle;

                _autoSave?.OnCutLoaded(sceneTitle, ___part?.uuId ?? "", ___part?.name ?? "",
                    Math.Max(0, ___cutIndex - 1));
                _advance?.OnCutLoaded(_cut);
            }

            [HarmonyPrefix]
            [HarmonyBefore("EC_VariableMod")]
            [HarmonyPriority(Priority.First)]
            [HarmonyPatch(typeof(HPlay.HPlayScene), "Update")]
            public static void Update_Prefix()
            {
                _panel?.TickKey();
                _autoSave?.Tick();

                // 面板输入框打字时暂停前进键，避免按回车确认输入被游戏吃成前进
                if (!ChapterPanel.TextEditing)
                    _advance?.Tick();
            }
        }
    }
}
