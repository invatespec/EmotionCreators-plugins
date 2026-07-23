using ADV;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using HEdit;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UnityEngine;

namespace EC_VariableMod_SaveLoad
{
    [BepInProcess("EmotionCreators")]
    [BepInPlugin("EC_VariableMod_SaveLoad", "EC_VariableMod_SaveLoad", "1.3.0")]
    [BepInDependency("EC_VariableMod", BepInDependency.DependencyFlags.HardDependency)]
    public sealed class EC_VariableMod_SaveLoadPlugin : BaseUnityPlugin
    {
        private static EC_VariableMod_SaveLoadPlugin _instance;
        private static SaveLoadEngine _engine;
        private static bool _hasPendingJump;
        private static string _pendingPartUid = "";
        private static int _pendingCutIndex = -1;

        private static AutoSaveManager _autoSave;
        private static ConfigEntry<bool> _cfgAutoSaveEnabled;
        private static ConfigEntry<int> _cfgAutoSaveInterval;
        private static ConfigEntry<float> _cfgAutoSaveMinSeconds;

        private static HPlayAdvanceManager _advance;
        private static ConfigEntry<KeyCode> _cfgAdvanceKey;
        private static ConfigEntry<KeyCode> _cfgAutoToggleKey;
        private static ConfigEntry<bool> _cfgAutoAdvanceEnabled;
        private static ConfigEntry<float> _cfgAutoAdvanceInterval;

        private static string _currentSaveFolder = "";
        private static string _lastSceneTitle = "";

        private void Start()
        {
            _instance = this;
            _engine = new SaveLoadEngine(Logger);

            _cfgAutoSaveEnabled = Config.Bind("AutoSave", "Enabled", false,
                new ConfigDescription("是否开启自动存档"));
            _cfgAutoSaveInterval = Config.Bind("AutoSave", "IntervalCuts", 10,
                new ConfigDescription("每隔多少个 CUT 自动存档一次（最小 5）", new AcceptableValueRange<int>(5, 1000)));
            _cfgAutoSaveMinSeconds = Config.Bind("AutoSave", "MinSaveIntervalSeconds", 3f,
                new ConfigDescription("两次自动存档写盘之间的最短真实秒数。快进时多次触发会被合并，只在间隔过后写一次，避免频繁写盘卡顿",
                    new AcceptableValueRange<float>(0.5f, 60f)));

            _autoSave = new AutoSaveManager(_engine, Logger,
                () => _cfgAutoSaveEnabled.Value, () => _cfgAutoSaveInterval.Value, () => _cfgAutoSaveMinSeconds.Value,
                () => _currentSaveFolder);

            _cfgAdvanceKey = Config.Bind("Advance", "Key", KeyCode.Return,
                "HPlay 中自定义前进(切到下一个 CUT/PART)的按键,默认 Enter");
            _cfgAutoToggleKey = Config.Bind("Advance", "AutoToggleKey", KeyCode.Backspace,
                "切换自动前进开启状态的快捷键。建议避开 Ctrl(左/右 Ctrl 是游戏自带的快速前进键)");
            _cfgAutoAdvanceEnabled = Config.Bind("Advance", "AutoEnabled", false,
                "是否开启自动前进");
            _cfgAutoAdvanceInterval = Config.Bind("Advance", "AutoIntervalSeconds", 2.5f,
                new ConfigDescription("自动前进的时间间隔(秒),最小 0.1",
                    new AcceptableValueRange<float>(0.1f, 60f)));

            _advance = new HPlayAdvanceManager(Logger,
                () => _cfgAdvanceKey.Value,
                () => _cfgAutoToggleKey.Value,
                () => _cfgAutoAdvanceEnabled.Value,
                () => _cfgAutoAdvanceInterval.Value,
                v => _cfgAutoAdvanceEnabled.Value = v);

            Harmony.CreateAndPatchAll(typeof(Hooks), null);
            Logger.LogInfo("EC_VariableMod_SaveLoad - start");
        }

        private static string GetSceneTitle()
        {
            try
            {
                var inst = Singleton<HEditData>.Instance;
                return inst?.info?.title ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static IEnumerable<XElement> ExtractScriptRoots(string msg)
        {
            if (string.IsNullOrEmpty(msg))
                yield break;

            if (!msg.Contains("<script>"))
                yield break;

            foreach (Match m in Regex.Matches(msg, "<script>([\\s\\S]*?)</script>", RegexOptions.IgnoreCase))
            {
                if (!m.Success)
                    continue;

                string xml = m.Value;
                XElement root;
                try
                {
                    root = XElement.Parse(xml);
                }
                catch
                {
                    continue;
                }

                if (root.Name.LocalName != "script")
                    continue;

                yield return root;
            }
        }

        private static string GetAttr(XElement elem, string name)
        {
            var a = elem.Attribute(name);
            if (a != null)
                return a.Value;

            if (name == "value")
            {
                var b = elem.Attribute("file");
                if (b != null)
                    return b.Value;
            }

            return "";
        }

        private static bool ShouldJumpAfterLoad(XElement elem)
        {
            string value = GetAttr(elem, "jump");
            if (string.IsNullOrWhiteSpace(value))
                return false;

            switch (value.Trim().ToLowerInvariant())
            {
                case "1":
                case "true":
                case "yes":
                case "on":
                case "jump":
                    return true;
                default:
                    return false;
            }
        }

        private static int GetCurrentCutIndexFromInternal(int ___cutIndex)
        {
            return Math.Max(0, ___cutIndex - 1);
        }

        private static void QueuePendingJump(string partUid, int cutIndex)
        {
            _pendingPartUid = partUid ?? "";
            _pendingCutIndex = cutIndex;
            _hasPendingJump = true;
            _instance?.Logger.LogDebug($"[EC_VariableMod_SaveLoad] 已登记待跳转: PartUID={_pendingPartUid}, CutIndex={_pendingCutIndex}");
        }

        private static void ClearPendingJump()
        {
            _hasPendingJump = false;
            _pendingPartUid = "";
            _pendingCutIndex = -1;
        }

        private static void RunSaveLoadOnCut(HEdit.ADVPart.Cut cut, HEdit.ADVPart part, int cutIndexInternal)
        {
            if (_engine == null || cut?.speechBubbles == null)
                return;

            string sceneTitle = GetSceneTitle();
            string partUid = part?.uuId ?? "";
            string partName = part?.name ?? "";
            int cutIndex = GetCurrentCutIndexFromInternal(cutIndexInternal);

            foreach (var bubble in cut.speechBubbles)
            {
                if (bubble?.textLayouts == null)
                    continue;

                foreach (var tl in bubble.textLayouts)
                {
                    string msg = tl.msg;
                    if (string.IsNullOrEmpty(msg))
                        continue;

                    if (!msg.Contains("<script>") || (!msg.Contains("<save") && !msg.Contains("<load") && !msg.Contains("<scan-saves") && !msg.Contains("<save-folder")))
                        continue;

                    foreach (var script in ExtractScriptRoots(msg))
                    {
                        // 只遍历 <script> 的直接子元素。save/load 等指令必须写在
                        // <script> 下一层，不能嵌套在 <event-select> / <if> / <loop>
                        // 等延迟执行的标签内部——SaveLoad 在 CUT 加载时同步处理，
                        // 无法感知 VariableMod 的延迟执行语义。
                        foreach (var node in script.Elements())
                        {
                            string ln = node.Name.LocalName;
                            string saveName = GetAttr(node, "value");
                            if (ln == "save")
                            {
                                _engine.Save(sceneTitle, partUid, partName, cutIndex, saveName, _currentSaveFolder);
                            }
                            else if (ln == "load")
                            {
                                bool shouldJump = ShouldJumpAfterLoad(node);
                                _instance?.Logger.LogDebug($"[EC_VariableMod_SaveLoad] 尝试读档: {saveName}");
                                if (_engine.Load(sceneTitle, saveName, out var loaded, _currentSaveFolder))
                                {
                                    // 跨场景保护：来自其他场景的存档禁止跳转
                                    bool isCrossScene = !string.IsNullOrEmpty(loaded.adv?.sceneTitle)
                                                        && loaded.adv.sceneTitle != sceneTitle;
                                    if (isCrossScene && shouldJump)
                                    {
                                        shouldJump = false;
                                        _instance?.Logger.LogMessage("[EC_VariableMod_SaveLoad] 跨场景存档不支持跳转，仅恢复变量。");
                                    }

                                    if (!shouldJump)
                                        continue;

                                    if (_engine.TryPrepareJump(loaded, partUid, sceneTitle, out var targetPartUid, out var targetCutIndex))
                                    {
                                        QueuePendingJump(targetPartUid, targetCutIndex);
                                    }
                                    else
                                    {
                                        _instance?.Logger.LogWarning("[EC_VariableMod_SaveLoad] 读档成功，但未能生成有效跳转目标。");
                                    }
                                }
                                else
                                {
                                    _instance?.Logger.LogWarning($"[EC_VariableMod_SaveLoad] 读档失败: {saveName}");
                                }
                            }
                            else if (ln == "scan-saves")
                            {
                                _engine.ScanAndInjectSaveDates(sceneTitle, _currentSaveFolder);
                            }
                            else if (ln == "save-folder")
                            {
                                string folder = GetAttr(node, "value");
                                if (!string.IsNullOrEmpty(folder))
                                {
                                    _currentSaveFolder = folder;
                                    _instance?.Logger.LogMessage($"[EC_VariableMod_SaveLoad] 存档目录已设置为: {folder}");
                                }
                                else
                                {
                                    _currentSaveFolder = "";
                                    _instance?.Logger.LogMessage("[EC_VariableMod_SaveLoad] 存档目录已重置为默认（场景标题）");
                                }
                            }
                        }
                    }
                }
            }
        }

        private static class Hooks
        {
            [HarmonyPostfix]
            [HarmonyAfter("EC_VariableMod")]
            [HarmonyPriority(Priority.Last)]
            [HarmonyPatch(typeof(ADVPlay), "LoadCut", new Type[] { typeof(HEdit.ADVPart.Cut) })]
            public static void LoadCut_Postfix(HEdit.ADVPart.Cut _cut, ref HEdit.ADVPart ___part, ref int ___cutIndex)
            {
                // 场景切换时重置 save-folder 和自动存档状态，
                // 避免上一个剧本的 save-folder 污染新剧本。
                string sceneTitle = GetSceneTitle();
                if (!string.IsNullOrEmpty(_lastSceneTitle) && _lastSceneTitle != sceneTitle)
                {
                    _currentSaveFolder = "";
                    _autoSave?.Reset();
                    _advance?.Reset();
                    _instance?.Logger.LogMessage($"[EC_VariableMod_SaveLoad] 检测到场景切换（{_lastSceneTitle} → {sceneTitle}），已重置存档目录和自动存档状态。");
                }
                _lastSceneTitle = sceneTitle;

                RunSaveLoadOnCut(_cut, ___part, ___cutIndex);

                // 自动存档触发（仅记账，绝不在此写盘——快进时此路径高频执行）
                if (_autoSave != null)
                {
                    _autoSave.OnCutLoaded(
                        sceneTitle,
                        ___part?.uuId ?? "",
                        ___part?.name ?? "",
                        GetCurrentCutIndexFromInternal(___cutIndex));
                }

                _advance?.OnCutLoaded(_cut);
            }

            [HarmonyPrefix]
            [HarmonyBefore("EC_VariableMod")]
            [HarmonyPriority(Priority.First)]
            [HarmonyPatch(typeof(HPlay.HPlayScene), "Update")]
            public static void Update_Prefix()
            {
                // 先提交待跳转（若有），跳转优先于自动存档
                if (_hasPendingJump && _engine != null)
                {
                    _instance?.Logger.LogDebug($"[EC_VariableMod_SaveLoad] 在 Update 中提交待跳转: PartUID={_pendingPartUid}, CutIndex={_pendingCutIndex}");
                    if (_engine.CommitJump(_pendingPartUid, _pendingCutIndex))
                    {
                        ClearPendingJump();
                    }
                    else
                    {
                        _instance?.Logger.LogWarning("[EC_VariableMod_SaveLoad] 在 Update 中提交待跳转失败，将在下次 Update 重试。");
                    }
                }

                // 节流写盘自动存档（每帧检查，距上次写盘不足阈值则跳过）
                _autoSave?.Tick();

                _advance?.Tick();
            }
        }
    }
}
