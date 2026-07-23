using System;
using System.Collections.Generic;
using YS_Node;

namespace EC_NodeSidePanel
{
    // 嵌套文件夹：仅插件 XML 持久化，不写游戏存档。
    internal sealed class FolderNode
    {
        internal string Id;
        internal string Name;
        internal string ParentId; // null/empty = root
        internal bool Expanded = true;
        internal readonly List<string> ChildFolderIds = new List<string>();
        internal readonly List<string> NodeUids = new List<string>();

        // 排序缓存（按当前 SortMode）
        internal readonly List<string> CachedSortedChildIds = new List<string>();
        internal readonly List<NodeUI> CachedSortedNodes = new List<NodeUI>();
    }

    internal sealed class FolderTreeService
    {
        internal readonly Dictionary<string, FolderNode> Folders = new Dictionary<string, FolderNode>();
        internal readonly Dictionary<string, string> NodeToFolder = new Dictionary<string, string>();
        internal readonly List<string> RootFolderIds = new List<string>();

        private readonly List<string> _cachedSortedRoots = new List<string>();
        private bool _structureDirty = true;

        internal void MarkDirty() => _structureDirty = true;

        internal void Clear()
        {
            Folders.Clear();
            NodeToFolder.Clear();
            RootFolderIds.Clear();
            _cachedSortedRoots.Clear();
            _structureDirty = true;
        }

        internal FolderNode CreateFolder(string name, string parentId)
        {
            var f = new FolderNode
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                Name = string.IsNullOrEmpty(name) ? "新建文件夹" : name,
                ParentId = string.IsNullOrEmpty(parentId) ? null : parentId,
            };
            Folders[f.Id] = f;
            if (string.IsNullOrEmpty(f.ParentId))
                RootFolderIds.Add(f.Id);
            else if (Folders.TryGetValue(f.ParentId, out var parent))
                parent.ChildFolderIds.Add(f.Id);
            _structureDirty = true;
            return f;
        }

        internal void Rename(string id, string name)
        {
            if (Folders.TryGetValue(id, out var f) && !string.IsNullOrEmpty(name))
            {
                f.Name = name;
                _structureDirty = true;
            }
        }

        internal void DeleteFolder(string id)
        {
            if (!Folders.TryGetValue(id, out var f))
                return;

            foreach (var childId in new List<string>(f.ChildFolderIds))
                DeleteFolder(childId);

            foreach (var uid in new List<string>(f.NodeUids))
                NodeToFolder.Remove(uid);

            if (!string.IsNullOrEmpty(f.ParentId) && Folders.TryGetValue(f.ParentId, out var parent))
                parent.ChildFolderIds.Remove(id);
            else
                RootFolderIds.Remove(id);

            Folders.Remove(id);
            _structureDirty = true;
        }

        internal void MoveNodesIn(IEnumerable<string> uids, string folderId)
        {
            if (string.IsNullOrEmpty(folderId) || !Folders.ContainsKey(folderId))
                return;
            var folder = Folders[folderId];
            foreach (var uid in uids)
            {
                if (string.IsNullOrEmpty(uid) || uid == "node_start" || uid == "node_end")
                    continue;
                if (NodeToFolder.TryGetValue(uid, out var oldId) && Folders.TryGetValue(oldId, out var old))
                    old.NodeUids.Remove(uid);
                NodeToFolder[uid] = folderId;
                if (!folder.NodeUids.Contains(uid))
                    folder.NodeUids.Add(uid);
            }
            _structureDirty = true;
        }

        internal void MoveNodesOut(IEnumerable<string> uids)
        {
            bool any = false;
            foreach (var uid in uids)
            {
                if (!NodeToFolder.TryGetValue(uid, out var oldId))
                    continue;
                if (Folders.TryGetValue(oldId, out var old))
                    old.NodeUids.Remove(uid);
                NodeToFolder.Remove(uid);
                any = true;
            }
            if (any)
                _structureDirty = true;
        }

        internal IEnumerable<string> CollectFolderNodeUids(string folderId)
        {
            if (!Folders.TryGetValue(folderId, out var f))
                yield break;
            foreach (var uid in f.NodeUids)
                yield return uid;
            foreach (var childId in f.ChildFolderIds)
                foreach (var uid in CollectFolderNodeUids(childId))
                    yield return uid;
        }

        // 脏时重建所有排序缓存；干净时直接读缓存
        internal void RebuildCachesIfNeeded(NodeListModel list, NodeControl control)
        {
            if (!_structureDirty && !list.ListDirty)
                return;
            if (control?.dictNode == null)
                return;

            SortIdsInto(_cachedSortedRoots, RootFolderIds, list);
            foreach (var f in Folders.Values)
            {
                SortIdsInto(f.CachedSortedChildIds, f.ChildFolderIds, list);
                f.CachedSortedNodes.Clear();
                foreach (var uid in f.NodeUids)
                {
                    if (control.dictNode.TryGetValue(uid, out var n) && n != null)
                        f.CachedSortedNodes.Add(n);
                }
                f.CachedSortedNodes.Sort(list.CompareNodes);
            }
            _structureDirty = false;
        }

        internal IReadOnlyList<string> SortedRootIds(NodeListModel list, NodeControl control)
        {
            RebuildCachesIfNeeded(list, control);
            return _cachedSortedRoots;
        }

        internal IReadOnlyList<string> SortedChildIds(FolderNode folder, NodeListModel list, NodeControl control)
        {
            RebuildCachesIfNeeded(list, control);
            return folder.CachedSortedChildIds;
        }

        internal IReadOnlyList<NodeUI> SortedFolderNodes(FolderNode folder, NodeListModel list, NodeControl control)
        {
            RebuildCachesIfNeeded(list, control);
            return folder.CachedSortedNodes;
        }

        private void SortIdsInto(List<string> dst, List<string> src, NodeListModel list)
        {
            dst.Clear();
            foreach (var id in src)
            {
                if (Folders.ContainsKey(id))
                    dst.Add(id);
            }
            if (list.SortMode == SortMode.Name)
            {
                dst.Sort((a, b) =>
                {
                    int c = string.Compare(Folders[a].Name, Folders[b].Name, StringComparison.OrdinalIgnoreCase);
                    return list.Ascending ? c : -c;
                });
            }
            else if (!list.Ascending)
            {
                dst.Reverse();
            }
        }

        // 清理已删除节点归属（仅在脏时由调用方触发）
        internal void PruneMissing(NodeControl control)
        {
            if (control?.dictNode == null)
                return;
            var dead = new List<string>();
            foreach (var uid in NodeToFolder.Keys)
            {
                if (!control.dictNode.ContainsKey(uid))
                    dead.Add(uid);
            }
            if (dead.Count > 0)
                MoveNodesOut(dead);
            foreach (var f in Folders.Values)
            {
                int before = f.NodeUids.Count;
                f.NodeUids.RemoveAll(uid => !control.dictNode.ContainsKey(uid));
                if (f.NodeUids.Count != before)
                    _structureDirty = true;
            }
        }

        internal bool ExpandAncestorsOfNode(string uid)
        {
            if (string.IsNullOrEmpty(uid) || !NodeToFolder.TryGetValue(uid, out var folderId))
                return false;

            bool changed = false;
            string cur = folderId;
            while (!string.IsNullOrEmpty(cur) && Folders.TryGetValue(cur, out var f))
            {
                if (!f.Expanded)
                {
                    f.Expanded = true;
                    changed = true;
                }
                cur = f.ParentId;
            }
            return changed;
        }
    }
}
