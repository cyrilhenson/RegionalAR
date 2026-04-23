// RegionalAR — Matte bone shader
//
// Self-contained lighting so the mesh looks like bone in AR/passthrough
// scenes that have NO directional light. Matches the matte "stone" look
// of commercial medical renders (Surgical Theater, apoQlar, Medivis).
//
// Shading formula:
//   base     = _Color                                (tinted white/ivory)
//   ndotV    = abs(dot(worldNormal, viewDir))        (0 at silhouette, 1 facing camera)
//   core     = lerp(_CoreColor, _Color, ndotV)       (deeper crevices shade toward CoreColor)
//   rim      = pow(1 - ndotV, _RimPow) * _RimIntensity
//   final    = core + rim * _RimColor
//
// Double-sided (Cull Off) so it doesn't matter which way the mesh winds.

Shader "RegionalAR/BoneMatte"
{
    Properties
    {
        // Warm bone ivory, dialed down so AR passthrough cameras don't
        // blow it out to white. 0.62 is conservatively dark — crank it
        // back up toward 0.8 if you ever render on a tethered PC VR
        // headset where the whites aren't clipped.
        _Color      ("Bone Tint",        Color)        = (0.62, 0.56, 0.48, 1)
        // Near-black crevice shade — makes the surface read 3D
        _CoreColor  ("Crevice Shade",    Color)        = (0.08, 0.07, 0.06, 1)
        // Ambient almost zero → silhouettes properly dark
        _AmbientMin ("Ambient Minimum",  Range(0,1))   = 0.05
        _RimColor   ("Rim Color",        Color)        = (1, 0.95, 0.85, 1)
        // Rim practically off — any rim at all in passthrough AR
        // produces a white halo that looks worse than no rim
        _RimIntensity("Rim Intensity",   Range(0,2))   = 0.05
        _RimPow     ("Rim Falloff",      Range(0.5,8))  = 4.0
        // Strong gamma → most of the surface renders darker than
        // camera-facing maximum, preventing the "flat white" look
        _ShadeGamma ("Shade Gamma",      Range(0.5,4))  = 2.4

        // Cut planes (object-local, centered at 0.5 matching the volume cube).
        // dist=1 is the "off" state — no plane can clip the [-0.5,0.5] cube
        // at that distance.
        _CutPlane1N  ("Cut Plane 1 Normal", Vector) = (0,1,0,0)
        _CutPlane1D  ("Cut Plane 1 Dist", Float)    = 1.0
        _CutPlane2N  ("Cut Plane 2 Normal", Vector) = (1,0,0,0)
        _CutPlane2D  ("Cut Plane 2 Dist", Float)    = 1.0

        _Opacity ("Opacity", Range(0,1)) = 1.0
    }

    SubShader
    {
        Tags { "Queue" = "Geometry" "RenderType" = "Opaque" }
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

            fixed4 _Color;
            fixed4 _CoreColor;
            fixed4 _RimColor;
            float  _AmbientMin;
            float  _RimIntensity;
            float  _RimPow;
            float  _ShadeGamma;
            float4 _CutPlane1N;
            float  _CutPlane1D;
            float  _Opacity;
            float4 _CutPlane2N;
            float  _CutPlane2D;

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
                // Object-space position relative to the volume cube center
                // (pCentered). Used to evaluate cut planes in the same
                // coord frame the raymarch volume shader uses.
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
                // Mesh verts are in [-0.5, 0.5]^3 already (see Python
                // export), so v.vertex IS the centered position.
                o.pCentered = v.vertex.xyz;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                // ── Cut planes (match volume raymarcher) ──────────
                // Same convention: discard any fragment on the "positive"
                // side of an active plane. dist=1.0 means plane is off.
                float3 cp1N = normalize(_CutPlane1N.xyz);
                float3 cp2N = normalize(_CutPlane2N.xyz);
                // C# negates active dist so push-deeper = more clipping
                if (dot(i.pCentered, cp1N) > _CutPlane1D) discard;
                if (dot(i.pCentered, cp2N) > _CutPlane2D) discard;

                // Per-eye camera position for correct stereo view direction
                #if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
                    float3 camW = unity_StereoWorldSpaceCameraPos[unity_StereoEyeIndex];
                #else
                    float3 camW = _WorldSpaceCameraPos;
                #endif

                float3 n = normalize(i.worldNrm);
                float3 v = normalize(camW - i.worldPos);
                float  ndotV = saturate(abs(dot(n, v)));

                // Gamma-shape the ndotV curve so most of the surface
                // renders with some shading rather than flat "camera-
                // facing = full bright". Higher ShadeGamma → more midtone.
                float shade = lerp(_AmbientMin, 1.0, pow(ndotV, _ShadeGamma));
                fixed3 core = lerp(_CoreColor.rgb, _Color.rgb, shade);

                // Restrained rim at silhouettes — enough to sell edges
                // without washing out the whole bone
                float rim  = pow(1.0 - ndotV, _RimPow) * _RimIntensity;
                fixed3 col = core + rim * _RimColor.rgb;

                return fixed4(col, _Opacity);
            }
            ENDCG
        }
    }
    Fallback "Unlit/Color"
}
