/// RegionalAR — Panel Stabilizer
/// Smooths world-space UI panel transforms to reduce jitter from
/// head tracking noise on Quest 3 passthrough AR.
///
/// How it works:
///   - Maintains a smoothed (target) position and rotation.
///   - Each LateUpdate, the panel's actual transform is interpolated
///     toward the smoothed target using exponential decay.
///   - When the panel is repositioned (opened, snapped in front),
///     call SnapToTarget() to teleport without smooth lag.
///
/// Attach to any world-space Canvas root GameObject.

using UnityEngine;

public class PanelStabilizer : MonoBehaviour
{
    [Header("Smoothing")]
    [Tooltip("Position smoothing speed. Lower = smoother but laggier. 8-12 is good.")]
    public float posSpeed = 12f;

    [Tooltip("Rotation smoothing speed. Lower = smoother but laggier.")]
    public float rotSpeed = 12f;

    [Tooltip("Maximum position correction per frame in meters (prevents teleporting)")]
    public float maxPosStep = 0.0036f;

    // Smoothed state
    Vector3    _smoothPos;
    Quaternion _smoothRot;
    bool       _initialized;

    /// Call this when the panel is teleported to a new position
    /// (toggle open, snap-in-front, reposition, etc.)
    public void SnapToTarget()
    {
        _smoothPos = transform.position;
        _smoothRot = transform.rotation;
        _initialized = true;
    }

    void OnEnable()
    {
        // Snap on enable so there's no lerp-in from origin
        SnapToTarget();
    }

    void LateUpdate()
    {
        if (!_initialized)
        {
            SnapToTarget();
            return;
        }

        Vector3 realPos = transform.position;
        Quaternion realRot = transform.rotation;

        // Exponential smoothing — critically-damped feel
        float dt = Time.deltaTime;
        float tPos = 1f - Mathf.Exp(-posSpeed * dt);
        float tRot = 1f - Mathf.Exp(-rotSpeed * dt);

        // Clamp position step to prevent large jumps from looking weird
        Vector3 targetPos = Vector3.Lerp(_smoothPos, realPos, tPos);
        Vector3 delta = targetPos - _smoothPos;
        if (delta.magnitude > maxPosStep)
            targetPos = _smoothPos + delta.normalized * maxPosStep;

        _smoothPos = targetPos;
        _smoothRot = Quaternion.Slerp(_smoothRot, realRot, tRot);

        // Apply smoothed transform
        transform.position = _smoothPos;
        transform.rotation = _smoothRot;
    }
}
