using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using EC_SceneAudit.Export;

namespace EC_SceneAudit.Repair
{
    // 新 F8 回验：逐项核对 after、确认旧 before 不再是当前文本，并比较未目标的结构与文本。
    internal sealed class RepairVerifier
    {
        private const int MaxAiSummaryBytes = 64 * 1024 * 1024;
        private const int MaxIssues = 20;

        public RepairVerificationReport Verify(string planPath, string originalBundlePath, string newBundlePath)
        {
            var timer = Stopwatch.StartNew();
            var report = new RepairVerificationReport
            {
                PlanPath = planPath ?? string.Empty,
                OriginalBundlePath = originalBundlePath ?? string.Empty,
                NewBundlePath = newBundlePath ?? string.Empty
            };

            RepairPlan plan;
            try
            {
                plan = RepairPlanReader.ReadFile(planPath);
            }
            catch (RepairInputException ex)
            {
                AddIssue(report, "修复清单读取失败：" + ex.Message);
                return Finish(report, timer);
            }

            if (string.IsNullOrWhiteSpace(newBundlePath) || !Directory.Exists(newBundlePath))
            {
                AddIssue(report, "找不到新的 F8 导出目录：" + (newBundlePath ?? "(空)"));
                return Finish(report, timer);
            }
            if (SameDirectory(originalBundlePath, newBundlePath))
            {
                AddIssue(report, "回验资料包与原始资料包是同一目录；请先在游戏内按 F8 重新导出。");
                return Finish(report, timer);
            }

            RepairBundleSource original;
            try
            {
                original = RepairBundleReader.Read(originalBundlePath);
            }
            catch (RepairInputException ex)
            {
                AddIssue(report, "原始资料包读取失败：" + ex.Message);
                return Finish(report, timer);
            }

            RepairBundleSource latest;
            try
            {
                latest = RepairBundleReader.Read(newBundlePath);
            }
            catch (RepairInputException ex)
            {
                AddIssue(report, "新 F8 资料包读取失败：" + ex.Message);
                return Finish(report, timer);
            }
            report.NewBundleId = latest.BundleId;

            // 每次 F8 都会生成新的 bundleId；与应用前资料包相同说明这是应用前的旧导出。
            if (string.Equals(latest.BundleId, original.BundleId, StringComparison.Ordinal))
            {
                AddIssue(report, "回验资料包与应用前资料包是同一批次（bundleId 相同），请先在游戏内按 F8 重新导出。");
                return Finish(report, timer);
            }

            CheckAiSummary(newBundlePath, report);
            CheckTargets(plan, latest.Scene, report);
            CompareUntouched(plan, original.Scene, latest.Scene, report);
            return Finish(report, timer);
        }

        private static void CheckAiSummary(string bundleDirectory, RepairVerificationReport report)
        {
            var path = Path.Combine(bundleDirectory, "ai-summary.json");
            if (!File.Exists(path))
            {
                AddIssue(report, "新导出缺少 ai-summary.json，不能作为完整回验证据。");
                return;
            }
            try
            {
                var text = RepairPlanReader.DecodeUtf8(
                    RepairPlanReader.ReadBounded(path, MaxAiSummaryBytes, "新导出 ai-summary.json"),
                    "新导出 ai-summary.json");
                if (!RepairJson.Parse(text).IsObject)
                    AddIssue(report, "新导出 ai-summary.json 不是 JSON 对象。");
            }
            catch (RepairInputException ex)
            {
                AddIssue(report, "新导出 ai-summary.json 无法解析：" + ex.Message);
            }
        }

        private static void CheckTargets(RepairPlan plan, SceneSnapshot scene, RepairVerificationReport report)
        {
            for (var index = 0; index < plan.Changes.Count; index++)
            {
                var change = plan.Changes[index];
                var target = change.Target;
                var item = new RepairVerificationItem
                {
                    Index = index,
                    Pointer = target.Reference.Pointer,
                    Label = target.NodeUid + " · CUT " + (target.CutSourceIndex + 1)
                        + " · 文本框 " + (target.TextBoxSourceIndex + 1) + " · 布局 " + target.LayoutIndex,
                    Expected = change.After
                };
                report.Items.Add(item);

                var node = RepairTargetLookup.FindNode(scene, target.NodeUid);
                if (node == null || node.part == null)
                {
                    item.Issues.Add("新导出找不到唯一 Part：" + target.NodeUid);
                    continue;
                }
                var cut = RepairTargetLookup.FindCut(node.part, target.CutSourceIndex);
                if (cut == null)
                {
                    item.Issues.Add("新导出找不到唯一 CUT 源序号 " + target.CutSourceIndex);
                    continue;
                }
                var box = RepairTargetLookup.FindTextBox(cut, target.TextBoxSourceIndex);
                if (box == null)
                {
                    item.Issues.Add("新导出找不到唯一文本框源序号 " + target.TextBoxSourceIndex);
                    continue;
                }
                var layout = RepairTargetLookup.FindLayout(box, target.LayoutIndex);
                if (layout == null)
                {
                    item.Issues.Add("新导出找不到唯一布局 " + target.LayoutIndex);
                    continue;
                }
                if (layout.rawText == null || RepairTargetLookup.HasUnavailableWarning(layout))
                {
                    item.Issues.Add("新导出该布局文本不可用");
                    continue;
                }

                item.Actual = layout.rawText;
                if (!string.Equals(item.Actual, change.After, StringComparison.Ordinal))
                    item.Issues.Add("新导出文本与修改稿不一致");
                if (string.Equals(item.Actual, change.Before, StringComparison.Ordinal))
                    item.Issues.Add("该位置仍是清单原文（可能未保存或已回退）");
            }
        }

        // 未目标结构或文本出现变化时保留差异，避免把额外改动当成修复结果。
        private static void CompareUntouched(
            RepairPlan plan,
            SceneSnapshot original,
            SceneSnapshot latest,
            RepairVerificationReport report)
        {
            if (original?.nodes == null || latest?.nodes == null)
            {
                AddIssue(report, "缺少可比较的 Scene 快照。");
                return;
            }
            if (!string.Equals(original.scene?.title ?? string.Empty, latest.scene?.title ?? string.Empty, StringComparison.Ordinal))
                AddIssue(report, "新导出 Scene 标题与原始资料包不一致。");
            if (original.nodes.Length != latest.nodes.Length)
            {
                AddIssue(report, "节点数量变化：" + original.nodes.Length + " -> " + latest.nodes.Length);
                return;
            }

            var targets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var change in plan.Changes)
                targets.Add(TargetKey(change.Target.NodeUid, change.Target.CutSourceIndex, change.Target.TextBoxSourceIndex, change.Target.LayoutIndex));

            foreach (var beforeNode in original.nodes)
            {
                if (beforeNode == null)
                    continue;
                var afterNode = RepairTargetLookup.FindNode(latest, beforeNode.uid);
                if (afterNode == null)
                {
                    AddIssue(report, "节点 UID 集合变化，新增或缺失：" + beforeNode.uid);
                    continue;
                }
                if ((beforeNode.part == null) != (afterNode.part == null))
                {
                    AddIssue(report, "节点 Part 存在性变化：" + beforeNode.uid);
                    continue;
                }
                if (beforeNode.part == null)
                    continue;
                if (!string.Equals(beforeNode.part.uid, afterNode.part.uid, StringComparison.Ordinal))
                    AddIssue(report, "Part UID 变化：" + beforeNode.uid);

                var beforeCuts = beforeNode.part.cuts ?? new CutSnapshot[0];
                var afterCuts = afterNode.part.cuts ?? new CutSnapshot[0];
                if (beforeCuts.Length != afterCuts.Length)
                {
                    AddIssue(report, "CUT 数量变化：" + beforeNode.uid + " " + beforeCuts.Length + " -> " + afterCuts.Length);
                    continue;
                }
                foreach (var beforeCut in beforeCuts)
                {
                    if (beforeCut == null)
                        continue;
                    var afterCut = RepairTargetLookup.FindCut(afterNode.part, beforeCut.sourceIndex);
                    if (afterCut == null)
                    {
                        AddIssue(report, "CUT 源序号集合变化：" + beforeNode.uid + "/cut:" + beforeCut.sourceIndex);
                        continue;
                    }
                    if (beforeCut.endCut != afterCut.endCut || beforeCut.optionCount != afterCut.optionCount)
                        AddIssue(report, "CUT 属性变化：" + beforeNode.uid + "/cut:" + beforeCut.sourceIndex);

                    var beforeBoxes = beforeCut.textBoxes ?? new TextBoxSnapshot[0];
                    var afterBoxes = afterCut.textBoxes ?? new TextBoxSnapshot[0];
                    if (beforeBoxes.Length != afterBoxes.Length)
                    {
                        AddIssue(report, "文本框数量变化：" + beforeNode.uid + "/cut:" + beforeCut.sourceIndex);
                        continue;
                    }
                    foreach (var beforeBox in beforeBoxes)
                    {
                        if (beforeBox == null)
                            continue;
                        var afterBox = RepairTargetLookup.FindTextBox(afterCut, beforeBox.sourceIndex);
                        if (afterBox == null)
                        {
                            AddIssue(report, "文本框源序号集合变化：" + beforeNode.uid + "/cut:"
                                + beforeCut.sourceIndex + "/box:" + beforeBox.sourceIndex);
                            continue;
                        }
                        if (beforeBox.id != afterBox.id || beforeBox.option != afterBox.option
                            || beforeBox.sortOrder != afterBox.sortOrder)
                            AddIssue(report, "文本框属性变化：" + beforeNode.uid + "/cut:"
                                + beforeCut.sourceIndex + "/box:" + beforeBox.sourceIndex);

                        var beforeLayouts = beforeBox.layouts ?? new TextLayoutSnapshot[0];
                        var afterLayouts = afterBox.layouts ?? new TextLayoutSnapshot[0];
                        if (beforeLayouts.Length != afterLayouts.Length)
                        {
                            AddIssue(report, "布局数量变化：" + beforeNode.uid + "/cut:"
                                + beforeCut.sourceIndex + "/box:" + beforeBox.sourceIndex);
                            continue;
                        }
                        foreach (var beforeLayout in beforeLayouts)
                        {
                            if (beforeLayout == null)
                                continue;
                            var afterLayout = RepairTargetLookup.FindLayout(afterBox, beforeLayout.layoutIndex);
                            if (afterLayout == null)
                            {
                                AddIssue(report, "布局序号集合变化：" + beforeNode.uid + "/cut:"
                                    + beforeCut.sourceIndex + "/box:" + beforeBox.sourceIndex
                                    + "/layout:" + beforeLayout.layoutIndex);
                                continue;
                            }
                            if (targets.Contains(TargetKey(beforeNode.uid, beforeCut.sourceIndex,
                                beforeBox.sourceIndex, beforeLayout.layoutIndex)))
                                continue;
                            if (!string.Equals(beforeLayout.rawText ?? string.Empty, afterLayout.rawText ?? string.Empty,
                                StringComparison.Ordinal))
                                AddIssue(report, "未目标文本变化：" + beforeNode.uid + "/cut:"
                                    + beforeCut.sourceIndex + "/box:" + beforeBox.sourceIndex
                                    + "/layout:" + beforeLayout.layoutIndex);
                        }
                    }
                }
            }
        }

        private static string TargetKey(string uid, int cutSourceIndex, int textBoxSourceIndex, int layoutIndex)
        {
            return uid + "|" + cutSourceIndex + "|" + textBoxSourceIndex + "|" + layoutIndex;
        }

        private static bool SameDirectory(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;
            try
            {
                var a = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var b = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void AddIssue(RepairVerificationReport report, string message)
        {
            if (report.GlobalIssues.Count >= MaxIssues)
            {
                if (report.GlobalIssues.Count == MaxIssues)
                    report.GlobalIssues.Add("其余差异已省略。");
                return;
            }
            report.GlobalIssues.Add(message);
        }

        private static RepairVerificationReport Finish(RepairVerificationReport report, Stopwatch timer)
        {
            report.ElapsedMilliseconds = timer.ElapsedMilliseconds;
            return report;
        }
    }
}
