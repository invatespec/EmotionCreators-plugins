using System.Collections.Generic;
using UnityEngine;

namespace EC_FaceSDFShadow
{
    /// <summary>
    /// 表情 UV 补偿·运行时实测模式。
    ///
    /// 背景：离线 blendshape 系数表只对纯 blendshape 表情
    /// （wink/def_cl）准确；切ない等表情的颊部上移由 FBSAssist 按表情权重直接驱动
    /// （不走 cf_O_face blendshape 通道），离线表结构性缺失。本模式改为每帧
    /// BakeMesh 实测"当前姿势 vs 中性参照"的顶点位移——对任意头型（原版/mod/换头）
    /// 与任意驱动源（blendshape/骨骼/第三方）天然正确。
    ///
    /// 管线：onPreCull（权重终值）→ BakeMesh → 顶点转到头骨空间（剔头部旋转）→
    /// 按区域求"UV 梯度投影位移"均值 → 以纯平移 affine 写入现有 _BlendCompAffine
    /// （shader 零改动）。
    ///
    /// 中性参照：全部 blendshape 权重≈0 的帧自动采样（FBSAssist 联动同步归零）。
    /// 参照建立前回退离线表（FaceBlendCompensation）。
    /// </summary>
    internal static class FaceLiveCompensation
    {
        // 区域矩形（u0,v0,u1,v1），与离线 REGIONS/shader 定义一致
        private static readonly Vector4[] RegionRects =
        {
            new Vector4(0.32f, 0.36f, 0.44f, 0.44f), // 0 R_cheek
            new Vector4(0.56f, 0.36f, 0.68f, 0.44f), // 1 L_cheek
            new Vector4(0.30f, 0.45f, 0.50f, 0.70f), // 2 R_eye
            new Vector4(0.50f, 0.45f, 0.70f, 0.70f), // 3 L_eye
            new Vector4(0.44f, 0.15f, 0.56f, 0.50f), // 4 mouth
        };

        private class LiveState
        {
            internal Mesh Bake;                    // 复用烘焙目标（BakeMesh 覆写）
            internal List<Vector3> BakedVertices;  // GetVertices 复用缓冲，避免每帧 Mesh.vertices 分配
            internal Vector3[] RefHead;            // 参照顶点（头骨空间）
            internal bool IsValid;                 // 坏拓扑只拒绝一次，后续稳定回退离线表
            internal bool HasRef;
            internal bool BakeIsWorld;             // BakeMesh 输出空间自适应结果
            internal Vector3[] GradU, GradV;       // 每顶点 UV 梯度（mesh 空间，归一化 UV/位置）
            internal int[] RegionOf;               // 每顶点区域，-1=区外
            internal readonly float[] SumDU = new float[5];
            internal readonly float[] SumDV = new float[5];
            internal readonly int[] Count = new int[5];
            internal Mesh MeshKey;                 // 梯度所属 mesh（换头重建）
            internal int LastRefFrame = -1000;
            // "无表情"判定豁免通道：眼/口的 def 插值对（中性=睁眼+闭嘴时恒非零）。
            // 实测 F10 dump：中性时 eye_def_cl≈26~49、kuti_def_cl=100。
            internal readonly int[] ExemptIdxs = new int[4];
            internal int CalmStreak;               // 连续"无表情且 def 稳定"帧计数（防 idle 眨眼中途采参照）
            internal float PrevDefCl = -999f;      // 上一帧 eye def_cl，稳定=帧间变化小（首帧无先验视为稳定）
        }

        private static readonly Dictionary<SkinnedMeshRenderer, LiveState> _states =
            new Dictionary<SkinnedMeshRenderer, LiveState>();

        // 每帧输出 buffer（5 区域 × 6 affine 系数，纯平移形式），复用零 GC
        private static readonly float[] _accum = new float[30];

        /// <summary>
        /// onPreCull 阶段：实测当前表情位移并推给 overlay 材质。
        /// 返回 false 表示本帧未产出（参照未建立/异常），调用方回退离线表。
        /// </summary>
        internal static bool Push(SkinnedMeshRenderer smr, Material mat, Transform headBone)
        {
            if (mat == null || smr == null) return false;
            if (!FaceSDFShadowPlugin.BlendCompLive.Value) return false;
            if (!FaceSDFShadowPlugin.BlendCompensation.Value)
            {
                mat.SetFloat(ShaderIDs.BlendCompEnable, 0f);
                return true; // 总开关关闭：已处理（推 0），无需离线兜底
            }

            var mesh = smr.sharedMesh;
            if (mesh == null || headBone == null) return false;

            if (!_states.TryGetValue(smr, out var st) || st.MeshKey != mesh)
            {
                ReleaseState(st);
                st = InitState(smr, mesh);
            }
            if (st == null || !st.IsValid) return false;

            // 中性参照：无表情帧刷新（def 插值豁免）。idle 自动眨眼时 def_cl 周期变化、
            // 其它通道恒零，故再要求 def 权重帧间稳定——半闭眼帧不会成为参照。
            if (AllWeightsZero(smr, st))
            {
                float defCl = st.ExemptIdxs[0] >= 0 ? smr.GetBlendShapeWeight(st.ExemptIdxs[0]) : 0f;
                bool defStable = st.PrevDefCl < -900f || Mathf.Abs(defCl - st.PrevDefCl) < 5f;
                st.PrevDefCl = defCl;
                if (defStable && Time.frameCount - st.LastRefFrame > 30 && ++st.CalmStreak >= 10)
                {
                    if (RefreshReference(smr, headBone, st))
                    {
                        st.LastRefFrame = Time.frameCount;
                        st.CalmStreak = 0;
                    }
                }
                else if (!defStable)
                {
                    st.CalmStreak = 0;
                }
            }
            else
            {
                st.CalmStreak = 0;
                st.PrevDefCl = -999f;
            }

            if (!st.HasRef)
            {
                // 参照未建立：本帧不出实测值，由离线表兜底
                mat.SetFloat(ShaderIDs.BlendCompEnable, 0f);
                return false;
            }

            // 当前姿势 → 头骨空间
            if (!TryBakeVertices(smr, st)) return false;
            var verts = st.BakedVertices;
            var toHead = HeadSpaceMatrix(smr, headBone, st.BakeIsWorld);

            for (int r = 0; r < 5; r++) { st.SumDU[r] = 0f; st.SumDV[r] = 0f; st.Count[r] = 0; }
            for (int v = 0; v < verts.Count; v++)
            {
                int r = st.RegionOf[v];
                if (r < 0) continue;
                var vp = toHead.MultiplyPoint3x4(verts[v]);
                var d = vp - st.RefHead[v];
                // 参照失效保护：单点位移超 0.2 mesh 单位（≈脸宽级别）视为异常，丢弃该区域帧
                if (d.sqrMagnitude > 0.04f) { st.Count[r] = -1000000; continue; }
                st.SumDU[r] += Vector3.Dot(st.GradU[v], d);
                st.SumDV[r] += Vector3.Dot(st.GradV[v], d);
                st.Count[r]++;
            }

            float gain = FaceSDFShadowPlugin.BlendCompGain.Value;
            float mouthScale = FaceSDFShadowPlugin.BlendCompMouth.Value ? 1f : 0f;
            for (int r = 0; r < 30; r++) _accum[r] = 0f;
            bool any = false;
            for (int r = 0; r < 5; r++)
            {
                if (st.Count[r] <= 0) continue; // 异常或空区域：推 0 = 该区不补偿
                // 梯度·位移 = UV 单位；按离线拟合的 512 texel 基准写入，shader 再还原 UV。
                float scale = gain * FaceBlendCompensation.TexelSize;
                if (r == 4) scale *= mouthScale;
                _accum[r * 6 + 2] = st.SumDU[r] / st.Count[r] * scale;
                _accum[r * 6 + 5] = st.SumDV[r] / st.Count[r] * scale;
                any = true;
            }

            mat.SetFloat(ShaderIDs.BlendCompEnable, any ? 1f : 0f);
            if (any)
                mat.SetFloatArray(ShaderIDs.BlendCompAffine, _accum);
            return true;
        }

        private static LiveState InitState(SkinnedMeshRenderer smr, Mesh mesh)
        {
            var st = new LiveState
            {
                MeshKey = mesh,
            };
            // 失败状态也缓存到 SMR；否则坏/mod 头会每帧重试并刷日志。
            _states[smr] = st;

            Vector3[] verts;
            Vector2[] uvs;
            int[] tris;
            try
            {
                verts = mesh.vertices;
                uvs = mesh.uv;
                tris = mesh.triangles;
            }
            catch (System.Exception ex)
            {
                FaceSDFShadowPlugin.Log.LogWarning(
                    $"[BlendComp] live mode rejected unreadable mesh '{mesh.name}': {ex.Message}; using offline fallback");
                return st;
            }

            if (!ValidateTopology(verts, uvs, tris))
            {
                FaceSDFShadowPlugin.Log.LogWarning(
                    $"[BlendComp] live mode rejected invalid mesh '{mesh.name}' " +
                    $"(vertices={verts?.Length ?? 0}, uv={uvs?.Length ?? 0}, triangles={tris?.Length ?? 0}); " +
                    "using offline fallback");
                return st;
            }

            st.Bake = new Mesh { name = "FaceSDF_LiveBake" };
            st.Bake.MarkDynamic();
            st.BakedVertices = new List<Vector3>(verts.Length);
            st.RefHead = new Vector3[verts.Length];
            st.RegionOf = BuildRegionMap(uvs);
            st.ExemptIdxs[0] = mesh.GetBlendShapeIndex("eye_face.f00_def_cl");
            st.ExemptIdxs[1] = mesh.GetBlendShapeIndex("eye_face.f00_def_op");
            st.ExemptIdxs[2] = mesh.GetBlendShapeIndex("kuti_face.f00_def_cl");
            st.ExemptIdxs[3] = mesh.GetBlendShapeIndex("kuti_face.f00_def_op");
            BuildGradients(verts, uvs, tris, out st.GradU, out st.GradV);
            st.IsValid = true;
            return st;
        }

        private static bool ValidateTopology(Vector3[] verts, Vector2[] uvs, int[] tris)
        {
            if (verts == null || verts.Length == 0 || uvs == null || uvs.Length != verts.Length ||
                tris == null || tris.Length == 0 || tris.Length % 3 != 0)
                return false;

            for (int i = 0; i < tris.Length; i++)
            {
                if (tris[i] < 0 || tris[i] >= verts.Length)
                    return false;
            }
            return true;
        }

        /// <summary>顶点 → 区域映射（按 UV 矩形，一次）</summary>
        private static int[] BuildRegionMap(Vector2[] uvs)
        {
            var map = new int[uvs.Length];
            for (int v = 0; v < uvs.Length; v++)
            {
                map[v] = -1;
                for (int r = 0; r < 5; r++)
                {
                    if (uvs[v].x >= RegionRects[r].x && uvs[v].x <= RegionRects[r].z &&
                        uvs[v].y >= RegionRects[r].y && uvs[v].y <= RegionRects[r].w)
                    {
                        map[v] = r;
                        break;
                    }
                }
            }
            return map;
        }

        /// <summary>
        /// 每顶点 UV 梯度（mesh 空间）：共享三角形按面积加权平均。
        /// 梯度把位置位移投影到 UV（与离线 Gram 逆修正同一数学，逐顶点精确版）。
        /// </summary>
        private static void BuildGradients(Vector3[] verts, Vector2[] uvs, int[] tris,
            out Vector3[] gradU, out Vector3[] gradV)
        {
            gradU = new Vector3[verts.Length];
            gradV = new Vector3[verts.Length];
            var wU = new float[verts.Length];
            var wV = new float[verts.Length];

            for (int t = 0; t < tris.Length; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                var e1 = verts[b] - verts[a];
                var e2 = verts[c] - verts[a];
                float eu1 = uvs[b].x - uvs[a].x, ev1 = uvs[b].y - uvs[a].y;
                float eu2 = uvs[c].x - uvs[a].x, ev2 = uvs[c].y - uvs[a].y;
                float det = eu1 * ev2 - eu2 * ev1;
                if (Mathf.Abs(det) < 1e-12f) continue;
                // ∂P/∂u、∂P/∂v（E1/E2），再 Gram 逆得 ∇u、∇v（位置空间梯度）
                var E1 = (e1 * ev2 - e2 * ev1) / det;
                var E2 = (e2 * eu1 - e1 * eu2) / det;
                float G11 = Vector3.Dot(E1, E1), G22 = Vector3.Dot(E2, E2), G12 = Vector3.Dot(E1, E2);
                float gdet = G11 * G22 - G12 * G12;
                if (Mathf.Abs(gdet) < 1e-16f) continue;
                var gU = (E1 * G22 - E2 * G12) / gdet;  // ∇u
                var gV = (E2 * G11 - E1 * G12) / gdet;  // ∇v
                float area = Vector3.Cross(e1, e2).magnitude;
                for (int k = 0; k < 3; k++)
                {
                    int vi = tris[t + k];
                    gradU[vi] += gU * area; wU[vi] += area;
                    gradV[vi] += gV * area; wV[vi] += area;
                }
            }
            for (int v = 0; v < verts.Length; v++)
            {
                if (wU[v] > 0f) gradU[v] /= wU[v];
                if (wV[v] > 0f) gradV[v] /= wV[v];
            }
        }

        /// <summary>
        /// "无表情"判定：除眼/口 def 插值对（中性=睁眼+闭嘴时恒非零：eye_def_cl≈26~49、
        /// kuti_def_cl=100）外全部权重≈0。参照在"当时的中性"采样：def 插值位移计入基准，
        /// 眨眼/表情相对它测量——正确语义。
        /// </summary>
        private static bool AllWeightsZero(SkinnedMeshRenderer smr, LiveState st)
        {
            int n = st.MeshKey.blendShapeCount;
            for (int i = 0; i < n; i++)
            {
                if (i == st.ExemptIdxs[0] || i == st.ExemptIdxs[1] ||
                    i == st.ExemptIdxs[2] || i == st.ExemptIdxs[3]) continue;
                if (Mathf.Abs(smr.GetBlendShapeWeight(i)) > 0.5f) return false;
            }
            return true;
        }

        private static bool RefreshReference(SkinnedMeshRenderer smr, Transform headBone, LiveState st)
        {
            if (!TryBakeVertices(smr, st)) return false;
            st.BakeIsWorld = DetectBakeSpace(smr, headBone, st.BakedVertices);
            var toHead = HeadSpaceMatrix(smr, headBone, st.BakeIsWorld);
            var verts = st.BakedVertices;
            for (int v = 0; v < verts.Count; v++)
                st.RefHead[v] = toHead.MultiplyPoint3x4(verts[v]);
            bool hadReference = st.HasRef;
            st.HasRef = true;
#if DEBUG
            if (!hadReference)
                FaceSDFShadowPlugin.Log.LogDebug("[BlendComp] neutral reference captured (live mode active)");
#endif
            return true;
        }

        private static bool TryBakeVertices(SkinnedMeshRenderer smr, LiveState st)
        {
            try
            {
                smr.BakeMesh(st.Bake);
                return ReadBakedVertices(st);
            }
            catch (System.Exception ex)
            {
                st.IsValid = false;
                st.HasRef = false;
                FaceSDFShadowPlugin.Log.LogWarning(
                    $"[BlendComp] BakeMesh failed for '{st.MeshKey.name}': {ex.Message}; using offline fallback");
                return false;
            }
        }

        private static bool ReadBakedVertices(LiveState st)
        {
            st.BakedVertices.Clear();
            st.Bake.GetVertices(st.BakedVertices);
            if (st.BakedVertices.Count == st.RefHead.Length) return true;

            st.IsValid = false;
            st.HasRef = false;
            FaceSDFShadowPlugin.Log.LogWarning(
                $"[BlendComp] BakeMesh vertex count changed for '{st.MeshKey.name}' " +
                $"({st.BakedVertices.Count} != {st.RefHead.Length}); using offline fallback");
            return false;
        }

        /// <summary>
        /// BakeMesh 输出空间自适应：比较两种解释下顶点均值到头骨位置的距离。
        /// EC 的 2017.4 实测为世界空间，但 SMR transform 接近 identity 时两者数值接近，
        /// 取更近者并保留该解释（头骨离脸中心近，错误解释会引入 SMR 偏移误差）。
        /// </summary>
        private static bool DetectBakeSpace(SkinnedMeshRenderer smr, Transform headBone, List<Vector3> verts)
        {
            if (verts.Count == 0) return true;
            var mean = Vector3.zero;
            for (int v = 0; v < verts.Count; v += 16) mean += verts[v]; // 抽样均值
            mean /= Mathf.Max(1, (verts.Count + 15) / 16);
            float dWorld = Vector3.Distance(mean, headBone.position);
            float dLocal = Vector3.Distance(
                smr.transform.localToWorldMatrix.MultiplyPoint3x4(mean), headBone.position);
            return dWorld <= dLocal;
        }

        private static Matrix4x4 HeadSpaceMatrix(SkinnedMeshRenderer smr, Transform headBone, bool bakeIsWorld)
        {
            // 世界 → 头骨 local；BakeMesh 若为 local 空间则先经 SMR 变换到世界
            return headBone.worldToLocalMatrix *
                   (bakeIsWorld ? Matrix4x4.identity : smr.transform.localToWorldMatrix);
        }

        /// <summary>清理已销毁 SMR 的状态（Poll 死条目清理时顺带调用）</summary>
        internal static void CleanupDead()
        {
            List<SkinnedMeshRenderer> dead = null;
            foreach (var k in _states.Keys)
            {
                if (k == null)
                    (dead ?? (dead = new List<SkinnedMeshRenderer>())).Add(k);
            }
            if (dead == null) return;
            foreach (var k in dead)
            {
                ReleaseState(_states[k]);
                _states.Remove(k);
            }
        }

        private static void ReleaseState(LiveState st)
        {
            if (st != null && st.Bake != null)
                Object.Destroy(st.Bake);
        }

        internal static void Dispose()
        {
            foreach (var st in _states.Values)
                ReleaseState(st);
            _states.Clear();
            System.Array.Clear(_accum, 0, _accum.Length);
        }

#if DEBUG
        /// <summary>
        /// live 状态诊断（F11 dump 追加段）：参照是否建立、卡在哪个条件、
        /// 当前阻碍"无表情"判定的非 def 非零通道。
        /// </summary>
        internal static void DumpState(SkinnedMeshRenderer smr)
        {
            var log = FaceSDFShadowPlugin.Log;
            log.LogWarning($"=== BlendComp live state: liveMode={FaceSDFShadowPlugin.BlendCompLive.Value} ===");
            if (smr == null || smr.sharedMesh == null) { log.LogWarning("  (no face smr)"); return; }
            if (!_states.TryGetValue(smr, out var st))
            {
                log.LogWarning("  no LiveState (InitState 未执行：Push 早退或 liveMode 关)");
                return;
            }
            log.LogWarning($"  HasRef={st.HasRef} CalmStreak={st.CalmStreak} PrevDefCl={st.PrevDefCl} " +
                           $"LastRefFrame={st.LastRefFrame} now={Time.frameCount} Exempt={string.Join(",", st.ExemptIdxs)}");
            var mesh = smr.sharedMesh;
            int n = mesh.blendShapeCount;
            int shown = 0;
            for (int i = 0; i < n && shown < 12; i++)
            {
                if (System.Array.IndexOf(st.ExemptIdxs, i) >= 0) continue;
                float w = smr.GetBlendShapeWeight(i);
                if (Mathf.Abs(w) > 0.5f)
                {
                    log.LogWarning($"  blocking: [{i}] {mesh.GetBlendShapeName(i)} = {w:F1}");
                    shown++;
                }
            }
            if (shown == 0) log.LogWarning("  (无阻碍通道——AllWeightsZero 应成立，若 HasRef 仍 false 则查帧条件)");
        }
#endif
    }
}
