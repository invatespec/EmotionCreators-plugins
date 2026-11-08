using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace EC_SceneAutoSave.Storage
{
    // 只用于判断两份序列化字节是否相同，不涉及安全用途。
    internal static class ContentHash
    {
        public static string Of(byte[] data, int offset = 0)
        {
            if (data == null)
                return null;
            var start = Math.Max(0, Math.Min(offset, data.Length));
            using (var md5 = MD5.Create())
                return Hex(md5.ComputeHash(data, start, data.Length - start));
        }

        public static string OfText(string text)
        {
            return Of(Encoding.UTF8.GetBytes(text ?? string.Empty));
        }

        private static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    // 会话目录名：可读的基准文件名 + 完整路径哈希，区分不同目录下的同名存档。
    internal static class SceneKey
    {
        private const int MaxNameLength = 40;

        public static string For(string basePath)
        {
            var full = BaseFingerprint.Normalize(Path.GetFullPath(basePath)).ToLowerInvariant();
            var name = Sanitize(Path.GetFileNameWithoutExtension(basePath));
            return name + "_" + ContentHash.OfText(full).Substring(0, 8);
        }

        private static string Sanitize(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (var c in name ?? string.Empty)
            {
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
                if (sb.Length >= MaxNameLength)
                    break;
            }
            // Windows 目录名不能以空格或句点结尾。
            var result = sb.ToString().TrimEnd(' ', '.');
            return result.Length == 0 ? "scene" : result;
        }
    }
}
