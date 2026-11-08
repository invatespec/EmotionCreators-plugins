using System;
using System.IO;

namespace EC_SceneAutoSave.Storage
{
    // 基准文件身份：路径 + 大小 + 修改时间。原生覆盖保存一定会改变后两者，会话据此判断基准是否已被改写。
    internal sealed class BaseFingerprint
    {
        public string Path { get; }
        public long Length { get; }
        public long WriteTicksUtc { get; }

        public BaseFingerprint(string path, long length, long writeTicksUtc)
        {
            Path = path;
            Length = length;
            WriteTicksUtc = writeTicksUtc;
        }

        public static BaseFingerprint TryRead(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            try
            {
                var full = System.IO.Path.GetFullPath(path);
                var info = new FileInfo(full);
                if (!info.Exists)
                    return null;
                return new BaseFingerprint(full, info.Length, info.LastWriteTimeUtc.Ticks);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
        }

        public static bool SamePath(string a, string b)
        {
            if (a == null || b == null)
                return false;
            return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
        }

        public static string Normalize(string path)
        {
            return path.Replace('\\', '/').TrimEnd('/');
        }

        public bool SameFile(BaseFingerprint other)
        {
            return other != null && SamePath(Path, other.Path) && Length == other.Length && WriteTicksUtc == other.WriteTicksUtc;
        }

        public bool Matches(SessionManifest manifest)
        {
            return manifest != null && SamePath(Path, manifest.BasePath)
                && Length == manifest.BaseLength && WriteTicksUtc == manifest.BaseWriteTicksUtc;
        }
    }
}
