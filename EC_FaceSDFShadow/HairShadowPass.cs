using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace EC_FaceSDFShadow
{
    /// <summary>
    /// 头发独占投影(发影):头发在屏幕域百分比位移(方向=光向屏幕投影反向,幅度=偏离
    /// 视轴角线性百分比,3/4 行程饱和;量程=02/03)后渲进与主相机同视角的屏幕分辨率 RT
    /// (R=预乘眼深,G=覆盖率);overlay 在像素自身位置单点采样+单侧深度门(拒背光假影)。
    ///
    /// 位移放几何侧而非采样侧:偏移采样有出屏截断/斜截两类结构墙,几何侧平移由视锥
    /// 裁剪天然连续。RT 的 ZWrite 塌缩 overlap+消费端 3×3 软核(06)+max 融合 =
    /// 柔边/单值/强度标定;直画乘算不可行:overdraw 连乘加深、多影形状叠加、
    /// 双环渐变在细发条上不可达。
    ///
    /// 光向:C# 每帧推 _HairLightDir(只认相机层级下的平行光,FaceOverlayCore.PushHairLight);
    /// 锁光(EC_LightingEnhance SyncCamera)转的是灯光本体,每帧重推自动跟随。
    ///
    /// 渲染:CB 挂单相机 BeforeForwardOpaque(每帧重画 RT)。场景里另有渲角色的
    /// 'Correct' 相机(画面校正链),实测其输出不进最终画面,挂它=无影——
    /// 只挂 Camera.main,兜底取非 Correct 的第一个 mask 命中相机。
    ///
    /// 已否决路线(禁止复活):
    /// - 头前正交 mask 相机:狗牙=光栅化 mask 采样率硬墙(留档分支 feat/hair-shadow-jfa)。
    /// - 偏移点采 RT:采样侧出屏/深度窗错配两类结构墙。
    /// - 直画乘算:无 overdraw 塌缩,加深/多影;双环软边不可达。
    ///
    /// 形态 01:0=上述屏幕位移;1=光空间正交深度(自建 shadow map,pass 1)——
    /// 影贴脸起伏、随光向转、与镜头解耦,矩阵与采样约定见 FaceOverlayCore.BuildLightSpaceFrame。
    /// </summary>
    internal static class HairShadowPass
    {
        // RT 不走全局绑定:_HairShadowRT 逐角色材质级绑定,由 FaceOverlayCore.PushHairLight
        // 每帧写到各角色 overlay 材质上(遮罩 pass 不采样 RT,不绑)。
        private static readonly int MainTexID = Shader.PropertyToID("_MainTex");
        private static readonly int CutoffID = Shader.PropertyToID("_Cutoff");
        // 是否走 alpha test:原版 EC 头发=0(实心渲几何),MOD 卡片式=1(见 BuildDraws)
        private static readonly int UseAlphaID = Shader.PropertyToID("_UseAlphaTest");

        private static CommandBuffer _cb;

        // 逐角色 RT(键=该角色的 rendFace SMR,与 FaceOverlayCore._registry 同键):
        // 多角色各自一张,消费端 overlay 材质按同一键配对绑定。单角色时退化为一格。
        private static readonly Dictionary<SkinnedMeshRenderer, RenderTexture> _rts =
            new Dictionary<SkinnedMeshRenderer, RenderTexture>();

        // 形态 B 的光空间 RT 边长(07)。0.4m 盒下 2048² ≈0.2mm/texel,特写
        // 时约合 1 个屏幕像素;1024² 是 2 个,4-tap PCF 后仍留可见台阶。
        internal static int LightRTSize => FaceSDFShadowPlugin.HairShadowRes.Value;

        /// <summary>形态号(01 枚举的数值,推 shader 的 _HairShadowForm 同值)。</summary>
        private static int Form => (int)FaceSDFShadowPlugin.HairShadowForm.Value;

        // 每个待画单元(renderer+submesh)一份遮罩材质。用结构体绑在一起,
        // 而不用两个下标对齐的 List——下标对齐在材质缺失时后续全体错位。
        private static readonly List<MaskDraw> _draws = new List<MaskDraw>();
        private static readonly Dictionary<Camera, CommandBuffer> _bound =
            new Dictionary<Camera, CommandBuffer>();

        private static string _lastSignature;
        // Rebuild 失败(建不出可画单元)的签名。没有它,Poll 会每 15 帧重试同一份
        // 必然失败的输入并刷同样的警告——签名相同就不该再试第二次。
        private static string _failedSignature;

        internal static bool Active =>
            FaceSDFShadowPlugin.Enabled.Value &&
            FaceSDFShadowPlugin.ShadowEnabled.Value;

        /// <summary>
        /// 一个 DrawRenderer 单元:renderer + submesh 序号 + 该 submesh 的遮罩材质。
        /// 多材质头发(本体+挑染+饰件)每个 submesh 各有自己的发丝纹理,只画 submesh 0
        /// 会静默丢形状。Owner=该角色 rendFace SMR,RT 与矩阵按它配对(核心不变量)。
        /// </summary>
        private struct MaskDraw
        {
            internal Renderer Rend;
            internal int Submesh;
            internal Material Mat;
            internal SkinnedMeshRenderer Owner;
        }

        /// <summary>一个角色的待画单元集合与归属键。渲染/采样两侧以 Face 同键配对。</summary>
        private struct CharaGroup
        {
            internal SkinnedMeshRenderer Face;
            internal List<Renderer> Rend;
        }

        /// <summary>
        /// 低频轮询(插件 Update 15 帧节拍):签名变了才重建 CB/RT;不活跃幂等清场。
        /// CB 挂相机后每帧由渲染管线自动执行,无逐帧驱动入口。
        /// </summary>
        internal static void Poll()
        {
            if (!Active)
            {
                Dispose();
                return;
            }

            var groups = CollectHairRenderers();
            if (groups.Count == 0)
            {
                Dispose();
                return;
            }

            string signature = BuildSignature(groups);
            if (signature == _lastSignature && _cb != null) return;
            if (signature == _failedSignature) return;
            _lastSignature = signature;

            if (!Rebuild(groups))
                _failedSignature = signature;
        }

        /// <summary>
        /// 收集参与发影的头发 renderer,按角色分组。本体头发只取 cha.cusHairCmp[].rendHair[]
        /// ——构造上就是头发,不按名字/shader 名二次过滤(命名过滤会误杀不含 hair 字样的
        /// MOD 头发,且静默无日志);另收饰品槽里挂 ME 标记 shader 的 renderer(见
        /// CollectMarkedAccessories)。归属键=该角色的 rendFace SMR(消费端 _registry 同键)。
        ///
        /// 固定仅前发。机制理由:后发一旦入镜,它的偏移
        /// 覆盖就落在脸上,后发的影子会糊到脸侧/下颌——脸前发影的语义是"额前遮挡"。
        /// 该过滤只管本体头发 kind,被标记的饰品不受过滤(标记即玩家意志)。
        /// rendFace 解析不到的角色整体跳过:没有配对键,画了也没人消费。
        /// </summary>
        private static List<CharaGroup> CollectHairRenderers()
        {
            var result = new List<CharaGroup>();
            if (!Manager.Character.IsInstance()) return result;

            var dict = Manager.Character.Instance.dictEntryChara;
            if (dict == null) return result;

            foreach (var cha in dict.Values)
            {
                if (cha == null || cha.cusHairCmp == null) continue;
                var face = cha.rendFace as SkinnedMeshRenderer;
                if (face == null) continue;

                var group = new CharaGroup { Face = face, Rend = new List<Renderer>() };
                // cusHairCmp 按 HairKind 序号索引(back/front/side/option)
                for (int kind = 0; kind < cha.cusHairCmp.Length; kind++)
                {
                    // 固定仅前发(语义见方法注释)
                    if ((ChaFileDefine.HairKind)kind != ChaFileDefine.HairKind.front) continue;
                    var cmp = cha.cusHairCmp[kind];
                    if (cmp == null || cmp.rendHair == null) continue;
                    foreach (var r in cmp.rendHair)
                    {
                        if (r == null || !r.enabled || !r.gameObject.activeInHierarchy)
                            continue;
                        group.Rend.Add(r);
                    }
                }
                // 饰品头发受影:ME 标记 shader 的饰品 renderer 与本体头发同机制入组
                CollectMarkedAccessories(cha, group);
                if (group.Rend.Count > 0) result.Add(group);
            }
            return result;
        }

        // 误标警告按 renderer 去重:轮询每 15 帧重扫,不去重会刷屏
        private static readonly HashSet<int> _markerMisuseWarned = new HashSet<int>();

        /// <summary>
        /// 扫饰品槽 cusAcsCmp:任一材质挂标记 shader 的 renderer 入组(去重),启用
        /// 门槛与本体头发一致。标记出现/消失即 group.Rend 变化 → 签名变化 → 自动重建。
        /// </summary>
        private static void CollectMarkedAccessories(ChaControl cha, CharaGroup group)
        {
            if (!MaterialEditorMarker.Injected || cha.cusAcsCmp == null) return;
            for (int slot = 0; slot < cha.cusAcsCmp.Length; slot++)
            {
                var cmp = cha.cusAcsCmp[slot];
                if (cmp == null) continue;
                foreach (var r in cmp.gameObject.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                    if (HasMarkerShader(r) && !group.Rend.Contains(r))
                        group.Rend.Add(r);
                }
            }
        }

        /// <summary>
        /// 该 renderer 是否挂了标记 shader。误标原材质(名字不带 .MECopy)会把饰品
        /// 本体几何隐藏成哑渲染,警告一次仍登记(shader 语义优先)。
        /// </summary>
        private static bool HasMarkerShader(Renderer r)
        {
            var mats = r.sharedMaterials;
            if (mats == null) return false;

            bool marked = false;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null || m.shader == null ||
                    m.shader.name != MaterialEditorMarker.MarkerShaderName) continue;
                marked = true;
                if (!m.name.Contains(".MECopy") && _markerMisuseWarned.Add(r.GetInstanceID()))
                    FaceSDFShadowPlugin.Log.LogWarning(
                        "[HairShadowMarker] '" + r.name + "' has the marker shader on a " +
                        "non-copy material (no .MECopy suffix) - the accessory will render " +
                        "invisible. Copy the material in MaterialEditor first, then change " +
                        "the copy's shader. Registered anyway.");
            }
            return marked;
        }

        private static string BuildSignature(List<CharaGroup> groups)
        {
            // 01 换 RT 规格与绘制 pass、06 跨 0 换 RT 倍率,都必须进签名;
            // 06 的核间距是纯 uniform 不重建
            var sb = new StringBuilder(256);
            sb.Append("form").Append(Form);
            // 07 只在光空间形态下决定 RT 边长,屏幕形态改它不该触发重建
            if (Form > 0) sb.Append("res").Append(LightRTSize);
            sb.Append("|ss").Append(FaceSDFShadowPlugin.HairShadowSoft.Value > 0f ? 2 : 1)
              .Append('|').Append(Screen.width).Append('x').Append(Screen.height)
              .Append("|g:");
            foreach (var g in groups)
            {
                sb.Append(g.Face.GetInstanceID()).Append(':');
                foreach (var r in g.Rend)
                    sb.Append(r.GetInstanceID()).Append(',');
                sb.Append(';');
            }
            sb.Append("|c:");
            foreach (var cam in Camera.allCameras)
                sb.Append(cam.GetInstanceID()).Append(',');
            return sb.ToString();
        }

        /// <summary>
        /// 重建 CB 与遮罩材质;返回 false=建不出可画单元,调用方记失败签名防重试。
        /// 逐角色:先对账 RT 字典(角色离开即释放),再为每个角色建 draws 与 RT,
        /// 单 CB 内按角色分段 SetRenderTarget→Clear→Draw——RT 互不污染。
        /// </summary>
        private static bool Rebuild(List<CharaGroup> groups)
        {
            UnbindAll();
            DestroyDraws();
            PruneRenderTargets(groups);

            var maskShader = ShaderBundle.HairMask;
            if (maskShader == null)
            {
                FaceSDFShadowPlugin.Log.LogWarning(
                    "HairShadowMask shader missing from bundle; hair shadow disabled.");
                return false;
            }

            for (int gi = 0; gi < groups.Count; gi++)
            {
                var g = groups[gi];
                EnsureRenderTarget(g.Face);
                for (int i = 0; i < g.Rend.Count; i++)
                    BuildDraws(maskShader, g.Rend[i], g.Face, _draws);
            }
            // 新 mask 材质的矩阵不必在此写:Update(此处)→onPreCull(逐角色推矩阵)
            // →CB 执行,同帧即被覆盖

            if (_draws.Count == 0)
            {
                FaceSDFShadowPlugin.Log.LogWarning(
                    "HairShadow: no drawable hair submesh (no material on any hair " +
                    "renderer); disabled. Hair material dump (Ctrl+F9 in DEBUG) " +
                    "lists what these renderers actually carry.");
                return false;
            }

            var cb = new CommandBuffer { name = "FaceSDF.HairMask" };
            // pass 必须显式指定:shader 现有两个 pass(0=屏幕位移/1=光空间),
            // 默认 -1 会两个都画,后者覆盖前者。
            int pass = Form > 0 ? 1 : 0;
            int drawn = 0;
            foreach (var g in groups)
            {
                var rt = _rts.TryGetValue(g.Face, out var r) ? r : null;
                if (rt == null) continue;
                cb.SetRenderTarget(rt);
                // 清屏 (0,0):G=覆盖率归零即"无发"。深度走预乘编码,不存在"背景深度"
                // 这个量——旧屏幕空间版"清屏深度 0=无限近,边缘插值虚假过门成描边"
                // 的坑在 G≈0 短路下结构性不存在。
                cb.ClearRenderTarget(true, true, Color.clear);
                for (int i = 0; i < _draws.Count; i++)
                {
                    var d = _draws[i];
                    if (d.Owner != g.Face || d.Rend == null || d.Mat == null) continue;
                    cb.DrawRenderer(d.Rend, d.Mat, d.Submesh, pass);
                    drawn++;
                }
            }
            // 恢复相机目标:本 buffer 在 BeforeForwardOpaque 执行,不还原会吞掉正常渲染
            cb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            _cb = cb;

            // 只挂单相机:场景里另有渲角色的 'Correct' 相机,实测其输出不进最终
            // 画面(挂它=无影);RT 只需主相机视角,单相机重画最稳
            var layers = new HashSet<int>();
            foreach (var g in groups)
                foreach (var r in g.Rend)
                    if (r != null) layers.Add(r.gameObject.layer);

            bool MaskHit(Camera cam)
            {
                foreach (var l in layers)
                    if ((cam.cullingMask & (1 << l)) != 0) return true;
                return false;
            }

            Camera primary = null;
            var mainCam = Camera.main;
            if (mainCam != null && MaskHit(mainCam)) primary = mainCam;
            if (primary == null)
            {
                foreach (var cam in Camera.allCameras)
                {
                    if (cam == null || cam.name == "Correct" || !MaskHit(cam)) continue;
                    primary = cam;
                    break;
                }
            }

            int bound = 0;
            if (primary != null)
            {
                primary.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, cb);
                _bound[primary] = cb;
                bound = 1;
            }

            // Info 级:这行是"发影建起来没有"的唯一凭据;hairQueue 验证头发晚于
            // overlay 2360 渲染的实证
            int hairQueue = -1;
            foreach (var d in _draws)
            {
                var om = d.Rend?.sharedMaterials;
                if (om != null && om.Length > 0 && om[0] != null)
                { hairQueue = om[0].renderQueue; break; }
            }
            FaceSDFShadowPlugin.Log.LogInfo(
                $"HairShadowPass rebuilt (form={Form} " +
                $"{(Form > 0 ? "light-space ortho" : "screen-shift")}, " +
                $"RT per-chara x{_rts.Count}, pass={pass}): {drawn} draws " +
                $"from {groups.Count} chara(e), " +
                $"hairQueue={hairQueue}, camera='{(primary != null ? primary.name : "none")}' " +
                $"(bound={bound})");
            return true;
        }

        /// <summary>
        /// 角色离开时释放其 RT:键不在当前组集合内即对账清理,防泄漏。
        /// </summary>
        private static void PruneRenderTargets(List<CharaGroup> groups)
        {
            if (_rts.Count == 0) return;
            var alive = new HashSet<SkinnedMeshRenderer>();
            foreach (var g in groups) alive.Add(g.Face);
            _deadRts.Clear();
            foreach (var kvp in _rts)
            {
                var smr = kvp.Key;
                if (smr != null && alive.Contains(smr)) continue;
                kvp.Value.Release();
                Object.Destroy(kvp.Value);
                _deadRts.Add(smr);
            }
            foreach (var smr in _deadRts) _rts.Remove(smr);
        }

        private static readonly List<SkinnedMeshRenderer> _deadRts =
            new List<SkinnedMeshRenderer>();

        /// <summary>供消费端(FaceOverlayCore)按角色取 RT 配对绑定。</summary>
        internal static bool TryGetRT(SkinnedMeshRenderer face, out RenderTexture rt)
        {
            rt = null;
            return face != null && _rts.TryGetValue(face, out rt) && rt != null;
        }

        // 遮罩 pass 1 的矩阵 uniform ID(多角色逐材质写,不走全局)
        private static readonly int LightViewID = Shader.PropertyToID("_HairLightView");
        private static readonly int LightVPID = Shader.PropertyToID("_HairLightVP");
        private static readonly int LightBoxID = Shader.PropertyToID("_HairLightBox");

        /// <summary>
        /// 把一个角色的光空间矩阵刷进**该角色**的所有遮罩材质(形态 B)。
        /// 每帧由 FaceOverlayCore.PushHairLight 按角色调用——矩阵随灯/头每帧变,
        /// 且各角色矩阵不同(统一刷最后角色帧值=其余角色 RT 全黑无影)。
        /// 渲染/采样两侧同角色同帧同值=核心不变量。
        /// </summary>
        internal static void PushMaskMatrices(SkinnedMeshRenderer owner,
            FaceOverlayCore.LightSpaceFrame frame)
        {
            for (int i = 0; i < _draws.Count; i++)
            {
                if (_draws[i].Owner != owner) continue;
                var mat = _draws[i].Mat;
                if (mat == null) continue;
                mat.SetMatrix(LightVPID, frame.VP);
                mat.SetMatrix(LightViewID, frame.View);
                mat.SetVector(LightBoxID, frame.Box);
            }
        }

        /// <summary>
        /// 取(或建)一个角色的 mask RT。规格规则:LightSpace=方形光空间盒,边长取 07
        /// (与镜头分辨率无关);ScreenSpace=06 0=锐边(全分辨率 RT 单点采样)/
        /// >0=软边(2× 超采样 RT + 消费端 3×3 软核)。纯 2× 超采样实测只把狗牙磨成
        /// AA 级锐边,肉眼难辨——可见柔边由软核给出,RT 的 2× 保证核内输入亚像素干净。
        /// 预乘编码下超采样/软核双线性安全(清屏(0,0)平均仍无发)。
        /// </summary>
        private static RenderTexture EnsureRenderTarget(SkinnedMeshRenderer face)
        {
            int w, h;
            if (Form > 0)
            {
                w = h = LightRTSize;
            }
            else
            {
                int ss = FaceSDFShadowPlugin.HairShadowSoft.Value > 0f ? 2 : 1;
                w = Screen.width * ss;
                h = Screen.height * ss;
            }
            // 形态 A 靠双线性做软化;形态 B 必须 Point(见下方新建处注释)。
            // 尺寸相同但形态变了也要重建,否则沿用上一形态的过滤模式。
            var want = Form > 0 ? FilterMode.Point : FilterMode.Bilinear;

            if (_rts.TryGetValue(face, out var rt) && rt != null)
            {
                if (rt.width != w || rt.height != h || rt.filterMode != want)
                {
                    _rts.Remove(face);
                    rt.Release();
                    Object.Destroy(rt);
                }
                else
                {
                    return rt;
                }
            }

            // RGHalf 两通道恰好装下 R=预乘覆盖率深度(形态 A 眼深/B 光空间深度)、
            // G=覆盖率。half 防深度量化在比较边缘闪烁。
            // 坑:RHalf 作渲染目标在本链路不工作(连 DrawRenderer 都不出内容),
            // 单通道也必须用 RGHalf。
            // 过滤:形态 A 靠双线性做软化;形态 B 必须 Point——消费端手写
            // 4-tap PCF 要"先比较后插值",硬件双线性会先把深度混掉。
            var created = new RenderTexture(w, h, 24, RenderTextureFormat.RGHalf)
            {
                name = "FaceSDF_HairMaskRT",
                filterMode = want,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            created.Create();
            _rts[face] = created;
            return created;
        }

        /// <summary>
        /// 为一个 renderer 的每个 submesh 各建一份遮罩材质,追加进 draws。
        /// owner=该角色的 rendFace SMR,RT/矩阵按它配对。
        ///
        /// submesh 逐个建:Unity 用 sharedMaterials[i] 画 submesh i,多材质头发
        /// (本体/挑染/饰件)只画 submesh 0 会静默丢形状。
        ///
        /// 原版 EC 头发没有可用的发丝 alpha——形状 100% 由几何(多边形发片)构成:
        /// main_hair 系 RenderType=Opaque、无 _Cutoff;发图 alpha 恒 255;材质
        /// _MainTex 恒 null(HasProperty 为 true、GetTexture 为 null,Set/Get 不对称)。
        /// MOD 卡片式头发(真挂了 _MainTex)才走 alpha test——那种情况不裁剪
        /// 反而会渲成矩形。
        /// </summary>
        private static void BuildDraws(Shader maskShader, Renderer r,
            SkinnedMeshRenderer owner, List<MaskDraw> draws)
        {
            if (r == null) return;
            var mats = r.sharedMaterials;
            if (mats == null || mats.Length == 0) return;

            for (int sub = 0; sub < mats.Length; sub++)
            {
                var orig = mats[sub];
                if (orig == null) continue;

                Texture mainTex = null;
                try
                {
                    if (orig.HasProperty(MainTexID)) mainTex = orig.GetTexture(MainTexID);
                }
                catch (System.Exception ex)
                {
                    FaceSDFShadowPlugin.Log.LogWarning(
                        $"HairMask read props failed for '{r.name}' sub{sub}: {ex.Message}");
                }

                var mask = new Material(maskShader) { name = $"HairMask_{r.name}_{sub}" };
                if (mainTex != null)
                {
                    // MOD 卡片式头发:有真贴图才走 alpha test(阈值取原材质的,没有就 0.5)
                    mask.SetTexture(MainTexID, mainTex);
                    mask.SetFloat(CutoffID,
                        orig.HasProperty(CutoffID) ? orig.GetFloat(CutoffID) : 0.5f);
                    mask.SetFloat(UseAlphaID, 1f);
                }
                else
                {
                    // 原版 EC 头发:实心渲几何,不做任何裁剪
                    mask.SetFloat(UseAlphaID, 0f);
                }
                draws.Add(new MaskDraw { Rend = r, Submesh = sub, Mat = mask, Owner = owner });
            }
        }

        private static void UnbindAll()
        {
            foreach (var kvp in _bound)
            {
                var cam = kvp.Key;
                if (cam == null || cam.Equals(null)) continue;
                cam.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque, kvp.Value);
            }
            _bound.Clear();

            if (_cb != null)
            {
                _cb.Dispose();
                _cb = null;
            }
        }

        private static void DestroyDraws()
        {
            for (int i = 0; i < _draws.Count; i++)
            {
                if (_draws[i].Mat != null) Object.Destroy(_draws[i].Mat);
            }
            _draws.Clear();
        }

        /// <summary>
        /// 全量清场:解绑命令缓冲、销毁遮罩材质、释放全部角色 RT。幂等。
        /// </summary>
        internal static void Dispose()
        {
            if (_bound.Count == 0 && _draws.Count == 0 && _rts.Count == 0 && _lastSignature == null)
                return;

            UnbindAll();
            DestroyDraws();

            foreach (var kvp in _rts)
            {
                kvp.Value.Release();
                Object.Destroy(kvp.Value);
            }
            _rts.Clear();
            _lastSignature = null;
            _failedSignature = null;
        }

        /// <summary>
        /// [HairMaskDiag] DEBUG:把当前 mask RT 存成 PNG(直接看遮罩对不对)。
        /// 用 ReadPixels 读回。路径 DataDir。多角色时逐张导出(_0/_1 序号)。
        /// </summary>
        internal static void DebugExportMask()
        {
            if (_rts.Count == 0)
            {
                FaceSDFShadowPlugin.Log.LogWarning("[HairMaskDiag] no mask RT to export");
                return;
            }
            try
            {
                int idx = 0;
                foreach (var kvp in _rts)
                    DumpRT(kvp.Value, $"hairmask_diag_{idx++}.png");
            }
            catch (System.Exception ex)
            {
                FaceSDFShadowPlugin.Log.LogWarning("[HairMaskDiag] export failed: " + ex.Message);
            }
        }

        private static void DumpRT(RenderTexture rt, string fileName)
        {
            var old = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = old;
            string path = System.IO.Path.Combine(FaceSDFShadowPlugin.DataDir, fileName);
            System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.Destroy(tex);
            FaceSDFShadowPlugin.Log.LogInfo("[HairMaskDiag] wrote " + path);
        }

#if DEBUG
        /// <summary>[HairRTView] 全屏回显开关:看 RT 里到底有没有头发轮廓、位置对不对。</summary>
        internal static bool DiagShowRT;

        /// <summary>
        /// 只能走 IMGUI:CB 内 Blit 在本链路失效、ReadPixels 读回实测不可靠。
        /// GUI 坐标 y 向下,画面相对 RT 上下相反,别拿它判翻转;ScaleToFit 保方形不变形,
        /// 拉伸会让"发顶是否顶出盒"看不准。R=米制深度(>1 溢出成白),有发白/黄、无发黑。
        /// 多角色:第一张全屏,其余等宽并列在下方(够看位置即可)。
        /// </summary>
        internal static void DrawDiagOverlay()
        {
            if (!DiagShowRT || _rts.Count == 0) return;
            int i = 0;
            foreach (var kvp in _rts)
            {
                if (i == 0)
                {
                    GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height),
                                    kvp.Value, ScaleMode.ScaleToFit, false);
                }
                else
                {
                    float w = Screen.width / (float)_rts.Count;
                    GUI.DrawTexture(new Rect(w * i, Screen.height * 0.75f, w, Screen.height * 0.25f),
                                    kvp.Value, ScaleMode.ScaleToFit, false);
                }
                i++;
            }
        }

        /// <summary>
        /// [HairMatDump] DEBUG:枚举头发材质**实际拥有**的贴图/浮点属性。
        /// 回答"发丝 alpha 存在哪个属性"——原版头发材质没有 _MainTex,按它假设
        /// 会让遮罩退化(见 BuildDraws 注释)。候选表覆盖 ChaShader 定义的贴图属性
        /// + 常见 cutout 命名;HasProperty 为真才打印,免刷屏。
        /// </summary>
        internal static void DebugDumpHairMaterials()
        {
            var groups = CollectHairRenderers();
            if (groups.Count == 0)
            {
                FaceSDFShadowPlugin.Log.LogWarning("[HairMatDump] no hair renderer found");
                return;
            }
            var renderers = new List<Renderer>();
            foreach (var g in groups) renderers.AddRange(g.Rend);

            string[] texCandidates = {
                "_MainTex", "_AlphaMask", "_ColorMask", "_LineMask", "_DetailMask",
                "_NormalMap", "_NormalMapDetail", "_HairGloss",
                "_Texture2", "_Texture3", "_Texture4", "_Texture5",
                "_overtex1", "_overtex2", "_overtex3",
                "_alpha_a", "_alpha_b",
                "_BaseMap", "_Albedo", "_AlphaTex", "_Alpha",
            };
            string[] floatCandidates = {
                "_Cutoff", "_alpha_a", "_alpha_b", "_Alpha", "_AlphaThreshold",
                "_Clip", "_ClipThreshold", "_notusetexspecular",
            };

            // 只 dump 前两个 renderer:属性布局同型,多打无意义
            int dumped = 0;
            foreach (var r in renderers)
            {
                if (r == null) continue;
                var mats = r.sharedMaterials;
                if (mats == null) continue;

                for (int s = 0; s < mats.Length; s++)
                {
                    var m = mats[s];
                    if (m == null) continue;
                    FaceSDFShadowPlugin.Log.LogInfo(
                        $"[HairMatDump] '{r.name}' sub{s} mat='{m.name}' shader='{m.shader?.name}' " +
                        $"renderQueue={m.renderQueue}");

                    foreach (var p in texCandidates)
                    {
                        if (!m.HasProperty(p)) continue;
                        var t = m.GetTexture(p);
                        FaceSDFShadowPlugin.Log.LogInfo(
                            $"[HairMatDump]     TEX {p} = " +
                            (t == null ? "null" : $"'{t.name}' {t.width}x{t.height} ({t.GetType().Name})"));
                    }
                    foreach (var p in floatCandidates)
                    {
                        if (!m.HasProperty(p)) continue;
                        FaceSDFShadowPlugin.Log.LogInfo(
                            $"[HairMatDump]     FLT {p} = {m.GetFloat(p):F3}");
                    }
                    // shader keyword 能反映是否走 alpha test 路径
                    var kw = m.shaderKeywords;
                    if (kw != null && kw.Length > 0)
                        FaceSDFShadowPlugin.Log.LogInfo(
                            "[HairMatDump]     keywords: " + string.Join(", ", kw));
                }

                if (++dumped >= 2) break;
            }
        }
#endif
    }
}
