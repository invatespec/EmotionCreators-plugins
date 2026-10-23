using System;
using System.IO;

namespace EC_SceneAudit.Export
{
    internal static class DependencyFileReader
    {
        public static bool HasReparsePoint(string path)
        {
            for (var current = path; current != null; current = Path.GetDirectoryName(current))
            {
                if ((File.Exists(current) || Directory.Exists(current))
                    && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return true;
            }
            return false;
        }

        public static byte[] ReadStable(string path, int maxBytes, out string failure)
        {
            failure = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var before = new FileInfo(path);
                var length = before.Length;
                var modified = before.LastWriteTimeUtc;
                if (length > maxBytes)
                {
                    failure = "limit";
                    return null;
                }
                var bytes = ReadBounded(path, maxBytes);
                if (bytes == null)
                {
                    failure = "limit";
                    return null;
                }
                var after = new FileInfo(path);
                if (length == bytes.Length && after.Length == length && after.LastWriteTimeUtc == modified)
                    return bytes;
            }
            failure = "unstable";
            return null;
        }

        private static byte[] ReadBounded(string path, int maxBytes)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var buffer = new MemoryStream())
            {
                var chunk = new byte[8192];
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (buffer.Length + read > maxBytes)
                        return null;
                    buffer.Write(chunk, 0, read);
                }
                return buffer.ToArray();
            }
        }
    }
}
