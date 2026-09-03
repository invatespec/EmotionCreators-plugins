using System.Collections.Generic;
using UnityEngine;

namespace EC_FaceSDFShadow
{
    /// <summary>
    /// 面部 SDF 阴影的两件事：
    /// 1) 把面部材质的 _RampG 覆盖成白图 —— 全局 ramp 阴影在脸上失效，身体/头发不受影响
    /// 2) 追加乘算叠加材质，画 SDF 硬边阴影
    ///
    /// 之所以能只让脸失效：Unity 中材质自身的属性值优先于 Shader.SetGlobalTexture 设的全局值，
    /// 而面部材质是逐角色克隆实例（CustomTextureControl.cs:28 clone=true）。
    /// </summary>
    internal static class FaceOverlayCore
    {
        private struct Entry
        {
            internal Material[] OrigMats;   // 原始材质数组（还原用）
            internal Material OverlayMat;   // 自建叠加材质
            internal Mesh Mesh;             // 应用时的 mesh，换 mesh 需重取阈值图
            internal string SdfSourceDir;   // 解析出的逐角色 SDF 目录，变化需重烘焙
            internal Transform HeadBone;    // 头骨 transform，每帧推 world→local 给 shader
            internal Matrix4x4 BindFix;     // 头骨 local → mesh 物体空间的静态校正（见 ResolveHeadBone）
            internal FaceSDFShadowController Controller;
            internal int ControllerRevision;
            // 哨兵：插件上次写入材质的值。材质当前值≠哨兵 = 被 ME/取色器改过（见 SyncPerMaterialParams）
            internal Color PushedColor;
            internal float PushedBias;
            internal float PushedSoftness;
        }

        private static readonly Dictionary<SkinnedMeshRenderer, Entry> _registry =
            new Dictionary<SkinnedMeshRenderer, Entry>();

        private static readonly List<SkinnedMeshRenderer> _deadBuffer =
            new List<SkinnedMeshRenderer>();

        // 全白 ramp：ramp 以光照项为 UV 采样，恒白 = 恒定全亮 = 无阴影
        private static Texture2D _whiteRamp;

        /// <summary>
        /// 尝试获取指定 SMR 的 overlay 材质（用于 Controller 读回 ShadowColor）
        /// </summary>
        internal static bool TryGetOverlayMaterial(SkinnedMeshRenderer smr, out Material overlayMat)
        {
            overlayMat = null;
            if (smr == null) return false;

            if (_registry.TryGetValue(smr, out var entry) && entry.OverlayMat != null)
            {
                overlayMat = entry.OverlayMat;
                return true;
            }
            return false;
        }

#if DEBUG
        /// <summary>
        /// [HairDiag] 发影混合架构实值:全局量一行 + 逐角色绑定一行。仅 DEBUG 构建。
        /// 坑:材质级 GetTexture/GetVector 对未列 Properties 的 uniform 恒报
        /// "doesn't have a property"(Set 可用、Get 不可用),绑定态必须从
        /// HairShadowPass 渲染侧字典读,不得读材质。
        /// </summary>
        internal static void LogHairDiag()
        {
            var dir = Shader.GetGlobalVector(ShaderIDs.HairLightDir);
            FaceSDFShadowPlugin.Log.LogInfo(string.Format(
                "[HairDiag] shiftXY(g)={0:F3}/{1:F3} baseXY(g)={2:F3}/{3:F3} " +
                "lightDir(g)=({4:F2},{5:F2},{6:F2}) form(g)={7:F0}",
                Shader.GetGlobalFloat(ShaderIDs.HairShadowShiftX),
                Shader.GetGlobalFloat(ShaderIDs.HairShadowShiftY),
                Shader.GetGlobalFloat(ShaderIDs.HairShadowBaseX),
                Shader.GetGlobalFloat(ShaderIDs.HairShadowBaseY),
                dir.x, dir.y, dir.z,
                Shader.GetGlobalFloat(ShaderIDs.HairShadowForm)));

            int idx = 0;
            foreach (var kvp in _registry)
            {
                var m = kvp.Value.OverlayMat;
                if (m == null) continue;
                bool rtBound = HairShadowPass.TryGetRT(kvp.Key, out var rt);
                FaceSDFShadowPlugin.Log.LogInfo(string.Format(
                    "[HairDiag] chara#{0} weight={1:F3} rt={2} headBone={3}",
                    idx++,
                    m.GetFloat(ShaderIDs.HairShadowWeight),
                    rtBound ? "bound" : "NULL",
                    kvp.Value.HeadBone != null ? "ok" : "null"));
            }
        }
#endif

        /// <summary>
        /// 每帧把头骨的 world→局部矩阵和表情 UV 补偿系数推给所有 overlay 材质。
        ///
        /// 必须每帧推、不能并进 15 帧的 Poll：阴影方向靠它算，隔帧推会让转头时阴影一跳一跳。
        /// shader 不能用 unity_WorldToObject —— cf_O_face 是 SkinnedMeshRenderer，顶点由骨骼
        /// 驱动，渲染器自身 transform 不跟头骨转（骨骼被重映射到共享骨架，
        /// AssignedAnotherWeights.AssignedWeightsAndSetBoundsLoop），用它会导致转头时阴影不动。
        /// </summary>
        internal static void PushHeadMatrices()
        {
            foreach (var kvp in _registry)
            {
                var entry = kvp.Value;
                var mat = entry.OverlayMat;
                if (mat == null) continue;

                // 表情 UV 补偿：live 实测优先（覆盖 blendshape/FBS 联动/骨骼全部驱动源），
                // 失败（模式关闭/参照未建立）回退离线系数表
                if (!FaceLiveCompensation.Push(kvp.Key, mat, entry.HeadBone))
                    FaceBlendCompensation.Push(kvp.Key, mat);

                var head = entry.HeadBone;
                if (head == null || head.Equals(null))
                {
                    // 头骨没解析到 → 让 shader 回落 unity_WorldToObject（不动但不崩）
                    mat.SetFloat(ShaderIDs.UseHeadMatrix, 0f);
                    continue;
                }

                var headWorldToLocal = entry.BindFix * head.worldToLocalMatrix;
                mat.SetMatrix(ShaderIDs.HeadWorldToLocal, headWorldToLocal);
                mat.SetFloat(ShaderIDs.UseHeadMatrix, 1f);
            }
        }

        // ---- 发影 v2 重画的全局推送 ----
        private static Light _hairLight;
        private static int _hairLightScansLeft;
        private static bool _hairLightWarned;

        /// <summary>光空间深度门 bias(米):贴头皮斑驳 ↔ 眨眼吞影的实测折中值,定死不暴露。</summary>
        internal const float HairShadowBiasMeters = 0.005f;

        // 光空间矩阵现在逐角色写入各 overlay/mask 材质(多角色),不再 SetGlobal。
        internal struct LightSpaceFrame
        {
            internal Matrix4x4 View;
            internal Matrix4x4 VP;
            internal Vector4 Box;
        }

        /// <summary>
        /// 每帧推发影几何偏移用的全局(CB 绘制不走 ForwardBase,拿不到
        /// _WorldSpaceLightPos0):灯向/移动量程(02/03)/基础偏移(04/05)。灯只取
        /// 相机层级下的平行光(见 IsUnderCamera),锁光插件转灯本体自动跟随;找不到
        /// 推零向量 → 偏移 0,RT 只在头发原位有值,脸像素采不到 → 发影自动静默。
        /// 权重/软核走 PushConfig 推给 overlay 消费端。
        /// 多角色:灯向/形态/量程保持全局;RT/光空间三件套逐角色写材质
        /// (材质属性优先于全局,shader 源码零改动)。
        /// </summary>
        internal static void PushHairLight()
        {
            bool lightAlive = _hairLight != null
                && _hairLight.enabled
                && _hairLight.gameObject.activeInHierarchy;
            if (!lightAlive || --_hairLightScansLeft <= 0)
            {
                RescanHairLight();
                _hairLightScansLeft = 15;
            }

            Shader.SetGlobalFloat(ShaderIDs.HairShadowShiftX,
                FaceSDFShadowPlugin.HairShadowShiftX.Value);
            Shader.SetGlobalFloat(ShaderIDs.HairShadowShiftY,
                FaceSDFShadowPlugin.HairShadowShiftY.Value);
            Shader.SetGlobalFloat(ShaderIDs.HairShadowBaseX,
                FaceSDFShadowPlugin.HairShadowBaseX.Value);
            Shader.SetGlobalFloat(ShaderIDs.HairShadowBaseY,
                FaceSDFShadowPlugin.HairShadowBaseY.Value);

            // forward 是光行进方向,"指向光源"取负(与 _WorldSpaceLightPos0 同语义)
            Vector3 lightDir = _hairLight != null
                ? -_hairLight.transform.forward : Vector3.zero;
            Shader.SetGlobalVector(ShaderIDs.HairLightDir, lightDir);

            // 01/06 形态与软边:两形态都推(消费端靠形态分支),06 的 LightSpace 路径
            // 沿用 _HairShadowBlur uniform 名
            int form = (int)FaceSDFShadowPlugin.HairShadowForm.Value;
            Shader.SetGlobalFloat(ShaderIDs.HairShadowForm, form);
            Shader.SetGlobalFloat(ShaderIDs.HairShadowBlur,
                FaceSDFShadowPlugin.HairShadowSoft.Value);
            // 深度门 bias 定死为实测折中值(0.005:贴头皮斑驳 ↔ 眨眼吞影),不暴露配置
            Shader.SetGlobalFloat(ShaderIDs.HairShadowBias, HairShadowBiasMeters);

            // 逐角色推送:RT 配对绑定(两形态都要——不写即采空)
            // + 形态 B 的光空间三件套(逐角色头位各算一份)。
            foreach (var kvp in _registry)
            {
                var entry = kvp.Value;
                var mat = entry.OverlayMat;
                if (mat == null) continue;

                if (!HairShadowPass.TryGetRT(kvp.Key, out var rt)) continue;
                mat.SetTexture(ShaderIDs.HairShadowRTGlobal, rt);

                if (form > 0)
                {
                    // 盒参数先推:无灯/无头骨时矩阵留零,但 Box 有值消费端
                    // 只是 uv=盒心、selfDepth=0 → 恒无影,不会 NaN(除零防护)。
                    var box = new Vector4(LightBoxHalfExtent,
                        1f / HairShadowPass.LightRTSize, 0f, 0f);
                    mat.SetVector(ShaderIDs.HairLightBox, box);

                    var head = entry.HeadBone;
                    if (lightDir.sqrMagnitude >= 1e-6f
                        && head != null && !head.Equals(null))
                    {
                        var frame = BuildLightSpaceFrame(lightDir, head.position);
                        // overlay 只声明 View/Box(VP 仅供 mask 光栅化,别往这推)
                        mat.SetMatrix(ShaderIDs.HairLightView, frame.View);
                        // 渲染侧同角色 mask 材质同帧同矩阵;统一刷"最后角色帧值"
                        // 会让其他角色头发投影出盒、RT 全黑无影
                        HairShadowPass.PushMaskMatrices(kvp.Key, frame);
                    }
                }
            }
            // 遮罩侧矩阵已在上面的循环里逐角色推送,此处无需再统一刷新
        }

        // 光空间正交盒:半宽 0.20m 恰好罩住头(直径约 0.25m),相机沿光向退 1.0m。
        // 盒越小 texel 越密——0.45m 半宽下 1 texel≈4 个屏幕像素,特写时边缘显影成
        // 阶梯锯齿;0.20m 下约 0.9 个像素(配 2048² RT)。代价是长发/侧发被裁出盒,
        // 但那些本来就投不到脸上。
        internal const float LightBoxHalfExtent = 0.20f;
        private const float LightBoxCamDist = 1.0f;
        // 盒心相对头骨上移:头骨在颈上端/下颌高度,头发体积中心比它高约 7cm,
        // 不偏置发顶会顶出盒上边界。
        internal const float LightBoxCenterUp = 0.07f;

        /// <summary>
        /// 形态 B 光空间三件套按角色计算。VP 只出光栅化位置(过 GL.GetGPUProjectionMatrix,
        /// 平台 FlipY/z 范围差异全关在里面);View+Box 让遮罩 pass 1 与 overlay 消费端
        /// 用同一公式算 UV/深度——渲染/采样映射错位类坑在此结构性不可达。
        /// </summary>
        internal static LightSpaceFrame BuildLightSpaceFrame(Vector3 lightDir, Vector3 head)
        {
            const float e = LightBoxHalfExtent;
            Vector3 l = ClampLightElevation(lightDir.normalized);
            Vector3 boxCenter = head + Vector3.up * LightBoxCenterUp;
            // 摆位:相机在光源侧看向头(shadow map 标准摆法)。反过来(头后看向光)
            // 会让刘海深度比脸远,深度门永拒。
            var trs = Matrix4x4.TRS(boxCenter + l * LightBoxCamDist,
                                    Quaternion.LookRotation(-l), Vector3.one);
            // Unity 相机看 -z,手算 view 必须补 z 翻转
            Matrix4x4 view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * trs.inverse;
            Matrix4x4 ortho = Matrix4x4.Ortho(-e, e, -e, e, 0.01f, LightBoxCamDist * 2f);

            return new LightSpaceFrame
            {
                View = view,
                VP = GL.GetGPUProjectionMatrix(ortho, true) * view,
                Box = new Vector4(e, 1f / HairShadowPass.LightRTSize, 0f, 0f),
            };
        }

        /// <summary>
        /// Ctrl+F6 边沿触发的光空间对齐诊断,按一次打一次。
        /// 逐角色打印头骨在光空间盒内的 uv/深度:判据 uv.x≈0.5、uv.y≈0.325
        /// (盒心=头骨+up×0.07)、headDepth≈CamDist=1.0。偏了=矩阵/头位问题。
        /// </summary>
        internal static void LogHairLightSpaceDiag()
        {
            if (_hairLight == null
                || (-_hairLight.transform.forward).sqrMagnitude < 1e-6f)
            {
                FaceSDFShadowPlugin.Log.LogWarning(
                    "[HairLS] no valid hair light; light-space diag unavailable");
                return;
            }
            var lightDir = -_hairLight.transform.forward;
            int idx = 0;
            foreach (var kvp in _registry)
            {
                var head = kvp.Value.HeadBone;
                if (head == null || head.Equals(null))
                {
                    FaceSDFShadowPlugin.Log.LogInfo(
                        $"[HairLS] chara#{idx++} head bone unresolved; no matrix");
                    continue;
                }
                var frame = BuildLightSpaceFrame(lightDir, head.position);
                Vector3 hv = frame.View.MultiplyPoint3x4(head.position);
                float e = LightBoxHalfExtent;
                FaceSDFShadowPlugin.Log.LogInfo(
                    $"[HairLS] chara#{idx++} headUV=({hv.x / e * 0.5f + 0.5f:F3}," +
                    $"{hv.y / e * 0.5f + 0.5f:F3}) headDepth={-hv.z:F3}");
            }
        }

        /// <summary>
        /// 投影用光向的仰角钳位 [10°,50°]:近水平时正交盒沿光向压扁(深度精度塌),
        /// 近竖直时方位角抖动被放大。只钳这一路,视觉光与形态 A 不受影响。
        /// </summary>
        private static Vector3 ClampLightElevation(Vector3 l)
        {
            float elev = Mathf.Asin(Mathf.Clamp(l.y, -1f, 1f)) * Mathf.Rad2Deg;
            float target = Mathf.Clamp(elev, 10f, 50f);
            if (Mathf.Abs(target - elev) < 0.01f) return l;

            var h = new Vector2(l.x, l.z);
            // 近竖直光方位退化,兜底取世界 +Z(只需一个确定方位,不求连续)
            h = h.sqrMagnitude > 1e-8f ? h.normalized : new Vector2(0f, 1f);
            float rad = target * Mathf.Deg2Rad;
            float c = Mathf.Cos(rad);
            return new Vector3(h.x * c, Mathf.Sin(rad), h.y * c);
        }

        private static void RescanHairLight()
        {
            Light best = null;
            float bestScore = -1f;
            foreach (var l in Object.FindObjectsOfType<Light>())
            {
                if (l == null || l.type != LightType.Directional || !l.enabled
                    || !l.gameObject.activeInHierarchy) continue;
                if (!IsUnderCamera(l.transform)) continue;
                float score = l.intensity * Mathf.Max(l.color.r, l.color.g, l.color.b);
                if (score > bestScore) { bestScore = score; best = l; }
            }
            // 锁光类插件会把角色光 reparent 出相机层级(如 MakerAdditions 的
            // lockCamlight):灯仍存活、方向正确,只是父链无 Camera。此时保留既有
            // 引用不清空——只有真死/禁用/非平行光才置空,避免锁光期间发影失效。
            if (best == null && _hairLight != null && _hairLight.type == LightType.Directional
                && _hairLight.enabled && _hairLight.gameObject.activeInHierarchy)
                return;
            if (best == null && !_hairLightWarned)
            {
                // 无灯=发影静默失效,无日志用户无从分辨"没建"还是"没灯"
                _hairLightWarned = true;
                FaceSDFShadowPlugin.Log.LogWarning(
                    "HairShadow: no directional light under a camera; hair shadow disabled.");
            }
            _hairLight = best;
        }

        /// <summary>
        /// 只认挂在相机层级下的平行光:EC 里角色打光恒在相机下(捏人
        /// CustomScene/CamBase/Camera/、其他场景 Camera/Main Camera/),锁光插件控的
        /// 也是它;MapLight/ 是地图环境光,不该驱动发影。
        /// 不回落"场景最亮"——ADV 场景两盏灯强度都可调,回落会在地图光调亮时跳灯。
        /// </summary>
        private static bool IsUnderCamera(Transform t)
        {
            for (var p = t.parent; p != null; p = p.parent)
                if (p.GetComponent<Camera>() != null) return true;
            return false;
        }

        private static Texture2D WhiteRamp
        {
            get
            {
                if (_whiteRamp == null)
                {
                    Texture2D ramp = null;
                    try
                    {
                        ramp = new Texture2D(4, 4, TextureFormat.RGB24, false)
                        {
                            name = "FaceSDF_WhiteRamp",
                            wrapMode = TextureWrapMode.Clamp,
                            filterMode = FilterMode.Bilinear,
                            hideFlags = HideFlags.HideAndDontSave,
                        };
                        var px = new Color32[16];
                        for (int i = 0; i < px.Length; i++)
                            px[i] = new Color32(255, 255, 255, 255);
                        ramp.SetPixels32(px);
                        ramp.Apply(false, false);
                        _whiteRamp = ramp;
                    }
                    catch
                    {
                        if (ramp != null) Object.Destroy(ramp);
                        throw;
                    }
                }
                return _whiteRamp;
            }
        }

        internal static void Poll()
        {
            CleanupDeadState();

            if (ShaderBundle.Overlay == null)
                return;

            if (!Manager.Character.IsInstance())
                return;

            var dict = Manager.Character.Instance.dictEntryChara;
            if (dict == null || dict.Count == 0)
                return;

            foreach (var cha in dict.Values)
            {
                if (cha == null) continue;

                var smr = cha.rendFace as SkinnedMeshRenderer;
                if (smr == null) continue;

                // 获取该角色的 Controller（保存/加载时提供初始配置）
                var controller = cha.GetComponent<FaceSDFShadowController>();

                if (_registry.TryGetValue(smr, out var entry))
                {
                    // 已注册：overlay 必须在材质数组里（ME 才能显示面板）。
                    // 换头/第三方改过材质数组 → 销毁重建。
                    if (!IsOverlayIntact(smr, entry))
                    {
                        RemoveOwnedOverlay(smr, entry);
                        DestroyOverlay(entry);
                        _registry.Remove(smr);
                        Apply(smr, cha, controller);
                        continue;
                    }

                    // 卡重载可能复用同一个 rendFace；只在 Controller 实例/版本变化时推一次，
                    // 避免轮询持续覆盖 MaterialEditor 的运行时调整。
                    if (controller != null &&
                        (entry.Controller != controller || entry.ControllerRevision != controller.Revision))
                    {
                        PushControllerConfig(entry.OverlayMat, controller, ref entry);
                        entry.Controller = controller;
                        entry.ControllerRevision = controller.Revision;
                        _registry[smr] = entry;
                    }

                    // 软开关：材质 _Enable 是运行时状态（ME 滑条写），
                    // 拉 0 = 软关闭（shader 输出白）+ 还原 ramp，拉 1 = 生效 + 中和 ramp。
                    // overlay 始终在数组里，因此 ME 面板始终可见、可再次修改。
                    bool materialEnable = controller != null &&
                        entry.OverlayMat.GetFloat(ShaderIDs.Enable) >= 0.5f;
                    // 全局总开关关闭时强制软关闭（overlay 仍保留在数组）
                    bool effectiveEnable = materialEnable && FaceSDFShadowPlugin.Enabled.Value;

                    SyncRamp(entry, effectiveEnable);
                    SyncFaceShadowG(entry, effectiveEnable);
                    SyncReceiveShadows(smr, effectiveEnable);
                    PushConfig(entry.OverlayMat);
                    // 02/04/05 全局跟随 + ME 定制锁存（与 PushConfigAll 同一入口，漏一条会半坏）
                    if (SyncPerMaterialParams(ref entry))
                        _registry[smr] = entry;

                    // Mesh 变化（换头）或逐角色 SDF 目录变化 → 重新烘焙 SDF
                    string sdfSourceDir = ResolvePerCharaSdfDir(cha);
                    if (entry.Mesh != smr.sharedMesh || entry.SdfSourceDir != sdfSourceDir)
                    {
                        bool meshChanged = entry.Mesh != smr.sharedMesh;
                        PushSDF(entry.OverlayMat, smr.sharedMesh, sdfSourceDir);
                        // 换头会换 mesh，bindpose 随之改变，头骨引用必须重解析
                        if (meshChanged)
                        {
                            entry.HeadBone = null;
                            entry.BindFix = Matrix4x4.identity;
                            if (ResolveHeadBone(smr, out var newBone, out var newFix))
                            {
                                entry.HeadBone = newBone;
                                entry.BindFix = newFix;
                            }
                            else
                            {
                                DisableHeadMatrix(entry.OverlayMat, smr);
                            }
                        }
                        entry.Mesh = smr.sharedMesh;
                        entry.SdfSourceDir = sdfSourceDir;
                        _registry[smr] = entry;
                    }
                }
                else
                {
                    // Controller 由 KKAPI 异步挂载；未就绪时不拿全局默认值抢先 Apply，
                    // 下一轮拿到卡数据后再建材质。
                    if (controller != null)
                        Apply(smr, cha, controller);
                }
            }
        }

        /// <summary>
        /// 叠加层是否还在末位。sharedMaterials getter 会分配数组，故先比长度。
        /// </summary>
        private static bool IsOverlayIntact(SkinnedMeshRenderer smr, Entry entry)
        {
            if (entry.OverlayMat == null) return false;

            var mats = smr.sharedMaterials;
            return mats.Length == entry.OrigMats.Length + 1
                && mats[mats.Length - 1] == entry.OverlayMat;
        }

        /// <summary>
        /// 本插件 overlay 的识别标准：shader 即 Overlay（含被 Unity 实例化的克隆体）。
        /// 化妆切换走 renderer.materials 实例化路径会克隆整个材质数组——克隆体引用
        /// 不同但渲染行为相同，按引用剔除会漏，克隆体与新 overlay 双重乘算=阴影
        /// 叠加变深。shader 是私有 bundle 加载的实例，引用比较即安全。
        /// </summary>
        private static bool IsOwnedOverlayMaterial(Material mat)
        {
            return mat != null && mat.shader == ShaderBundle.Overlay;
        }

        /// <summary>
        /// 第三方追加/重排材质时，只剔除本插件自己的旧 overlay（含克隆体），保留其它材质。
        /// 低频自愈路径允许分配一次新数组。
        /// </summary>
        private static void RemoveOwnedOverlay(SkinnedMeshRenderer smr, Entry entry)
        {
            RestoreRamp(entry);
            var mats = smr.sharedMaterials;
            int kept = 0;
            for (int i = 0; i < mats.Length; i++)
            {
                if (!IsOwnedOverlayMaterial(mats[i])) kept++;
            }
            if (kept == mats.Length) return;

            var stripped = new Material[kept];
            int dst = 0;
            for (int i = 0; i < mats.Length; i++)
            {
                if (IsOwnedOverlayMaterial(mats[i]))
                {
                    // entry.OverlayMat 由调用方 DestroyOverlay 统一销毁；
                    // 其余（克隆体）在此销毁，避免每次化妆切换泄漏一个 Material。
                    if (mats[i] != entry.OverlayMat)
                        Object.Destroy(mats[i]);
                    continue;
                }
                stripped[dst++] = mats[i];
            }
            smr.sharedMaterials = stripped;
        }

        private static void Apply(SkinnedMeshRenderer smr, ChaInfo chaInfo, FaceSDFShadowController controller)
        {
            var shader = ShaderBundle.Overlay;
            if (shader == null || controller == null) return;

            var origMats = smr.sharedMaterials;
            if (origMats == null || origMats.Length == 0) return;

            // 幂等防御：数组里残留本插件 overlay（克隆体/上次未清理）时先剥离再追加，
            // 否则会双重乘算。registry miss + 数组含克隆的场景（如卸载重装）会走到这里。
            origMats = StripOwnedOverlays(origMats);

            var overlay = new Material(shader) { name = "FaceSDFOverlay" };

            string sdfSourceDir = ResolvePerCharaSdfDir(chaInfo);

            var entry = new Entry
            {
                OrigMats = origMats,
                OverlayMat = overlay,
                Mesh = smr.sharedMesh,
                SdfSourceDir = sdfSourceDir,
                Controller = controller,
                ControllerRevision = controller.Revision,
            };

            PushControllerConfig(overlay, controller, ref entry);
            PushConfig(overlay);
            PushSDF(overlay, smr.sharedMesh, sdfSourceDir);

            var newMats = new Material[origMats.Length + 1];
            System.Array.Copy(origMats, newMats, origMats.Length);
            newMats[origMats.Length] = overlay;
            smr.sharedMaterials = newMats;

            if (ResolveHeadBone(smr, out var headBone, out var bindFix))
            {
                entry.HeadBone = headBone;
                entry.BindFix = bindFix;
            }
            else
            {
                // 找不到 → shader 回落 unity_WorldToObject（阴影不跟头转，但不崩）。
                // 把实际 bones 打出来：候选名猜错时能一次定位，免得反复试。
                DisableHeadMatrix(overlay, smr);
            }
            SyncRamp(entry, controller.Enable);
            SyncFaceShadowG(entry, controller.Enable);
            SyncReceiveShadows(smr, controller.Enable && FaceSDFShadowPlugin.Enabled.Value);
            _registry[smr] = entry;
        }

        /// <summary>
        /// 新建 overlay / 卡重载时写入四项卡配置。Enable 恒写卡值；三个形态属性按
        /// ME 定制锁存分流：已定制=写卡值（存卡读回的 ME 值），未定制=写全局 Config 值。
        /// 哨兵=本次实际写入值，供后续 SyncPerMaterialParams 检测外部修改。
        /// </summary>
        private static void PushControllerConfig(Material overlay, FaceSDFShadowController controller, ref Entry entry)
        {
            overlay.SetFloat(ShaderIDs.Enable, controller.Enable ? 1f : 0f);

            Color color = controller.CustomizedColor
                ? controller.ShadowColor
                : FaceSDFShadowPlugin.ShadowColor.Value;
            overlay.SetColor(ShaderIDs.ShadowColor, color);
            entry.PushedColor = color;

            float bias = controller.CustomizedBias
                ? controller.ThresholdBias
                : FaceSDFShadowPlugin.ThresholdBias.Value;
            overlay.SetFloat(ShaderIDs.ThresholdBias, bias);
            entry.PushedBias = bias;

            float softness = controller.CustomizedSoftness
                ? controller.SoftnessAngle
                : FaceSDFShadowPlugin.SoftnessAngle.Value;
            overlay.SetFloat(ShaderIDs.SoftnessAngle, softness);
            entry.PushedSoftness = softness;
        }

        /// <summary>
        /// 取色器写入阴影色：写材质（ME 运行时真源，面板同步可见）+ 立即置 CustomizedColor
        /// 并更新哨兵——取色器是显式定制动作，不等下一轮检测（否则窗口期内 PushConfigAll
        /// 会拿全局值覆盖掉刚写的色）。存卡靠 OnCardBeingSaved 从材质读回。
        /// 捏人场景单角色使用。
        /// </summary>
        internal static int ApplyShadowColorAll(Color shadowColor)
        {
            int applied = 0;
            // Entry 是 struct 且要写回哨兵：先快照键再改值，避免边遍历边写字典
            var smrs = new List<SkinnedMeshRenderer>(_registry.Keys);
            foreach (var smr in smrs)
            {
                var entry = _registry[smr];
                if (entry.OverlayMat == null) continue;
                entry.OverlayMat.SetColor(ShaderIDs.ShadowColor, shadowColor);
                entry.PushedColor = shadowColor;
                if (entry.Controller != null)
                    entry.Controller.CustomizedColor = true;
                _registry[smr] = entry;
                applied++;
            }
            return applied;
        }

        /// <summary>
        /// 解锁「全局跟随 + ME 定制锁存」（取色器按钮调用）：清三属性锁存标志，
        /// 材质直接写全局值并同步哨兵。不能复用 PushControllerConfig——它会把
        /// _Enable 也弹回卡值，冲掉 ME 滑条未保存的开关状态；也不能走
        /// SyncPerMaterialParams——材质当前值仍是 ME 定制值、≠哨兵，会被检测
        /// 分支当场重新锁存。捏人场景单角色使用。
        /// </summary>
        internal static int ResetCustomizationAll()
        {
            int reset = 0;
            var smrs = new List<SkinnedMeshRenderer>(_registry.Keys);
            foreach (var smr in smrs)
            {
                var entry = _registry[smr];
                if (entry.OverlayMat == null || entry.Controller == null) continue;

                entry.Controller.CustomizedColor = false;
                entry.Controller.CustomizedBias = false;
                entry.Controller.CustomizedSoftness = false;

                var mat = entry.OverlayMat;
                mat.SetColor(ShaderIDs.ShadowColor, FaceSDFShadowPlugin.ShadowColor.Value);
                entry.PushedColor = FaceSDFShadowPlugin.ShadowColor.Value;
                mat.SetFloat(ShaderIDs.ThresholdBias, FaceSDFShadowPlugin.ThresholdBias.Value);
                entry.PushedBias = FaceSDFShadowPlugin.ThresholdBias.Value;
                mat.SetFloat(ShaderIDs.SoftnessAngle, FaceSDFShadowPlugin.SoftnessAngle.Value);
                entry.PushedSoftness = FaceSDFShadowPlugin.SoftnessAngle.Value;

                _registry[smr] = entry;
                reset++;
            }
            return reset;
        }

        /// <summary>
        /// 剥离数组里所有本插件 overlay（含克隆体）。无残留时原样返回零分配。
        /// 残留材质是克隆体（非插件持有），剥离即无人引用，直接 Destroy 防泄漏。
        /// </summary>
        private static Material[] StripOwnedOverlays(Material[] mats)
        {
            int kept = 0;
            for (int i = 0; i < mats.Length; i++)
            {
                if (!IsOwnedOverlayMaterial(mats[i])) kept++;
            }
            if (kept == mats.Length) return mats;

            var stripped = new Material[kept];
            int dst = 0;
            for (int i = 0; i < mats.Length; i++)
            {
                if (IsOwnedOverlayMaterial(mats[i]))
                {
                    Object.Destroy(mats[i]);
                    continue;
                }
                stripped[dst++] = mats[i];
            }
            return stripped;
        }

        /// <summary>
        /// 解析逐角色手工 SDF 来源目录：见 <see cref="PerCharaSdfResolver"/>（EC_Profile 描述指令）。
        /// 返回 null 表示回退全局 SDF/ 目录。
        /// </summary>
        private static string ResolvePerCharaSdfDir(ChaInfo cha)
            => PerCharaSdfResolver.Resolve(cha);

        /// 面部骨架根。骨骼链：cf_j_neck → cf_j_head → cf_s_head → p_cf_head_bone
        /// → cf_J_N_FaceRoot → cf_J_FaceRoot → …
        ///
        /// 不能用 cf_j_head：它在**身体**骨架里，而 face SMR 的 bones 由
        /// `aaWeightsHead.CreateBoneList(objHeadBone, "")` 建立（ChaControl.cs:6814），
        /// 只收集 p_cf_head_bone 子树，cf_j_head 是其祖先、不在其中 —— 实测报
        /// "Head bone not found"。这里取子树内最靠根、刚性跟随头部的骨骼。
        /// 按顺序尝试，取第一个命中的（不同头部资产的层级可能有出入）。
        private static readonly string[] HeadBoneCandidates =
        {
            "cf_J_FaceRoot",
            "cf_J_N_FaceRoot",
            "cf_J_FaceBase",
        };

        /// <summary>
        /// 找到跟随头部旋转的骨骼，并算出「该骨骼 local → mesh 物体空间」的静态校正矩阵。
        ///
        /// 为什么需要校正：阈值图的 UV 模板（鼻翼/颊上标定坐标）是按 <b>mesh 物体空间</b> 定的，
        /// 而骨骼 local 的轴向未必与之一致。bindpose 正是「物体空间 → 骨骼 local」的绑定期变换，
        /// 取它的逆即得「骨骼 local → 物体空间」，与运行时 worldToLocal 相乘后，
        /// 光方向就落回模板标定所在的参照系，已有标定全部继续有效。
        ///
        /// 解析失败返回 false，调用方让 shader 回落 unity_WorldToObject（行为退回修复前，不崩）。
        /// </summary>
        private static bool ResolveHeadBone(SkinnedMeshRenderer smr, out Transform bone, out Matrix4x4 bindFix)
        {
            bone = null;
            bindFix = Matrix4x4.identity;

            var bones = smr.bones;
            var mesh = smr.sharedMesh;
            if (bones == null || mesh == null) return false;

            var bindposes = mesh.bindposes;
            for (int c = 0; c < HeadBoneCandidates.Length; c++)
            {
                for (int i = 0; i < bones.Length; i++)
                {
                    if (bones[i] == null || bones[i].name != HeadBoneCandidates[c]) continue;

                    // bindpose 校正不可省；第三方改出长度不一致时继续找其它候选，
                    // 全部失败则回落 unity_WorldToObject，不能沿用错误坐标系。
                    if (bindposes == null || i >= bindposes.Length) continue;
                    bone = bones[i];
                    bindFix = bindposes[i].inverse;
                    return true;
                }
            }
            return false;
        }

        private static void DisableHeadMatrix(Material overlay, SkinnedMeshRenderer smr)
        {
            overlay.SetFloat(ShaderIDs.UseHeadMatrix, 0f);
            FaceSDFShadowPlugin.Log.LogWarning(
                $"No head-following bone with a matching bindpose found in face SMR " +
                $"(tried: {string.Join(", ", HeadBoneCandidates)}); " +
                "face shadow will not follow head rotation. Actual bones: " +
                DescribeBones(smr));
        }

        /// <summary>
        /// 列出 SMR 的骨骼名，供候选名猜错时排查。骨骼可能上百根，截断到前 40 个。
        /// </summary>
        private static string DescribeBones(SkinnedMeshRenderer smr)
        {
            var bones = smr.bones;
            if (bones == null || bones.Length == 0) return "(none)";

            var sb = new System.Text.StringBuilder();
            int shown = System.Math.Min(bones.Length, 40);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(bones[i] == null ? "(null)" : bones[i].name);
            }
            if (bones.Length > shown)
                sb.Append(", … (").Append(bones.Length).Append(" total)");
            return sb.ToString();
        }

        /// <summary>
        /// 给叠加材质挂上该 mesh 的手绘阈值图。取不到（无素材 / mesh 无模板）时
        /// _UseSDFTex=0，shader 直接输出白 —— 整层不生效，脸回到原版。
        /// </summary>
        private static void PushSDF(Material mat, Mesh mesh, string sdfSourceDir = null)
        {
            if (mat == null) return;

            Texture sdf = FaceStructuredSDFBaker.GetOrBake(mesh, sdfSourceDir);
            mat.SetTexture(ShaderIDs.SDFTex, sdf);
            mat.SetFloat(ShaderIDs.UseSDFTex, sdf != null ? 1f : 0f);
        }

        /// <summary>
        /// 按配置把面部原始材质的 ramp 设为白图（中和）或还原为全局 ramp。
        /// 用材质级 SetTexture 创建 local override：只覆盖面部材质实例，身体/头发不受影响
        /// （_RampG 是全局属性，但 Material.SetTexture 会在该实例上建 local override）。
        /// enable=false 时还原全局 ramp（软关闭，overlay 仍在数组里输出白）。
        /// </summary>
        private static void SyncRamp(Entry entry, bool enable)
        {
            // 中和条件：全局 NeutralizeFaceRamp 开启 且 该角色 enable。
            // 否则还原全局 ramp（软关闭 / 用户关闭中和）。
            bool neutralize = FaceSDFShadowPlugin.NeutralizeFaceRamp.Value && enable;
            Texture target = neutralize ? (Texture)WhiteRamp : ChaShader.texRamp;

            // 全局 ramp 尚未初始化时不还原，免得写进 null 把脸弄黑
            if (!neutralize && target == null) return;

            var mats = entry.OrigMats;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null) continue;
                mats[i].SetTexture(ChaShader._RampG, target);
            }
        }

        /// <summary>
        /// 按配置排除/衰减面部实时自阴影：写 main_skin 实例的 _FaceShadowG（强度标量）。
        /// 0=原版实时阴影；1=自阴影（自身投影）消失，**头发在脸上的投影只稍微变淡、大体保留**；
        /// 中间值=部分衰减。机制与 _RampG 相同：SetFloat 静默建 local override，
        /// 只影响面部材质实例，身体/头发不受影响（实测证实）。
        /// 游戏 C# 从不回写该属性（ItemShader.cs:153/158 只声明 ID）。
        /// enable=false（软关闭/总开关关）时还原 0 = 完全回到原版。
        /// </summary>
        private static void SyncFaceShadowG(Entry entry, bool enable)
        {
            float g = enable ? FaceSDFShadowPlugin.FaceRealtimeShadowG.Value : 0f;

            var mats = entry.OrigMats;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null) continue;
                mats[i].SetFloat(ShaderIDs.FaceShadowG, g);
            }
        }

        /// <summary>
        /// 脖颈干净底色：跟随 General 00_Enabled 主开关（× 逐角色 _Enable 软关），
        /// 与 HairShadow 00_ShadowEnabled 无关——底色是颈带复制品正确工作的前提，
        /// 收窄后关发影开关不会连带还原底色。软关闭/卸载还原 receiveShadows=true。
        /// 幂等重写（Poll 自愈，换头重建后也覆盖）。注意与 ME 的 receiveShadows
        /// 归属：生效期间 ME 面板的同名开关会被每轮轮询压回。
        /// </summary>
        private static void SyncReceiveShadows(SkinnedMeshRenderer smr, bool enable)
        {
            // enable = 总开关 × 材质 _Enable（调用方传入），与发影权重判定解耦
            smr.receiveShadows = !enable;

            // FaceNoSelfCast：屏幕空间阴影图分不出投影来源，脸自投影回来是纯黑；
            // cast=Off 从源头切掉且外部投影保留。ShadowsOnly 会使 renderer 整个
            // 隐形（用户实测），禁止使用。
            if (enable && FaceSDFShadowPlugin.FaceNoSelfCast.Value)
                smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            else
                smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        }

        private static void PushConfig(Material mat)
        {
            if (mat == null) return;

            // ShadowColor / ThresholdBias / SoftnessAngle 不在这里推：
            // 走 SyncPerMaterialParams 的"全局跟随 + ME 定制锁存"哨兵机制。
            // UseSDFTex 由 PushSDF 按是否拿到阈值图设置，这里不碰

            // EC 面部材质的 renderQueue 是 2350，叠加层必须排在它之后才可见
            mat.renderQueue = FaceSDFShadowPlugin.RenderQueue.Value;

            // 端点钳位（全局形态参数，不进 ME 面板）：度数 → sideAngle 域（0..1 = 0..180°）
            mat.SetFloat(ShaderIDs.EndpointSnap,
                FaceSDFShadowPlugin.ManualEndpointSnapDegrees.Value / 180f);

            // 颈带 N·L 复制品标定（全局显示参数，不进 ME 面板）：纯显示 uniform，改值即生效
            mat.SetFloat(ShaderIDs.NeckRampScale, FaceSDFShadowPlugin.NeckRampScale.Value);
            mat.SetFloat(ShaderIDs.NeckRampBias, FaceSDFShadowPlugin.NeckRampBias.Value);
            mat.SetFloat(ShaderIDs.NeckEdgeSoftness, FaceSDFShadowPlugin.NeckEdgeSoftness.Value);
            mat.SetFloat(ShaderIDs.NeckBandTopV, FaceSDFShadowPlugin.NeckBandTopV.Value);
            mat.SetFloat(ShaderIDs.NeckReplicaCap, FaceSDFShadowPlugin.NeckReplicaCap.Value);
            mat.SetFloat(ShaderIDs.NeckShadowCompensation, FaceSDFShadowPlugin.NeckShadowCompensation.Value);

            // 阴影图可用性。自阴影关=奇数质量档 → QualitySettings.shadows=Disable，
            // 屏幕空间阴影图未绑定、不可采，shader 侧 attenEff 退化为 1（独立判光合成）。
            mat.SetFloat(ShaderIDs.ShadowmapAvail,
                QualitySettings.shadows == ShadowQuality.Disable ? 0f : 1f);

            // 发影(00)：权重 = 总开关 × 材质 _Enable × 00_ShadowEnabled。开 = 满权重 1，
            // 关 = 0。00 只管发影；干净底色(receiveShadows)由 SyncReceiveShadows
            // 按 总开关 × _Enable 独立判定。02~05 量程/偏移与灯向走全局
            // （HairShadowMask 顶点偏移用，PushHairLight）
            bool shadowEnabled = FaceSDFShadowPlugin.Enabled.Value
                && mat.GetFloat(ShaderIDs.Enable) >= 0.5f
                && FaceSDFShadowPlugin.ShadowEnabled.Value;
            mat.SetFloat(ShaderIDs.HairShadowWeight, shadowEnabled ? 1f : 0f);
            // 06 软边：形态 A 核间距 / LightSpace 沿用 _HairShadowBlur 名
            mat.SetFloat(ShaderIDs.HairShadowSoft, FaceSDFShadowPlugin.HairShadowSoft.Value);
        }

        /// <summary>
        /// 02/04/05 全局跟随 + ME 定制锁存的唯一检测/推送入口（Poll 已注册路径与
        /// PushConfigAll 都走这里，漏一条会出现"scene 生效、config 变更不生效"式半坏）：
        /// 材质当前值≠哨兵 → 被 ME/取色器改过 → 置 Controller.Customized* 一次性锁存
        /// （永不自动解锁）+ 哨兵←当前值，本轮不推全局；值相等且未定制 → 材质值≠全局值
        /// 才写全局，哨兵同步。回推只发生在"值==哨兵"时，不会覆盖 ME 刚写的值
        /// （ME 写完即不等，转入锁存分支）。
        /// 返回 entry 是否被修改（struct 拷贝，调用方需写回 _registry）。
        /// </summary>
        private static bool SyncPerMaterialParams(ref Entry entry)
        {
            var mat = entry.OverlayMat;
            if (mat == null) return false;

            // Unity 重载 !=：已销毁的 Controller 视同未挂载（此时按未定制处理）
            var controller = entry.Controller;

            bool changed = SyncColorParam(ref entry, mat, controller);
            changed |= SyncBiasParam(ref entry, mat, controller);
            changed |= SyncSoftnessParam(ref entry, mat, controller);
            return changed;
        }

        private static bool SyncColorParam(ref Entry entry, Material mat, FaceSDFShadowController controller)
        {
            Color current = mat.GetColor(ShaderIDs.ShadowColor);
            if (current != entry.PushedColor)
            {
                if (controller != null) controller.CustomizedColor = true;
                entry.PushedColor = current;
                return true;
            }
            if (controller != null && controller.CustomizedColor) return false;

            Color global = FaceSDFShadowPlugin.ShadowColor.Value;
            if (current == global) return false;
            mat.SetColor(ShaderIDs.ShadowColor, global);
            entry.PushedColor = global;
            return true;
        }

        private static bool SyncBiasParam(ref Entry entry, Material mat, FaceSDFShadowController controller)
        {
            float current = mat.GetFloat(ShaderIDs.ThresholdBias);
            if (current != entry.PushedBias)
            {
                if (controller != null) controller.CustomizedBias = true;
                entry.PushedBias = current;
                return true;
            }
            if (controller != null && controller.CustomizedBias) return false;

            float global = FaceSDFShadowPlugin.ThresholdBias.Value;
            if (current == global) return false;
            mat.SetFloat(ShaderIDs.ThresholdBias, global);
            entry.PushedBias = global;
            return true;
        }

        private static bool SyncSoftnessParam(ref Entry entry, Material mat, FaceSDFShadowController controller)
        {
            float current = mat.GetFloat(ShaderIDs.SoftnessAngle);
            if (current != entry.PushedSoftness)
            {
                if (controller != null) controller.CustomizedSoftness = true;
                entry.PushedSoftness = current;
                return true;
            }
            if (controller != null && controller.CustomizedSoftness) return false;

            float global = FaceSDFShadowPlugin.SoftnessAngle.Value;
            if (current == global) return false;
            mat.SetFloat(ShaderIDs.SoftnessAngle, global);
            entry.PushedSoftness = global;
            return true;
        }

        /// <summary>
        /// 配置变更时刷新所有已应用条目（无需重建材质）。
        /// </summary>
        internal static void PushConfigAll()
        {
            // Entry 是 struct 且哨兵可能被更新：先快照键再写回，避免边遍历边改字典值
            var smrs = new List<SkinnedMeshRenderer>(_registry.Keys);
            foreach (var smr in smrs)
            {
                var entry = _registry[smr];
                PushConfig(entry.OverlayMat);
                if (SyncPerMaterialParams(ref entry))
                    _registry[smr] = entry;

                // 必须带上 SdfSourceDir：漏传会让配置变更后的重推悄悄退回全局目录
                PushSDF(entry.OverlayMat, entry.Mesh, entry.SdfSourceDir);

                // 软关闭态还原 ramp，生效态中和；enable 从材质 _Enable 读
                bool materialEnable = entry.OverlayMat.GetFloat(ShaderIDs.Enable) >= 0.5f;
                bool effectiveEnable = materialEnable && FaceSDFShadowPlugin.Enabled.Value;
                SyncRamp(entry, effectiveEnable);
                SyncFaceShadowG(entry, effectiveEnable);
                SyncReceiveShadows(smr, effectiveEnable);
            }
        }

        private static void CleanupDeadEntries()
        {
            _deadBuffer.Clear();
            foreach (var kvp in _registry)
            {
                if (kvp.Key == null || kvp.Key.Equals(null))
                    _deadBuffer.Add(kvp.Key);
            }

            foreach (var dead in _deadBuffer)
            {
                var entry = _registry[dead];
                RestoreRamp(entry);
                DestroyOverlay(entry);
                _registry.Remove(dead);
            }
        }

        private static void CleanupDeadState()
        {
            CleanupDeadEntries();
            FaceBlendCompensation.CleanupDead();
            FaceLiveCompensation.CleanupDead();
        }

        private static void DestroyOverlay(Entry entry)
        {
            if (entry.OverlayMat != null)
                Object.Destroy(entry.OverlayMat);
        }

        /// <summary>
        /// 还原所有条目：材质数组写回、ramp 交还全局、销毁自建资源。
        /// </summary>
        internal static void RestoreAll()
        {
            foreach (var kvp in _registry)
            {
                var smr = kvp.Key;
                var entry = kvp.Value;

                RestoreRamp(entry);

                // 干净底色的 renderer 状态还原（与 overlay 是否完整无关，活着就还原）
                if (smr != null && !smr.Equals(null))
                {
                    smr.receiveShadows = true;
                    smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                }

                if (smr != null && !smr.Equals(null) && IsOverlayIntact(smr, entry))
                    smr.sharedMaterials = entry.OrigMats;

                DestroyOverlay(entry);
            }

            _registry.Clear();
            FaceBlendCompensation.Dispose();
            FaceLiveCompensation.Dispose();

            if (_whiteRamp != null)
            {
                Object.Destroy(_whiteRamp);
                _whiteRamp = null;
            }
        }

        /// <summary>
        /// 把 ramp 写回当前全局值，等价于"交还全局控制权"。
        /// </summary>
        private static void RestoreRamp(Entry entry)
        {
            var global = ChaShader.texRamp;

            // 材质级还原：把面部材质的 local override 交还给全局值
            // （SetTexture 创建 local override，还原时设回全局值即可）
            var mats = entry.OrigMats;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null) continue;
                if (global != null)
                    mats[i].SetTexture(ChaShader._RampG, global);
                // _FaceShadowG 还原 0（原版实时阴影）；游戏从不回写它，设 0 即等价原版
                mats[i].SetFloat(ShaderIDs.FaceShadowG, 0f);
            }
        }
    }

    /// <summary>
    /// shader 属性 ID 缓存，避免轮询里反复做字符串哈希。
    /// </summary>
    internal static class ShaderIDs
    {
        internal static readonly int ShadowColor = Shader.PropertyToID("_ShadowColor");
        internal static readonly int Enable = Shader.PropertyToID("_Enable");
        internal static readonly int SoftnessAngle = Shader.PropertyToID("_SoftnessAngle");
        internal static readonly int ThresholdBias = Shader.PropertyToID("_ThresholdBias");
        internal static readonly int EndpointSnap = Shader.PropertyToID("_EndpointSnap");
        // 颈带 N·L 复制品：光照项标定旋钮 + 形态/范围 uniform
        internal static readonly int NeckRampScale = Shader.PropertyToID("_NeckRampScale");
        internal static readonly int NeckRampBias = Shader.PropertyToID("_NeckRampBias");
        internal static readonly int NeckEdgeSoftness = Shader.PropertyToID("_NeckEdgeSoftness");
        internal static readonly int NeckBandTopV = Shader.PropertyToID("_NeckBandTopV");
        // 复制品压黑上限（默认 1 = 不设限）
        internal static readonly int NeckReplicaCap = Shader.PropertyToID("_NeckReplicaCap");
        // 真实阴影消费权重（默认 1 = 合成消费；0 = 旧式独立判光）
        internal static readonly int NeckShadowCompensation = Shader.PropertyToID("_NeckShadowCompensation");
        // 阴影图可用性（QualitySettings.shadows != Disable；0 时 shader 不采样）
        internal static readonly int ShadowmapAvail = Shader.PropertyToID("_ShadowmapAvail");
        // 发影权重(00,总开关×_Enable×00_ShadowEnabled 门控,PushConfig 推 overlay 消费端)
        internal static readonly int HairShadowWeight = Shader.PropertyToID("_HairShadowWeight");
        // 发影软边(06,0..1)——形态 A 核间距;形态 B 沿用 _HairShadowBlur 名
        internal static readonly int HairShadowSoft = Shader.PropertyToID("_HairShadowSoft");
        // 发影灯向全局(CB 绘制不走 ForwardBase,由 PushHairLight 每帧推)
        internal static readonly int HairLightDir = Shader.PropertyToID("_HairLightDir");
        internal static readonly int HairShadowRTGlobal = Shader.PropertyToID("_HairShadowRT");
        // 发影 X/Y 移动量程(02/03,米,光驱动百分比位移的满量程)
        internal static readonly int HairShadowShiftX = Shader.PropertyToID("_HairShadowShiftX");
        internal static readonly int HairShadowShiftY = Shader.PropertyToID("_HairShadowShiftY");
        // 发影初始 X/Y 偏移(04/05,米,不依赖光向的基础位移)
        internal static readonly int HairShadowBaseX = Shader.PropertyToID("_HairShadowBaseX");
        internal static readonly int HairShadowBaseY = Shader.PropertyToID("_HairShadowBaseY");
        // 发影形态(01,0=屏幕位移/1=光空间)、软边值源(06,uniform 名 _HairShadowBlur)
        // 与光空间深度门 bias(定死常量,无配置项)
        internal static readonly int HairShadowForm = Shader.PropertyToID("_HairShadowForm");
        internal static readonly int HairShadowBlur = Shader.PropertyToID("_HairShadowBlur");
        internal static readonly int HairShadowBias = Shader.PropertyToID("_HairShadowBias");
        // 光空间三件套:VP 只出光栅化位置(已过 GL.GetGPUProjectionMatrix),
        // View+Box 供渲染与采样两侧用同一公式算 UV/深度(见 HairShadowMask pass 1)
        internal static readonly int HairLightVP = Shader.PropertyToID("_HairLightVP");
        internal static readonly int HairLightView = Shader.PropertyToID("_HairLightView");
        internal static readonly int HairLightBox = Shader.PropertyToID("_HairLightBox");

        internal static readonly int UseSDFTex = Shader.PropertyToID("_UseSDFTex");
        internal static readonly int SDFTex = Shader.PropertyToID("_SDFTex");
        internal static readonly int HeadWorldToLocal = Shader.PropertyToID("_HeadWorldToLocal");
        internal static readonly int UseHeadMatrix = Shader.PropertyToID("_UseHeadMatrix");
        internal static readonly int FaceShadowG = Shader.PropertyToID("_FaceShadowG");
        // 表情 UV 补偿（分区 affine）：30 系数数组 + 早退开关
        internal static readonly int BlendCompAffine = Shader.PropertyToID("_BlendCompAffine");
        internal static readonly int BlendCompEnable = Shader.PropertyToID("_BlendCompEnable");
    }
}
