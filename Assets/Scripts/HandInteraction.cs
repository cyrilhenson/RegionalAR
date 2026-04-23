/// RegionalAR — Hand / Controller Interaction
/// Grab, move, scale, rotate, and dual cut-plane the volume using Quest controllers.
///
/// CONTROLS:
///   Right Grip          = grab & move volume (proximity-gated)
///   Both Grips          = pinch to scale
///   Left Thumbstick     = move active cut plane in/out (normal follows left controller)
///   A button            = cycle: Plane1 → Plane2 → Both Locked → All Off
///   B button            = reset volume + panel + clear all cut planes
///
/// Attach to the Volume GameObject (same as VolumeRenderer).

using UnityEngine;
using System.Collections;

public class HandInteraction : MonoBehaviour
{
    // ── Input mode toggle ────────────────────────────────────────
    public enum InputMode { Controllers, Hands }
    public InputMode currentInputMode = InputMode.Controllers;

    public void ToggleInputMode()
    {
        currentInputMode = (currentInputMode == InputMode.Controllers)
            ? InputMode.Hands : InputMode.Controllers;

        Debug.Log($"[RegionalAR] Input mode: {currentInputMode}");
    }

    public string InputModeLabel => currentInputMode == InputMode.Controllers ? "Controllers" : "Hands";

    /// Reset all grab/interaction state — call after external repositioning
    public void ResetGrabState()
    {
        _grabbing = false;
        _twoHand = false;
        _cpGrabbing = false;
        SetHighlight(false);
    }

    [Header("Limits")]
    public float MinScale = 0.05f;
    public float MaxScale = 3.0f;

    [Header("Editor Mouse Fallback")]
    public bool enableMouseFallback = true;

    // Grab state
    bool  _grabbing, _twoHand;
    float _initGrabDist, _initScale;
    Vector3 _grabOffset;
    Quaternion _grabRotOffset;

    // Visual feedback
    MeshRenderer _mr;
    bool _highlighted;

    // ── Dual cut plane state ──────────────────────────────────────
    VolumeRenderer _vr;

    // Cycle states: 0=Plane1 active, 1=Plane2 active, 2=Both locked, 3=All off
    int  _cutState = 3;  // start with all off

    // Per-plane runtime data (world-space normal + dist)
    float   _cpDist0, _cpDist1;
    Vector3 _cpWorldNormal0 = Vector3.up;
    Vector3 _cpWorldNormal1 = Vector3.right;

    // Anchored (locked) plane data — stored in object-local space so they
    // survive grab/rotate/scale changes
    Vector3 _lockedLocalNormal0, _lockedLocalNormal1;
    float   _lockedLocalDist0,   _lockedLocalDist1;

    // Cut plane visuals (two semi-transparent discs, different colors)
    GameObject   _cpVis0, _cpVis1;
    MeshRenderer _cpVisR0, _cpVisR1;

    // Cut plane grab state
    bool       _cpGrabbing;
    Vector3    _cpGrabOffset;         // hand-to-disc offset at grab start
    Quaternion _cpGrabRotOffset;      // rotation offset at grab start
    float      _cpGrabRadius = 0.15f; // proximity to disc center to initiate grab

    // Grab feedback
    AudioClip _grabClip;

    // Mouse fallback
    bool    _mouseDrag;
    Vector3 _mousePrev;

    // Rig cache
    Transform _rigRoot;

    // Hand tracking
    OVRHand _handL, _handR;
    const float FIST_THRESH = 0.5f;       // per-finger pinch strength for fist detection
    const float HAND_SMOOTH = 6f;         // Lerp speed — lower = smoother, avoids jams
    const float MAX_HAND_JUMP = 0.08f;    // max meters per frame before we dampen (8cm)
    Vector3    _smoothHandPosR, _smoothHandPosL;
    Quaternion _smoothHandRotR = Quaternion.identity;
    Quaternion _smoothHandRotL = Quaternion.identity;
    bool _smoothInitR, _smoothInitL;

    // (Cut plane cycling removed from hand gestures — use control panel button instead)

    // Marker pointer: visual arrow on right controller, only visible while
    // MarkerManager.IsMarkerMode is true. Lets the user see exactly where
    // the marker will land before they pull the trigger.
    GameObject _markerPointer;

    void Start()
    {
        _vr = GetComponent<VolumeRenderer>();
        _mr = GetComponent<MeshRenderer>();
        _cpDist0 = 0f;
        _cpDist1 = 0f;
        BuildCutPlaneVisuals();
        CacheHands();
        BuildMarkerPointer();

        _grabClip = Resources.Load<AudioClip>("bong_001");
        if (_grabClip == null) _grabClip = Resources.Load<AudioClip>("water_drop");
    }

    /// Play grab feedback — sound + brief haptic pulse
    void PlayGrabFeedback()
    {
        if (_grabClip != null)
        {
            Transform listener = Camera.main != null ? Camera.main.transform : transform;
            AudioSource.PlayClipAtPoint(_grabClip, listener.position, 1.0f);
        }
        OVRInput.SetControllerVibration(0.4f, 0.3f, OVRInput.Controller.RTouch);
        OVRInput.SetControllerVibration(0.4f, 0.3f, OVRInput.Controller.LTouch);
        StartCoroutine(StopHapticsAfter(0.15f));
    }

    IEnumerator StopHapticsAfter(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);
        OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.LTouch);
    }

    void BuildMarkerPointer()
    {
        if (_markerPointer != null) return;

        // Simple two-piece arrow: cylinder shaft + cone tip, both under a
        // parent we can toggle and move as a unit. Unit meshes scaled down.
        _markerPointer = new GameObject("MarkerPointer");
        _markerPointer.SetActive(false);

        // Shaft — thin cylinder laid along +Z
        var shaft = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        shaft.name = "Shaft";
        shaft.transform.SetParent(_markerPointer.transform, false);
        // Unity cylinder is 2 units tall on Y by default, so rotate Y→Z
        // and scale Z down to represent the shaft length (15 cm).
        shaft.transform.localRotation = Quaternion.Euler(90, 0, 0);
        shaft.transform.localScale    = new Vector3(0.010f, 0.075f, 0.010f);
        shaft.transform.localPosition = new Vector3(0, 0, 0.075f);
        var sc = shaft.GetComponent<Collider>(); if (sc != null) Destroy(sc);

        // Tip — capsule with small radius acting as a cone-ish head
        var tip = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        tip.name = "Tip";
        tip.transform.SetParent(_markerPointer.transform, false);
        tip.transform.localRotation = Quaternion.Euler(90, 0, 0);
        tip.transform.localScale    = new Vector3(0.025f, 0.020f, 0.025f);
        tip.transform.localPosition = new Vector3(0, 0, 0.168f);
        var tc = tip.GetComponent<Collider>(); if (tc != null) Destroy(tc);

        // Material — bright green, unlit, renders on top of everything
        // so the arrow is always clearly visible against the volume.
        Shader sh = Shader.Find("Unlit/Color");
        if (sh == null) sh = Shader.Find("Standard");
        var mat = new Material(sh);
        mat.color = new Color(0.10f, 0.95f, 0.20f, 1f);
        mat.renderQueue = 5000;
        if (mat.HasProperty("_ZTest"))
            mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
        if (mat.HasProperty("_ZWrite"))
            mat.SetInt("_ZWrite", 0);
        foreach (var r in _markerPointer.GetComponentsInChildren<MeshRenderer>())
            r.sharedMaterial = mat;
    }

    /// Position the right-controller pointer each frame and toggle
    /// its visibility based on MarkerManager.IsMarkerMode.
    void UpdateMarkerPointer()
    {
        if (_markerPointer == null) return;
        var mm = _vr != null ? _vr.GetComponent<MarkerManager>() : null;
        bool showIt = (mm != null && mm.IsMarkerMode);
        if (_markerPointer.activeSelf != showIt) _markerPointer.SetActive(showIt);
        if (!showIt) return;

        // Follow the right controller's pose each frame
        Vector3 pos = CtrlPos(OVRInput.Controller.RTouch);
        Quaternion rot = CtrlRot(OVRInput.Controller.RTouch);
        _markerPointer.transform.SetPositionAndRotation(pos, rot);
    }

    void CacheHands()
    {
        var rig = FindObjectOfType<OVRCameraRig>();
        if (rig == null) return;
        if (rig.leftHandAnchor != null)
            _handL = rig.leftHandAnchor.GetComponentInChildren<OVRHand>();
        if (rig.rightHandAnchor != null)
            _handR = rig.rightHandAnchor.GetComponentInChildren<OVRHand>();
    }

    void Update()
    {
        #if !UNITY_EDITOR || UNITY_ANDROID
        try { UpdateOVRGrab(); } catch { }
        try { UpdateCutPlane(); } catch { }
        try { UpdateMarkerPointer(); } catch { }
        #endif

        if (enableMouseFallback)
            HandleMouseInput();

        UpdateCutPlaneVisuals();
    }

    // ─────────────────────────────────────────────────────────────
    //  RIG HELPERS
    // ─────────────────────────────────────────────────────────────
    Transform GetRig()
    {
        if (_rigRoot != null) return _rigRoot;
        Camera cam = Camera.main;
        if (cam != null && cam.transform.parent != null && cam.transform.parent.parent != null)
            _rigRoot = cam.transform.parent.parent;
        return _rigRoot;
    }

    Vector3 CtrlPos(OVRInput.Controller c)
    {
        Vector3 lp = OVRInput.GetLocalControllerPosition(c);
        Transform r = GetRig();
        return r != null ? r.TransformPoint(lp) : lp;
    }

    Quaternion CtrlRot(OVRInput.Controller c)
    {
        Quaternion lr = OVRInput.GetLocalControllerRotation(c);
        Transform r = GetRig();
        return r != null ? r.rotation * lr : lr;
    }

    Vector3 CtrlFwd(OVRInput.Controller c) => CtrlRot(c) * Vector3.forward;

    // Ray-AABB intersection against the volume's object-local [-0.5, 0.5] cube
    // (the raymarch cube mesh). Returns the WORLD-space entry point, or null
    // if the ray misses. Used by the teaching Marker feature.
    Vector3? RaycastVolumeBounds(Vector3 worldO, Vector3 worldD)
    {
        if (_vr == null) return null;
        Transform t = _vr.transform;
        // Transform ray into volume object space
        Vector3 lO = t.InverseTransformPoint(worldO);
        Vector3 lD = t.InverseTransformDirection(worldD).normalized;
        if (lD.sqrMagnitude < 0.0001f) return null;

        Vector3 invD = new Vector3(1f / (lD.x + 1e-6f), 1f / (lD.y + 1e-6f), 1f / (lD.z + 1e-6f));
        Vector3 minB = new Vector3(-0.5f, -0.5f, -0.5f);
        Vector3 maxB = new Vector3( 0.5f,  0.5f,  0.5f);
        Vector3 t0 = Vector3.Scale(minB - lO, invD);
        Vector3 t1 = Vector3.Scale(maxB - lO, invD);
        Vector3 tN = Vector3.Min(t0, t1);
        Vector3 tF = Vector3.Max(t0, t1);
        float tEntry = Mathf.Max(tN.x, Mathf.Max(tN.y, tN.z));
        float tExit  = Mathf.Min(tF.x, Mathf.Min(tF.y, tF.z));
        if (tExit < Mathf.Max(tEntry, 0f)) return null;
        float tHit = Mathf.Max(tEntry, 0f);
        Vector3 hitLocal = lO + lD * tHit;
        return t.TransformPoint(hitLocal);
    }

    // ─────────────────────────────────────────────────────────────
    //  HAND TRACKING HELPERS
    // ─────────────────────────────────────────────────────────────
    bool IsHandTracked(OVRHand h) => h != null && h.IsTracked;

    /// Fist gesture — all four fingers curled toward palm.
    /// Uses pinch strength as proxy (rises when fingers close).
    bool IsHandGrabbing(OVRHand h)
    {
        if (h == null || !h.IsTracked) return false;
        float idx   = h.GetFingerPinchStrength(OVRHand.HandFinger.Index);
        float mid   = h.GetFingerPinchStrength(OVRHand.HandFinger.Middle);
        float ring  = h.GetFingerPinchStrength(OVRHand.HandFinger.Ring);
        float pinky = h.GetFingerPinchStrength(OVRHand.HandFinger.Pinky);
        // Require at least 3 of 4 fingers above threshold for a forgiving fist
        int count = 0;
        if (idx   > FIST_THRESH) count++;
        if (mid   > FIST_THRESH) count++;
        if (ring  > FIST_THRESH) count++;
        if (pinky > FIST_THRESH) count++;
        return count >= 3;
    }

    Vector3 HandPos(OVRHand h)
    {
        if (h != null && h.IsTracked && h.PointerPose != null)
            return h.PointerPose.position;
        return Vector3.zero;
    }

    Quaternion HandRot(OVRHand h)
    {
        if (h != null && h.IsTracked && h.PointerPose != null)
            return h.PointerPose.rotation;
        return Quaternion.identity;
    }

    Vector3 HandFwd(OVRHand h) => HandRot(h) * Vector3.forward;

    /// Smoothed hand position — reduces jitter and clamps large jumps
    /// (which happen when tracking drops and snaps back, causing "jams").
    Vector3 SmoothedHandPos(OVRHand h, bool isLeft)
    {
        Vector3 raw = HandPos(h);
        // Get tracking confidence — low confidence in poor lighting causes jitter
        float confidence = (h != null && h.IsTracked && h.HandConfidence == OVRHand.TrackingConfidence.High)
            ? 1f : 0.4f;

        if (isLeft)
        {
            if (!_smoothInitL) { _smoothHandPosL = raw; _smoothInitL = true; return raw; }
            float jump = Vector3.Distance(_smoothHandPosL, raw);
            float t = HAND_SMOOTH * Time.deltaTime * confidence;
            if (jump > MAX_HAND_JUMP) t *= 0.15f;        // heavy damping on snaps
            else if (jump > MAX_HAND_JUMP * 0.5f) t *= 0.5f;  // moderate damping
            _smoothHandPosL = Vector3.Lerp(_smoothHandPosL, raw, Mathf.Clamp01(t));
            return _smoothHandPosL;
        }
        else
        {
            if (!_smoothInitR) { _smoothHandPosR = raw; _smoothInitR = true; return raw; }
            float jump = Vector3.Distance(_smoothHandPosR, raw);
            float t = HAND_SMOOTH * Time.deltaTime * confidence;
            if (jump > MAX_HAND_JUMP) t *= 0.15f;
            else if (jump > MAX_HAND_JUMP * 0.5f) t *= 0.5f;
            _smoothHandPosR = Vector3.Lerp(_smoothHandPosR, raw, Mathf.Clamp01(t));
            return _smoothHandPosR;
        }
    }

    Quaternion SmoothedHandRot(OVRHand h, bool isLeft)
    {
        Quaternion raw = HandRot(h);
        float confidence = (h != null && h.IsTracked && h.HandConfidence == OVRHand.TrackingConfidence.High)
            ? 1f : 0.4f;

        if (isLeft)
        {
            float angle = Quaternion.Angle(_smoothHandRotL, raw);
            float t = HAND_SMOOTH * Time.deltaTime * confidence;
            if (angle > 25f) t *= 0.15f;
            else if (angle > 12f) t *= 0.5f;
            _smoothHandRotL = Quaternion.Slerp(_smoothHandRotL, raw, Mathf.Clamp01(t));
            return _smoothHandRotL;
        }
        else
        {
            float angle = Quaternion.Angle(_smoothHandRotR, raw);
            float t = HAND_SMOOTH * Time.deltaTime * confidence;
            if (angle > 25f) t *= 0.15f;
            else if (angle > 12f) t *= 0.5f;
            _smoothHandRotR = Quaternion.Slerp(_smoothHandRotR, raw, Mathf.Clamp01(t));
            return _smoothHandRotR;
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  GRAB + SCALE  (with proximity check — only grab when near)
    // ─────────────────────────────────────────────────────────────

    bool IsNearVolume(Vector3 worldPos)
    {
        if (_mr != null)
        {
            Bounds b = _mr.bounds;
            b.Expand(0.2f); // 20cm padding around the volume
            return b.Contains(worldPos);
        }
        float radius = transform.localScale.x * 0.5f + 0.2f;
        return Vector3.Distance(worldPos, transform.position) < radius;
    }

    void UpdateOVRGrab()
    {
        // Lazy-cache hands if not found at Start
        if (_handL == null || _handR == null) CacheHands();

        bool useHands = (currentInputMode == InputMode.Hands);
        bool useCtrls = (currentInputMode == InputMode.Controllers);

        bool rHandTracked = useHands && IsHandTracked(_handR);
        bool lHandTracked = useHands && IsHandTracked(_handL);

        // GRIP — only from the active input mode
        bool rGrab = false, lGrab = false;
        if (useCtrls)
        {
            rGrab = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);
            lGrab = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch);
        }
        else if (useHands)
        {
            rGrab = IsHandGrabbing(_handR);
            lGrab = IsHandGrabbing(_handL);
        }

        Vector3 rPos = rHandTracked ? SmoothedHandPos(_handR, false) : CtrlPos(OVRInput.Controller.RTouch);
        Vector3 lPos = lHandTracked ? SmoothedHandPos(_handL, true)  : CtrlPos(OVRInput.Controller.LTouch);

        // Two-hand scale — only if already grabbing or both hands near volume
        if (rGrab && lGrab)
        {
            if (!_twoHand)
            {
                if (!_grabbing && !IsNearVolume(rPos) && !IsNearVolume(lPos))
                    return;
                _twoHand = true;
                _initGrabDist = Vector3.Distance(rPos, lPos);
                _initScale = transform.localScale.x;
                PlayGrabFeedback();
            }
            else
            {
                float d = Vector3.Distance(rPos, lPos);
                float s = Mathf.Clamp(_initScale * (d / Mathf.Max(_initGrabDist, 0.01f)), MinScale, MaxScale);
                transform.localScale = Vector3.one * s;
                transform.position = (rPos + lPos) * 0.5f;
            }
            SetHighlight(true);
            return;
        }
        else
        {
            if (_twoHand && _vr != null) _vr.SnapStablePose();
            _twoHand = false;
        }

        // Single grab (right hand or right controller) — only if near volume
        if (rGrab)
        {
            Quaternion rRot = rHandTracked ? SmoothedHandRot(_handR, false) : CtrlRot(OVRInput.Controller.RTouch);
            if (!_grabbing)
            {
                if (!IsNearVolume(rPos))
                {
                    // Not near volume — don't grab
                }
                else
                {
                    _grabbing = true;
                    _grabOffset = transform.position - rPos;
                    _grabRotOffset = Quaternion.Inverse(rRot) * transform.rotation;
                    SetHighlight(true);
                    PlayGrabFeedback();
                    // Notify spatial anchor stabilizer
                    var sas = GetComponent<SpatialAnchorStabilizer>();
                    if (sas != null) sas.OnGrabStart();
                }
            }
            else
            {
                transform.position = rPos + _grabOffset;
                transform.rotation = rRot * _grabRotOffset;
                SetHighlight(true);
            }
        }
        else
        {
            if (_grabbing)
            {
                SetHighlight(false);
                if (_vr != null) _vr.SnapStablePose();
                // Recreate spatial anchor at new position
                var sas = GetComponent<SpatialAnchorStabilizer>();
                if (sas != null) sas.OnGrabEnd();
            }
            _grabbing = false;
        }

        // Right-index-trigger: place a teaching marker when Marker Mode is ON.
        // Raycasts forward from the controller and drops a sphere where the
        // ray first hits the volume bounds (object-local [0,1]^3 AABB).
        if (useCtrls && _vr != null)
        {
            var mm = _vr.GetComponent<MarkerManager>();
            if (mm != null && mm.IsMarkerMode &&
                OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch))
            {
                Vector3 rayO = CtrlPos(OVRInput.Controller.RTouch);
                Vector3 rayD = CtrlFwd(OVRInput.Controller.RTouch);
                Vector3? hit = RaycastVolumeBounds(rayO, rayD);
                if (hit.HasValue) mm.PlaceMarkerAt(hit.Value);
                else              mm.PlaceMarkerAt(rayO + rayD * 0.35f); // fallback: 35 cm ahead
            }
        }

        // B = reset everything
        if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch))
        {
            if (_vr != null) { _vr.PositionInFrontOfUser(); _vr.ResetCrop(); }
            _cutState = 3;  // all off
            _cpDist0 = 0f;
            _cpDist1 = 0f;
            if (_vr != null) _vr.ClearAllCutPlanes();
            var wifi = FindObjectOfType<WiFiDownloader>();
            if (wifi != null) wifi.RepositionPanel();
            Debug.Log("[RegionalAR] Reset all — cut planes off");
        }

        // A = cycle cut plane states: P1 active → P2 active → Both locked → All off
        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch))
        {
            _cutState = (_cutState + 1) % 4;
            ApplyCutState();
        }
    }

    /// <summary>
    /// Apply the current _cutState to VolumeRenderer.
    /// State 0: Plane 1 active (controllable), Plane 2 off
    /// State 1: Plane 1 locked, Plane 2 active (controllable)
    /// State 2: Both planes locked (anchored in place)
    /// State 3: All off
    /// </summary>
    void ApplyCutState()
    {
        if (_vr == null) return;

        switch (_cutState)
        {
            case 0: // Plane 1 active — start at center facing user
                _cpDist0 = 0f;
                Camera cam0 = Camera.main;
                if (cam0 != null)
                    // Normal points AWAY from camera (into volume far side).
                    // Shaders use inverted-compare so this still culls the
                    // NEAR half (the side facing the user) — which is what
                    // we want on activation. Keeping the normal oriented
                    // this way is required for the disc-drag controls to
                    // feel natural (push disc deeper = less cut).
                    _cpWorldNormal0 = (transform.position - cam0.transform.position).normalized;
                else
                    _cpWorldNormal0 = Vector3.up;

                _vr.SetCutPlane(0, _cpWorldNormal0, _cpDist0);
                _vr.ClearCutPlane(1);
                _vr.SetCutPlaneLocked(0, false);
                Debug.Log("[RegionalAR] Cut state: Plane 1 ACTIVE");
                break;

            case 1: // Plane 2 active — lock plane 1 in place
                // Snapshot plane 1 into locked local coords
                _lockedLocalNormal0 = _vr.cutPlanes[0].normal;
                _lockedLocalDist0   = _vr.cutPlanes[0].dist;
                _vr.SetCutPlaneLocked(0, true);

                // Start plane 2 at center, normal perpendicular to plane 1
                _cpDist1 = 0f;
                _cpWorldNormal1 = Vector3.Cross(_cpWorldNormal0, Vector3.up).normalized;
                if (_cpWorldNormal1.sqrMagnitude < 0.01f)
                    _cpWorldNormal1 = Vector3.Cross(_cpWorldNormal0, Vector3.forward).normalized;

                _vr.SetCutPlane(1, _cpWorldNormal1, _cpDist1);
                _vr.SetCutPlaneLocked(1, false);
                Debug.Log("[RegionalAR] Cut state: Plane 2 ACTIVE (Plane 1 locked)");
                break;

            case 2: // Both locked — snapshot plane 2
                _lockedLocalNormal1 = _vr.cutPlanes[1].normal;
                _lockedLocalDist1   = _vr.cutPlanes[1].dist;
                _vr.SetCutPlaneLocked(0, true);
                _vr.SetCutPlaneLocked(1, true);
                Debug.Log("[RegionalAR] Cut state: BOTH LOCKED");
                break;

            case 3: // All off
                _vr.ClearAllCutPlanes();
                Debug.Log("[RegionalAR] Cut state: ALL OFF");
                break;
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  CUT PLANE UPDATE  (grab disc with left grip + thumbstick fine-tune)
    // ─────────────────────────────────────────────────────────────
    void UpdateCutPlane()
    {
        if (_vr == null) return;

        // Only update if we have an active (non-locked) plane
        if (_cutState >= 2) return;  // locked or off — nothing to control

        bool lHandTracked = IsHandTracked(_handL);
        bool lGrip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch)
                  || IsHandGrabbing(_handL);
        Vector3 lPos = lHandTracked ? SmoothedHandPos(_handL, true) : CtrlPos(OVRInput.Controller.LTouch);
        Quaternion lRot = lHandTracked ? SmoothedHandRot(_handL, true) : CtrlRot(OVRInput.Controller.LTouch);
        Vector2 lStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);

        // Which disc is active?
        GameObject activeVis = (_cutState == 0) ? _cpVis0 : _cpVis1;

        if (lGrip)
        {
            if (!_cpGrabbing)
            {
                // Check if hand is near the active disc to start grab
                if (activeVis != null && activeVis.activeSelf)
                {
                    float dist = Vector3.Distance(lPos, activeVis.transform.position);
                    // Scale grab radius with disc size (disc is 1.6× volume scale)
                    float grabR = Mathf.Max(_cpGrabRadius, transform.localScale.x * 1.0f);
                    if (dist < grabR)
                    {
                        _cpGrabbing = true;
                        _cpGrabOffset = activeVis.transform.position - lPos;
                        _cpGrabRotOffset = Quaternion.Inverse(lRot) * activeVis.transform.rotation;
                        PlayGrabFeedback();
                    }
                }
            }

            if (_cpGrabbing)
            {
                // Move disc with hand — use smoothed tracking to prevent stalls
                Vector3 discWorldPos = lPos + _cpGrabOffset;
                Quaternion discWorldRot = lRot * _cpGrabRotOffset;
                Vector3 worldNormal = discWorldRot * Vector3.forward;

                // Safety: if normal is degenerate, keep the previous one
                if (worldNormal.sqrMagnitude < 0.001f)
                    worldNormal = (_cutState == 0) ? _cpWorldNormal0 : _cpWorldNormal1;
                else
                    worldNormal = worldNormal.normalized;

                // Convert disc world position to cut plane parameters
                Vector3 localDiscPos = transform.InverseTransformPoint(discWorldPos);
                Vector3 localNormal = transform.InverseTransformDirection(worldNormal);

                // Safety: avoid degenerate normals from non-uniform scale
                if (localNormal.sqrMagnitude < 0.0001f)
                    localNormal = (_cutState == 0) ? _cpWorldNormal0 : _cpWorldNormal1;
                else
                    localNormal = localNormal.normalized;

                float cpDist = Vector3.Dot(localDiscPos, localNormal);
                cpDist = Mathf.Clamp(cpDist, -0.5f, 0.5f);

                // Smooth the normal and distance to prevent jerky updates
                float cpSmooth = 10f * Time.deltaTime;
                if (_cutState == 0)
                {
                    _cpWorldNormal0 = Vector3.Slerp(_cpWorldNormal0, worldNormal, cpSmooth).normalized;
                    if (_cpWorldNormal0.sqrMagnitude < 0.001f) _cpWorldNormal0 = worldNormal;
                    _cpDist0 = Mathf.Lerp(_cpDist0, cpDist, cpSmooth);
                    _vr.SetCutPlane(0, _cpWorldNormal0, _cpDist0);
                }
                else
                {
                    _cpWorldNormal1 = Vector3.Slerp(_cpWorldNormal1, worldNormal, cpSmooth).normalized;
                    if (_cpWorldNormal1.sqrMagnitude < 0.001f) _cpWorldNormal1 = worldNormal;
                    _cpDist1 = Mathf.Lerp(_cpDist1, cpDist, cpSmooth);
                    _vr.SetCutPlane(1, _cpWorldNormal1, _cpDist1);
                    _vr.SetCutPlaneLocal(0, _lockedLocalNormal0, _lockedLocalDist0);
                }
            }
        }
        else
        {
            _cpGrabbing = false;
        }

        // Thumbstick fine-tune (works whether grabbing or not)
        if (Mathf.Abs(lStick.y) > 0.15f)
        {
            if (_cutState == 0)
            {
                _cpDist0 += lStick.y * Time.deltaTime * 0.4f;
                _cpDist0 = Mathf.Clamp(_cpDist0, -0.5f, 0.5f);
                _vr.SetCutPlane(0, _cpWorldNormal0, _cpDist0);
            }
            else if (_cutState == 1)
            {
                _cpDist1 += lStick.y * Time.deltaTime * 0.4f;
                _cpDist1 = Mathf.Clamp(_cpDist1, -0.5f, 0.5f);
                _vr.SetCutPlane(1, _cpWorldNormal1, _cpDist1);
                _vr.SetCutPlaneLocal(0, _lockedLocalNormal0, _lockedLocalDist0);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  CUT PLANE VISUALS  (two semi-transparent discs)
    // ─────────────────────────────────────────────────────────────
    void BuildCutPlaneVisuals()
    {
        // Transparent interior + thick colored border shader. Falls back
        // to Sprites/Default (semi-opaque) if the shader got stripped from
        // the build for any reason — still visible, just less clean.
        Shader borderShader = Resources.Load<Shader>("CutPlaneBorder");
        if (borderShader == null) borderShader = Shader.Find("RegionalAR/CutPlaneBorder");
        if (borderShader == null) borderShader = Shader.Find("Sprites/Default");

        // Plane 1 — cyan border
        _cpVis0 = GameObject.CreatePrimitive(PrimitiveType.Quad);
        _cpVis0.name = "CutPlaneVis0";
        Destroy(_cpVis0.GetComponent<Collider>());
        _cpVisR0 = _cpVis0.GetComponent<MeshRenderer>();
        var mat0 = new Material(borderShader);
        if (mat0.HasProperty("_BorderColor"))
            mat0.SetColor("_BorderColor", new Color(0.2f, 0.8f, 1f, 1f));
        else
            mat0.color = new Color(0.2f, 0.8f, 1f, 0.25f);
        if (mat0.HasProperty("_BorderWidth")) mat0.SetFloat("_BorderWidth", 0.05f);
        mat0.renderQueue = 4000;  // after Transparent (3000), before markers (5000)
        _cpVisR0.material = mat0;
        _cpVis0.SetActive(false);

        // Plane 2 — orange border
        _cpVis1 = GameObject.CreatePrimitive(PrimitiveType.Quad);
        _cpVis1.name = "CutPlaneVis1";
        Destroy(_cpVis1.GetComponent<Collider>());
        _cpVisR1 = _cpVis1.GetComponent<MeshRenderer>();
        var mat1 = new Material(borderShader);
        if (mat1.HasProperty("_BorderColor"))
            mat1.SetColor("_BorderColor", new Color(1f, 0.6f, 0.15f, 1f));
        else
            mat1.color = new Color(1f, 0.6f, 0.15f, 0.25f);
        if (mat1.HasProperty("_BorderWidth")) mat1.SetFloat("_BorderWidth", 0.05f);
        mat1.renderQueue = 4000;
        _cpVisR1.material = mat1;
        _cpVis1.SetActive(false);
    }

    void UpdateCutPlaneVisuals()
    {
        UpdateOnePlaneVisual(0, _cpVis0);
        UpdateOnePlaneVisual(1, _cpVis1);
    }

    void UpdateOnePlaneVisual(int idx, GameObject vis)
    {
        if (vis == null || _vr == null) return;

        var cp = _vr.cutPlanes[idx];
        if (!cp.active)
        {
            vis.SetActive(false);
            return;
        }

        vis.SetActive(true);

        Vector3 localNormal = cp.normal;
        float dist = cp.dist;

        // Object-space center is at local (0,0,0) since vertex positions are -0.5..0.5
        // The cut plane in object space: center + normal * dist
        Vector3 localPlanePos = localNormal * dist;
        Vector3 worldPlanePos = transform.TransformPoint(localPlanePos);
        Vector3 worldNormal = transform.TransformDirection(localNormal).normalized;

        // Offset slightly toward camera to prevent Z-fighting with volume surface
        vis.transform.position = worldPlanePos + worldNormal * 0.002f;
        vis.transform.rotation = Quaternion.LookRotation(worldNormal);

        // Scale to match volume size
        float s = transform.localScale.x * 1.6f;  // 2× volume size to cover most anatomy
        vis.transform.localScale = new Vector3(s, s, 1f);
    }

    // ─────────────────────────────────────────────────────────────
    //  VISUAL FEEDBACK
    // ─────────────────────────────────────────────────────────────
    void SetHighlight(bool on)
    {
        if (on == _highlighted || _mr == null) return;
        _highlighted = on;
        if (_mr.material != null)
            _mr.material.SetFloat("_AlphaScale", on ? 2.5f : 1.5f);
    }

    // ─────────────────────────────────────────────────────────────
    //  MOUSE FALLBACK
    // ─────────────────────────────────────────────────────────────
    void HandleMouseInput()
    {
        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(scroll) > 0.001f)
        {
            float s = Mathf.Clamp(transform.localScale.x + scroll * 0.3f, MinScale, MaxScale);
            transform.localScale = Vector3.one * s;
        }
        if (Input.GetMouseButtonDown(1)) { _mouseDrag = true; _mousePrev = Input.mousePosition; }
        if (Input.GetMouseButtonUp(1))     _mouseDrag = false;
        if (_mouseDrag)
        {
            Vector3 d = Input.mousePosition - _mousePrev;
            transform.Rotate(Vector3.up,   -d.x * 0.3f, Space.World);
            transform.Rotate(Vector3.right,  d.y * 0.3f, Space.World);
            _mousePrev = Input.mousePosition;
        }
    }

    /// Called by WiFiDownloader's "Cut Plane" button on the control panel.
    public void CycleCutPlaneState()
    {
        _cutState = (_cutState + 1) % 4;
        ApplyCutState();
        Debug.Log($"[RegionalAR] CycleCutPlaneState → state {_cutState}");
    }

    public void MoveTo(Vector3 worldPos) => transform.position = worldPos;
    public void ScaleBy(float mult)
    {
        float s = Mathf.Clamp(transform.localScale.x * mult, MinScale, MaxScale);
        transform.localScale = Vector3.one * s;
    }
    public void RotateY(float deg) => transform.Rotate(Vector3.up, deg, Space.World);
}
