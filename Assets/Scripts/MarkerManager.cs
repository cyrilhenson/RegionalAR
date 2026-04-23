/// RegionalAR — Marker Manager
/// Teaching-focused annotation layer. Lets the instructor tag points on the
/// volume (e.g. "insert needle here") with small glowing spheres. Markers
/// are parented to the volume transform so they move/scale/rotate with it.
///
/// Public API (used by WiFiDownloader UI + HandInteraction):
///   ToggleMarkerMode()   — flips IsMarkerMode on/off
///   PlaceMarkerAt(worldPos, color)
///   UndoLast()           — removes the most recent marker
///   ClearAll()           — removes every marker
///   IsMarkerMode         — property; HandInteraction checks this to route trigger
///
/// Usage pattern on Quest:
///   1. User opens control panel and taps MARKERS button → IsMarkerMode = true
///   2. User points right controller at volume and pulls index trigger
///   3. HandInteraction raycasts against the volume bounds, calls PlaceMarkerAt
///   4. User taps MARKERS again to exit (or leaves it on to place more)
///   5. UNDO / CLEAR buttons remove markers as needed

using System.Collections.Generic;
using UnityEngine;

public class MarkerManager : MonoBehaviour
{
    [Header("Marker appearance")]
    // Arrow length in world-space metres (~18 mm — 3× the previous 6 mm so the
    // shaft is long enough to point past deeper structures inside the volume).
    public float   markerSize    = 0.018f;
    public Color   defaultColor  = new Color(0.10f, 0.95f, 0.20f, 1f);  // bright green
    public float   emissionBoost = 2.0f;         // unlit brightness

    readonly List<GameObject> _markers = new List<GameObject>();
    static Mesh _arrowMesh;
    bool _markerMode;

    public bool IsMarkerMode => _markerMode;
    public int  Count         => _markers.Count;

    /// Fired whenever the mode flips. UI can subscribe to update button color.
    public event System.Action<bool> OnMarkerModeChanged;

    public void ToggleMarkerMode()
    {
        _markerMode = !_markerMode;
        Debug.Log($"[RegionalAR] Marker mode: {_markerMode}");
        OnMarkerModeChanged?.Invoke(_markerMode);
    }

    public void SetMarkerMode(bool on)
    {
        if (_markerMode == on) return;
        _markerMode = on;
        OnMarkerModeChanged?.Invoke(_markerMode);
    }

    /// Place a marker at a world-space point. Parented to the volume so it
    /// moves with grabs / scales / rotates automatically.
    public GameObject PlaceMarkerAt(Vector3 worldPos)
    {
        return PlaceMarkerAt(worldPos, defaultColor);
    }

    public GameObject PlaceMarkerAt(Vector3 worldPos, Color color)
    {
        // Build an arrow mesh (shaft + conical tip) that points along local +Z.
        if (_arrowMesh == null) _arrowMesh = BuildArrowMesh();

        GameObject m = new GameObject($"Marker_{_markers.Count}");
        m.transform.SetParent(transform, worldPositionStays: true);
        m.transform.position = worldPos;

        var mf = m.AddComponent<MeshFilter>();
        mf.sharedMesh = _arrowMesh;
        var mr = m.AddComponent<MeshRenderer>();

        // Counter-scale so arrows stay the same apparent size regardless
        // of the volume's current scale
        Vector3 pScale = transform.lossyScale;
        float pS = Mathf.Max(pScale.x, Mathf.Max(pScale.y, pScale.z));
        float size = pS > 0.001f ? markerSize / pS : markerSize;
        m.transform.localScale = Vector3.one * size;

        // Orient so the arrow TIP points toward the volume center (local origin).
        // Mesh is authored with base at z=0 and tip at +z=1, so we aim +z at
        // -localPosition (direction from the hit point back toward 0,0,0).
        Vector3 toCenter = -m.transform.localPosition;
        if (toCenter.sqrMagnitude > 1e-6f)
            m.transform.localRotation = Quaternion.LookRotation(toCenter.normalized);

        // Unlit, bright green. Render on top of everything — the volume is
        // drawn in the Transparent queue and would otherwise occlude the
        // arrow shaft whenever it passes through deeper structures. We push
        // the arrow to queue 5000 AND disable ZTest so it always wins.
        Shader sh = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
        Material mat = sh != null ? new Material(sh)
                                  : new Material(Shader.Find("Standard"));
        mat.color = new Color(
            Mathf.Clamp01(color.r * emissionBoost),
            Mathf.Clamp01(color.g * emissionBoost),
            Mathf.Clamp01(color.b * emissionBoost),
            color.a);
        mat.renderQueue = 5000;                 // after Transparent (3000) and Overlay (4000)
        mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
        mat.SetInt("_ZWrite", 0);
        mr.material = mat;

        _markers.Add(m);
        Debug.Log($"[RegionalAR] Arrow marker placed @ {worldPos} (total: {_markers.Count})");
        return m;
    }

    // Build a simple arrow mesh pointing along +Z.
    //   Shaft: square cross-section, 0..0.6 on Z
    //   Head:  8-sided cone, 0.5..1.0 on Z (tip at +Z=1)
    // Mesh units are normalized (length 1); caller applies localScale.
    static Mesh BuildArrowMesh()
    {
        const int sides = 8;
        // 30% thinner than v1 (shaftR was 0.12, headR was 0.30)
        const float shaftR = 0.084f;
        const float headR  = 0.21f;
        const float shaftLen = 0.55f;   // shaft runs z=0..shaftLen
        const float headStart = 0.45f;  // head base at this z (slight overlap w/ shaft)
        const float tipZ = 1.0f;

        var verts = new System.Collections.Generic.List<Vector3>();
        var tris  = new System.Collections.Generic.List<int>();

        // ── Shaft (as a small cylinder approximated with 4 sides) ──
        int shaftBase = verts.Count;
        for (int i = 0; i < 4; i++)
        {
            float a = i * Mathf.PI * 2f / 4f + Mathf.PI * 0.25f;
            float x = Mathf.Cos(a) * shaftR;
            float y = Mathf.Sin(a) * shaftR;
            verts.Add(new Vector3(x, y, 0));          // back ring
            verts.Add(new Vector3(x, y, shaftLen));   // front ring
        }
        // Shaft sides (4 quads → 8 tris)
        for (int i = 0; i < 4; i++)
        {
            int i0 = shaftBase + i * 2;
            int i1 = shaftBase + ((i + 1) % 4) * 2;
            tris.Add(i0);     tris.Add(i1);     tris.Add(i0 + 1);
            tris.Add(i1);     tris.Add(i1 + 1); tris.Add(i0 + 1);
        }
        // Back cap (fan)
        int backCenter = verts.Count; verts.Add(new Vector3(0, 0, 0));
        for (int i = 0; i < 4; i++)
        {
            int i0 = shaftBase + i * 2;
            int i1 = shaftBase + ((i + 1) % 4) * 2;
            tris.Add(backCenter); tris.Add(i0); tris.Add(i1);
        }

        // ── Head (8-sided cone) ──
        int headBase = verts.Count;
        for (int i = 0; i < sides; i++)
        {
            float a = i * Mathf.PI * 2f / sides;
            verts.Add(new Vector3(Mathf.Cos(a) * headR, Mathf.Sin(a) * headR, headStart));
        }
        int tipIdx = verts.Count; verts.Add(new Vector3(0, 0, tipZ));
        int headCenter = verts.Count; verts.Add(new Vector3(0, 0, headStart));
        for (int i = 0; i < sides; i++)
        {
            int i0 = headBase + i;
            int i1 = headBase + (i + 1) % sides;
            // Side face (fan from tip)
            tris.Add(i0);  tris.Add(i1);  tris.Add(tipIdx);
            // Cone base (fan from head center, faces back)
            tris.Add(headCenter); tris.Add(i1); tris.Add(i0);
        }

        var m = new Mesh { name = "RegionalAR_Arrow" };
        m.SetVertices(verts);
        m.SetTriangles(tris, 0);
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    public void UndoLast()
    {
        if (_markers.Count == 0) return;
        int idx = _markers.Count - 1;
        if (_markers[idx] != null) Destroy(_markers[idx]);
        _markers.RemoveAt(idx);
        Debug.Log($"[RegionalAR] Undo marker — {_markers.Count} remain");
    }

    public void ClearAll()
    {
        foreach (var m in _markers)
            if (m != null) Destroy(m);
        _markers.Clear();
        Debug.Log("[RegionalAR] All markers cleared");
    }
}
