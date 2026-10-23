using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using EC_SceneAudit.Export;

namespace EC_SceneAudit.Repair
{
    // 应用编排只操作当前内存：重新核验 -> 受影响内存快照 -> 文本事务 -> 等待 F8 回验。
    // 不读取、创建、替换或删除 Scene 文件，也不调用原生全量保存。
    internal sealed class RepairApplyEngine
    {
        private readonly RepairPreflight preflight = new RepairPreflight();
        private readonly RepairVerifier verifier = new RepairVerifier();

        public RepairApplyReport Apply(
            string planPath,
            string bundlePath,
            string sceneDisplay,
            Func<SceneSnapshot> readCurrentScene,
            Func<IRepairTargetSink> buildSink,
            IRepairMemoryHost memory)
        {
            return Apply(planPath, bundlePath, sceneDisplay, readCurrentScene, buildSink, memory, null);
        }

        // previousSession 非空且仍在等待处理时拒绝新应用，避免覆盖有效快照。
        public RepairApplyReport Apply(
            string planPath,
            string bundlePath,
            string sceneDisplay,
            Func<SceneSnapshot> readCurrentScene,
            Func<IRepairTargetSink> buildSink,
            IRepairMemoryHost memory,
            RepairApplyReport previousSession)
        {
            var timer = Stopwatch.StartNew();
            var report = new RepairApplyReport
            {
                PlanPath = planPath ?? string.Empty,
                BundlePath = bundlePath ?? string.Empty,
                SceneDisplay = sceneDisplay ?? string.Empty,
                Phase = RepairApplyPhase.PendingPreflight
            };
            report.Steps.Add("待核验");

            if (previousSession != null && previousSession.HasSession)
            {
                report.MarkFailed("上一次应用尚未恢复或清除，不能再次应用；请先恢复内存快照或清除本次记录。");
                return Finish(report, timer);
            }
            if (memory == null)
            {
                report.MarkFailed("缺少内存快照网关，未做任何修改。");
                return Finish(report, timer);
            }

            RepairPlan plan;
            RepairBundleSource bundle;
            try
            {
                plan = RepairPlanReader.ReadFile(planPath);
                bundle = RepairBundleReader.Read(bundlePath);
            }
            catch (RepairInputException ex)
            {
                report.MarkFailed("重新读取清单或资料包失败，未做任何修改：" + ex.Message);
                return Finish(report, timer);
            }

            RepairPreflightReport check;
            try
            {
                check = preflight.Evaluate(plan, planPath, bundlePath, bundle, readCurrentScene());
            }
            catch (Exception ex)
            {
                report.MarkFailed("核验当前 Scene 失败，未做任何修改：" + ex.Message);
                return Finish(report, timer);
            }

            if (plan.Changes.Count == 0)
            {
                report.MarkFailed("修复清单没有可应用的修改，未做任何修改。");
                report.Phase = RepairApplyPhase.PreflightFailed;
                return Finish(report, timer);
            }
            if (!check.Passed)
            {
                CollectPreflightIssues(check, report);
                report.MarkFailed("应用前核验未通过：" + check.Verdict + "。未修改内存。");
                report.Phase = RepairApplyPhase.PreflightFailed;
                return Finish(report, timer);
            }
            report.Steps.Add("已核验 " + plan.Changes.Count + " 项（" + check.Verdict + "）");

            RepairMemorySnapshot snapshot;
            string snapshotError;
            try
            {
                if (!RepairMemoryCapture.TryCapture(plan, memory, out snapshot, out snapshotError) || snapshot == null)
                {
                    report.MarkFailed("创建内存快照失败，未写回：" + snapshotError);
                    return Finish(report, timer);
                }
            }
            catch (Exception ex)
            {
                report.MarkFailed("创建内存快照异常，未写回：" + ex.Message);
                return Finish(report, timer);
            }
            snapshot.SceneDisplay = report.SceneDisplay;

            IRepairTargetSink sink;
            try
            {
                sink = buildSink != null ? buildSink() : null;
            }
            catch (Exception ex)
            {
                report.MarkFailed("创建目标定位器失败，未写回：" + ex.Message);
                return Finish(report, timer);
            }

            List<RepairApplyItem> items;
            string transactionError;
            if (!RepairApplyTransaction.TryRun(plan, sink, out items, out transactionError))
            {
                ApplyLabels(items, check);
                report.Items.AddRange(items);
                // 事务失败已回滚内存文本，本次不产生会话，避免顶掉已有有效快照。
                report.MarkFailed("写回目标失败：" + transactionError);
                return Finish(report, timer);
            }
            ApplyLabels(items, check);
            report.Items.AddRange(items);
            report.Snapshot = snapshot;
            report.SnapshotPartCount = snapshot.PartCount;
            report.SnapshotConnectionCount = snapshot.ConnectionCount;
            report.AppliedAtUtc = DateTime.UtcNow;
            report.Phase = RepairApplyPhase.AppliedUnsaved;
            report.Steps.Add("已保存内存快照：" + snapshot.Describe());
            report.Steps.Add("已写回 " + items.Count + " 项文本（仅内存，未保存文件）");
            report.Steps.Add("待 F8 回验：应用前请自行保存；请在游戏内按 F8 重新导出后再读取回验");
            return Finish(report, timer);
        }

        public bool Restore(RepairApplyReport report, IRepairMemoryHost memory)
        {
            if (report == null)
                return false;
            if (!report.HasSnapshot)
            {
                report.MarkFailed("没有可用的内存快照。");
                return false;
            }

            string error;
            string rollback;
            if (!RepairMemoryRestore.TryRestore(report.Snapshot, memory, out error, out rollback))
            {
                report.MarkFailed(error);
                if (!string.IsNullOrEmpty(rollback))
                    report.Steps.Add(rollback);
                return false;
            }

            report.Phase = RepairApplyPhase.Restored;
            report.Steps.Add("已从内存快照恢复：" + report.Snapshot.Describe());
            return true;
        }

        public RepairVerificationReport Verify(string planPath, string originalBundlePath, string newBundlePath)
        {
            return verifier.Verify(planPath, originalBundlePath, newBundlePath);
        }

        // 新的 F8 导出目录：<root>/<title>/<date>/<time>_<id>/，取严格晚于阈值的最新完整批次。
        public static string FindNewestBundleDirectory(string root, DateTime notBeforeUtc)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return null;
            string newest = null;
            var newestTime = notBeforeUtc;
            try
            {
                foreach (var titleDir in Directory.GetDirectories(root))
                foreach (var dateDir in Directory.GetDirectories(titleDir))
                foreach (var exportDir in Directory.GetDirectories(dateDir))
                {
                    if (!File.Exists(Path.Combine(exportDir, "bundle.json"))
                        || !File.Exists(Path.Combine(exportDir, "bundle.sha256")))
                        continue;
                    var time = Directory.GetLastWriteTimeUtc(exportDir);
                    if (time > newestTime)
                    {
                        newest = exportDir;
                        newestTime = time;
                    }
                }
            }
            catch (Exception)
            {
                // 返回已找到的结果，由窗口显示找不到新导出。
            }
            return newest;
        }

        private static void ApplyLabels(List<RepairApplyItem> items, RepairPreflightReport check)
        {
            if (items == null || check?.Items == null)
                return;
            foreach (var item in items)
            {
                if (item.Index >= 0 && item.Index < check.Items.Count
                    && !string.IsNullOrEmpty(check.Items[item.Index].Label))
                    item.Label = check.Items[item.Index].Label;
            }
        }

        private static void CollectPreflightIssues(RepairPreflightReport check, RepairApplyReport report)
        {
            foreach (var issue in check.GlobalIssues)
                report.Issues.Add("全局：" + issue);
            foreach (var item in check.Items)
            {
                foreach (var issue in item.SourceIssues)
                    report.Issues.Add("第 " + (item.Index + 1) + " 项资料包：" + issue);
                foreach (var issue in item.CurrentIssues)
                    report.Issues.Add("第 " + (item.Index + 1) + " 项当前：" + issue);
            }
        }

        private static RepairApplyReport Finish(RepairApplyReport report, Stopwatch timer)
        {
            report.ElapsedMilliseconds = timer.ElapsedMilliseconds;
            return report;
        }
    }
}
