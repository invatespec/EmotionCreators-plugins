using System;
using System.Collections.Generic;
using UnityEngine;
using YS_Node;

namespace EC_NodeSidePanel
{
    // 一条连线的身份 = 发出它的节点 uid + 输出槽号
    internal struct LineKey : IEquatable<LineKey>
    {
        internal readonly string Uid;
        internal readonly int Idx;

        internal LineKey(string uid, int idx)
        {
            Uid = uid;
            Idx = idx;
        }

        public bool Equals(LineKey other) => Idx == other.Idx && string.Equals(Uid, other.Uid, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is LineKey k && Equals(k);
        public override int GetHashCode() => ((Uid != null ? Uid.GetHashCode() : 0) * 397) ^ Idx;
    }

    // 连线高亮状态机。
    // 只记录并还原「本插件染过」的线；原版会把线刷成红(划线删除)/绿(拖拽命中)/白，不去干涉。
    internal sealed class LineHighlightService
    {
        // 避开原版占用色：白=默认、红=划线删除、绿=拖拽命中
        private static readonly Color InColor = new Color32(0x3C, 0xD2, 0xF0, 0xFF);
        private static readonly Color OutColor = new Color32(0xFF, 0x8A, 0x1F, 0xFF);

        internal bool InMode;
        internal bool OutMode;
        internal bool AnyMode => InMode || OutMode;

        private HashSet<LineKey> _applied = new HashSet<LineKey>();
        private HashSet<LineKey> _next = new HashSet<LineKey>();
        private readonly HashSet<string> _targetUids = new HashSet<string>();
        private readonly List<LineKey> _outLines = new List<LineKey>();

        // 幂等：ChangeOutputLineColor 只是给 VectorLine.color 赋值，Draw() 不重置，重复赋同色零成本。
        // 由调用方按固定间隔驱动，顺带盖住选择变化 / 线重建 / 原版 OnPointerExit 刷白三种情况。
        internal void Sync(NodeControl control, IList<NodeUI> targets)
        {
            if (control?.dictNode == null || targets == null || targets.Count == 0)
            {
                Clear(control);
                return;
            }

            _next.Clear();
            _outLines.Clear();
            _targetUids.Clear();
            for (int i = 0; i < targets.Count; i++)
            {
                var t = targets[i];
                if (t?.nodeBase != null)
                    _targetUids.Add(t.nodeBase.uid);
            }

            if (OutMode)
                CollectOutLines(control, targets);
            if (InMode)
                CollectInLines(control);

            // 先还原本次不再命中的，再上色：避免刚上色又被刷白
            foreach (var key in _applied)
            {
                if (!_next.Contains(key))
                    Paint(control, key, Color.white);
            }
            if (InMode)
            {
                foreach (var key in _next)
                    Paint(control, key, InColor);
            }
            // 出线色后刷，冲突时胜出（规则固定，便于预期）
            for (int i = 0; i < _outLines.Count; i++)
                Paint(control, _outLines[i], OutColor);

            var swap = _applied;
            _applied = _next;
            _next = swap;
        }

        // 目标自身发出的线
        private void CollectOutLines(NodeControl control, IList<NodeUI> targets)
        {
            for (int i = 0; i < targets.Count; i++)
            {
                var node = targets[i];
                if (node?.nodeBase?.childUID == null)
                    continue;
                AddLinesOf(control, node, null);
            }
        }

        // 指向目标的线：必须全量扫 dictNode，输入侧没有反向索引
        private void CollectInLines(NodeControl control)
        {
            foreach (var kv in control.dictNode)
            {
                var node = kv.Value;
                if (node?.nodeBase?.childUID == null)
                    continue;
                AddLinesOf(control, node, _targetUids);
            }
        }

        // childFilter 为 null = 收全部已连线；否则只收指向集合内节点的线
        private void AddLinesOf(NodeControl control, NodeUI node, HashSet<string> childFilter)
        {
            var child = node.nodeBase.childUID;
            for (int i = 0; i < child.Length; i++)
            {
                string dst = child[i];
                if (string.IsNullOrEmpty(dst) || !control.dictNode.ContainsKey(dst))
                    continue;
                if (childFilter != null && !childFilter.Contains(dst))
                    continue;

                var key = new LineKey(node.nodeBase.uid, i);
                _next.Add(key);
                if (childFilter == null)
                    _outLines.Add(key);
            }
        }

        private static void Paint(NodeControl control, LineKey key, Color color)
        {
            NodeUI node;
            if (control.dictNode.TryGetValue(key.Uid, out node) && node != null)
                node.ChangeOutputLineColor(key.Idx, color);
        }

        // 还原所有本插件染过的线并关掉开关。control 为 null 时（场景已销毁）只清状态。
        internal void Clear(NodeControl control)
        {
            if (control?.dictNode != null)
            {
                foreach (var key in _applied)
                    Paint(control, key, Color.white);
            }
            _applied.Clear();
            _next.Clear();
            InMode = false;
            OutMode = false;
        }
    }
}
