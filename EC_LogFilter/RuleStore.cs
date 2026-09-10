using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using BepInEx.Logging;

namespace EC_LogFilter
{
    [XmlRoot("LogFilterRules")]
    public sealed class RuleDocument
    {
        [XmlAttribute] public int Version { get; set; }
        [XmlElement("Rule")] public List<RuleEntry> Rules { get; set; } = new List<RuleEntry>();
    }

    public sealed class RuleEntry
    {
        [XmlAttribute] public string Source { get; set; }
        [XmlAttribute] public LogLevel Blocked { get; set; }
    }

    internal sealed class RuleStore
    {
        private readonly XmlSerializer _serializer = new XmlSerializer(typeof(RuleDocument));
        private bool _scheduled;
        private double _saveAt;

        internal RuleStore(string path) { FilePath = Path.GetFullPath(path); }

        internal string FilePath { get; }
        internal bool CanSave { get; private set; } = true;
        internal bool HasPendingChanges { get; private set; }
        internal string Status { get; private set; } = "尚未修改规则";
        internal string ErrorDetail { get; private set; } = string.Empty;

        internal RuleEntry[] Load()
        {
            CanSave = true;
            HasPendingChanges = false;
            _scheduled = false;
            ErrorDetail = string.Empty;
            try
            {
                RuleEntry[] rules = ReadDocument();
                Status = "已读取保存的规则";
                return rules;
            }
            catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
            {
                Status = "没有规则文件，全部放行";
                return new RuleEntry[0];
            }
            catch (Exception ex)
            {
                CanSave = false;
                Status = "读取失败，当前全部放行；修改仅本次生效";
                ErrorDetail = Describe(ex);
                return new RuleEntry[0];
            }
        }

        internal void MarkChanged(double now)
        {
            HasPendingChanges = true;
            _scheduled = CanSave;
            _saveAt = now + 0.5;
            Status = CanSave ? "修改已生效，等待保存" : "修改仅本次生效：原规则文件读取失败";
        }

        internal bool NeedsSave(double now)
            => CanSave && HasPendingChanges && _scheduled && now >= _saveAt;

        internal void RequestRetry(double now)
        {
            if (!CanSave || !HasPendingChanges) return;
            _scheduled = true;
            _saveAt = now;
        }

        internal bool TrySave(IList<RuleEntry> rules)
        {
            if (!CanSave) return false;
            _scheduled = false;
            string temp = FilePath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                var document = new RuleDocument { Version = 1, Rules = new List<RuleEntry>(rules) };
                document.Rules = new List<RuleEntry>(Validate(document));
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                // 属性中的制表和换行必须实体化，否则 XML 读取会改变来源身份。
                var settings = new XmlWriterSettings
                {
                    Encoding = new UTF8Encoding(false, true), Indent = true, NewLineHandling = NewLineHandling.Entitize
                };
                using (XmlWriter writer = XmlWriter.Create(temp, settings))
                    _serializer.Serialize(writer, document);
                ReplaceFile(temp);
                HasPendingChanges = false;
                Status = "规则已保存";
                ErrorDetail = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                HasPendingChanges = true;
                Status = "保存失败：修改已生效，但尚未保存";
                ErrorDetail = Describe(ex);
                return false;
            }
            finally
            {
                TryDeleteTemp(temp);
            }
        }

        private RuleEntry[] ReadDocument()
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using (XmlReader reader = XmlReader.Create(FilePath, settings))
            {
                var document = (RuleDocument)_serializer.Deserialize(reader,
                    new XmlDeserializationEvents { OnUnknownNode = RejectUnknownNode });
                // 反序列化只读根节点，必须继续验证尾部，避免覆盖被误判为有效的损坏文件。
                while (reader.Read()) { }
                return Validate(document);
            }
        }

        private static void RejectUnknownNode(object sender, XmlNodeEventArgs args)
            => throw new InvalidDataException("规则包含未识别的节点或属性：" + args.Name);

        private static RuleEntry[] Validate(RuleDocument document)
        {
            if (document == null || document.Version != 1 || document.Rules == null)
                throw new InvalidDataException("不支持的规则文件版本或结构。");
            var names = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<RuleEntry>();
            foreach (RuleEntry rule in document.Rules)
            {
                if (rule == null || rule.Source == null || !names.Add(rule.Source))
                    throw new InvalidDataException("规则包含缺失或重复的来源名称。");
                if (((int)rule.Blocked & ~LogStatistics.KnownMask) != 0)
                    throw new InvalidDataException("规则包含不支持的日志等级。");
                if (rule.Blocked != LogLevel.None) result.Add(rule);
            }
            return result.ToArray();
        }

        private void ReplaceFile(string temp)
        {
            // 替换失败时保留原文件，禁止退化为先删除旧规则再写入。
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".bak");
            else File.Move(temp, FilePath);
        }

        private static void TryDeleteTemp(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static string Describe(Exception exception)
        {
            while (exception.InnerException != null) exception = exception.InnerException;
            return exception.GetType().Name + ": " + exception.Message;
        }
    }
}
