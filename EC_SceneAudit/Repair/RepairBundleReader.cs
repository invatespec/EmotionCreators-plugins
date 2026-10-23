using System;
using System.Collections.Generic;
using System.IO;
using EC_SceneAudit.Export;

namespace EC_SceneAudit.Repair
{
    internal sealed class RepairBundleSource
    {
        public string Directory;
        public string BundleId;
        public string ManifestSha256;
        public string ScenePath;
        public SceneSnapshot Scene;
    }

    // 预检只依赖 bundle.json、bundle.sha256 和 scene 成员。
    internal static class RepairBundleReader
    {
        public const int MaxManifestBytes = 8 * 1024 * 1024;
        public const int MaxSceneBytes = 256 * 1024 * 1024;
        private const int MaxHashBytes = 4096;

        public static RepairBundleSource Read(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new RepairInputException("资料包目录为空。");
            var full = Path.GetFullPath(directory);
            if (!System.IO.Directory.Exists(full))
                throw new RepairInputException("找不到资料包目录：" + full);

            var manifestPath = Path.Combine(full, "bundle.json");
            var checksumPath = Path.Combine(full, "bundle.sha256");
            if (!File.Exists(manifestPath))
                throw new RepairInputException("资料包缺少 bundle.json，只能是未完成导出：" + full);
            if (!File.Exists(checksumPath))
                throw new RepairInputException("资料包缺少 bundle.sha256，只能是未完成导出：" + full);

            var manifestBytes = RepairPlanReader.ReadBounded(manifestPath, MaxManifestBytes, "资料包 bundle.json");
            var manifestHash = ReviewBundleWriter.Hash(manifestBytes);
            var declaredHash = RepairPlanReader
                .DecodeUtf8(RepairPlanReader.ReadBounded(checksumPath, MaxHashBytes, "资料包 bundle.sha256"), "bundle.sha256")
                .Trim();
            if (!string.Equals(declaredHash, manifestHash, StringComparison.OrdinalIgnoreCase))
                throw new RepairInputException("资料包不完整：bundle.sha256 与 bundle.json 的实际哈希不一致。");

            var manifest = RepairJson.Parse(RepairPlanReader.DecodeUtf8(manifestBytes, "bundle.json"));
            if (!manifest.IsObject)
                throw new RepairInputException("资料包 bundle.json 必须是 JSON 对象。");
            var format = RequiredString(manifest, "format");
            if (format != "ec-scene-review-bundle")
                throw new RepairInputException("资料包格式不是 ec-scene-review-bundle：" + format);
            if (!TryInteger(manifest, "version", out var version) || version != 1)
                throw new RepairInputException("不支持的材料包版本。");
            var bundleId = RequiredString(manifest, "bundleId");
            if (bundleId.Length == 0)
                throw new RepairInputException("资料包 bundleId 为空。");

            var sceneEntry = FindSceneEntry(manifest);
            var relativePath = RequiredString(sceneEntry, "path");
            var scenePath = ResolveMemberPath(full, relativePath);
            if (!File.Exists(scenePath))
                throw new RepairInputException("资料包 scene 成员不存在：" + relativePath);
            var sceneBytes = RepairPlanReader.ReadBounded(scenePath, MaxSceneBytes, "资料包 scene 成员");
            if (!TryInteger(sceneEntry, "byteLength", out var declaredLength) || declaredLength != sceneBytes.Length)
                throw new RepairInputException("资料包 scene 成员大小与清单声明不一致：" + relativePath);
            var declaredSha = RequiredString(sceneEntry, "sha256");
            if (!string.Equals(declaredSha, ReviewBundleWriter.Hash(sceneBytes), StringComparison.OrdinalIgnoreCase))
                throw new RepairInputException("资料包 scene 成员哈希与清单声明不一致：" + relativePath);

            return new RepairBundleSource
            {
                Directory = full,
                BundleId = bundleId,
                ManifestSha256 = manifestHash,
                ScenePath = scenePath,
                Scene = SceneSnapshotJson.Read(RepairPlanReader.DecodeUtf8(sceneBytes, "scene"))
            };
        }

        // scene 成员必须落在资料包目录内，拒绝绝对路径、上级跳转和符号链接逃逸。
        public static string ResolveMemberPath(string root, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new RepairInputException("资料包 scene 成员缺少路径。");
            var normalized = relativePath.Replace('\\', '/');
            if (Path.IsPathRooted(relativePath) || normalized.StartsWith("/", StringComparison.Ordinal))
                throw new RepairInputException("资料包 scene 成员路径必须是相对路径：" + relativePath);
            foreach (var segment in normalized.Split('/'))
            {
                if (segment.Length == 0 || segment == "." || segment == "..")
                    throw new RepairInputException("资料包 scene 成员路径越出目录范围：" + relativePath);
            }

            var combined = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? root
                : root + Path.DirectorySeparatorChar;
            if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new RepairInputException("资料包 scene 成员路径越出目录范围：" + relativePath);
            if (DependencyFileReader.HasReparsePoint(combined))
                throw new RepairInputException("资料包 scene 成员路径经过符号链接，已拒绝：" + relativePath);
            return combined;
        }

        private static RepairJsonValue FindSceneEntry(RepairJsonValue manifest)
        {
            var files = manifest.Member("files");
            if (files == null || !files.IsArray)
                throw new RepairInputException("资料包 bundle.json 缺少 files 数组。");
            RepairJsonValue found = null;
            foreach (var entry in files.Items)
            {
                if (entry == null || !entry.IsObject)
                    continue;
                var fileId = entry.Member("fileId");
                if (fileId != null && fileId.IsString && fileId.Text == "scene")
                {
                    if (found != null || RequiredString(entry, "role") != "scene")
                        throw new RepairInputException("资料包 scene 成员重复或 role 错误。");
                    found = entry;
                }
            }
            if (found != null) return found;
            throw new RepairInputException("资料包 bundle.json 没有 scene 成员。");
        }

        private static string RequiredString(RepairJsonValue parent, string name)
        {
            var value = parent.Member(name);
            if (value == null || !value.IsString)
                throw new RepairInputException("资料包 bundle.json 缺少文本字段 " + name + "。");
            return value.Text;
        }

        private static bool TryInteger(RepairJsonValue parent, string name, out long value)
        {
            value = 0;
            var member = parent.Member(name);
            return member != null && member.IsNumber && member.TryGetInteger(out value);
        }
    }
}
