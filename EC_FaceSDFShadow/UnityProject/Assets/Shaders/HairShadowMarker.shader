// ME 标记专用:玩家对饰品材质的 .MECopy 副本换上它,插件按 shader 名识别为发影投影源。
// ColorMask 0 = 副本叠加渲染但零输出,视觉零影响;改成可见输出会把饰品画花。
Shader "Rainbowing/HairShadowMarker"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }

        Pass
        {
            Cull Off
            ZWrite Off
            ColorMask 0

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            float4 vert(float4 v : POSITION) : SV_POSITION
            {
                return UnityObjectToClipPos(v);
            }

            fixed4 frag() : SV_Target
            {
                return 0;
            }
            ENDCG
        }
    }
}
