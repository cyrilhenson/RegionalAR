/// RegionalAR — Spatial Anchor Stabilizer
/// Uses OVRSpatialAnchor to world-lock the volume rendering, reducing
/// perceived jitter from head tracking inconsistencies.
///
/// Attach to the Volume GameObject (same object as VolumeRenderer).
///
/// How it works:
///   1. When the volume is placed, a spatial anchor is created at its position.
///   2. OVR's SLAM system keeps the anchor locked to the physical environment.
///   3. Each frame, the volume's transform is nudged toward the anchor's
///      world-locked position, absorbing tracking drift/jitter.
///   4. When the user grabs and moves the volume, the old anchor is destroyed
///      and a new one is created at the release position.
///
/// Works in conjunction with:
///   - VolumeRenderer.StabilizeRenderPose() for sub-frame smoothing
///   - Late Latching (enabled in Project Settings) for GPU-side pose updates

using UnityEngine;

public class SpatialAnchorStabilizer : MonoBehaviour
{
    OVRSpatialAnchor _anchor;
    bool _anchorReady;

    // Track whether user is currently grabbing (skip correction during grab)
    bool _isGrabbing;

    void Start()
    {
        // Do NOT create an anchor on startup — the volume starts at scene
        // origin (floor level) and hasn't been positioned yet. An early
        // anchor would world-lock it to the floor, fighting every later
        // PositionInFrontOfUser() call. Instead, the anchor is created
        // explicitly after the volume is loaded and positioned.
    }

    /// Create a spatial anchor at the volume's current world position.
    /// Call this after placing the volume, loading a scan, or releasing a grab.
    public void CreateAnchor()
    {
        // Destroy existing anchor if any
        DestroyAnchor();

        try
        {
            _anchor = gameObject.AddComponent<OVRSpatialAnchor>();
            _anchorReady = false;
            Debug.Log("[RegionalAR] Spatial anchor creation requested");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[RegionalAR] Failed to create spatial anchor: {e.Message}");
        }
    }

    /// Destroy the current anchor (call before creating a new one)
    public void DestroyAnchor()
    {
        if (_anchor != null)
        {
            Destroy(_anchor);
            _anchor = null;
        }
        _anchorReady = false;
    }

    /// Call when user starts grabbing the volume
    public void OnGrabStart()
    {
        _isGrabbing = true;
    }

    /// Call when user releases the volume — recreates anchor at new position
    public void OnGrabEnd()
    {
        _isGrabbing = false;
        // Small delay to let the final position settle
        Invoke(nameof(CreateAnchor), 0.1f);
    }

    void LateUpdate()
    {
        if (_anchor == null || _isGrabbing) return;

        // Check if anchor is localized (OVR has locked it to the environment)
        if (!_anchorReady)
        {
            // OVRSpatialAnchor sets its transform once localized.
            // We detect this by checking if the anchor component is still valid.
            // After creation, give it a moment to localize.
            if (_anchor.Created)
            {
                _anchorReady = true;
                // Store the offset between anchor and volume at creation time
                // (they should be identical since anchor is on the same GO,
                //  but this handles any edge cases)
                Debug.Log("[RegionalAR] Spatial anchor localized and ready");
            }
            return;
        }

        // The OVRSpatialAnchor component automatically updates the
        // GameObject's transform to stay world-locked. Since it's on the
        // same GameObject as the volume, the volume inherits this stability.
        //
        // However, if the user's grab system temporarily moves the transform,
        // the anchor's correction can conflict. The _isGrabbing flag prevents this.
    }

    void OnDestroy()
    {
        DestroyAnchor();
    }
}
