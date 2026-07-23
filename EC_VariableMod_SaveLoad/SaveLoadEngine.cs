using BepInEx;
using BepInEx.Logging;
using System;
using System.IO;
using System.Text;
using System.Xml.Linq;
using System.Xml.Serialization;

namespace EC_VariableMod_SaveLoad
{
    internal sealed class SaveLoadEngine
    {
        private readonly ManualLogSource _log;
        private readonly VariableModBridge _bridge;

        public SaveLoadEngine(ManualLogSource log)
        {
            _log = log;
            _bridge = new VariableModBridge(log);
        }

        public string GetBaseDir(string sceneTitle)
        {
            string root = Path.Combine(Paths.GameRootPath, "UserData\\edit\\Otherscene");
            return Path.Combine(root, SanitizePathSegment(sceneTitle ?? "UnknownScene"));
        }

        public string GetSavePath(string sceneTitle, string saveName)
        {
            string dir = GetBaseDir(sceneTitle);
            return Path.Combine(dir, SanitizePathSegment(saveName) + ".xml");
        }

        public bool Save(string sceneTitle, string partUid, string partName, int cutIndex,
                          string saveName, string saveFolder = "")
        {
            if (string.IsNullOrWhiteSpace(saveName))
            {
                _log.LogWarning("save 的 value 为空，已忽略。");
                return false;
            }

            if (!_bridge.TrySnapshot(out var globals, out var arrays))
                return false;

            string effectiveFolder = string.IsNullOrEmpty(saveFolder) ? sceneTitle : saveFolder;

            var file = new SaveFile
            {
                timestampUtc = DateTime.UtcNow.ToString("O"),
                adv = new AdvInfo
                {
                    sceneTitle = sceneTitle ?? "",
                    saveFolder = saveFolder ?? "",
                    partUID = partUid ?? "",
                    partName = partName ?? "",
                    cutIndex = cutIndex
                },
                globals = globals,
                arrays = arrays
            };

            string dir = GetBaseDir(effectiveFolder);
            string path = GetSavePath(effectiveFolder, saveName);

            try
            {
                Directory.CreateDirectory(dir);

                var serializer = new XmlSerializer(typeof(SaveFile));
                using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
                {
                    serializer.Serialize(writer, file);
                }

                _log.LogInfo($"[EC_VariableMod_SaveLoad] 存档成功: {path}");
                return true;
            }
            catch (Exception ex)
            {
                _log.LogError($"[EC_VariableMod_SaveLoad] 存档失败: {path} : {ex}");
                return false;
            }
        }

        public bool Load(string sceneTitle, string saveName, out SaveFile loaded, string saveFolder = "")
        {
            loaded = null;

            if (string.IsNullOrWhiteSpace(saveName))
            {
                _log.LogWarning("load 的 value 为空，已忽略。");
                return false;
            }

            string effectiveFolder = string.IsNullOrEmpty(saveFolder) ? sceneTitle : saveFolder;
            string path = GetSavePath(effectiveFolder, saveName);
            if (!File.Exists(path))
            {
                _log.LogWarning($"[EC_VariableMod_SaveLoad] 读档文件不存在: {path}");
                return false;
            }

            try
            {
                var serializer = new XmlSerializer(typeof(SaveFile));
                using (var reader = new StreamReader(path, Encoding.UTF8))
                {
                    loaded = (SaveFile)serializer.Deserialize(reader);
                }

                if (loaded == null)
                {
                    _log.LogError($"[EC_VariableMod_SaveLoad] 读档解析失败: {path}");
                    return false;
                }

                if (!_bridge.TryRestore(loaded.globals, loaded.arrays))
                {
                    _log.LogError("[EC_VariableMod_SaveLoad] 变量恢复失败。");
                    return false;
                }

                _log.LogInfo($"[EC_VariableMod_SaveLoad] 读档成功: {path}");

                // 把存档的日期写入 VariableMod 全局变量，供 #SAVE_{saveName}_DATE# 在文本框显示
                SetSaveMetaVariables(saveName, loaded);

                return true;
            }
            catch (Exception ex)
            {
                _log.LogError($"[EC_VariableMod_SaveLoad] 读档失败: {path} : {ex}");
                return false;
            }
        }

        public bool TryPrepareJump(SaveFile loaded, string currentPartUid, string currentSceneTitle,
                                    out string targetPartUid, out int targetCutIndex)
        {
            targetPartUid = "";
            targetCutIndex = -1;

            if (loaded?.adv == null)
                return false;

            // 跨场景存档不支持跳转
            if (!string.IsNullOrEmpty(loaded.adv.sceneTitle) && loaded.adv.sceneTitle != currentSceneTitle)
            {
                _log.LogMessage("[EC_VariableMod_SaveLoad] 跨场景存档不支持跳转（存档来自其他场景）。");
                return false;
            }

            if (string.IsNullOrEmpty(loaded.adv.partUID))
            {
                _log.LogWarning("[EC_VariableMod_SaveLoad] 存档内 partUID 为空，无法跳转。");
                return false;
            }

            targetPartUid = loaded.adv.partUID;
            targetCutIndex = loaded.adv.cutIndex;

            if (!string.IsNullOrEmpty(currentPartUid) && targetPartUid == currentPartUid)
            {
                _log.LogDebug($"[EC_VariableMod_SaveLoad] 准备跳转到当前 part 内的 cut {targetCutIndex}（partUID 设为空）");
                targetPartUid = "";
            }
            else
            {
                _log.LogDebug($"[EC_VariableMod_SaveLoad] 准备跳转到其他 part: {targetPartUid}, cut {targetCutIndex}");
            }

            return true;
        }

        public bool CommitJump(string partUid, int cutIndex)
        {
            return _bridge.TrySetJump(partUid, cutIndex);
        }

        /// <summary>
        /// 读档成功后，将存档元信息（时间戳等）写入 VariableMod 全局变量，
        /// 用户可通过 #SAVE_{saveName}_DATE# 等语法在文本框中显示。
        /// 注意：#变量#替换由 VariableMod 在 LoadCut 前完成，
        /// 所以日期变量只能在 load 之后的文本框中生效。
        /// </summary>
        private void SetSaveMetaVariables(string saveName, SaveFile loaded)
        {
            if (string.IsNullOrEmpty(saveName) || loaded == null)
                return;

            // 写入存档日期到 VariableMod 全局变量
            string dateFormatted = FormatTimestamp(loaded.timestampUtc);
            if (dateFormatted != null)
            {
                _bridge.TrySetGlobalVar($"#SAVE_{saveName}_DATE#", dateFormatted);
                _log.LogDebug($"[EC_VariableMod_SaveLoad] 已注入存档日期变量: #SAVE_{saveName}_DATE# = {dateFormatted}");
            }
        }

        /// <summary>
        /// 将 ISO 8601 UTC 时间戳转为本地可读格式。
        /// 格式：yyyy/MM/dd HH:mm:ss
        /// 解析失败返回 null。
        /// </summary>
        internal static string FormatTimestamp(string timestampUtc)
        {
            if (string.IsNullOrEmpty(timestampUtc))
                return null;

            try
            {
                if (DateTime.TryParse(timestampUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                {
                    return dt.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss");
                }
            }
            catch
            {
                // 解析失败，放弃注入，避免写入无效日期
            }

            return null;
        }

        /// <summary>
        /// 扫描当前场景所有存档文件（*.xml），只解析时间戳并注入 VariableMod 全局变量。
        /// 变量名格式：#SAVE_{存档名}_DATE#，值格式：yyyy/MM/dd HH:mm:ss
        /// 与 Load 不同，此方法不恢复任何游戏变量，不触发跳转。
        /// 适用于场景开头调用一次，让后续文本框都能引用存档日期。
        /// 返回成功注入的变量数。
        /// </summary>
        public int ScanAndInjectSaveDates(string sceneTitle, string saveFolder = "")
        {
            string effectiveFolder = string.IsNullOrEmpty(saveFolder) ? sceneTitle : saveFolder;
            string dir = GetBaseDir(effectiveFolder);
            if (!Directory.Exists(dir))
            {
                _log.LogDebug($"[EC_VariableMod_SaveLoad] 存档目录不存在，跳过扫描: {dir}");
                return 0;
            }

            int injected = 0;
            foreach (string filePath in Directory.GetFiles(dir, "*.xml"))
            {
                string saveName = Path.GetFileNameWithoutExtension(filePath);
                if (string.IsNullOrEmpty(saveName))
                    continue;

                try
                {
                    var doc = XDocument.Load(filePath);
                    string timestampUtc = doc.Root?.Element("timestampUtc")?.Value;
                    string dateFormatted = FormatTimestamp(timestampUtc);
                    if (dateFormatted != null)
                    {
                        string varName = $"#SAVE_{saveName}_DATE#";
                        if (_bridge.TrySetGlobalVar(varName, dateFormatted))
                        {
                            injected++;
                            _log.LogDebug($"[EC_VariableMod_SaveLoad] 扫描注入: {varName} = {dateFormatted}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning($"[EC_VariableMod_SaveLoad] 扫描存档文件失败，已跳过: {filePath} : {ex.Message}");
                }
            }

            _log.LogInfo($"[EC_VariableMod_SaveLoad] 存档日期扫描完成，共注入 {injected} 个变量（场景: {sceneTitle}）");
            return injected;
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
