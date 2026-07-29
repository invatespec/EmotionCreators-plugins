using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using YS_Node;

namespace EC_NodeSidePanel
{
    // 节点槽位只读探测。
    // 槽可用性的真值由原版 NodeUI.UpdateConnectParts() 写在 rtfOutput[i].activeSelf 上，
    // 它已同时覆盖 H 的 endConditionType 规则与 ADV 的选项数规则；自己重算等于复制两套规则且必然漂移。
    internal static class NodeSlotInfo
    {
        private static readonly AccessTools.FieldRef<NodeUI, RectTransform[]> RtfOutputRef;
        private static readonly AccessTools.FieldRef<NodeUI, RectTransform> RtfInputRef;

        // 反射失败不抛：连接功能整体禁用，高亮与既有功能不受影响
        internal static readonly bool Available;

        static NodeSlotInfo()
        {
            try
            {
                RtfOutputRef = AccessTools.FieldRefAccess<NodeUI, RectTransform[]>("rtfOutput");
                RtfInputRef = AccessTools.FieldRefAccess<NodeUI, RectTransform>("rtfInput");
                Available = true;
            }
            catch (Exception ex)
            {
                NodeSidePanelPlugin.Log?.LogWarning($"NodeUI 槽位字段反射失败，连接功能禁用: {ex.Message}");
                Available = false;
            }
        }

        internal static int OutputCount(NodeUI node)
        {
            if (!Available || node == null)
                return 0;
            try
            {
                var slots = RtfOutputRef(node);
                return slots?.Length ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        internal static bool IsOutputEnabled(NodeUI node, int idx)
        {
            if (!Available || node == null || idx < 0)
                return false;
            try
            {
                var slots = RtfOutputRef(node);
                if (slots == null || idx >= slots.Length)
                    return false;
                var rt = slots[idx];
                return rt != null && rt.gameObject.activeSelf;
            }
            catch
            {
                return false;
            }
        }

        // node_start 无输入槽：连过去会建出一条终点为 null 的废线
        internal static bool HasInput(NodeUI node)
        {
            if (!Available || node == null)
                return false;
            try
            {
                return RtfInputRef(node) != null;
            }
            catch
            {
                return false;
            }
        }

        // 第一个已启用的槽（不管占没占）；没有返回 -1
        internal static int FirstEnabledOutput(NodeUI node)
        {
            int count = OutputCount(node);
            for (int i = 0; i < count; i++)
            {
                if (IsOutputEnabled(node, i))
                    return i;
            }
            return -1;
        }

        internal static string ChildUidAt(NodeUI node, int idx)
        {
            var child = node?.nodeBase?.childUID;
            if (child == null || idx < 0 || idx >= child.Length)
                return null;
            return string.IsNullOrEmpty(child[idx]) ? null : child[idx];
        }

        internal static string DisplayName(NodeControl control, string uid)
        {
            NodeUI n;
            if (control?.dictNode != null && control.dictNode.TryGetValue(uid, out n) && n?.nodeBase != null)
                return n.nodeBase.name ?? uid;
            return uid;
        }

        // 按节点名精确匹配（忽略大小写、去首尾空格）。重名视为错误，不猜。
        internal static bool TryResolveByName(NodeControl control, string name, out NodeUI node, out string error)
        {
            node = null;
            error = null;
            string key = (name ?? string.Empty).Trim();
            if (key.Length == 0)
            {
                error = "节点名为空";
                return false;
            }
            if (control?.dictNode == null)
            {
                error = "无 NodeControl";
                return false;
            }

            int hits = 0;
            foreach (var kv in control.dictNode)
            {
                var n = kv.Value;
                if (n?.nodeBase == null)
                    continue;
                if (!string.Equals(n.nodeBase.name ?? string.Empty, key, StringComparison.OrdinalIgnoreCase))
                    continue;
                hits++;
                if (hits > 1)
                {
                    error = $"节点名「{key}」不唯一";
                    node = null;
                    return false;
                }
                node = n;
            }

            if (hits == 0)
            {
                error = $"找不到节点「{key}」";
                return false;
            }
            return true;
        }

        // 「名称」或「名称:槽号」。节点名允许含 ':'，故取最后一个 ':' 且后缀须是合法槽号，否则整串当名字。
        internal static void SplitNameAndSlot(string token, out string name, out int slot)
        {
            name = (token ?? string.Empty).Trim();
            slot = -1;
            int cut = name.LastIndexOf(':');
            if (cut <= 0 || cut >= name.Length - 1)
                return;

            int parsed;
            string suffix = name.Substring(cut + 1).Trim();
            // 解析放宽到 0..9，越界交给 ValidateSlot 报「无输出槽 N」；
            // 这里若直接卡 OutputMax，A:9 会退化成「找不到节点 A:9」，提示误导。
            if (!int.TryParse(suffix, out parsed) || parsed < 0 || parsed > 9)
                return;

            slot = parsed;
            name = name.Substring(0, cut).Trim();
        }

        internal static List<string> SplitTokens(string raw)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(raw))
                return result;
            // 中文逗号一并接受：输入法习惯性打全角，报「找不到节点」会很莫名
            foreach (var part in raw.Split(',', '，'))
            {
                string t = part.Trim();
                if (t.Length > 0)
                    result.Add(t);
            }
            return result;
        }
    }
}
