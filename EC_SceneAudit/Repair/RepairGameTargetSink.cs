using HEdit;

namespace EC_SceneAudit.Repair
{
    // 写回定位：UID 取 HEditData.nodes，CUT/文本框/布局序号取导出时的列表下标。
    // 编辑器重排会改写顺序，因此每次写回都重新定位，不复用旧结果。
    internal sealed class RepairGameTargetSink : IRepairTargetSink
    {
        private readonly HEditData data;

        public RepairGameTargetSink(HEditData data)
        {
            this.data = data;
        }

        public bool TryLocate(RepairPlanTarget target, out IRepairTextSlot slot, out string issue)
        {
            slot = null;
            issue = null;
            if (target == null)
            {
                issue = "清单目标为空";
                return false;
            }
            if (data?.nodes == null)
            {
                issue = "当前 Scene 没有节点数据";
                return false;
            }

            BasePart part;
            if (string.IsNullOrEmpty(target.NodeUid) || !data.nodes.TryGetValue(target.NodeUid, out part) || part == null)
            {
                issue = "找不到唯一的 Part UID：" + target.NodeUid;
                return false;
            }
            var advPart = part as HEdit.ADVPart;
            if (advPart == null)
            {
                issue = "目标 UID 不是 ADV Part：" + target.NodeUid;
                return false;
            }

            var cuts = advPart.cuts;
            if (cuts == null || target.CutSourceIndex < 0 || target.CutSourceIndex >= cuts.Count)
            {
                issue = "找不到 CUT 源序号 " + target.CutSourceIndex;
                return false;
            }
            var bubbles = cuts[target.CutSourceIndex]?.speechBubbles;
            if (bubbles == null || target.TextBoxSourceIndex < 0 || target.TextBoxSourceIndex >= bubbles.Count)
            {
                issue = "找不到文本框源序号 " + target.TextBoxSourceIndex;
                return false;
            }
            var layouts = bubbles[target.TextBoxSourceIndex]?.textLayouts;
            if (layouts == null || target.LayoutIndex < 0 || target.LayoutIndex >= layouts.Length)
            {
                issue = "找不到布局 " + target.LayoutIndex;
                return false;
            }
            var layout = layouts[target.LayoutIndex];
            if (layout == null)
            {
                issue = "布局对象为空";
                return false;
            }

            slot = new LayoutSlot(layout);
            return true;
        }

        private sealed class LayoutSlot : IRepairTextSlot
        {
            // 必须写全 HEdit.：命名空间 ADVPart 与类型 HEdit.ADVPart 同名，限定名会先命中命名空间。
            private readonly HEdit.ADVPart.SpeechBubbles.TextLayout layout;

            public LayoutSlot(HEdit.ADVPart.SpeechBubbles.TextLayout layout)
            {
                this.layout = layout;
            }

            public string Read()
            {
                return layout.msg ?? string.Empty;
            }

            // 只写文本布局原文，不触碰对白框属性、播放状态或节点连接。
            public void Write(string text)
            {
                layout.msg = text ?? string.Empty;
            }
        }
    }

}
