using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using UnityEngine;

namespace EC_HEditPosePanel
{
    internal enum PoseLibraryKind
    {
        Hand,
        Skirt
    }

    internal sealed class PoseEntry
    {
        internal string Name;
        internal string DataPath;
        internal string ThumbnailPath;
        internal bool HasThumbnail;
    }

    internal sealed class PoseFolder
    {
        internal string Name;
        internal string Path;
        internal bool IsDefault;
        internal readonly List<PoseEntry> Entries = new List<PoseEntry>();
    }

    internal static class PoseNameValidator
    {
        private static readonly char[] WindowsInvalidChars = "<>:\"/\\|?*".ToCharArray();
        private static readonly HashSet<string> ReservedNames = new HashSet<string>(
            new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" },
            StringComparer.OrdinalIgnoreCase);

        internal static bool TryNormalize(string raw, out string value, out string error)
        {
            value = (raw ?? string.Empty).Trim();
            error = null;
            if (value.Length == 0 || value == "." || value == "..")
            {
                error = "名称不能为空。";
                return false;
            }
            if (value.EndsWith(".", StringComparison.Ordinal) || value.EndsWith(" ", StringComparison.Ordinal))
            {
                error = "名称不能以点或空格结尾。";
                return false;
            }
            string deviceName = value.Split('.')[0];
            if (ReservedNames.Contains(deviceName))
            {
                error = "名称是 Windows 保留设备名。";
                return false;
            }
            if (value.IndexOfAny(WindowsInvalidChars) >= 0 || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                error = "名称包含 Windows 不允许的字符。";
                return false;
            }
            return true;
        }
    }

    internal sealed class PoseLibrary
    {
        internal const int MaxEntriesPerFolder = 50;
        private const string RootFolder = "EC_HEditPosePanel";
        private const string DefaultMarker = ".ec-default-folder";
        private readonly string _root;
        private readonly Action<string> _log;
        private readonly HashSet<string> _loggedInvalidFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal PoseLibrary(Action<string> log = null, string root = null)
        {
            _root = root ?? System.IO.Path.Combine(Paths.GameRootPath, "UserData", "PluginData", RootFolder);
            _log = log;
        }

        internal List<PoseFolder> Scan(PoseLibraryKind kind)
        {
            string root = GetKindRoot(kind);
            Directory.CreateDirectory(root);
            EnsureDefaultFolder(root);
            string extension = GetDataExtension(kind);
            var folders = new List<PoseFolder>();
            foreach (string directory in Directory.GetDirectories(root))
            {
                var folder = new PoseFolder
                {
                    Name = new DirectoryInfo(directory).Name,
                    Path = directory,
                    IsDefault = File.Exists(System.IO.Path.Combine(directory, DefaultMarker))
                };
                foreach (string dataPath in Directory.GetFiles(directory, "*" + extension, SearchOption.TopDirectoryOnly))
                {
                    string name = System.IO.Path.GetFileNameWithoutExtension(dataPath);
                    PartialPoseData ignored;
                    string error;
                    if (!PartialPoseSerializer.TryRead(dataPath, kind, out ignored, out error))
                    {
                        if (_loggedInvalidFiles.Add(dataPath))
                            _log?.Invoke("跳过损坏姿势 " + dataPath + ": " + error);
                        continue;
                    }
                    _loggedInvalidFiles.Remove(dataPath);
                    string thumbnail = System.IO.Path.Combine(directory, name + ".png");
                    folder.Entries.Add(new PoseEntry
                    {
                        Name = name,
                        DataPath = dataPath,
                        ThumbnailPath = thumbnail,
                        HasThumbnail = File.Exists(thumbnail)
                    });
                }
                folder.Entries.Sort((a, b) => NaturalNameComparer.Instance.Compare(a.Name, b.Name));
                folders.Add(folder);
            }
            folders.Sort((a, b) => NaturalNameComparer.Instance.Compare(a.Name, b.Name));
            return folders;
        }

        internal bool TryCreateFolder(PoseLibraryKind kind, string rawName, out string error)
        {
            string name;
            if (!PoseNameValidator.TryNormalize(rawName, out name, out error)) return false;
            string root = GetKindRoot(kind);
            Directory.CreateDirectory(root);
            if (FindDirectory(root, name) != null) { error = "文件夹已存在。"; return false; }
            try
            {
                Directory.CreateDirectory(System.IO.Path.Combine(root, name));
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        internal bool TryRenameFolder(PoseFolder folder, string rawName, out string error)
        {
            if (folder == null) { error = "未选择文件夹。"; return false; }
            string name;
            if (!PoseNameValidator.TryNormalize(rawName, out name, out error)) return false;
            string target = System.IO.Path.Combine(Directory.GetParent(folder.Path).FullName, name);
            string existing = FindDirectory(Directory.GetParent(folder.Path).FullName, name);
            if (existing != null && !string.Equals(existing, folder.Path, StringComparison.OrdinalIgnoreCase))
            {
                error = "文件夹已存在。";
                return false;
            }
            if (string.Equals(folder.Path, target, StringComparison.Ordinal)) return true;
            try
            {
                MoveCaseAware(folder.Path, target);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        internal bool TryDeleteFolder(PoseFolder folder, out string error)
        {
            error = null;
            if (folder == null) { error = "未选择文件夹。"; return false; }
            if (folder.IsDefault) { error = "默认文件夹不可删除。"; return false; }
            try { Directory.Delete(folder.Path, true); return true; }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        internal static bool CanAddPose(PoseFolder folder, string name, out string error)
        {
            string normalized;
            if (!PoseNameValidator.TryNormalize(name, out normalized, out error)) return false;
            if (folder == null) { error = "未选择文件夹。"; return false; }
            bool exists = folder.Entries.Any(e => string.Equals(e.Name, normalized, StringComparison.OrdinalIgnoreCase));
            if (!exists && folder.Entries.Count >= MaxEntriesPerFolder)
            {
                error = "当前文件夹已达到 50 个姿势。";
                return false;
            }
            return true;
        }

        internal bool TryGetPoseTargets(PoseLibraryKind kind, PoseFolder folder, string rawName, bool overwrite,
            out string dataPath, out string thumbnailPath, out string error)
        {
            dataPath = null;
            thumbnailPath = null;
            string name;
            if (!PoseNameValidator.TryNormalize(rawName, out name, out error)) return false;
            if (folder == null) { error = "未选择文件夹。"; return false; }
            PoseFolder liveFolder = Scan(kind).FirstOrDefault(candidate =>
                string.Equals(candidate.Path, folder.Path, StringComparison.OrdinalIgnoreCase));
            if (liveFolder == null) { error = "文件夹已不存在。"; return false; }
            if (!CanAddPose(liveFolder, name, out error)) return false;
            dataPath = System.IO.Path.Combine(liveFolder.Path, name + GetDataExtension(kind));
            thumbnailPath = System.IO.Path.Combine(liveFolder.Path, name + ".png");
            PoseEntry existing = liveFolder.Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null && !overwrite)
            {
                dataPath = existing.DataPath;
                thumbnailPath = existing.ThumbnailPath;
                error = "姿势已存在，需要确认覆盖。";
                return false;
            }
            return true;
        }

        internal bool TryRenamePose(PoseEntry entry, string rawName, bool overwrite, out string error)
        {
            error = null;
            string name;
            if (entry == null) { error = "未选择姿势。"; return false; }
            if (!PoseNameValidator.TryNormalize(rawName, out name, out error)) return false;
            string directory = System.IO.Path.GetDirectoryName(entry.DataPath);
            string dataTarget = System.IO.Path.Combine(directory, name + System.IO.Path.GetExtension(entry.DataPath));
            string pngTarget = System.IO.Path.Combine(directory, name + ".png");
            if (string.Equals(entry.DataPath, dataTarget, StringComparison.Ordinal)) return true;
            bool dataIsCaseOnlyRename = string.Equals(entry.DataPath, dataTarget, StringComparison.OrdinalIgnoreCase);
            bool thumbnailIsCaseOnlyRename = string.Equals(entry.ThumbnailPath, pngTarget, StringComparison.OrdinalIgnoreCase);
            bool targetConflicts = (!dataIsCaseOnlyRename && File.Exists(dataTarget))
                || (!thumbnailIsCaseOnlyRename && File.Exists(pngTarget));
            if (targetConflicts && !overwrite)
            {
                error = "姿势已存在，需要确认覆盖。";
                return false;
            }
            bool dataCommitted = false;
            try
            {
                MoveOrReplace(entry.DataPath, dataTarget, overwrite);
                dataCommitted = true;
                if (File.Exists(entry.ThumbnailPath)) MoveOrReplace(entry.ThumbnailPath, pngTarget, overwrite);
                else if (overwrite && File.Exists(pngTarget)) File.Delete(pngTarget);
                return true;
            }
            catch (Exception ex)
            {
                error = dataCommitted ? "姿势数据已重命名，但缩略图处理失败: " + ex.Message : ex.Message;
                return false;
            }
        }

        internal bool TryDeletePose(PoseEntry entry, out string error)
        {
            error = null;
            if (entry == null) { error = "未选择姿势。"; return false; }
            try
            {
                if (File.Exists(entry.ThumbnailPath)) File.Delete(entry.ThumbnailPath);
                if (File.Exists(entry.DataPath)) File.Delete(entry.DataPath);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        internal static string GetDataExtension(PoseLibraryKind kind)
            => kind == PoseLibraryKind.Hand ? ".echpose" : ".ecspose";

        private string GetKindRoot(PoseLibraryKind kind)
            => System.IO.Path.Combine(_root, kind.ToString());

        private static string FindDirectory(string root, string name)
            => Directory.GetDirectories(root)
                .FirstOrDefault(path => string.Equals(new DirectoryInfo(path).Name, name, StringComparison.OrdinalIgnoreCase));

        private static void EnsureDefaultFolder(string root)
        {
            string markerFolder = Directory.GetDirectories(root)
                .FirstOrDefault(d => File.Exists(System.IO.Path.Combine(d, DefaultMarker)));
            if (markerFolder != null) return;
            string folder = System.IO.Path.Combine(root, "默认");
            int suffix = 2;
            while (Directory.Exists(folder)) folder = System.IO.Path.Combine(root, "默认 " + suffix++);
            Directory.CreateDirectory(folder);
            File.WriteAllText(System.IO.Path.Combine(folder, DefaultMarker), "EC_HEditPosePanel");
        }

        private static void MoveOrReplace(string source, string target, bool overwrite)
        {
            if (string.Equals(source, target, StringComparison.Ordinal)) return;
            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            {
                string temporary = source + ".rename-" + Guid.NewGuid().ToString("N");
                File.Move(source, temporary);
                try { File.Move(temporary, target); }
                catch
                {
                    if (File.Exists(temporary) && !File.Exists(source)) File.Move(temporary, source);
                    throw;
                }
                return;
            }
            if (File.Exists(target))
            {
                if (!overwrite) throw new IOException("目标已存在。");
                File.Replace(source, target, null);
            }
            else File.Move(source, target);
        }

        private static void MoveCaseAware(string source, string target)
        {
            if (!string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Move(source, target);
                return;
            }
            string temporary = source + ".rename-" + Guid.NewGuid().ToString("N");
            Directory.Move(source, temporary);
            try { Directory.Move(temporary, target); }
            catch
            {
                if (Directory.Exists(temporary) && !Directory.Exists(source)) Directory.Move(temporary, source);
                throw;
            }
        }
    }

    internal sealed class PartialPoseBone
    {
        internal string Key;
        internal Quaternion Rotation;
    }

    internal sealed class PartialPoseData
    {
        internal PoseLibraryKind Kind;
        internal readonly List<PartialPoseBone> Bones = new List<PartialPoseBone>();
    }

    internal static class PartialPoseSerializer
    {
        private const string Magic = "ECPose";
        private const int Version = 1;
        private const int MaxBones = 256;
        private const int MaxKeyLength = 256;
        private const long MaxFileBytes = 1024 * 1024;

        internal static bool TryRead(string path, PoseLibraryKind expected, out PartialPoseData data, out string error)
        {
            data = null;
            error = null;
            try
            {
                using (var stream = File.OpenRead(path))
                return TryRead(stream, expected, out data, out error);
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        internal static bool TryRead(Stream stream, PoseLibraryKind expected, out PartialPoseData data, out string error)
        {
            data = null;
            error = null;
            try
            {
                if (stream == null || !stream.CanRead) { error = "姿势数据流不可读"; return false; }
                if (stream.CanSeek && stream.Length > MaxFileBytes) { error = "姿势文件过大"; return false; }
                using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
                {
                    if (reader.ReadString() != Magic) { error = "magic 不匹配"; return false; }
                    if (reader.ReadInt32() != Version) { error = "版本不支持"; return false; }
                    var kind = (PoseLibraryKind)reader.ReadInt32();
                    if (!IsKnownKind(kind)) { error = "姿势类型未知"; return false; }
                    if (kind != expected) { error = "姿势类型不匹配"; return false; }
                    int count = reader.ReadInt32();
                    if (count <= 0 || count > MaxBones) { error = "骨骼数量为空或超限"; return false; }
                    var parsed = new PartialPoseData { Kind = kind };
                    var keys = new HashSet<string>(StringComparer.Ordinal);
                    for (int i = 0; i < count; i++)
                    {
                        string key = reader.ReadString();
                        if (key.Length == 0 || key.Length > MaxKeyLength
                            || Encoding.UTF8.GetByteCount(key) > MaxKeyLength
                            || !keys.Add(key))
                        {
                            error = "骨骼键为空或重复";
                            return false;
                        }
                        float x = reader.ReadSingle();
                        float y = reader.ReadSingle();
                        float z = reader.ReadSingle();
                        float w = reader.ReadSingle();
                        if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z) || !IsFinite(w)) { error = "Quaternion 包含非法浮点数"; return false; }
                        parsed.Bones.Add(new PartialPoseBone { Key = key, Rotation = new Quaternion(x, y, z, w) });
                    }
                    if (stream.CanSeek && stream.Position != stream.Length) { error = "姿势文件包含尾随数据"; return false; }
                    data = parsed;
                    return true;
                }
            }
            catch (Exception ex) { data = null; error = ex.Message; return false; }
        }

        internal static void WriteAtomic(string path, PartialPoseData data)
        {
            if (data == null) throw new ArgumentException("姿势数据无效");
            string temp = path + ".tmp";
            string directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            try
            {
                using (var stream = File.Create(temp))
                Write(stream, data);
                ReplaceFile(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        internal static void Write(Stream stream, PartialPoseData data)
        {
            if (stream == null || !stream.CanWrite || data == null || !IsKnownKind(data.Kind)
                || data.Bones == null || data.Bones.Count == 0 || data.Bones.Count > MaxBones)
                throw new ArgumentException("姿势数据无效");
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write((int)data.Kind);
                writer.Write(data.Bones.Count);
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (PartialPoseBone bone in data.Bones)
                {
                    if (bone == null || string.IsNullOrEmpty(bone.Key) || bone.Key.Length > MaxKeyLength
                        || Encoding.UTF8.GetByteCount(bone.Key) > MaxKeyLength || !keys.Add(bone.Key))
                        throw new InvalidDataException("骨骼键为空、过长或重复");
                    Quaternion q = bone.Rotation;
                    if (!IsFinite(q.x) || !IsFinite(q.y) || !IsFinite(q.z) || !IsFinite(q.w)) throw new InvalidDataException("Quaternion 非法");
                    writer.Write(bone.Key);
                    writer.Write(q.x); writer.Write(q.y); writer.Write(q.z); writer.Write(q.w);
                }
                writer.Flush();
            }
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsKnownKind(PoseLibraryKind kind)
            => kind == PoseLibraryKind.Hand || kind == PoseLibraryKind.Skirt;

        private static void ReplaceFile(string temp, string target)
        {
            if (File.Exists(target)) File.Replace(temp, target, null);
            else File.Move(temp, target);
        }
    }

    internal sealed class NaturalNameComparer : IComparer<string>
    {
        internal static readonly NaturalNameComparer Instance = new NaturalNameComparer();
        public int Compare(string left, string right)
        {
            int i = 0, j = 0;
            while (i < left.Length && j < right.Length)
            {
                bool ld = char.IsDigit(left[i]), rd = char.IsDigit(right[j]);
                if (ld && rd)
                {
                    long ln = ReadNumber(left, ref i), rn = ReadNumber(right, ref j);
                    int number = ln.CompareTo(rn);
                    if (number != 0) return number;
                }
                else
                {
                    int text = char.ToUpperInvariant(left[i++]).CompareTo(char.ToUpperInvariant(right[j++]));
                    if (text != 0) return text;
                }
            }
            return left.Length.CompareTo(right.Length);
        }

        private static long ReadNumber(string value, ref int index)
        {
            long result = 0;
            while (index < value.Length && char.IsDigit(value[index]))
            {
                int digit = value[index++] - '0';
                if (result > (long.MaxValue - digit) / 10) result = long.MaxValue;
                else result = result * 10 + digit;
            }
            return result;
        }
    }
}
