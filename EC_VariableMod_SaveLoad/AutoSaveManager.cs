using BepInEx.Logging;
using System;
using UnityEngine;

namespace EC_VariableMod_SaveLoad
{
    /// <summary>
    /// 自动存档状态机。
    ///
    /// 设计要点（解决快进时高频触发导致的写盘性能问题）：
    /// 把「触发」与「写盘」解耦——
    ///   - <see cref="OnCutLoaded"/> 由 LoadCut_Postfix 调用，每加载一个 CUT 触发一次，
    ///     快进时会高频触发。此方法只做廉价记账（计数、检测 PART 切换、缓存上下文、置脏标记），
    ///     绝不快照、绝不写盘。
    ///   - <see cref="Tick"/> 由 HPlayScene.Update 每帧调用，做真实时间节流：
    ///     距上次写盘不足 MinSaveIntervalSeconds 秒就跳过。快进时大量触发被合并为
    ///     「每窗口至多写一次」；因为 Update 每帧都跑，快进结束后窗口一过即补写最终状态。
    ///
    /// 一致性：变量快照与记录的位置都在 Tick() 写盘那一刻一起获取；缓存的上下文始终是
    /// 「最近加载的 CUT」，而 CUT 之间变量不变（脚本在 LoadCut 内执行，本插件 Postfix 在
    /// VariableMod 之后），故写盘时的实时变量与缓存上下文自洽。
    /// </summary>
    internal sealed class AutoSaveManager
    {
        /// <summary>默认自动存档文件名。</summary>
        private const string BaseAutoSaveName = "AutoSave";

        /// <summary>间隔下限（防御性，配置层也用 AcceptableValueRange 限制）。</summary>
        private const int MinIntervalFloor = 5;

        private readonly SaveLoadEngine _engine;
        private readonly ManualLogSource _log;
        private readonly Func<bool> _enabled;
        private readonly Func<int> _intervalCuts;
        private readonly Func<float> _minSaveSeconds;
        private readonly Func<string> _getSaveFolder;

        private string _lastSeenPartUid;   // null 直到首个有效 CUT
        private int _cutsSinceTrigger;
        private bool _dirty;               // 有一次待写存档
        private float _lastWriteRealtime = float.NegativeInfinity;

        // 缓存「最近加载的 CUT」上下文，作为写盘时的目标位置
        private string _ctxSceneTitle = "";
        private string _ctxPartUid = "";
        private string _ctxPartName = "";
        private int _ctxCutIndex;

        public AutoSaveManager(
            SaveLoadEngine engine,
            ManualLogSource log,
            Func<bool> enabled,
            Func<int> intervalCuts,
            Func<float> minSaveSeconds,
            Func<string> getSaveFolder)
        {
            _engine = engine;
            _log = log;
            _enabled = enabled;
            _intervalCuts = intervalCuts;
            _minSaveSeconds = minSaveSeconds;
            _getSaveFolder = getSaveFolder;
        }

        /// <summary>
        /// 由 LoadCut_Postfix 调用（快进时高频）。只记账，绝不写盘。
        /// </summary>
        public void OnCutLoaded(string sceneTitle, string partUid, string partName, int cutIndex)
        {
            if (!_enabled())
            {
                Reset();
                return;
            }

            // partUid 为空属退化状态：不武装。既避免写出无法跳转的存档，
            // 也避免「真 UID → 空」被误判为 PART 切换。
            if (string.IsNullOrEmpty(partUid))
                return;

            _ctxSceneTitle = sceneTitle ?? "";
            _ctxPartUid = partUid;
            _ctxPartName = partName ?? "";
            _ctxCutIndex = cutIndex;

            bool partChanged = _lastSeenPartUid != null && _lastSeenPartUid != partUid;
            _lastSeenPartUid = partUid;

            _cutsSinceTrigger++;

            int interval = Math.Max(MinIntervalFloor, _intervalCuts());
            if (partChanged)
            {
                // PART 切换时只重置计数，不触发存档。
                // 避免进入存读档菜单等功能 Part 时误覆盖自动存档。
                _cutsSinceTrigger = 0;
            }
            else if (_cutsSinceTrigger >= interval)
            {
                _cutsSinceTrigger = 0;
                _dirty = true;
            }
        }

        /// <summary>
        /// 由 HPlayScene.Update 每帧调用。节流写盘：合并快进期间的多次触发。
        /// </summary>
        public void Tick()
        {
            if (!_enabled())
            {
                // 关闭时清掉待写，杜绝再次开启时的陈旧写入。
                Reset();
                return;
            }

            if (!_dirty)
                return;

            float now = Time.realtimeSinceStartup;
            if (now - _lastWriteRealtime < _minSaveSeconds())
                return; // 距上次写盘太近：合并本次触发，留待下一帧重试

            string saveFolder = _getSaveFolder?.Invoke() ?? "";
            string autoSaveName = GetAutoSaveName(saveFolder);

            bool ok = _engine.Save(_ctxSceneTitle, _ctxPartUid, _ctxPartName, _ctxCutIndex,
                                   autoSaveName, saveFolder);

            // 成功失败都推进时间戳并清脏，避免写盘失败时每帧重试刷屏。
            _lastWriteRealtime = now;
            _dirty = false;

            if (ok)
                _log.LogMessage("📁 已自动存档");
            else
                _log.LogWarning("😇 自动存档写入失败。");
        }

        /// <summary>
        /// 计算自动存档文件名。
        /// 默认（无共享目录）用 "AutoSave"；共享目录时用 "AutoSave-{场景标题}" 防止各章覆盖。
        /// </summary>
        private string GetAutoSaveName(string saveFolder)
        {
            return string.IsNullOrEmpty(saveFolder)
                ? BaseAutoSaveName
                : $"{BaseAutoSaveName}-{_ctxSceneTitle}";
        }

        public void Reset()
        {
            _dirty = false;
            _cutsSinceTrigger = 0;
            _lastSeenPartUid = null;
        }
    }
}
