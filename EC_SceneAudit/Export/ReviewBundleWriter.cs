using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace EC_SceneAudit.Export
{
    public sealed class ReviewBundleWriter
    {
        public string Write(SceneSnapshot snapshot, string userDataRoot, string exportRoot = null, DateTime? exportTime = null)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));
            var now = exportTime ?? DateTime.Now;
            var date = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var title = JsonSnapshotWriter.SanitizeFileName(snapshot.scene?.title, date);
            var id = Guid.NewGuid().ToString("N");
            var directory = Path.Combine(exportRoot ?? Path.Combine(userDataRoot, "PluginData", "SceneAudit"), title, date,
                now.ToString("HHmmssfff", CultureInfo.InvariantCulture) + "_" + id.Substring(0, 8));
            Directory.CreateDirectory(directory);
            var manifest = new BundleManifest { bundleId = id, scene = snapshot.scene };
            AddJson(manifest, directory, "scene", "scene.json", JsonSnapshotWriter.Serialize(snapshot));
            try
            {
                AddJson(manifest, directory, "summary", "ai-summary.json",
                    JsonSnapshotWriter.SerializeAiSummary(AiSummaryBuilder.Build(snapshot, "scene.json")));
                manifest.collection = new ExternalScriptCollector(userDataRoot).Collect(snapshot);
                foreach (var script in manifest.collection.scripts)
                {
                    WriteBytes(directory, script.file.path, script.bytes);
                    script.file.sha256 = Hash(script.bytes);
                    manifest.files.Add(script.file);
                }
                var bytes = new UTF8Encoding(false).GetBytes(JsonSnapshotWriter.SerializeBundle(manifest));
                WriteBytes(directory, "bundle.json", bytes);
                // 最后写入校验文件；缺少它的目录只能作为未完成导出读取。
                WriteBytes(directory, "bundle.sha256", Encoding.ASCII.GetBytes(Hash(bytes) + Environment.NewLine));
                return directory;
            }
            catch (Exception ex)
            {
                throw new IOException("资料包未完成；已保存的 scene.json 位于 " + directory, ex);
            }
        }

        private static void AddJson(BundleManifest manifest, string directory, string role, string path, string json)
        {
            var bytes = new UTF8Encoding(false).GetBytes(json);
            WriteBytes(directory, path, bytes);
            manifest.files.Add(new BundleFile
            {
                fileId = role, role = role, path = path,
                byteLength = bytes.Length, sha256 = Hash(bytes)
            });
        }

        internal static string Hash(byte[] bytes)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static void WriteBytes(string directory, string relativePath, byte[] bytes)
        {
            var path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            // 新批次内每个成员只写一次，拒绝覆盖已有成员。
            File.Move(temporary, path);
        }
    }
}
