using System.Collections.Generic;
using UnityEngine;
using YS_Node;

namespace EC_NodeSidePanel
{
    // 方向键微调节点 pos；步进 Ctrl2 / 默认20 / Shift100。
    internal static class NodeNudgeService
    {
        private const float StepSmall = 2f;
        private const float StepNormal = 20f;
        private const float StepLarge = 100f;

        internal static float CurrentStep()
        {
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                return StepSmall;
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                return StepLarge;
            return StepNormal;
        }

        // 目标优先级：勾选 > 文件夹(含子夹) > 高亮单节点
        internal static List<NodeUI> ResolveTargets(NodeControl control, NodeListModel list, FolderTreeService folders)
        {
            var result = new List<NodeUI>();
            if (control?.dictNode == null)
                return result;

            if (list.CheckedUids.Count > 0)
            {
                foreach (var uid in list.CheckedUids)
                    TryAdd(result, control, uid);
                return result;
            }

            if (!string.IsNullOrEmpty(list.HighlightFolderId))
            {
                foreach (var uid in folders.CollectFolderNodeUids(list.HighlightFolderId))
                    TryAdd(result, control, uid);
                return result;
            }

            if (!string.IsNullOrEmpty(list.HighlightUid))
                TryAdd(result, control, list.HighlightUid, allowSystem: true);
            return result;
        }

        private static void TryAdd(List<NodeUI> result, NodeControl control, string uid, bool allowSystem = false)
        {
            if (string.IsNullOrEmpty(uid))
                return;
            if (!allowSystem && (uid == "node_start" || uid == "node_end"))
                return;
            if (control.dictNode.TryGetValue(uid, out var n) && n != null)
                result.Add(n);
        }

        internal static void Nudge(IList<NodeUI> targets, Vector2 dir)
        {
            if (targets == null || targets.Count == 0)
                return;
            // dir 已是轴单位向量（↑↓←→）
            float step = CurrentStep();
            Vector2 delta = new Vector2(dir.x * step, dir.y * step);

            foreach (var node in targets)
            {
                if (node?.nodeBase == null)
                    continue;
                node.KeepNodeWithinRange(node.nodeBase.pos + delta);
            }
        }
    }
}
