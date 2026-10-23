using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace EC_SceneAudit.Repair
{
    internal enum RepairJsonKind
    {
        Object,
        Array,
        String,
        Number,
        Bool,
        Null
    }

    // 严格 JSON DOM：字段缺失与 null 分开保留，数字按原文保存，避免用默认 0 冒充必填索引。
    internal sealed class RepairJsonValue
    {
        private RepairJsonValue(RepairJsonKind kind)
        {
            Kind = kind;
        }

        public RepairJsonKind Kind { get; private set; }

        public Dictionary<string, RepairJsonValue> Members { get; private set; }

        public List<RepairJsonValue> Items { get; private set; }

        public string Text { get; private set; }

        public bool Boolean { get; private set; }

        public bool IsObject => Kind == RepairJsonKind.Object;

        public bool IsArray => Kind == RepairJsonKind.Array;

        public bool IsString => Kind == RepairJsonKind.String;

        public bool IsNumber => Kind == RepairJsonKind.Number;

        public bool IsNull => Kind == RepairJsonKind.Null;

        public int Count => Items != null ? Items.Count : 0;

        public static RepairJsonValue NewObject()
        {
            return new RepairJsonValue(RepairJsonKind.Object)
            {
                Members = new Dictionary<string, RepairJsonValue>(StringComparer.Ordinal)
            };
        }

        public static RepairJsonValue NewArray()
        {
            return new RepairJsonValue(RepairJsonKind.Array)
            {
                Items = new List<RepairJsonValue>()
            };
        }

        public static RepairJsonValue FromString(string value)
        {
            return new RepairJsonValue(RepairJsonKind.String) { Text = value };
        }

        public static RepairJsonValue FromNumber(string literal)
        {
            return new RepairJsonValue(RepairJsonKind.Number) { Text = literal };
        }

        public static RepairJsonValue FromBool(bool value)
        {
            return new RepairJsonValue(RepairJsonKind.Bool) { Boolean = value };
        }

        public static RepairJsonValue Null()
        {
            return new RepairJsonValue(RepairJsonKind.Null);
        }

        // 字段缺失返回 null；与显式 json null 区分。
        public RepairJsonValue Member(string name)
        {
            if (Members == null)
                return null;
            return Members.TryGetValue(name, out var value) ? value : null;
        }

        // 与 JavaScript Number.isInteger 对齐：接受 1.0、1e2 这类整数值数字。
        public bool TryGetInteger(out long value)
        {
            value = 0;
            if (Kind != RepairJsonKind.Number || Text == null)
                return false;
            if (Text.IndexOf('.') < 0 && Text.IndexOf('e') < 0 && Text.IndexOf('E') < 0)
                return long.TryParse(Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
            if (!double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                return false;
            if (double.IsNaN(number) || double.IsInfinity(number) || number != Math.Floor(number))
                return false;
            if (number < long.MinValue || number > long.MaxValue)
                return false;
            value = (long)number;
            return true;
        }
    }

    internal static class RepairJson
    {
        private const int MaxDepth = 200;

        public static RepairJsonValue Parse(string text)
        {
            if (text == null)
                throw new RepairInputException("JSON 内容为空。");
            var parser = new Parser(text);
            var value = parser.ReadValue(0);
            parser.SkipWhitespace();
            if (!parser.AtEnd)
                parser.Fail("JSON 结尾存在多余内容");
            return value;
        }

        private sealed class Parser
        {
            private readonly string text;
            private int index;

            public Parser(string source)
            {
                text = source;
            }

            public bool AtEnd => index >= text.Length;

            public void Fail(string message)
            {
                throw new RepairInputException(message + "（第 " + (index + 1) + " 个字符附近）。");
            }

            public void SkipWhitespace()
            {
                while (index < text.Length)
                {
                    var character = text[index];
                    if (character == ' ' || character == '\t' || character == '\n' || character == '\r')
                        index++;
                    else
                        break;
                }
            }

            public RepairJsonValue ReadValue(int depth)
            {
                if (depth > MaxDepth)
                    Fail("JSON 嵌套过深");
                SkipWhitespace();
                if (AtEnd)
                    Fail("JSON 意外结束");
                switch (text[index])
                {
                    case '{':
                        return ReadObject(depth);
                    case '[':
                        return ReadArray(depth);
                    case '"':
                        return RepairJsonValue.FromString(ReadString());
                    case 't':
                        ReadLiteral("true");
                        return RepairJsonValue.FromBool(true);
                    case 'f':
                        ReadLiteral("false");
                        return RepairJsonValue.FromBool(false);
                    case 'n':
                        ReadLiteral("null");
                        return RepairJsonValue.Null();
                    default:
                        return ReadNumber();
                }
            }

            private RepairJsonValue ReadObject(int depth)
            {
                var value = RepairJsonValue.NewObject();
                index++;
                SkipWhitespace();
                if (!AtEnd && text[index] == '}')
                {
                    index++;
                    return value;
                }

                while (true)
                {
                    SkipWhitespace();
                    if (AtEnd || text[index] != '"')
                        Fail("JSON 对象键必须是字符串");
                    var name = ReadString();
                    SkipWhitespace();
                    if (AtEnd || text[index] != ':')
                        Fail("JSON 对象键后缺少冒号");
                    index++;
                    // 与 JSON.parse 一致：重复键保留最后一次出现的值。
                    value.Members[name] = ReadValue(depth + 1);
                    SkipWhitespace();
                    if (AtEnd)
                        Fail("JSON 对象未闭合");
                    if (text[index] == ',')
                    {
                        index++;
                        continue;
                    }

                    if (text[index] == '}')
                    {
                        index++;
                        return value;
                    }

                    Fail("JSON 对象成员之间缺少逗号");
                }
            }

            private RepairJsonValue ReadArray(int depth)
            {
                var value = RepairJsonValue.NewArray();
                index++;
                SkipWhitespace();
                if (!AtEnd && text[index] == ']')
                {
                    index++;
                    return value;
                }

                while (true)
                {
                    value.Items.Add(ReadValue(depth + 1));
                    SkipWhitespace();
                    if (AtEnd)
                        Fail("JSON 数组未闭合");
                    if (text[index] == ',')
                    {
                        index++;
                        continue;
                    }

                    if (text[index] == ']')
                    {
                        index++;
                        return value;
                    }

                    Fail("JSON 数组元素之间缺少逗号");
                }
            }

            private string ReadString()
            {
                index++;
                var builder = new StringBuilder();
                while (true)
                {
                    if (AtEnd)
                        Fail("JSON 字符串未闭合");
                    var character = text[index++];
                    if (character == '"')
                        return builder.ToString();
                    if (character != '\\')
                    {
                        if (character < 0x20)
                            Fail("JSON 字符串包含未转义的控制字符");
                        builder.Append(character);
                        continue;
                    }

                    if (AtEnd)
                        Fail("JSON 转义字符未完成");
                    var escape = text[index++];
                    switch (escape)
                    {
                        case '"':
                            builder.Append('"');
                            break;
                        case '\\':
                            builder.Append('\\');
                            break;
                        case '/':
                            builder.Append('/');
                            break;
                        case 'b':
                            builder.Append('\b');
                            break;
                        case 'f':
                            builder.Append('\f');
                            break;
                        case 'n':
                            builder.Append('\n');
                            break;
                        case 'r':
                            builder.Append('\r');
                            break;
                        case 't':
                            builder.Append('\t');
                            break;
                        case 'u':
                            builder.Append(ReadUnicodeEscape());
                            break;
                        default:
                            Fail("JSON 字符串包含不支持的转义字符");
                            break;
                    }
                }
            }

            // 逐字符还原 \uXXXX，包括孤立代理项，保持原文与网页解析结果一致。
            private char ReadUnicodeEscape()
            {
                if (index + 4 > text.Length)
                    Fail("JSON \\u 转义不完整");
                var code = 0;
                for (var offset = 0; offset < 4; offset++)
                {
                    var digit = HexValue(text[index + offset]);
                    if (digit < 0)
                        Fail("JSON \\u 转义包含非十六进制字符");
                    code = code * 16 + digit;
                }

                index += 4;
                return (char)code;
            }

            private void ReadLiteral(string literal)
            {
                if (index + literal.Length > text.Length
                    || string.CompareOrdinal(text, index, literal, 0, literal.Length) != 0)
                    Fail("JSON 字面量无效");
                index += literal.Length;
            }

            private RepairJsonValue ReadNumber()
            {
                var start = index;
                if (!AtEnd && text[index] == '-')
                    index++;
                if (AtEnd)
                    Fail("JSON 数字格式无效");
                if (text[index] == '0')
                {
                    index++;
                }
                else if (text[index] >= '1' && text[index] <= '9')
                {
                    while (!AtEnd && text[index] >= '0' && text[index] <= '9')
                        index++;
                }
                else
                {
                    Fail("JSON 数字格式无效");
                }

                if (!AtEnd && text[index] == '.')
                {
                    index++;
                    if (AtEnd || text[index] < '0' || text[index] > '9')
                        Fail("JSON 数字小数部分无效");
                    while (!AtEnd && text[index] >= '0' && text[index] <= '9')
                        index++;
                }

                if (!AtEnd && (text[index] == 'e' || text[index] == 'E'))
                {
                    index++;
                    if (!AtEnd && (text[index] == '+' || text[index] == '-'))
                        index++;
                    if (AtEnd || text[index] < '0' || text[index] > '9')
                        Fail("JSON 数字指数部分无效");
                    while (!AtEnd && text[index] >= '0' && text[index] <= '9')
                        index++;
                }

                return RepairJsonValue.FromNumber(text.Substring(start, index - start));
            }

            private static int HexValue(char character)
            {
                if (character >= '0' && character <= '9')
                    return character - '0';
                if (character >= 'a' && character <= 'f')
                    return character - 'a' + 10;
                if (character >= 'A' && character <= 'F')
                    return character - 'A' + 10;
                return -1;
            }
        }
    }
}
