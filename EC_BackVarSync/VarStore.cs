using ADV;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using HEdit;
using HPlay;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using System.Xml.Serialization;

namespace EC_BackVarSync
{
    /// <summary>面板存档列表条目（轻量解析，不反序列化变量体）。</summary>
    public sealed class SaveEntry
    {
        public string FileName;   // 净化后的存档名（无扩展名）
        public string SceneTitle;
        public string TimeLocal;  // 本地时间，解析失败为 ""
    }

    /// <summary>
    /// 变量存档读写引擎，三类目标：
    /// 章节存档 UserData/PluginData/EC_BackVarSync/chapters|autosave 下；
    /// 书签槽位与 EC_back 书签同目录（&lt;bookmark_dir&gt;/&lt;场景&gt;/vars/NN.xml）。
    /// </summary>
    internal sealed class VarStore
    {
        public string RootDir { get; }

        private readonly ManualLogSource _log;
        private readonly VariableModBridge _bridge;

        public VarStore(ManualLogSource log)
        {
            _log = log;
            _bridge = new VariableModBridge(log);
            RootDir = Path.Combine(Paths.GameRootPath, "UserData", "PluginData", "EC_BackVarSync");
        }

        #region 当前上下文

        public static string GetSceneTitle()
        {
            try
            {
                return Singleton<HEditData>.Instance?.info?.title ?? "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>读当前游玩位置（ADVPlay 内部 cutIndex 转 0 基）。失败返回 false。</summary>
        public static bool TryGetCurrentPosition(out string partUid, out string partName, out int cutIndex)
        {
            partUid = "";
            partName = "";
            cutIndex = 0;

            try
            {
                var hpd = Singleton<HPlayData>.Instance;
                var part = hpd?.basePart;
                if (part == null || string.IsNullOrEmpty(part.uuId))
                    return false;

                partUid = part.uuId;
                partName = part.name ?? "";

                var advPlay = Singleton<ADVPlay>.Instance;
                int raw = -1;
                var tr = advPlay == null ? null : Traverse.Create(advPlay).Field("cutIndex");
                if (tr != null && tr.FieldExists())
                    raw = tr.GetValue<int>();
                cutIndex = Math.Max(0, raw - 1);
                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region 核心读写

        private bool SaveTo(string path, string sceneTitle, string partUid, string partName, int cutIndex)
        {
            if (!_bridge.TrySnapshot(out var globals, out var arrays))
                return false;

            var file = new SaveFile
            {
                timestampUtc = DateTime.UtcNow.ToString("O"),
                adv = new AdvInfo
                {
                    sceneTitle = sceneTitle ?? "",
                    partUID = partUid ?? "",
                    partName = partName ?? "",
                    cutIndex = cutIndex
                },
                globals = globals,
                arrays = arrays
            };

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var serializer = new XmlSerializer(typeof(SaveFile));
                using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
                {
                    serializer.Serialize(writer, file);
                }
                return true;
            }
            catch (Exception ex)
            {
                _log.LogError($"存档写入失败: {path} : {ex.Message}");
                return false;
            }
        }

        private bool TryLoadFile(string path, out SaveFile file)
        {
            file = null;
            if (!File.Exists(path))
                return false;

            try
            {
                var serializer = new XmlSerializer(typeof(SaveFile));
                using (var reader = new StreamReader(path, Encoding.UTF8))
                {
                    file = (SaveFile)serializer.Deserialize(reader);
                }
                return file != null;
            }
            catch (Exception ex)
            {
                _log.LogError($"存档读取失败: {path} : {ex.Message}");
                return false;
            }
        }

        /// <summary>反序列化并恢复变量到 VariableMod。</summary>
        public bool RestoreFrom(string path)
        {
            if (!TryLoadFile(path, out var file))
                return false;

            if (!_bridge.TryRestore(file.globals, file.arrays))
            {
                _log.LogError($"变量恢复失败: {path}");
                return false;
            }
            return true;
        }

        #endregion

        #region 剧本/章节存档（两级目录：chapters/<剧本>/<章节>/<存档>.xml）

        private string PlayDir(string play)
        {
            return Path.Combine(RootDir, "chapters", play ?? "");
        }

        private string ChapterDir(string play, string chapter)
        {
            return Path.Combine(PlayDir(play), chapter ?? "");
        }

        public List<string> ListPlays()
        {
            var dir = Path.Combine(RootDir, "chapters");
            var result = new List<string>();
            if (!Directory.Exists(dir))
                return result;

            result.AddRange(Directory.GetDirectories(dir).Select(Path.GetFileName));
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        /// <summary>新建剧本目录。重名或非法名返回 false。</summary>
        public bool CreatePlay(string rawName, out string sanitized)
        {
            return CreateDirSafe(Path.Combine(RootDir, "chapters", SanitizePathSegment(rawName)),
                rawName, "剧本", out sanitized);
        }

        public List<string> ListChapters(string play)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(play))
                return result;

            string dir = PlayDir(play);
            if (!Directory.Exists(dir))
                return result;

            result.AddRange(Directory.GetDirectories(dir).Select(Path.GetFileName));
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        /// <summary>在剧本下新建章节目录。</summary>
        public bool CreateChapter(string play, string rawName, out string sanitized)
        {
            if (string.IsNullOrEmpty(play))
            {
                sanitized = null;
                return false;
            }
            return CreateDirSafe(ChapterDir(play, SanitizePathSegment(rawName)),
                rawName, "章节", out sanitized);
        }

        /// <summary>重命名剧本目录。</summary>
        public bool RenamePlay(string oldName, string rawNewName, out string sanitized)
        {
            sanitized = SanitizePathSegment(rawNewName);
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(sanitized) || sanitized == oldName)
                return false;

            string from = PlayDir(oldName);
            string to = PlayDir(sanitized);
            if (!Directory.Exists(from) || Directory.Exists(to))
                return false;

            try
            {
                Directory.Move(from, to);
                return true;
            }
            catch (Exception ex)
            {
                _log.LogError($"剧本重命名失败: {from} → {to} : {ex.Message}");
                return false;
            }
        }

        /// <summary>重命名章节目录。</summary>
        public bool RenameChapter(string play, string oldName, string rawNewName, out string sanitized)
        {
            sanitized = SanitizePathSegment(rawNewName);
            if (string.IsNullOrEmpty(play) || string.IsNullOrEmpty(oldName)
                || string.IsNullOrEmpty(sanitized) || sanitized == oldName)
                return false;

            string from = ChapterDir(play, oldName);
            string to = ChapterDir(play, sanitized);
            if (!Directory.Exists(from) || Directory.Exists(to))
                return false;

            try
            {
                Directory.Move(from, to);
                return true;
            }
            catch (Exception ex)
            {
                _log.LogError($"章节重命名失败: {from} → {to} : {ex.Message}");
                return false;
            }
        }

        /// <summary>删除剧本目录（含全部章节与存档）。仅允许删自己数据根下的直接子目录。</summary>
        public bool DeletePlay(string name)
        {
            return DeleteDirSafe(PlayDir(name), name);
        }

        /// <summary>删除章节目录（含其下全部存档）。</summary>
        public bool DeleteChapter(string play, string chapter)
        {
            if (string.IsNullOrEmpty(play))
                return false;
            return DeleteDirSafe(ChapterDir(play, chapter), chapter);
        }

        private bool DeleteDirSafe(string dir, string displayName)
        {
            if (string.IsNullOrEmpty(displayName))
                return false;

            try
            {
                string full = Path.GetFullPath(dir);
                string rootFull = Path.GetFullPath(RootDir).TrimEnd(Path.DirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                    return false; // 防御：路径必须在本插件数据根内

                if (!Directory.Exists(dir))
                    return false;

                Directory.Delete(dir, true);
                return true;
            }
            catch (Exception ex)
            {
                _log.LogError($"目录删除失败: {dir} : {ex.Message}");
                return false;
            }
        }

        private bool CreateDirSafe(string dir, string rawName, string kind, out string sanitized)
        {
            sanitized = SanitizePathSegment(rawName);
            if (string.IsNullOrEmpty(sanitized))
                return false;

            if (Directory.Exists(dir))
                return false;

            try
            {
                Directory.CreateDirectory(dir);
                return true;
            }
            catch (Exception ex)
            {
                // 覆盖 Windows 保留设备名（CON/PRN 等）、权限、磁盘 IO 等异常
                _log.LogError($"{kind}目录创建失败: {dir} : {ex.Message}");
                return false;
            }
        }

        public bool SaveChapter(string play, string chapter, string rawSaveName, string sceneTitle,
                                 string partUid, string partName, int cutIndex, out string fileName)
        {
            fileName = SanitizePathSegment(rawSaveName);
            if (string.IsNullOrEmpty(play) || string.IsNullOrEmpty(chapter) || string.IsNullOrEmpty(fileName))
                return false;

            string path = Path.Combine(ChapterDir(play, chapter), fileName + ".xml");
            if (!SaveTo(path, sceneTitle, partUid, partName, cutIndex))
            {
                fileName = null;
                return false;
            }
            return true;
        }

        public List<SaveEntry> ListSaves(string play, string chapter)
        {
            var result = new List<SaveEntry>();
            if (string.IsNullOrEmpty(play) || string.IsNullOrEmpty(chapter))
                return result;

            string dir = ChapterDir(play, chapter);
            if (!Directory.Exists(dir))
                return result;

            foreach (string path in Directory.GetFiles(dir, "*.xml").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var entry = new SaveEntry { FileName = Path.GetFileNameWithoutExtension(path) };
                try
                {
                    var doc = XDocument.Load(path);
                    entry.SceneTitle = doc.Root?.Element("adv")?.Element("sceneTitle")?.Value ?? "";
                    entry.TimeLocal = FormatTimestamp(doc.Root?.Element("timestampUtc")?.Value) ?? "";
                }
                catch
                {
                    // 单个文件损坏不阻塞列表
                }
                result.Add(entry);
            }
            return result;
        }

        public bool LoadChapterSave(string play, string chapter, string fileName)
        {
            if (string.IsNullOrEmpty(play) || string.IsNullOrEmpty(chapter) || string.IsNullOrEmpty(fileName))
                return false;
            return RestoreFrom(Path.Combine(ChapterDir(play, chapter), Path.GetFileNameWithoutExtension(fileName) + ".xml"));
        }

        public bool DeleteChapterSave(string play, string chapter, string fileName)
        {
            if (string.IsNullOrEmpty(play) || string.IsNullOrEmpty(chapter) || string.IsNullOrEmpty(fileName))
                return false;

            string path = Path.Combine(ChapterDir(play, chapter), Path.GetFileNameWithoutExtension(fileName) + ".xml");
            if (!File.Exists(path))
                return false;

            try
            {
                File.Delete(path);
                return true;
            }
            catch (Exception ex)
            {
                _log.LogError($"存档删除失败: {path} : {ex.Message}");
                return false;
            }
        }

        /// <summary>重命名章节内存档文件。</summary>
        public bool RenameSave(string play, string chapter, string oldFileName, string rawNewName, out string sanitized)
        {
            sanitized = SanitizePathSegment(rawNewName);
            if (string.IsNullOrEmpty(play) || string.IsNullOrEmpty(chapter) || string.IsNullOrEmpty(oldFileName)
                || string.IsNullOrEmpty(sanitized) || sanitized == oldFileName)
                return false;

            string from = Path.Combine(ChapterDir(play, chapter), Path.GetFileNameWithoutExtension(oldFileName) + ".xml");
            string to = Path.Combine(ChapterDir(play, chapter), sanitized + ".xml");
            if (!File.Exists(from) || File.Exists(to))
                return false;

            try
            {
                File.Move(from, to);
                return true;
            }
            catch (Exception ex)
            {
                _log.LogError($"存档重命名失败: {from} → {to} : {ex.Message}");
                return false;
            }
        }

        #endregion

        #region 自动存档

        private string AutoSavePath(string sceneTitle)
        {
            return Path.Combine(RootDir, "autosave", SanitizePathSegment(sceneTitle), "AutoSave.xml");
        }

        public bool SaveAuto(string sceneTitle, string partUid, string partName, int cutIndex)
        {
            return SaveTo(AutoSavePath(sceneTitle), sceneTitle, partUid, partName, cutIndex);
        }

        public bool RestoreAuto(string sceneTitle)
        {
            return RestoreFrom(AutoSavePath(sceneTitle));
        }

        /// <summary>最近一次自动存档的本地时间，无存档返回 null。</summary>
        public string GetAutoSaveTime(string sceneTitle)
        {
            string path = AutoSavePath(sceneTitle);
            if (!File.Exists(path))
                return null;

            try
            {
                return FormatTimestamp(XDocument.Load(path).Root?.Element("timestampUtc")?.Value);
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region EC_back 书签槽位

        private static string BookmarkVarsPath(string bookmarkDir, string title, int slot)
        {
            return Path.Combine(bookmarkDir ?? "", title ?? "", "vars", string.Format("{0:00}.xml", slot));
        }

        public bool SaveBookmarkVars(string bookmarkDir, string title, int slot,
                                      string sceneTitle, string partUid, string partName, int cutIndex)
        {
            return SaveTo(BookmarkVarsPath(bookmarkDir, title, slot), sceneTitle, partUid, partName, cutIndex);
        }

        public bool TryRestoreBookmarkVars(string bookmarkDir, string title, int slot)
        {
            return RestoreFrom(BookmarkVarsPath(bookmarkDir, title, slot));
        }

        /// <summary>清理被 EC_back 重置为 init 的槽位对应的变量文件，返回删除数。</summary>
        public int CleanupBookmarkVars(string bookmarkDir, string title, string[] bmark)
        {
            if (bmark == null)
                return 0;

            int removed = 0;
            for (int i = 0; i < bmark.Length; i++)
            {
                if (bmark[i] != "init")
                    continue;

                string path = BookmarkVarsPath(bookmarkDir, title, i);
                if (!File.Exists(path))
                    continue;

                try
                {
                    File.Delete(path);
                    removed++;
                }
                catch (Exception ex)
                {
                    _log.LogWarning($"清理书签变量文件失败: {path} : {ex.Message}");
                }
            }
            return removed;
        }

        #endregion

        internal static string FormatTimestamp(string timestampUtc)
        {
            if (string.IsNullOrEmpty(timestampUtc))
                return null;

            try
            {
                if (DateTime.TryParse(timestampUtc, null, DateTimeStyles.RoundtripKind, out var dt))
                    return dt.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss");
            }
            catch
            {
                // 解析失败返回 null，调用方按无时间处理
            }
            return null;
        }

        internal static string SanitizePathSegment(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";

            s = s.Replace("..", "_");
            s = s.Replace("/", "_").Replace("\\", "_");

            foreach (char c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');

            return s.Trim();
        }
    }
}
