using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace EC_FaceSDFShadow
{
    /// <summary>
    /// 从用户绘制的手绘帧生成 mode 2 半浮点「受光窗口」阈值图。
    ///
    /// 帧语义(2026-08 起):白色 = 受光,黑色 = 阴影。帧文件名纯数字,帧序
    /// 0=背面(sideAngle 1.0) → max=正面(sideAngle 0.0)。运行时 sideAngle ∈ [0,1],0=正面、1=背面。
    /// 每个像素被提取成一个受光窗口 [lo, hi]:sideAngle 落在窗口内 = 受光(无阴影),
    /// 窗口外 = 阴影。这样能表达「下颚带侧光亮、正面/背面都暗」的暗→亮→暗时序,
    /// 而不是旧实现只会表达单次翻转(导致下颚带被丢弃成恒暗)。
    ///
    /// 通道:R=右光 hi、G=左光 hi、B=右光 lo、A=左光 lo。左右严格镜像对称,因此只画
    /// left 一套,右光由 UV 水平镜像得到。
    /// </summary>
    internal static class FaceManualSDFBaker
    {
        private const float LitCutoff = 0.5f;

        /// <param name="customDir">逐角色自定义 SDF 来源目录。空 → 全局 config 目录（原行为）；
        /// 非空 → 作为手工帧目录（与 SDF/ 同契约）。优先级高于全局目录。</param>
        internal static Texture2D TryLoad(Mesh mesh, string customDir = null)
        {
            if (mesh == null || !mesh.isReadable)
                return null;

            // 取图目录一律先报一次：素材没被找到时，光看"阴影没出来"无法区分是目录不对、
            // 帧数不足还是图本身被拒。
            string sourceDir = string.IsNullOrEmpty(customDir)
                ? Path.Combine(FaceSDFShadowPlugin.DataDir, "SDF")
                : customDir;
            FaceSDFShadowPlugin.Log.LogInfo(
                $"Manual SDF source dir for '{mesh.name}': {sourceDir}");

            return TryLoadFrames(mesh, customDir);
        }

        private static Texture2D TryLoadFrames(Mesh mesh, string customDir)
        {
            FrameSet leftSet = ResolveFrameSet(customDir);
            if (leftSet == null)
                return null;

            if (!TryReadFrames(leftSet.Paths, out float[][] frames))
                return null;

            // 按 sideAngle 升序重排:frames[0]=正面(sideAngle 0) → 末尾=背面(sideAngle 1)。
            float[] frameAngles = SortByAngle(frames, leftSet.Angles);

            try
            {
                var covered = new bool[ManualContourInterpolator.Size * ManualContourInterpolator.Size];
                if (!FaceStructuredSDFBaker.TryBuildCoverage(mesh, ManualContourInterpolator.Size, covered, out int _))
                {
                    FaceSDFShadowPlugin.Log.LogWarning(
                        $"Manual SDF frame coverage could not be built for '{mesh.name}'.");
                    return null;
                }

                if (!TryBuildThresholdField(mesh, frames, frameAngles, covered, out float[] lo, out float[] hi))
                    return null;

                // 左右严格镜像对称:只画 left 一套,右光由 UV 水平镜像得到。
                float[] rightLo = MirrorField(lo);
                float[] rightHi = MirrorField(hi);

                return BuildTexture(mesh, lo, hi, rightLo, rightHi, covered,
                    "manual left frames (mirrored right)");
            }
            catch (Exception ex)
            {
                FaceSDFShadowPlugin.Log.LogWarning(
                    $"Failed to convert manual SDF frames for '{mesh.name}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 从一套帧提取受光窗口 [lo, hi](sideAngle 域 0..1)。frameAngles 已按 sideAngle 升序。
        /// 26 开启时用 contour 双场插值(相邻帧距离场零交叉),否则用 raw 帧中点插值。
        /// </summary>
        private static bool TryBuildThresholdField(Mesh mesh, float[][] frames, float[] frameAngles,
            bool[] covered, out float[] lo, out float[] hi)
        {
            lo = null;
            hi = null;

            // 硬边帧下 raw 中点插值会锁死在帧中点,导致阴影一跳一跳;
            // contour 用相邻帧距离场零交叉得到连续 sub-frame 角度。
            if (FaceSDFShadowPlugin.ManualContourInterpolation.Value)
            {
                try
                {
                    if (ManualContourInterpolator.TryBuildWindow(
                        frames, covered, frameAngles, out lo, out hi,
                        out ManualContourInterpolator.Diagnostics diagnostics))
                    {
                        FaceSDFShadowPlugin.Log.LogInfo(
                            $"Manual SDF contour window for '{mesh.name}': {diagnostics}.");
                    }
                    else
                    {
                        lo = null;
                        hi = null;
                        FaceSDFShadowPlugin.Log.LogWarning(
                            $"Manual SDF contour window failed for '{mesh.name}'; " +
                            "using raw frame midpoint.");
                    }
                }
                catch (Exception ex)
                {
                    lo = null;
                    hi = null;
                    FaceSDFShadowPlugin.Log.LogWarning(
                        $"Manual SDF contour window threw for '{mesh.name}': {ex.Message}; " +
                        "using raw frame midpoint.");
                }
            }

            if (lo == null || hi == null)
            {
                BuildWindow(frames, frameAngles, covered, out lo, out hi);
            }

            float blurSigma = FaceSDFShadowPlugin.ManualSDFBlurSigma.Value;
            if (blurSigma > 0f)
            {
                lo = BlurThresholdField(lo, covered, ManualContourInterpolator.Size, blurSigma);
                hi = BlurThresholdField(hi, covered, ManualContourInterpolator.Size, blurSigma);
                FaceSDFShadowPlugin.Log.LogInfo(
                    $"Manual SDF spatial blur for '{mesh.name}': sigma={blurSigma:0.#}.");
            }
            else
            {
                FaceSDFShadowPlugin.Log.LogInfo(
                    $"Manual SDF for '{mesh.name}': no spatial blur (ManualSDFBlurSigma=0).");
            }

            ExtendEndpoints(lo, hi, covered);
            ValidatePlayback(mesh.name, frames, frameAngles, lo, hi, covered);
            return true;
        }

        /// <summary>
        /// 端点外扩:把 lo==0 扩到负哨兵、hi==1 扩到 >1 哨兵。
        /// 否则 shader 的 smoothstep(lo-edge, lo+edge, sideAngle) 在 sideAngle=0 时
        /// 恰好落在 lo=0 像素的中点(0.5),整脸被压暗一半;背面同理。
        /// blur 之后做,只动端点值,不碰边界像素的真实角度。
        /// </summary>
        private static void ExtendEndpoints(float[] lo, float[] hi, bool[] covered)
        {
            const float Epsilon = 0.01f;
            for (int p = 0; p < lo.Length; p++)
            {
                if (!covered[p]) continue;
                if (lo[p] <= 1e-6f) lo[p] = -Epsilon;   // 从正面就受光
                if (hi[p] >= 1f - 1e-6f) hi[p] = 1f + Epsilon; // 受光到背面
            }
        }

        /// <summary>逐像素把帧序明暗变化收敛成受光窗口。</summary>
        private static void BuildWindow(float[][] frames, float[] angles, bool[] covered,
            out float[] lo, out float[] hi)
        {
            lo = new float[covered.Length];
            hi = new float[covered.Length];
            for (int p = 0; p < covered.Length; p++)
            {
                if (!covered[p])
                {
                    lo[p] = 1f;
                    hi[p] = 0f; // 全程阴影
                    continue;
                }
                FindWindow(frames, angles, p, out lo[p], out hi[p]);
            }
        }

        /// <summary>
        /// 从该像素在帧序中的明暗变化,提取受光窗口 [lo, hi]。
        /// frames 已按 sideAngle 升序(0=正面 → 1=背面)。取最长连续受光段;lo>hi 表示全程阴影。
        /// </summary>
        private static void FindWindow(float[][] frames, float[] angles, int pixel, out float lo, out float hi)
        {
            int n = frames.Length;

            // 找最长受光段 [bestA, bestB](含端点)
            int bestLen = 0, bestA = -1, bestB = -1;
            int runStart = -1;
            for (int k = 0; k <= n; k++)
            {
                bool lit = k < n && frames[k][pixel] > LitCutoff;
                if (lit)
                {
                    if (runStart < 0) runStart = k;
                }
                else if (runStart >= 0)
                {
                    int len = k - runStart;
                    if (len > bestLen) { bestLen = len; bestA = runStart; bestB = k - 1; }
                    runStart = -1;
                }
            }

            if (bestA < 0)
            {
                lo = 1f;
                hi = 0f;
                return;
            }

            // 起角 lo:受光段从帧 0 开始 → 0;否则在 bestA-1(暗)→bestA(亮)之间按 luma 插值。
            if (bestA == 0)
            {
                lo = 0f;
            }
            else
            {
                float dark = frames[bestA - 1][pixel];
                float lit = frames[bestA][pixel];
                float denom = lit - dark;
                float t = denom > 1e-6f ? (LitCutoff - dark) / denom : 0.5f;
                lo = Mathf.Lerp(angles[bestA - 1], angles[bestA], Mathf.Clamp01(t));
            }

            // 止角 hi:受光段到最后一帧 → 1;否则在 bestB(亮)→bestB+1(暗)之间按 luma 插值。
            if (bestB == n - 1)
            {
                hi = 1f;
            }
            else
            {
                float lit = frames[bestB][pixel];
                float dark = frames[bestB + 1][pixel];
                float denom = lit - dark;
                float t = denom > 1e-6f ? (lit - LitCutoff) / denom : 0.5f;
                hi = Mathf.Lerp(angles[bestB], angles[bestB + 1], Mathf.Clamp01(t));
            }

            lo = Mathf.Clamp01(lo);
            hi = Mathf.Clamp01(hi);
        }

        private static float[] SortByAngle(float[][] frames, float[] fileAngles)
        {
            int count = frames.Length;
            var indices = new int[count];
            for (int i = 0; i < count; i++) indices[i] = i;
            Array.Sort(indices, (a, b) => fileAngles[a].CompareTo(fileAngles[b]));

            var sortedFrames = new float[count][];
            var sortedAngles = new float[count];
            for (int i = 0; i < count; i++)
            {
                sortedFrames[i] = frames[indices[i]];
                sortedAngles[i] = fileAngles[indices[i]];
            }
            for (int i = 0; i < count; i++) frames[i] = sortedFrames[i];
            return sortedAngles;
        }

        /// <summary>UV 水平镜像一张阈值场(左右对称:右光 = 左光镜像)。</summary>
        private static float[] MirrorField(float[] source)
        {
            int size = ManualContourInterpolator.Size;
            var mirror = new float[source.Length];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    mirror[y * size + x] = source[y * size + (size - 1 - x)];
            return mirror;
        }

        private static bool TryReadFrames(string[] paths, out float[][] frames)
        {
            int count = paths.Length;
            frames = new float[count][];
            try
            {
                for (int i = 0; i < count; i++)
                {
                    Texture2D source = null;
                    Texture2D scaled = null;
                    try
                    {
                        source = new Texture2D(2, 2, TextureFormat.RGBA32, false, true)
                        {
                            hideFlags = HideFlags.HideAndDontSave,
                        };
                        if (!source.LoadImage(File.ReadAllBytes(paths[i]), false))
                        {
                            FaceSDFShadowPlugin.Log.LogWarning(
                                $"Manual SDF frame failed to load: {paths[i]}");
                            return false;
                        }
                        // 运行时只接受 1024 及以上方形输入；更高分辨率按 bilinear 降采样。
                        if (source.width == ManualContourInterpolator.Size &&
                            source.height == ManualContourInterpolator.Size)
                        {
                            frames[i] = ReadLuma(source);
                        }
                        else if (source.width == source.height &&
                                 source.width >= SDFTextureResolution.DefaultSize)
                        {
                            scaled = TextureResampler.ResizeSquare(
                                source, ManualContourInterpolator.Size, "ManualSDF_scaled");
                            frames[i] = ReadLuma(scaled);
                        }
                        else
                        {
                            FaceSDFShadowPlugin.Log.LogWarning(
                                $"Manual SDF frame must be square and at least " +
                                $"{SDFTextureResolution.DefaultSize}x{SDFTextureResolution.DefaultSize}: " +
                                $"{paths[i]} ({source.width}x{source.height})");
                            return false;
                        }
                    }
                    finally
                    {
                        if (scaled != null) UnityEngine.Object.Destroy(scaled);
                        if (source != null) UnityEngine.Object.Destroy(source);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                FaceSDFShadowPlugin.Log.LogWarning(
                    $"Failed to read manual SDF frame set: {ex.Message}");
                return false;
            }
        }

        /// 用左右两套受光窗口打包 RGBAHalf。R=右光 hi、G=左光 hi、B=右光 lo、A=左光 lo。
        private static Texture2D BuildTexture(Mesh mesh, float[] leftLo, float[] leftHi,
            float[] rightLo, float[] rightHi, bool[] covered, string sourceLabel)
        {
            if (!SystemInfo.SupportsTextureFormat(TextureFormat.RGBAHalf))
            {
                FaceSDFShadowPlugin.Log.LogWarning(
                    "RGBAHalf Texture2D is unsupported; manual SDF stays off.");
                return null;
            }

            int size = ManualContourInterpolator.Size;
            var pixels = new Color[leftLo.Length];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int index = y * size + x;
                    if (!covered[index])
                    {
                        // coverage 外 = 全程阴影(lo>hi → shader active=0)
                        pixels[index] = new Color(0f, 0f, 1f, 1f);
                        continue;
                    }
                    pixels[index] = new Color(rightHi[index], leftHi[index], rightLo[index], leftLo[index]);
                }
            }

#if DEBUG
            bool[] diagnosticCoverage = FaceSDFShadowPlugin.ExportPngOnce
                ? (bool[])covered.Clone()
                : null;
#endif
            FaceStructuredSDFBaker.ApplyDilation(pixels, covered, ManualContourInterpolator.Size);
            Texture2D result = null;
            try
            {
                result = new Texture2D(size, size, TextureFormat.RGBAHalf, false, true)
                {
                    name = $"ManualSDF_{mesh.name}",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                result.SetPixels(pixels);
                result.Apply(false, false);
            }
            catch (Exception ex)
            {
                if (result != null) UnityEngine.Object.Destroy(result);
                FaceSDFShadowPlugin.Log.LogWarning(
                    $"Failed to create {sourceLabel} texture for '{mesh.name}': {ex.Message}");
                return null;
            }

#if DEBUG
            if (diagnosticCoverage != null)
            {
                try
                {
                    FaceStructuredSDFBaker.ExportCoverageMask(
                        mesh.name, diagnosticCoverage, "_manual_pre_dilation_coverage");
                    ExportDiagnostics(mesh.name, leftLo, leftHi, rightLo, rightHi, diagnosticCoverage);
                }
                catch (Exception ex)
                {
                    // 诊断磁盘不可写不能让已经生成成功的运行时贴图失效。
                    FaceSDFShadowPlugin.Log.LogWarning(
                        $"Failed to export manual SDF diagnostics for '{mesh.name}': {ex.Message}");
                }
            }
#endif

            FaceSDFShadowPlugin.Log.LogInfo(
                $"Loaded {sourceLabel} for '{mesh.name}' as RGBAHalf " +
                $"({size}x{size}); R/B=right light hi/lo, G/A=left light hi/lo.");
            return result;
        }

        /// <summary>
        /// 从目录解析左光帧集。识别纯数字文件名(0.png、8.png);
        /// left*.png、right*.png、SDF.png 等一律忽略(左右对称,只需一套左光帧)。
        /// </summary>
        private static FrameSet ResolveFrameSet(string customDir)
        {
            string dir;
            if (!string.IsNullOrEmpty(customDir))
                dir = customDir;
            else
                dir = Path.Combine(FaceSDFShadowPlugin.DataDir, "SDF");
            if (!Directory.Exists(dir))
            {
                FaceSDFShadowPlugin.Log.LogWarning(
                    $"Manual SDF frame directory does not exist: {dir}");
                return null;
            }

            var files = new List<KeyValuePair<int, string>>();
            foreach (string file in Directory.GetFiles(dir, "*.png", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                // 纯数字文件名(2026-08-22 恢复 left 前缀前的命名契约,与 README 一致)
                int idx;
                if (name.Length > 0 &&
                    int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out idx))
                    files.Add(new KeyValuePair<int, string>(idx, file));
            }

            if (files.Count < 2)
            {
                FaceSDFShadowPlugin.Log.LogInfo(
                    $"Manual SDF: {files.Count} frame(s) under {dir} (need >= 2); no manual SDF.");
                return null;
            }
            return TryBuildFrameSet(dir, files);
        }

        /// <summary>一套帧的最终解析(校验索引连续或 angles.txt、无重复)。失败返回 null。</summary>
        private static FrameSet TryBuildFrameSet(string dir, List<KeyValuePair<int, string>> present)
        {
            present.Sort((a, b) => a.Key.CompareTo(b.Key));
            for (int i = 1; i < present.Count; i++)
            {
                if (present[i].Key == present[i - 1].Key)
                {
                    FaceSDFShadowPlugin.Log.LogWarning(
                        $"Manual SDF: duplicate frame index {present[i].Key}; rejecting.");
                    return null;
                }
            }
            int maxIndex = present[present.Count - 1].Key;

            string anglesPath = Path.Combine(dir, "angles.txt");
            if (File.Exists(anglesPath))
            {
                float[] explicitAngles = TryLoadAngles(anglesPath, present.Count);
                if (explicitAngles == null)
                {
                    FaceSDFShadowPlugin.Log.LogWarning(
                        "Manual SDF: angles.txt invalid; rejecting frame set.");
                    return null;
                }
                var pathsA = new string[present.Count];
                for (int i = 0; i < present.Count; i++) pathsA[i] = present[i].Value;
                FaceSDFShadowPlugin.Log.LogInfo(
                    $"Manual SDF: loaded explicit sideAngle mapping ({explicitAngles.Length} frames) from angles.txt.");
                return new FrameSet(pathsA, explicitAngles);
            }

            bool contiguous = present.Count == maxIndex + 1;
            for (int i = 0; contiguous && i < present.Count; i++)
                contiguous = present[i].Key == i;
            if (!contiguous)
            {
                FaceSDFShadowPlugin.Log.LogWarning(
                    $"Manual SDF: frame indices must be contiguous 0..{maxIndex} " +
                    $"(found {present.Count}); rejecting.");
                return null;
            }
            var paths = new string[present.Count];
            var angles = new float[present.Count];
            for (int i = 0; i < present.Count; i++)
            {
                paths[i] = present[i].Value;
                // sideAngle 域 0..1:0=背面(1.0) → maxIndex=正面(0.0)。
                angles[i] = 1.0f - present[i].Key / (float)maxIndex;
            }
            FaceSDFShadowPlugin.Log.LogInfo(
                $"Manual SDF: zero-config sideAngle from contiguous indices 0..{maxIndex}: " +
                string.Join(", ", angles));
            return new FrameSet(paths, angles);
        }

        /// <summary>一套手绘帧的路径 + 角度(角度按文件顺序,尚未排序)。</summary>
        private sealed class FrameSet
        {
            internal readonly string[] Paths;
            internal readonly float[] Angles;
            internal FrameSet(string[] paths, float[] angles) { Paths = paths; Angles = angles; }
        }

        private static float[] TryLoadAngles(string path, int expectedCount)
        {
            try
            {
                string[] lines = File.ReadAllLines(path);
                if (lines.Length != expectedCount)
                {
                    FaceSDFShadowPlugin.Log.LogWarning(
                        $"Manual SDF: angles.txt has {lines.Length} lines, expected {expectedCount} frames.");
                    return null;
                }
                var angles = new float[expectedCount];
                for (int i = 0; i < expectedCount; i++)
                {
                    if (!float.TryParse(
                        lines[i].Trim(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out angles[i]))
                    {
                        FaceSDFShadowPlugin.Log.LogWarning(
                            $"Manual SDF: invalid angle on line {i + 1} of angles.txt: '{lines[i]}'.");
                        return null;
                    }
                    // sideAngle 域 0..1
                    if (!IsFinite(angles[i]) || angles[i] < 0f || angles[i] > 1f)
                    {
                        FaceSDFShadowPlugin.Log.LogWarning(
                            $"Manual SDF: sideAngle on line {i + 1} out of [0,1]: {angles[i]}.");
                        return null;
                    }
                }
                // 按文件顺序必须严格递减(背面→正面),且含 1.0 与 0.0 端点
                bool hasOne = false;
                bool hasZero = false;
                for (int i = 0; i < angles.Length; i++)
                {
                    if (angles[i] == 1f) hasOne = true;
                    if (angles[i] == 0f) hasZero = true;
                    if (i > 0 && angles[i] >= angles[i - 1])
                    {
                        FaceSDFShadowPlugin.Log.LogWarning(
                            "Manual SDF: angles.txt not strictly decreasing (back→front).");
                        return null;
                    }
                }
                if (!hasOne || !hasZero)
                {
                    FaceSDFShadowPlugin.Log.LogWarning(
                        "Manual SDF: angles.txt must include 1.0 (back) and 0.0 (front) endpoints.");
                    return null;
                }
                return angles;
            }
            catch (Exception ex)
            {
                FaceSDFShadowPlugin.Log.LogWarning(
                    $"Failed to read angles.txt: {ex.Message}");
                return null;
            }
        }

        private static float[] ReadLuma(Texture2D source)
        {
            Color[] pixels = source.GetPixels();
            var luma = new float[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
                luma[i] = SampleLuma(pixels[i]);
            return luma;
        }

        private static float SampleLuma(Color color)
        {
            float luma = color.r * 0.2126f + color.g * 0.7152f + color.b * 0.0722f;
            return Mathf.Clamp01(luma * Mathf.Clamp01(color.a));
        }

        private static void ValidatePlayback(string meshName, float[][] frames,
            float[] frameAngles, float[] lo, float[] hi, bool[] covered)
        {
            // 采样几帧(正面/中段/背面)验证窗口回放一致性。
            int[] steps = { 0, frames.Length / 2, frames.Length - 1 };
            for (int s = 0; s < steps.Length; s++)
            {
                int step = steps[s];
                if (step < 0 || step >= frames.Length)
                    continue;

                int mismatch = 0;
                int total = 0;
                float angle = frameAngles[step];
                for (int p = 0; p < lo.Length; p++)
                {
                    if (!covered[p]) continue;
                    bool expected = frames[step][p] > LitCutoff;
                    bool actual = lo[p] <= angle && angle <= hi[p];
                    if (expected != actual) mismatch++;
                    total++;
                }
                float ratio = total > 0 ? mismatch / (float)total : 1f;
                FaceSDFShadowPlugin.Log.LogInfo(
                    $"Manual SDF '{meshName}' replay sideAngle {angle:0.##}: " +
                    $"mismatch {mismatch}/{total} ({ratio * 100f:0.##}%).");
            }
        }

        /// <summary>
        /// Coverage-aware separable Gaussian blur on a threshold field.
        /// Only blurs over covered texels, re-normalizing the kernel to avoid
        /// pulling in values from uncovered (zero) areas.
        /// </summary>
        private static float[] BlurThresholdField(float[] source, bool[] covered,
            int size, float sigma)
        {
            int radius = Mathf.CeilToInt(3f * sigma);
            float[] kernel = BuildGaussianKernel(sigma, radius);
            float[] temp = new float[source.Length];
            float[] result = new float[source.Length];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int index = y * size + x;
                    if (!covered[index])
                    {
                        temp[index] = 0f;
                        continue;
                    }
                    float sum = 0f, weightSum = 0f;
                    for (int offset = -radius; offset <= radius; offset++)
                    {
                        int sx = x + offset;
                        if (sx < 0 || sx >= size) continue;
                        int nidx = y * size + sx;
                        if (!covered[nidx]) continue;
                        float w = kernel[offset + radius];
                        sum += source[nidx] * w;
                        weightSum += w;
                    }
                    temp[index] = weightSum > 1e-6f ? sum / weightSum : source[index];
                }
            }

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int index = y * size + x;
                    if (!covered[index])
                    {
                        result[index] = 0f;
                        continue;
                    }
                    float sum = 0f, weightSum = 0f;
                    for (int offset = -radius; offset <= radius; offset++)
                    {
                        int sy = y + offset;
                        if (sy < 0 || sy >= size) continue;
                        int nidx = sy * size + x;
                        if (!covered[nidx]) continue;
                        float w = kernel[offset + radius];
                        sum += temp[nidx] * w;
                        weightSum += w;
                    }
                    result[index] = weightSum > 1e-6f ? sum / weightSum : temp[index];
                }
            }
            return result;
        }

        private static float[] BuildGaussianKernel(float sigma, int radius)
        {
            var kernel = new float[radius * 2 + 1];
            double weightSum = 0d;
            double denominator = 2d * sigma * sigma;
            for (int offset = -radius; offset <= radius; offset++)
            {
                double weight = Math.Exp(-(offset * offset) / denominator);
                kernel[offset + radius] = (float)weight;
                weightSum += weight;
            }
            for (int i = 0; i < kernel.Length; i++)
                kernel[i] = (float)(kernel[i] / weightSum);
            return kernel;
        }

        private static void ExportDiagnostics(string meshName, float[] leftLo, float[] leftHi,
            float[] rightLo, float[] rightHi, bool[] covered)
        {
            string dir = Path.Combine(FaceSDFShadowPlugin.DataDir, "manual");
            Directory.CreateDirectory(dir);

            WriteGray(Path.Combine(dir, Sanitize(meshName) + "_manual_left_lo.png"), leftLo, covered);
            WriteGray(Path.Combine(dir, Sanitize(meshName) + "_manual_left_hi.png"), leftHi, covered);
            WriteGray(Path.Combine(dir, Sanitize(meshName) + "_manual_right_lo.png"), rightLo, covered);
            WriteGray(Path.Combine(dir, Sanitize(meshName) + "_manual_right_hi.png"), rightHi, covered);

            ExportAngleSlices(dir, meshName, "left", leftLo, leftHi, covered);
            ExportAngleSlices(dir, meshName, "right", rightLo, rightHi, covered);
            FaceSDFShadowPlugin.Log.LogInfo(
                $"Exported manual SDF left/right 360/270/180deg slices for '{meshName}'.");
        }

        private static void ExportAngleSlices(string dir, string meshName, string side,
            float[] lo, float[] hi, bool[] covered)
        {
            // 游戏光照角 → sideAngle:360(正面)=0、270(侧光)=0.5、180(背面)=1
            var slices = new[] {
                new { Deg = 360f, SideAngle = 0.0f },
                new { Deg = 270f, SideAngle = 0.5f },
                new { Deg = 180f, SideAngle = 1.0f },
            };
            var slice = new float[lo.Length];
            foreach (var s in slices)
            {
                for (int p = 0; p < slice.Length; p++)
                    slice[p] = covered[p] && lo[p] <= s.SideAngle && s.SideAngle <= hi[p] ? 1f : 0f;
                WriteGray(
                    Path.Combine(dir, Sanitize(meshName) + "_manual_" + side + "_lit_" +
                        s.Deg.ToString("000") + "deg.png"), slice, covered);
            }
        }

        private static void WriteGray(string path, float[] values, bool[] covered)
        {
            int size = ManualContourInterpolator.Size;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            try
            {
                var pixels = new Color32[values.Length];
                for (int i = 0; i < pixels.Length; i++)
                {
                    byte value = covered[i] ? ToByte(values[i]) : (byte)0;
                    pixels[i] = new Color32(value, value, value, 255);
                }
                tex.SetPixels32(pixels);
                tex.Apply(false, false);
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.Destroy(tex);
            }
        }

        private static byte ToByte(float value)
        {
            return (byte)(Mathf.Clamp01(value) * 255f + 0.5f);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
    }
}
