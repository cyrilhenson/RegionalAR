// RegionalAR — Cut-plane disc border
//
// Renders only a colored border frame around a unit UV quad, with
// completely transparent interior. Used for the cut-plane visuals so
// the colored rectangle outline shows the plane extent without
// obscuring the anatomy inside.

Shader "RegionalAR/CutPlaneBorder"
{
    Properties
    {
        _BorderColor ("Border Color",      Color)       = (0.2, 0.8, 1, 1)
        _BorderWidth ("Border Width (UV)", Range(0,0.5)) = 0.04
    }

    SubShader
    {
        Tags { "Queue" = "Overlay" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            fixed4 _BorderColor;
            float  _BorderWidth;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv  = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // Distance from the nearest edge, in UV space [0 .. 0.5]
                float d = min(min(i.uv.x, 1.0 - i.uv.x),
                              min(i.uv.y, 1.0 - i.uv.y));
                // Inside the border? render opaque border color.
                // Outside the border (= interior)? fully transparent.
                if (d < _BorderWidth)
                    return _BorderColor;
                return fixed4(0, 0, 0, 0);
            }
            ENDCG
        }
    }
    Fallback Off
}
