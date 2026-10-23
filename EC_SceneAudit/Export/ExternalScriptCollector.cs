using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

namespace EC_SceneAudit.Export
{
    public sealed class ExternalScriptCollector
    {
        private readonly string _root;
        private readonly CollectionLimits _limits;
        private readonly DependencyCollection _result = new DependencyCollection();
        private readonly Dictionary<string, ScriptDependency> _seen =
            new Dictionary<string, ScriptDependency>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<PendingReference> _pending = new Queue<PendingReference>();
        private int _totalBytes;

        public ExternalScriptCollector(string userDataRoot, CollectionLimits limits = null)
        {
            _root = Path.GetFullPath(userDataRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            _limits = limits ?? new CollectionLimits();
        }

        public DependencyCollection Collect(SceneSnapshot snapshot)
        {
            _seen.Clear();
            _pending.Clear();
            _result.scripts.Clear();
            _result.dependencies.Clear();
            _result.limitations.Clear();
            _totalBytes = 0;
            foreach (var script in SnapshotScripts.Enumerate(snapshot))
                QueueReferences(script.raw, script.source, 1);
            while (_pending.Count != 0)
                Capture(_pending.Dequeue());
            return _result;
        }

        private bool QueueReferences(string text, EvidenceReference source, int depth)
        {
            try
            {
                foreach (var reference in ScriptXml.ReadReferences(text, source))
                {
                    _result.dependencies.Add(reference);
                    _pending.Enqueue(new PendingReference(reference, depth));
                }
                return true;
            }
            catch (XmlException)
            {
                _result.limitations.Add(new BundleLimitation
                {
                    code = "invalid-xml", source = source,
                    message = "XML 无法完整解析，未猜测其中的外部依赖。"
                });
                return false;
            }
        }

        private void Capture(PendingReference pending)
        {
            var reference = pending.reference;
            try
            {
                var path = Resolve(reference);
                if (path == null)
                    return;
                if (_seen.TryGetValue(path, out var existing))
                {
                    reference.targetFileId = existing.targetFileId;
                    reference.status = existing.status;
                    reference.reason = existing.reason;
                    return;
                }
                if (pending.depth > _limits.depth || _result.scripts.Count >= _limits.files)
                {
                    Reject(reference, "limit", "达到依赖深度或文件数量限制。");
                    return;
                }
                _seen.Add(path, reference);
                ReadScript(path, reference, pending.depth);
            }
            catch (UnauthorizedAccessException)
            {
                Reject(reference, "unreadable", "没有权限读取该依赖。");
            }
            catch (IOException)
            {
                Reject(reference, "unreadable", "读取依赖时发生文件系统错误。");
            }
        }

        private string Resolve(ScriptDependency reference)
        {
            var raw = reference.requestedPath;
            if (string.IsNullOrWhiteSpace(raw))
                return Reject(reference, "invalid-path", "load-script 缺少文件路径。");
            if (raw.IndexOf('#') >= 0)
                return Reject(reference, "dynamic", "路径依赖变量，未推算运行时文件名。");
            string path;
            try
            {
                var normalized = raw.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                path = Path.GetFullPath(Path.Combine(_root, normalized));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return Reject(reference, "invalid-path", "无法解析文件路径。");
            }
            if (!path.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
                return Reject(reference, "outside-userdata", "目标不在 UserData 内，未自动收录。");
            // 目录联接也会越出 UserData，不能只检查路径字符串。
            if (DependencyFileReader.HasReparsePoint(path))
                return Reject(reference, "link", "依赖路径包含链接或重解析点，未自动收录。");
            if (!File.Exists(path))
                return Reject(reference, "missing", "采集时未找到目标文件。");
            return path;
        }

        private void ReadScript(string path, ScriptDependency reference, int depth)
        {
            var bytes = DependencyFileReader.ReadStable(path,
                Math.Min(_limits.fileBytes, _limits.totalBytes - _totalBytes), out var failure);
            if (bytes == null)
            {
                Reject(reference, failure, failure == "limit" ? "达到依赖正文体积限制。" : "采集期间文件发生变化。");
                return;
            }
            var id = "dep-" + (_result.scripts.Count + 1).ToString("D4", CultureInfo.InvariantCulture);
            var file = new BundleFile
            {
                fileId = id, role = "dependency", path = "dependencies/" + id + ".xml",
                sourcePath = path.Substring(_root.Length).Replace('\\', '/'),
                byteLength = bytes.Length, capturedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                parseStatus = "unreadable"
            };
            _result.scripts.Add(new CapturedScript { file = file, bytes = bytes });
            _totalBytes += bytes.Length;
            reference.targetFileId = id;
            reference.status = "included";
            var source = new EvidenceReference { fileId = id, kind = "text-lines", startLine = 1, endLine = 1 };
            string text;
            try
            {
                text = ScriptXml.Decode(bytes, out file.encoding);
            }
            catch (ArgumentException)
            {
                file.encoding = null;
                _result.limitations.Add(new BundleLimitation
                {
                    code = "encoding-unknown", source = source, message = "正文已保留，但无法严格解码。"
                });
                return;
            }
            file.parseStatus = QueueReferences(text, source, depth + 1) ? "valid" : "invalid";
        }

        private static string Reject(ScriptDependency reference, string status, string reason)
        {
            reference.status = status;
            reference.reason = reason;
            return null;
        }

        private sealed class PendingReference
        {
            public readonly ScriptDependency reference;
            public readonly int depth;
            public PendingReference(ScriptDependency reference, int depth)
            {
                this.reference = reference;
                this.depth = depth;
            }
        }
    }
}
