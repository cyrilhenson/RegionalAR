// RegionalAR — Matte nerve shader
//
// Same view-dependent matte model as BoneMatte / VesselMatte but tuned
// for nerve appearance: warm amber-gold base, slightly brighter overall
// (nerves should "pop" against bone and vessels for teaching use).
// Cut-plane discard matches the volume raymarcher.

Shader "RegionalAR/NerveMatte"
{
    Properties
    {
        // Warm amber — nerves render bright enough to stand out against
        // bone ivory and vessel crimson
        _Color      ("Nerve Tint",       Color)       = (0.90, 0.72, 0.20, 1)
        _CoreColor  ("Crevice Shade",    Color)       = (0.28, 0.18, 0.05, 1)
        _AmbientMin ("Ambient Minimum",  Range(0,1))  = 0.12
        _RimColor   ("Rim Color",        Color)       = (1, 0.92, 0.50, 1)
        _RimIntensity("Rim Intensity",   Range(0,2))  = 0.20
        _RimPow     ("Rim Falloff",      Range(0.5,8)) = 3.0
        _ShadeGamma ("Shade Gamma",      Range(0.5,4)) = 2.0

        _CutPlane1N ("Cut Plane 1 Normal", Vector) = (0,1,0,0)
        _CutPlane1D ("Cut Plane 1 Dist",   Float)  = 1.0
        _CutPlane2N ("Cut Plane 2 Normal", Vector) = (1,0,0,0)
        _CutPlane2D ("Cut Plane 2 Dist",   Float)  = 1.0

        _Opacity ("Opacity", Range(0,1)) = 1.0
    }

    SubShader
    {
        Tags { "Queue" = "Geometry+20" "RenderType" = "Opaque" }
        Cull Off
        ZWrite On
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            fixed4 _Color, _CoreColor, _RimColor;
            float  _AmbientMin, _RimIntensity, _RimPow, _ShadeGamma;
            float4 _CutPlane1N; float _CutPlane1D;
            float4 _CutPlane2N; float _CutPlane2D;
            float  _Opacity;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos       : SV_POSITION;
                float3 worldNrm  : TEXCOORD0;
                float3 worldPos  : TEXCOORD1;
                float3 pCentered : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos       = UnityObjectToClipPos(v.vertex);
                o.worldNrm  = UnityObjectToWorldNormal(v.normal);
                o.worldPos  = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.pCentered = v.vertex.xyz;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float3 cp1N = normalize(_CutPlane1N.xyz);
                float3 cp2N = normalize(_CutPlane2N.xyz);
                if (dot(i.pCentered, cp1N) > _CutPlane1D) discard;
                if (dot(i.pCentered, cp2N) > _CutPlane2D) discard;

                #if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
                    float3 camW = unity_StereoWorldSpaceCameraPos[unity_StereoEyeIndex];
                #else
                    float3 camW = _WorldSpaceCameraPos;
                #endif

                float3 n = normalize(i.worldNrm);
                float3 v = normalize(camW - i.worldPos);
                float  ndotV = saturate(abs(dot(n, v)));

                float shade = lerp(_AmbientMin, 1.0, pow(ndotV, _ShadeGamma));
                fixed3 core = lerp(_CoreColor.rgb, _Color.rgb, shade);
                float  rim  = pow(1.0 - ndotV, _RimPow) * _RimIntensity;
                fixed3 col  = core + rim * _RimColor.rgb;

                return fixed4(col, _Opacity);
            }
            ENDCG
        }
    }
    Fallback "Unlit/Color"
}
