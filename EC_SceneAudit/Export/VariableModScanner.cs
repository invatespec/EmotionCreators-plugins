using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;

namespace EC_SceneAudit.Export
{
    internal sealed class VariableModScanner
    {
        private static readonly Regex ScriptRegex = new Regex(
            @"<script\b[^>]*>.*?</script\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static readonly Regex ScriptOpenRegex = new Regex(
            @"<script\b[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static readonly Regex CommandRegex = new Regex(
            @"<\s*(?<name>[A-Za-z_][A-Za-z0-9_.-]*)(?<tail>[^>]*)>",
            RegexOptions.Singleline);

        private static readonly Regex AttributeRegex = new Regex(
            @"(?<key>[A-Za-z_][A-Za-z0-9_.:-]*)\s*=\s*(?:""(?<double>[^""]*)""|'(?<single>[^']*)')",
            RegexOptions.Singleline);

        private static readonly Regex VariableRegex = new Regex(
            @"#(?<name>[^#\r\n<>]+)#",
            RegexOptions.Singleline);

        private static readonly HashSet<string> KnownCommands =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "initialize",
                "invisible",
                "variable",
                "delete",
                "set",
                "compute",
                "add",
                "random",
                "if",
                "loop",
                "break",
                "goto-cut",
                "goto-part",
                "event-next",
                "event-select",
                "event-part",
                "event-update",
                "sub-script",
                "call-sub",
                "load-script",
                "reset-script",
                "char-ctrl",
                "camera-ctrl",
                "item-ctrl",
                "anime-cut",
                "h-part-set",
                "array",
                "array-set",
                "array-get",
                "calc-update"
            };

        private static readonly HashSet<string> FlowCommands =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "goto-cut",
                "goto-part",
                "event-next",
                "event-select",
                "event-part",
                "event-update",
                "call-sub",
                "sub-script",
                "load-script",
                "reset-script"
            };

        public static bool IsKnownCommand(string command)
        {
            return !string.IsNullOrEmpty(command) && KnownCommands.Contains(command);
        }

        public TextLayoutSnapshot Scan(
            string rawText,
            string externalScriptRoot,
            ISet<string> knownPartNames,
            int currentCutCount)
        {
            var layout = new TextLayoutSnapshot
            {
                rawText = rawText ?? string.Empty,
                containsScript = false,
                scriptBlocks = new ScriptBlockSnapshot[0],
                variableReferences = new VariableReferenceSnapshot[0],
                warnings = new SnapshotWarning[0]
            };

            var warnings = new List<SnapshotWarning>();
            var variables = ExtractVariables(layout.rawText);
            var scripts = new List<ScriptBlockSnapshot>();
            var parsedScriptOffsets = new HashSet<int>();

            foreach (Match scriptMatch in ScriptRegex.Matches(layout.rawText))
            {
                parsedScriptOffsets.Add(scriptMatch.Index);
                layout.containsScript = true;
                var scriptRaw = scriptMatch.Value;
                var bodyStart = scriptRaw.IndexOf('>');
                var bodyEnd = scriptRaw.LastIndexOf("</", StringComparison.OrdinalIgnoreCase);
                var body = bodyStart >= 0 && bodyEnd > bodyStart
                    ? scriptRaw.Substring(bodyStart + 1, bodyEnd - bodyStart - 1)
                    : string.Empty;

                var block = new ScriptBlockSnapshot
                {
                    raw = scriptRaw,
                    sourceOffset = scriptMatch.Index,
                    commands = new string[0],
                    attributes = new AttributeSnapshot[0],
                    references = new ScriptReferenceSnapshot[0]
                };

                if (!TryParseXml(body))
                {
                    warnings.Add(new SnapshotWarning(
                        "script-xml-invalid",
                        "脚本块 XML 无法完整解析，已保留原文并使用宽松标签扫描。",
                        "textOffset:" + scriptMatch.Index));
                }

                var commands = new List<string>();
                var references = new List<ScriptReferenceSnapshot>();
                foreach (Match commandMatch in CommandRegex.Matches(body))
                {
                    var commandName = commandMatch.Groups["name"].Value;
                    if (!IsKnownCommand(commandName))
                        continue;

                    commands.Add(commandName);
                    var attributes = ParseAttributes(commandMatch.Groups["tail"].Value);
                    var commandOffset = scriptMatch.Index + bodyStart + 1 + commandMatch.Index;
                    var reference = new ScriptReferenceSnapshot
                    {
                        kind = FlowCommands.Contains(commandName) ? "flow" : "command",
                        name = commandName,
                        attributes = attributes.ToArray(),
                        sourceOffset = commandOffset,
                        targetStatus = DetermineTargetStatus(
                            commandName,
                            attributes,
                            externalScriptRoot,
                            knownPartNames,
                            currentCutCount,
                            warnings)
                    };
                    references.Add(reference);
                }

                block.commands = commands.ToArray();
                block.references = references.ToArray();
                scripts.Add(block);
            }

            foreach (Match scriptOpenMatch in ScriptOpenRegex.Matches(layout.rawText))
            {
                if (parsedScriptOffsets.Contains(scriptOpenMatch.Index))
                    continue;

                layout.containsScript = true;
                warnings.Add(new SnapshotWarning(
                    "script-xml-invalid",
                    "脚本块缺少闭合的 </script> 标签，已保留原文。",
                    "textOffset:" + scriptOpenMatch.Index));
            }

            layout.variableReferences = variables.ToArray();
            layout.scriptBlocks = scripts.ToArray();
            layout.warnings = warnings.ToArray();
            return layout;
        }

        private static List<VariableReferenceSnapshot> ExtractVariables(string rawText)
        {
            var result = new List<VariableReferenceSnapshot>();
            foreach (Match match in VariableRegex.Matches(rawText))
            {
                var name = match.Groups["name"].Value.Trim();
                var category = "user";
                if (name.Length == 0 || name.IndexOfAny(new[] { '\r', '\n', '<', '>' }) >= 0)
                {
                    category = "unknown";
                }
                else if (name.StartsWith("SYS.", StringComparison.OrdinalIgnoreCase)
                         || name.StartsWith("SUB.", StringComparison.OrdinalIgnoreCase))
                {
                    category = "system";
                }
                else if (!Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_.:-]*$"))
                {
                    category = "unknown";
                }

                result.Add(new VariableReferenceSnapshot
                {
                    raw = match.Value,
                    name = name,
                    category = category,
                    sourceOffset = match.Index
                });
            }
            return result;
        }

        private static List<AttributeSnapshot> ParseAttributes(string text)
        {
            var result = new List<AttributeSnapshot>();
            foreach (Match match in AttributeRegex.Matches(text ?? string.Empty))
            {
                var value = match.Groups["double"].Success
                    ? match.Groups["double"].Value
                    : match.Groups["single"].Value;
                result.Add(new AttributeSnapshot
                {
                    key = match.Groups["key"].Value,
                    value = value
                });
            }
            return result;
        }

        private static string DetermineTargetStatus(
            string commandName,
            List<AttributeSnapshot> attributes,
            string externalScriptRoot,
            ISet<string> knownPartNames,
            int currentCutCount,
            List<SnapshotWarning> warnings)
        {
            if (string.Equals(commandName, "goto-cut", StringComparison.OrdinalIgnoreCase))
            {
                var index = FindAttribute(attributes, "index");
                if (string.IsNullOrEmpty(index))
                {
                    warnings.Add(new SnapshotWarning("missing-target", "goto-cut 缺少 index 属性。", commandName));
                    return "missing";
                }
                if (int.TryParse(index, out var numericIndex)
                    && (numericIndex < 0 || numericIndex >= currentCutCount))
                {
                    warnings.Add(new SnapshotWarning(
                        "missing-target",
                        "goto-cut 指向当前 Part 中不存在的 CUT：" + index,
                        commandName));
                    return "missing";
                }
                return IsDynamicValue(index) ? "dynamic" : "unverified";
            }

            if (string.Equals(commandName, "goto-part", StringComparison.OrdinalIgnoreCase)
                || string.Equals(commandName, "event-part", StringComparison.OrdinalIgnoreCase))
            {
                var name = FindAttribute(attributes, "name");
                if (string.IsNullOrEmpty(name))
                {
                    warnings.Add(new SnapshotWarning(
                        "missing-target",
                        commandName + " 缺少 name 属性。",
                        commandName));
                    return "missing";
                }
                if (IsDynamicValue(name))
                    return "dynamic";
                if (knownPartNames != null && !knownPartNames.Contains(name))
                {
                    warnings.Add(new SnapshotWarning(
                        "missing-target",
                        commandName + " 指向不存在的 Part：" + name,
                        commandName));
                    return "missing";
                }
                return "unverified";
            }

            if (string.Equals(commandName, "call-sub", StringComparison.OrdinalIgnoreCase)
                || string.Equals(commandName, "sub-script", StringComparison.OrdinalIgnoreCase))
            {
                var name = FindAttribute(attributes, "name");
                if (string.IsNullOrEmpty(name))
                {
                    warnings.Add(new SnapshotWarning(
                        "missing-target",
                        commandName + " 缺少 name 属性。",
                        commandName));
                    return "missing";
                }
                return IsDynamicValue(name) ? "dynamic" : "unverified";
            }

            if (string.Equals(commandName, "load-script", StringComparison.OrdinalIgnoreCase))
            {
                var file = FindAttribute(attributes, "file");
                if (string.IsNullOrEmpty(file))
                {
                    warnings.Add(new SnapshotWarning(
                        "external-script-unreadable",
                        "load-script 缺少 file 属性。",
                        commandName));
                    return "missing";
                }
                if (IsDynamicValue(file))
                    return "dynamic";
                if (string.IsNullOrEmpty(externalScriptRoot))
                    return "unverified";

                try
                {
                    var normalizedFile = file
                        .Replace('/', Path.DirectorySeparatorChar)
                        .Replace('\\', Path.DirectorySeparatorChar);
                    var fullPath = Path.GetFullPath(Path.Combine(externalScriptRoot, normalizedFile));
                    if (File.Exists(fullPath))
                        return "exists";

                    warnings.Add(new SnapshotWarning(
                        "external-script-unreadable",
                        "外部脚本文件不存在：" + file,
                        fullPath));
                    return "missing";
                }
                catch (Exception ex)
                {
                    warnings.Add(new SnapshotWarning(
                        "external-script-unreadable",
                        "外部脚本路径无法解析：" + ex.Message,
                        file));
                    return "unverified";
                }
            }

            return FlowCommands.Contains(commandName) ? "unverified" : string.Empty;
        }

        private static string FindAttribute(List<AttributeSnapshot> attributes, string key)
        {
            foreach (var attribute in attributes)
            {
                if (string.Equals(attribute.key, key, StringComparison.OrdinalIgnoreCase))
                    return attribute.value;
            }
            return null;
        }

        private static bool IsDynamicValue(string value)
        {
            return value != null && value.IndexOf('#') >= 0;
        }

        private static bool TryParseXml(string body)
        {
            try
            {
                var document = new XmlDocument
                {
                    PreserveWhitespace = true
                };
                document.LoadXml("<root>" + body + "</root>");
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
