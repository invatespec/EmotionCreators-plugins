using System;
using System.Collections.Generic;

namespace EC_SceneAudit.Repair
{
    // 单项结论只有「原文一致」或「冲突」：核验清单原文与当前 Scene 文本是否相同。
    internal sealed class RepairPreflightItem
    {
        public int Index;
        public string Label;
        public string Pointer;
        public string Before;
        public string After;
        public string CurrentText;
        public readonly List<string> SourceIssues = new List<string>();
        public readonly List<string> CurrentIssues = new List<string>();

        public bool Passed => SourceIssues.Count == 0 && CurrentIssues.Count == 0;
    }

    internal sealed class RepairPreflightReport
    {
        public DateTime CheckedAt = DateTime.UtcNow;
        public long ElapsedMilliseconds;
        public string PlanPath;
        public string BundlePath;
        public string BundleId;
        public string ManifestSha256;
        public string SceneTitle;
        public string CurrentTitle;
        public int ChangeCount;
        public readonly List<string> GlobalIssues = new List<string>();
        public readonly List<RepairPreflightItem> Items = new List<RepairPreflightItem>();

        public bool Passed
        {
            get
            {
                if (GlobalIssues.Count > 0)
                    return false;
                foreach (var item in Items)
                {
                    if (!item.Passed)
                        return false;
                }
                return true;
            }
        }

        public int PassedCount
        {
            get
            {
                var count = 0;
                foreach (var item in Items)
                {
                    if (item.Passed)
                        count++;
                }
                return count;
            }
        }

        public string Verdict
        {
            get
            {
                if (GlobalIssues.Count > 0)
                    return "预检未通过：资料包或当前 Scene 结构核验失败";
                if (Items.Count == 0)
                    return "清单为空，没有需要核验的修改";
                if (PassedCount == Items.Count)
                    return "原文一致 " + Items.Count + "/" + Items.Count;
                return "冲突 " + (Items.Count - PassedCount) + "/" + Items.Count + "，原文一致 " + PassedCount + "/" + Items.Count;
            }
        }
    }
}
