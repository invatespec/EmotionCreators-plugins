using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using BepInEx.Logging;
using EC_SceneAutoSave.Storage;
using HEdit;

namespace EC_SceneAutoSave.Core
{
    internal enum CaptureOutcome { Committed, NoChange, Failed }

    // 一次自动保存要写的数据文件与写成功后要更新的记录，先算好再统一提交。
    internal sealed class CapturePlan
    {
        public readonly List<KeyValuePair<string, byte[]>> Writes = new List<KeyValuePair<string, byte[]>>();
        public readonly Dictionary<string, string> PartBlob = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> PartHash = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<int, string> CharaBlob = new Dictionary<int, string>();
        public string GraphHash, MapsHash, InfoHash, GraphBlob, MapsBlob, InfoBlob;
        public int PartWrites;
        public long Bytes;
        public SessionManifest Manifest;

        public void Add(string name, byte[] data)
        {
            Writes.Add(new KeyValuePair<string, byte[]>(name, data));
            Bytes += data.Length;
        }
    }

    internal sealed class SceneCapture
    {
        private readonly ManualLogSource log;
        private readonly SessionStore store;

        public SceneCapture(ManualLogSource log, SessionStore store)
        {
            this.log = log;
            this.store = store;
        }

        public CaptureOutcome Run(SessionState state, ChangeTracker tracker, HEditData data, HEditGlobal global, int keepSessions)
        {
            var watch = Stopwatch.StartNew();
            var seq = state.Seq + 1;
            CapturePlan plan;
            try
            {
                // 与原生保存一致：先把正在编辑的 Part 的结束条件同步到节点。
                if (global.SelectPart != null)
                    global.nodeControl.UpdateNodeConditions(global.SelectPart.uuId);
                tracker.PromoteCandidates(data.nodes);
                plan = BuildPlan(state, tracker, data, global, seq);
            }
            catch (Exception ex)
            {
                state.LastError = "序列化失败：" + ex.Message;
                log.LogError("Autosave serialize failed: " + ex);
                return CaptureOutcome.Failed;
            }

            if (plan.Writes.Count == 0 && plan.Manifest.SameContent(state.Last))
            {
                tracker.AfterCommit(data.nodes, global);
                return CaptureOutcome.NoChange;
            }
            return Commit(state, tracker, data, global, plan, seq, watch, keepSessions);
        }

        private CapturePlan BuildPlan(SessionState state, ChangeTracker tracker, HEditData data, HEditGlobal global, int seq)
        {
            var plan = new CapturePlan();
            foreach (var pair in state.PartBlob)
                plan.PartBlob[pair.Key] = pair.Value;
            foreach (var pair in state.PartHash)
                plan.PartHash[pair.Key] = pair.Value;
            foreach (var pair in state.CharaBlob)
                plan.CharaBlob[pair.Key] = pair.Value;

            CaptureParts(state, tracker, data, plan, seq);
            CaptureGraph(state, global, plan, seq);
            CaptureMaps(state, data, plan, seq);
            CaptureInfo(state, data, plan, seq);
            CaptureCharas(tracker, data, plan, seq);
            plan.Manifest = BuildManifest(state, data, plan, seq);
            return plan;
        }

        private void CaptureParts(SessionState state, ChangeTracker tracker, HEditData data, CapturePlan plan, int seq)
        {
            var index = 0;
            foreach (var uid in tracker.Candidates(data.nodes))
            {
                var bytes = SceneCodec.SavePart(data.nodes[uid]);
                var hash = ContentHash.Of(bytes);
                if (plan.PartHash.TryGetValue(uid, out var known) && known == hash)
                    continue;
                var name = "part-" + seq + "-" + index++ + ".bin";
                plan.Add(name, bytes);
                plan.PartBlob[uid] = name;
                plan.PartHash[uid] = hash;
                plan.PartWrites++;
                log.LogInfo($"Autosave part {uid} \"{data.nodes[uid].name}\": {bytes.Length} bytes");
            }
            foreach (var uid in plan.PartBlob.Keys.Where(k => !data.nodes.ContainsKey(k)).ToList())
                plan.PartBlob.Remove(uid);
            foreach (var uid in plan.PartHash.Keys.Where(k => !data.nodes.ContainsKey(k)).ToList())
                plan.PartHash.Remove(uid);
        }

        private static void CaptureGraph(SessionState state, HEditGlobal global, CapturePlan plan, int seq)
        {
            var bytes = SceneCodec.SaveGraph(global.nodeControl);
            plan.GraphHash = ContentHash.Of(bytes, SceneCodec.GraphViewBytes);
            plan.GraphBlob = state.GraphBlob;
            if (plan.GraphHash == state.GraphHash)
                return;
            plan.GraphBlob = "graph-" + seq + ".bin";
            plan.Add(plan.GraphBlob, bytes);
        }

        private static void CaptureMaps(SessionState state, HEditData data, CapturePlan plan, int seq)
        {
            var bytes = SceneCodec.SaveMaps(data.maps);
            plan.MapsHash = ContentHash.Of(bytes);
            plan.MapsBlob = state.MapsBlob;
            if (plan.MapsHash == state.MapsHash)
                return;
            plan.MapsBlob = "maps-" + seq + ".bin";
            plan.Add(plan.MapsBlob, bytes);
        }

        private static void CaptureInfo(SessionState state, HEditData data, CapturePlan plan, int seq)
        {
            plan.InfoHash = InfoHash(data.info);
            plan.InfoBlob = state.InfoBlob;
            if (plan.InfoHash == state.InfoHash)
                return;
            plan.InfoBlob = "info-" + seq + ".bin";
            plan.Add(plan.InfoBlob, SceneCodec.SaveInfo(data.info));
        }

        private void CaptureCharas(ChangeTracker tracker, HEditData data, CapturePlan plan, int seq)
        {
            foreach (var index in tracker.Swapped)
            {
                if (index >= data.charas.Count || data.charas[index] == null)
                    continue;
                var bytes = SceneCodec.SaveChara(data.charas[index]);
                var name = "chara-" + seq + "-" + index + ".bin";
                plan.Add(name, bytes);
                plan.CharaBlob[index] = name;
                log.LogInfo($"Autosave chara #{index}: {bytes.Length} bytes");
            }
        }

        // 资源包集合会被原生代码按需重建，顺序不稳定；按排序后的内容比较，避免无编辑时误判。
        public static string InfoHash(HEditData.InfoData info)
        {
            var sb = new StringBuilder();
            sb.Append(info.language).Append('|').Append(info.userID).Append('|').Append(info.saveID).Append('|')
                .Append(info.title).Append('|').Append(info.comment).Append('|').Append(info.defaultBGM).Append('|')
                .Append(string.Join(",", info.tagKinds.Select(t => t.ToString()).ToArray())).Append('|')
                .Append(info.maleCount).Append('|').Append(info.femaleCount).Append('|')
                .Append(info.isPlaying).Append(info.isUseADVPart).Append(info.isUseHPart).Append('|')
                .Append(string.Join(",", info.charaPackage.OrderBy(v => v).Select(v => v.ToString()).ToArray())).Append('|')
                .Append(string.Join(",", info.mapPackage.OrderBy(v => v).Select(v => v.ToString()).ToArray())).Append('|')
                .Append(info.isMapSet).Append('|').Append(info.useMapObject);
            return ContentHash.OfText(sb.ToString());
        }

        private static SessionManifest BuildManifest(SessionState state, HEditData data, CapturePlan plan, int seq)
        {
            var now = DateTime.UtcNow.Ticks;
            var manifest = new SessionManifest
            {
                GameVersion = SceneCodec.FormatVersion(data).ToString(),
                BasePath = state.Base.Path,
                BaseLength = state.Base.Length,
                BaseWriteTicksUtc = state.Base.WriteTicksUtc,
                CreatedTicksUtc = state.CreatedTicksUtc != 0 ? state.CreatedTicksUtc : now,
                UpdatedTicksUtc = now,
                Seq = seq,
                GraphBlob = plan.GraphBlob,
                MapsBlob = plan.MapsBlob,
                InfoBlob = plan.InfoBlob
            };
            foreach (var pair in data.nodes)
            {
                plan.PartBlob.TryGetValue(pair.Key, out var blob);
                manifest.Parts.Add(new SessionPartEntry { Uid = pair.Key, Kind = pair.Value.kind, MapId = pair.Value.useMapID, Blob = blob });
            }
            foreach (var pair in plan.CharaBlob.OrderBy(p => p.Key))
                manifest.Charas.Add(new SessionCharaEntry { Index = pair.Key, Blob = pair.Value });
            return manifest;
        }

        private CaptureOutcome Commit(SessionState state, ChangeTracker tracker, HEditData data, HEditGlobal global,
            CapturePlan plan, int seq, Stopwatch watch, int keepSessions)
        {
            var createdDir = false;
            try
            {
                if (state.Dir == null)
                {
                    state.Dir = store.CreateSessionDir(state.SceneKey, DateTime.Now);
                    createdDir = true;
                }
                foreach (var write in plan.Writes)
                    store.WriteBlob(state.Dir, write.Key, write.Value);
                store.CommitManifest(state.Dir, plan.Manifest);
            }
            catch (Exception ex)
            {
                // 失败的序号不再复用；已有目录里的残留文件由下次成功提交后的清理删除。
                state.Seq = seq;
                if (createdDir)
                {
                    store.DeleteSession(state.Dir);
                    state.Dir = null;
                }
                state.LastError = "写入失败：" + ex.Message;
                log.LogError("Autosave commit failed: " + ex);
                return CaptureOutcome.Failed;
            }

            Remember(state, plan, seq);
            tracker.AfterCommit(data.nodes, global);
            CleanUp(state, keepSessions);
            state.LastMillis = watch.ElapsedMilliseconds;
            log.LogInfo($"Autosave committed seq {seq}: {plan.PartWrites} part(s), {plan.Writes.Count} file(s), "
                + $"{plan.Bytes} bytes, {state.LastMillis} ms -> {state.Dir}");
            return CaptureOutcome.Committed;
        }

        private static void Remember(SessionState state, CapturePlan plan, int seq)
        {
            state.Seq = seq;
            state.CreatedTicksUtc = plan.Manifest.CreatedTicksUtc;
            CopyInto(state.PartBlob, plan.PartBlob);
            CopyInto(state.PartHash, plan.PartHash);
            CopyInto(state.CharaBlob, plan.CharaBlob);
            state.GraphHash = plan.GraphHash;
            state.MapsHash = plan.MapsHash;
            state.InfoHash = plan.InfoHash;
            state.GraphBlob = plan.GraphBlob;
            state.MapsBlob = plan.MapsBlob;
            state.InfoBlob = plan.InfoBlob;
            state.Last = plan.Manifest;
            state.LastCommitLocal = DateTime.Now;
            state.LastPartCount = plan.PartWrites;
            state.LastBytes = plan.Bytes;
            state.LastError = null;
        }

        private static void CopyInto<TKey>(Dictionary<TKey, string> target, Dictionary<TKey, string> source)
        {
            target.Clear();
            foreach (var pair in source)
                target[pair.Key] = pair.Value;
        }

        private void CleanUp(SessionState state, int keepSessions)
        {
            try
            {
                store.CollectGarbage(state.Dir);
                store.EnforceRetention(state.SceneKey, keepSessions, state.Dir);
            }
            catch (Exception ex)
            {
                log.LogWarning("Autosave cleanup failed: " + ex.Message);
            }
        }
    }
}
