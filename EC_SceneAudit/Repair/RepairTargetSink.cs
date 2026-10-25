namespace EC_SceneAudit.Repair
{
    // 写回只暴露单个文本槽的读写；定位与对象结构由实现负责，写回器不越界改动其它字段。
    internal interface IRepairTextSlot
    {
        string Read();
        void Write(string text);
    }

    internal interface IRepairTargetSink
    {
        // 按 UID、CUT/文本框源序号和布局序号唯一定位目标；歧义或缺失必须返回 false。
        bool TryLocate(RepairPlanTarget target, out IRepairTextSlot slot, out string issue);
    }
}
