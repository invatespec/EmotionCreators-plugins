// 头发阴影 RT 输出:头发在屏幕域位移后渲进屏幕分辨率
// RT——clip 平移无透视形变(出屏截断/斜截/凸凹形变三族旧失败根治);RT 接收
// (ZWrite 塌缩 overlap+消费端 3×3 软核=柔边/单值融合)。
// 输出 R=预乘覆盖率的米制眼深(a·eye)、G=覆盖率 a;消费端 G≈0 短路,有发时
// 眼深=R/G 还原;预乘防边缘双线性把"无发"混成中间深度。
// 坑:原版 EC 头发无发丝 alpha(_MainTex 运行时恒 null),默认实心渲;仅 MOD
// 卡片式头发(_UseAlphaTest=1)走裁剪。
Shader "Rainbowing/HairShadowMask"
{
    Properties
    {
        _MainTex ("发丝 alpha 纹理(仅 MOD 卡片式头发)", 2D) = "white" {}
        _Cutoff ("裁剪阈值", Range(0, 1)) = 0.5
        // 0=实心渲几何(原版 EC 头发)、1=走 alpha test(MOD 卡片式)
        _UseAlphaTest ("启用 alpha 裁剪", Float) = 0
    }
    SubShader
    {
        // 实心几何为主:Opaque 与原版头发 shader 的 RenderType 一致
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }

        Pass
        {
            Cull Off
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed _Cutoff;
            float _UseAlphaTest;

            // C# 每帧推的全局(CB 绘制不走 ForwardBase,拿不到 _WorldSpaceLightPos0):
            // 灯向(指向光源)/X·Y 移动量程(02/03,米)/初始 X·Y 偏移(04/05,米,
            // 不依赖光向)。灯向为零或光沿视轴时光驱动位移 0,仅剩 04/05 基础偏移。
            float4 _HairLightDir;
            float _HairShadowShiftX;
            float _HairShadowShiftY;
            float _HairShadowBaseX;
            float _HairShadowBaseY;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
                // 偏移后顶点的米制眼深(正数,越近越小)
                float eye  : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                // 屏幕域百分比位移:方向=视空间光向 xy 反向(光左转影右
                // 移/上转下移);幅度=θ 线性百分比,θ=atan2(|xy|,z) 分辨正/背面
                // (sin 会早饱和且背面回缩,不可用),135°(3/4 行程)满量程后保持。
                float lLen = length(_HairLightDir.xyz);
                float3 lDir = (lLen > 1e-5) ? _HairLightDir.xyz / lLen
                                            : float3(0, 0, 0);
                float3 lv = mul((float3x3)UNITY_MATRIX_V, lDir);
                float lenXY = length(lv.xy);
                float theta = atan2(lenXY, lv.z);
                float s = saturate(theta * 0.42441);
                float2 dxy = (lenXY > 1e-4) ? (-lv.xy / lenXY) * s
                                            : float2(0, 0);
                // 位移=光驱动(量程 02/03)+初始基础偏移(04/05,不依赖光向,可为负)
                float2 dView = float2(
                    dxy.x * _HairShadowShiftX + _HairShadowBaseX,
                    dxy.y * _HairShadowShiftY + _HairShadowBaseY);
                // 位移经投影矩阵入 clip(视平面 xy 平移,z/w 不动):ndc 位移=
                // 米制量程在该顶点深度处的正确透视;深度编码仍用原位置,
                // 单侧门语义不变(消费端无感)
                float3 wpos0 = mul(unity_ObjectToWorld, v.vertex).xyz;
                float4 clip0 = mul(UNITY_MATRIX_VP, float4(wpos0, 1.0));
                clip0.xy += float2(UNITY_MATRIX_P[0].x, UNITY_MATRIX_P[1].y)
                            * dView;
                o.pos = clip0;
                o.eye = -(mul(UNITY_MATRIX_V, float4(wpos0, 1.0))).z;
                o.uv  = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                // 实心支路(原版):a 恒 1,形状即发片几何的剪影。
                // alpha 支路(MOD 卡片式):软裁剪 smoothstep,边缘在 RT 里是渐变。
                fixed a = 1.0;
                if (_UseAlphaTest > 0.5)
                {
                    fixed alpha = tex2D(_MainTex, i.uv).a;
                    a = smoothstep(_Cutoff - 0.06, _Cutoff + 0.06, alpha);
                }
                // R=预乘覆盖率的米制眼深、G=覆盖率。预乘的意义:边缘双线性只会
                // 把"无发(0,0)"混进来压低 G(等效软化),绝不会造出不存在的中间
                // 深度——旧清屏深度 0(=无限近)混色成描边的坑结构性消失。
                return float4(a * i.eye, a, 0, 1);
            }
            ENDCG
        }

        // Pass 1:光空间正交深度(形态 B)。与 pass 0 同款预乘编码,只换投影与深度源。
        // 关键约定:投影矩阵只出 SV_POSITION,深度/UV 一律从光空间
        // view 空间现算——正交投影 clip.w 恒 1(拿它当深度必是常数),且平台
        // (D3D FlipY/z∈[0,1] vs GL)差异只关在 _HairLightVP 内部,不进采样公式。
        Pass
        {
            Cull Off
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed _Cutoff;
            float _UseAlphaTest;

            // C# 每帧推(PushHairLight):VP=GL.GetGPUProjectionMatrix(ortho,true)×view,
            // 仅供光栅化;View=纯光空间 view;Box.x=正交盒半宽(米)。
            float4x4 _HairLightVP;
            float4x4 _HairLightView;
            float4 _HairLightBox;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos   : SV_POSITION;
                float2 uv    : TEXCOORD0;
                // 光空间米制线性深度(正数,离光越远越大)
                float depth  : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                float3 wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.pos = mul(_HairLightVP, float4(wpos, 1.0));
                o.depth = -(mul(_HairLightView, float4(wpos, 1.0))).z;
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                fixed a = 1.0;
                if (_UseAlphaTest > 0.5)
                {
                    fixed alpha = tex2D(_MainTex, i.uv).a;
                    a = smoothstep(_Cutoff - 0.06, _Cutoff + 0.06, alpha);
                }
                return float4(a * i.depth, a, 0, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
