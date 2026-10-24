using BepInEx.Logging;
using System;
using UnityEngine;

namespace EC_BackVarSync
{
    /// <summary>
    /// 自动存档状态机（沿用 EC_VariableMod_SaveLoad 已实测的触发/写盘解耦设计）。
    ///
    /// OnCutLoaded 由 LoadCut Postfix 调用，快进时高频触发——只做廉价记账，
    /// 绝不快照、绝不写盘。Tick 由 HPlayScene.Update 每帧调用，做真实时间节流：
    /// 距上次写盘不足 MinSaveIntervalSeconds 秒就合并本次触发，窗口一过补写最终状态。
    /// </summary>
    internal sealed class AutoSaveManager
    {
        /// <summary>间隔下限（防御性，配置层也用 AcceptableValueRange 限制）。</summary>
        private const int MinIntervalFloor = 5;

        private readonly VarStore _store;
        private readonly ManualLogSource _log;
        private readonly Func<bool> _enabled;
        private readonly Func<int> _intervalCuts;
        private readonly Func<float> _minSaveSeconds;

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
            VarStore store,
            ManualLogSource log,
            Func<bool> enabled,
            Func<int> intervalCuts,
            Func<float> minSaveSeconds)
        {
            _store = store;
            _log = log;
            _enabled = enabled;
            _intervalCuts = intervalCuts;
            _minSaveSeconds = minSaveSeconds;
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

            // partUid 为空属退化状态：不武装。既避免写出无位置存档，
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

            bool ok = _store.SaveAuto(_ctxSceneTitle, _ctxPartUid, _ctxPartName, _ctxCutIndex);

            // 成功失败都推进时间戳并清脏，避免写盘失败时每帧重试刷屏。
            _lastWriteRealtime = now;
            _dirty = false;

            if (!ok)
                _log.LogWarning("自动存档写入失败。");
        }

        public void Reset()
        {
            _dirty = false;
            _cutsSinceTrigger = 0;
            _lastSeenPartUid = null;
        }
    }
}
