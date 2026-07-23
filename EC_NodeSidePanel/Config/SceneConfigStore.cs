using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using BepInEx.Logging;
using UnityEngine;

namespace EC_NodeSidePanel
{
    // SceneKey → UserData/edit/Otherscene/nodepanel/<key>/config.xml
    internal sealed class SceneConfigStore
    {
        private const string RelativeRoot = "UserData/edit/Otherscene/nodepanel";
        private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

        internal ManualLogSource Log;
        internal string ActiveKey;
        internal bool MemoryOnly;
        private bool _invalidKeyLogged;
        private bool _emptyKeyLogged;
        private float _dirtyAt = -1f;
        private const float DebounceSec = 0.5f;

        internal string ResolveKey(string sceneTitle)
        {
            string key = sceneTitle;

            if (string.IsNullOrWhiteSpace(key))
            {
                if (!_emptyKeyLogged)
                {
                    Log?.LogWarning("SceneKey empty — panel config stays in memory only.");
                    _emptyKeyLogged = true;
                }
                MemoryOnly = true;
                ActiveKey = null;
                return null;
            }

            key = key.Trim();
            if (!IsValidKey(key))
            {
                if (!_invalidKeyLogged)
                {
                    Log?.LogError($"SceneKey has invalid path chars, refuse XML I/O: '{key}'");
                    _invalidKeyLogged = true;
                }
                MemoryOnly = true;
                ActiveKey = null;
                return null;
            }

            MemoryOnly = false;
            _invalidKeyLogged = false;
            _emptyKeyLogged = false;
            ActiveKey = key;
            return key;
        }

        internal static bool IsValidKey(string key)
        {
            if (string.IsNullOrEmpty(key))
                return false;
            if (key.EndsWith(" ") || key.EndsWith("."))
                return false;
            foreach (char c in key)
            {
                if (Array.IndexOf(InvalidChars, c) >= 0)
                    return false;
                if (c == '\\' || c == '/' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|')
                    return false;
            }
            return true;
        }

        internal string ConfigPath(string key)
        {
            string root = Path.Combine(Application.dataPath, "..", RelativeRoot, key);
            return Path.GetFullPath(Path.Combine(root, "config.xml"));
        }

        internal void MarkDirty()
        {
            if (MemoryOnly || string.IsNullOrEmpty(ActiveKey))
                return;
            _dirtyAt = Time.realtimeSinceStartup;
        }

        internal void TickDebounced(NodeListModel list, FolderTreeService folders)
        {
            if (_dirtyAt < 0f)
                return;
            if (Time.realtimeSinceStartup - _dirtyAt < DebounceSec)
                return;
            Flush(list, folders);
        }

        internal void Flush(NodeListModel list, FolderTreeService folders)
        {
            _dirtyAt = -1f;
            if (MemoryOnly || string.IsNullOrEmpty(ActiveKey))
                return;
            try
            {
                Save(ActiveKey, list, folders);
            }
            catch (Exception ex)
            {
                Log?.LogError($"config save failed: {ex.Message}");
            }
        }

        internal void LoadInto(string key, NodeListModel list, FolderTreeService folders)
        {
            if (string.IsNullOrEmpty(key) || MemoryOnly)
                return;
            string path = ConfigPath(key);
            if (!File.Exists(path))
                return;
            try
            {
                var doc = XDocument.Load(path);
                var root = doc.Root;
                if (root == null)
                    return;

                var sort = root.Element("Sort");
                if (sort != null)
                {
                    list.SortMode = string.Equals((string)sort.Attribute("mode"), "name", StringComparison.OrdinalIgnoreCase)
                        ? SortMode.Name : SortMode.Created;
                    list.Ascending = !string.Equals((string)sort.Attribute("ascending"), "false", StringComparison.OrdinalIgnoreCase);
                }

                list.CreationOrder.Clear();
                var order = root.Element("CreationOrder");
                if (order != null)
                {
                    var seen = new HashSet<string>();
                    foreach (var u in order.Elements("Uid"))
                    {
                        string id = (string)u;
                        if (!string.IsNullOrEmpty(id) && seen.Add(id))
                            list.CreationOrder.Add(id);
                    }
                }
                list.SyncCreationStructuresFromOrder();

                folders.Clear();
                var foldersEl = root.Element("Folders");
                if (foldersEl != null)
                    LoadFolders(foldersEl, null, folders);
                folders.MarkDirty();
                list.MarkDirty();

                Log?.LogInfo($"loaded panel config: {path}");
            }
            catch (Exception ex)
            {
                Log?.LogError($"config load failed: {ex.Message}");
            }
        }

        private void LoadFolders(XElement parentEl, string parentId, FolderTreeService folders)
        {
            foreach (var fe in parentEl.Elements("Folder"))
            {
                string id = (string)fe.Attribute("id") ?? Guid.NewGuid().ToString("N").Substring(0, 8);
                string name = (string)fe.Attribute("name") ?? "文件夹";
                bool expanded = !string.Equals((string)fe.Attribute("expanded"), "false", StringComparison.OrdinalIgnoreCase);
                var f = new FolderNode { Id = id, Name = name, ParentId = parentId, Expanded = expanded };
                folders.Folders[id] = f;
                if (string.IsNullOrEmpty(parentId))
                    folders.RootFolderIds.Add(id);
                else if (folders.Folders.TryGetValue(parentId, out var p))
                    p.ChildFolderIds.Add(id);

                foreach (var ne in fe.Elements("Node"))
                {
                    string uid = (string)ne.Attribute("uid");
                    if (string.IsNullOrEmpty(uid) || uid == "node_start" || uid == "node_end")
                        continue;
                    f.NodeUids.Add(uid);
                    folders.NodeToFolder[uid] = id;
                }

                LoadFolders(fe, id, folders);
            }
        }

        private void Save(string key, NodeListModel list, FolderTreeService folders)
        {
            string path = ConfigPath(key);
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var root = new XElement("NodePanelConfig", new XAttribute("version", "1"));
            root.Add(new XElement("Sort",
                new XAttribute("mode", list.SortMode == SortMode.Name ? "name" : "created"),
                new XAttribute("ascending", list.Ascending ? "true" : "false")));

            var order = new XElement("CreationOrder");
            foreach (var uid in list.CreationOrder)
                order.Add(new XElement("Uid", uid));
            root.Add(order);

            var foldersEl = new XElement("Folders");
            foreach (var id in folders.RootFolderIds)
                if (folders.Folders.TryGetValue(id, out var f))
                    foldersEl.Add(WriteFolder(f, folders));
            root.Add(foldersEl);

            var doc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root);
            // 原子写：先 tmp 再替换，避免半截文件
            string tmp = path + ".tmp";
            doc.Save(tmp);
            if (File.Exists(path))
                File.Delete(path);
            File.Move(tmp, path);
        }

        private static XElement WriteFolder(FolderNode f, FolderTreeService folders)
        {
            var el = new XElement("Folder",
                new XAttribute("id", f.Id),
                new XAttribute("name", f.Name ?? string.Empty),
                new XAttribute("expanded", f.Expanded ? "true" : "false"));
            foreach (var uid in f.NodeUids)
                el.Add(new XElement("Node", new XAttribute("uid", uid)));
            foreach (var childId in f.ChildFolderIds)
                if (folders.Folders.TryGetValue(childId, out var child))
                    el.Add(WriteFolder(child, folders));
            return el;
        }
    }
}
