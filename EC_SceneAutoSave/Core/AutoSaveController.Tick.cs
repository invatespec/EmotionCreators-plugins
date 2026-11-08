using System;
using System.Diagnostics;
using System.IO;
using EC_SceneAutoSave.Storage;
using UnityEngine;

namespace EC_SceneAutoSave.Core
{
    // 每帧调度：输入空闲判定、自动保存、面板请求的立即保存与应用会话。
    internal sealed partial class AutoSaveController
    {
        private const float WaitHintInterval = 0.5f;
        private const float ApplyHintInterval = 2f;

        private float lastInputTime;
        private float lastAttemptTime = float.NegativeInfinity;
        private bool inputSinceCapture;
        private Vector3 lastMouse;
        private string pendingApplyDir;
        private bool pendingSave;
        private float nextWaitHintAt;
        private float nextApplyHintAt;

        public string WaitHint { get; private set; }
        public string ApplyHint { get; private set; }

        public void Tick(bool windowVisible)
        {
            if (pendingRebasePath != null && Time.frameCount >= pendingRebaseFrame)
                Rebase();
            if (Mode != AutoSaveMode.Active)
            {
                pendingApplyDir = null;
                pendingSave = false;
                return;
            }
            var data = EditorContext.Data;
            var global = EditorContext.Global;
            if (data == null || global == null || !ReferenceEquals(global, boundGlobal))
            {
                Deactivate("已离开场景编辑");
                return;
            }

            TrackInput();
            tracker.Poll(global);
            if (pendingApplyDir != null)
            {
                var dir = pendingApplyDir;
                pendingApplyDir = null;
                RunApply(dir);
                return;
            }
            if (pendingSave)
            {
                pendingSave = false;
                SaveNow();
                return;
            }
            if (windowVisible)
                RefreshHints();
            if (ShouldAutoSave())
                Capture();
        }

        public void RequestSave() => pendingSave = true;

        public void RequestApply(string dir) => pendingApplyDir = dir;

        private bool ShouldAutoSave()
        {
            if (!settings.Enabled.Value || !inputSinceCapture)
                return false;
            var now = Time.realtimeSinceStartup;
            if (now - lastInputTime < settings.IdleSeconds.Value)
                return false;
            if (now - lastAttemptTime < settings.MinIntervalSeconds.Value)
                return false;
            return EditorContext.BlockReasonForAutoSave() == null;
        }

        private CaptureOutcome Capture()
        {
            var data = EditorContext.Data;
            var global = EditorContext.Global;
            if (State == null || data == null || global == null || global.nodeControl == null)
                return CaptureOutcome.Failed;
            lastAttemptTime = Time.realtimeSinceStartup;
            var outcome = capture.Run(State, tracker, data, global, settings.SessionsPerScene.Value);
            if (outcome != CaptureOutcome.Failed)
                inputSinceCapture = false;
            if (outcome == CaptureOutcome.Committed)
                RefreshRecoverable();
            return outcome;
        }

        private void SaveNow()
        {
            var reason = EditorContext.BlockReasonForAutoSave();
            if (reason != null)
            {
                Notice = "现在不能保存：" + reason;
                return;
            }
            switch (Capture())
            {
                case CaptureOutcome.Committed:
                    Notice = "已保存";
                    break;
                case CaptureOutcome.NoChange:
                    Notice = "没有新改动";
                    break;
                default:
                    Notice = State?.LastError ?? "保存失败";
                    break;
            }
        }

        // 先完整读取并校验会话，失败时场景不动；开始改场景后失败只能停用，提示重新读档。
        private void RunApply(string dir)
        {
            var data = EditorContext.Data;
            var global = EditorContext.Global;
            var session = store.TryReadSession(dir);
            var reason = session == null ? "会话文件已损坏或被删除" : ApplyBlockReason(session);
            if (reason != null)
            {
                Notice = "不能应用：" + reason;
                return;
            }

            PreparedSession prepared;
            try
            {
                prepared = applier.Prepare(session, data);
            }
            catch (Exception ex)
            {
                log.LogError("Autosave session read failed: " + ex);
                Notice = "会话数据无法读取：" + ex.Message;
                return;
            }

            var watch = Stopwatch.StartNew();
            Applying = true;
            try
            {
                applier.Execute(prepared, data, global);
                SessionApplier.Remember(State, tracker, prepared, data, global);
            }
            catch (Exception ex)
            {
                log.LogError("Autosave apply failed: " + ex);
                Deactivate("应用失败，自动保存暂停");
                Notice = "应用失败：请重新读档后再试。" + ex.Message;
                return;
            }
            finally
            {
                Applying = false;
            }
            ResetTimers();
            RefreshRecoverable();
            Notice = "已应用会话 " + Path.GetFileName(dir);
            log.LogInfo($"Autosave applied {dir}: {prepared.NewParts.Count} part(s), {prepared.Charas.Count} chara(s), {watch.ElapsedMilliseconds} ms");
        }

        private string ApplyBlockReason(SessionInfo session)
        {
            var reason = BaseApplyBlockReason();
            if (reason != null)
                return reason;
            var manifest = session.Manifest;
            if (!BaseFingerprint.SamePath(manifest.BasePath, State.Base.Path))
                return "会话属于另一个存档";
            var disk = BaseFingerprint.TryRead(State.Base.Path);
            if (disk == null || !disk.Matches(manifest))
                return "存档文件在会话之后被改写过";
            if (!disk.SameFile(State.Base))
                return "存档文件在读档后被改写过，请重新读档";
            if (manifest.GameVersion != SceneCodec.FormatVersion(EditorContext.Data).ToString())
                return "游戏场景版本与会话不一致";
            return null;
        }

        // 与具体会话无关的条件，面板据此决定"应用"按钮是否可用。
        private string BaseApplyBlockReason()
        {
            if (Mode != AutoSaveMode.Active || State == null)
                return "当前没有基准存档";
            var reason = EditorContext.BlockReasonForApply();
            if (reason != null)
                return reason;
            reason = SessionApplier.EditedReason(State, tracker, EditorContext.Data, EditorContext.Global);
            return reason == null ? null : reason + "，请重新读档后再应用";
        }

        // 应用判定要序列化地图与打开过的 Part，只在面板可见且有可恢复会话时低频计算。
        private void RefreshHints()
        {
            var now = Time.realtimeSinceStartup;
            if (now >= nextWaitHintAt)
            {
                nextWaitHintAt = now + WaitHintInterval;
                WaitHint = ComputeWaitHint(now);
            }
            if (Recoverable.Count == 0)
            {
                ApplyHint = null;
                return;
            }
            if (now >= nextApplyHintAt)
            {
                nextApplyHintAt = now + ApplyHintInterval;
                ApplyHint = BaseApplyBlockReason();
            }
        }

        private string ComputeWaitHint(float now)
        {
            if (!settings.Enabled.Value)
                return "自动保存已在配置中关闭";
            if (!inputSinceCapture)
                return "没有新操作";
            var idle = settings.IdleSeconds.Value - (now - lastInputTime);
            if (idle > 0f)
                return $"等待空闲 {Mathf.CeilToInt(idle)} 秒";
            var interval = settings.MinIntervalSeconds.Value - (now - lastAttemptTime);
            if (interval > 0f)
                return $"距下次自动保存 {Mathf.CeilToInt(interval)} 秒";
            var reason = EditorContext.BlockReasonForAutoSave();
            return reason == null ? "即将保存" : "暂缓：" + reason;
        }

        private void TrackInput()
        {
            var mouse = Input.mousePosition;
            if (Input.anyKey || Input.mouseScrollDelta.sqrMagnitude > 0f || (mouse - lastMouse).sqrMagnitude > 1f)
            {
                lastInputTime = Time.realtimeSinceStartup;
                inputSinceCapture = true;
            }
            lastMouse = mouse;
        }

        private void ResetTimers()
        {
            lastInputTime = Time.realtimeSinceStartup;
            lastAttemptTime = float.NegativeInfinity;
            inputSinceCapture = false;
            lastMouse = Input.mousePosition;
            nextWaitHintAt = 0f;
            nextApplyHintAt = 0f;
        }
    }
}
