using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace EC_SceneAudit.Repair
{
    // 应用会话阶段：只描述本会话进度，不表示脚本运行结果或修改稿语义正确。
    internal enum RepairApplyPhase
    {
        Idle,
        PendingPreflight,
        PreflightFailed,
        AppliedUnsaved,
        Verified,
        VerifyFailed,
        Failed,
        Restored
    }

    internal static class RepairApplyPhaseText
    {
        public static string Describe(RepairApplyPhase phase)
        {
            switch (phase)
            {
                case RepairApplyPhase.PendingPreflight:
                    return "待核验";
                case RepairApplyPhase.PreflightFailed:
                    return "核验未通过，未写回";
                case RepairApplyPhase.AppliedUnsaved:
                    return "已应用，未保存，待 F8 回验";
                case RepairApplyPhase.Verified:
                    return "回验通过";
                case RepairApplyPhase.VerifyFailed:
                    return "回验未通过，可恢复";
                case RepairApplyPhase.Failed:
                    return "已失败";
                case RepairApplyPhase.Restored:
                    return "已从内存快照恢复，可再次预检修复";
                default:
                    return "未开始";
            }
        }

        // 只有已改动内存或仍在等待处理的阶段才禁止再次写回；核验未通过或已恢复时数据未变，可直接重试。
        public static bool BlocksNewApply(RepairApplyPhase phase)
        {
            return phase == RepairApplyPhase.PendingPreflight
                || phase == RepairApplyPhase.AppliedUnsaved
                || phase == RepairApplyPhase.Verified
                || phase == RepairApplyPhase.VerifyFailed
                || phase == RepairApplyPhase.Failed;
        }

        // 会话是否仍持有有效快照：恢复后阶段为 Restored，快照仅作展示，不再阻止新的修复。
        public static bool HoldsSnapshot(RepairApplyPhase phase)
        {
            return BlocksNewApply(phase);
        }
    }

    internal sealed class RepairApplyItem
    {
        public int Index;
        public string Label;
        public string Pointer;
        public string Before;
        public string After;
        public string BeforeDigest;
        public string AfterDigest;
        public bool Written;
        public string Issue;

        public bool Located => string.IsNullOrEmpty(Issue);
    }

    internal sealed class RepairApplyReport
    {
        public RepairApplyPhase Phase = RepairApplyPhase.Idle;
        public string PlanPath = string.Empty;
        public string BundlePath = string.Empty;
        public string SceneDisplay = string.Empty;
        public DateTime? AppliedAtUtc;
        public RepairMemorySnapshot Snapshot;
        public int SnapshotPartCount;
        public int SnapshotConnectionCount;
        public long ElapsedMilliseconds;
        public readonly List<string> Steps = new List<string>();
        public readonly List<string> Issues = new List<string>();
        public readonly List<RepairApplyItem> Items = new List<RepairApplyItem>();

        public bool HasSnapshot => Snapshot != null;

        public bool HasSession => HasSnapshot && RepairApplyPhaseText.HoldsSnapshot(Phase);

        public string StageText => RepairApplyPhaseText.Describe(Phase);

        public string FirstIssue => Issues.Count > 0 ? Issues[0] : string.Empty;

        public void MarkFailed(string message)
        {
            Issues.Add(message);
            Phase = RepairApplyPhase.Failed;
        }
    }

    // 报告只记录文本长度和哈希前缀，不复制对白全文。
    internal static class RepairDigest
    {
        private const int PrefixLength = 12;

        public static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes ?? new byte[0]);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var value in hash)
                    builder.Append(value.ToString("x2"));
                return builder.ToString();
            }
        }

        public static string Sha256Text(string text)
        {
            return Sha256(new UTF8Encoding(false).GetBytes(text ?? string.Empty));
        }

        public static string Describe(string text)
        {
            var value = text ?? string.Empty;
            return "长度 " + value.Length + " / sha256 " + Short(Sha256Text(value));
        }

        public static string Short(string sha256)
        {
            if (string.IsNullOrEmpty(sha256) || sha256.Length <= PrefixLength)
                return sha256;
            return sha256.Substring(0, PrefixLength);
        }
    }

    internal sealed class RepairVerificationItem
    {
        public int Index;
        public string Label;
        public string Pointer;
        public string Expected;
        public string Actual;
        public readonly List<string> Issues = new List<string>();

        public bool Passed => Issues.Count == 0;
    }

    internal sealed class RepairVerificationReport
    {
        public DateTime CheckedAt = DateTime.UtcNow;
        public long ElapsedMilliseconds;
        public string PlanPath = string.Empty;
        public string OriginalBundlePath = string.Empty;
        public string NewBundlePath = string.Empty;
        public string NewBundleId = string.Empty;
        public readonly List<string> GlobalIssues = new List<string>();
        public readonly List<RepairVerificationItem> Items = new List<RepairVerificationItem>();

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

        public bool Passed
        {
            get
            {
                if (GlobalIssues.Count > 0 || Items.Count == 0)
                    return false;
                foreach (var item in Items)
                {
                    if (!item.Passed)
                        return false;
                }
                return true;
            }
        }

        public string Verdict
        {
            get
            {
                if (GlobalIssues.Count > 0)
                    return "回验未通过：" + GlobalIssues[0];
                if (Items.Count == 0)
                    return "回验未通过：清单没有目标";
                if (PassedCount == Items.Count)
                    return "回验通过 " + Items.Count + "/" + Items.Count;
                return "回验未通过：一致 " + PassedCount + "/" + Items.Count;
            }
        }
    }

    internal static class RepairApplyFormat
    {
        public static string FormatText(RepairApplyReport report, RepairVerificationReport verification)
        {
            var builder = new StringBuilder();
            builder.AppendLine("EC SceneAudit 修复应用（可恢复）");
            if (report == null)
            {
                builder.AppendLine("本次没有应用记录。");
                AppendVerification(builder, verification);
                return builder.ToString();
            }

            builder.AppendLine("阶段：" + report.StageText);
            builder.AppendLine("耗时：" + report.ElapsedMilliseconds + " ms");
            builder.AppendLine("修复清单：" + Display(report.PlanPath));
            builder.AppendLine("资料包：" + Display(report.BundlePath));
            builder.AppendLine("当前 Scene：" + Display(report.SceneDisplay));
            builder.AppendLine("内存快照：" + (report.HasSnapshot
                ? report.Snapshot.Describe()
                : "(无)"));
            if (report.AppliedAtUtc.HasValue)
                builder.AppendLine("应用时间：" + report.AppliedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            builder.AppendLine("目标数量：" + report.Items.Count);

            foreach (var step in report.Steps)
                builder.AppendLine("阶段记录：" + step);

            foreach (var item in report.Items)
            {
                builder.AppendLine();
                builder.AppendLine((item.Index + 1) + ". " + (item.Label ?? item.Pointer));
                builder.AppendLine("   位置：" + item.Pointer);
                builder.AppendLine("   原文：" + Display(item.BeforeDigest));
                builder.AppendLine("   新文：" + Display(item.AfterDigest));
                builder.AppendLine("   写回：" + (item.Written ? "已写入" : "未写入"));
                if (!item.Located)
                    builder.AppendLine("   - " + item.Issue);
            }

            foreach (var issue in report.Issues)
                builder.AppendLine("问题：" + issue);

            AppendVerification(builder, verification);

            builder.AppendLine();
            builder.AppendLine("说明：应用只改清单目标的文本布局原文，不写文件、不做全量保存；结果只在内存中，");
            builder.AppendLine("      需要落盘时由用户在游戏内手动保存，恢复只还原快照记录的目标 Part 与相关连线。");
            return builder.ToString();
        }

        private static void AppendVerification(StringBuilder builder, RepairVerificationReport verification)
        {
            if (verification == null)
                return;
            builder.AppendLine();
            builder.AppendLine("F8 回验（" + verification.NewBundlePath + "）");
            builder.AppendLine("结论：" + verification.Verdict);
            builder.AppendLine("新 bundleId：" + Display(verification.NewBundleId));
            foreach (var issue in verification.GlobalIssues)
                builder.AppendLine("全局问题：" + issue);
            foreach (var item in verification.Items)
            {
                builder.AppendLine((item.Index + 1) + ". " + (item.Label ?? item.Pointer)
                    + " —— " + (item.Passed ? "新文一致" : "不一致"));
                builder.AppendLine("   期望：" + Preview(item.Expected));
                builder.AppendLine("   导出：" + Preview(item.Actual));
                foreach (var issue in item.Issues)
                    builder.AppendLine("   - " + issue);
            }
        }

        private static string Display(string value)
        {
            return string.IsNullOrEmpty(value) ? "(无)" : value;
        }

        private static string Preview(string text)
        {
            if (text == null)
                return "(缺失)";
            var flat = text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            return flat.Length <= 160 ? flat : flat.Substring(0, 160) + "…";
        }
    }
}
