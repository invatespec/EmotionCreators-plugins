using EC_SceneAudit.Export;

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

    // 当前 Scene 的唯一查找规则，供预检、写回和回验共用；重复匹配返回 null，禁止取首项。
    internal static class RepairTargetLookup
    {
        public static NodeSnapshot FindNode(SceneSnapshot scene, string uid)
        {
            if (scene?.nodes == null)
                return null;
            NodeSnapshot found = null;
            foreach (var node in scene.nodes)
            {
                if (node != null && string.Equals(node.uid, uid, System.StringComparison.Ordinal))
                {
                    if (found != null) return null;
                    found = node;
                }
            }
            return found;
        }

        public static CutSnapshot FindCut(PartSnapshot part, int sourceIndex)
        {
            if (part?.cuts == null)
                return null;
            CutSnapshot found = null;
            foreach (var cut in part.cuts)
            {
                if (cut != null && cut.sourceIndex == sourceIndex)
                {
                    if (found != null) return null;
                    found = cut;
                }
            }
            return found;
        }

        public static TextBoxSnapshot FindTextBox(CutSnapshot cut, int sourceIndex)
        {
            if (cut?.textBoxes == null)
                return null;
            TextBoxSnapshot found = null;
            foreach (var box in cut.textBoxes)
            {
                if (box != null && box.sourceIndex == sourceIndex)
                {
                    if (found != null) return null;
                    found = box;
                }
            }
            return found;
        }

        public static TextLayoutSnapshot FindLayout(TextBoxSnapshot box, int layoutIndex)
        {
            if (box?.layouts == null)
                return null;
            TextLayoutSnapshot found = null;
            foreach (var layout in box.layouts)
            {
                if (layout != null && layout.layoutIndex == layoutIndex)
                {
                    if (found != null) return null;
                    found = layout;
                }
            }
            return found;
        }

        public static bool HasUnavailableWarning(TextLayoutSnapshot layout)
        {
            if (layout?.warnings == null)
                return false;
            foreach (var warning in layout.warnings)
            {
                if (warning != null && warning.code == "text-layout-read-failed")
                    return true;
            }
            return false;
        }
    }
}
