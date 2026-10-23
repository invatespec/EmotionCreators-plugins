using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using EC_SceneAudit.Export;

namespace EC_SceneAudit.Repair
{
    // 只读预检：先核验清单与磁盘 F8 资料包，再用当前 Scene 快照逐项核对。
    // 不修改 TextLayout.msg、节点、播放状态或原资料；任一条冲突都不产生整份通过。
    internal sealed class RepairPreflight
    {
        private const int MaxUidSamples = 5;

        public RepairPreflightReport Run(string planPath, string bundleDirectory, SceneSnapshot currentScene)
        {
            return Run(planPath, bundleDirectory, () => currentScene);
        }

        public RepairPreflightReport Run(string planPath, string bundleDirectory, Func<SceneSnapshot> readCurrent)
        {
            var timer = Stopwatch.StartNew();
            var report = new RepairPreflightReport
            {
                PlanPath = planPath,
                BundlePath = bundleDirectory
            };

            RepairPlan plan;
            try
            {
                plan = RepairPlanReader.ReadFile(planPath);
            }
            catch (RepairInputException ex)
            {
                report.GlobalIssues.Add("修复清单读取失败：" + ex.Message);
                return report;
            }

            RepairBundleSource bundle;
            try
            {
                bundle = RepairBundleReader.Read(bundleDirectory);
            }
            catch (RepairInputException ex)
            {
                report.GlobalIssues.Add("资料包核验失败：" + ex.Message);
                return report;
            }

            // 保存包的定位和 before 先过关，坏来源不会触发当前游戏的全量扫描。
            report = Evaluate(plan, planPath, bundleDirectory, bundle, bundle.Scene);
            if (report.Passed && plan.Changes.Count > 0)
                report = Evaluate(plan, planPath, bundleDirectory, bundle, readCurrent());
            else
            {
                report.CurrentTitle = null;
                foreach (var item in report.Items)
                {
                    item.CurrentText = null;
                    item.CurrentIssues.Clear();
                    item.CurrentIssues.Add("未读取当前 Scene：请先修正清单或资料包问题。");
                }
            }
            report.ElapsedMilliseconds = timer.ElapsedMilliseconds;
            return report;
        }

        // 离线回归直接喂已解析的清单与资料包快照，避免重复读写磁盘。
        internal RepairPreflightReport Evaluate(
            RepairPlan plan,
            string planPath,
            string bundleDirectory,
            RepairBundleSource bundle,
            SceneSnapshot currentScene)
        {
            var report = new RepairPreflightReport
            {
                PlanPath = planPath,
                BundlePath = bundleDirectory,
                ChangeCount = plan.Changes.Count
            };
            report.BundleId = bundle.BundleId;
            report.ManifestSha256 = bundle.ManifestSha256;
            report.SceneTitle = bundle.Scene?.scene?.title;

            if (!string.Equals(plan.Source.BundleId, bundle.BundleId, StringComparison.Ordinal))
                report.GlobalIssues.Add(
                    "修复清单属于另一批资料：清单 bundleId " + plan.Source.BundleId + " ≠ 资料包 " + bundle.BundleId + "。");
            if (!string.Equals(plan.Source.ManifestSha256, bundle.ManifestSha256, StringComparison.OrdinalIgnoreCase))
                report.GlobalIssues.Add("修复清单的清单哈希与资料包不一致。");
            if (report.GlobalIssues.Count > 0)
                return report;

            if (currentScene == null || currentScene.nodes == null || currentScene.nodes.Length == 0)
            {
                report.GlobalIssues.Add("当前 Scene 没有可用节点数据，请在 HEdit 节点总览中重试。");
                return report;
            }

            report.CurrentTitle = currentScene.scene?.title;
            CheckSceneStructure(bundle.Scene, currentScene, report);

            for (var index = 0; index < plan.Changes.Count; index++)
                report.Items.Add(Evaluate(plan.Changes[index], index, bundle.Scene, currentScene));
            return report;
        }

        private static void CheckSceneStructure(
            SceneSnapshot source,
            SceneSnapshot current,
            RepairPreflightReport report)
        {
            if (!string.Equals(source.scene?.title ?? string.Empty, current.scene?.title ?? string.Empty, StringComparison.Ordinal))
                report.GlobalIssues.Add(
                    "当前 Scene 标题与资料包不一致：当前「" + (current.scene?.title ?? string.Empty)
                    + "」≠ 资料包「" + (source.scene?.title ?? string.Empty) + "」。");

            var sourceUids = CollectUids(source);
            var currentUids = CollectUids(current);
            if (sourceUids.Count != source.nodes.Length || currentUids.Count != current.nodes.Length)
                report.GlobalIssues.Add("节点 UID 缺失或重复，无法唯一定位当前 Scene。");
            var missing = new List<string>();
            var added = new List<string>();
            foreach (var uid in sourceUids)
            {
                if (!currentUids.Contains(uid))
                    missing.Add(uid);
            }
            foreach (var uid in currentUids)
            {
                if (!sourceUids.Contains(uid))
                    added.Add(uid);
            }
            if (missing.Count > 0 || added.Count > 0)
                report.GlobalIssues.Add(
                    "当前 Scene 节点集合与资料包不一致：缺少 " + missing.Count + " 个"
                    + Sample(missing) + "，新增 " + added.Count + " 个" + Sample(added) + "。");
        }

        private static HashSet<string> CollectUids(SceneSnapshot scene)
        {
            var uids = new HashSet<string>(StringComparer.Ordinal);
            if (scene?.nodes == null)
                return uids;
            foreach (var node in scene.nodes)
            {
                if (node != null && !string.IsNullOrEmpty(node.uid))
                    uids.Add(node.uid);
            }
            return uids;
        }

        private static string Sample(List<string> values)
        {
            if (values.Count == 0)
                return string.Empty;
            var builder = new StringBuilder("（");
            for (var index = 0; index < values.Count && index < MaxUidSamples; index++)
            {
                if (index > 0)
                    builder.Append('、');
                builder.Append(values[index]);
            }
            if (values.Count > MaxUidSamples)
                builder.Append("…");
            builder.Append('）');
            return builder.ToString();
        }

        private static RepairPreflightItem Evaluate(
            RepairChange change,
            int index,
            SceneSnapshot source,
            SceneSnapshot current)
        {
            var item = new RepairPreflightItem
            {
                Index = index,
                Pointer = change.Target.Reference.Pointer,
                Before = change.Before,
                After = change.After
            };
            RepairLayoutPosition position = null;
            if (!RepairLayoutPointer.TryParse(change.Target.Reference.Pointer, out position))
            {
                item.SourceIssues.Add("清单位置不是 scene layout.rawText 引用");
                item.Label = change.Target.NodeUid + " · 布局 " + change.Target.LayoutIndex;
                return item;
            }

            var sourceLocation = LocateSource(source, change, position, item.SourceIssues);
            ResolveCurrent(change, current, sourceLocation, item);
            item.Label = BuildLabel(change, sourceLocation, item);
            return item;
        }

        private static SourceLocation LocateSource(
            SceneSnapshot scene,
            RepairChange change,
            RepairLayoutPosition position,
            List<string> issues)
        {
            var nodes = scene?.nodes ?? new NodeSnapshot[0];
            if (position.NodeIndex >= nodes.Length)
            {
                issues.Add("资料包 scene 中不存在 nodes[" + position.NodeIndex + "]");
                return null;
            }

            var node = nodes[position.NodeIndex];
            if (node == null)
            {
                issues.Add("资料包 scene 的 nodes[" + position.NodeIndex + "] 为空");
                return null;
            }
            if (!string.Equals(node.uid, change.Target.NodeUid, StringComparison.Ordinal))
                issues.Add("Part 标识与清单不一致：资料包 " + node.uid + " ≠ 清单 " + change.Target.NodeUid);
            if (node.part == null)
            {
                issues.Add("资料包 scene 的该节点没有 Part 数据");
                return null;
            }

            var cuts = node.part.cuts ?? new CutSnapshot[0];
            if (position.CutIndex >= cuts.Length || cuts[position.CutIndex] == null)
            {
                issues.Add("资料包 scene 中不存在 cuts[" + position.CutIndex + "]");
                return null;
            }
            var cut = cuts[position.CutIndex];
            if (cut.sourceIndex != change.Target.CutSourceIndex)
                issues.Add("CUT 源序号与清单不一致：资料包 " + cut.sourceIndex + " ≠ 清单 " + change.Target.CutSourceIndex);

            var boxes = cut.textBoxes ?? new TextBoxSnapshot[0];
            if (position.TextBoxIndex >= boxes.Length || boxes[position.TextBoxIndex] == null)
            {
                issues.Add("资料包 scene 中不存在 textBoxes[" + position.TextBoxIndex + "]");
                return null;
            }
            var box = boxes[position.TextBoxIndex];
            if (box.sourceIndex != change.Target.TextBoxSourceIndex)
                issues.Add("文本框源序号与清单不一致：资料包 " + box.sourceIndex + " ≠ 清单 " + change.Target.TextBoxSourceIndex);

            var layouts = box.layouts ?? new TextLayoutSnapshot[0];
            if (position.LayoutIndex >= layouts.Length || layouts[position.LayoutIndex] == null)
            {
                issues.Add("资料包 scene 中不存在 layouts[" + position.LayoutIndex + "]");
                return null;
            }
            var layout = layouts[position.LayoutIndex];
            if (layout.layoutIndex != change.Target.LayoutIndex)
                issues.Add("布局序号与清单不一致：资料包 " + layout.layoutIndex + " ≠ 清单 " + change.Target.LayoutIndex);
            if (layout.rawText == null)
            {
                issues.Add("资料包该位置缺少原始文本");
                return null;
            }
            if (RepairTargetLookup.HasUnavailableWarning(layout))
                issues.Add("资料包标记该布局文本不可用");
            if (!string.Equals(layout.rawText, change.Before, StringComparison.Ordinal))
                issues.Add("清单原文与资料包 scene 不一致");
            return new SourceLocation { Node = node, Cut = cut, Box = box, Layout = layout };
        }

        private static void ResolveCurrent(
            RepairChange change,
            SceneSnapshot current,
            SourceLocation source,
            RepairPreflightItem item)
        {
            var node = RepairTargetLookup.FindNode(current, change.Target.NodeUid);
            if (node == null)
            {
                item.CurrentIssues.Add("当前 Scene 找不到唯一的 Part UID：" + change.Target.NodeUid);
                return;
            }
            if (node.part == null)
            {
                item.CurrentIssues.Add("当前节点没有 Part 数据");
                return;
            }
            if (source?.Node?.part != null
                && !string.Equals(node.part.uid, source.Node.part.uid, StringComparison.Ordinal))
                item.CurrentIssues.Add(
                    "Part 标识不一致：当前 " + node.part.uid + " ≠ 资料包 " + source.Node.part.uid);

            var cut = RepairTargetLookup.FindCut(node.part, change.Target.CutSourceIndex);
            if (cut == null)
            {
                item.CurrentIssues.Add("当前 Scene 找不到唯一的 CUT 源序号 " + change.Target.CutSourceIndex);
                return;
            }
            var box = RepairTargetLookup.FindTextBox(cut, change.Target.TextBoxSourceIndex);
            if (box == null)
            {
                item.CurrentIssues.Add("当前 CUT 找不到唯一的文本框源序号 " + change.Target.TextBoxSourceIndex);
                return;
            }
            if (source?.Box != null)
            {
                if (box.id != source.Box.id)
                    item.CurrentIssues.Add("文本框 id 不一致：当前 " + box.id + " ≠ 资料包 " + source.Box.id);
                if (box.option != source.Box.option)
                    item.CurrentIssues.Add(
                        "文本框选项状态不一致：当前 " + box.option + " ≠ 资料包 " + source.Box.option);
            }

            var layout = RepairTargetLookup.FindLayout(box, change.Target.LayoutIndex);
            if (layout == null)
            {
                item.CurrentIssues.Add("当前文本框找不到唯一的布局 " + change.Target.LayoutIndex);
                return;
            }
            if (layout.rawText == null)
            {
                item.CurrentIssues.Add("当前布局文本不可用");
                return;
            }
            if (RepairTargetLookup.HasUnavailableWarning(layout))
            {
                item.CurrentIssues.Add("当前布局读取失败（text-layout-read-failed）");
                return;
            }
            item.CurrentText = layout.rawText;
            if (!string.Equals(layout.rawText, change.Before, StringComparison.Ordinal))
                item.CurrentIssues.Add("当前原文与清单不一致");
        }

        private static string BuildLabel(
            RepairChange change,
            SourceLocation source,
            RepairPreflightItem item)
        {
            var name = source?.Node?.name ?? change.Target.NodeUid;
            var option = source?.Box != null && source.Box.option ? " · 选项" : string.Empty;
            return name
                + " · CUT " + (change.Target.CutSourceIndex + 1)
                + " · 文本框 " + (change.Target.TextBoxSourceIndex + 1)
                + option
                + " · 布局 " + change.Target.LayoutIndex;
        }

        public static string FormatText(RepairPreflightReport report)
        {
            var builder = new StringBuilder();
            builder.AppendLine("EC SceneAudit 修复预检（只读，未修改任何游戏数据）");
            builder.AppendLine("检查时间：" + report.CheckedAt.ToString("O") + "；耗时：" + report.ElapsedMilliseconds + " ms");
            builder.AppendLine("修复清单：" + report.PlanPath);
            builder.AppendLine("资料包：" + report.BundlePath);
            builder.AppendLine("bundleId：" + Display(report.BundleId));
            builder.AppendLine("资料包清单哈希：" + Display(report.ManifestSha256));
            builder.AppendLine("Scene 标题：资料包 " + Display(report.SceneTitle) + " / 当前 " + Display(report.CurrentTitle));
            builder.AppendLine("清单条目：" + report.ChangeCount);
            builder.AppendLine("结论：" + report.Verdict);
            foreach (var issue in report.GlobalIssues)
                builder.AppendLine("全局问题：" + issue);

            foreach (var item in report.Items)
            {
                builder.AppendLine();
                builder.AppendLine((item.Index + 1) + ". " + item.Label + " —— " + (item.Passed ? "原文一致" : "冲突"));
                builder.AppendLine("   位置：" + item.Pointer);
                builder.AppendLine("   原文：" + Preview(item.Before));
                builder.AppendLine("   新文：" + Preview(item.After));
                builder.AppendLine("   当前：" + Preview(item.CurrentText));
                foreach (var issue in item.SourceIssues)
                    builder.AppendLine("   - 资料包：" + issue);
                foreach (var issue in item.CurrentIssues)
                    builder.AppendLine("   - 当前：" + issue);
            }

            if (report.Items.Count == 0 && report.GlobalIssues.Count == 0)
                builder.AppendLine("清单没有需要核验的修改。");
            builder.AppendLine();
            builder.AppendLine("说明：结论只表示清单原文与当前 Scene 文本一致，不代表脚本运行结果正确，也不构成已应用修改。");
            return builder.ToString();
        }

        private static string Display(string value)
        {
            return string.IsNullOrEmpty(value) ? "(未提供)" : value;
        }

        private static string Preview(string text)
        {
            if (text == null)
                return "(缺失)";
            var flat = text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            return flat.Length <= 160 ? flat : flat.Substring(0, 160) + "…";
        }

        private sealed class SourceLocation
        {
            public NodeSnapshot Node;
            public CutSnapshot Cut;
            public TextBoxSnapshot Box;
            public TextLayoutSnapshot Layout;
        }
    }
}
