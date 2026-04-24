// RegionalAR  Volume Raymarch — Quest 3 / Vulkan safe
// OPTIMIZED: 48 steps (was 80), forward-difference gradient (3 reads vs 6),
//            empty-space skipping, half precision where safe.
//            Targeting consistent 72Hz on Quest 3 Adreno GPU.
//
// Uses: sampler3D + tex3Dlod (no gradient in loop), constant loop bound,
//       _WorldSpaceCameraPos (standard Unity), 3 tissue layers + 2 cut planes.
//       Gradient-based opacity for tissue boundary enhancement.
//       Muscle layer removed — produced unusable red haze across whole body.

Shader "RegionalAR/VolumeRaymarch"
{
    Properties
    {
        _Volume     ("Volume", 3D)             = "" {}
        _AlphaScale ("Alpha Scale", Float)     = 1.5
        _Threshold  ("Density Floor", Float)   = 0.01

        // Gradient opacity: 0 = density-only, 1 = gradient fully modulates opacity
        _GradOpacity ("Gradient Opacity", Range(0,1)) = 0.6
        _GradBoost   ("Gradient Boost",  Float)        = 3.0

        // Ray step count: 48 for 128³, 96 for 256³ (set by C# based on volume size)
        _Steps      ("Ray Steps", Float)           = 48

        // Window/Level controls — used ONLY by the cut-plane slice view.
        // Grayscale MRI mode was removed from the user-facing UI; kept
        // here as fixed defaults for radiological slice rendering.
        _GrayscaleMode ("Grayscale Mode (legacy)", Float) = 0
        _WindowCenter  ("Window Center", Float)           = 0.4
        _WindowWidth   ("Window Width",  Float)           = 0.8

        // Axis-aligned crop (0-1 UV space)
        _CropMin    ("Crop Min", Vector)       = (0,0,0,0)
        _CropMax    ("Crop Max", Vector)       = (1,1,1,0)

        // Cut plane 1 (object space, centered at 0.5)
        _CutPlane1N  ("Cut Plane 1 Normal", Vector) = (0,1,0,0)
        _CutPlane1D  ("Cut Plane 1 Dist", Float)    = 1.0

        // Cut plane 2
        _CutPlane2N  ("Cut Plane 2 Normal", Vector) = (1,0,0,0)
        _CutPlane2D  ("Cut Plane 2 Dist", Float)    = 1.0

        // Layer 0 — Bone (HU 700+ → cortical + dense cancellous only)
        _L0On  ("Bone On",   Float)   = 1
        _L0Min ("Bone Min",  Float)   = 0.561
        _L0Max ("Bone Max",  Float)   = 1.0
        _L0Col ("Bone Color",Color)   = (0.95, 0.92, 0.85, 1)

        // Layer 1 — Vasculature (HU 150-400, contrast-enhanced vessels only)
        _L1On  ("Vasc On",   Float)   = 1
        _L1Min ("Vasc Min",  Float)   = 0.382
        _L1Max ("Vasc Max",  Float)   = 0.464
        _L1Col ("Vasc Color",Color)   = (0.95, 0.15, 0.10, 0.95)

        // Layer 2 — Nerves (HU 110-150, AI-remapped peripheral nerve tissue)
        // Natural nerve HU (~30) would overlap muscle/soft tissue, so the AI
        // preprocessing remaps neural-labeled voxels to HU 140 (unique band
        // the shader can detect without collisions).
        _L2On  ("Nerves On",   Float)   = 1
        _L2Min ("Nerves Min",  Float)   = 0.369
        _L2Max ("Nerves Max",  Float)   = 0.382
        _L2Col ("Nerves Color",Color)   = (1.0, 0.95, 0.2, 0.90)

        // Layer 3 — Muscle (HU 70-110, AI-remapped discrete muscle groups)
        // Natural muscle HU (~40-80) overlaps soft tissue, so AI
        // preprocessing remaps labeled muscle voxels to HU 90.
        _L3On  ("Muscle On",   Float)   = 1
        _L3Min ("Muscle Min",  Float)   = 0.356
        _L3Max ("Muscle Max",  Float)   = 0.369
        _L3Col ("Muscle Color",Color)   = (0.85, 0.35, 0.55, 0.75)
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Cull   Front
        ZWrite Off
        Blend  SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            sampler3D _Volume;
            float4 _Volume_TexelSize;  // Unity auto-fills (1/w, 1/h, 1/d, 0)
            half   _AlphaScale;
            half   _Threshold;
            half   _GradOpacity;
            half   _GradBoost;
            float  _Steps;
            float4 _CropMin;
            float4 _CropMax;

            float4 _CutPlane1N;
            float  _CutPlane1D;
            float4 _CutPlane2N;
            float  _CutPlane2D;

            half   _L0On, _L1On, _L2On, _L3On;
            half   _L0Min, _L0Max, _L1Min, _L1Max, _L2Min, _L2Max, _L3Min, _L3Max;
            half4  _L0Col, _L1Col, _L2Col, _L3Col;

            // Grayscale MRI mode
            half   _GrayscaleMode;
            half   _WindowCenter;
            half   _WindowWidth;

            // Stabilized world-to-object matrix — temporally smoothed in C#
            // to absorb sub-frame tracking jitter
            float4x4 _StableWorldToObject;

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos      : SV_POSITION;
                float3 localPos : TEXCOORD0;   // object-space position [0,1]
                float3 worldPos : TEXCOORD1;   // world-space position (for per-eye ray calc)
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos      = UnityObjectToClipPos(v.vertex);
                o.localPos = v.vertex.xyz + 0.5;
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            // Forward-difference gradient: 3 texture reads instead of 6 (central).
            // Slightly less accurate but 50% cheaper — critical for Quest 3 perf.
            // Returns magnitude; outputs normalized gradient direction.
            half gradientAndNormal(float3 p, float3 texSz, half d0, out half3 gNorm)
            {
                half dx = tex3Dlod(_Volume, float4(p + float3(texSz.x, 0, 0), 0)).r - d0;
                half dy = tex3Dlod(_Volume, float4(p + float3(0, texSz.y, 0), 0)).r - d0;
                half dz = tex3Dlod(_Volume, float4(p + float3(0, 0, texSz.z), 0)).r - d0;
                half3 g = half3(dx, dy, dz);
                half mag = length(g);
                gNorm = mag > 1e-5 ? g / mag : half3(0, 1, 0);
                return mag;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                // Per-eye camera position — fixes stereo jitter on Quest 3
                #if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
                    float3 camWorld = unity_StereoWorldSpaceCameraPos[unity_StereoEyeIndex];
                #else
                    float3 camWorld = _WorldSpaceCameraPos;
                #endif

                // Use stabilized world-to-object matrix for smoother ray computation
                // Falls back to standard matrix if stabilized one isn't set
                float4x4 w2o = _StableWorldToObject;
                // Check if matrix is valid (non-zero determinant proxy)
                bool stableValid = (w2o[0][0] != 0 || w2o[1][1] != 0 || w2o[2][2] != 0);
                if (!stableValid) w2o = unity_WorldToObject;

                float3 ro = mul(w2o, float4(camWorld, 1.0)).xyz + 0.5;
                float3 rd = normalize(i.localPos - ro);

                // Texel size for gradient sampling (fallback if _Volume_TexelSize unavailable)
                float3 texSz = float3(1.0/256.0, 1.0/256.0, 1.0/256.0);
                if (_Volume_TexelSize.x > 0)
                    texSz = _Volume_TexelSize.xyz;

                // Ray-AABB  [0,1]^3
                float3 invD = 1.0 / (rd + 1e-6);
                float3 t0   = (float3(0,0,0) - ro) * invD;
                float3 t1   = (float3(1,1,1) - ro) * invD;
                float3 tN   = min(t0, t1);
                float3 tF   = max(t0, t1);
                float tEntry = max(max(tN.x, tN.y), tN.z);
                float tExit  = min(min(tF.x, tF.y), tF.z);
                if (tExit < max(tEntry, 0.0)) discard;
                tEntry = max(tEntry, 0.0);

                // Dynamic step count: 48 for 128³, 96 for 256³ (set by C#)
                // Uses compile-time max of 128 with early break for GPU compat
                int numSteps = clamp((int)_Steps, 16, 128);
                float dt     = (tExit - tEntry) / (float)numSteps;
                // Small deterministic jitter (quarter-step) to reduce banding
                // without the shimmer/noise that full stochastic jitter causes
                float jitter = frac(dot(i.localPos.xy, float2(0.4839, 0.2917))) * 0.25;
                float tCur   = tEntry + dt * jitter;
                float3 step  = rd * dt;
                float3 p     = ro + rd * tCur;
                half4  col   = half4(0, 0, 0, 0);

                // Pre-compute cut plane normals (full precision needed for plane math)
                float3 cp1N = normalize(_CutPlane1N.xyz);
                float3 cp2N = normalize(_CutPlane2N.xyz);

                // When ALL volume layers are off (meshes handle rendering),
                // skip the slice cross-section view — it would produce a
                // blocky warm-tinted skeleton showing through clipped meshes.
                bool anyLayerOn = (_L0On > 0.5 || _L1On > 0.5 || _L2On > 0.5 || _L3On > 0.5);

                // Track consecutive empty samples for adaptive stepping
                int emptyRun = 0;

                // Track whether we've already drawn a slice pixel at a cut
                // plane — only the FIRST crossing should paint the cross-
                // section, subsequent samples along that ray are occluded.
                bool sliceDrawn = false;

                // Constant upper bound (128) for GPU loop unrolling;
                // early break at numSteps keeps actual work proportional
                for (int s = 0; s < 128; s++)
                {
                    if (s >= numSteps) break;
                    // Axis-aligned crop check
                    if (p.x < _CropMin.x || p.y < _CropMin.y || p.z < _CropMin.z ||
                        p.x > _CropMax.x || p.y > _CropMax.y || p.z > _CropMax.z)
                    {
                        p += step;
                        continue;
                    }

                    float3 pC = p - float3(0.5, 0.5, 0.5);

                    // ── Cut plane handling with slice view ──
                    // When a ray enters the "clipped" side of a cut plane,
                    // sample the volume and paint an opaque cross-section
                    // pixel IF there's real tissue at that point. Air/void
                    // regions past the plane are NOT painted (prevents the
                    // black-box artefact when the plane cuts through empty
                    // space). Once a slice has been drawn the ray stops.
                    // Cull side = half where dot(pC, N) < dist (flipped from
                    // the natural > so that "push disc deeper into volume"
                    // reduces the cut — matches user's mental model of the
                    // disc being the cut surface). C# negates both normal
                    // and dist for active planes — flips clip to NEAR half
                    // while preserving push-deeper = more clipping feel.
                    bool past1 = (dot(pC, cp1N) > _CutPlane1D);
                    bool past2 = (dot(pC, cp2N) > _CutPlane2D);
                    if (past1 || past2)
                    {
                        // When all layers are off (meshes render instead),
                        // skip slice view entirely — just discard/skip the
                        // clipped voxel so meshes handle it alone.
                        if (!anyLayerOn)
                        {
                            p += step;
                            continue;
                        }

                        if (sliceDrawn) break;

                        half ds = tex3Dlod(_Volume, float4(p, 0)).r;
                        // Only paint slice if we're on actual tissue. The
                        // threshold is generous so soft tissue shows up,
                        // but noise-level (<_Threshold) stays transparent.
                        if (ds > _Threshold * 2.0)
                        {
                            half wMin = _WindowCenter - _WindowWidth * 0.5;
                            half wMax = _WindowCenter + _WindowWidth * 0.5;
                            half brightness = saturate((ds - wMin) / max(wMax - wMin, 0.001));
                            half3 tint = half3(1.00, 0.92, 0.82);
                            col.rgb += (1.0 - col.a) * brightness * tint;
                            col.a    = 1.0;
                            sliceDrawn = true;
                            break;
                        }
                        // Air past the plane — skip without drawing
                        p += step;
                        continue;
                    }

                    half d = tex3Dlod(_Volume, float4(p, 0)).r;

                    // ── Empty-space skipping ─────────────────────────
                    // If density is below threshold, advance faster.
                    // After 2+ consecutive empty samples, take a double step.
                    if (d <= _Threshold)
                    {
                        emptyRun++;
                        if (emptyRun >= 2)
                        {
                            p += step;  // extra step (total 2x advance)
                            s++;        // count the extra step
                        }
                        p += step;
                        continue;
                    }
                    emptyRun = 0;

                    // ── Tissue color rendering ─────────────────────
                    // (Grayscale mode branch removed — the _GrayscaleMode
                    //  uniform is kept as a legacy no-op so cached builds
                    //  don't crash. Cut-plane slice still uses the
                    //  _WindowCenter/_WindowWidth uniforms above.)
                    {
                        // Layer check FIRST — skip gradient if no layer matches (saves 3 tex reads)
                        half3 c  = half3(d, d, d);
                        half  la = 1.0;
                        bool  hit = false;
                        half  gradMix = _GradOpacity;

                        // Priority: Bone → Vasculature → Nerves
                        if (_L0On > 0.5 && d >= _L0Min && d <= _L0Max)
                        {
                            c = _L0Col.rgb; la = _L0Col.a * 1.2; hit = true;
                            gradMix = _GradOpacity * 0.1;
                        }
                        else if (_L1On > 0.5 && d >= _L1Min && d <= _L1Max)
                        {
                            half vascT = saturate((d - _L1Min) / max(_L1Max - _L1Min, 0.001));
                            // Color ramp: bright scarlet for low density (small vessels,
                            // contrast-edge HU) → deep maroon for high density (big vessel
                            // cores). Gives visual weight to major arteries.
                            half3 vascLo = half3(0.98, 0.25, 0.18);  // bright scarlet
                            half3 vascHi = half3(0.38, 0.02, 0.02);  // deep maroon
                            c = lerp(vascLo, vascHi, vascT);
                            la = _L1Col.a; hit = true;
                            la *= smoothstep(0.0, 0.5, vascT) * 1.8;
                            gradMix = _GradOpacity * 0.4;
                        }
                        else if (_L2On > 0.5 && d >= _L2Min && d <= _L2Max)
                        {
                            c = _L2Col.rgb; la = _L2Col.a * 1.5; hit = true;
                            gradMix = _GradOpacity * 0.3;  // less gradient shading = brighter nerves
                        }
                        else if (_L3On > 0.5 && d >= _L3Min && d <= _L3Max)
                        {
                            c = _L3Col.rgb; la = _L3Col.a * 1.0; hit = true;
                            gradMix = _GradOpacity * 0.2;  // subtle gradient for muscle mass
                        }
                        if (hit)
                        {
                            // Forward-difference gradient (3 tex reads)
                            half3 gNorm;
                            half gm = gradientAndNormal(p, texSz, d, gNorm);
                            half gradFactor = saturate(gm * _GradBoost);
                            half opacityMod = lerp(1.0, gradFactor, gradMix);

                            // Pseudo-lighting
                            half NdotV = abs(dot(gNorm, half3(-rd)));
                            half lighting = 0.45 + 0.55 * NdotV;
                            c *= lighting;

                            half a = saturate(d * _AlphaScale * la * opacityMod * dt * 8.0);
                            if (a > 0.003)
                            {
                                col.rgb += (1.0 - col.a) * c * a;
                                col.a   += (1.0 - col.a) * a;
                            }
                        }
                    } // end tissue color block

                    if (col.a > 0.95) break;
                    p += step;
                }

                if (col.a < 0.005) discard;
                return col;
            }
            ENDCG
        }
    }
    Fallback Off
}
