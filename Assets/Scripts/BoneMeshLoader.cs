/// RegionalAR — Bone Mesh Loader (Phase 1 of mesh-based rendering)
///
/// Loads a companion .omsh file alongside the .vol and displays the
/// pre-extracted bone surface as solid geometry. All heavy lifting
/// (marching cubes, Taubin smoothing, decimation) happens on the
/// desktop processor — Quest 3 just uploads the finished mesh to GPU.
///
/// .omsh binary format (little-endian):
///   [ 4B ] magic        = "OMSH"
///   [ 4B ] version      (uint32)
///   [ 4B ] num_verts    (uint32)
///   [ 4B ] num_tris     (uint32)
///   [ num_verts * 12B ] positions (float32 xyz)
///   [ num_verts * 12B ] normals   (float32 xyz)
///   [ num_tris  * 12B ] indices   (uint32  abc)
///
/// Coordinates are already in the [-0.5, 0.5]^3 local space used by
/// the volume cube, so the child renderer drops in with identity transform.
///
/// Public API:
///   TryLoadCompanion(string volPath) — looks for "<volPath>.omsh" and loads
///   SetMeshEnabled(bool)             — toggle visibility for A/B compare
///   HasMesh                          — property

using System;
using System.IO;
using UnityEngine;

[RequireComponent(typeof(VolumeRenderer))]
public class BoneMeshLoader : MonoBehaviour
{
    [Header("Appearance")]
    // Tuned to match real cadaver bone in passthrough AR — full white
    // reads as "overexposed" through the Quest's passthrough cameras.
    public Color boneColor  = new Color(0.80f, 0.74f, 0.66f, 1f);
    public float smoothness = 0.10f;
    public float metallic   = 0.05f;

    GameObject _meshGO;
    MeshFilter _mf;
    MeshRenderer _mr;
    Mesh _mesh;
    // Last attempt's outcome — surfaced in the UI so we can debug without adb.
    string _lastAttemptedPath = "";
    string _lastError         = "";

    public bool HasMesh => _mesh != null && _mesh.vertexCount > 0;

    /// Short tag (<20 chars) for the BONE: mode button label so errors
    /// are visible without opening the WiFi panel.
    public string ShortStateTag()
    {
        if (HasMesh) return "";
        if (!string.IsNullOrEmpty(_lastError))
        {
            // Map common errors to compact tags
            if (_lastError == "not found")          return "(no file)";
            if (_lastError.StartsWith("load err"))  return "(parse err)";
            if (_lastError.StartsWith("empty"))     return "(bad path)";
            return "(err)";
        }
        return "(never tried)";  // distinguishes from file-missing
    }

    // ── Public API ────────────────────────────────────────────────
    /// Try to find and load <volPath-without-extension>.omsh.
    /// Returns true on success. Silent no-op if the companion file is missing.
    public bool TryLoadCompanion(string volPath)
    {
        _lastError = "";
        _lastAttemptedPath = "";
        if (string.IsNullOrEmpty(volPath))
        {
            _lastError = "empty vol path";
            return false;
        }
        string meshPath = Path.ChangeExtension(volPath, ".omsh");
        _lastAttemptedPath = meshPath;
        if (!File.Exists(meshPath))
        {
            _lastError = $"not found";
            Debug.Log($"[RegionalAR][Mesh] No companion mesh at {meshPath}");
            return false;
        }
        try
        {
            LoadOMSH(meshPath);
            Debug.Log($"[RegionalAR][Mesh] Loaded {meshPath}");
            return true;
        }
        catch (Exception e)
        {
            _lastError = $"load err: {e.Message}";
            Debug.LogWarning($"[RegionalAR][Mesh] Failed to load {meshPath}: {e.Message}");
            return false;
        }
    }

    public void SetMeshEnabled(bool on)
    {
        if (_meshGO != null) _meshGO.SetActive(on && HasMesh);
    }

    public bool IsMeshEnabled => _meshGO != null && _meshGO.activeSelf;

    /// Expose the bone mesh material so VolumeRenderer can sync cut-plane
    /// parameters onto it every frame (same uniforms the volume shader uses).
    public Material GetMeshMaterial()
    {
        return _mr != null ? _mr.sharedMaterial : null;
    }

    // ── File IO ───────────────────────────────────────────────────
    void LoadOMSH(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var br = new BinaryReader(fs);

        if (new string(br.ReadChars(4)) != "OMSH")
            throw new InvalidDataException("Bad magic — expected OMSH");
        uint ver = br.ReadUInt32();
        if (ver != 1) throw new InvalidDataException($"Unsupported .omsh version {ver}");

        int nV = (int)br.ReadUInt32();
        int nT = (int)br.ReadUInt32();
        if (nV <= 0 || nT <= 0)
            throw new InvalidDataException($"Empty mesh (v={nV}, t={nT})");

        // Read all three arrays as raw byte blobs, then reinterpret via
        // float[] intermediate. Buffer.BlockCopy only works on primitive
        // arrays — Vector3 is a struct, so we go byte→float→Vector3.
        byte[] posBytes = br.ReadBytes(nV * 12);
        byte[] nrmBytes = br.ReadBytes(nV * 12);
        byte[] idxBytes = br.ReadBytes(nT * 12);

        if (posBytes.Length != nV * 12 || nrmBytes.Length != nV * 12 ||
            idxBytes.Length != nT * 12)
        {
            throw new InvalidDataException(
                $"Truncated .omsh: expected vp={nV*12} vn={nV*12} ti={nT*12}, "
                + $"got vp={posBytes.Length} vn={nrmBytes.Length} ti={idxBytes.Length}");
        }

        // Positions: bytes → float[] → Vector3[]
        float[] posF = new float[nV * 3];
        Buffer.BlockCopy(posBytes, 0, posF, 0, posBytes.Length);
        Vector3[] positions = new Vector3[nV];
        for (int i = 0; i < nV; i++)
            positions[i] = new Vector3(posF[i * 3], posF[i * 3 + 1], posF[i * 3 + 2]);

        // Normals: same pattern
        float[] nrmF = new float[nV * 3];
        Buffer.BlockCopy(nrmBytes, 0, nrmF, 0, nrmBytes.Length);
        Vector3[] normals = new Vector3[nV];
        for (int i = 0; i < nV; i++)
            normals[i] = new Vector3(nrmF[i * 3], nrmF[i * 3 + 1], nrmF[i * 3 + 2]);

        // Indices: .omsh stores uint32; Unity wants int[]. BlockCopy is fine
        // here since both source and destination are primitive arrays.
        int[] indices = new int[nT * 3];
        Buffer.BlockCopy(idxBytes, 0, indices, 0, idxBytes.Length);

        // Replace any previous mesh
        if (_mesh != null) Destroy(_mesh);
        _mesh = new Mesh { name = "RegionalAR_Bone" };
        // Enable 32-bit indices so we can exceed 65k verts
        _mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        _mesh.SetVertices(positions);
        _mesh.SetNormals(normals);
        _mesh.SetTriangles(indices, 0, calculateBounds: true);
        _mesh.RecalculateBounds();
        // Leave normals as-is (precomputed by desktop Taubin/area-weighted)

        EnsureMeshObject();
        _mf.sharedMesh = _mesh;
        Debug.Log($"[RegionalAR][Mesh] OMSH loaded — {nV:N0} verts, {nT:N0} tris");
    }

    void EnsureMeshObject()
    {
        if (_meshGO != null && _mf != null && _mr != null) return;

        _meshGO = new GameObject("BoneMesh");
        // Parent to the stabilized container so mesh renders jitter-free
        var vr = GetComponent<VolumeRenderer>();
        Transform stableParent = (vr != null) ? vr.GetStableParent() : transform;
        _meshGO.transform.SetParent(stableParent, worldPositionStays: false);
        _meshGO.transform.localPosition = Vector3.zero;
        _meshGO.transform.localRotation = Quaternion.identity;
        _meshGO.transform.localScale    = Vector3.one;

        _mf = _meshGO.AddComponent<MeshFilter>();
        _mr = _meshGO.AddComponent<MeshRenderer>();
        _mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _mr.receiveShadows    = false;

        // Load the matte bone shader. Resources.Load is the build-safe
        // path — Shader.Find alone is unreliable because Unity strips
        // shaders that aren't referenced by any scene material or
        // placed in a Resources folder.
        Shader sh = Resources.Load<Shader>("BoneMatte");
        string shaderPath = "Resources/BoneMatte";
        if (sh == null) { sh = Shader.Find("RegionalAR/BoneMatte"); shaderPath = "Shader.Find(RegionalAR/BoneMatte)"; }
        if (sh == null) { sh = Shader.Find("Unlit/Color");       shaderPath = "FALLBACK Unlit/Color"; }
        if (sh == null) { sh = Shader.Find("Sprites/Default");   shaderPath = "FALLBACK Sprites/Default"; }
        if (sh == null) { sh = Shader.Find("Standard");          shaderPath = "FALLBACK Standard"; }
        string shName = sh != null ? sh.name : "NULL";
        Debug.Log($"[RegionalAR][Mesh] Bone shader resolved via: {shaderPath} (shader={shName})");
        var mat = new Material(sh);
        if (mat.HasProperty("_Color"))      mat.SetColor("_Color", boneColor);
        // Deep crevice shade — independent of tint so we keep strong
        // contrast even if the tint is relatively bright. The shader's
        // default crevice color works well for every bone tint.
        if (mat.HasProperty("_CoreColor"))  mat.SetColor("_CoreColor",
            new Color(0.18f, 0.15f, 0.13f, 1f));

        // BoneMatte is double-sided. Other fallbacks may need manual unculling.
        if (mat.HasProperty("_Cull"))
            mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);

        _mr.sharedMaterial = mat;

        // Start hidden so volume-bone is the default until the user toggles.
        _meshGO.SetActive(false);
    }

    /// Diagnostic summary used by the WiFi panel to show mesh state on-device.
    public string StatusSummary()
    {
        if (HasMesh)
            return $"Bone mesh: {_mesh.vertexCount:N0} v, {_mesh.triangles.Length / 3:N0} t";
        if (!string.IsNullOrEmpty(_lastError))
        {
            // Trim the path to just the filename for readability on the panel
            string shortPath = _lastAttemptedPath;
            try { shortPath = Path.GetFileName(_lastAttemptedPath); } catch { }
            return $"Mesh FAIL: {_lastError} [{shortPath}]";
        }
        return "Bone mesh: load not attempted";
    }
}
