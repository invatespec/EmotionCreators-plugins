using System;
using System.Collections.Generic;
using HEdit;
using HarmonyLib;
using UnityEngine;
using YS_Node;

namespace EC_SceneAudit.Repair
{
    // 只写目标 NodeBase.pos 与来源槽 childUID；不替换 Part，不 RebuildNodeUI，不调用全量 Save/Load。
    internal sealed class NodePatchGameMemoryHost : INodePatchMemoryHost
    {
        private const int NodeSlotCount = 5;
        private static readonly AccessTools.FieldRef<NodeUI, RectTransform> InputRectRef =
            AccessTools.FieldRefAccess<NodeUI, RectTransform>("rtfInput");
        private static readonly AccessTools.FieldRef<NodeUI, RectTransform[]> OutputRectsRef =
            AccessTools.FieldRefAccess<NodeUI, RectTransform[]>("rtfOutput");

        private readonly HEditData data;
        private readonly NodeControl nodeControl;

        public NodePatchGameMemoryHost(HEditData data, NodeControl nodeControl)
        {
            this.data = data;
            this.nodeControl = nodeControl;
        }

        public object SceneOwner => data;
        public object NodeOwner => nodeControl;
        public object GenerationOwner => nodeControl == null ? null : nodeControl.dictNode;

        public bool TryValidateTargets(ICollection<string> targetUids, out string error)
        {
            error = null;
            if (data?.nodes == null || nodeControl?.dictNode == null)
            {
                error = "HEditData 或 NodeControl 不可用。";
                return false;
            }
            foreach (var uid in targetUids ?? new List<string>())
            {
                NodeUI node;
                if (!TryGetNode(uid, out node, out error))
                    return false;
            }
            return true;
        }

        public bool TryGetCanvas(out float width, out float height, out bool supportsAbsoluteResize, out string error)
        {
            width = 0f;
            height = 0f;
            supportsAbsoluteResize = false;
            error = null;
            if (nodeControl?.rtfGrid == null)
            {
                error = "NodeControl.rtfGrid 不可用。";
                return false;
            }
            var size = nodeControl.rtfGrid.sizeDelta;
            if (!(size.x > 0f) || !(size.y > 0f))
            {
                error = "当前画布尺寸无效：" + size;
                return false;
            }
            width = size.x;
            height = size.y;
            supportsAbsoluteResize = NodePatchCanvasBridge.IsAvailable;
            if (supportsAbsoluteResize)
            {
                Vector2 reported;
                string apiError;
                if (NodePatchCanvasBridge.TryGetState(nodeControl, out reported, out apiError))
                {
                    width = reported.x;
                    height = reported.y;
                }
                else if (!string.IsNullOrEmpty(apiError))
                {
                    error = apiError;
                    return false;
                }
            }
            return true;
        }

        public bool TryResizeAbsolute(float width, float height, out float actualWidth, out float actualHeight, out string error)
        {
            actualWidth = 0f;
            actualHeight = 0f;
            Vector2 actual;
            if (!NodePatchCanvasBridge.TryResizeAbsolute(
                nodeControl, new Vector2(width, height), out actual, out error))
                return false;
            actualWidth = actual.x;
            actualHeight = actual.y;
            return true;
        }

        public bool TryReadPlacement(string uid, out NodePatchPlacementValue value, out string error)
        {
            value = default(NodePatchPlacementValue);
            NodeUI node;
            if (!TryGetNode(uid, out node, out error))
                return false;
            value = new NodePatchPlacementValue { X = node.nodeBase.pos.x, Y = node.nodeBase.pos.y };
            return true;
        }

        public bool TryWritePlacement(string uid, NodePatchPlacementValue value, out string error)
        {
            error = null;
            NodeUI node;
            if (!TryGetNode(uid, out node, out error))
                return false;
            node.KeepNodeWithinRange(new Vector2(value.X, value.Y));
            return true;
        }

        public bool TryReadSlot(string sourceUid, int slot, out NodePatchSlotValue value, out string error)
        {
            value = default(NodePatchSlotValue);
            NodeUI node;
            if (!TryGetNode(sourceUid, out node, out error))
                return false;
            if (!HasSlot(node.nodeBase, slot))
            {
                error = "节点槽位越界：" + sourceUid + "/slot:" + slot;
                return false;
            }
            value = new NodePatchSlotValue(
                node.nodeBase.childUID[slot] ?? string.Empty,
                node.nodeBase.endConditionType[slot].ToString(),
                node.nodeBase.endConditionCount[slot],
                node.nodeBase.fadeType[slot],
                node.nodeBase.fadeTime[slot]);
            return true;
        }

        public bool TryWriteSlotTarget(string sourceUid, int slot, string targetUid, out string error)
        {
            error = null;
            NodeUI source;
            NodeUI target;
            if (!TryGetNode(sourceUid, out source, out error))
                return false;
            if (!HasSlot(source.nodeBase, slot))
            {
                error = "节点槽位越界：" + sourceUid + "/slot:" + slot;
                return false;
            }
            if (string.IsNullOrEmpty(targetUid))
            {
                source.DisconnectLine(slot);
                return true;
            }
            if (!TryGetNode(targetUid, out target, out error))
                return false;
            if (InputRectRef(target) == null)
            {
                error = "目标节点没有输入端口：" + targetUid;
                return false;
            }
            var outputRects = OutputRectsRef(source);
            if (outputRects == null || slot >= outputRects.Length || outputRects[slot] == null
                || !outputRects[slot].gameObject.activeSelf)
            {
                error = "来源槽位当前不是可用状态：" + sourceUid + "/slot:" + slot;
                return false;
            }

            source.CreateConnectLine(slot, targetUid);
            source.nodeBase.childUID[slot] = targetUid ?? string.Empty;
            return true;
        }

        private bool TryGetNode(string uid, out NodeUI node, out string error)
        {
            node = null;
            error = null;
            if (string.IsNullOrEmpty(uid))
            {
                error = "节点 UID 为空。";
                return false;
            }
            if (nodeControl?.dictNode == null)
            {
                error = "NodeControl.dictNode 不可用。";
                return false;
            }
            if (!nodeControl.dictNode.TryGetValue(uid, out node) || node == null || node.nodeBase == null)
            {
                error = "找不到节点：" + uid;
                return false;
            }
            return true;
        }

        private static bool HasSlot(NodeBase nodeBase, int slot)
        {
            return nodeBase != null && slot >= 0 && slot < NodeSlotCount
                && nodeBase.childUID != null && nodeBase.childUID.Length >= NodeSlotCount
                && nodeBase.endConditionType != null && nodeBase.endConditionType.Length >= NodeSlotCount
                && nodeBase.endConditionCount != null && nodeBase.endConditionCount.Length >= NodeSlotCount
                && nodeBase.fadeType != null && nodeBase.fadeType.Length >= NodeSlotCount
                && nodeBase.fadeTime != null && nodeBase.fadeTime.Length >= NodeSlotCount;
        }
    }
}
