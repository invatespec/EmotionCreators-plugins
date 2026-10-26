using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Xml;

namespace EC_SceneAudit.Export
{
    internal static class AiSummaryBuilder
    {
        private static readonly Regex VariableTokenRegex = new Regex(
            @"#(?<name>[A-Za-z_][A-Za-z0-9_.:-]*)#",
            RegexOptions.Compiled);

        private static readonly Regex EqualityRegex = new Regex(
            @"#?(?<name>[A-Za-z_][A-Za-z0-9_.:-]*)#?\s*==\s*(?<value>-?\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex LessOrEqualRegex = new Regex(
            @"#?(?<name>[A-Za-z_][A-Za-z0-9_.:-]*)#?\s*<=\s*(?<value>-?\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static AiSummarySnapshot Build(
            SceneSnapshot snapshot,
            string evidencePath)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));

            var parts = new List<AiPartIndex>();
            var scripts = new List<AiScriptSummary>();
            var findings = new List<AiFinding>();
            var stats = new AiSceneStatistics
            {
                nodes = snapshot.nodes == null ? 0 : snapshot.nodes.Length,
                warnings = snapshot.warnings == null ? 0 : snapshot.warnings.Length
            };

            if (snapshot.nodes != null)
            {
                for (var nodeIndex = 0; nodeIndex < snapshot.nodes.Length; nodeIndex++)
                {
                    AddNodeSummary(
                        snapshot.nodes[nodeIndex],
                        nodeIndex,
                        parts,
                        scripts,
                        findings,
                        stats);
                }
            }

            return new AiSummarySnapshot
            {
                schemaVersion = "1.0-ai",
                sourceEvidenceFile = evidencePath,
                scene = new AiSceneSummary
                {
                    title = snapshot.scene == null ? string.Empty : snapshot.scene.title,
                    exportedAt = snapshot.scene == null ? string.Empty : snapshot.scene.exportedAt
                },
                statistics = stats,
                parts = parts.ToArray(),
                scripts = scripts.ToArray(),
                findings = findings.ToArray()
            };
        }

        private static void AddNodeSummary(
            NodeSnapshot node,
            int nodeIndex,
            List<AiPartIndex> parts,
            List<AiScriptSummary> scripts,
            List<AiFinding> findings,
            AiSceneStatistics stats)
        {
            if (node == null || node.part == null)
                return;

            var partRef = "#/nodes/" + nodeIndex + "/part";
            var textBoxCount = 0;
            var cutCount = node.part.cuts == null ? 0 : node.part.cuts.Length;
            stats.parts++;
            stats.cuts += cutCount;

            if (node.part.cuts != null)
            {
                for (var cutIndex = 0; cutIndex < node.part.cuts.Length; cutIndex++)
                {
                    var cut = node.part.cuts[cutIndex];
                    if (cut == null || cut.textBoxes == null)
                        continue;

                    textBoxCount += cut.textBoxes.Length;
                    stats.textBoxes += cut.textBoxes.Length;
                    for (var textBoxIndex = 0; textBoxIndex < cut.textBoxes.Length; textBoxIndex++)
                    {
                        AddTextBoxScripts(
                            node,
                            nodeIndex,
                            cut,
                            cutIndex,
                            cut.textBoxes[textBoxIndex],
                            textBoxIndex,
                            scripts,
                            findings,
                            stats);
                    }
                }
            }

            AddNoExitFinding(node, nodeIndex, findings);

            parts.Add(new AiPartIndex
            {
                nodeUid = node.uid,
                uid = node.part.uid,
                name = node.part.name,
                type = node.part.type,
                cutCount = cutCount,
                textBoxCount = textBoxCount,
                rawRef = partRef
            });
        }

        // ADV 运行时只按最后 CUT 的选项数选下一步，NotUsed 槽即使有目标也不参与；编辑器 NodeCheck 覆盖不到。
        private static void AddNoExitFinding(NodeSnapshot node, int nodeIndex, List<AiFinding> findings)
        {
            if (!string.Equals(node.kind, "ADV", StringComparison.Ordinal)
                || node.isSystemNode
                || node.outputs == null
                // CUT 读不到时无法判断运行时出口，交给 part-read-failed warning，不报死路。
                || node.part.cuts == null
                || node.part.cuts.Length == 0)
                return;
            if (HasScriptExit(node.part))
                return;

            var optionCount = LastCutOptionCount(node.part.cuts);
            var usableCount = optionCount > 0 ? optionCount : 1;
            var emptySlots = new List<string>();
            for (var slot = 0; slot < usableCount && slot < 5; slot++)
            {
                if (!HasTarget(node.outputs, slot))
                    emptySlots.Add((slot + 1).ToString());
            }
            if (emptySlots.Count == 0)
                return;

            var name = string.IsNullOrEmpty(node.part.name) ? node.name : node.part.name;
            findings.Add(new AiFinding(
                "part-no-exit",
                name + " 的出口 " + string.Join("、", emptySlots) + " 没有目标，播放到这里不会继续。",
                "#/nodes/" + nodeIndex));
        }

        // 运行时按 cuts 数组最后一个 CUT 决定出口：有选项走选项槽，否则只读 slot0（ADVPlay.OnClick / LoadCut）。
        private static int LastCutOptionCount(CutSnapshot[] cuts)
        {
            for (var index = cuts == null ? -1 : cuts.Length - 1; index >= 0; index--)
            {
                if (cuts[index] != null)
                    return cuts[index].endCut ? cuts[index].optionCount : 0;
            }
            return 0;
        }

        // 任一跳转类命令（含动态目标和外部脚本）都可能接管后续，静态无法确认出口缺失。
        private static bool HasScriptExit(PartSnapshot part)
        {
            for (var cutIndex = 0; part.cuts != null && cutIndex < part.cuts.Length; cutIndex++)
            {
                var boxes = part.cuts[cutIndex]?.textBoxes;
                for (var boxIndex = 0; boxes != null && boxIndex < boxes.Length; boxIndex++)
                {
                    var layouts = boxes[boxIndex]?.layouts;
                    for (var layoutIndex = 0; layouts != null && layoutIndex < layouts.Length; layoutIndex++)
                    {
                        var blocks = layouts[layoutIndex]?.scriptBlocks;
                        for (var blockIndex = 0; blocks != null && blockIndex < blocks.Length; blockIndex++)
                        {
                            var references = blocks[blockIndex]?.references;
                            for (var refIndex = 0; references != null && refIndex < references.Length; refIndex++)
                            {
                                if (references[refIndex] != null
                                    && string.Equals(references[refIndex].kind, "flow", StringComparison.Ordinal))
                                    return true;
                            }
                        }
                    }
                }
            }
            return false;
        }

        private static bool HasTarget(OutputSlotSnapshot[] outputs, int slot)
        {
            for (var index = 0; index < outputs.Length; index++)
            {
                if (outputs[index] != null && outputs[index].slot == slot)
                    return !string.IsNullOrEmpty(outputs[index].targetUid);
            }
            return false;
        }

        private static void AddTextBoxScripts(
            NodeSnapshot node,
            int nodeIndex,
            CutSnapshot cut,
            int cutIndex,
            TextBoxSnapshot textBox,
            int textBoxIndex,
            List<AiScriptSummary> scripts,
            List<AiFinding> findings,
            AiSceneStatistics stats)
        {
            if (textBox == null || textBox.layouts == null)
                return;

            for (var layoutIndex = 0; layoutIndex < textBox.layouts.Length; layoutIndex++)
            {
                var layout = textBox.layouts[layoutIndex];
                if (layout == null || layout.scriptBlocks == null)
                    continue;

                for (var scriptIndex = 0; scriptIndex < layout.scriptBlocks.Length; scriptIndex++)
                {
                    var block = layout.scriptBlocks[scriptIndex];
                    if (block == null)
                        continue;

                    var rawRef = "#/nodes/" + nodeIndex
                        + "/part/cuts/" + cutIndex
                        + "/textBoxes/" + textBoxIndex
                        + "/layouts/" + layoutIndex
                        + "/scriptBlocks/" + scriptIndex;
                    var summary = BuildScriptSummary(block, rawRef);
                    scripts.Add(summary);
                    stats.scriptBlocks++;
                    AddScriptFindings(summary, findings);
                }
            }
        }

        private static AiScriptSummary BuildScriptSummary(
            ScriptBlockSnapshot block,
            string rawRef)
        {
            var summary = new AiScriptSummary
            {
                rawRef = rawRef,
                parseStatus = "invalid",
                commands = block.commands ?? new string[0],
                declarations = new AiVariable[0],
                reads = new AiVariable[0],
                writes = new AiVariable[0],
                steps = new AiLogicStep[0],
                events = new AiEventTrigger[0],
                findings = new AiFinding[0]
            };

            var body = ExtractScriptBody(block.raw);
            var declarations = new List<AiVariable>();
            var reads = new List<AiVariable>();
            var writes = new List<AiVariable>();
            var steps = new List<AiLogicStep>();
            var events = new List<AiEventTrigger>();
            var findings = new List<AiFinding>();
            ExtractVariableReads(body, block.sourceOffset, rawRef, reads);
            AddReferenceFindings(block.references, rawRef, findings);

            XmlDocument document;
            try
            {
                document = new XmlDocument { PreserveWhitespace = true };
                document.LoadXml("<root>" + body + "</root>");
            }
            catch
            {
                findings.Add(new AiFinding(
                    "script-xml-invalid",
                    "脚本 XML 无法解析，摘要只保留命令列表和来源定位。",
                    rawRef));
                summary.findings = findings.ToArray();
                return summary;
            }

            summary.parseStatus = "valid";
            var referenceCursor = 0;
            foreach (XmlNode child in document.DocumentElement.ChildNodes)
            {
                var step = BuildStep(
                    child,
                    block.sourceOffset,
                    rawRef,
                    block.references,
                    ref referenceCursor,
                    declarations,
                    reads,
                    writes,
                    events);
                if (step != null)
                    steps.Add(step);
            }

            AddConditionOverlapFindings(steps, findings);
            summary.declarations = declarations.ToArray();
            summary.reads = reads.ToArray();
            summary.writes = writes.ToArray();
            summary.steps = steps.ToArray();
            summary.events = events.ToArray();
            summary.findings = findings.ToArray();
            return summary;
        }

        private static AiLogicStep BuildStep(
            XmlNode node,
            int blockOffset,
            string rawRef,
            ScriptReferenceSnapshot[] references,
            ref int referenceCursor,
            List<AiVariable> declarations,
            List<AiVariable> reads,
            List<AiVariable> writes,
            List<AiEventTrigger> events)
        {
            var element = node as XmlElement;
            if (element == null)
                return null;

            var command = element.Name;
            if (!VariableModScanner.IsKnownCommand(command))
                return null;

            var attributes = ToAttributes(element);
            var sourceOffset = blockOffset;
            var targetStatus = FindTargetStatus(
                command,
                attributes,
                references,
                ref referenceCursor,
                out var referenceOffset);
            if (referenceOffset >= 0)
                sourceOffset = referenceOffset;

            var stepRef = rawRef + "/command:" + command + "@" + sourceOffset;
            CollectCommandVariables(
                command,
                attributes,
                sourceOffset,
                stepRef,
                declarations,
                reads,
                writes);

            var step = new AiLogicStep
            {
                kind = GetStepKind(command),
                name = command,
                attributes = attributes,
                targetStatus = targetStatus,
                sourceOffset = sourceOffset,
                rawRef = stepRef,
                children = new AiLogicStep[0]
            };

            var childSteps = new List<AiLogicStep>();
            foreach (XmlNode child in element.ChildNodes)
            {
                var childStep = BuildStep(
                    child,
                    blockOffset,
                    rawRef,
                    references,
                    ref referenceCursor,
                    declarations,
                    reads,
                    writes,
                    events);
                if (childStep != null)
                    childSteps.Add(childStep);
            }
            step.children = childSteps.ToArray();

            if (command.StartsWith("event-", StringComparison.OrdinalIgnoreCase))
            {
                events.Add(new AiEventTrigger
                {
                    kind = command,
                    attributes = attributes,
                    targetStatus = step.targetStatus,
                    sourceOffset = sourceOffset,
                    rawRef = stepRef
                });
            }
            return step;
        }

        private static void CollectCommandVariables(
            string command,
            AttributeSnapshot[] attributes,
            int sourceOffset,
            string rawRef,
            List<AiVariable> declarations,
            List<AiVariable> reads,
            List<AiVariable> writes)
        {
            var name = FindAttribute(attributes, "name")
                ?? FindAttribute(attributes, "variable")
                ?? FindAttribute(attributes, "var")
                ?? FindAttribute(attributes, "result");
            var expression = FindAttribute(attributes, "value")
                ?? FindAttribute(attributes, "expression")
                ?? FindAttribute(attributes, "cond")
                ?? FindAttribute(attributes, "condition");

            if (string.Equals(command, "variable", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(name))
            {
                declarations.Add(CreateVariable(name, "declare", expression, sourceOffset, rawRef));
            }

            if (IsWriteCommand(command) && !string.IsNullOrEmpty(name))
            {
                writes.Add(CreateVariable(
                    name,
                    command,
                    expression,
                    sourceOffset,
                    rawRef));
            }

            foreach (var attribute in attributes)
                ExtractVariableReads(attribute.value, sourceOffset, rawRef, reads);
        }

        private static void ExtractVariableReads(
            string text,
            int sourceOffset,
            string rawRef,
            List<AiVariable> reads)
        {
            if (string.IsNullOrEmpty(text))
                return;

            foreach (Match match in VariableTokenRegex.Matches(text))
            {
                var name = match.Groups["name"].Value;
                reads.Add(CreateVariable(
                    name,
                    "read",
                    null,
                    sourceOffset + match.Index,
                    rawRef));
            }
        }

        private static void AddConditionOverlapFindings(
            List<AiLogicStep> steps,
            List<AiFinding> findings)
        {
            var equalities = new List<ConditionValue>();
            var lessOrEquals = new List<ConditionValue>();
            CollectConditions(steps, equalities, lessOrEquals);
            foreach (var equality in equalities)
            {
                foreach (var lessOrEqual in lessOrEquals)
                {
                    if (!string.Equals(equality.Name, lessOrEqual.Name, StringComparison.OrdinalIgnoreCase)
                        || equality.Value != lessOrEqual.Value)
                        continue;

                    findings.Add(new AiFinding(
                        "condition-overlap",
                        equality.Name + " == " + equality.Value
                        + " 与 " + lessOrEqual.Name + " <= " + lessOrEqual.Value
                        + " 存在条件重叠。",
                        equality.RawRef));
                }
            }
        }

        private static void CollectConditions(
            IEnumerable<AiLogicStep> steps,
            List<ConditionValue> equalities,
            List<ConditionValue> lessOrEquals)
        {
            foreach (var step in steps)
            {
                if (step == null)
                    continue;
                if (string.Equals(step.name, "if", StringComparison.OrdinalIgnoreCase))
                {
                    var condition = ReadCondition(step.attributes);
                    var equality = MatchCondition(EqualityRegex, condition, step.rawRef);
                    if (equality != null)
                        equalities.Add(equality);
                    var lessOrEqual = MatchCondition(LessOrEqualRegex, condition, step.rawRef);
                    if (lessOrEqual != null)
                        lessOrEquals.Add(lessOrEqual);
                }
                CollectConditions(step.children, equalities, lessOrEquals);
            }
        }

        private static ConditionValue MatchCondition(
            Regex regex,
            string condition,
            string rawRef)
        {
            if (string.IsNullOrEmpty(condition))
                return null;
            var match = regex.Match(condition);
            return match.Success
                ? new ConditionValue
                {
                    Name = match.Groups["name"].Value,
                    Value = match.Groups["value"].Value,
                    RawRef = rawRef
                }
                : null;
        }

        private static string ReadCondition(AttributeSnapshot[] attributes)
        {
            var left = FindAttribute(attributes, "value1");
            var right = FindAttribute(attributes, "value2");
            var comparison = FindAttribute(attributes, "operator");
            if (left != null && right != null && comparison != null)
            {
                if (string.Equals(comparison, "eq", StringComparison.OrdinalIgnoreCase))
                    return left + " == " + right;
                if (string.Equals(comparison, "lteq", StringComparison.OrdinalIgnoreCase))
                    return left + " <= " + right;
                return null;
            }
            return FindAttribute(attributes, "cond")
                ?? FindAttribute(attributes, "condition")
                ?? FindAttribute(attributes, "value");
        }

        private static void AddScriptFindings(
            AiScriptSummary summary,
            List<AiFinding> findings)
        {
            if (summary.findings == null)
                return;
            findings.AddRange(summary.findings);
        }

        private static void AddReferenceFindings(
            ScriptReferenceSnapshot[] references,
            string rawRef,
            List<AiFinding> findings)
        {
            if (references == null)
                return;
            foreach (var reference in references)
            {
                if (reference == null)
                    continue;
                if (reference.targetStatus == "missing")
                {
                    findings.Add(new AiFinding(
                        "missing-target",
                        reference.name + " 的静态目标不存在或缺少必要属性。",
                        rawRef + "/reference@" + reference.sourceOffset));
                }
                else if (reference.targetStatus == "dynamic")
                {
                    findings.Add(new AiFinding(
                        "dynamic-target",
                        reference.name + " 的目标依赖运行时变量，无法静态确认。",
                        rawRef + "/reference@" + reference.sourceOffset));
                }
                else if (reference.targetStatus == "unverified"
                         && HasStaticTarget(reference.name))
                {
                    findings.Add(new AiFinding(
                        "low-confidence-target",
                        reference.name + " 的静态目标未能在当前证据范围内确认。",
                        rawRef + "/reference@" + reference.sourceOffset));
                }
            }
        }

        private static bool HasStaticTarget(string command)
        {
            return string.Equals(command, "goto-cut", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "goto-part", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "event-part", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "call-sub", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "sub-script", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "load-script", StringComparison.OrdinalIgnoreCase);
        }

        private static AttributeSnapshot[] ToAttributes(XmlElement element)
        {
            var attributes = new List<AttributeSnapshot>();
            foreach (XmlAttribute attribute in element.Attributes)
            {
                attributes.Add(new AttributeSnapshot
                {
                    key = attribute.Name,
                    value = attribute.Value
                });
            }
            return attributes.ToArray();
        }

        private static AiVariable CreateVariable(
            string name,
            string operation,
            string expression,
            int sourceOffset,
            string rawRef)
        {
            var normalized = name == null ? string.Empty : name.Trim().Trim('#');
            return new AiVariable
            {
                name = normalized,
                category = normalized.StartsWith("SYS.", StringComparison.OrdinalIgnoreCase)
                    ? "system"
                    : "user",
                operation = operation,
                expression = expression,
                sourceOffset = sourceOffset,
                rawRef = rawRef
            };
        }

        private static bool IsWriteCommand(string command)
        {
            return string.Equals(command, "set", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "add", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "compute", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "delete", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetStepKind(string command)
        {
            if (string.Equals(command, "if", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "loop", StringComparison.OrdinalIgnoreCase))
                return "control";
            if (command.StartsWith("event-", StringComparison.OrdinalIgnoreCase))
                return "event";
            if (command.StartsWith("goto-", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "load-script", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "call-sub", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "sub-script", StringComparison.OrdinalIgnoreCase))
                return "flow";
            return "action";
        }

        private static string FindTargetStatus(
            string command,
            AttributeSnapshot[] attributes,
            ScriptReferenceSnapshot[] references,
            ref int referenceCursor,
            out int referenceOffset)
        {
            referenceOffset = -1;
            if (references != null)
            {
                while (referenceCursor < references.Length)
                {
                    var reference = references[referenceCursor++];
                    if (reference != null
                        && string.Equals(reference.name, command, StringComparison.OrdinalIgnoreCase))
                    {
                        referenceOffset = reference.sourceOffset;
                        return reference.targetStatus;
                    }
                }
            }
            if (!command.StartsWith("goto-", StringComparison.OrdinalIgnoreCase)
                && !command.StartsWith("event-", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(command, "load-script", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(command, "call-sub", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(command, "sub-script", StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            var target = FindAttribute(attributes, "name")
                ?? FindAttribute(attributes, "index")
                ?? FindAttribute(attributes, "file");
            return string.IsNullOrEmpty(target)
                ? "missing"
                : (target.IndexOf('#') >= 0 ? "dynamic" : "unverified");
        }

        private static string FindAttribute(AttributeSnapshot[] attributes, string key)
        {
            if (attributes == null)
                return null;
            foreach (var attribute in attributes)
            {
                if (attribute != null && string.Equals(attribute.key, key, StringComparison.OrdinalIgnoreCase))
                    return attribute.value;
            }
            return null;
        }

        private static string ExtractScriptBody(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return string.Empty;
            var start = raw.IndexOf('>');
            var end = raw.LastIndexOf("</", StringComparison.OrdinalIgnoreCase);
            return start >= 0 && end > start
                ? raw.Substring(start + 1, end - start - 1)
                : raw;
        }

        private sealed class ConditionValue
        {
            public string Name;
            public string Value;
            public string RawRef;
        }
    }
}
