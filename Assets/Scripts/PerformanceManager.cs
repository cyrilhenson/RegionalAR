/// RegionalAR — Performance Manager
/// Configures Quest 3 GPU/CPU performance settings at startup
/// for smooth AR volume rendering at consistent 72Hz.
///
/// Attach to any always-active GameObject in the scene (e.g. OVRCameraRig).
///
/// What this does:
///   1. Fixed Foveated Rendering (FFR) — reduces pixel shading at display edges
///      where the user isn't looking. Saves 30-40% GPU fill rate.
///   2. GPU/CPU performance level hints — tells the Quest scheduler to allocate
///      more GPU headroom for the raymarch shader.
///   3. Eye texture resolution — slight reduction to ease GPU bandwidth.
///   4. MSAA — reduces from 4x to 2x (volume rendering doesn't benefit from 4x).
///   5. Refresh rate — locks to 72Hz for maximum GPU budget per frame.

using UnityEngine;

public class PerformanceManager : MonoBehaviour
{
    void Start()
    {
        ApplyPerformanceSettings();
    }

    void ApplyPerformanceSettings()
    {
        // ── 1. Fixed Foveated Rendering (FFR) ───────────────────────
        // Quest 3 uses "fixed" foveation (not eye-tracked).
        // HighTop (level 4) aggressively reduces peripheral resolution
        // while keeping the central region (where the user looks) sharp.
        // This is the single biggest GPU optimization available.
        try
        {
            OVRManager.foveatedRenderingLevel = OVRManager.FoveatedRenderingLevel.HighTop;
            // useDynamicFoveatedRendering adapts FFR level based on GPU load —
            // raises FFR when the GPU is under pressure, lowers it when idle.
            OVRManager.useDynamicFoveatedRendering = true;
            Debug.Log("[RegionalAR] FFR enabled: HighTop + dynamic");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[RegionalAR] FFR setup failed: {e.Message}");
        }

        // ── 2. GPU/CPU performance levels ────────────────────────────
        // Quest's power manager throttles clocks by default. Setting higher
        // levels tells it we need sustained GPU performance for raymarching.
        // Level range: 0 (powersave) to 5 (max). 4 = high without thermal risk.
        try
        {
            OVRManager.suggestedGpuPerfLevel = OVRManager.ProcessorPerformanceLevel.SustainedHigh;
            OVRManager.suggestedCpuPerfLevel = OVRManager.ProcessorPerformanceLevel.SustainedLow;
            Debug.Log("[RegionalAR] GPU=SustainedHigh, CPU=SustainedLow");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[RegionalAR] Perf level setup failed: {e.Message}");
        }

        // ── 3. Eye texture resolution ────────────────────────────────
        // Default is 1.0 (native). Dropping to 0.9 reduces total pixel count
        // by ~19% with minimal visual impact at Quest 3 DPI. The raymarch
        // shader's soft nature (no hard polygon edges) hides the reduction.
        try
        {
            UnityEngine.XR.XRSettings.eyeTextureResolutionScale = 0.9f;
            Debug.Log("[RegionalAR] Eye texture resolution: 0.9x");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[RegionalAR] Eye texture resolution setup failed: {e.Message}");
        }

        // ── 4. MSAA ─────────────────────────────────────────────────
        // Volume rendering is all transparent blending — MSAA only helps with
        // polygon edges (UI buttons, cut plane discs). 2x is enough for those.
        // Reducing from 4x to 2x saves significant fill rate.
        QualitySettings.antiAliasing = 2;
        Debug.Log("[RegionalAR] MSAA: 2x");

        // ── 5. Refresh rate ─────────────────────────────────────────
        // Quest 3 supports 72/90/120Hz. Lock to 72Hz for maximum GPU budget
        // per frame (13.9ms vs 11.1ms at 90Hz). The extra 2.8ms is critical
        // for the raymarch shader. Users won't notice the difference in AR.
        try
        {
            OVRManager.display.displayFrequency = 72f;
            Debug.Log("[RegionalAR] Display frequency: 72Hz");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[RegionalAR] Display frequency setup failed: {e.Message}");
        }

        // ── 6. Physics (minor) ──────────────────────────────────────
        // Volume rendering doesn't use rigidbodies, but we DO use BoxCollider
        // raycasts for the button/slider system. FixedUpdate mode keeps the
        // automatic transform-sync that Collider.Raycast depends on, while
        // the slow timestep (25Hz) minimises CPU overhead.
        Time.fixedDeltaTime = 0.04f;  // 25Hz instead of default 50Hz
        Debug.Log("[RegionalAR] Physics: FixedUpdate @ 25Hz (collider sync preserved)");
    }
}
