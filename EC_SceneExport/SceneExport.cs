// EC_SceneExport v1.1 — BepInEx 5 plugin
//
// 快捷键配置在 BepInEx\config\com.monophony.bepinex.sceneexport.cfg 中

using BepInEx;
using BepInEx.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

using HEdit;
using YS_Node;

namespace EC_SceneExport
{
    [BepInPlugin(GUID, PluginName, Version)]
    public class SceneExport : BaseUnityPlugin
    {
        public const string PluginNameInternal = "EC_SceneExport";
        public const string GUID = "com.monophony.bepinex.sceneexport";
        public const string PluginName = "Scene Export";
        public const string Version = "1.1";
        public static string ExportPath;

        // partファイル識別用
        private const string MAGIC = "ECP1";
        public static ConfigEntry<KeyboardShortcut> PartsExportHotkey { get; private set; }
        public static ConfigEntry<KeyboardShortcut> PartsImportHotkey { get; private set; }
        public static ConfigEntry<bool> EnableCharaRemove { get; private set; }
        private const int PART_KIND_H = 0;
        private const int PART_KIND_ADV = 1;
        private const string FileExtension = "part";

        // 连接信息文件标识
        private const string MAGIC_LINK = "ECLK";
        private const int LINK_VERSION = 1;
        private const string LinkFileExtension = "scnlink";

        private const String SceneName_HEditScene = "HEditScene";

        // Used by the Unity Engine Scripting API
        internal void Awake()
        {
            ExportPath = Path.Combine(Paths.GameRootPath, "UserData\\SceneExport");
            Logger.LogDebug("Awake, ExportPath=" + ExportPath);

            // 在 Awake 中绑定配置项确保 Start/Update 之前可用
            PartsExportHotkey = Config.Bind("Config", "Export Parts", new KeyboardShortcut(KeyCode.E, new KeyCode[] { KeyCode.LeftAlt }), "Export all currently loaded parts in the game.");
            PartsImportHotkey = Config.Bind("Config", "Import Parts", new KeyboardShortcut(KeyCode.I, new KeyCode[] { KeyCode.LeftAlt }), "Import all files in the exported folder.");
            EnableCharaRemove = Config.Bind("Config", "Enable chara remove (Experimental)", false, "If the importing ADV part over characters, delete the characters.");

            Logger.LogDebug("Awake");

            SceneManager.sceneUnloaded += (_scene) =>
            {
                Logger.LogDebug("unloaded:" + _scene.name);
                if (_scene.name == SceneName_HEditScene)
                {
                    // プラグイン無効
                    this.enabled = false;
                }
            };

            SceneManager.sceneLoaded += (_scene, _mode) =>
            {
                Logger.LogDebug("loaded:" + _scene.name);
                if (_scene.name == SceneName_HEditScene)
                {
                    // プラグイン有効
                    this.enabled = true;
                }
            };
        }

        // Used by the Unity Engine Scripting API
        internal void Start()
        {
            // プラグイン無効
            this.enabled = false;
        }

        // Used by the Unity Engine Scripting API
        internal void Update()
        {

            if (PartsExportHotkey.Value.IsDown())
                this.SafeAction(ExportParts);
            if (PartsImportHotkey.Value.IsDown())
                this.SafeAction(ImportParts);
        }

        private void SafeAction(Action _action)
        {
            try
            {
                _action();
            }
            catch (Exception ex)
            {
                Logger.Log(BepInEx.Logging.LogLevel.Message, "Error: " + ex.Message);
                Illusion.Game.Utils.Sound.Play(Illusion.Game.SystemSE.cancel);
                throw;
            }
        }

        /// <summary>
        /// Exports all currently loaded characters. Probably wont export characters that have not been loaded yet, like characters in a different classroom.
        /// </summary>
        private void ExportParts()
        {
            Logger.LogDebug("Start export, ExportPath=" + ExportPath);

            var node = GameObject.FindObjectOfType<HEdit.NodeSettingCanvas>();
            if (node == null) return;

            Logger.LogDebug("ExportPath length=" + (ExportPath ?? "").Length + " chars");

            if (!Directory.Exists(ExportPath))
            {
                Directory.CreateDirectory(ExportPath);
            }

            // 统计总数（排除 start/end 节点）
            int totalParts = 0;
            foreach (KeyValuePair<string, BasePart> kvp in HEditData.Instance.nodes)
            {
                if (kvp.Value.kind == PART_KIND_H || kvp.Value.kind == PART_KIND_ADV)
                    totalParts++;
            }

            // 收集本次导出的 UUID，供连接导出使用
            HashSet<string> exportedUids = new HashSet<string>();

            // ADVPartのデータをセーブ
            int partCount = 0;
            foreach (KeyValuePair<string, BasePart> keyValuePair in HEditData.Instance.nodes)
            {
                if ((keyValuePair.Value.kind != PART_KIND_H) && (keyValuePair.Value.kind != PART_KIND_ADV))
                {
                    continue;
                }

                // パートの名前を取得
                NodeUI _nodeUI = HEdit.HEditGlobal.Instance.nodeControl.dictNode[keyValuePair.Value.uuId];
                string partName = _nodeUI.nodeBase.name;

                // Kindを文字列にする
                string strKind = (keyValuePair.Value.kind == PART_KIND_ADV)? "ADV" : "H";

                string fileName = HEditData.Instance.info.title + "_" + partCount + "_" + strKind + "_" + partName + "." + FileExtension;
                string fullPath = Path.Combine(ExportPath, fileName);

                Logger.LogDebug(fileName);

                partCount++;

                using (FileStream fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write))
                {
                    using (BinaryWriter binaryWriter = new BinaryWriter(fileStream))
                    {
                        //マジックコード
                        binaryWriter.Write(MAGIC);
                        // 名前
                        binaryWriter.Write(partName);

                        // ここから【EroMakeHScene】の一部と同じ
                        // ADV == 1 or H == 0
                        binaryWriter.Write(keyValuePair.Value.kind);
                        // キーを書き込み
                        binaryWriter.Write(keyValuePair.Key);

                        keyValuePair.Value.Save(binaryWriter);
                        Logger.Log(BepInEx.Logging.LogLevel.Message, "Exported " + fullPath);
                    }
                }

                exportedUids.Add(keyValuePair.Value.uuId);
            }

            // 日志：总数 vs 已导出数
            Logger.Log(BepInEx.Logging.LogLevel.Message,
                       "Export done: " + partCount + "/" + totalParts + " Parts");

            // 导出连接信息
            if (exportedUids.Count > 0)
            {
                ExportNodeLinks(exportedUids);
            }

            Illusion.Game.Utils.Sound.Play(Illusion.Game.SystemSE.ok_s);
        }

        /// <summary>
        /// 导出所有 Part 之间的连接信息到 .scnlink 文件
        /// </summary>
        /// <param name="exportedUids">本次导出的 Part UUID 集合</param>
        private void ExportNodeLinks(HashSet<string> exportedUids)
        {
            var nodeControl = HEdit.HEditGlobal.Instance.nodeControl;
            if (nodeControl == null || nodeControl.dictNode == null) return;

            string fileName = HEditData.Instance.info.title + "_connections_" +
                              DateTime.Now.ToString("yyyyMMdd_HHmmss") + "." + LinkFileExtension;
            string fullPath = Path.Combine(ExportPath, fileName);

            int linkCount = 0;
            using (FileStream fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write))
            {
                using (BinaryWriter bw = new BinaryWriter(fileStream))
                {
                    bw.Write(MAGIC_LINK);
                    bw.Write(LINK_VERSION);

                    // 统计需要写入的节点数（跳过 start/end，只写本次导出的节点）
                    long countPos = bw.BaseStream.Position;
                    bw.Write(0); // 占位，稍后回填

                    foreach (KeyValuePair<string, NodeUI> kvp in nodeControl.dictNode)
                    {
                        string uid = kvp.Key;
                        if (uid == "node_start" || uid == "node_end") continue;
                        if (!exportedUids.Contains(uid)) continue;

                        NodeBase nb = kvp.Value.nodeBase;
                        if (nb == null) continue;

                        bw.Write(uid);                              // oldUID
                        bw.Write((int)nb.kind);                     // nodeKind
                        bw.Write(nb.pos.x);                         // nodePosX
                        bw.Write(nb.pos.y);                         // nodePosY
                        for (int i = 0; i < 5; i++)
                            bw.Write(nb.childUID[i] ?? "");         // childUID[5]
                        for (int i = 0; i < 5; i++)
                            bw.Write((int)nb.endConditionType[i]);  // endCondition[5]
                        for (int i = 0; i < 5; i++)
                            bw.Write(nb.endConditionCount[i]);      // endConditionCount[5]
                        for (int i = 0; i < 5; i++)
                            bw.Write(nb.fadeType[i]);               // fadeType[5]
                        for (int i = 0; i < 5; i++)
                            bw.Write(nb.fadeTime[i]);               // fadeTime[5]

                        linkCount++;
                    }

                    // 回填节点数
                    long endPos = bw.BaseStream.Position;
                    bw.BaseStream.Seek(countPos, SeekOrigin.Begin);
                    bw.Write(linkCount);
                    bw.BaseStream.Seek(endPos, SeekOrigin.Begin);
                }
            }

            Logger.Log(BepInEx.Logging.LogLevel.Message,
                       "Exported links: " + linkCount + " nodes → " + fileName);
        }

        /// <summary>
        /// 查找最新的 .scnlink 文件（按修改时间降序）
        /// </summary>
        private FileInfo FindLatestLinkFile()
        {
            DirectoryInfo di = new DirectoryInfo(ExportPath);
            FileInfo[] files = di.GetFiles("*." + LinkFileExtension, SearchOption.TopDirectoryOnly);
            if (files.Length == 0) return null;

            FileInfo latest = files[0];
            for (int i = 1; i < files.Length; i++)
            {
                if (files[i].LastWriteTime > latest.LastWriteTime)
                    latest = files[i];
            }
            return latest;
        }

        /// <summary>
        /// 读取 .scnlink 文件并恢复 Part 之间的连接
        /// </summary>
        /// <param name="uidMap">旧UUID → 新UUID 映射表</param>
        private void ImportNodeLinks(Dictionary<string, string> uidMap)
        {
            FileInfo linkFile = FindLatestLinkFile();
            if (linkFile == null)
            {
                Logger.LogDebug("No connection file found, skip link restore");
                return;
            }

            var nodeControl = HEdit.HEditGlobal.Instance.nodeControl;
            if (nodeControl == null || nodeControl.dictNode == null) return;

            int restoredNodes = 0;
            int restoredLinks = 0;

            using (FileStream fileStream = new FileStream(linkFile.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                using (BinaryReader br = new BinaryReader(fileStream))
                {
                    string magic = br.ReadString();
                    if (magic != MAGIC_LINK)
                    {
                        Logger.Log(BepInEx.Logging.LogLevel.Message,
                                   "Error: Invalid link file format: " + linkFile.FullName);
                        return;
                    }

                    int version = br.ReadInt32();
                    int partCount = br.ReadInt32();

                    for (int n = 0; n < partCount; n++)
                    {
                        string oldUID = br.ReadString();
                        int nodeKind = br.ReadInt32();
                        float posX = br.ReadSingle();
                        float posY = br.ReadSingle();

                        string[] childUIDs = new string[5];
                        EndConditionType[] endConditions = new EndConditionType[5];
                        int[] endCounts = new int[5];
                        int[] fadeTypes = new int[5];
                        float[] fadeTimes = new float[5];

                        for (int i = 0; i < 5; i++)
                            childUIDs[i] = br.ReadString();
                        for (int i = 0; i < 5; i++)
                            endConditions[i] = (EndConditionType)br.ReadInt32();
                        for (int i = 0; i < 5; i++)
                            endCounts[i] = br.ReadInt32();
                        for (int i = 0; i < 5; i++)
                            fadeTypes[i] = br.ReadInt32();
                        for (int i = 0; i < 5; i++)
                            fadeTimes[i] = br.ReadSingle();

                        // 映射旧 UUID → 新 UUID
                        string newUID;
                        if (!uidMap.TryGetValue(oldUID, out newUID))
                        {
                            Logger.LogDebug("Skip link node: " + oldUID + " not in imported parts");
                            continue;
                        }

                        NodeUI nodeUI;
                        if (!nodeControl.dictNode.TryGetValue(newUID, out nodeUI))
                        {
                            Logger.LogDebug("Skip link node: " + newUID + " not found in dictNode");
                            continue;
                        }

                        NodeBase nb = nodeUI.nodeBase;

                        // 恢复位置和条件数据
                        nb.pos = new Vector2(posX, posY);
                        for (int i = 0; i < 5; i++)
                            nb.endConditionType[i] = endConditions[i];
                        for (int i = 0; i < 5; i++)
                            nb.endConditionCount[i] = endCounts[i];
                        for (int i = 0; i < 5; i++)
                            nb.fadeType[i] = fadeTypes[i];
                        for (int i = 0; i < 5; i++)
                            nb.fadeTime[i] = fadeTimes[i];

                        // 重映射 childUID 并创建连线
                        for (int i = 0; i < 5; i++)
                        {
                            string oldChildUID = childUIDs[i];
                            if (string.IsNullOrEmpty(oldChildUID))
                            {
                                nb.childUID[i] = "";
                                continue;
                            }

                            if (oldChildUID == "node_end")
                            {
                                nb.childUID[i] = "node_end";
                            }
                            else
                            {
                                string newChildUID;
                                if (uidMap.TryGetValue(oldChildUID, out newChildUID))
                                {
                                    nb.childUID[i] = newChildUID;
                                }
                                else
                                {
                                    nb.childUID[i] = "";
                                    Logger.Log(BepInEx.Logging.LogLevel.Debug,
                                               "Link target not imported: " + oldChildUID + ", skip");
                                    continue;
                                }
                            }

                            nodeUI.CreateConnectLine(i, nb.childUID[i]);
                            restoredLinks++;
                        }

                        nodeUI.UpdateNodePosition();
                        restoredNodes++;
                    }
                }
            }

            Logger.Log(BepInEx.Logging.LogLevel.Message,
                       "Restored links: " + restoredLinks + " lines, " + restoredNodes + " nodes");
        }

        /// <summary>
        /// パートをインポートします。
        /// </summary>
        private void ImportParts()
        {
            Logger.LogDebug("Start import, ExportPath=" + ExportPath);

            var node = GameObject.FindObjectOfType<HEdit.NodeSettingCanvas>();
            if (node == null) return;

            if (!Directory.Exists(ExportPath))
            {
                Logger.LogDebug("No files");
                // フォルダなし
                return;
            }

            DirectoryInfo di = new System.IO.DirectoryInfo(ExportPath);
            FileInfo[] files =
                di.GetFiles("*." + FileExtension, System.IO.SearchOption.TopDirectoryOnly);

            // 旧UUID → 新UUID 映射表
            Dictionary<string, string> uidMap = new Dictionary<string, string>();

            foreach (System.IO.FileInfo f in files)
            {
                BasePart aPart;
                string partTitle;

                // ADVPartにデータをロード
                using (FileStream fileStream = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    using (BinaryReader binaryReader = new BinaryReader(fileStream))
                    {
                        if (this.ReadPart(binaryReader, f, out aPart, out partTitle) == false)
                        {
                            continue;
                        }
                    }
                }

                // 记录旧 UUID（Part 中保存的原始 uuId）
                string oldUID = aPart.uuId;

                // ノードを作成
                NodeBase aNode = HEdit.HEditGlobal.Instance.nodeControl.Create((NodeKind)aPart.kind, 0);

                // パートのIDをノード（画面上の箱）のIDに一致させる
                aPart.uuId = aNode.uid;

                aNode.name = partTitle;
                // パートのテーブルに追加
                HEdit.HEditData.Instance.nodes.Add(aNode.uid, aPart);

                // NodeUIを取得してタイトルを更新
                HEdit.HEditGlobal.Instance.nodeControl.dictNode[aPart.uuId].UpdateTitle();

                // 记录映射
                uidMap[oldUID] = aNode.uid;

                Logger.Log(BepInEx.Logging.LogLevel.Message, "Imported " + f.FullName);
            }

            // 恢复连接
            if (uidMap.Count > 0)
            {
                ImportNodeLinks(uidMap);
            }

            Illusion.Game.Utils.Sound.Play(Illusion.Game.SystemSE.ok_s);
        }
    
        private void CheckMap(HEdit.BasePart part)
        {
            Logger.LogDebug("Part mapID:" + part.useMapID);

            //マップID
            if (part.useMapID >= HEditData.Instance.maps.Count)
            {
                Logger.LogDebug("useMapID out of range. Set map id to 0");
                part.useMapID = 0;
            }
        }

        /// <summary>
        /// ADVパートのキャラ数を調整
        /// </summary>
        /// <param name="part"></param>
        private bool CheckADVPart(HEdit.ADVPart part, string partName)
        {
            Logger.LogDebug("check start");

            CheckMap(part);

            int charaNum = 0;

            // キャラチェック
            foreach (HEdit.ADVPart.Cut c in part.cuts)
            {
                int diff = HEditData.Instance.charas.Count - c.charStates.Count;

                if (diff == 0) continue;

                charaNum = c.charStates.Count;

                Logger.LogDebug("Chara num:" + charaNum);

                if (diff > 0)
                {
                    //キャラが不足しているので追加
                    for (int i = 0; i < diff; i++)
                    {
                        //非表示に設定して追加
                        HEdit.ADVPart.CharState cs = new HEdit.ADVPart.CharState();
                        cs.visible = false;
                        c.charStates.Add(cs);
                    }
                }
                else if (diff < 0)
                {
                    //キャラが多いので削除
                    if (EnableCharaRemove.Value)
                    {
                        c.charStates.RemoveRange(c.charStates.Count + diff, -diff);
                    }
                    else
                    {
                        //キャラが多い場合はエラーで抜ける
                        Logger.Log(BepInEx.Logging.LogLevel.Message, "Error: The number of charas (" + c.charStates.Count + ") is over in ADV part." + partName);
                        return false;
                    }
                }
            }

            if (charaNum != 0)
            {
                Logger.Log(BepInEx.Logging.LogLevel.Message, charaNum + " charactors in ADV part:" + partName);
            }

            return true;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="part"></param>
        /// <param name="partName"></param>
        /// <returns>パートに問題があるときはfalse</returns>
        private bool CheckHPart(HEdit.HPart part, string partName)
        {
            CheckMap(part);

            foreach (HEdit.HPart.Group g in part.groups)
            {
                foreach (var cs in g.infoCharas)
                {
                    if (cs.useCharaID >= HEditData.Instance.charas.Count)
                    {
                        Logger.Log(BepInEx.Logging.LogLevel.Message, "Error: Invalid charaID (" + cs.useCharaID + ") in H part." + partName);
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="br"></param>
        /// <param name="f"></param>
        /// <param name="aPart"></param>
        /// <param name="partTitle"></param>
        /// <returns></returns>
        private bool ReadPart(BinaryReader br, FileInfo f, out BasePart aPart, out string partTitle)
        {
            Logger.LogDebug("ReadPart " + f.FullName);

            aPart = null;
            partTitle = null;

            // マジック
            string tmpMagic = br.ReadString();
            if (tmpMagic != SceneExport.MAGIC)
            {
                Logger.Log(BepInEx.Logging.LogLevel.Message, "Error: Invalid file format:" + f.FullName);
                return false;
            }
            // 名前
            partTitle = br.ReadString();

            // Kind
            int kind = br.ReadInt32();

            //キー
            string sb_key = br.ReadString();

            if (kind == 0)
            {
                //H パート
                aPart = new HEdit.HPart();
                aPart.Load(br, HEditData.Instance.dataVersion);
                if (this.CheckHPart((HEdit.HPart)aPart, f.Name) == false)
                {
                    //読み込めないデータ
                    return false;
                }
            }
            else
            {
                //ADV パート
                aPart = new HEdit.ADVPart(0);
                aPart.Load(br, HEditData.Instance.dataVersion);

                if (this.CheckADVPart((HEdit.ADVPart)aPart, f.Name) == false)
                {
                    //読み込めないデータ
                    return false;
                }
            }

            return true;
        }
    }
}
