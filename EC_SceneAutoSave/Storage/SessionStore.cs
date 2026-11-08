using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace EC_SceneAutoSave.Storage
{
    internal sealed class SessionInfo
    {
        public string Dir;
        public SessionManifest Manifest;
        public bool FromBackup;
    }

    // 会话目录的全部文件操作。数据文件只新建不覆盖，清单最后提交，崩溃时旧清单与它引用的文件仍完整。
    internal sealed class SessionStore
    {
        public const string ManifestName = "manifest.xml";
        public const string BackupName = "manifest.xml.bak";
        private const string TempName = "manifest.xml.tmp";

        private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(SessionManifest));

        public string Root { get; }

        public SessionStore(string root)
        {
            Root = root;
        }

        public string SceneDir(string sceneKey) => Path.Combine(Root, sceneKey);

        public string CreateSessionDir(string sceneKey, DateTime now)
        {
            var baseName = now.ToString("yyyyMMdd-HHmmss");
            for (var i = 0; i < 100; i++)
            {
                var name = i == 0 ? baseName : baseName + "-" + i;
                var dir = Path.Combine(SceneDir(sceneKey), name);
                if (Directory.Exists(dir))
                    continue;
                Directory.CreateDirectory(dir);
                return dir;
            }
            throw new IOException("无法创建会话目录：" + SceneDir(sceneKey));
        }

        public void WriteBlob(string dir, string name, byte[] data)
        {
            if (!SessionManifest.IsSafeBlobName(name))
                throw new ArgumentException("数据文件名无效：" + name);
            using (var stream = new FileStream(Path.Combine(dir, name), FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(data, 0, data.Length);
                stream.Flush();
            }
        }

        public byte[] ReadBlob(string dir, string name)
        {
            if (!SessionManifest.IsSafeBlobName(name))
                throw new InvalidDataException("数据文件名无效：" + name);
            return File.ReadAllBytes(Path.Combine(dir, name));
        }

        public void CommitManifest(string dir, SessionManifest manifest)
        {
            manifest.Validate();
            var target = Path.Combine(dir, ManifestName);
            var temp = Path.Combine(dir, TempName);
            try
            {
                var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true };
                using (var writer = XmlWriter.Create(temp, settings))
                    Serializer.Serialize(writer, manifest);
                if (File.Exists(target))
                    File.Replace(temp, target, Path.Combine(dir, BackupName));
                else
                    File.Move(temp, target);
            }
            finally
            {
                TryDelete(temp);
            }
        }

        // 只删本插件写的 .bin，且清单与备份清单都不引用；清单读不出时不清理。
        public int CollectGarbage(string dir)
        {
            var current = TryReadManifest(Path.Combine(dir, ManifestName), out _);
            if (current == null)
                return 0;
            var keep = new HashSet<string>(current.ReferencedBlobs(), StringComparer.OrdinalIgnoreCase);
            var backup = TryReadManifest(Path.Combine(dir, BackupName), out _);
            if (backup != null)
                keep.UnionWith(backup.ReferencedBlobs());

            var removed = 0;
            foreach (var file in Directory.GetFiles(dir, "*.bin"))
            {
                if (keep.Contains(Path.GetFileName(file)))
                    continue;
                if (TryDelete(file))
                    removed++;
            }
            return removed;
        }

        public SessionInfo TryReadSession(string dir)
        {
            var manifest = TryReadManifest(Path.Combine(dir, ManifestName), out _);
            if (manifest != null)
                return new SessionInfo { Dir = dir, Manifest = manifest };
            manifest = TryReadManifest(Path.Combine(dir, BackupName), out _);
            return manifest == null ? null : new SessionInfo { Dir = dir, Manifest = manifest, FromBackup = true };
        }

        public List<SessionInfo> ListSessions(string sceneKey, out int unreadable)
        {
            unreadable = 0;
            var result = new List<SessionInfo>();
            var sceneDir = SceneDir(sceneKey);
            if (!Directory.Exists(sceneDir))
                return result;
            foreach (var dir in Directory.GetDirectories(sceneDir))
            {
                var session = TryReadSession(dir);
                if (session == null)
                    unreadable++;
                else
                    result.Add(session);
            }
            return result.OrderByDescending(s => s.Manifest.UpdatedTicksUtc).ToList();
        }

        public List<SessionInfo> FindRecoverable(string sceneKey, BaseFingerprint fingerprint)
        {
            return ListSessions(sceneKey, out _).Where(s => fingerprint.Matches(s.Manifest)).ToList();
        }

        // 读不出的会话不计数也不删除，保留给用户手动处理。
        public int EnforceRetention(string sceneKey, int keepCount, string protectedDir)
        {
            var removed = 0;
            var kept = 0;
            foreach (var session in ListSessions(sceneKey, out _))
            {
                var isProtected = SameDir(session.Dir, protectedDir);
                if (isProtected || kept < keepCount)
                {
                    kept++;
                    continue;
                }
                if (DeleteSession(session.Dir))
                    removed++;
            }
            return removed;
        }

        // 原生覆盖保存后，基准路径相同但指纹已变化的会话无法再应用。
        public int PurgeMismatched(string sceneKey, BaseFingerprint fingerprint)
        {
            var removed = 0;
            foreach (var session in ListSessions(sceneKey, out _))
            {
                if (!BaseFingerprint.SamePath(session.Manifest.BasePath, fingerprint.Path))
                    continue;
                if (fingerprint.Matches(session.Manifest))
                    continue;
                if (DeleteSession(session.Dir))
                    removed++;
            }
            return removed;
        }

        // 只删本插件认识的文件，目录里有其它文件时保留目录。
        public bool DeleteSession(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return false;
            foreach (var file in Directory.GetFiles(dir, "*.bin"))
                TryDelete(file);
            TryDelete(Path.Combine(dir, ManifestName));
            TryDelete(Path.Combine(dir, BackupName));
            TryDelete(Path.Combine(dir, TempName));
            try
            {
                if (Directory.GetFileSystemEntries(dir).Length > 0)
                    return false;
                Directory.Delete(dir, false);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        public static SessionManifest TryReadManifest(string path, out string error)
        {
            error = null;
            try
            {
                return ReadManifest(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                || ex is InvalidOperationException || ex is InvalidDataException || ex is XmlException)
            {
                error = ex.Message;
                return null;
            }
        }

        private static SessionManifest ReadManifest(string path)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using (var reader = XmlReader.Create(path, settings))
            {
                var events = new XmlDeserializationEvents { OnUnknownNode = RejectUnknownNode };
                var manifest = (SessionManifest)Serializer.Deserialize(reader, events);
                // 根节点之后的残留内容同样说明文件损坏。
                while (reader.Read()) { }
                if (manifest == null)
                    throw new InvalidDataException("清单为空。");
                manifest.Validate();
                return manifest;
            }
        }

        private static void RejectUnknownNode(object sender, XmlNodeEventArgs args)
        {
            throw new InvalidDataException("清单含未知内容：" + args.Name);
        }

        public static bool SameDir(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return false;
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryDelete(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return false;
                File.Delete(path);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }
}
