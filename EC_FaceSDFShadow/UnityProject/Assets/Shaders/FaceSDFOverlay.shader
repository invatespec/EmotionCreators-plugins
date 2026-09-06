// 面部 SDF 硬边阴影叠加层。
// 乘算叠加在 main_skin 之上，只压暗不改亮部，因此不需要复刻 EC 皮肤着色的任何逻辑。
//
// 阈值来源唯一：手绘角度帧烘出的 RGBAHalf 受光窗口图。每个像素存一个受光窗口
// [lo, hi]：sideAngle ∈ [lo,hi] 内受光(无阴影)，窗口外为阴影。通道 R/B = 右光 hi/lo，
// G/A = 左光 hi/lo。这样能表达「下颚带侧光亮、正面/背面都暗」的暗→亮→暗时序。
// 没有阈值图时整层直接输出白（不生效）。
//
// 两条分支：① 面部主体 = 上面的 SDF 受光窗口；② 颈带 = N·L 复制品
// crossfade——世界法线·世界光向过软硬可调的 smoothstep 出阴影量，再用与 SDF 分支
// 完全同款的 _ShadowColor 混色。缝两侧颜色语义一致（ramp 暗端纯黑会把复制品压成
// 全黑，故不查任何 ramp 纹理）。
//
// 约束：SDF 窗口分支是 yaw-only。光的俯仰分量被丢弃，正上/正下打光时退化为无阴影
// ——这是 SDF 面部阴影的固有特性（赛马娘/原神同样如此），非缺陷；pitch 维度的
// 响应由颈带复制品分支补上（该分支消费完整世界光向）。
Shader "Rainbowing/FaceSDFOverlay"
{
    Properties
    {
        _SDFTex ("SDF 阈值图", 2D) = "black" {}
        _ShadowColor ("阴影色与强度", Color) = (0.79, 0.43, 0.0, 0.27)
        // 边缘软化在"角度域"：sideAngle 0..1 = 0..180°，值即过渡带的角度宽度。
        // 角度域恒定，不随镜头距离变化——屏幕像素域软化拉远时 fwidth 变大、过渡带
        // 在阈值域摊宽，会把阈值场折线的 C1 折点显影成白线。抹平场折点归
        // ManualSDFBlurSigma（烘前空间模糊）。
        _SoftnessAngle ("边缘软化(角度)", Range(0, 30)) = 3
        _ThresholdBias ("阈值偏移", Range(-1, 1)) = 0
        // 端点钳位（sideAngle 域，C# 由 ManualEndpointSnapDegrees 换算度数推入）。
        // 0 = 禁用。
        _EndpointSnap ("端点钳位", Float) = 0.00833
        // ---- 颈带 N·L 复制品（C# 由 NeckRampScale/Bias/EdgeSoftness/BandTopV 推入）----
        // 形态全手动：光照项过 smoothstep（NeckEdgeSoftness 控边缘软硬），不查任何 ramp 纹理。
        _NeckRampScale ("颈带光照项缩放", Float) = 1
        _NeckRampBias ("颈带光照项偏移", Float) = 0
        _NeckEdgeSoftness ("颈带边缘软化", Range(0, 0.5)) = 0.1
        _NeckBandTopV ("颈带带顶 V", Range(0.2, 0.5)) = 0.2
        // 复制品压黑上限（默认 1=不设限、与不设上限逐位一致）。自阴影开+底光时复制品
        // 压到全黑会叠在引擎 receive_shadows 投影上成双层全黑分割线；调低上限只减淡
        // 这层压黑（原生投影层次透出来），非逐方向补丁。
        _NeckReplicaCap ("复制品压黑上限", Range(0, 1)) = 1
        // 真实阴影消费权重（默认 1）：颈带复制品把屏幕空间真实阴影合进光能再过阴影
        // 曲线（与 body 单次着色同构）；0 = N·L 独立判光。自阴影关或
        // _ShadowmapAvail=0 时两者同值。注意：须配合主开关（00_Enabled，
        // 清底色）使用——底色若仍带原生投影，本项只会叠加不会抵消（除法补偿
        // 因 HDR 缓冲不钳位过驱泛白，禁止）。
        _NeckShadowCompensation ("复制品真实阴影消费", Range(0, 1)) = 1
        // 发影(00/02~05/06/01):HairShadowPass 把头发渲进 RT,本层采样后
        // max 融合。两形态(01):A=屏幕域位移 RT + 像素自身位置单点采样 + 单侧
        // 深度门;B=光空间正交深度 RT + worldPos 投影采样 + PCF(06 控块间距),
        // 影贴脸起伏、随光向转、与镜头解耦。
        // 已否决路线(禁止复活):头前正交 mask 相机(狗牙=采样率硬墙);偏移点采 RT
        // (出屏截断/深度窗错配);直画乘算(加深/双影/双环无解)。
        // C# 取到阈值图时置 1。为 0 表示没有手绘素材可用，整层不生效。
        _UseSDFTex ("已挂阈值图", Float) = 0
        // 逐角色软开关（float 滑条，不依赖 keyword 变体）。
        // ME 桶登记属性名 Enable(Float)，ME 写回 SetFloat("_Enable")；shader 内
        // _Enable<0.5 输出白色=不生效。插件 Apply 新材质时 SetFloat(_Enable,1)
        // 默认生效，玩家 ME 滑条拉到 0 才软关闭。
        _Enable ("Enable", Float) = 1
    }

    SubShader
    {
        // EC 面部材质 renderQueue 是 2350，叠加层必须排在它之后才可见。
        // C# 侧会用配置值覆盖 material.renderQueue，这里只是兜底默认。
        Tags { "RenderType"="Overlay" "Queue"="Transparent" }

        Pass
        {
            // ForwardBase 让 Unity 自动喂 _WorldSpaceLightPos0，
            // 绕开 EC 三套光源系统（Map sunlight / HEditGlobal / 捏脸场景）的适配问题。
            Tags { "LightMode"="ForwardBase" }

            Blend DstColor Zero   // 乘算
            ZWrite Off
            ZTest LEqual
            Cull Back


            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase
            // fwidth 需要 SM3.0；EC 用 Unity 2017.4.24，目标硬件均支持。
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _SDFTex;
            // 屏幕空间阴影图（引擎全局绑定）。receiveShadows=false 的 renderer 上
            // 该绑定仍成立（实测）；若不成立，症状 = 颈带出现异常平坦明暗
            // （采样到未绑定的黑/白）。
            // 头发独占屏幕空间 RT:HairShadowPass 把头发屏幕域
            // 位移后渲进与主相机同视角的 RT;R=预乘"原位置"眼深,G=覆盖率;
            // 本层在像素自身位置单点采样,无偏移采样
            sampler2D _HairShadowRT;
            // 头发独占投影权重(00,C# 按 总开关×_Enable×00_ShadowEnabled 门控)
            float _HairShadowWeight;
            // 06:软边(0..1)。形态 A 0=单 tap锐边/>0=3×3 软核(间距 v×6px,配 2× 超采样 RT)
            float _HairShadowSoft;
            // 深度门正面区容差(米,Debug 节 SS_DepthTol);掠射区按比例收紧(见 HairShadowSample)
            float _HairShadowTol;
            // 01:发影形态(0=屏幕位移/1=光空间正交投影)。1 时 RT 内容是光空间
            // 深度图,采样坐标改由 worldPos 投影得到,与镜头解耦。
            float _HairShadowForm;
            // 06(形态 B 路径):PCF 块间距(0..1 → 0..6 texel,0=单块;uniform 名沿用)
            float _HairShadowBlur;
            // 光空间深度门 bias(米,C# 定死常量推送,无配置项)
            float _HairShadowBias;
            // 光空间 view 与正交盒(C# 每帧推,与遮罩 pass 1 同源同帧)
            float4x4 _HairLightView;
            float4 _HairLightBox;   // x=正交盒半宽(米)、y=RT texel 尺寸(1/边长)
            sampler2D _ShadowMapTexture;

            // C# 每轮轮询推送：QualitySettings.shadows != Disable 时为 1。
            // 0 = 阴影图不存在（自阴影关），attenEff 退化为 1。
            float _ShadowmapAvail;
            fixed4 _ShadowColor;
            float _Enable;          // 软开关：<0.5 输出白=不生效（ME Float 滑条写回）
            float _SoftnessAngle;
            float _ThresholdBias;
            float _EndpointSnap;   // 端点钳位：把光角钳离 0/1 端点，0=禁用（见 Properties 注释）
            float _NeckRampScale;  // 复制品光照项缩放（纯标定旋钮）
            float _NeckRampBias;   // 复制品光照项偏移（纯标定旋钮）
            float _NeckEdgeSoftness; // 复制品 smoothstep 半宽 w（0=硬边，越大越软）
            float _NeckBandTopV;   // 颈带带顶 V（上拉盖住下颌遗留 SDF 形状）
            float _NeckReplicaCap; // 复制品压黑上限（1=不设上限）
            float _NeckShadowCompensation; // 真实阴影消费权重（0=独立判光）
            float _UseSDFTex;       // 无阈值图：<0.5 输出白=不生效
            // 头骨 world→local 矩阵，C# 每帧从 objHeadBone 推入。
            // 不能用 unity_WorldToObject：cf_O_face 是 SkinnedMeshRenderer，顶点由骨骼驱动，
            // 渲染器自身 transform 不跟头骨转，用它会导致转头时阴影纹丝不动。
            // _UseHeadMatrix<0.5 时回落 unity_WorldToObject（C# 没推成功时的兜底）。
            float4x4 _HeadWorldToLocal;
            float _UseHeadMatrix;
            // ---- 表情 UV 补偿（分区 affine）----
            // 30 = 5 区域 × 6 系数（du=a*u+b*v+tx; dv=c*u+d*v+ty，u,v 归一化 UV）。
            // C# 每帧按 blendshape 权重线性累加后推入，单位为离线拟合的 512px texel 基准；
            // 运行时 1024 SDF 仍使用同一 UV 位移语义，不改系数表/Bundle。
            // 方向：表情把皮肤连同阈值图案一起拽动，采样沿位移场正向偏移才能取回
            // 中性姿势的图案。区域矩形与离线拟合脚本 REGIONS 一致（见 FaceBlendCompensation）。
            // _BlendCompEnable<0.5 或旧 bundle（无此 uniform，默认 0）时不补偿。
            float _BlendCompAffine[30];
            float _BlendCompEnable;

            // ---- 颈带 crossfade V 常量 ----
            // cf_O_face 的颈段是 UV 低 V 处的近全宽圆柱展开带（512 coverage 逐行实测
            // v≈0.017..0.33），唇部约 v0.30 以上。带顶 _NeckBandTopV 可调（覆盖全颈段
            // 含下颌下；上拉可盖住下颌两侧遗留的静态 SDF 形状/收窄 y 向渐变），
            // 带底 0.05 近 mesh 缝；两者之间 smoothstep 渐变交接。
            #define NECK_BAND_BOT_V 0.05

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
                // 头发独占投影的世界坐标(单点采样深度锚)。
                // SMR 顶点到 shader 时已蒙皮,unity_ObjectToWorld 即得世界位。
                float3 worldPos : TEXCOORD3;
                float3 worldNormal : TEXCOORD1;
                // 屏幕空间阴影采样坐标（手动采样，不走 keyword 宏——干净底色方案下
                // 底色 renderer receiveShadows 被关掉，keyword 变体不会带阴影坐标）
                float4 shadowCoord : TEXCOORD2;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv  = v.uv;
                // 蒙皮后法线与蒙皮顶点同源（rootBone 物体空间），UnityObjectToWorldNormal
                // 与之配对可用（实测）；颈带复制品用它对世界光向做 N·L。
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.shadowCoord = ComputeScreenPos(o.pos);
                return o;
            }

            fixed4 ApplyShadowColor(float shadow)
            {
                // alpha 明确定义为阴影强度；输出 alpha 对 DstColor 乘算本身无意义，固定为 1。
                float strength = saturate(shadow) * saturate(_ShadowColor.a);
                return fixed4(lerp(fixed3(1,1,1), _ShadowColor.rgb, strength), 1);
            }

            // 区域矩形软掩码：rect = (u0, v0, u1, v1)，e 为过渡带宽（UV 单位）。
            // 矩形本不重叠、边界相接（颊/眼 v 相邻、左右眼 u=0.5 相接），
            // 过渡带在缝隙处互补，防止补偿在区域边界跳变。
            float RegionMask(float2 uv, float4 rect, float e)
            {
                float2 lo = smoothstep(rect.xy - e, rect.xy + e, uv);
                float2 hi = 1.0 - smoothstep(rect.zw - e, rect.zw + e, uv);
                return lo.x * lo.y * hi.x * hi.y;
            }

            // r 区域的 texel 位移场（affine）。索引全为编译期常量展开，避开 SM3.0 动态索引。
            #define COMP_AFFINE(uv, r) \
                float2(_BlendCompAffine[(r)*6+0]*(uv).x + _BlendCompAffine[(r)*6+1]*(uv).y + _BlendCompAffine[(r)*6+2], \
                       _BlendCompAffine[(r)*6+3]*(uv).x + _BlendCompAffine[(r)*6+4]*(uv).y + _BlendCompAffine[(r)*6+5])

            float2 ApplyBlendComp(float2 uv)
            {
                if (_BlendCompEnable < 0.5) return uv;
                float2 d = 0;
                // e=0.015 ≈ 7.7 texel：够遮边界跳线，又不至于把区域糊没
                d += RegionMask(uv, float4(0.32, 0.36, 0.44, 0.44), 0.015) * COMP_AFFINE(uv, 0); // R_cheek
                d += RegionMask(uv, float4(0.56, 0.36, 0.68, 0.44), 0.015) * COMP_AFFINE(uv, 1); // L_cheek
                d += RegionMask(uv, float4(0.30, 0.45, 0.50, 0.70), 0.015) * COMP_AFFINE(uv, 2); // R_eye
                d += RegionMask(uv, float4(0.50, 0.45, 0.70, 0.70), 0.015) * COMP_AFFINE(uv, 3); // L_eye
                d += RegionMask(uv, float4(0.44, 0.15, 0.56, 0.50), 0.015) * COMP_AFFINE(uv, 4); // mouth
                return uv + d * (1.0 / 512.0);
            }

            // 发影采样:位移在几何侧(HairShadowMask 顶点屏幕域
            // 百分比平移后渲进 RT),本层在像素自身位置单点采样——无偏移采样,
            // 出屏截断/深度窗错配两族旧失败结构性不存在。深度门为单侧门(判 RT
            // 编码的"原位置"深度,拒脸后头发的穿透影)。
            float HairShadowSample(float2 suv, float3 worldPos, float3 worldNormal)
            {
                float eye = -(mul(UNITY_MATRIX_V, float4(worldPos, 1.0))).z;
                float2 rg;
                float soft = saturate(_HairShadowSoft);
                if (soft > 0.001)
                {
                    // 06 软档:3×3 软核,间距=v×6px(消费像素域)。满档柔和度按
                    // 实测翻倍(3→6)。预乘对 (R,G) 一起累加,平均后 r/g 仍是
                    // 覆盖率加权的平均深度;2× 超采样 RT 保证核内输入亚像素干净。
                    float2 px = (soft * 6.0) / _ScreenParams.xy;
                    float2 acc = float2(0.0, 0.0);
                    for (int ky = -1; ky <= 1; ky++)
                    {
                        for (int kx = -1; kx <= 1; kx++)
                        {
                            acc += tex2Dlod(_HairShadowRT,
                                float4(suv + float2(kx, ky) * px,
                                       0.0, 0.0)).rg;
                        }
                    }
                    rg = acc * (1.0 / 9.0);
                }
                else
                {
                    rg = tex2Dlod(_HairShadowRT, float4(suv, 0.0, 0.0)).rg;
                }
                // G=覆盖率:预乘编码下无发 texel 恒 0 短路
                if (rg.g < 0.05) return 0.0;
                float hairEye = rg.r / rg.g;
                // 单侧门(判 RT 编码的"原位置"深度):只认原深度在脸面前(≤eye+tol)
                // 的头发。远侧(脸后)头发原深度恒 >eye → 穿透被拒;可见面在背光时
                // 本就不该有前发影(影落头后不可见侧),单侧门拒=正确。
                // 坑:不能用双侧窗——多层头发共址时 coverage 是并集、值是最前层的,
                // 下界会把"最前层出窗"的像素整格挖掉(合法影被拖累),容差小值时
                // 表现为影突变/U 形缩放。
                // 正面区容差(米,Debug 节 SS_DepthTol)。掠射感知:轮廓带(NdV→0)视线掠过
                // 脸颊后到贴脸头发的深度增长仅几毫米,大容差必放行穿透影→按 ~1/30 收紧;
                // 渐隐带宽随 tol 缩放,否则收紧段只压淡不拒掉。ndv≥0.6 用全量保贴脸合法影。
                float tolFront = max(_HairShadowTol, 0.001);
                float3 vDir = normalize(_WorldSpaceCameraPos.xyz - worldPos);
                float ndv = saturate(dot(normalize(worldNormal), vDir));
                float tol = max(tolFront * lerp(0.033, 1.0, smoothstep(0.30, 0.60, ndv)), 0.0005);
                float outside = hairEye - eye - tol;
                float gate = 1.0 - smoothstep(0.0, tol, outside);
                return rg.g * gate;
            }

            // 单 texel 深度门:返回该 texel 的遮挡量(0 或覆盖率)。RT 走 Point 采样,
            // uv 必须已对齐 texel 中心。
            float HairGate(float2 uv, float selfDepth)
            {
                float2 rg = tex2Dlod(_HairShadowRT, float4(uv, 0.0, 0.0)).rg;
                if (rg.g < 0.05) return 0.0;
                // 单侧软化:余量 m≥0 恒全影=静态影形零回归;[−S,0] 渐隐带兜眨眼等
                // 表情隆起耗尽 bias 余量时的硬吞。S 覆盖 RT 深度抖动、远小于发脸间距。
                const float HAIR_GATE_SOFTNESS = 0.002;
                float m = selfDepth - _HairShadowBias - rg.r / rg.g;
                return rg.g * smoothstep(-HAIR_GATE_SOFTNESS, 0.0, m);
            }

            // 4-tap 双线性 PCF:先逐 texel 比较、再按亚 texel 权重插值。顺序不能反
            // ——先双线性混合深度再比较,结果恒是二值,而光空间 1 texel 在特写下
            // 约合 4 个屏幕像素,于是直接显影成阶梯锯齿。
            float HairPcf4(float2 uv, float selfDepth, float2 texel)
            {
                float2 t = uv / texel - 0.5;
                float2 b = floor(t);
                float2 f = t - b;
                float2 c = (b + 0.5) * texel;
                float o00 = HairGate(c, selfDepth);
                float o10 = HairGate(c + float2(texel.x, 0.0), selfDepth);
                float o01 = HairGate(c + float2(0.0, texel.y), selfDepth);
                float o11 = HairGate(c + texel, selfDepth);
                return lerp(lerp(o00, o10, f.x), lerp(o01, o11, f.x), f.y);
            }

            // 光空间发影采样(形态 B):脸像素 worldPos 投回光空间取 UV 与自身深度,
            // 与 RT 里该方向最前头发的深度比较——影随光向转、贴真实脸起伏,与镜头解耦。
            // UV/深度公式与遮罩 pass 1 逐字符同源(光空间 view 线性映射),投影矩阵
            // 不参与任何采样计算:clip.w 恒 1 与平台 FlipY/z 范围两坑
            // 在此结构性不可达。
            float HairShadowSampleLight(float3 worldPos)
            {
                float3 vpos = mul(_HairLightView, float4(worldPos, 1.0)).xyz;
                float2 uv = vpos.xy / _HairLightBox.x * 0.5 + 0.5;
                // 盒外无数据:Clamp 采样会把边缘值沿盒外抹成条带,显式判越界
                if (any(uv < 0.0) || any(uv > 1.0)) return 0.0;
                float selfDepth = -vpos.z;

                float2 texel = _HairLightBox.yy;
                float blur = saturate(_HairShadowBlur);
                // 06=0:单个 PCF 块(1 texel 过渡,已足够平滑,不糊)
                if (blur < 0.01) return HairPcf4(uv, selfDepth, texel);

                // 06>0:3×3 个 PCF 块,块间距随 06 拉宽 = 可调软影半径
                // (满档柔和度按实测翻倍:系数 3→6)
                float2 s = texel * (blur * 6.0);
                float acc = 0.0;
                for (int ky = -1; ky <= 1; ky++)
                {
                    for (int kx = -1; kx <= 1; kx++)
                        acc += HairPcf4(uv + float2(kx, ky) * s, selfDepth, texel);
                }
                return acc * (1.0 / 9.0);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // 两种"不生效"合并在这里输出白（乘算不变=对脸零影响）：
                // ① _Enable<0.5：玩家在 ME 里逐角色软关闭；
                // ② _UseSDFTex<0.5：没有手绘阈值图可挂（没有任何自动兜底图）。
                if (_Enable < 0.5 || _UseSDFTex < 0.5)
                    return fixed4(1,1,1,1);

                // 光方向转到"脸的参照系"。方向光时 _WorldSpaceLightPos0.w==0。
                // 用头骨矩阵而非 unity_WorldToObject：见 _HeadWorldToLocal 处注释。
                float3 lWorld = normalize(_WorldSpaceLightPos0.xyz);
                float3 lObj;
                if (_UseHeadMatrix > 0.5)
                    lObj = normalize(mul((float3x3)_HeadWorldToLocal, lWorld));
                else
                    lObj = normalize(mul((float3x3)unity_WorldToObject, lWorld));

                // 阈值图采样统一走补偿后的 UV（图案锚回中性姿势）
                // 双边界语义：像素存受光窗口 [lo, hi]（sideAngle 域 0=正面、1=背面）。
                // sideAngle ∈ [lo,hi] 内受光(无阴影)，窗口外阴影；lo>hi 表示该像素永远阴影。
                // 通道：B=右光 lo、R=右光 hi、A=左光 lo、G=左光 hi。
                float2 sdfUv = ApplyBlendComp(i.uv);
                float4 thresholdData = tex2D(_SDFTex, sdfUv);

                // 侧光角：abs(yaw)/PI ∈ [0,1]，正面(yaw=0)=0 → 背面(|yaw|=PI)=1。
                // 语义为"正面到背面半周连续扫掠"。不要用 2/PI(正→侧 90 度即饱和)。
                float sideAngle = saturate(abs(atan2(lObj.x, lObj.z)) * 0.31830988618);

                // 原始光角：正面死区判定必须用它——端点钳位会改变端点邻域取值，
                // 用钳过的值判死区会破坏"正面附近强制读左光"的语义。
                float sideAngleRaw = sideAngle;

                // pitch 渐暗门：阈值图是 yaw 域产物，对极端俯仰无感知（冻结水平帧），
                // 光接近头轴(顶/底 37°锥内)时手绘形态必然失真——与其显错不如渐隐。
                // |lObj.y| 0.8→1.0 线性 0→1 全暗，底/顶两侧对称，与阴影色同语义。
                float pitchDark = saturate((abs(lObj.y) - 0.8) * 5.0);

                // 左右判读：x>0=右光、x<0=左光。但正面(0°)附近 lObj.x 是浮点噪声，
                // step(0,x) 会乱跳，把正面光误读成右光(镜像)，导致鼻影形态和 left8 不符。
                // 加正面死区：sideAngle 落在正面死区时强制读左光原图(左右对称，读哪边一致)。
                float rightLight = step(0.0, lObj.x);
                float frontDead = step(sideAngleRaw, 0.02);   // sideAngle<=0.02(约1°) → 读左光
                rightLight = lerp(rightLight, 0.0, frontDead);

                float lo = rightLight > 0.5 ? thresholdData.b : thresholdData.a;
                float hi = rightLight > 0.5 ? thresholdData.r : thresholdData.g;
                // 有效受光窗口：lo<=hi 才存在窗口；lo>hi（全程暗）→ active=0 → 永远阴影。
                float active = step(lo, hi);

                // 颈带 crossfade 权重：v 低（带底，近 mesh 缝）→1 纯复制品，
                // v 高（带顶）→0 纯 SDF 窗口，两个 smoothstep 端点间渐变交接。
                // 不乘 active：下颌/颈部 coverage 无效区（lo>hi，烘焙写死永远阴影）
                // 由复制品接管——否则 SDF 无 pitch 感知地永久压暗该区，底光时与
                // body 侧 N·L 明暗割裂。
                float neckBand = 1.0 - smoothstep(NECK_BAND_BOT_V, _NeckBandTopV, sdfUv.y);

                // 端点钳位：sideAngle 恰为 0/1 时，可见边界是零距离等高线，直接骑在
                // 手绘帧轮廓的原始像素锯齿上（端点折线）。把光角钳离端点 = 水位抬高而
                // 场不动，端点处直接显示已实测平滑的"偏离 snap 度"形态。
                sideAngle = clamp(sideAngle, _EndpointSnap, 1.0 - _EndpointSnap);

                float bias = _ThresholdBias * 0.5;
                lo += bias;
                hi += bias;
                // 过渡带 = max(fwidth 抗锯齿下限, 角度域软化, 1/1024 texel 地板)。
                // 角度项：soft = 软化角度 / 180°，在 sideAngle 域恒定，不随镜头
                // 距离变化——屏幕像素域过渡带随 fwidth 随距离增大，远距离会把阈值场
                // 折线的 C1 折点摊宽显影成白线，软化必须用角度域。
                // fwidth 项只兜底抗锯齿下限(AA_PX 恒 1.5px)：宽带柔边全由角度项给。
                // 地板 1/1024(≈0.18°)：soft=0 且场平坦(fwidth≈0)时仍保住最小
                // 过渡带，避免零宽 smoothstep 退化成纯硬 step 抖动。用户把 05
                // 拉到 0 想要"硬边"时，0.18° 在屏幕上不可辨，语义不受影响。
                // 关键：fwidth 必须作用在"逐像素变化"的量上(lo/hi 是纹理采样值)；
                // sideAngle 对整个 mesh 恒定，fwidth(sideAngle)=0，不能用它。
                // ManualSDFBlurSigma 空间模糊负责抹平手绘场折点（消白线），与这里的柔边分工。
                float soft = _SoftnessAngle / 180.0;                 // sideAngle 域 0..1
                float edgeLo = max(max(fwidth(lo) * 1.5, soft), 1.0 / 1024.0);
                float edgeHi = max(max(fwidth(hi) * 1.5, soft), 1.0 / 1024.0);
                float wlo = smoothstep(lo - edgeLo, lo + edgeLo, sideAngle);   // 已过受光开角
                float whi = 1.0 - smoothstep(hi - edgeHi, hi + edgeHi, sideAngle); // 未到受光关角
                float lit = active * wlo * whi;                     // 窗口内=1(受光)
                float shadow = 1.0 - lit;                           // 受光外=阴影

                // 引擎屏幕空间阴影图采样:颈带复制品消费(真实阴影合成)。
                // 脸部主体不消费——背光时舌头/后发影会糊满本已阴影的面部，
                // 底色投影由主开关 00_Enabled 从 renderer 层清干净。
                float attenRaw = tex2D(_ShadowMapTexture,
                                       i.shadowCoord.xy / i.shadowCoord.w).r;
                float hairAmount = 0.0;
                if (_HairShadowWeight > 0.001)
                {
                    // 形态 A=屏幕位移单点+单侧门;形态 B=光空间深度门+PCF
                    float tap = (_HairShadowForm > 0.5)
                        ? HairShadowSampleLight(i.worldPos)
                        : HairShadowSample(i.shadowCoord.xy / i.shadowCoord.w, i.worldPos, i.worldNormal);
                    hairAmount = _HairShadowWeight * tap;
                    // 颈带排除防双算:复制品消费的引擎阴影图里本来就含头发投影
                    hairAmount *= 1.0 - neckBand;
                }
                // 同层融合:SDF 与发影取暗合一再过一次阴影色——单值融合只有一个
                // 明暗包络,额发际不叠乘变深(独立乘算 draw 做不到)
                shadow = max(shadow, hairAmount);
                shadow = max(shadow, pitchDark);
                float3 outMul = ApplyShadowColor(shadow).rgb;

                // 颈带 N·L 复制品：世界法线 · 世界光向（ForwardBase 主光）→
                // 半兰伯特 → 软硬可调的 smoothstep 出受光量，阴影乘数走与 SDF 分支完全
                // 同款的 ApplyShadowColor（判定因子 1-lit 即阴影量）——缝两侧颜色语义
                // 一致、随 02_ShadowColor 同步。消费完整光向（含 pitch，SDF 窗口给不了的）。
                // 禁止用 lObj（头空间）参与 N·L——头转动会把 N·L 一起转走，与 body 侧
                // 世界光向语义脱钩。
                {
                    float3 nWorld = normalize(i.worldNormal);
                    // 消费屏幕空间真实阴影（采样已在上方全脸段完成）。除法补偿会
                    // 把乘子顶到 >1 直接泛白光（HDR 缓冲不钳位）→ 本式恒乘算、
                    // 所有因子 ≤1。底色 renderer 的 receiveShadows 由 C# 关闭
                    // （主开关 00_Enabled），底色不再含原生投影，插件层全权负责颈带明暗。
                    // _ShadowmapAvail=0（自阴影关/质量档奇数）时阴影图未绑定不可采，
                    // C# 推 0 → attenEff=1 退回独立判光合成，两态自动切换。
                    float attenEff = lerp(1.0, saturate(attenRaw),
                                          _ShadowmapAvail * _NeckShadowCompensation);
                    // 遮挡合入顺序：先半兰伯特、后乘 atten。游戏实测依据：开自阴影
                    // 被身体挡住时朝光的表面也全暗 → 主皮肤是 ((N·L+1)/2)×atten；
                    // 若按 (N·L×atten+1)/2 合成，朝光面被遮挡时恒为 0.5（中间值），
                    // 颈带压不到暗 → 身侧已暗、颈带还亮 → 接缝泛白（2026-08-25 实测）。
                    // 附带性质：attenEff=1 时本式与独立判光式逐位相同，
                    // _NeckRampScale/_NeckRampBias 标定不受影响。
                    // 它们是光照项标定旋钮；_NeckEdgeSoftness = smoothstep 半宽 w（钳离 0 防 NaN）。
                    float neckW = max(_NeckEdgeSoftness, 1e-4);
                    float ndl01 = dot(nWorld, lWorld) * 0.5 + 0.5;
                    float litNew = smoothstep(0.5 - neckW, 0.5 + neckW,
                                              ndl01 * attenEff * _NeckRampScale
                                              + _NeckRampBias);
                    // cap 默认 1 时 min 退化为原式；输出恒 ≤1，无过驱风险。
                    // 颈带同吃 pitch 渐暗：顶光时 N·L 会把颈带判亮，与主域的
                    // 渐暗割裂，同一门拉平保证两侧包络连续。
                    float neckShadow = min(1.0 - litNew, _NeckReplicaCap);
                    neckShadow = max(neckShadow, pitchDark);
                    float3 neckMul = ApplyShadowColor(neckShadow).rgb;
                    outMul = lerp(outMul, neckMul, neckBand);
                }
            return fixed4(outMul, 1);
            }
            ENDCG
        }
    }

    Fallback Off
}
