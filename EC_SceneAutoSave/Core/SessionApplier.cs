using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using EC_SceneAutoSave.Storage;
using HarmonyLib;
using HEdit;
using LoadCharaFileSystem;

namespace EC_SceneAutoSave.Core
{
    // 会话内容已全部读入内存并通过解析校验，应用阶段不再读盘。
    internal sealed class PreparedSession
    {
        public SessionInfo Session;
        public Version Version;
        public readonly Dictionary<string, BasePart> NewParts = new Dictionary<string, BasePart>(StringComparer.Ordinal);
        public byte[] Graph;
        public List<byte[]> Maps;
        public HEditData.InfoData Info;
        public byte[] InfoBytes;
        public readonly List<KeyValuePair<int, byte[]>> Charas = new List<KeyValuePair<int, byte[]>>();
    }

    // 把会话应用到刚读档的基准场景。顺序：Part → 节点图 → 场景信息 → 角色 → 地图。
    internal sealed class SessionApplier
    {
        private static readonly MethodInfo CharaOnSelect = AccessTools.Method(typeof(BaseSettingCharacterUI), "OnSelect");

        private readonly SessionStore store;
        private readonly string tempDir;

        public SessionApplier(SessionStore store, string tempDir)
        {
            this.store = store;
            this.tempDir = tempDir;
        }

        // 场景仍等于读档时的基准才允许应用；按内容哈希判断，只打开看过的 Part 不算编辑。
        public static string EditedReason(SessionState state, ChangeTracker tracker, HEditData data, HEditGlobal global)
        {
            if (state.Applied)
                return "本次读档后已应用过会话";
            if (state.Dir != null)
                return "读档后的新编辑已自动保存";
            if (tracker.AnySwapped)
                return "读档后换过角色";
            if (tracker.PartSetChanged(data.nodes))
                return "读档后增删过 Part";
            foreach (var uid in tracker.Candidates(data.nodes))
            {
                if (!state.PartHash.TryGetValue(uid, out var known)
                    || known != ContentHash.Of(SceneCodec.SavePart(data.nodes[uid])))
                    return "读档后修改过 Part";
            }
            if (ContentHash.Of(SceneCodec.SaveGraph(global.nodeControl), SceneCodec.GraphViewBytes) != state.LoadGraphHash)
                return "读档后修改过节点图";
            if (ContentHash.Of(SceneCodec.SaveMaps(data.maps)) != state.LoadMapsHash)
                return "读档后修改过地图";
            if (SceneCapture.InfoHash(data.info) != state.LoadInfoHash)
                return "读档后修改过场景信息";
            return null;
        }

        public PreparedSession Prepare(SessionInfo session, HEditData data)
        {
            var manifest = session.Manifest;
            var prepared = new PreparedSession { Session = session, Version = SceneCodec.FormatVersion(data) };
            foreach (var entry in manifest.Parts)
            {
                if (string.IsNullOrEmpty(entry.Blob))
                {
                    if (!data.nodes.TryGetValue(entry.Uid, out var current))
                        throw new InvalidDataException("会话引用的 Part 不在当前场景中：" + entry.Uid);
                    if (current.kind != entry.Kind)
                        throw new InvalidDataException("Part 类型与当前场景不一致：" + entry.Uid);
                    continue;
                }
                var part = SceneCodec.LoadPart(store.ReadBlob(session.Dir, entry.Blob), entry.Kind, prepared.Version);
                prepared.NewParts[entry.Uid] = part;
            }
            if (!string.IsNullOrEmpty(manifest.GraphBlob))
            {
                prepared.Graph = store.ReadBlob(session.Dir, manifest.GraphBlob);
                SceneCodec.ValidateGraph(prepared.Graph, prepared.Version);
            }
            if (!string.IsNullOrEmpty(manifest.MapsBlob))
            {
                prepared.Maps = SceneCodec.SplitMaps(store.ReadBlob(session.Dir, manifest.MapsBlob));
                foreach (var bytes in prepared.Maps)
                    SceneCodec.LoadMap(bytes);
            }
            if (!string.IsNullOrEmpty(manifest.InfoBlob))
            {
                prepared.InfoBytes = store.ReadBlob(session.Dir, manifest.InfoBlob);
                prepared.Info = SceneCodec.LoadInfo(prepared.InfoBytes, prepared.Version);
            }
            foreach (var entry in manifest.Charas)
            {
                if (entry.Index >= data.charas.Count || data.charas[entry.Index] == null)
                    throw new InvalidDataException("会话里的角色序号超出当前场景：" + entry.Index);
                var bytes = store.ReadBlob(session.Dir, entry.Blob);
                SceneCodec.ValidateChara(bytes);
                prepared.Charas.Add(new KeyValuePair<int, byte[]>(entry.Index, bytes));
            }
            return prepared;
        }

        public void Execute(PreparedSession prepared, HEditData data, HEditGlobal global)
        {
            ApplyParts(prepared, data, global);
            ApplyGraph(prepared, global);
            ApplyInfo(prepared, data);
            ApplyCharas(prepared, global);
            ApplyMaps(prepared, data, global);
        }

        // 其它插件可能持有 nodes 字典引用，只能清空后按清单顺序重填同一个实例。
        private static void ApplyParts(PreparedSession prepared, HEditData data, HEditGlobal global)
        {
            var current = new Dictionary<string, BasePart>(data.nodes, StringComparer.Ordinal);
            var ordered = new List<KeyValuePair<string, BasePart>>();
            foreach (var entry in prepared.Session.Manifest.Parts)
            {
                if (!prepared.NewParts.TryGetValue(entry.Uid, out var part))
                    part = current[entry.Uid];
                part.useMapID = entry.MapId;
                ordered.Add(new KeyValuePair<string, BasePart>(entry.Uid, part));
            }
            data.nodes.Clear();
            foreach (var pair in ordered)
                data.nodes.Add(pair.Key, pair.Value);
            global.SelectPart = null;
        }

        // 节点图没变时不重建节点 UI；增删 Part 一定伴随节点图变化。
        private static void ApplyGraph(PreparedSession prepared, HEditGlobal global)
        {
            if (prepared.Graph == null)
                return;
            global.nodeControl.Load(prepared.Graph, prepared.Version);
            global.nodeControl.RebuildNodeUI();
            EditorContext.NodeCanvas()?.NodeRestriction();
        }

        // 缩略图与数据版本不在 InfoData 序列化里，读入后保持读档时的值。
        private static void ApplyInfo(PreparedSession prepared, HEditData data)
        {
            if (prepared.InfoBytes == null)
                return;
            SceneCodec.LoadInfoInto(data.info, prepared.InfoBytes, prepared.Version);
        }

        // 走原生换卡入口，复用它对衣装、资源包、角色名显示的全部刷新；临时卡用完即删。
        private void ApplyCharas(PreparedSession prepared, HEditGlobal global)
        {
            if (prepared.Charas.Count == 0)
                return;
            if (CharaOnSelect == null)
                throw new MissingMethodException("BaseSettingCharacterUI.OnSelect");
            var characterUI = Traverse.Create(global.baseSettingCanvas).Field("characterUI").GetValue<BaseSettingCharacterUI>();
            if (characterUI == null)
                throw new InvalidOperationException("基础设置的角色面板不可用。");
            characterUI.Init();
            var cards = Traverse.Create(characterUI).Field("infoCharaCards").GetValue<IList>();
            Directory.CreateDirectory(tempDir);
            foreach (var pair in prepared.Charas)
            {
                if (cards == null || pair.Key >= cards.Count)
                    throw new InvalidOperationException("角色卡片数量与场景不一致。");
                var path = Path.Combine(tempDir, "chara-" + pair.Key + ".png");
                File.WriteAllBytes(path, pair.Value);
                try
                {
                    CharaOnSelect.Invoke(characterUI, new object[] { cards[pair.Key], new CharaFileInfo { FullPath = path } });
                }
                catch (TargetInvocationException ex) when (ex.InnerException != null)
                {
                    throw new InvalidOperationException("换卡失败：" + ex.InnerException.Message, ex.InnerException);
                }
                finally
                {
                    TryDelete(path);
                }
            }
        }

        // 与原生地图删除/更换一致：就地更新 MapInfo 对象；当前显示的地图变了就退回"无地图"，打开 Part 时会按 useMapID 重新加载。
        private static void ApplyMaps(PreparedSession prepared, HEditData data, HEditGlobal global)
        {
            if (prepared.Maps == null)
                return;
            var before = SceneCodec.SplitCurrentMaps(data.maps);
            var map = Singleton<global::Map.Map>.IsInstance() ? Singleton<global::Map.Map>.Instance : null;
            var shownIndex = map != null ? data.maps.IndexOf(map.mapInfo) : -1;

            for (var i = 0; i < prepared.Maps.Count; i++)
            {
                var slot = i + 1;
                if (slot < data.maps.Count)
                {
                    if (!SameBytes(before[i], prepared.Maps[i]))
                        SceneCodec.LoadMapInto(data.maps[slot], prepared.Maps[i]);
                }
                else
                {
                    data.maps.Add(SceneCodec.LoadMap(prepared.Maps[i]));
                }
            }
            while (data.maps.Count > prepared.Maps.Count + 1)
                data.maps.RemoveAt(data.maps.Count - 1);

            var shownChanged = shownIndex > 0
                && (shownIndex > prepared.Maps.Count || !SameBytes(before[shownIndex - 1], prepared.Maps[shownIndex - 1]));
            if (shownChanged)
            {
                global.cam.ClearVanishCollider();
                global.cam.VisibleFroceVanish(true);
                map.Load(data.maps[0]);
            }
            global.CreateMapPackage();
            global.onChangeMap?.Invoke();
        }

        // 应用后以会话内容为新的已记录状态，后续自动保存继续写入这个会话目录。
        public static void Remember(SessionState state, ChangeTracker tracker, PreparedSession prepared, HEditData data, HEditGlobal global)
        {
            var manifest = prepared.Session.Manifest;
            state.Dir = prepared.Session.Dir;
            state.Seq = manifest.Seq;
            state.CreatedTicksUtc = manifest.CreatedTicksUtc;
            state.Applied = true;
            state.Last = manifest;

            state.PartBlob.Clear();
            foreach (var entry in manifest.Parts)
            {
                if (!string.IsNullOrEmpty(entry.Blob))
                    state.PartBlob[entry.Uid] = entry.Blob;
            }
            foreach (var uid in new List<string>(state.PartHash.Keys))
            {
                if (!data.nodes.ContainsKey(uid))
                    state.PartHash.Remove(uid);
            }
            foreach (var pair in prepared.NewParts)
                state.PartHash[pair.Key] = ContentHash.Of(SceneCodec.SavePart(pair.Value));

            state.CharaBlob.Clear();
            foreach (var entry in manifest.Charas)
                state.CharaBlob[entry.Index] = entry.Blob;

            state.GraphBlob = manifest.GraphBlob;
            state.MapsBlob = manifest.MapsBlob;
            state.InfoBlob = manifest.InfoBlob;
            state.GraphHash = ContentHash.Of(SceneCodec.SaveGraph(global.nodeControl), SceneCodec.GraphViewBytes);
            state.MapsHash = ContentHash.Of(SceneCodec.SaveMaps(data.maps));
            state.InfoHash = SceneCapture.InfoHash(data.info);
            state.LastError = null;
            tracker.AfterCommit(data.nodes, global);
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return false;
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
