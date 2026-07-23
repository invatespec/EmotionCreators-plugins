using System;
using System.Collections.Generic;
using YS_Node;

namespace EC_NodeSidePanel
{
    internal enum SortMode
    {
        Created,
        Name,
    }

    // 列表状态：创建序、排序、高亮、勾选、搜索。
    // 性能：CreationOrder 用 HashSet/字典 O(1)；未分组列表仅脏时重建。
    internal sealed class NodeListModel
    {
        internal SortMode SortMode = SortMode.Created;
        internal bool Ascending = true;
        internal string SearchText = string.Empty;
        internal string HighlightUid;
        internal string HighlightFolderId;
        internal readonly HashSet<string> CheckedUids = new HashSet<string>();
        internal readonly List<string> CreationOrder = new List<string>();

        private readonly HashSet<string> _creationSet = new HashSet<string>();
        private readonly Dictionary<string, int> _creationIndex = new Dictionary<string, int>();
        private int _searchMatchIndex = -1;

        // 脏标记：节点增删 / 排序规则 / 文件夹变更后置 true
        internal bool ListDirty = true;

        private readonly List<NodeUI> _cachedUngrouped = new List<NodeUI>();
        private int _cachedDictCount = -1;

        internal void MarkDirty() => ListDirty = true;

        internal void EnsureCreationOrder(NodeControl control)
        {
            if (control?.dictNode == null)
                return;

            int dictCount = control.dictNode.Count;
            if (!ListDirty && dictCount == _cachedDictCount && dictCount == CreationOrder.Count)
                return;

            bool changed = false;
            foreach (var kv in control.dictNode)
            {
                string uid = kv.Key;
                if (string.IsNullOrEmpty(uid))
                    continue;
                if (_creationSet.Add(uid))
                {
                    CreationOrder.Add(uid);
                    changed = true;
                }
            }

            for (int i = CreationOrder.Count - 1; i >= 0; i--)
            {
                string uid = CreationOrder[i];
                if (!control.dictNode.ContainsKey(uid))
                {
                    CreationOrder.RemoveAt(i);
                    _creationSet.Remove(uid);
                    changed = true;
                }
            }

            if (changed)
                RebuildCreationIndex();

            _cachedDictCount = control.dictNode.Count;
        }

        internal void OnCreated(string uid)
        {
            if (string.IsNullOrEmpty(uid))
                return;
            if (_creationSet.Add(uid))
            {
                CreationOrder.Add(uid);
                _creationIndex[uid] = CreationOrder.Count - 1;
            }
            ListDirty = true;
        }

        internal void OnDeleted(string uid)
        {
            if (_creationSet.Remove(uid))
            {
                CreationOrder.Remove(uid);
                RebuildCreationIndex();
            }
            CheckedUids.Remove(uid);
            if (HighlightUid == uid)
                HighlightUid = null;
            ListDirty = true;
        }

        internal void RebuildCreationIndex()
        {
            _creationIndex.Clear();
            for (int i = 0; i < CreationOrder.Count; i++)
                _creationIndex[CreationOrder[i]] = i;
        }

        // XML 加载后重建索引（保序去重）
        internal void SyncCreationStructuresFromOrder()
        {
            _creationSet.Clear();
            var deduped = new List<string>(CreationOrder.Count);
            for (int i = 0; i < CreationOrder.Count; i++)
            {
                string uid = CreationOrder[i];
                if (string.IsNullOrEmpty(uid) || !_creationSet.Add(uid))
                    continue;
                deduped.Add(uid);
            }
            CreationOrder.Clear();
            CreationOrder.AddRange(deduped);
            RebuildCreationIndex();
            _cachedDictCount = -1;
            ListDirty = true;
        }

        internal IReadOnlyList<NodeUI> GetUngroupedUserNodes(NodeControl control, FolderTreeService folders)
        {
            if (control?.dictNode == null)
            {
                _cachedUngrouped.Clear();
                return _cachedUngrouped;
            }

            // ListDirty=false 且 dict 数量未变 → 用缓存（由 Update 脏路径保证缓存已刷）
            if (!ListDirty && _cachedDictCount == control.dictNode.Count)
                return _cachedUngrouped;

            _cachedUngrouped.Clear();
            foreach (var kv in control.dictNode)
            {
                var n = kv.Value;
                if (n?.nodeBase == null)
                    continue;
                string uid = n.nodeBase.uid;
                if (uid == "node_start" || uid == "node_end")
                    continue;
                if (folders != null && folders.NodeToFolder.ContainsKey(uid))
                    continue;
                _cachedUngrouped.Add(n);
            }
            _cachedUngrouped.Sort(CompareNodes);
            _cachedDictCount = control.dictNode.Count;
            return _cachedUngrouped;
        }

        internal IEnumerable<NodeUI> EnumerateSystemNodes(NodeControl control)
        {
            if (control?.dictNode == null)
                yield break;
            NodeUI start, end;
            if (control.dictNode.TryGetValue("node_start", out start) && start != null)
                yield return start;
            if (control.dictNode.TryGetValue("node_end", out end) && end != null)
                yield return end;
        }

        internal int CompareNodes(NodeUI a, NodeUI b)
        {
            int c;
            if (SortMode == SortMode.Name)
            {
                string na = a.nodeBase?.name ?? string.Empty;
                string nb = b.nodeBase?.name ?? string.Empty;
                c = string.Compare(na, nb, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                int ia, ib;
                if (a.nodeBase == null || !_creationIndex.TryGetValue(a.nodeBase.uid, out ia))
                    ia = int.MaxValue;
                if (b.nodeBase == null || !_creationIndex.TryGetValue(b.nodeBase.uid, out ib))
                    ib = int.MaxValue;
                c = ia.CompareTo(ib);
            }
            return Ascending ? c : -c;
        }

        internal List<NodeUI> GetSearchMatches(NodeControl control)
        {
            var result = new List<NodeUI>();
            if (control?.dictNode == null || string.IsNullOrEmpty(SearchText))
                return result;

            string q = SearchText;
            foreach (var kv in control.dictNode)
            {
                var n = kv.Value;
                if (n?.nodeBase == null)
                    continue;
                string name = n.nodeBase.name ?? string.Empty;
                if (name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                    result.Add(n);
            }
            result.Sort(CompareNodes);
            return result;
        }

        internal NodeUI StepSearch(NodeControl control, int dir)
        {
            var matches = GetSearchMatches(control);
            if (matches.Count == 0)
                return null;
            if (_searchMatchIndex < 0 || _searchMatchIndex >= matches.Count)
                _searchMatchIndex = dir > 0 ? -1 : 0;
            _searchMatchIndex = (_searchMatchIndex + dir + matches.Count) % matches.Count;
            var node = matches[_searchMatchIndex];
            HighlightUid = node.nodeBase.uid;
            HighlightFolderId = null;
            return node;
        }

        internal void ResetSearchCursor()
        {
            _searchMatchIndex = -1;
        }
    }
}
