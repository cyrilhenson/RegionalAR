/// RegionalAR — Nerve Mesh Loader (Phase 3 of mesh-based rendering)
///
/// Loads a companion .nmsh file alongside the .vol. Structurally identical
/// to VesselMeshLoader — same .omsh binary format, same [-0.5, 0.5]³ coord
/// space, same cut-plane sync pattern. Nerve meshes only exist when AI
/// mode produced anatomical landmarks (spinal cord + root zones).

using System;
using System.IO;
using UnityEngine;

[RequireComponent(typeof(VolumeRenderer))]
public class NerveMeshLoader : MonoBehaviour
{
    [Header("Appearance")]
    public Color nerveColor = new Color(0.90f, 0.72f, 0.20f, 1f);  // warm amber

    GameObject _meshGO;
    MeshFilter _mf;
    MeshRenderer _mr;
    Mesh _mesh;
    string _lastAttemptedPath = "";
    string _lastError         = "";

    public bool HasMesh => _mesh != null && _mesh.vertexCount > 0;
    public bool IsMeshEnabled => _meshGO != null && _meshGO.activeSelf;

    public string ShortStateTag()
    {
        if (HasMesh) return "";
        if (!string.IsNullOrEmpty(_lastError))
        {
            if (_lastError == "not found")          return "(no file)";
            if (_lastError.StartsWith("load err"))  return "(parse err)";
            if (_lastError.StartsWith("empty"))     return "(bad path)";
            return "(err)";
        }
        return "(never tried)";
    }

    public Material GetMeshMaterial() => _mr != null ? _mr.sharedMaterial : null;

    public bool TryLoadCompanion(string volPath)
    {
        _lastError = "";
        _lastAttemptedPath = "";
        if (string.IsNullOrEmpty(volPath)) { _lastError = "empty vol path"; return false; }
        string meshPath = Path.ChangeExtension(volPath, ".nmsh");
        _lastAttemptedPath = meshPath;
        if (!File.Exists(meshPath))
        {
            _lastError = "not found";
            Debug.Log($"[RegionalAR][NMesh] No companion nerve mesh at {meshPath}");
            return false;
        }
        try { LoadOMSH(meshPath); Debug.Log($"[RegionalAR][NMesh] Loaded {meshPath}"); return true; }
        catch (Exception e)
        {
            _lastError = $"load err: {e.Message}";
            Debug.LogWarning($"[RegionalAR][NMesh] Failed to load {meshPath}: {e.Message}");
            return false;
        }
    }

    public void SetMeshEnabled(bool on) { if (_meshGO != null) _meshGO.SetActive(on && HasMesh); }

    void LoadOMSH(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var br = new BinaryReader(fs);

        if (new string(br.ReadChars(4)) != "OMSH")
            throw new InvalidDataException("Bad magic — expected OMSH");
        uint ver = br.ReadUInt32();
        if (ver != 1) throw new InvalidDataException($"Unsupported .nmsh version {ver}");

        int nV = (int)br.ReadUInt32();
        int nT = (int)br.ReadUInt32();
        if (nV <= 0 || nT <= 0) throw new InvalidDataException($"Empty mesh (v={nV}, t={nT})");

        byte[] posBytes = br.ReadBytes(nV * 12);
        byte[] nrmBytes = br.ReadBytes(nV * 12);
        byte[] idxBytes = br.ReadBytes(nT * 12);
        if (posBytes.Length != nV * 12 || nrmBytes.Length != nV * 12 || idxBytes.Length != nT * 12)
            throw new InvalidDataException("Truncated .nmsh");

        float[] posF = new float[nV * 3]; Buffer.BlockCopy(posBytes, 0, posF, 0, posBytes.Length);
        Vector3[] positions = new Vector3[nV];
        for (int i = 0; i < nV; i++) positions[i] = new Vector3(posF[i*3], posF[i*3+1], posF[i*3+2]);

        float[] nrmF = new float[nV * 3]; Buffer.BlockCopy(nrmBytes, 0, nrmF, 0, nrmBytes.Length);
        Vector3[] normals = new Vector3[nV];
        for (int i = 0; i < nV; i++) normals[i] = new Vector3(nrmF[i*3], nrmF[i*3+1], nrmF[i*3+2]);

        int[] indices = new int[nT * 3]; Buffer.BlockCopy(idxBytes, 0, indices, 0, idxBytes.Length);

        if (_mesh != null) Destroy(_mesh);
        _mesh = new Mesh { name = "RegionalAR_Nerve" };
        _mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        _mesh.SetVertices(positions);
        _mesh.SetNormals(normals);
        _mesh.SetTriangles(indices, 0, calculateBounds: true);
        _mesh.RecalculateBounds();

        EnsureMeshObject();
        _mf.sharedMesh = _mesh;
        Debug.Log($"[RegionalAR][NMesh] loaded — {nV:N0} verts, {nT:N0} tris");
    }

    void EnsureMeshObject()
    {
        if (_meshGO != null && _mf != null && _mr != null) return;

        _meshGO = new GameObject("NerveMesh");
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

        Shader sh = Resources.Load<Shader>("NerveMatte");
        string via = "Resources/NerveMatte";
        if (sh == null) { sh = Shader.Find("RegionalAR/NerveMatte"); via = "Shader.Find(RegionalAR/NerveMatte)"; }
        if (sh == null) { sh = Shader.Find("Unlit/Color");         via = "FALLBACK Unlit/Color"; }
        Debug.Log($"[RegionalAR][NMesh] Nerve shader resolved via: {via} (shader={(sh != null ? sh.name : "NULL")})");

        var mat = new Material(sh);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", nerveColor);
        if (mat.HasProperty("_Cull"))  mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
        _mr.sharedMaterial = mat;

        _meshGO.SetActive(false);
    }
}
