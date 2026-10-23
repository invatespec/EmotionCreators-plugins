using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace EC_SceneAudit.Export
{
    internal static class ScriptXml
    {
        public static List<ScriptDependency> ReadReferences(string text, EvidenceReference source)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                MaxCharactersInDocument = 32 * 1024 * 1024
            };
            var result = new List<ScriptDependency>();
            var parents = new List<string>();
            using (var reader = XmlReader.Create(new StringReader(text), settings))
            {
                while (reader.Read())
                    ReadElement(reader, source, parents, result);
            }
            return result;
        }

        private static void ReadElement(XmlReader reader, EvidenceReference source,
            List<string> parents, List<ScriptDependency> result)
        {
            if (reader.NodeType != XmlNodeType.Element)
                return;
            while (parents.Count > reader.Depth)
                parents.RemoveAt(parents.Count - 1);
            if (reader.Name == "load-script")
            {
                var line = ((IXmlLineInfo)reader).LineNumber;
                result.Add(new ScriptDependency
                {
                    from = source.kind == "text-lines"
                        ? new EvidenceReference { fileId = source.fileId, kind = "text-lines", startLine = line, endLine = line }
                        : source,
                    requestedPath = reader.GetAttribute("file") ?? string.Empty,
                    context = parents.ToArray()
                });
            }
            var description = new StringBuilder(reader.Name);
            while (reader.MoveToNextAttribute())
                description.Append(' ').Append(reader.Name).Append("=\"").Append(reader.Value).Append('"');
            reader.MoveToElement();
            if (!reader.IsEmptyElement)
                parents.Add(description.ToString());
        }

        public static string Decode(byte[] bytes, out string encodingName)
        {
            var name = "utf-8";
            var skip = 0;
            if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
                skip = 3;
            else if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe)
            {
                name = "utf-16";
                skip = 2;
            }
            else if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff)
            {
                name = "utf-16BE";
                skip = 2;
            }
            else
            {
                var prefix = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 512));
                var declaration = Regex.Match(prefix, "^<\\?xml\\s[^?]*encoding\\s*=\\s*['\"]([^'\"]+)['\"]");
                if (declaration.Success)
                    name = declaration.Groups[1].Value;
            }
            var encoding = Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            encodingName = encoding.CodePage == 1200 ? "utf-16le" : encoding.WebName;
            return encoding.GetString(bytes, skip, bytes.Length - skip);
        }
    }
}
