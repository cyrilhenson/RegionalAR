// RegionalAR — Matte vessel shader
//
// Same lighting model as BoneMatte (view-dependent, no scene light needed)
// but tuned for arterial tissue: deeper crimson base, slightly more
// saturation in facing surfaces, hotter rim to pick up on curved tubes.
// Includes cut-plane discard matching the volume raymarcher.

Shader "RegionalAR/VesselMatte"
{
    Properties
    {
        // Warm arterial red, dark enough to survive passthrough highlights
        _Color      ("Vessel Tint",      Color)        = (0.58, 0.08, 0.06, 1)
        // Near-black crevice — vessels branch and curve, so deep shading
        // in the shadow side really helps sell the tubular shape.
        _CoreColor  ("Crevice Shade",    Color)        = (0.10, 0.02, 0.02, 1)
        _AmbientMin ("Ambient Minimum",  Range(0,1))   = 0.08
        // Rim is slightly more prominent than bone — tubes look better
        // with a little specular pop at silhouettes.
        _RimColor   ("Rim Color",        Color)        = (1, 0.35, 0.30, 1)
        _RimIntensity("Rim Intensity",   Range(0,2))   = 0.20
        _RimPow     ("Rim Falloff",      Range(0.5,8))  = 3.0
        _ShadeGamma ("Shade Gamma",      Range(0.5,4))  = 2.0

        // Cut planes (same uniforms as volume raymarcher)
        _CutPlane1N ("Cut Plane 1 Normal", Vector) = (0,1,0,0)
        _CutPlane1D ("Cut Plane 1 Dist",   Float)  = 1.0
        _CutPlane2N ("Cut Plane 2 Normal", Vector) = (1,0,0,0)
        _CutPlane2D ("Cut Plane 2 Dist",   Float)  = 1.0

        _Opacity ("Opacity", Range(0,1)) = 1.0
    }

    SubShader
    {
        Tags { "Queue" = "Geometry+10" "RenderType" = "Opaque" }
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

                // Cut-plane discard — match bone/volume behavior
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
