using System;
using System.Collections.Generic;
using YS_Node;

namespace EC_NodeSidePanel
{
    internal struct LinkResult
    {
        internal bool Ok;
        internal string Message;

        internal static LinkResult Fail(string msg) => new LinkResult { Ok = false, Message = msg };
        internal static LinkResult Done(string msg) => new LinkResult { Ok = true, Message = msg };
    }

    // 面板内建连接。写入前必须全部校验通过，避免出现「校验失败但 childUID 已改」的半状态。
    internal static class NodeLinkService
    {
        // 唯一写入口。顺序与原版 NodeUI.OnDropFunc 一致：先建线（内部 Destroy 旧线）再写数据。
        private static void Apply(NodeUI src, int idx, string dstUid)
        {
            src.CreateConnectLine(idx, dstUid);
            src.nodeBase.childUID[idx] = dstUid;
        }

        // 出方向：src 的 slotIdx → 目标名。槽已占用直接覆盖并报告原目标。
        internal static LinkResult ConnectOut(NodeControl control, NodeUI src, int slotIdx, string dstName)
        {
            if (!NodeSlotInfo.Available)
                return LinkResult.Fail("槽位反射不可用，连接功能已禁用");
            if (src?.nodeBase?.childUID == null)
                return LinkResult.Fail("源节点无效");

            NodeUI dst;
            string error;
            if (!NodeSlotInfo.TryResolveByName(control, dstName, out dst, out error))
                return LinkResult.Fail(error);

            LinkResult check = ValidateSlot(src, slotIdx, dst);
            if (!check.Ok)
                return check;

            string dstUid = dst.nodeBase.uid;
            string old = NodeSlotInfo.ChildUidAt(src, slotIdx);
            Apply(src, slotIdx, dstUid);

            if (old != null && old != dstUid)
                return LinkResult.Done($"输出{slotIdx} → {dst.nodeBase.name}（已替换 {NodeSlotInfo.DisplayName(control, old)}）");
            return LinkResult.Done($"输出{slotIdx} → {dst.nodeBase.name}");
        }

        // 入方向：多个「名称」或「名称:槽号」 → dst 的输入。逐条独立，单条失败不影响其余。
        internal static List<LinkResult> ConnectIn(NodeControl control, NodeUI dst, string rawList)
        {
            var results = new List<LinkResult>();
            if (!NodeSlotInfo.Available)
            {
                results.Add(LinkResult.Fail("槽位反射不可用，连接功能已禁用"));
                return results;
            }
            if (dst?.nodeBase == null)
            {
                results.Add(LinkResult.Fail("目标节点无效"));
                return results;
            }
            if (!NodeSlotInfo.HasInput(dst))
            {
                results.Add(LinkResult.Fail($"「{dst.nodeBase.name}」无输入槽"));
                return results;
            }

            var tokens = NodeSlotInfo.SplitTokens(rawList);
            if (tokens.Count == 0)
            {
                results.Add(LinkResult.Fail("来源为空"));
                return results;
            }

            foreach (var token in tokens)
            {
                try
                {
                    results.Add(ConnectOneSource(control, dst, token));
                }
                catch (Exception ex)
                {
                    results.Add(LinkResult.Fail($"{token}: {ex.Message}"));
                }
            }
            return results;
        }

        // 单个来源 → dst 的输入。未指定槽号则取第一个已启用槽；两种情况都覆盖既有连线。
        private static LinkResult ConnectOneSource(NodeControl control, NodeUI dst, string token)
        {
            string name;
            int slot;
            NodeSlotInfo.SplitNameAndSlot(token, out name, out slot);

            NodeUI src;
            string error;
            if (!NodeSlotInfo.TryResolveByName(control, name, out src, out error))
                return LinkResult.Fail(error);
            if (src?.nodeBase?.childUID == null)
                return LinkResult.Fail($"「{name}」节点无效");

            if (slot < 0)
            {
                slot = NodeSlotInfo.FirstEnabledOutput(src);
                if (slot < 0)
                    return LinkResult.Fail($"「{src.nodeBase.name}」无可用输出槽");
            }

            LinkResult check = ValidateSlot(src, slot, dst);
            if (!check.Ok)
                return check;

            string dstUid = dst.nodeBase.uid;
            string old = NodeSlotInfo.ChildUidAt(src, slot);
            Apply(src, slot, dstUid);

            if (old != null && old != dstUid)
                return LinkResult.Done($"{src.nodeBase.name}.输出{slot} → 本节点（已替换 {NodeSlotInfo.DisplayName(control, old)}）");
            return LinkResult.Done($"{src.nodeBase.name}.输出{slot} → 本节点");
        }

        // 写入前的完整门槛：越界会数组越界，无输入槽会建出终点为 null 的废线。
        private static LinkResult ValidateSlot(NodeUI src, int slotIdx, NodeUI dst)
        {
            if (src == dst)
                return LinkResult.Fail("不能连接自身");
            if (!NodeSlotInfo.HasInput(dst))
                return LinkResult.Fail($"「{dst.nodeBase.name}」无输入槽");

            int count = NodeSlotInfo.OutputCount(src);
            if (count == 0)
                return LinkResult.Fail($"「{src.nodeBase.name}」无输出槽");
            if (slotIdx < 0 || slotIdx >= count || slotIdx >= src.nodeBase.childUID.Length)
                return LinkResult.Fail($"「{src.nodeBase.name}」无输出槽 {slotIdx}");
            if (!NodeSlotInfo.IsOutputEnabled(src, slotIdx))
                return LinkResult.Fail($"「{src.nodeBase.name}」输出{slotIdx} 未启用");

            return LinkResult.Done(null);
        }
    }
}
