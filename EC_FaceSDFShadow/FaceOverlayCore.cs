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

        internal static int AppliedCount => _registry.Count;

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

                mat.SetMatrix(ShaderIDs.HeadWorldToLocal, entry.BindFix * head.worldToLocalMatrix);
                mat.SetFloat(ShaderIDs.UseHeadMatrix, 1f);
            }
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
        /// 按配置排除/衰减面部实时自阴影：写 main_skin 实例的 _FaceShadowG（强度标量，群主实测）。
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
        // 颈带 N·L 复制品（路线 1.1）：光照项标定旋钮 + 形态/范围 uniform
        internal static readonly int NeckRampScale = Shader.PropertyToID("_NeckRampScale");
        internal static readonly int NeckRampBias = Shader.PropertyToID("_NeckRampBias");
        internal static readonly int NeckEdgeSoftness = Shader.PropertyToID("_NeckEdgeSoftness");
        internal static readonly int NeckBandTopV = Shader.PropertyToID("_NeckBandTopV");
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
