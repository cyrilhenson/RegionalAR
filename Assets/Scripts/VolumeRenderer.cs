/// RegionalAR — Volume Renderer
/// Loads .vol (OVOL) files, drives the VolumeRaymarch shader, and manages
/// tissue-layer presets + crop bounds + dual cut planes.
///
/// Public API (used by WiFiDownloader + UI + HandInteraction):
///   ReloadVolume(string path)
///   PositionInFrontOfUser()
///   SetLayerEnabled(int layer, bool on)
///   SetCrop(Vector3 min, Vector3 max)
///   ResetCrop()
///   SetCutPlane(int idx, Vector3 worldNormal, float dist)
///   SetCutPlaneLocal(int idx, Vector3 localNormal, float dist)
///   ClearCutPlane(int idx)
///   ClearAllCutPlanes()
///   SetCutPlaneLocked(int idx, bool locked)

using System;
using System.Collections;
using System.IO;
using UnityEngine;

[RequireComponent(typeof(MeshRenderer))]
public class VolumeRenderer : MonoBehaviour
{
    [Header("Material (auto-found if empty)")]
    public Material volumeMaterial;

    [Header("Rendering")]
    public float AlphaScale    = 1.5f;
    public float DensityThresh = 0.01f;
    public float GradOpacity   = 0.6f;   // how much gradient modulates opacity (0=off, 1=full)
    public float GradBoost     = 3.0f;   // multiplier for gradient magnitude

    // Window/level parameters used ONLY for the cut-plane slice view.
    // The user-facing grayscale MRI mode was removed — these stay fixed
    // at radiological default values, hardcoded into the shader via
    // PushGrayscaleToMaterial below.
    const float windowCenter = 0.4f;
    const float windowWidth  = 0.8f;

    // ── Tissue layer state (matches shader _L0.._L2) ─────────────
    [System.Serializable]
    public class TissueLayer
    {
        public string name;
        public bool   enabled;
        public float  densityMin;
        public float  densityMax;
        public Color  color;
    }

    // HU → normalized: (HU + 1024) / 3072
    // Bone:        HU  700-2048 → 0.561-1.000  (cortical + dense cancellous)
    // Vasculature: HU  150- 400 → 0.382-0.464  (contrast-enhanced vessels)
    // Nerves:      HU  110- 150 → 0.369-0.382  (AI-remapped neural tissue)
    // (Muscle layer removed — produced unusable red haze even at low alpha)
    public TissueLayer[] layers = new TissueLayer[]
    {
        new TissueLayer { name = "Bone",        enabled = true, densityMin = 0.561f, densityMax = 1.000f, color = new Color(0.95f,0.92f,0.85f,1f) },
        new TissueLayer { name = "Vasculature", enabled = true, densityMin = 0.382f, densityMax = 0.464f, color = new Color(0.95f,0.15f,0.10f,0.95f) },
        new TissueLayer { name = "Nerves",      enabled = true, densityMin = 0.369f, densityMax = 0.382f, color = new Color(1f,0.95f,0.2f,0.90f) },
    };

    // Crop bounds (UV 0-1)
    [HideInInspector] public Vector3 cropMin = Vector3.zero;
    [HideInInspector] public Vector3 cropMax = Vector3.one;

    // ── Dual cut planes ──────────────────────────────────────────
    // Each plane: active, locked, normal (object-local), dist
    // dist=1.0 means "off" — max dot with unit normal in [-0.5,0.5] cube is 0.5,
    // so nothing clips when dist >= 0.6
    [System.Serializable]
    public class CutPlane
    {
        public bool    active;
        public bool    locked;     // when locked, plane stays put during grab/scale
        public Vector3 normal = Vector3.up;
        public float   dist   = 1.0f;
    }

    [HideInInspector]
    public CutPlane[] cutPlanes = new CutPlane[]
    {
        new CutPlane(),
        new CutPlane()
    };

    // Internal
    Texture3D    _tex3D;
    MeshRenderer _mr;
    Material     _matInstance;  // runtime material instance

    // Stable container: mesh child objects parent to this instead of
    // the raw volume transform. LateUpdate adjusts its local transform
    // to match the smoothed pose, so meshes render jitter-free while
    // the actual volume transform stays "raw" for interaction/grab.
    Transform _stableContainer;

    /// Mesh loaders call this to get the transform they should parent to.
    /// Returns the stabilized container (created in Start). Falls back to
    /// this transform if the container hasn't been created yet.
    public Transform GetStableParent()
    {
        return _stableContainer != null ? _stableContainer : transform;
    }

    // ═════════════════════════════════════════════════════════════════
    void Start()
    {
        _mr = GetComponent<MeshRenderer>();

        // Create stable container BEFORE mesh loaders so they can parent to it
        var go = new GameObject("StableContainer");
        _stableContainer = go.transform;
        _stableContainer.SetParent(transform, worldPositionStays: false);
        _stableContainer.localPosition = Vector3.zero;
        _stableContainer.localRotation = Quaternion.identity;
        _stableContainer.localScale    = Vector3.one;

        // Force layer array to match code defaults — Unity serialization
        // may cache old 4-layer values from the scene file.
        ForceLayerDefaults();

        // Auto-attach spatial anchor stabilizer for world-locked positioning
        if (GetComponent<SpatialAnchorStabilizer>() == null)
            gameObject.AddComponent<SpatialAnchorStabilizer>();

        // Auto-attach marker manager for teaching annotations
        if (GetComponent<MarkerManager>() == null)
            gameObject.AddComponent<MarkerManager>();

        // Auto-attach bone-mesh loader (Phase 1 of mesh-based rendering).
        // Loads the .omsh file the desktop processor exports next to the .vol.
        if (GetComponent<BoneMeshLoader>() == null)
            gameObject.AddComponent<BoneMeshLoader>();

        // Auto-attach vessel-mesh loader (Phase 2 of mesh-based rendering).
        // Loads the .vmsh file the desktop processor exports next to the .vol.
        if (GetComponent<VesselMeshLoader>() == null)
            gameObject.AddComponent<VesselMeshLoader>();

        // Auto-attach nerve-mesh loader (Phase 3 of mesh-based rendering).
        // Loads the .nmsh file (AI-mode only; no fallback — nerves invisible on CT).
        if (GetComponent<NerveMeshLoader>() == null)
            gameObject.AddComponent<NerveMeshLoader>();

        // Auto-attach muscle-mesh loader (Phase 4 of mesh-based rendering).
        // Loads the .mmsh file (AI-mode only; discrete muscle groups).
        if (GetComponent<MuscleMeshLoader>() == null)
            gameObject.AddComponent<MuscleMeshLoader>();

        StartCoroutine(InitAfterXR());
    }

    void ForceLayerDefaults()
    {
        // Always overwrite with code-defined layers to avoid stale serialized data
        layers = new TissueLayer[]
        {
            new TissueLayer { name = "Bone",        enabled = true, densityMin = 0.561f, densityMax = 1.000f, color = new Color(0.95f,0.92f,0.85f,1f) },
            new TissueLayer { name = "Vasculature", enabled = true, densityMin = 0.382f, densityMax = 0.464f, color = new Color(0.95f,0.15f,0.10f,0.95f) },
            new TissueLayer { name = "Nerves",      enabled = true, densityMin = 0.369f, densityMax = 0.382f, color = new Color(1f,0.95f,0.2f,0.90f) },
        };
        Debug.Log("[RegionalAR] Layer defaults applied: 3 layers (Bone, Vasc, Nerves)");
    }

    IEnumerator InitAfterXR()
    {
        // Start with nothing rendered — the user picks a volume from the
        // library or receives one over WiFi. No auto-load of leftover files.
        if (_mr != null) _mr.enabled = false;
        Debug.Log("[RegionalAR] Startup: no volume loaded — waiting for user selection");
        yield break;
    }

    // Get the head transform using the most direct path available.
    // Prefers OVRCameraRig.centerEyeAnchor (a Transform, always present on Quest)
    // over cached cameras which can go stale if the rig is rebuilt.
    Transform GetHeadTransform()
    {
        var rig = FindObjectOfType<OVRCameraRig>();
        if (rig != null && rig.centerEyeAnchor != null)
            return rig.centerEyeAnchor;
        Camera cam = Camera.main ?? FindObjectOfType<Camera>();
        return cam != null ? cam.transform : null;
    }


    // ── Render-pose stabilization ──────────────────────────────────
    // Smooths the object-to-world matrix sent to the shader to absorb
    // sub-frame jitter from tracking/frame drops without affecting
    // interaction (grab, crop, etc. still use the real transform).
    Vector3    _stablePos;
    Quaternion _stableRot = Quaternion.identity;
    bool       _stableInit;
    const float STABLE_POS_SPEED = 10f;  // lower = more smoothing/less jitter (was 25)
    const float STABLE_ROT_SPEED = 10f;

    // Push cut planes + stabilized matrices to material every frame
    void Update()
    {
        PushCutPlanesToMaterial();
    }

    void LateUpdate()
    {
        StabilizeRenderPose();
    }

    void StabilizeRenderPose()
    {
        if (_matInstance == null) return;

        Vector3 realPos = transform.position;
        Quaternion realRot = transform.rotation;

        if (!_stableInit)
        {
            _stablePos = realPos;
            _stableRot = realRot;
            _stableInit = true;
        }
        else
        {
            float dt = Time.deltaTime;
            float tPos = 1f - Mathf.Exp(-STABLE_POS_SPEED * dt);
            float tRot = 1f - Mathf.Exp(-STABLE_ROT_SPEED * dt);
            _stablePos = Vector3.Lerp(_stablePos, realPos, tPos);
            _stableRot = Quaternion.Slerp(_stableRot, realRot, tRot);
        }

        // Build stabilized world-to-object matrix and push to shader
        // This makes the volume render at the smoothed position while
        // Unity's transform stays at the real position for interaction
        Matrix4x4 stableW2O = Matrix4x4.TRS(_stablePos, _stableRot, transform.localScale).inverse;
        _matInstance.SetMatrix("_StableWorldToObject", stableW2O);

        // Move the stable container so mesh children also render at the
        // smoothed pose. The container is a child of the raw transform,
        // so we set its local offset to compensate for the jitter delta.
        if (_stableContainer != null)
        {
            float scale = transform.localScale.x;
            float invScale = (scale > 0.001f) ? (1f / scale) : 1f;
            _stableContainer.localPosition =
                Quaternion.Inverse(realRot) * (_stablePos - realPos) * invScale;
            _stableContainer.localRotation =
                Quaternion.Inverse(realRot) * _stableRot;
        }
    }

    /// Call when volume is repositioned externally (grab release, bring here, etc.)
    public void SnapStablePose()
    {
        _stablePos = transform.position;
        _stableRot = transform.rotation;
        // Reset container to identity so meshes snap to the new pose instantly
        if (_stableContainer != null)
        {
            _stableContainer.localPosition = Vector3.zero;
            _stableContainer.localRotation = Quaternion.identity;
        }
    }

    // ═════════════════════════════════════════════════════════════════
    //  PUBLIC API
    // ═════════════════════════════════════════════════════════════════

    public void ReloadVolume(string path) => StartCoroutine(LoadAndApply(path));
    public void ReloadVolume()
    {
        string p = Path.Combine(Application.persistentDataPath, "head_neck.vol");
        ReloadVolume(p);
    }

    /// <summary>
    /// Synchronous load — throws on failure. Use from coroutines that need
    /// to know whether the load actually succeeded.
    /// </summary>
    public void ReloadVolumeSync(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Volume file not found: {path}");
        LoadOVOL(path);
        TryLoadCompanionMesh(path);
        Debug.Log($"[RegionalAR] ReloadVolumeSync OK: {path}");
    }

    public void PositionInFrontOfUser()
    {
        Transform headT = GetHeadTransform();
        if (headT == null)
        {
            Debug.LogWarning("[RegionalAR] PositionInFrontOfUser: no head transform");
            return;
        }

        Vector3 fwd = headT.forward; fwd.y = 0; fwd.Normalize();
        if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;

        // Place 65cm in front of the user, 5cm below eye level
        Vector3 pos = headT.position + fwd * 0.65f;
        pos.y = headT.position.y - 0.05f;
        // Safety clamp: volume should never appear below knee level
        pos.y = Mathf.Max(pos.y, 0.7f);

        transform.position = pos;
        transform.rotation = Quaternion.LookRotation(-fwd);
        SnapStablePose();
        ReAnchor();
        Debug.Log($"[RegionalAR] PositionInFrontOfUser: head={headT.position} fwd={fwd} → vol={pos}");
    }

    /// <summary>
    /// Destroy the old spatial anchor and create a new one at the current
    /// position. Must be called after any external reposition (load, bring
    /// here, reset) so the anchor doesn't drag the volume back to its
    /// previous world-locked position.
    /// </summary>
    public void ReAnchor()
    {
        var sas = GetComponent<SpatialAnchorStabilizer>();
        if (sas != null) sas.CreateAnchor();
    }

    public void SetLayerEnabled(int idx, bool on)
    {
        if (idx < 0 || idx >= layers.Length) return;
        layers[idx].enabled = on;
        PushLayersToMaterial();
    }

    public bool IsLayerEnabled(int idx)
    {
        return idx >= 0 && idx < layers.Length && layers[idx].enabled;
    }

    public void SetCrop(Vector3 min, Vector3 max)
    {
        cropMin = min; cropMax = max;
        PushCropToMaterial();
    }

    public void ResetCrop()
    {
        cropMin = Vector3.zero; cropMax = Vector3.one;
        PushCropToMaterial();
        ClearAllCutPlanes();
    }

    // ── Cut Plane API (dual) ─────────────────────────────────────

    /// <summary>
    /// Set a free-angle cut plane by index (0 or 1). Normal is in WORLD space —
    /// converted to object-local automatically. dist is how deep into the volume
    /// (0 = center, positive = toward normal direction, range ~-0.5..0.5).
    /// </summary>
    public void SetCutPlane(int idx, Vector3 worldNormal, float dist)
    {
        if (idx < 0 || idx >= cutPlanes.Length) return;
        var cp = cutPlanes[idx];
        cp.active = true;
        // Use pure quaternion inverse to convert world→local normal
        // (InverseTransformDirection can misbehave after scale changes)
        cp.normal = (Quaternion.Inverse(transform.rotation) * worldNormal).normalized;
        cp.dist   = dist;
    }

    /// <summary>
    /// Set cut plane directly in object-local space (no conversion).
    /// </summary>
    public void SetCutPlaneLocal(int idx, Vector3 localNormal, float dist)
    {
        if (idx < 0 || idx >= cutPlanes.Length) return;
        var cp = cutPlanes[idx];
        cp.active = true;
        cp.normal = localNormal.normalized;
        cp.dist   = dist;
    }

    public void ClearCutPlane(int idx)
    {
        if (idx < 0 || idx >= cutPlanes.Length) return;
        var cp = cutPlanes[idx];
        cp.active = false;
        cp.locked = false;
        cp.dist   = 1.0f;  // above max dot → nothing clips (shader uses >)
    }

    public void ClearAllCutPlanes()
    {
        for (int i = 0; i < cutPlanes.Length; i++)
            ClearCutPlane(i);
    }

    public void SetCutPlaneLocked(int idx, bool locked)
    {
        if (idx < 0 || idx >= cutPlanes.Length) return;
        cutPlanes[idx].locked = locked;
    }

    // ═════════════════════════════════════════════════════════════════
    //  FILE IO
    // ═════════════════════════════════════════════════════════════════

    /// Kick the companion mesh loaders. Silent no-op if either file missing.
    /// When a mesh loads successfully, we auto-enable it AND turn the
    /// matching volume layer off so the two don't overlap. If a mesh is
    /// missing, the matching volume layer stays on as the fallback.
    void TryLoadCompanionMesh(string volPath)
    {
        // ── Clear all old meshes first so stale data doesn't persist ──
        var bml = GetComponent<BoneMeshLoader>();
        if (bml != null) bml.SetMeshEnabled(false);
        var vml = GetComponent<VesselMeshLoader>();
        if (vml != null) vml.SetMeshEnabled(false);
        var nml = GetComponent<NerveMeshLoader>();
        if (nml != null) nml.SetMeshEnabled(false);
        var mml = GetComponent<MuscleMeshLoader>();
        if (mml != null) mml.SetMeshEnabled(false);

        // ── Re-enable volume layers as fallback (mesh load overrides below) ──
        if (layers != null)
        {
            for (int i = 0; i < layers.Length; i++)
                SetLayerEnabled(i, true);
        }

        // ── Now try loading new companion meshes ──
        if (bml != null)
        {
            bml.TryLoadCompanion(volPath);
            if (bml.HasMesh)
            {
                bml.SetMeshEnabled(true);
                if (layers != null && layers.Length > 0)
                    SetLayerEnabled(0, false);   // volume bone off
            }
        }
        if (vml != null)
        {
            vml.TryLoadCompanion(volPath);
            if (vml.HasMesh)
            {
                vml.SetMeshEnabled(true);
                if (layers != null && layers.Length > 1)
                    SetLayerEnabled(1, false);   // volume vasc off
            }
        }
        if (nml != null)
        {
            nml.TryLoadCompanion(volPath);
            if (nml.HasMesh)
            {
                nml.SetMeshEnabled(true);
                if (layers != null && layers.Length > 2)
                    SetLayerEnabled(2, false);   // volume nerves off
            }
        }
        if (mml != null)
        {
            mml.TryLoadCompanion(volPath);
            if (mml.HasMesh)
                mml.SetMeshEnabled(true);    // no volume layer for muscle
        }
    }

    IEnumerator LoadAndApply(string path)
    {
        yield return null;
        if (!File.Exists(path)) { Debug.LogError($"[RegionalAR] Not found: {path}"); yield break; }
        try   { LoadOVOL(path); TryLoadCompanionMesh(path); Debug.Log($"[RegionalAR] Reloaded: {path}"); }
        catch (Exception e) { Debug.LogError($"[RegionalAR] Reload failed: {e}"); }
    }

    // ═════════════════════════════════════════════════════════════════
    //  OVOL 32-byte header
    // ═════════════════════════════════════════════════════════════════
    void LoadOVOL(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var br = new BinaryReader(fs);

        if (new string(br.ReadChars(4)) != "OVOL")
            throw new InvalidDataException("Bad magic");
        uint ver = br.ReadUInt32();
        int W = (int)br.ReadUInt32(), H = (int)br.ReadUInt32(), D = (int)br.ReadUInt32();
        uint dtype = br.ReadUInt32(); br.ReadUInt32(); br.ReadUInt32();

        if (ver != 1) throw new InvalidDataException($"Version {ver}");
        if (dtype != 0) throw new InvalidDataException($"Dtype {dtype}");

        byte[] voxels = br.ReadBytes(W * H * D);
        Debug.Log($"[RegionalAR] OVOL {W}x{H}x{D}");
        ApplyTexture3D(voxels, W, H, D);
    }

    // ═════════════════════════════════════════════════════════════════
    //  TEXTURE / MATERIAL
    // ═════════════════════════════════════════════════════════════════
    void ApplyTexture3D(byte[] voxels, int W, int H, int D)
    {
        if (_tex3D != null) Destroy(_tex3D);
        _tex3D = new Texture3D(W, H, D, TextureFormat.R8, false);
        _tex3D.wrapMode   = TextureWrapMode.Clamp;
        _tex3D.filterMode = FilterMode.Bilinear;
        _tex3D.SetPixelData(voxels, 0);
        _tex3D.Apply();

        // Auto-set ray step count based on volume resolution.
        // 128³ → 48 steps (light), 256³ → 96 steps (detailed), scales linearly.
        int maxDim = Mathf.Max(W, Mathf.Max(H, D));
        _autoSteps = Mathf.Clamp(Mathf.RoundToInt(maxDim * 0.375f), 32, 128);
        Debug.Log($"[RegionalAR] Volume {W}x{H}x{D} → {_autoSteps} ray steps");

        // Re-enable renderer (disabled at startup until first volume load)
        if (_mr != null) _mr.enabled = true;

        PushToMaterial();
    }

    int _autoSteps = 48;

    void PushToMaterial()
    {
        EnsureMaterial();
        if (_matInstance == null) return;

        _matInstance.SetTexture("_Volume",     _tex3D);
        _matInstance.SetFloat  ("_AlphaScale", AlphaScale);
        _matInstance.SetFloat  ("_Threshold",  DensityThresh);
        _matInstance.SetFloat  ("_GradOpacity", GradOpacity);
        _matInstance.SetFloat  ("_GradBoost",   GradBoost);
        _matInstance.SetFloat  ("_Steps",       _autoSteps);
        PushGrayscaleToMaterial();
        PushLayersToMaterial();
        PushCropToMaterial();
        PushCutPlanesToMaterial();

        Debug.Log("[RegionalAR] Material updated" +
                  (_matInstance.shader.isSupported ? "" : " — WARNING: shader NOT supported on this GPU!"));
    }

    void PushLayersToMaterial()
    {
        if (_matInstance == null) return;
        string[] pfx = { "_L0", "_L1", "_L2" };
        for (int i = 0; i < Mathf.Min(layers.Length, pfx.Length); i++)
        {
            var L = layers[i];
            _matInstance.SetFloat(pfx[i] + "On",  L.enabled ? 1f : 0f);
            _matInstance.SetFloat(pfx[i] + "Min", L.densityMin);
            _matInstance.SetFloat(pfx[i] + "Max", L.densityMax);
            _matInstance.SetColor(pfx[i] + "Col", L.color);
        }
    }

    void PushCropToMaterial()
    {
        if (_matInstance == null) return;
        _matInstance.SetVector("_CropMin", new Vector4(cropMin.x, cropMin.y, cropMin.z, 0));
        _matInstance.SetVector("_CropMax", new Vector4(cropMax.x, cropMax.y, cropMax.z, 0));
    }

    void PushCutPlanesToMaterial()
    {
        // Re-create material if lost (can happen after transform changes)
        if (_matInstance == null) { EnsureMaterial(); if (_matInstance == null) return; }

        // Re-apply material to renderer if Unity detached it
        if (_mr != null && _mr.sharedMaterial != _matInstance)
            _mr.material = _matInstance;

        // Plane 1
        // Negate BOTH normal and dist for active planes:
        //   Normal stored = away-from-camera.  Shader receives toward-camera.
        //   dot(pC, toward_camera) > 0  clips the NEAR half (user peeks inside).
        //   Negated dist preserves "push deeper = more clipping" feel.
        //   Off = original normal, dist=1.0 → dot > 1.0 = never clips.
        var cp1 = cutPlanes[0];
        Vector4 n1; float d1;
        if (cp1.active) {
            n1 = new Vector4(-cp1.normal.x, -cp1.normal.y, -cp1.normal.z, 0);
            d1 = -cp1.dist;
        } else {
            n1 = new Vector4(cp1.normal.x, cp1.normal.y, cp1.normal.z, 0);
            d1 = 1.0f;
        }
        _matInstance.SetVector("_CutPlane1N", n1);
        _matInstance.SetFloat ("_CutPlane1D", d1);

        // Plane 2 — same dual negation
        var cp2 = cutPlanes[1];
        Vector4 n2; float d2;
        if (cp2.active) {
            n2 = new Vector4(-cp2.normal.x, -cp2.normal.y, -cp2.normal.z, 0);
            d2 = -cp2.dist;
        } else {
            n2 = new Vector4(cp2.normal.x, cp2.normal.y, cp2.normal.z, 0);
            d2 = 1.0f;
        }
        _matInstance.SetVector("_CutPlane2N", n2);
        _matInstance.SetFloat ("_CutPlane2D", d2);

        // Mirror the same planes onto every mesh material so cut planes
        // affect the volume raymarch, the bone mesh, and the vessel mesh
        // uniformly. Helper method keeps the uniform-push symmetrical.
        PushPlanesTo(GetComponent<BoneMeshLoader>()?.GetMeshMaterial(),   n1, d1, n2, d2);
        PushPlanesTo(GetComponent<VesselMeshLoader>()?.GetMeshMaterial(), n1, d1, n2, d2);
        PushPlanesTo(GetComponent<NerveMeshLoader>()?.GetMeshMaterial(),  n1, d1, n2, d2);
        PushPlanesTo(GetComponent<MuscleMeshLoader>()?.GetMeshMaterial(), n1, d1, n2, d2);
    }

    static void PushPlanesTo(Material m, Vector4 n1, float d1, Vector4 n2, float d2)
    {
        if (m == null) return;
        if (m.HasProperty("_CutPlane1N")) m.SetVector("_CutPlane1N", n1);
        if (m.HasProperty("_CutPlane1D")) m.SetFloat ("_CutPlane1D", d1);
        if (m.HasProperty("_CutPlane2N")) m.SetVector("_CutPlane2N", n2);
        if (m.HasProperty("_CutPlane2D")) m.SetFloat ("_CutPlane2D", d2);
    }

    // Pushes fixed window/level values used ONLY by the cut-plane slice
    // view in VolumeRaymarch.shader. No user-tunable grayscale mode.
    void PushGrayscaleToMaterial()
    {
        if (_matInstance == null) return;
        _matInstance.SetFloat("_GrayscaleMode", 0f);              // always tissue mode
        _matInstance.SetFloat("_WindowCenter",  windowCenter);
        _matInstance.SetFloat("_WindowWidth",   windowWidth);
    }

    void EnsureMaterial()
    {
        if (_matInstance != null) return;
        if (volumeMaterial != null)
            _matInstance = new Material(volumeMaterial);  // runtime copy
        else if (_mr.sharedMaterial != null)
            _matInstance = new Material(_mr.sharedMaterial);
        else
        {
            // Last resort: find the shader and create material from scratch
            Shader sh = Shader.Find("RegionalAR/VolumeRaymarch");
            if (sh != null)
                _matInstance = new Material(sh);
            else
            {
                Debug.LogError("[RegionalAR] Shader 'RegionalAR/VolumeRaymarch' not found!");
                return;
            }
        }
        _mr.material = _matInstance;
    }

}
