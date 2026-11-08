using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using BepInEx.Logging;
using EC_SceneAutoSave.Storage;
using HEdit;
using UnityEngine;

namespace EC_SceneAutoSave.Core
{
    internal enum AutoSaveMode { Inactive, Active, Loading, Trial }

    internal enum LoadKind { Normal, Temporary, Tutorial }

    // 会话生命周期：读档、原生保存、试玩往返都在这里切换基准；Tick 只在 Active 时记录。
    internal sealed partial class AutoSaveController
    {
        private readonly ManualLogSource log;
        private readonly AutoSaveSettings settings;
        private readonly SessionStore store;
        private readonly SceneCapture capture;
        private readonly SessionApplier applier;
        private readonly ChangeTracker tracker = new ChangeTracker();

        private HEditGlobal boundGlobal;
        private BaseFingerprint trialFile;
        private string pendingLoadPath;
        private bool pendingLoadFromTrial;
        private string pendingRebasePath;
        private int pendingRebaseFrame;
        private bool openRequested;

        public AutoSaveMode Mode { get; private set; }
        public SessionState State { get; private set; }
        public string StatusText { get; private set; } = "未进入场景编辑";
        public string Notice { get; set; }
        public List<SessionInfo> Recoverable { get; private set; } = new List<SessionInfo>();
        public bool Applying { get; private set; }

        public AutoSaveController(ManualLogSource log, AutoSaveSettings settings, string autosaveRoot)
        {
            this.log = log;
            this.settings = settings;
            store = new SessionStore(Path.Combine(autosaveRoot, "sessions"));
            capture = new SceneCapture(log, store);
            applier = new SessionApplier(store, Path.Combine(autosaveRoot, "tmp"));
        }

        public void OnBeforeLoad()
        {
            if (Applying)
                return;
            pendingLoadFromTrial = Mode == AutoSaveMode.Trial;
            pendingLoadPath = null;
            pendingRebasePath = null;
            Mode = AutoSaveMode.Loading;
            StatusText = "读档中";
        }

        public void OnDataLoaded(string path, bool success)
        {
            if (Applying)
                return;
            if (!success)
            {
                Deactivate("读档失败，自动保存暂停");
                return;
            }
            pendingLoadPath = path;
        }

        // 两个读档入口都在角色加载完成后调用 RebuildNodeUI，此时数据与节点 UI 都已就绪。
        public void OnNodeUiRebuilt(bool success)
        {
            if (Applying || Mode != AutoSaveMode.Loading || pendingLoadPath == null)
                return;
            var path = pendingLoadPath;
            var fromTrial = pendingLoadFromTrial;
            pendingLoadPath = null;
            pendingLoadFromTrial = false;
            if (!success)
            {
                Deactivate("节点重建失败，自动保存暂停");
                return;
            }
            try
            {
                HandleLoaded(path, fromTrial);
            }
            catch (Exception ex)
            {
                log.LogError("Autosave start failed: " + ex);
                Deactivate("初始化失败：" + ex.Message);
            }
        }

        private void HandleLoaded(string path, bool fromTrial)
        {
            switch (Classify(path))
            {
                case LoadKind.Tutorial:
                    Deactivate("教程中不自动保存");
                    return;
                case LoadKind.Temporary:
                    // 试玩期间可能经标题进入了别的场景并再次试玩，trial.png 已被改写时旧会话不能接上。
                    if (fromTrial && State != null && trialFile != null && trialFile.SameFile(BaseFingerprint.TryRead(path)))
                        ResumeAfterTrial();
                    else
                        Deactivate("临时场景不自动保存，请先原生保存一次");
                    return;
            }
            StartBase(path);
            RefreshRecoverable();
            if (Recoverable.Count > 0)
                openRequested = true;
        }

        private void ResumeAfterTrial()
        {
            tracker.RebuildRefs(EditorContext.Data.nodes);
            var editing = tracker.Adopt(EditorContext.Global);
            if (editing != null && !State.PartHash.ContainsKey(editing.uuId))
                RecordBaseHash(editing);
            boundGlobal = EditorContext.Global;
            trialFile = null;
            Mode = AutoSaveMode.Active;
            StatusText = "自动保存中";
            lastInputTime = Time.realtimeSinceStartup;
        }

        private void StartBase(string path)
        {
            var data = EditorContext.Data;
            var global = EditorContext.Global;
            var fingerprint = BaseFingerprint.TryRead(path);
            if (data == null || global == null || global.nodeControl == null || fingerprint == null)
            {
                Deactivate("找不到基准存档，自动保存暂停");
                return;
            }
            if (EditorContext.IsTutorial())
            {
                Deactivate("教程中不自动保存");
                return;
            }
            State = NewState(fingerprint, data, global);
            tracker.Reset(data.nodes);
            var editing = tracker.Adopt(global);
            if (editing != null)
                RecordBaseHash(editing);
            boundGlobal = global;
            trialFile = null;
            Mode = AutoSaveMode.Active;
            StatusText = "自动保存中";
            Notice = null;
            ResetTimers();
            log.LogInfo($"Autosave base: {fingerprint.Path} ({data.nodes.Count} parts)");
        }

        private static SessionState NewState(BaseFingerprint fingerprint, HEditData data, HEditGlobal global)
        {
            var state = new SessionState
            {
                Base = fingerprint,
                SceneKey = SceneKey.For(fingerprint.Path),
                GraphHash = ContentHash.Of(SceneCodec.SaveGraph(global.nodeControl), SceneCodec.GraphViewBytes),
                MapsHash = ContentHash.Of(SceneCodec.SaveMaps(data.maps)),
                InfoHash = SceneCapture.InfoHash(data.info)
            };
            state.LoadGraphHash = state.GraphHash;
            state.LoadMapsHash = state.MapsHash;
            state.LoadInfoHash = state.InfoHash;
            state.Last = BaseManifest(data);
            return state;
        }

        // 与基准完全一致的清单：不引用任何数据文件，只有 Part 顺序与地图编号。
        private static SessionManifest BaseManifest(HEditData data)
        {
            var manifest = new SessionManifest();
            foreach (var pair in data.nodes)
                manifest.Parts.Add(new SessionPartEntry { Uid = pair.Key, Kind = pair.Value.kind, MapId = pair.Value.useMapID });
            return manifest;
        }

        // 打开 Part 编辑前记下它在基准里的内容哈希，之后只看过不改的 Part 不会被写入。
        private void RecordBaseHash(BasePart part)
        {
            var watch = Stopwatch.StartNew();
            var bytes = SceneCodec.SavePart(part);
            State.PartHash[part.uuId] = ContentHash.Of(bytes);
            log.LogInfo($"Autosave baseline {part.uuId} \"{part.name}\": {bytes.Length} bytes, {watch.ElapsedMilliseconds} ms");
        }

        public void OnDataSaved(string path)
        {
            if (Applying)
                return;
            if (IsTrialFile(path))
            {
                // 试玩按钮存完 trial.png 会立刻 HEditData.Init()，这是记录试玩前改动的最后机会。
                if (Mode != AutoSaveMode.Active)
                    return;
                if (settings.Enabled.Value)
                    Capture();
                trialFile = BaseFingerprint.TryRead(path);
                Mode = AutoSaveMode.Trial;
                StatusText = "试玩中";
                return;
            }
            // 另存为、覆盖保存都以新文件为基准；saveID/userID 由调用方在返回后才恢复，下一帧再取哈希。
            pendingRebasePath = path;
            pendingRebaseFrame = Time.frameCount + 1;
            if (Mode == AutoSaveMode.Active)
                Mode = AutoSaveMode.Loading;
        }

        private void Rebase()
        {
            var path = pendingRebasePath;
            pendingRebasePath = null;
            if (Classify(path) != LoadKind.Normal)
            {
                Deactivate("临时场景不自动保存，请先原生保存一次");
                return;
            }
            var fingerprint = BaseFingerprint.TryRead(path);
            if (fingerprint != null)
                TryCleanUp(() => store.PurgeMismatched(SceneKey.For(fingerprint.Path), fingerprint));
            StartBase(path);
            if (State != null)
                TryCleanUp(() => store.EnforceRetention(State.SceneKey, settings.SessionsPerScene.Value, null));
            RefreshRecoverable();
        }

        // 试玩前的 Init 发生在 Trial 状态，不处理；其它时机说明场景被整体重置，旧会话不能继续写。
        public void OnDataReset()
        {
            if (Applying || Mode == AutoSaveMode.Trial || Mode == AutoSaveMode.Inactive)
                return;
            Deactivate("场景已重置，请读档或原生保存一次");
        }

        public void OnBeforePartEdit(string uid)
        {
            if (Applying || Mode != AutoSaveMode.Active || State == null || uid == null)
                return;
            var data = EditorContext.Data;
            if (data == null || !data.nodes.TryGetValue(uid, out var part))
                return;
            // 已记录过、或打开前就被改过（引用变化、已在候选里）的 Part，当前内容不能当作基准。
            if (State.PartHash.ContainsKey(uid) || tracker.IsTouched(uid) || !tracker.IsBaseRef(uid, part))
                return;
            RecordBaseHash(part);
        }

        public void OnCharaSwapped(int index)
        {
            if (!Applying && Mode == AutoSaveMode.Active)
                tracker.MarkChara(index);
        }

        public void OnBatchReplaced(string uid)
        {
            if (!Applying && Mode == AutoSaveMode.Active)
                tracker.MarkPart(uid);
        }

        public bool ConsumeOpenRequest()
        {
            if (!openRequested || !EditorContext.IsHEditTopScene())
                return false;
            openRequested = false;
            return true;
        }

        private void Deactivate(string reason)
        {
            Mode = AutoSaveMode.Inactive;
            State = null;
            boundGlobal = null;
            trialFile = null;
            pendingLoadPath = null;
            pendingLoadFromTrial = false;
            pendingRebasePath = null;
            pendingApplyDir = null;
            pendingSave = false;
            openRequested = false;
            Recoverable = new List<SessionInfo>();
            StatusText = reason;
        }

        private void RefreshRecoverable()
        {
            if (State == null)
            {
                Recoverable = new List<SessionInfo>();
                return;
            }
            try
            {
                var list = store.FindRecoverable(State.SceneKey, State.Base);
                list.RemoveAll(s => SessionStore.SameDir(s.Dir, State.Dir));
                Recoverable = list;
            }
            catch (Exception ex)
            {
                log.LogWarning("Autosave session scan failed: " + ex.Message);
                Recoverable = new List<SessionInfo>();
            }
        }

        private void TryCleanUp(Func<int> action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                log.LogWarning("Autosave cleanup failed: " + ex.Message);
            }
        }

        private static string NormalizedFullPath(string path)
        {
            try
            {
                return BaseFingerprint.Normalize(Path.GetFullPath(path)).ToLowerInvariant();
            }
            catch (Exception)
            {
                return BaseFingerprint.Normalize(path ?? string.Empty).ToLowerInvariant();
            }
        }

        // 试玩写 DefaultData/edit/temporary/trial.png，教程读 DefaultData/edit/tutorial/。
        internal static LoadKind Classify(string path)
        {
            var normalized = NormalizedFullPath(path);
            if (normalized.Contains("/edit/temporary/"))
                return LoadKind.Temporary;
            if (normalized.Contains("/edit/tutorial/"))
                return LoadKind.Tutorial;
            return LoadKind.Normal;
        }

        private static bool IsTrialFile(string path)
        {
            return Classify(path) == LoadKind.Temporary
                && string.Equals(Path.GetFileName(path), "trial.png", StringComparison.OrdinalIgnoreCase);
        }
    }
}
