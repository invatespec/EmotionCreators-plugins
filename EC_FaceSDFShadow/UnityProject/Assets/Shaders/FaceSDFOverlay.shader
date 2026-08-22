// 面部 SDF 硬边阴影叠加层。
// 乘算叠加在 main_skin 之上，只压暗不改亮部，因此不需要复刻 EC 皮肤着色的任何逻辑。
//
// 阈值来源唯一：手绘角度帧烘出的 RGBAHalf 受光窗口图。每个像素存一个受光窗口
// [lo, hi]：sideAngle ∈ [lo,hi] 内受光(无阴影)，窗口外为阴影。通道 R/B = 右光 hi/lo，
// G/A = 左光 hi/lo。这样能表达「下颚带侧光亮、正面/背面都暗」的暗→亮→暗时序。
// v0.7 移除了「物体空间法线现算」与「法线方向阈值图」两条旧路径，没有阈值图时整层直接输出白（不生效）。
//
// 两条分支（v0.9.0 路线 1.1）：① 面部主体 = 上面的 SDF 受光窗口；② 颈带 = N·L 复制品
// crossfade——世界法线·世界光向过软硬可调的 smoothstep 出阴影量，再用与 SDF 分支
// 完全同款的 _ShadowColor 混色。缝两侧颜色语义一致（ramp 暗端纯黑导致复制品全黑，
// 且真 ramp 只在部分 ramp 下形态正常，已彻底去 ramp 纹理）。
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
        // 角度域恒定，不随镜头距离变化——旧屏幕像素域方案(_EdgeSoftnessPx)
        // 拉远时 fwidth 变大、过渡带在阈值域摊宽，会把阈值场帧轮廓的 C1 折线
        // 显影成白线，已废弃。抹平场折点归 27_ManualSDFBlurSigma（烘前空间模糊）。
        _SoftnessAngle ("边缘软化(角度)", Range(0, 30)) = 3
        _ThresholdBias ("阈值偏移", Range(-1, 1)) = 0
        // 端点钳位（sideAngle 域，C# 由 29_ManualEndpointSnapDegrees 换算度数推入）。
        // 0 = 禁用。
        _EndpointSnap ("端点钳位", Float) = 0.00833
        // ---- 颈带 N·L 复制品（路线 1.1，C# 由 31/32/33/34 配置推入）----
        // 形态全手动：光照项过 smoothstep（33 控边缘软硬），不查任何 ramp 纹理。
        _NeckRampScale ("颈带光照项缩放", Float) = 1
        _NeckRampBias ("颈带光照项偏移", Float) = 0
        _NeckEdgeSoftness ("颈带边缘软化", Range(0, 0.5)) = 0.1
        _NeckBandTopV ("颈带带顶 V", Range(0.2, 0.5)) = 0.2
        // C# 取到阈值图时置 1。为 0 表示没有手绘素材可用，整层不生效。
        _UseSDFTex ("已挂阈值图", Float) = 0
        // 逐角色软开关（F2：float 滑条，不依赖 keyword 变体）。
        // ME 桶登记属性名 Enable(Float)，ME 写回 SetFloat("_Enable")；shader 内
        // _Enable<0.5 输出白色=不生效。X2 语义：插件 Apply 新材质时 SetFloat(_Enable,1)
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
            fixed4 _ShadowColor;
            float _Enable;          // 软开关：<0.5 输出白=不生效（ME Float 滑条写回）
            float _SoftnessAngle;
            float _ThresholdBias;
            float _EndpointSnap;   // 端点钳位：把光角钳离 0/1 端点，0=禁用（见 Properties 注释）
            float _NeckRampScale;  // 复制品光照项缩放（31_NeckRampScale，纯标定旋钮）
            float _NeckRampBias;   // 复制品光照项偏移（32_NeckRampBias，纯标定旋钮）
            float _NeckEdgeSoftness; // 复制品 smoothstep 半宽 w（33，0=硬边，越大越软）
            float _NeckBandTopV;   // 颈带带顶 V（34_NeckBandTopV，上拉盖住下颌遗留 SDF 形状）
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

            // ---- 颈带 crossfade V 常量（路线 1）----
            // cf_O_face 的颈段是 UV 低 V 处的近全宽圆柱展开带（512 coverage 逐行实测
            // v≈0.017..0.33），唇部约 v0.30 以上。带顶 34 可调（默认 0.32 覆盖全颈段
            // 含下颌下，上拉可盖住下颌两侧遗留的静态 SDF 形状/收窄 y 向渐变），
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
                float3 worldNormal : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv  = v.uv;
                // 蒙皮后法线与蒙皮顶点同源（rootBone 物体空间），UnityObjectToWorldNormal
                // 与之配对可用（08-17 实测）；颈带复制品用它对世界光向做 N·L。
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
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

            fixed4 frag(v2f i) : SV_Target
            {
                // 两种"不生效"合并在这里输出白（乘算不变=对脸零影响）：
                // ① _Enable<0.5：玩家在 ME 里逐角色软关闭；
                // ② _UseSDFTex<0.5：没有手绘阈值图可挂（v0.7 起没有任何自动兜底图）。
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

                // 颈带 crossfade 权重（路线 1）：v 低（带底，近 mesh 缝）→1 纯复制品，
                // v 高（带顶）→0 纯 SDF 窗口，两个 smoothstep 端点间渐变交接。
                // 乘 active（coverage 有效性）限定：coverage 外 texel（lo>hi）不得出复制品。
                float neckBand = (1.0 - smoothstep(NECK_BAND_BOT_V, _NeckBandTopV, sdfUv.y)) * active;

                // 端点钳位：sideAngle 恰为 0/1 时，可见边界是零距离等高线，直接骑在
                // 手绘帧轮廓的原始像素锯齿上（端点折线）。把光角钳离端点 = 水位抬高而
                // 场不动，端点处直接显示已实测平滑的"偏离 snap 度"形态。
                sideAngle = clamp(sideAngle, _EndpointSnap, 1.0 - _EndpointSnap);

                float bias = _ThresholdBias * 0.5;
                lo += bias;
                hi += bias;
                // 过渡带 = max(fwidth 抗锯齿下限, 角度域软化)。
                // 角度项：soft = 软化角度 / 180°，在 sideAngle 域恒定，不随镜头
                // 距离变化——旧方案过渡带随 fwidth 随距离增大，远距离把阈值场
                // 帧轮廓的 C1 折线摊宽显影成白线，故废弃屏幕像素域软化。
                // fwidth 项只兜底抗锯齿下限(AA_PX 恒 1.5px)：宽带柔边全由角度项给。
                // 关键：fwidth 必须作用在"逐像素变化"的量上(lo/hi 是纹理采样值)；
                // sideAngle 对整个 mesh 恒定，fwidth(sideAngle)=0，不能用它。
                // 27 空间模糊负责抹平手绘场折点（消白线），与这里的柔边分工。
                float soft = _SoftnessAngle / 180.0;                 // sideAngle 域 0..1
                float edgeLo = max(max(fwidth(lo) * 1.5, soft), 1e-5);
                float edgeHi = max(max(fwidth(hi) * 1.5, soft), 1e-5);
                float wlo = smoothstep(lo - edgeLo, lo + edgeLo, sideAngle);   // 已过受光开角
                float whi = 1.0 - smoothstep(hi - edgeHi, hi + edgeHi, sideAngle); // 未到受光关角
                float lit = active * wlo * whi;                     // 窗口内=1(受光)
                float shadow = 1.0 - lit;                           // 受光外=阴影
                float3 outMul = ApplyShadowColor(shadow).rgb;

                // 颈带 N·L 复制品（路线 1.1）：世界法线 · 世界光向（ForwardBase 主光）→
                // 半兰伯特 → 软硬可调的 smoothstep 出受光量，阴影乘数走与 SDF 分支完全
                // 同款的 ApplyShadowColor（判定因子 1-lit 即阴影量）——缝两侧颜色语义
                // 一致、随 02_ShadowColor 同步。消费完整光向（含 pitch，SDF 窗口给不了的）。
                // 禁止用 lObj（头空间）参与 N·L——头转动会把 N·L 一起转走，与 body 侧
                // 世界光向语义脱钩。
                {
                    float3 nWorld = normalize(i.worldNormal);
                    float ndl01 = dot(nWorld, lWorld) * 0.5 + 0.5;
                    // 31/32 是光照项标定旋钮（形态全手动，不再查 ramp）；
                    // 33 = smoothstep 半宽 w：0=硬边，越大边缘越软。
                    // w 必须钳离 0：edge0==edge1 时 smoothstep 除零未定义，NaN 会经
                    // lerp 扩散到带顶以上的纯 SDF 区域。1e-4 带宽视觉上即硬边。
                    float neckW = max(_NeckEdgeSoftness, 1e-4);
                    float neckLit = smoothstep(0.5 - neckW, 0.5 + neckW,
                                               ndl01 * _NeckRampScale + _NeckRampBias);
                    float3 neckMul = ApplyShadowColor(1.0 - neckLit).rgb;
                    outMul = lerp(outMul, neckMul, neckBand);
                }
                return fixed4(outMul, 1);
            }
            ENDCG
        }
    }

    Fallback Off
}
