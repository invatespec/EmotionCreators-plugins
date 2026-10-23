using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using EC_SceneAudit.Export;

namespace EC_SceneAudit.Repair
{
    // 清单是唯一输入格式：版本、位置、长度、类型和重复项都在这里拒绝，UI 与预检不另写一套规则。
    internal static class RepairPlanReader
    {
        public const int MaxPlanBytes = 64 * 1024 * 1024;
        public const int MaxChanges = 1000;
        public const int MaxTextUnits = 1000000;

        private static readonly Regex Sha256Pattern = new Regex(
            "^[0-9a-fA-F]{64}$",
            RegexOptions.CultureInvariant);

        public static RepairPlan ReadFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new RepairInputException("修复清单路径为空。");
            var full = Path.GetFullPath(path);
            if (!File.Exists(full))
                throw new RepairInputException("找不到修复清单文件：" + full);
            var bytes = ReadBounded(full, MaxPlanBytes, "修复清单");
            var plan = Parse(DecodeUtf8(bytes, "修复清单"));
            plan.SourcePath = full;
            return plan;
        }

        public static RepairPlan Parse(string json)
        {
            var root = RepairJson.Parse(json);
            if (!root.IsObject)
                throw new RepairInputException("修复清单必须是 JSON 对象。");
            var format = RequiredString(root, "format", "修复清单");
            if (format != RepairPlan.Format)
                throw new RepairInputException("不支持的修复清单格式：" + format);
            if (!TryReadInteger(root, "version", out var version))
                throw new RepairInputException("修复清单缺少整数 version。");
            if (version != RepairPlan.Version)
                throw new RepairInputException("不支持的修复清单版本：" + version);

            var sourceValue = RequiredObject(root, "source", "修复清单");
            var source = new RepairPlanSource
            {
                BundleId = RequiredString(sourceValue, "bundleId", "修复清单 source"),
                ManifestSha256 = RequiredString(sourceValue, "manifestSha256", "修复清单 source")
            };
            if (source.BundleId.Length == 0)
                throw new RepairInputException("修复清单 source.bundleId 不能为空。");
            if (!Sha256Pattern.IsMatch(source.ManifestSha256))
                throw new RepairInputException("修复清单 source.manifestSha256 必须是 64 位十六进制清单哈希。");

            var changesValue = RequiredArray(root, "changes", "修复清单");
            if (changesValue.Count > MaxChanges)
                throw new RepairInputException("修复清单最多 " + MaxChanges + " 项。");
            var plan = new RepairPlan { Source = source };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < changesValue.Count; index++)
                plan.Changes.Add(ReadChange(changesValue.Items[index], index, seen));
            return plan;
        }

        public static string DecodeUtf8(byte[] bytes, string label)
        {
            try
            {
                var text = new UTF8Encoding(false, true).GetString(bytes, 0, bytes.Length);
                return text.Length > 0 && text[0] == '\uFEFF' ? text.Substring(1) : text;
            }
            catch (DecoderFallbackException)
            {
                throw new RepairInputException(label + "不是有效的 UTF-8 文本。");
            }
        }

        public static byte[] ReadBounded(string path, int maxBytes, string label)
        {
            var failure = string.Empty;
            var bytes = DependencyFileReader.ReadStable(path, maxBytes, out failure);
            if (bytes != null)
                return bytes;
            throw new RepairInputException(
                failure == "limit"
                    ? label + "超过 " + maxBytes + " 字节，已拒绝读取。"
                    : label + "在读取期间被修改，请重试。");
        }

        private static RepairChange ReadChange(RepairJsonValue value, int index, HashSet<string> seen)
        {
            var label = "修复清单第 " + (index + 1) + " 项";
            if (!value.IsObject)
                throw new RepairInputException(label + "必须是 JSON 对象。");
            var targetValue = RequiredObject(value, "target", label);
            var referenceValue = RequiredObject(targetValue, "reference", label + " target");
            var fileId = RequiredString(referenceValue, "fileId", label + " reference");
            if (fileId != "scene")
                throw new RepairInputException(label + "只允许引用 scene 文件。");
            var kind = RequiredString(referenceValue, "kind", label + " reference");
            if (kind != "json-pointer")
                throw new RepairInputException(label + "只允许使用 json-pointer 引用。");
            var pointer = RequiredString(referenceValue, "pointer", label + " reference");
            if (!RepairLayoutPointer.TryParse(pointer, out _))
                throw new RepairInputException(label + "的目标必须指向 scene 的 layout.rawText。");

            var nodeUid = RequiredString(targetValue, "nodeUid", label + " target");
            if (nodeUid.Length == 0)
                throw new RepairInputException(label + "的 nodeUid 不能为空。");
            var change = new RepairChange
            {
                Target = new RepairPlanTarget
                {
                    Reference = new RepairPlanReference { FileId = fileId, Kind = kind, Pointer = pointer },
                    NodeUid = nodeUid,
                    CutSourceIndex = RequiredIndex(targetValue, "cutSourceIndex", label + " target"),
                    TextBoxSourceIndex = RequiredIndex(targetValue, "textBoxSourceIndex", label + " target"),
                    LayoutIndex = RequiredIndex(targetValue, "layoutIndex", label + " target")
                },
                Before = RequiredString(value, "before", label),
                After = RequiredString(value, "after", label)
            };
            if (change.After.Length > MaxTextUnits)
                throw new RepairInputException(label + "的新文超过 " + MaxTextUnits + " 字符。");
            if (change.After == change.Before)
                throw new RepairInputException(label + "的修改稿与原文相同：" + pointer);
            if (!seen.Add(pointer))
                throw new RepairInputException(label + "与前面的条目指向同一位置：" + pointer);
            return change;
        }

        private static RepairJsonValue RequiredMember(RepairJsonValue parent, string name, string label)
        {
            var value = parent.Member(name);
            if (value == null)
                throw new RepairInputException(label + " 缺少字段 " + name + "。");
            return value;
        }

        private static string RequiredString(RepairJsonValue parent, string name, string label)
        {
            var value = RequiredMember(parent, name, label);
            if (!value.IsString)
                throw new RepairInputException(label + " 的 " + name + " 必须是文本。");
            return value.Text;
        }

        private static RepairJsonValue RequiredObject(RepairJsonValue parent, string name, string label)
        {
            var value = RequiredMember(parent, name, label);
            if (!value.IsObject)
                throw new RepairInputException(label + " 的 " + name + " 必须是 JSON 对象。");
            return value;
        }

        private static RepairJsonValue RequiredArray(RepairJsonValue parent, string name, string label)
        {
            var value = RequiredMember(parent, name, label);
            if (!value.IsArray)
                throw new RepairInputException(label + " 的 " + name + " 必须是 JSON 数组。");
            return value;
        }

        private static int RequiredIndex(RepairJsonValue parent, string name, string label)
        {
            if (!TryReadInteger(parent, name, out var value))
                throw new RepairInputException(label + " 的 " + name + " 必须是非负整数。");
            return (int)value;
        }

        private static bool TryReadInteger(RepairJsonValue parent, string name, out long value)
        {
            value = 0;
            var member = parent.Member(name);
            if (member == null || !member.IsNumber || !member.TryGetInteger(out var number))
                return false;
            if (number < 0 || number > int.MaxValue)
                return false;
            value = number;
            return true;
        }
    }
}
