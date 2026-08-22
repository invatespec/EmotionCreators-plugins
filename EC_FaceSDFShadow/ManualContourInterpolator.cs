using System;
using System.Diagnostics;

namespace EC_FaceSDFShadow
{
    /// <summary>
    /// 把一套硬边手绘帧(白=受光、黑=阴影)按相邻帧的带符号距离场(chamfer)插值成
    /// 连续的受光窗口 [lo, hi]。
    ///
    /// 帧已按 sideAngle 升序传入(0=正面 → 1=背面)。每个像素找最长受光段:
    /// - rising(暗→亮)边界 → lo,falling(亮→暗)边界 → hi,各自用相邻帧的距离场零交叉
    ///   插值,得到连续的 sub-frame 角度。这样 9 帧硬边输入也能平滑扫掠,而不是
    ///   所有边界像素锁在帧中点同时跳。
    /// </summary>
    internal static class ManualContourInterpolator
    {
        internal const int Size = SDFTextureResolution.DefaultSize;
        private const int PixelCount = Size * Size;

        private const float LitCutoff = 0.5f;
        private const float DistanceEpsilon = 1e-5f;
        private const float DistanceInfinity = float.PositiveInfinity;
        private const float DiagonalCost = 1.4142135623730951f;

        private static readonly int[] NeighborX = { -1, 0, 1, -1, 1, -1, 0, 1 };
        private static readonly int[] NeighborY = { -1, -1, -1, 0, 0, 1, 1, 1 };

        internal struct Diagnostics
        {
            internal bool ValidInput;
            internal int ChartCount;
            internal int CoveredPixelCount;
            internal int RisingCount;
            internal int FallingCount;
            internal int DistanceFallbackCount;
            internal double DistanceElapsedMs;
            internal double TotalElapsedMs;
            public override string ToString()
            {
                return $"charts {ChartCount}, rising {RisingCount}, falling {FallingCount}, " +
                    $"distance {DistanceElapsedMs:0.###} ms, total {TotalElapsedMs:0.###} ms, " +
                    $"fallbacks {DistanceFallbackCount}";
            }
        }

        /// <summary>
        /// frames 按 sideAngle 升序(frames[0]=正面 0 → 末尾=背面 1);angles 为每帧 sideAngle。
        /// 输出 lo/hi(受光窗口)。失败返回 false,调用方回退 raw 帧中点插值。
        /// </summary>
        internal static bool TryBuildWindow(float[][] frames, bool[] coverage, float[] angles,
            out float[] lo, out float[] hi, out Diagnostics diagnostics)
        {
            lo = null;
            hi = null;
            diagnostics = new Diagnostics();
            if (!HasValidInput(frames, coverage, angles))
                return false;

            int frameCount = frames.Length;
            var totalStopwatch = Stopwatch.StartNew();

            var labels = new int[PixelCount];
            var queue = new int[PixelCount];
            diagnostics.ChartCount = LabelCharts(coverage, labels, queue,
                out diagnostics.CoveredPixelCount);
            if (diagnostics.ChartCount == 0)
                return false;

            lo = new float[PixelCount];
            hi = new float[PixelCount];

            // 逐像素二值化 lit 序列
            var lit = new byte[frameCount][];
            for (int k = 0; k < frameCount; k++)
            {
                lit[k] = new byte[PixelCount];
                float[] fk = frames[k];
                for (int i = 0; i < PixelCount; i++)
                    lit[k][i] = coverage[i] && fk[i] > LitCutoff ? (byte)1 : (byte)0;
            }

            // 每个像素的最长受光段 [bestA, bestB](帧索引);全暗/全亮直接写死,不进插值。
            var bestA = new int[PixelCount];
            var bestB = new int[PixelCount];
            for (int i = 0; i < PixelCount; i++)
            {
                bestA[i] = -1;
                bestB[i] = -1;
                if (!coverage[i]) { lo[i] = 1f; hi[i] = 0f; continue; }

                int bestLen = 0, a = -1, b = -1, runStart = -1;
                for (int k = 0; k <= frameCount; k++)
                {
                    bool l = k < frameCount && lit[k][i] == 1;
                    if (l)
                    {
                        if (runStart < 0) runStart = k;
                    }
                    else if (runStart >= 0)
                    {
                        int len = k - runStart;
                        if (len > bestLen) { bestLen = len; a = runStart; b = k - 1; }
                        runStart = -1;
                    }
                }

                if (a < 0)
                {
                    lo[i] = 1f; hi[i] = 0f;      // 全程阴影
                }
                else if (a == 0 && b == frameCount - 1)
                {
                    lo[i] = 0f; hi[i] = 1f;      // 全程受光
                }
                else
                {
                    bestA[i] = a;
                    bestB[i] = b;
                    // 帧中点兜底:插值失败时也保持正确区间
                    lo[i] = angles[a];
                    hi[i] = angles[b];
                }
            }

            // 逐帧对算带符号距离,对 rising/falling 边界做零交叉插值。
            var prevDist = new float[PixelCount];
            var curDist = new float[PixelCount];
            var distanceStopwatch = Stopwatch.StartNew();
            ComputeSignedDistance(labels, lit[0], prevDist);

            for (int k = 0; k < frameCount - 1; k++)
            {
                if (k > 0)
                    Array.Copy(curDist, prevDist, PixelCount);
                ComputeSignedDistance(labels, lit[k + 1], curDist);

                for (int i = 0; i < PixelCount; i++)
                {
                    if (!coverage[i]) continue;

                    // rising:像素在帧 k 暗、k+1 亮 → lo
                    if (bestA[i] == k + 1)
                    {
                        lo[i] = InterpolateZero(angles[k], angles[k + 1],
                            prevDist[i], curDist[i], ref diagnostics);
                        diagnostics.RisingCount++;
                    }
                    // falling:像素在帧 k 亮、k+1 暗 → hi
                    if (bestB[i] == k)
                    {
                        hi[i] = InterpolateZero(angles[k], angles[k + 1],
                            prevDist[i], curDist[i], ref diagnostics);
                        diagnostics.FallingCount++;
                    }
                }
            }
            distanceStopwatch.Stop();
            diagnostics.DistanceElapsedMs =
                distanceStopwatch.ElapsedTicks * 1000.0 / Stopwatch.Frequency;
            totalStopwatch.Stop();
            diagnostics.TotalElapsedMs =
                totalStopwatch.ElapsedTicks * 1000.0 / Stopwatch.Frequency;
            diagnostics.ValidInput = true;
            return true;
        }

        /// <summary>
        /// 用相邻帧的带符号距离场求零交叉,得到连续 sub-frame sideAngle。
        /// rising/falling 共用一个公式:fraction = prevDist / (prevDist - curDist),
        /// 其中暗区距离为负、亮区距离为正,已验证两个方向都收敛到 d_p/(d_p+d_c)。
        /// </summary>
        private static float InterpolateZero(float a0, float a1, float prevDist, float curDist,
            ref Diagnostics diagnostics)
        {
            float denominator = prevDist - curDist;
            float fraction = 0.5f;
            bool valid = IsFinite(prevDist) && IsFinite(curDist) &&
                Math.Abs(denominator) > DistanceEpsilon;
            if (valid)
            {
                fraction = prevDist / denominator;
                if (!IsFinite(fraction))
                    valid = false;
            }
            if (!valid)
            {
                diagnostics.DistanceFallbackCount++;
                fraction = 0.5f;
            }
            else
            {
                fraction = Clamp01(fraction);
            }
            return a0 + fraction * (a1 - a0);
        }

        /// <summary>
        /// 8 邻域 chamfer distance 只在同一 chart 内传播;洞和相邻 chart 都是硬边界。
        /// </summary>
        private static void ComputeSignedDistance(int[] labels, byte[] lit, float[] distance)
        {
            MarkContourSeeds(labels, lit, distance);
            ForwardDistancePass(labels, distance);
            ReverseDistancePass(labels, distance);
            SignDistance(labels, lit, distance);
        }

        private static void MarkContourSeeds(int[] labels, byte[] lit, float[] distance)
        {
            for (int y = 0; y < Size; y++)
            {
                int row = y * Size;
                for (int x = 0; x < Size; x++)
                {
                    int index = row + x;
                    int chart = labels[index];
                    if (chart == 0)
                    {
                        distance[index] = 0f;
                        continue;
                    }
                    bool boundary = false;
                    byte state = lit[index];
                    for (int n = 0; n < NeighborX.Length; n++)
                    {
                        int nx = x + NeighborX[n];
                        int ny = y + NeighborY[n];
                        if (nx < 0 || nx >= Size || ny < 0 || ny >= Size)
                            continue;
                        int neighbor = ny * Size + nx;
                        if (labels[neighbor] == chart && lit[neighbor] != state)
                        {
                            boundary = true;
                            break;
                        }
                    }
                    // 轮廓位于相邻像素中心之间;用 0.5 保证翻转阈值严格落在两张关键帧之间。
                    distance[index] = boundary ? 0.5f : DistanceInfinity;
                }
            }
        }

        private static void ForwardDistancePass(int[] labels, float[] distance)
        {
            for (int y = 0; y < Size; y++)
            {
                int row = y * Size;
                for (int x = 0; x < Size; x++)
                {
                    int index = row + x;
                    int chart = labels[index];
                    if (chart == 0)
                        continue;
                    float value = distance[index];
                    if (x > 0 && labels[index - 1] == chart)
                        value = Math.Min(value, distance[index - 1] + 1f);
                    if (y > 0 && labels[index - Size] == chart)
                        value = Math.Min(value, distance[index - Size] + 1f);
                    if (x > 0 && y > 0 && labels[index - Size - 1] == chart)
                        value = Math.Min(value, distance[index - Size - 1] + DiagonalCost);
                    if (x + 1 < Size && y > 0 && labels[index - Size + 1] == chart)
                        value = Math.Min(value, distance[index - Size + 1] + DiagonalCost);
                    distance[index] = value;
                }
            }
        }

        private static void ReverseDistancePass(int[] labels, float[] distance)
        {
            for (int y = Size - 1; y >= 0; y--)
            {
                int row = y * Size;
                for (int x = Size - 1; x >= 0; x--)
                {
                    int index = row + x;
                    int chart = labels[index];
                    if (chart == 0)
                        continue;
                    float value = distance[index];
                    if (x + 1 < Size && labels[index + 1] == chart)
                        value = Math.Min(value, distance[index + 1] + 1f);
                    if (y + 1 < Size && labels[index + Size] == chart)
                        value = Math.Min(value, distance[index + Size] + 1f);
                    if (x + 1 < Size && y + 1 < Size && labels[index + Size + 1] == chart)
                        value = Math.Min(value, distance[index + Size + 1] + DiagonalCost);
                    if (x > 0 && y + 1 < Size && labels[index + Size - 1] == chart)
                        value = Math.Min(value, distance[index + Size - 1] + DiagonalCost);
                    distance[index] = value;
                }
            }
        }

        private static void SignDistance(int[] labels, byte[] lit, float[] distance)
        {
            for (int i = 0; i < PixelCount; i++)
            {
                if (labels[i] != 0 && lit[i] == 0)
                    distance[i] = -distance[i];
            }
        }

        private static int LabelCharts(bool[] coverage, int[] labels, int[] queue,
            out int coveredPixelCount)
        {
            int chartCount = 0;
            coveredPixelCount = 0;
            for (int index = 0; index < PixelCount; index++)
            {
                if (!coverage[index])
                    continue;
                coveredPixelCount++;
                if (labels[index] != 0)
                    continue;
                chartCount++;
                int head = 0;
                int tail = 0;
                queue[tail++] = index;
                labels[index] = chartCount;
                while (head < tail)
                {
                    int current = queue[head++];
                    int x = current % Size;
                    int y = current / Size;
                    for (int n = 0; n < NeighborX.Length; n++)
                    {
                        int nx = x + NeighborX[n];
                        int ny = y + NeighborY[n];
                        if (nx < 0 || nx >= Size || ny < 0 || ny >= Size)
                            continue;
                        int neighbor = ny * Size + nx;
                        if (!coverage[neighbor] || labels[neighbor] != 0)
                            continue;
                        labels[neighbor] = chartCount;
                        queue[tail++] = neighbor;
                    }
                }
            }
            return chartCount;
        }

        private static bool HasValidInput(float[][] frames, bool[] coverage, float[] angles)
        {
            if (frames == null || frames.Length < 2 ||
                coverage == null || coverage.Length != PixelCount ||
                angles == null || angles.Length != frames.Length)
                return false;
            for (int f = 0; f < frames.Length; f++)
            {
                if (frames[f] == null || frames[f].Length != PixelCount)
                    return false;
            }
            // sideAngle 域 0..1,升序,含 0 与 1 端点。
            for (int i = 0; i < angles.Length; i++)
            {
                if (!IsFinite(angles[i]) || angles[i] < 0f || angles[i] > 1f)
                    return false;
                if (i > 0 && angles[i] <= angles[i - 1])
                    return false;
            }
            return angles[0] == 0f && angles[angles.Length - 1] == 1f;
        }

        private static float Clamp01(float value)
        {
            if (value <= 0f) return 0f;
            if (value >= 1f) return 1f;
            return value;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}