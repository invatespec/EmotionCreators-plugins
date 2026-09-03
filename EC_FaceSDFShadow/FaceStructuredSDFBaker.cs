using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace EC_FaceSDFShadow
{
    /// <summary>
    /// 面部阈值图的取图入口与缓存。
    ///
    /// 来源只有手绘素材：<see cref="FaceManualSDFBaker"/> 的角度帧，其次同目录的 SDF.png。
    /// 拿不到就返回 null，叠加层不生效。
    ///
    /// 另对外提供 UV coverage 光栅化与膨胀，供 manual 路径复用。
    /// </summary>
    internal static class FaceStructuredSDFBaker
    {
        internal const int Size = SDFTextureResolution.DefaultSize;

        // 按 512 texel 标定；随输出尺寸同比放大，保持 UV 空间宽度不变。
        private const int DilationPasses =
            4 * Size / SDFTextureResolution.TemplateCalibrationSize;

        private static readonly Dictionary<CacheIdentity, Texture> Cache =
            new Dictionary<CacheIdentity, Texture>();
        private static readonly HashSet<CacheIdentity> Failed = new HashSet<CacheIdentity>();

        // 同名 mesh 也可能是换头或 mod 替换出的不同实例；coverage/失败状态必须按实际对象隔离。
        // customDir 为空 = 全局 SDF 目录（默认）。
        private struct CacheIdentity : IEquatable<CacheIdentity>
        {
            internal readonly Mesh Mesh;
            internal readonly string CustomDir;

            internal CacheIdentity(Mesh mesh, string customDir)
            {
                Mesh = mesh;
                CustomDir = customDir ?? string.Empty;
            }

            public bool Equals(CacheIdentity other)
            {
                return ReferenceEquals(Mesh, other.Mesh) &&
                    StringComparer.OrdinalIgnoreCase.Equals(CustomDir, other.CustomDir);
            }

            public override bool Equals(object obj)
            {
                return obj is CacheIdentity other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (RuntimeHelpers.GetHashCode(Mesh) * 397) ^
                        StringComparer.OrdinalIgnoreCase.GetHashCode(CustomDir);
                }
            }
        }

        internal static Texture GetOrBake(Mesh mesh, string customDir = null)
        {
            if (mesh == null || string.IsNullOrEmpty(mesh.name)) return null;
            if (!mesh.isReadable)
            {
                FaceSDFShadowPlugin.Log.LogWarning(
                    $"Mesh '{mesh.name}' is not readable; face SDF shadow stays off.");
                return null;
            }

            string key = mesh.name;
            var cacheKey = new CacheIdentity(mesh, customDir);
            if (Cache.TryGetValue(cacheKey, out var cached) && cached != null)
                return cached;
            if (Failed.Contains(cacheKey)) return null;

            if (!IsSupportedMesh(key))
            {
                FaceSDFShadowPlugin.Log.LogWarning(
                    $"No UV template for mesh '{key}'; face SDF shadow stays off.");
                Failed.Add(cacheKey);
                return null;
            }

            Texture tex = null;
            try
            {
                tex = FaceManualSDFBaker.TryLoad(mesh, customDir);
            }
            catch (Exception ex)
            {
                if (tex != null) UnityEngine.Object.Destroy(tex);
                FaceSDFShadowPlugin.Log.LogWarning(
                    $"Manual SDF source chain failed for '{key}': {ex.Message}; " +
                    "face SDF shadow stays off.");
                Failed.Add(cacheKey);
                return null;
            }

            if (tex == null)
            {
                FaceSDFShadowPlugin.Log.LogWarning(
                    $"No manual SDF input found for '{key}'; face SDF shadow stays off. " +
                    "Put angle frames in UserData/PluginData/EC_FaceSDFShadow/SDF/.");
                Failed.Add(cacheKey);
                return null;
            }

            Cache[cacheKey] = tex;
            return tex;
        }

        private static bool IsSupportedMesh(string meshName)
        {
            return meshName == "cf_O_face" || meshName == "cf_O_face_SphN";
        }

        private static bool Rasterize(Mesh mesh, int size, Vector3[] positions,
            bool[] covered, out int coverage)
        {
            Vector3[] verts = mesh.vertices;
            Vector2[] uvs = mesh.uv;
            int[] tris = mesh.triangles;
            Vector3 center = mesh.bounds.center;
            var radial = new float[positions.Length];
            coverage = 0;

            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= verts.Length ||
                    i1 >= verts.Length || i2 >= verts.Length || i0 >= uvs.Length ||
                    i1 >= uvs.Length || i2 >= uvs.Length) continue;

                Vector2 u0 = uvs[i0], u1 = uvs[i1], u2 = uvs[i2];
                float den = Cross(u1 - u0, u2 - u0);
                if (Mathf.Abs(den) < 1e-10f) continue;

                int minX = Mathf.Max(0, Mathf.FloorToInt(
                    Mathf.Min(u0.x, Mathf.Min(u1.x, u2.x)) * size));
                int maxX = Mathf.Min(size - 1, Mathf.CeilToInt(
                    Mathf.Max(u0.x, Mathf.Max(u1.x, u2.x)) * size));
                int minY = Mathf.Max(0, Mathf.FloorToInt(
                    Mathf.Min(u0.y, Mathf.Min(u1.y, u2.y)) * size));
                int maxY = Mathf.Min(size - 1, Mathf.CeilToInt(
                    Mathf.Max(u0.y, Mathf.Max(u1.y, u2.y)) * size));

                for (int py = minY; py <= maxY; py++)
                {
                    for (int px = minX; px <= maxX; px++)
                    {
                        Vector2 p = new Vector2((px + 0.5f) / size, (py + 0.5f) / size);
                        float w0 = Cross(u1 - p, u2 - p) / den;
                        float w1 = Cross(u2 - p, u0 - p) / den;
                        float w2 = Cross(u0 - p, u1 - p) / den;
                        if (w0 < -1e-6f || w1 < -1e-6f || w2 < -1e-6f) continue;

                        Vector3 pos = w0 * verts[i0] + w1 * verts[i1] + w2 * verts[i2];
                        int index = py * size + px;
                        float radius = (pos - center).sqrMagnitude;
                        if (!covered[index])
                        {
                            covered[index] = true;
                            coverage++;
                            positions[index] = pos;
                            radial[index] = radius;
                        }
                        else if (radius > radial[index])
                        {
                            positions[index] = pos;
                            radial[index] = radius;
                        }
                    }
                }
            }
            return coverage > 0;
        }

        internal static bool TryBuildCoverage(Mesh mesh, bool[] covered, out int coverage)
        {
            return TryBuildCoverage(mesh, Size, covered, out coverage);
        }

        /// <summary>
        /// 供 manual SDF 路径使用,指定目标纹理大小,不依赖结构化 SDF 的固定 Size。
        /// </summary>
        internal static bool TryBuildCoverage(Mesh mesh, int size, bool[] covered, out int coverage)
        {
            coverage = 0;
            if (mesh == null || covered == null || covered.Length != size * size ||
                !mesh.isReadable)
                return false;

            Array.Clear(covered, 0, covered.Length);
            var positions = new Vector3[size * size];
            return Rasterize(mesh, size, positions, covered, out coverage);
        }

        internal static void ApplyDilation(Color[] pixels, bool[] covered)
        {
            ApplyDilation(pixels, covered, Size);
        }

        /// <summary>
        /// 供 manual SDF 路径使用,指定目标纹理大小。
        /// </summary>
        internal static void ApplyDilation(Color[] pixels, bool[] covered, int size)
        {
            if (pixels == null || covered == null || pixels.Length != size * size ||
                covered.Length != size * size)
                return;
            Dilate(pixels, covered, size);
        }

        private static void Dilate(Color[] pixels, bool[] covered, int size)
        {
            for (int pass = 0; pass < DilationPasses; pass++)
            {
                var next = (bool[])covered.Clone();
                for (int y = 1; y < size - 1; y++)
                {
                    for (int x = 1; x < size - 1; x++)
                    {
                        int index = y * size + x;
                        if (covered[index]) continue;
                        for (int dy = -1; dy <= 1 && !next[index]; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int neighbor = (y + dy) * size + x + dx;
                                if (!covered[neighbor]) continue;
                                pixels[index] = pixels[neighbor];
                                next[index] = true;
                                break;
                            }
                        }
                    }
                }
                Array.Copy(next, covered, covered.Length);
            }
        }

        private static float Cross(Vector2 a, Vector2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        private static string DiskDir =>
            FaceSDFShadowPlugin.DataDir;

        private static string DiskPath(string meshName, string suffix) =>
            Path.Combine(DiskDir, Sanitize(meshName) + suffix + ".png");

        internal static void ExportCoverageMask(string meshName, bool[] covered,
            string suffix = "_pre_dilation_coverage")
        {
            if (covered == null || covered.Length != Size * Size)
                return;

            var mask = new Color32[covered.Length];
            int count = 0;
            for (int i = 0; i < mask.Length; i++)
            {
                if (covered[i]) count++;
                byte value = covered[i] ? (byte)255 : (byte)0;
                mask[i] = new Color32(value, value, value, 255);
            }
            Directory.CreateDirectory(DiskDir);
            WritePng(DiskPath(meshName, suffix), mask);
            FaceSDFShadowPlugin.Log.LogInfo(
                $"Exported pre-dilation UV coverage for '{meshName}': {count}/{mask.Length}.");
        }

        private static void WritePng(string path, Color32[] pixels)
        {
            var png = new Texture2D(Size, Size, TextureFormat.RGBA32, false, true);
            try
            {
                png.SetPixels32(pixels);
                png.Apply(false, false);
                File.WriteAllBytes(path, png.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.Destroy(png);
            }
        }

        private static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        internal static void InvalidateAll()
        {
            foreach (var tex in Cache.Values)
                if (tex != null) UnityEngine.Object.Destroy(tex);
            Cache.Clear();
            Failed.Clear();
        }

        internal static void Dispose()
        {
            InvalidateAll();
        }
    }
}
