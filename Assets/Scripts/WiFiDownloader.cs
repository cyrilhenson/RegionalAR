/// RegionalAR — Control Panel + WiFi Panel (separated)
///
/// CONTROLS:
///   X or Menu       = Open / Close control panel
///   Either Trigger  = Click panel buttons (point and pull)
///   Left Grip       = Grab & drag either panel (proximity-based)
///   Both Grips      = Scale panel (while left is grabbing)
///   B               = Reset volume + panels + cut planes
///
/// Two independent panels:
///   CONTROL PANEL — tissue layer toggles, bring here, reset, close, WiFi button
///   WIFI PANEL    — IP display, download, progress, status, close
///
/// Each button/toggle has its own world-scale BoxCollider. Button hits
/// use Collider.Raycast per-button (not Physics.Raycast) so buttons
/// always take priority over the backdrop proxy regardless of viewing angle.
///
/// Trigger detection uses Axis1D (analog) with manual edge detection.
///
/// Attach to the Volume GameObject.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using UnityEngine.EventSystems;

public class WiFiDownloader : MonoBehaviour
{
    [Header("Auto-found if empty")]
    public VolumeRenderer volumeRenderer;

    const int    HTTP_PORT = 8765;
    const int    UDP_PORT  = 8766;
    const string PREFS_IP  = "RegionalAR_LastServerIP";

    // ── Button audio feedback ──────────────────────────────────
    AudioSource _uiAudio;
    AudioClip   _dropClip;

    // ── Per-button 3D collider ──────────────────────────────────
    struct BtnCol
    {
        public GameObject go;
        public BoxCollider col;
        public Transform  panelTF;     // which panel owns this button
        public Vector3    localOffset;  // offset from panel center (meters at base scale)
        public Vector3    baseSize;     // collider scale at base panel scale
        public Action     onClick;      // null for toggles
        public Toggle     toggle;       // null for buttons
        public string     name;
        public Image      bg;           // button background image (for flash feedback)
        public Color      bgOrig;       // original background color
    }
    List<BtnCol> _btnCols = new List<BtnCol>();

    // ── Draggable slider collider ──────────────────────────────
    struct SliderCol
    {
        public GameObject go;
        public BoxCollider col;
        public Transform   panelTF;
        public Vector3     localOffset;   // center offset from panel (meters at base scale)
        public Vector3     baseSize;      // collider scale at base panel scale
        public float       widthMeters;   // slider width in world meters (at base scale)
        public Image       fill;          // fill bar (stretches 0→1)
        public RectTransform knob;        // knob indicator
        public float       value;         // current 0..1
        public float       minVal, maxVal; // mapped range
        public System.Action<float> onChanged; // callback with mapped value
        public string      name;
    }
    List<SliderCol> _sliderCols = new List<SliderCol>();
    int _activeSlider = -1;  // index of slider being dragged (-1 = none)

    // ── Control Panel ───────────────────────────────────────────
    Canvas        _ctrlCanvas;
    Transform     _ctrlTF;
    GameObject    _ctrlBG;
    bool          _ctrlOpen;
    GameObject    _ctrlProxy;
    BoxCollider   _ctrlProxyCol;

    // ── WiFi Panel ──────────────────────────────────────────────
    Canvas        _wifiCanvas;
    Transform     _wifiTF;
    GameObject    _wifiBG;
    bool          _wifiOpen;
    GameObject    _wifiProxy;
    BoxCollider   _wifiProxyCol;
    InputField    _ipInput;
    Text          _statusText;
    Button        _downloadBtn;
    RectTransform _fillRT;

    // ── Hint ────────────────────────────────────────────────────
    Canvas     _hintCanvas;
    GameObject _hintRoot;
    Transform  _hintTF;
    GameObject _hintProxy;
    BoxCollider _hintProxyCol;

    // ── Laser pointers ──────────────────────────────────────────
    LineRenderer _laserR, _laserL;
    GameObject   _dotR, _dotL;

    // ── Panel grab & scale ──────────────────────────────────────
    bool      _draggingPanel;
    Transform _dragTarget;        // which panel is being dragged
    Vector3   _dragOffset;
    Vector3   _dragStartCtrl;     // controller pos when drag began
    Vector3   _dragStartPanel;    // panel pos when drag began
    const float DRAG_SPEED = 1.2f; // 20% faster than 1:1 tracking
    float     _scaleStartDist;
    Vector3   _scaleStartScale;

    // ── Axis1D trigger edge detection ───────────────────────────
    bool _prevTrigR, _prevTrigL;

    // ── Rig / camera ────────────────────────────────────────────
    Transform _rigRoot;
    Camera    _vrCam;
    Transform _leftHandAnchor;
    Transform _rightHandAnchor;

    // ── Hand tracking ───────────────────────────────────────────
    OVRHand       _handL, _handR;
    LineRenderer  _handLaserL, _handLaserR;
    GameObject    _handDotL, _handDotR;
    bool          _prevPinchL, _prevPinchR;   // edge detection
    const float   PINCH_THRESH = 0.7f;
    const float   FIST_THRESH  = 0.5f;       // per-finger for fist detection

    // ── Auto-discovery ──────────────────────────────────────────
    UdpClient _udpListener;
    Thread    _udpThread;
    string    _discoveredIP;
    bool      _discoveryRunning;

    // ── Camera & tracking retry ────────────────────────────────
    bool _needsCameraRetry = true;   // retry until camera is confirmed
    int  _cameraRetryCount;
    bool _trackingReady;             // true once camera is at plausible head height
    bool _needsReposition;           // reposition panels once tracking is ready

    // ── Scan Library ────────────────────────────────────────────
    Canvas        _libCanvas;
    Transform     _libTF;
    GameObject    _libBG;
    bool          _libOpen;
    GameObject    _libProxy;
    BoxCollider   _libProxyCol;
    GameObject    _libContent;       // parent for scan entry buttons
    string        _activeScanName;
    List<ScanEntry> _scanEntries = new List<ScanEntry>();

    struct ScanEntry
    {
        public string name;
        public string path;
        public long   sizeBytes;
        public string date;        // human-readable
    }

    // ── Debug ───────────────────────────────────────────────────

    // ═════════════════════════════════════════════════════════════
    void Start()
    {
        if (volumeRenderer == null)
            volumeRenderer = FindObjectOfType<VolumeRenderer>();

        // Apply GPU/CPU performance settings (FFR, MSAA, refresh rate, etc.)
        if (FindObjectOfType<PerformanceManager>() == null)
        {
            var perfGO = new GameObject("RegionalAR_PerfManager");
            perfGO.AddComponent<PerformanceManager>();
            DontDestroyOnLoad(perfGO);
        }

        // Audio feedback for button presses
        _uiAudio = gameObject.AddComponent<AudioSource>();
        _uiAudio.playOnAwake = false;
        _uiAudio.spatialBlend = 0f;  // 2D sound — always audible
        _uiAudio.volume = 1.0f;
        _dropClip = Resources.Load<AudioClip>("bong_001");
        if (_dropClip == null)
        {
            Debug.LogWarning("[RegionalAR] bong_001 not found in Resources/, trying water_drop fallback");
            _dropClip = Resources.Load<AudioClip>("water_drop");
        }
        if (_dropClip == null)
            Debug.LogWarning("[RegionalAR] No UI audio clip found in Resources/");
        else
            Debug.Log($"[RegionalAR] Loaded UI clip: {_dropClip.name}, length={_dropClip.length}s, samples={_dropClip.samples}");

        SetupEventSystem();
        StartCoroutine(InstallBundledSample());
        StartCoroutine(InitAfterXR());
        StartUDPDiscovery();
    }

    void OnDestroy() { StopUDPDiscovery(); }

    IEnumerator InitAfterXR()
    {
        yield return new WaitForSeconds(1.0f);
        yield return null;
        CacheCamera();
        EnsureHandTracking();

        BuildLaserPointers();
        try { BuildControlPanel(); }
        catch (Exception ex) { Debug.LogError($"[RegionalAR] BuildControlPanel FAILED: {ex.Message}\n{ex.StackTrace}"); }
        try { BuildWiFiPanel(); }
        catch (Exception ex) { Debug.LogError($"[RegionalAR] BuildWiFiPanel FAILED: {ex.Message}\n{ex.StackTrace}"); }
        try { BuildLibPanel(); }
        catch (Exception ex) { Debug.LogError($"[RegionalAR] BuildLibPanel FAILED: {ex.Message}\n{ex.StackTrace}"); }
        try { BuildHint(); }
        catch (Exception ex) { Debug.LogError($"[RegionalAR] BuildHint FAILED: {ex.Message}\n{ex.StackTrace}"); }

        yield return new WaitForSeconds(0.5f);
        CacheCamera();
        PositionHintInFront();

        // Auto-open the control panel on startup so the user sees it immediately
        if (!_ctrlOpen)
            ToggleCtrlPanel();

        Debug.Log($"[RegionalAR] WiFiDownloader ready. BtnCols={_btnCols.Count} ctrlTF={_ctrlTF != null} ctrlBG={_ctrlBG != null} wifiTF={_wifiTF != null}");
    }

    void CacheCamera()
    {
        // Primary: get camera directly from OVRCameraRig.centerEyeAnchor
        // (same pattern Meta uses in OVRRaycaster — Camera.main is unreliable on Quest 3)
        var ovrRig = FindObjectOfType<OVRCameraRig>();
        if (ovrRig != null)
        {
            _rigRoot = ovrRig.transform;
            _leftHandAnchor  = ovrRig.leftHandAnchor;
            _rightHandAnchor = ovrRig.rightHandAnchor;
            if (ovrRig.centerEyeAnchor != null)
            {
                var cam = ovrRig.centerEyeAnchor.GetComponent<Camera>();
                if (cam != null) _vrCam = cam;
            }
            // Cache OVRHand for hand tracking (may be on hand anchor children)
            if (_handL == null && ovrRig.leftHandAnchor != null)
                _handL = ovrRig.leftHandAnchor.GetComponentInChildren<OVRHand>();
            if (_handR == null && ovrRig.rightHandAnchor != null)
                _handR = ovrRig.rightHandAnchor.GetComponentInChildren<OVRHand>();
        }
        // Fallback: Camera.main
        if (_vrCam == null) _vrCam = Camera.main;
        // Last resort: any Camera in the scene
        if (_vrCam == null) _vrCam = FindObjectOfType<Camera>();

        if (_rigRoot == null && _vrCam != null)
        {
            if (_vrCam.transform.parent != null && _vrCam.transform.parent.parent != null)
                _rigRoot = _vrCam.transform.parent.parent;
            else
                _rigRoot = _vrCam.transform.root;
        }
        Debug.Log($"[RegionalAR] CacheCamera: cam={_vrCam != null} rig={_rigRoot != null}");
    }

    Camera Cam()
    {
        if (_vrCam == null) CacheCamera();
        return _vrCam;
    }

    /// <summary>
    /// Public accessor for the cached VR camera. WiFiDownloader's camera
    /// caching is the most reliable on Quest 3 — other scripts should use
    /// this rather than FindObjectOfType or Camera.main.
    /// </summary>
    public Camera GetCachedCamera()
    {
        if (_vrCam == null) CacheCamera();
        return _vrCam;
    }

    /// <summary>
    /// Ensures OVRHand components exist on the hand anchors for hand tracking.
    /// If OVRHandPrefab is already in the scene (under hand anchors), this
    /// finds and caches them. If not, adds OVRHand components directly.
    /// </summary>
    void EnsureHandTracking()
    {
        var rig = FindObjectOfType<OVRCameraRig>();
        if (rig == null) return;

        // Try to find existing OVRHand in the hierarchy
        if (rig.leftHandAnchor != null)
        {
            _handL = rig.leftHandAnchor.GetComponentInChildren<OVRHand>();
            if (_handL == null)
            {
                // Add OVRHand to the hand anchor itself
                var go = new GameObject("OVRHandL");
                go.transform.SetParent(rig.leftHandAnchor, false);
                _handL = go.AddComponent<OVRHand>();
                Debug.Log("[RegionalAR] Added OVRHand to left hand anchor");
            }
        }

        if (rig.rightHandAnchor != null)
        {
            _handR = rig.rightHandAnchor.GetComponentInChildren<OVRHand>();
            if (_handR == null)
            {
                var go = new GameObject("OVRHandR");
                go.transform.SetParent(rig.rightHandAnchor, false);
                _handR = go.AddComponent<OVRHand>();
                Debug.Log("[RegionalAR] Added OVRHand to right hand anchor");
            }
        }

        // NOTE: Hand tracking must also be enabled in OVR Manager Inspector:
        //   OVRCameraRig > OVR Manager > Quest Features > Hand Tracking Support = "Controllers and Hands"
        // Without this, OVRHand.IsTracked will always be false (gracefully handled).
        Debug.Log($"[RegionalAR] Hand tracking setup: L={_handL != null} R={_handR != null}");
    }

    void Set3DDebug(string msg)
    {
        // Debug cube removed; method kept for download status logging
        Debug.Log($"[RegionalAR] {msg.Replace('\n', ' ')}");
    }

    void Update()
    {
        // NOTE: do NOT early-return if _ctrlTF==null — lasers, tracking,
        // and debug display must always run regardless of panel state

        // ── Camera retry: ensure canvases have a valid worldCamera ──
        if (_needsCameraRetry && _cameraRetryCount < 600)
        {
            _cameraRetryCount++;
            CacheCamera();
            Camera cam = _vrCam;
            if (cam != null)
            {
                if (_ctrlCanvas != null && _ctrlCanvas.worldCamera == null)
                    _ctrlCanvas.worldCamera = cam;
                if (_wifiCanvas != null && _wifiCanvas.worldCamera == null)
                    _wifiCanvas.worldCamera = cam;
                if (_libCanvas != null && _libCanvas.worldCamera == null)
                    _libCanvas.worldCamera = cam;
                if (_hintCanvas != null && _hintCanvas.worldCamera == null)
                {
                    _hintCanvas.worldCamera = cam;
                    PositionHintInFront();
                }
                _needsCameraRetry = false;
            }
        }

        // ── Tracking retry: wait until head tracking gives a real position ──
        // Quest 3 tracking starts with camera at (0,0,0); once active it
        // moves to actual head height (typically 0.8–2.0 m for seated/standing)
        if (!_trackingReady && _vrCam != null)
        {
            float camY = _vrCam.transform.position.y;
            if (camY > 0.6f)   // camera above 60cm → tracking is active
            {
                _trackingReady = true;
                _needsReposition = true;
                Debug.Log($"[RegionalAR] Tracking ready at y={camY:F2}");
            }
        }

        // ── Reposition panels once tracking is confirmed ──────────
        if (_needsReposition && _trackingReady)
        {
            _needsReposition = false;
            if (_ctrlOpen && _ctrlTF != null)
                SnapPanelInFront(_ctrlTF, _ctrlCanvas, _ctrlProxy, _ctrlProxyCol);
            if (_wifiOpen && _wifiTF != null)
            {
                SnapPanelInFront(_wifiTF, _wifiCanvas, _wifiProxy, _wifiProxyCol);
                Camera cam = Cam();
                if (cam != null)
                {
                    _wifiTF.position += cam.transform.right * 0.35f;
                    var ws = _wifiTF.GetComponent<PanelStabilizer>();
                    if (ws != null) ws.SnapToTarget();
                }
            }
            if (_libOpen && _libTF != null)
            {
                SnapPanelInFront(_libTF, _libCanvas, _libProxy, _libProxyCol);
                Camera cam2 = Cam();
                if (cam2 != null)
                {
                    _libTF.position += cam2.transform.right * -0.35f;
                    var ls = _libTF.GetComponent<PanelStabilizer>();
                    if (ls != null) ls.SnapToTarget();
                }
            }
            PositionHintInFront();
            Set3DDebug($"REPOSITIONED\ny={_vrCam.transform.position.y:F2}\nCtrl={_ctrlOpen}");
        }

        // Auto-discovered server → fill IP field only (NO auto-download)
        // User must explicitly open WiFi panel and click Download.
        if (_discoveredIP != null && _ipInput != null && !_downloading)
        {
            _ipInput.text = _discoveredIP;
            _discoveredIP = null;
            if (!_downloadedThisSession)
            {
                SetStatus($"Server found: {_ipInput.text.Trim()} — open WiFi to download");
            }
            else
            {
                SetStatus($"Server: {_ipInput.text.Trim()} (already downloaded)");
            }
        }
        else if (_discoveredIP != null && _downloading)
        {
            // Silently discard repeated broadcasts while download is in progress
            _discoveredIP = null;
        }

        #if !UNITY_EDITOR || UNITY_ANDROID
        try
        {
            // X / Menu = toggle control panel only
            if (OVRInput.GetDown(OVRInput.Button.Three)) ToggleCtrlPanel();
            if (OVRInput.GetDown(OVRInput.Button.Start)) ToggleCtrlPanel();
            // B = reset panels to front (always re-cache camera first)
            if (OVRInput.GetDown(OVRInput.Button.Two))
            {
                CacheCamera();
                _trackingReady = true; // user pressed B, trust current position
                if (_ctrlOpen && _ctrlTF != null)
                    SnapPanelInFront(_ctrlTF, _ctrlCanvas, _ctrlProxy, _ctrlProxyCol);
                if (_wifiOpen && _wifiTF != null)
                    SnapPanelInFront(_wifiTF, _wifiCanvas, _wifiProxy, _wifiProxyCol);
                if (_libOpen && _libTF != null)
                    SnapPanelInFront(_libTF, _libCanvas, _libProxy, _libProxyCol);
                if (_hintRoot != null && _hintRoot.activeSelf)
                    PositionHintInFront();
            }
        }
        catch { }
        #endif
        #if UNITY_EDITOR
        if (Input.GetKeyDown(KeyCode.P)) ToggleCtrlPanel();
        #endif

        // Sync all collider positions
        SyncAllColliders();

        // Panel grab with left grip
        HandlePanelGrab();

        UpdateLasers();
    }

    // ═════════════════════════════════════════════════════════════
    //  CONTROLLER HELPERS
    // ═════════════════════════════════════════════════════════════
    Vector3 CtrlPos(OVRInput.Controller c)
    {
        if (c == OVRInput.Controller.LTouch && _leftHandAnchor != null)
            return _leftHandAnchor.position;
        if (c == OVRInput.Controller.RTouch && _rightHandAnchor != null)
            return _rightHandAnchor.position;
        Vector3 lp = OVRInput.GetLocalControllerPosition(c);
        return _rigRoot != null ? _rigRoot.TransformPoint(lp) : lp;
    }

    Vector3 CtrlFwd(OVRInput.Controller c)
    {
        if (c == OVRInput.Controller.LTouch && _leftHandAnchor != null)
            return _leftHandAnchor.forward;
        if (c == OVRInput.Controller.RTouch && _rightHandAnchor != null)
            return _rightHandAnchor.forward;
        Quaternion lr = OVRInput.GetLocalControllerRotation(c);
        Quaternion wr = _rigRoot != null ? _rigRoot.rotation * lr : lr;
        return wr * Vector3.forward;
    }

    // ═════════════════════════════════════════════════════════════
    //  BUTTON COLLIDER SYSTEM
    // ═════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a world-scale BoxCollider for a button. canvasPos/canvasSize
    /// are in canvas-pixel units, multiplied by 0.001 for world offsets.
    /// </summary>
    void RegBtnCol(Transform panel, Vector2 canvasPos, Vector2 canvasSize,
                   Action onClick, Toggle toggle, string name, Image bg = null, Color bgOrig = default)
    {
        var go = new GameObject($"BC_{name.Replace(" ", "")}");
        var col = go.AddComponent<BoxCollider>();
        col.size = Vector3.one;
        go.transform.localScale = new Vector3(
            canvasSize.x * 0.001f,
            canvasSize.y * 0.001f,
            0.025f
        );

        var baseSize = new Vector3(canvasSize.x * 0.001f, canvasSize.y * 0.001f, 0.025f);
        _btnCols.Add(new BtnCol
        {
            go          = go,
            col         = col,
            panelTF     = panel,
            localOffset = new Vector3(canvasPos.x * 0.001f, canvasPos.y * 0.001f, 0),
            baseSize    = baseSize,
            onClick     = onClick,
            toggle      = toggle,
            name        = name,
            bg          = bg,
            bgOrig      = bgOrig
        });
        go.SetActive(false);
    }

    /// <summary>
    /// Positions all button colliders 1.5cm in front of their owning panel.
    /// </summary>
    /// Base panel scale (panels are created at 0.001 = 1 canvas-pixel-per-mm)
    const float BASE_PANEL_SCALE = 0.001f;

    void SyncAllColliders()
    {
        for (int i = 0; i < _btnCols.Count; i++)
        {
            var bc = _btnCols[i];
            if (bc.panelTF == null) continue;

            // Scale factor: how much the panel has been resized from its base scale
            float sf = bc.panelTF.localScale.x / BASE_PANEL_SCALE;

            bc.go.transform.position = bc.panelTF.position
                + bc.panelTF.right   * (bc.localOffset.x * sf)
                + bc.panelTF.up      * (bc.localOffset.y * sf)
                - bc.panelTF.forward * (0.015f * sf);
            bc.go.transform.rotation = bc.panelTF.rotation;
            bc.go.transform.localScale = bc.baseSize * sf;
        }
        // Sync slider colliders
        for (int i = 0; i < _sliderCols.Count; i++)
        {
            var sc = _sliderCols[i];
            if (sc.panelTF == null) continue;

            float sf = sc.panelTF.localScale.x / BASE_PANEL_SCALE;

            sc.go.transform.position = sc.panelTF.position
                + sc.panelTF.right   * (sc.localOffset.x * sf)
                + sc.panelTF.up      * (sc.localOffset.y * sf)
                - sc.panelTF.forward * (0.015f * sf);
            sc.go.transform.rotation = sc.panelTF.rotation;
            sc.go.transform.localScale = sc.baseSize * sf;
        }
        // Sync proxies
        if (_ctrlProxy != null && _ctrlTF != null)
        {
            _ctrlProxy.transform.position = _ctrlTF.position;
            _ctrlProxy.transform.rotation = _ctrlTF.rotation;
        }
        if (_wifiProxy != null && _wifiTF != null)
        {
            _wifiProxy.transform.position = _wifiTF.position;
            _wifiProxy.transform.rotation = _wifiTF.rotation;
        }
        if (_hintProxy != null && _hintTF != null)
        {
            _hintProxy.transform.position = _hintTF.position;
            _hintProxy.transform.rotation = _hintTF.rotation;
        }

        // Force the physics engine to see the moved colliders before we raycast.
        // Without this, Collider.Raycast can miss because the internal physics
        // representation is one frame behind the visual transform.
        Physics.SyncTransforms();
    }

    // ═════════════════════════════════════════════════════════════
    //  BACKDROP PROXY CREATION
    // ═════════════════════════════════════════════════════════════
    void CreateProxy(out GameObject proxy, out BoxCollider proxyCol,
                     float w, float h, string name)
    {
        proxy = GameObject.CreatePrimitive(PrimitiveType.Cube);
        proxy.name = name;
        proxy.transform.localScale = new Vector3(w, h, 0.006f);
        proxyCol = proxy.GetComponent<BoxCollider>();
        var mr = proxy.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            var sh = Shader3D();
            if (sh != null)
            {
                var mat = new Material(sh);
                mat.color = new Color(0.08f, 0.15f, 0.4f, 0.06f);
                mr.material = mat;
            }
        }
        proxy.SetActive(false);
    }

    // ═════════════════════════════════════════════════════════════
    //  PANEL GRAB & SCALE
    // ═════════════════════════════════════════════════════════════
    void HandlePanelGrab()
    {
        #if !UNITY_EDITOR || UNITY_ANDROID
        try
        {
            // Get position + grip from either controllers or hand tracking
            bool lHandTracked = _handL != null && _handL.IsTracked;
            bool rHandTracked = _handR != null && _handR.IsTracked;

            Vector3 lPos = lHandTracked ? HandPos(_handL) : CtrlPos(OVRInput.Controller.LTouch);
            Vector3 rPos = rHandTracked ? HandPos(_handR) : CtrlPos(OVRInput.Controller.RTouch);

            bool lGrip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger,
                                       OVRInput.Controller.LTouch)
                      || IsHandGrabbing(_handL);
            bool rGrip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger,
                                       OVRInput.Controller.RTouch)
                      || IsHandGrabbing(_handR);

            if (!_draggingPanel && lGrip)
            {
                // Check proximity to any panel (ctrl, wifi, library, hint)
                Transform target = null;
                if (_ctrlOpen && _ctrlTF != null && Vector3.Distance(lPos, _ctrlTF.position) < 0.4f)
                    target = _ctrlTF;
                else if (_wifiOpen && _wifiTF != null && Vector3.Distance(lPos, _wifiTF.position) < 0.4f)
                    target = _wifiTF;
                else if (_libOpen && _libTF != null && Vector3.Distance(lPos, _libTF.position) < 0.4f)
                    target = _libTF;
                else if (_hintRoot != null && _hintRoot.activeSelf
                         && Vector3.Distance(lPos, _hintRoot.transform.position) < 0.4f)
                    target = _hintRoot.transform;

                if (target != null)
                {
                    _draggingPanel = true;
                    _dragTarget = target;
                    _dragOffset = target.position - lPos;
                    _dragStartCtrl = lPos;
                    _dragStartPanel = target.position;
                    _scaleStartDist = 0;
                    PlayUISound();
                    PlayHapticPulse();
                }
            }

            if (_draggingPanel)
            {
                if (lGrip && _dragTarget != null)
                {
                    // Amplified drag: 20% faster than raw controller movement
                    Vector3 delta = lPos - _dragStartCtrl;
                    _dragTarget.position = _dragStartPanel + delta * DRAG_SPEED;
                    FacePanelToUser(_dragTarget);

                    // Both grips = scale
                    if (rGrip)
                    {
                        float curDist = Vector3.Distance(lPos, rPos);
                        if (_scaleStartDist > 0.02f)
                        {
                            float factor = Mathf.Clamp(curDist / _scaleStartDist, 0.3f, 3f);
                            _dragTarget.localScale = _scaleStartScale * factor;
                        }
                        else
                        {
                            _scaleStartDist = curDist;
                            _scaleStartScale = _dragTarget.localScale;
                        }
                    }
                    else { _scaleStartDist = 0; }
                }
                else
                {
                    _draggingPanel = false;
                    _dragTarget = null;
                    _scaleStartDist = 0;
                }
            }
        }
        catch { }
        #endif
    }

    void FacePanelToUser(Transform panel)
    {
        Camera cam = Cam();
        if (cam == null) return;
        Vector3 toUser = cam.transform.position - panel.position;
        toUser.y = 0;
        if (toUser.sqrMagnitude > 0.01f)
            panel.rotation = Quaternion.LookRotation(-toUser);
    }

    // ═════════════════════════════════════════════════════════════
    //  PANEL TOGGLE & POSITIONING
    // ═════════════════════════════════════════════════════════════
    void ToggleCtrlPanel()
    {
        try
        {
            _ctrlOpen = !_ctrlOpen;
            Debug.Log($"[RegionalAR] ToggleCtrlPanel: open={_ctrlOpen} ctrlBG={_ctrlBG != null} ctrlTF={_ctrlTF != null} cam={Cam() != null}");
            if (_ctrlBG != null) _ctrlBG.SetActive(_ctrlOpen);
            if (_ctrlProxy != null) _ctrlProxy.SetActive(_ctrlOpen);
            SetBtnColsActive(_ctrlTF, _ctrlOpen);
            // UNDO / CLEAR markers and opacity slider are mode-dependent
            // sub-groups; override the blanket SetBtnColsActive call.
            ApplyMarkerButtonVisibility();
            ApplyOpacitySliderVisibility();

            if (_ctrlOpen)
            {
                if (_tissueSection != null) _tissueSection.SetActive(true);
                CacheCamera();
                SnapPanelInFront(_ctrlTF, _ctrlCanvas, _ctrlProxy, _ctrlProxyCol);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[RegionalAR] ToggleCtrlPanel FAILED: {ex.Message}\n{ex.StackTrace}");
        }
    }

    void ToggleWiFiPanel()
    {
        try
        {
            _wifiOpen = !_wifiOpen;
            if (_wifiBG != null) _wifiBG.SetActive(_wifiOpen);
            if (_wifiProxy != null) _wifiProxy.SetActive(_wifiOpen);
            SetBtnColsActive(_wifiTF, _wifiOpen);
            if (_wifiOpen)
            {
                CacheCamera();
                SnapPanelInFront(_wifiTF, _wifiCanvas, _wifiProxy, _wifiProxyCol);
                // Offset slightly right of camera so it doesn't overlap control panel
                Camera cam = Cam();
                if (cam != null)
                {
                    _wifiTF.position += cam.transform.right * 0.35f;
                    var ws = _wifiTF.GetComponent<PanelStabilizer>();
                    if (ws != null) ws.SnapToTarget();
                }
            }
            Debug.Log($"[RegionalAR] WiFi panel toggled: {_wifiOpen} cam={Cam() != null}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[RegionalAR] ToggleWiFiPanel FAILED: {ex.Message}\n{ex.StackTrace}");
        }
    }

    void SetBtnColsActive(Transform panel, bool active)
    {
        for (int i = 0; i < _btnCols.Count; i++)
            if (_btnCols[i].panelTF == panel)
                _btnCols[i].go.SetActive(active);
        // Also activate/deactivate slider colliders on this panel
        for (int i = 0; i < _sliderCols.Count; i++)
            if (_sliderCols[i].panelTF == panel)
                _sliderCols[i].go.SetActive(active);
    }

    public void RepositionPanel()
    {
        CacheCamera();
        if (_ctrlOpen) SnapPanelInFront(_ctrlTF, _ctrlCanvas, _ctrlProxy, _ctrlProxyCol);
        if (_wifiOpen) SnapPanelInFront(_wifiTF, _wifiCanvas, _wifiProxy, _wifiProxyCol);
        if (_libOpen)  SnapPanelInFront(_libTF,  _libCanvas,  _libProxy,  _libProxyCol);
        PositionHintInFront();
    }


    void SnapPanelInFront(Transform panelTF, Canvas canvas, GameObject proxy, BoxCollider proxyCol)
    {
        Camera cam = Cam();
        if (cam == null)
        {
            CacheCamera();
            cam = _vrCam;
        }
        if (cam == null)
        {
            _needsCameraRetry = true;
            return;
        }
        if (canvas != null) canvas.worldCamera = cam;
        if (_ctrlCanvas != null && _ctrlCanvas.worldCamera == null) _ctrlCanvas.worldCamera = cam;
        if (_wifiCanvas != null && _wifiCanvas.worldCamera == null) _wifiCanvas.worldCamera = cam;
        if (_libCanvas  != null && _libCanvas.worldCamera  == null) _libCanvas.worldCamera  = cam;

        // If tracking hasn't initialized yet (camera at floor level),
        // position the panel anyway but flag for repositioning once tracking is ready
        float camY = cam.transform.position.y;
        if (camY < 0.6f && !_trackingReady)
        {
            _needsReposition = true;
            Debug.Log($"[RegionalAR] SnapPanel: cam.y={camY:F2} — tracking not ready, will reposition");
        }

        Vector3 fwd = cam.transform.forward; fwd.y = 0; fwd.Normalize();
        if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
        // Panel at eye level minus 15cm — comfortable reading height
        panelTF.position = cam.transform.position + fwd * 0.9f + Vector3.down * 0.15f;
        panelTF.rotation = Quaternion.LookRotation(fwd);

        // Snap the stabilizer so there's no lerp-in from the old position
        var stab = panelTF.GetComponent<PanelStabilizer>();
        if (stab != null) stab.SnapToTarget();
    }

    void PositionHintInFront()
    {
        if (_hintRoot == null) return;
        Camera cam = Cam();
        if (cam == null) return;
        if (_hintCanvas != null) _hintCanvas.worldCamera = cam;
        Vector3 fwd = cam.transform.forward; fwd.y = 0; fwd.Normalize();
        if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
        // Position hint just to the right of where the control panel appears
        Vector3 right = Vector3.Cross(fwd, Vector3.up).normalized;
        _hintRoot.transform.position = cam.transform.position
            + fwd * 1.2f + right * 0.32f + Vector3.down * 0.10f;
        _hintRoot.transform.rotation = Quaternion.LookRotation(fwd);

        var stab = _hintRoot.GetComponent<PanelStabilizer>();
        if (stab != null) stab.SnapToTarget();
    }

    void ToggleHint()
    {
        if (_hintRoot == null) return;
        bool show = !_hintRoot.activeSelf;
        _hintRoot.SetActive(show);
        if (_hintProxy != null) _hintProxy.SetActive(show);
        SetBtnColsActive(_hintTF, show);
        if (show) PositionHintInFront();
    }

    /// Close the RegionalAR app. On Android / Quest this ends the player;
    /// in the Unity Editor it exits Play Mode so development isn't interrupted.
    void QuitApp()
    {
        Debug.Log("[RegionalAR] QUIT button pressed — exiting application");
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    // ═════════════════════════════════════════════════════════════
    //  LASER POINTERS + INTERACTION
    // ═════════════════════════════════════════════════════════════
    void BuildLaserPointers()
    {
        _laserR = MakeLaser("RegionalAR_LaserR",
            new Color(0.3f, 0.8f, 1f, 0.6f), new Color(0.3f, 0.8f, 1f, 0.15f));
        _laserL = MakeLaser("RegionalAR_LaserL",
            new Color(0.3f, 1f, 0.5f, 0.5f), new Color(0.3f, 1f, 0.5f, 0.12f));
        _dotR = MakeDot("LaserDotR", Color.white);
        _dotL = MakeDot("LaserDotL", new Color(0.5f, 1f, 0.5f));

        // Hand tracking lasers (slightly different colors — warm tones)
        _handLaserR = MakeLaser("RegionalAR_HandLaserR",
            new Color(1f, 0.7f, 0.3f, 0.5f), new Color(1f, 0.7f, 0.3f, 0.12f));
        _handLaserL = MakeLaser("RegionalAR_HandLaserL",
            new Color(1f, 0.5f, 0.8f, 0.5f), new Color(1f, 0.5f, 0.8f, 0.12f));
        _handDotR = MakeDot("HandDotR", new Color(1f, 0.7f, 0.3f));
        _handDotL = MakeDot("HandDotL", new Color(1f, 0.5f, 0.8f));
    }

    /// Shader for 3D objects: LineRenderer, MeshRenderer, primitives.
    /// "Sprites/Default" works for LineRenderer + supports vertex colors.
    static Shader Shader3D()
    {
        return Shader.Find("Sprites/Default")
            ?? Shader.Find("Unlit/Color")
            ?? Shader.Find("UI/Default");
    }

    LineRenderer MakeLaser(string n, Color s, Color e)
    {
        var go = new GameObject(n);
        var lr = go.AddComponent<LineRenderer>();
        lr.startWidth = 0.003f; lr.endWidth = 0.001f;
        var sh = Shader3D();
        if (sh != null) lr.material = new Material(sh);
        lr.startColor = s; lr.endColor = e;
        lr.positionCount = 2; lr.useWorldSpace = true;
        lr.enabled = false;
        // Render lasers ABOVE panel canvases (sortingOrder 30-32) so they stay visible
        lr.sortingOrder = 50;
        return lr;
    }

    GameObject MakeDot(string n, Color c)
    {
        var d = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        d.name = n;
        d.transform.localScale = Vector3.one * 0.018f;  // slightly larger for visibility
        Destroy(d.GetComponent<Collider>());

        // Use Unlit/Color with ZTest Always so the dot is visible on top of
        // world-space Canvas panels (Sprites/Default gets depth-occluded).
        Shader sh = Shader.Find("Unlit/Color");
        if (sh == null) sh = Shader3D();
        if (sh != null)
        {
            var mat = new Material(sh) { color = c };
            mat.renderQueue = 5000; // render on top of everything
            if (mat.HasProperty("_ZTest"))
                mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            if (mat.HasProperty("_ZWrite"))
                mat.SetInt("_ZWrite", 0);
            d.GetComponent<Renderer>().material = mat;
        }
        d.SetActive(false);
        return d;
    }

    void UpdateLasers()
    {
        #if !UNITY_EDITOR || UNITY_ANDROID
        // Controller-only mode (hand tracking toggle removed for now)
        // Hide hand lasers if they exist
        if (_handLaserR != null) _handLaserR.enabled = false;
        if (_handDotR != null) _handDotR.SetActive(false);
        if (_handLaserL != null) _handLaserL.enabled = false;
        if (_handDotL != null) _handDotL.SetActive(false);

        try { ProcessController(OVRInput.Controller.RTouch, _laserR, _dotR); }
        catch { _laserR.enabled = false; _dotR.SetActive(false); }

        try { ProcessController(OVRInput.Controller.LTouch, _laserL, _dotL); }
        catch { _laserL.enabled = false; _dotL.SetActive(false); }
        #else
        Camera cam = Cam();
        if (cam != null && Input.GetMouseButtonDown(0))
        {
            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            int idx = RaycastButtons(ray, 4f);
            if (idx >= 0) InvokeBtn(idx);
        }
        #endif
    }

    /// <summary>
    /// Checks each active button collider individually with Collider.Raycast.
    /// Returns the closest hit button index, or -1 if none.
    /// This guarantees buttons always take priority over the backdrop proxy
    /// regardless of viewing angle.
    /// </summary>
    int RaycastButtons(Ray ray, float maxDist)
    {
        int bestIdx = -1;
        float bestDist = float.MaxValue;

        for (int i = 0; i < _btnCols.Count; i++)
        {
            var bc = _btnCols[i];
            if (bc.col == null || !bc.go.activeSelf) continue;
            RaycastHit bh;
            if (bc.col.Raycast(ray, out bh, maxDist) && bh.distance < bestDist)
            {
                bestDist = bh.distance;
                bestIdx = i;
            }
        }
        return bestIdx;
    }

    /// <summary>
    /// Processes one controller: draws laser, checks buttons via per-collider
    /// Raycast, falls back to Physics.Raycast for visual feedback.
    /// </summary>
    void ProcessController(OVRInput.Controller ctrl, LineRenderer laser, GameObject dot)
    {
        Vector3 pos = CtrlPos(ctrl);
        Vector3 fwd = CtrlFwd(ctrl);
        float   maxDist = 3f;
        Vector3 endPt = pos + fwd * maxDist;
        bool    showDot = false;

        // ── Axis1D trigger with manual edge detection ───────────
        float trigVal = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, ctrl);
        bool trigPressed = trigVal > 0.7f;
        bool trigDown;
        if (ctrl == OVRInput.Controller.RTouch)
        { trigDown = trigPressed && !_prevTrigR; _prevTrigR = trigPressed; }
        else
        { trigDown = trigPressed && !_prevTrigL; _prevTrigL = trigPressed; }

        // ── Release slider when trigger released ────────────────
        if (!trigPressed && _activeSlider >= 0)
            _activeSlider = -1;

        Ray ray = new Ray(pos, fwd);

        // ── Slider drag (continuous while trigger held) ─────────
        bool onSlider = false;
        if (trigPressed)
        {
            int sliderIdx = RaycastSliders(ray, maxDist);
            if (sliderIdx >= 0 && (_activeSlider < 0 || _activeSlider == sliderIdx))
            {
                bool wasIdle = _activeSlider < 0;
                _activeSlider = sliderIdx;
                if (wasIdle) { PlayHapticPulse(); }
            }
            if (_activeSlider >= 0)
            {
                var sc = _sliderCols[_activeSlider];
                RaycastHit sh;
                if (sc.col.Raycast(ray, out sh, maxDist))
                {
                    endPt = sh.point;
                    showDot = true;
                    onSlider = true;

                    // Cyan dot when dragging slider
                    var dotR = dot.GetComponent<Renderer>();
                    if (dotR != null) dotR.material.color = new Color(0f, 0.85f, 0.95f);

                    // Map hit position along slider's local X axis to 0..1
                    Vector3 localHit = sc.go.transform.InverseTransformPoint(sh.point);
                    float t = Mathf.Clamp01(localHit.x / 1f + 0.5f); // collider is unit box, -0.5..0.5
                    UpdateSliderValue(_activeSlider, t);
                }
            }
        }

        if (!onSlider)
        {
        // ── Step 1: Check each button collider individually ─────
        int btnIdx = RaycastButtons(ray, maxDist);

        if (btnIdx >= 0)
        {
            // Hit a button — use the button collider's hit point for the dot
            RaycastHit bh;
            _btnCols[btnIdx].col.Raycast(ray, out bh, maxDist);
            endPt = bh.point;
            showDot = true;

            // Green dot on button
            var dotR = dot.GetComponent<Renderer>();
            if (dotR != null) dotR.material.color = new Color(0.2f, 1f, 0.3f);

            if (trigDown)
            {
                InvokeBtn(btnIdx);
                Debug.Log($"[RegionalAR] CLICK '{_btnCols[btnIdx].name}' via {ctrl}");
            }
        }
        else
        {
            // ── Step 2: Regular Physics.Raycast for visual feedback ──
            RaycastHit hit;
            if (Physics.Raycast(pos, fwd, out hit, maxDist))
            {
                endPt = hit.point;
                showDot = true;

                bool isProxy = (_ctrlOpen && _ctrlProxyCol != null && hit.collider == _ctrlProxyCol)
                            || (_wifiOpen && _wifiProxyCol != null && hit.collider == _wifiProxyCol)
                            || (_libOpen  && _libProxyCol  != null && hit.collider == _libProxyCol);

                if (isProxy)
                {
                    var dotR = dot.GetComponent<Renderer>();
                    if (dotR != null) dotR.material.color = new Color(1f, 0.6f, 0.1f);
                }
                else
                {
                    var dotR = dot.GetComponent<Renderer>();
                    if (dotR != null)
                        dotR.material.color = (ctrl == OVRInput.Controller.RTouch)
                            ? Color.white : new Color(0.5f, 1f, 0.5f);
                }
            }
            else
            {
                var dotR = dot.GetComponent<Renderer>();
                if (dotR != null)
                    dotR.material.color = (ctrl == OVRInput.Controller.RTouch)
                        ? Color.white : new Color(0.5f, 1f, 0.5f);
            }
        }
        } // end !onSlider

        // ── Draw laser and dot ───────────────────────────────────
        dot.SetActive(showDot);
        if (showDot) dot.transform.position = endPt + (pos - endPt).normalized * 0.005f;
        laser.enabled = true;
        laser.SetPosition(0, pos);
        laser.SetPosition(1, endPt);
    }

    /// Silent controller click processing — no laser or dot, just button detection.
    /// Used in Hands mode so the toggle button is always reachable via controller trigger.
    // ═════════════════════════════════════════════════════════════
    //  SLIDER HELPERS
    // ═════════════════════════════════════════════════════════════

    /// Raycast all slider colliders, return closest hit index or -1
    int RaycastSliders(Ray ray, float maxDist)
    {
        int bestIdx = -1;
        float bestDist = float.MaxValue;
        for (int i = 0; i < _sliderCols.Count; i++)
        {
            var sc = _sliderCols[i];
            if (sc.col == null || !sc.go.activeSelf) continue;
            RaycastHit sh;
            if (sc.col.Raycast(ray, out sh, maxDist) && sh.distance < bestDist)
            {
                bestDist = sh.distance;
                bestIdx = i;
            }
        }
        return bestIdx;
    }

    /// Update a slider's value (0..1) and invoke its callback with mapped value
    void UpdateSliderValue(int idx, float t)
    {
        var sc = _sliderCols[idx];
        sc.value = t;
        _sliderCols[idx] = sc; // struct copy back

        // Update fill bar width
        if (sc.fill != null)
        {
            var fillRT = sc.fill.GetComponent<RectTransform>();
            fillRT.anchorMax = new Vector2(t, 1);
        }

        // Update knob position
        if (sc.knob != null)
        {
            // Knob anchored at (t, 0.5) within parent
            sc.knob.anchorMin = sc.knob.anchorMax = new Vector2(t, 0.5f);
            sc.knob.anchoredPosition = Vector2.zero;
        }

        // Invoke callback with mapped value
        float mapped = Mathf.Lerp(sc.minVal, sc.maxVal, t);
        sc.onChanged?.Invoke(mapped);
    }

    /// Register a slider: creates 3D collider and returns its index
    void RegSliderCol(Transform panel, Vector2 canvasPos, Vector2 canvasSize,
                      Image fill, RectTransform knob,
                      float minVal, float maxVal, float initVal,
                      System.Action<float> onChanged, string name)
    {
        var go = new GameObject($"SC_{name.Replace(" ", "")}");
        var col = go.AddComponent<BoxCollider>();
        col.size = Vector3.one;
        go.transform.localScale = new Vector3(
            canvasSize.x * 0.001f,
            canvasSize.y * 0.001f,
            0.025f
        );

        float initT = Mathf.InverseLerp(minVal, maxVal, initVal);

        var baseSize = new Vector3(canvasSize.x * 0.001f, canvasSize.y * 0.001f, 0.025f);
        _sliderCols.Add(new SliderCol
        {
            go         = go,
            col        = col,
            panelTF    = panel,
            localOffset = new Vector3(canvasPos.x * 0.001f, canvasPos.y * 0.001f, 0),
            baseSize    = baseSize,
            widthMeters = canvasSize.x * 0.001f,
            fill       = fill,
            knob       = knob,
            value      = initT,
            minVal     = minVal,
            maxVal     = maxVal,
            onChanged  = onChanged,
            name       = name
        });

        // Set initial fill and knob
        if (fill != null)
        {
            var fillRT = fill.GetComponent<RectTransform>();
            fillRT.anchorMax = new Vector2(initT, 1);
        }
        if (knob != null)
        {
            knob.anchorMin = knob.anchorMax = new Vector2(initT, 0.5f);
            knob.anchoredPosition = Vector2.zero;
        }

        go.SetActive(false);
    }

    // ═════════════════════════════════════════════════════════════
    //  HAND TRACKING — point and pinch
    // ═════════════════════════════════════════════════════════════

    /// <summary>
    /// Processes one hand: draws laser from PointerPose, raycasts buttons,
    /// uses index pinch as trigger click. Mirrors ProcessController logic.
    /// Returns true if this hand is actively tracked (so caller can hide
    /// the corresponding controller laser).
    /// </summary>
    bool ProcessHand(OVRHand hand, bool isLeft, LineRenderer laser, GameObject dot)
    {
        if (hand == null || !hand.IsTracked || !hand.IsPointerPoseValid)
        {
            laser.enabled = false;
            dot.SetActive(false);
            return false;
        }

        Transform pp = hand.PointerPose;
        Vector3 pos = pp.position;
        Vector3 fwd = pp.forward;
        float maxDist = 3f;
        Vector3 endPt = pos + fwd * maxDist;
        bool showDot = false;

        // Pinch edge detection (same pattern as Axis1D trigger)
        float pinch = hand.GetFingerPinchStrength(OVRHand.HandFinger.Index);
        bool pinchNow = pinch > PINCH_THRESH;
        bool pinchDown;
        if (isLeft)
        { pinchDown = pinchNow && !_prevPinchL; _prevPinchL = pinchNow; }
        else
        { pinchDown = pinchNow && !_prevPinchR; _prevPinchR = pinchNow; }

        // Release slider when pinch released
        if (!pinchNow && _activeSlider >= 0) _activeSlider = -1;

        Ray ray = new Ray(pos, fwd);

        // ── Slider drag (continuous while pinching) ─────────
        bool onSlider = false;
        if (pinchNow)
        {
            int sliderIdx = RaycastSliders(ray, maxDist);
            if (sliderIdx >= 0 && (_activeSlider < 0 || _activeSlider == sliderIdx))
            {
                bool wasIdle = _activeSlider < 0;
                _activeSlider = sliderIdx;
                if (wasIdle) { PlayHapticPulse(); }
            }
            if (_activeSlider >= 0)
            {
                var sc = _sliderCols[_activeSlider];
                RaycastHit sh;
                if (sc.col.Raycast(ray, out sh, maxDist))
                {
                    endPt = sh.point;
                    showDot = true;
                    onSlider = true;
                    var dotR = dot.GetComponent<Renderer>();
                    if (dotR != null) dotR.material.color = new Color(0f, 0.85f, 0.95f);
                    Vector3 localHit = sc.go.transform.InverseTransformPoint(sh.point);
                    float t = Mathf.Clamp01(localHit.x / 1f + 0.5f);
                    UpdateSliderValue(_activeSlider, t);
                }
            }
        }

        if (!onSlider)
        {
        // Button raycast (same system as controllers)
        int btnIdx = RaycastButtons(ray, maxDist);

        if (btnIdx >= 0)
        {
            RaycastHit bh;
            _btnCols[btnIdx].col.Raycast(ray, out bh, maxDist);
            endPt = bh.point;
            showDot = true;

            var dotR = dot.GetComponent<Renderer>();
            if (dotR != null) dotR.material.color = new Color(0.2f, 1f, 0.3f);

            if (pinchDown)
            {
                InvokeBtn(btnIdx);
                Debug.Log($"[RegionalAR] PINCH-CLICK '{_btnCols[btnIdx].name}' via {(isLeft ? "LHand" : "RHand")}");
            }
        }
        else
        {
            RaycastHit hit;
            if (Physics.Raycast(pos, fwd, out hit, maxDist))
            {
                endPt = hit.point;
                showDot = true;

                bool isProxy = (_ctrlOpen && _ctrlProxyCol != null && hit.collider == _ctrlProxyCol)
                            || (_wifiOpen && _wifiProxyCol != null && hit.collider == _wifiProxyCol)
                            || (_libOpen  && _libProxyCol  != null && hit.collider == _libProxyCol);

                var dotR = dot.GetComponent<Renderer>();
                if (dotR != null)
                    dotR.material.color = isProxy
                        ? new Color(1f, 0.6f, 0.1f)
                        : (isLeft ? new Color(1f, 0.5f, 0.8f) : new Color(1f, 0.7f, 0.3f));
            }
        }
        } // end !onSlider

        dot.SetActive(showDot);
        if (showDot) dot.transform.position = endPt + (pos - endPt).normalized * 0.005f;
        laser.enabled = true;
        laser.SetPosition(0, pos);
        laser.SetPosition(1, endPt);
        return true;
    }

    /// <summary>
    /// Returns hand world position (wrist area) for grab proximity checks.
    /// </summary>
    Vector3 HandPos(OVRHand hand)
    {
        if (hand != null && hand.IsTracked && hand.PointerPose != null)
            return hand.PointerPose.position;
        return Vector3.zero;
    }

    /// <summary>
    /// Returns true if the hand is doing a grab gesture (all fingers pinching toward palm).
    /// We approximate this by checking index + middle pinch strength.
    /// </summary>
    /// Fist gesture — at least 3 of 4 fingers curled.
    bool IsHandGrabbing(OVRHand hand)
    {
        if (hand == null || !hand.IsTracked) return false;
        float idx   = hand.GetFingerPinchStrength(OVRHand.HandFinger.Index);
        float mid   = hand.GetFingerPinchStrength(OVRHand.HandFinger.Middle);
        float ring  = hand.GetFingerPinchStrength(OVRHand.HandFinger.Ring);
        float pinky = hand.GetFingerPinchStrength(OVRHand.HandFinger.Pinky);
        int count = 0;
        if (idx   > FIST_THRESH) count++;
        if (mid   > FIST_THRESH) count++;
        if (ring  > FIST_THRESH) count++;
        if (pinky > FIST_THRESH) count++;
        return count >= 3;
    }

    void InvokeBtn(int idx)
    {
        var bc = _btnCols[idx];
        Debug.Log($"[RegionalAR] InvokeBtn '{bc.name}' click={bc.onClick != null} tog={bc.toggle != null}");

        // Audio + haptic feedback on every button press
        PlayUISound();
        PlayHapticPulse();

        if (bc.onClick != null)
        {
            bc.onClick();
            // Flash feedback for action buttons
            if (bc.bg != null)
                StartCoroutine(FlashButton(bc.bg, bc.bgOrig));
        }
        else if (bc.toggle != null)
            bc.toggle.isOn = !bc.toggle.isOn;
    }

    /// Play UI feedback sound — uses PlayClipAtPoint for maximum Quest compatibility
    void PlayUISound()
    {
        if (_dropClip == null) return;
        // PlayClipAtPoint is more reliable on Quest than PlayOneShot on a dynamic AudioSource
        // It creates a temporary GameObject with an AudioSource at the listener position
        Transform listener = Camera.main != null ? Camera.main.transform : transform;
        AudioSource.PlayClipAtPoint(_dropClip, listener.position, 1.0f);
    }

    /// Brief haptic pulse on both controllers (~0.15 s)
    void PlayHapticPulse()
    {
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

    IEnumerator FlashButton(Image bg, Color origColor)
    {
        Color flash = new Color(1.00f, 0.42f, 0.21f, 1f); // orange flash (Medivis active)
        bg.color = flash;
        float duration = 0.30f;
        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            bg.color = Color.Lerp(flash, origColor, t / duration);
            yield return null;
        }
        bg.color = origColor;
    }

    // ═════════════════════════════════════════════════════════════
    //  UDP AUTO-DISCOVERY
    // ═════════════════════════════════════════════════════════════
    void StartUDPDiscovery()
    {
        _discoveryRunning = true;
        _udpThread = new Thread(UDPListenLoop) { IsBackground = true };
        _udpThread.Start();
    }

    void StopUDPDiscovery()
    {
        _discoveryRunning = false;
        try { _udpListener?.Close(); } catch { }
    }

    void UDPListenLoop()
    {
        try
        {
            _udpListener = new UdpClient(UDP_PORT);
            _udpListener.Client.ReceiveTimeout = 2000;
            IPEndPoint ep = new IPEndPoint(IPAddress.Any, UDP_PORT);
            while (_discoveryRunning)
            {
                try
                {
                    byte[] data = _udpListener.Receive(ref ep);
                    string msg = Encoding.UTF8.GetString(data);
                    if (msg.StartsWith("BLOCKAR_SERVER:"))
                    {
                        string[] parts = msg.Split(':');
                        if (parts.Length >= 2)
                        {
                            _discoveredIP = parts[1];
                            Debug.Log($"[RegionalAR] Discovered server: {_discoveredIP}");
                        }
                    }
                }
                catch (SocketException) { }
            }
        }
        catch (Exception e) { Debug.LogWarning($"[RegionalAR] UDP error: {e.Message}"); }
    }

    // ═════════════════════════════════════════════════════════════
    //  ACTIONS
    // ═════════════════════════════════════════════════════════════
    void BringVolumeHere()
    {
        var vr = volumeRenderer ?? FindObjectOfType<VolumeRenderer>();
        if (vr != null)
        {
            vr.PositionInFrontOfUser();
            vr.ResetCrop();
            vr.ClearAllCutPlanes();
            vr.transform.localScale = Vector3.one;
        }
        // Reset grab state so interaction works immediately
        var hi = FindObjectOfType<HandInteraction>();
        if (hi != null) hi.ResetGrabState();
        SetStatus("Volume repositioned.");
    }

    void OnLayerToggle(int idx, bool on)
    {
        var vr = volumeRenderer ?? FindObjectOfType<VolumeRenderer>();
        if (vr == null) return;

        // Meshes are the default rendering for bone (idx 0) and
        // vasculature (idx 1). When a mesh is loaded, the tissue-row
        // toggle controls the mesh visibility and the volume layer stays
        // off. When no mesh exists (e.g. pre-Phase-1/2 volumes or raw
        // mode), we fall back to toggling the volume layer directly.
        if (idx == 0)
        {
            var bml = vr.GetComponent<BoneMeshLoader>();
            if (bml != null && bml.HasMesh)
            {
                bml.SetMeshEnabled(on);
                vr.SetLayerEnabled(0, false); // keep volume bone off
                return;
            }
        }
        else if (idx == 1)
        {
            var vml = vr.GetComponent<VesselMeshLoader>();
            if (vml != null && vml.HasMesh)
            {
                vml.SetMeshEnabled(on);
                vr.SetLayerEnabled(1, false); // keep volume vasc off
                return;
            }
        }
        else if (idx == 2)
        {
            var nml = vr.GetComponent<NerveMeshLoader>();
            if (nml != null && nml.HasMesh)
            {
                nml.SetMeshEnabled(on);
                vr.SetLayerEnabled(2, false); // keep volume nerves off
                return;
            }
        }
        else if (idx == 3)
        {
            var mml = vr.GetComponent<MuscleMeshLoader>();
            if (mml != null && mml.HasMesh)
            {
                mml.SetMeshEnabled(on);
                return;   // no volume layer for muscle
            }
            return;   // no fallback — muscle is mesh-only
        }

        // Fallback (no AI mesh available): toggle the volume layer directly
        vr.SetLayerEnabled(idx, on);
    }

    void ResetCrop()
    {
        var vr = volumeRenderer ?? FindObjectOfType<VolumeRenderer>();
        if (vr != null) vr.ResetCrop();
        SetStatus("Crop & cut planes reset.");
    }

    void CycleCutPlane()
    {
        var hi = FindObjectOfType<HandInteraction>();
        if (hi != null)
        {
            hi.CycleCutPlaneState();
            SetStatus("Cut plane cycled.");
        }
        else
            SetStatus("HandInteraction not found.");
    }

    void ToggleInputMode()
    {
        var hi = FindObjectOfType<HandInteraction>();
        if (hi != null)
        {
            hi.ToggleInputMode();
            // Update button label to show current mode
            UpdateInputModeButton(hi.InputModeLabel);
            SetStatus($"Input: {hi.InputModeLabel}");
        }
    }

    void UpdateInputModeButton(string label)
    {
        // Find the input mode button by searching control panel children for UI Text
        if (_ctrlBG == null) return;
        string upperLabel = label.ToUpper();
        var texts = _ctrlBG.GetComponentsInChildren<Text>(true);
        foreach (var txt in texts)
        {
            if (txt.text == "CONTROLS" || txt.text == "CONTROLLERS" || txt.text == "HANDS")
            {
                txt.text = upperLabel;
                return;
            }
        }
    }

    // ═════════════════════════════════════════════════════════════
    //  DOWNLOAD
    // ═════════════════════════════════════════════════════════════
    bool _downloading;
    bool _downloadedThisSession;  // true after one successful download — blocks auto-re-download

    void StartDownload()
    {
        if (_downloading) return;  // prevent concurrent downloads
        _downloading = true;       // set IMMEDIATELY to block re-entry from UDP
        string ip = _ipInput.text.Trim();
        if (string.IsNullOrEmpty(ip)) { SetStatus("Waiting for server...", true); _downloading = false; return; }
        PlayerPrefs.SetString(PREFS_IP, ip);
        PlayerPrefs.Save();
        StartCoroutine(DoDownload($"http://{ip}:{HTTP_PORT}/volume.vol"));
    }

    IEnumerator DoDownload(string url)
    {
        _downloading = true;
        if (_downloadBtn != null) _downloadBtn.interactable = false;
        SetFill(0f); SetStatus("Connecting...");
        Set3DDebug($"DL START\n{url}");
        Debug.Log($"[RegionalAR] DoDownload starting: {url}");

        // ── Download into memory buffer (64MB fits in Quest 3's 8GB RAM) ──
        byte[] data = null;
        UnityWebRequest req = null;
        UnityWebRequestAsyncOperation op = null;
        try
        {
            req = UnityWebRequest.Get(url);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.timeout = 180;  // 3 min timeout for large volumes over WiFi
            op = req.SendWebRequest();
        }
        catch (Exception ex)
        {
            SetStatus($"Connection failed: {ex.Message}", true);
            Set3DDebug($"DL EXCEPTION\n{ex.Message}");
            Debug.LogError($"[RegionalAR] SendWebRequest exception: {ex}");
            if (_downloadBtn != null) _downloadBtn.interactable = true;
            _downloading = false; SetFill(0f);
            if (req != null) req.Dispose();
            yield break;
        }

        float stallTimer = 0f;
        bool seenProgress = false;
        while (!op.isDone)
        {
            float pct = req.downloadProgress;
            ulong bytes = req.downloadedBytes;
            if (pct > 0f || bytes > 0) seenProgress = true;

            SetFill(Mathf.Max(pct, 0f));
            if (seenProgress)
                SetStatus($"Downloading... {Mathf.RoundToInt(pct * 100)}%  ({bytes / 1024}KB)");
            else
            {
                stallTimer += Time.deltaTime;
                SetStatus($"Connecting... ({stallTimer:F0}s)");
                // Show diagnostic after 15s stall
                if (stallTimer > 15f && stallTimer < 15.5f)
                    Set3DDebug($"STALL {stallTimer:F0}s\nNo response from\n{url}");
            }
            yield return null;
        }

        string error = req.error;
        var result = req.result;
        if (result != UnityWebRequest.Result.Success)
        {
            SetStatus($"Download error: {error}\n({result})", true);
            Set3DDebug($"DL FAIL\n{error}\n{result}");
            Debug.LogError($"[RegionalAR] Download failed: {error} result={result}");
            if (_downloadBtn != null) _downloadBtn.interactable = true;
            _downloading = false; SetFill(0f);
            req.Dispose();
            yield break;
        }
        data = req.downloadHandler.data;
        // Read scan name from server header (desktop app sends X-Scan-Name)
        string serverScanName = req.GetResponseHeader("X-Scan-Name");
        req.Dispose();
        SetFill(1f);

        if (data == null || data.Length < 32)
        {
            SetStatus($"Error: empty response ({data?.Length ?? 0} bytes)", true);
            if (_downloadBtn != null) _downloadBtn.interactable = true;
            _downloading = false; SetFill(0f); yield break;
        }

        SetStatus($"Downloaded {data.Length / 1024}KB — verifying...");

        // ── Validate OVOL magic ──
        bool validMagic = data[0] == (byte)'O' && data[1] == (byte)'V'
                       && data[2] == (byte)'O' && data[3] == (byte)'L';
        if (!validMagic)
        {
            string hexPreview = BitConverter.ToString(data, 0, Math.Min(16, data.Length));
            SetStatus($"Error: not OVOL format [{hexPreview}]", true);
            Set3DDebug($"BAD FORMAT\n{data.Length}B\n{hexPreview}");
            if (_downloadBtn != null) _downloadBtn.interactable = true;
            _downloading = false; SetFill(0f); yield break;
        }

        // ── Save to library ──
        // Use the scan name from the desktop app if provided; fall back to timestamp
        string scanName = !string.IsNullOrEmpty(serverScanName)
            ? serverScanName
            : $"scan_{DateTime.Now:yyyyMMdd_HHmmss}";
        string libDir = ScanLibraryDir();
        string finalPath = Path.Combine(libDir, scanName + ".vol");
        try
        {
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.WriteAllBytes(finalPath, data);
        }
        catch (Exception ex)
        {
            SetStatus($"Save error: {ex.Message}", true);
            if (_downloadBtn != null) _downloadBtn.interactable = true;
            _downloading = false; SetFill(0f); yield break;
        }

        SetStatus($"Saved {data.Length / 1024}KB — loading volume...");
        Set3DDebug($"Saved {data.Length/1024}KB\nLoading...");

        SaveScanToLibrary(scanName, finalPath, data.Length);

        // Allow one frame for GC before loading volume (just freed 64MB buffer)
        data = null;
        yield return null;

        // ── Fetch companion meshes (.omsh bone + .vmsh vessels) ──
        // Best-effort; server returns 404 if the volume was processed
        // before Phase 1/2 mesh export existed. Both are downloaded
        // BEFORE ReloadVolumeSync so they're on disk when the volume
        // load hook triggers the mesh loaders.
        string boneUrl   = url.Replace("/volume.vol", "/volume.omsh");
        string vesselUrl = url.Replace("/volume.vol", "/volume.vmsh");
        string nerveUrl  = url.Replace("/volume.vol", "/volume.nmsh");
        string muscleUrl = url.Replace("/volume.vol", "/volume.mmsh");
        yield return StartCoroutine(TryDownloadCompanionMesh(boneUrl,   finalPath, ".omsh"));
        yield return StartCoroutine(TryDownloadCompanionMesh(vesselUrl, finalPath, ".vmsh"));
        yield return StartCoroutine(TryDownloadCompanionMesh(nerveUrl,  finalPath, ".nmsh"));
        yield return StartCoroutine(TryDownloadCompanionMesh(muscleUrl, finalPath, ".mmsh"));

        // ── Load into VolumeRenderer ──
        var vr = volumeRenderer ?? FindObjectOfType<VolumeRenderer>();
        if (vr == null)
        {
            SetStatus("Error: VolumeRenderer not found!", true);
            if (_downloadBtn != null) _downloadBtn.interactable = true;
            _downloading = false; yield break;
        }

        bool loadOK = false;
        string loadErr = null;
        try
        {
            vr.ReloadVolumeSync(finalPath);
            loadOK = true;
        }
        catch (Exception ex)
        {
            loadErr = ex.Message;
        }
        if (!loadOK)
        {
            SetStatus($"Load error: {loadErr}", true);
            Set3DDebug($"LOAD ERR\n{loadErr}");
        }
        else
        {
            _activeScanName = scanName;
            _downloadedThisSession = true;
            SetStatus($"Volume loaded! ({new FileInfo(finalPath).Length / 1024}KB)");
            Set3DDebug("LOADED OK");
            // Reposition volume in front of user after loading
            vr.PositionInFrontOfUser();
            yield return new WaitForSeconds(2f);
            if (_wifiBG != null) _wifiBG.SetActive(false);
            if (_wifiProxy != null) _wifiProxy.SetActive(false);
            SetBtnColsActive(_wifiTF, false);
            _wifiOpen = false;
        }

        if (_downloadBtn != null) _downloadBtn.interactable = true;
        _downloading = false;
    }

    /// Best-effort companion mesh fetch — every branch now reports an
    /// on-screen status so we can tell from inside VR what happened.
    /// ext = ".omsh" (bone) or ".vmsh" (vessels). Used for save filename
    /// and the status label so both can run through the same code path.
    IEnumerator TryDownloadCompanionMesh(string meshUrl, string scanLibPath, string ext = ".omsh")
    {
        string kind = ext == ".vmsh" ? "vessel mesh" :
                      ext == ".nmsh" ? "nerve mesh"  :
                      ext == ".mmsh" ? "muscle mesh" : "bone mesh";
        SetStatus($"Fetching {kind}: {meshUrl}");
        Debug.Log($"[RegionalAR][Mesh] GET {meshUrl}");

        UnityWebRequest req = null;
        try { req = UnityWebRequest.Get(meshUrl); }
        catch (Exception ex)
        {
            SetStatus($"Mesh: request init failed ({ex.Message})", true);
            Debug.LogWarning($"[RegionalAR][Mesh] Request init failed: {ex.Message}");
            yield break;
        }
        req.timeout = 30;
        yield return req.SendWebRequest();

        long code = req.responseCode;
        UnityWebRequest.Result result = req.result;

        if (result != UnityWebRequest.Result.Success)
        {
            string errMsg = req.error ?? "unknown";
            // 404 is expected for vessel mesh on pre-Phase-2 volumes;
            // don't red-flag it, just log and move on silently.
            bool is404 = code == 404;
            if (!is404) SetStatus($"{kind}: HTTP {code} ({result}) — {errMsg}", true);
            else        Debug.Log($"[RegionalAR][Mesh] No {kind} available (404)");
            Debug.LogWarning($"[RegionalAR][Mesh] Download failed: HTTP {code} result={result} err={errMsg}");
            req.Dispose();
            yield break;
        }

        byte[] meshBytes = req.downloadHandler?.data;
        req.Dispose();
        if (meshBytes == null || meshBytes.Length < 32)
        {
            SetStatus($"Mesh: empty response (got {(meshBytes?.Length ?? 0)} bytes)", true);
            Debug.LogWarning("[RegionalAR][Mesh] Mesh response empty/too small.");
            yield break;
        }

        // Validate magic bytes so a stray HTML error page doesn't pass as a mesh
        if (meshBytes.Length >= 4 &&
            !(meshBytes[0] == 'O' && meshBytes[1] == 'M' &&
              meshBytes[2] == 'S' && meshBytes[3] == 'H'))
        {
            SetStatus($"Mesh: wrong magic — server returned non-OMSH data", true);
            Debug.LogWarning($"[RegionalAR][Mesh] Bad magic: {(char)meshBytes[0]}{(char)meshBytes[1]}{(char)meshBytes[2]}{(char)meshBytes[3]}");
            yield break;
        }

        string scanLibMesh = Path.ChangeExtension(scanLibPath, ext);
        try
        {
            File.WriteAllBytes(scanLibMesh, meshBytes);
            Debug.Log($"[RegionalAR][Mesh] Saved {meshBytes.Length / 1024} KB → {scanLibMesh}");
            SetStatus($"{kind}: {meshBytes.Length / 1024} KB saved");
        }
        catch (Exception ex)
        {
            SetStatus($"{kind} save failed: {ex.Message}", true);
            Debug.LogWarning($"[RegionalAR][Mesh] Save failed: {ex.Message}");
        }
        // Keep the mesh status visible for ~2 s before the "Volume loaded"
        // message overwrites it — otherwise the user never sees it.
        yield return new WaitForSeconds(2f);
    }

    void SetStatus(string msg, bool bad = false)
    {
        if (_statusText == null) return;
        _statusText.text  = msg;
        _statusText.color = bad ? new Color(1f, .42f, .42f) : new Color(.65f, .92f, .65f);
    }

    void SetFill(float t)
    {
        if (_fillRT == null) return;
        _fillRT.anchorMax = new Vector2(Mathf.Clamp01(t), 1f);
    }

    // ═════════════════════════════════════════════════════════════
    //  BUNDLED SAMPLE — first-launch copy from StreamingAssets
    // ═════════════════════════════════════════════════════════════
    const string SAMPLE_NAME = "Head-Neck_CTA";
    static readonly string[] SAMPLE_EXTS = { ".vol", ".omsh", ".vmsh", ".nmsh", ".mmsh" };

    IEnumerator InstallBundledSample()
    {
        // Check if sample already installed (marker file)
        string markerPath = Path.Combine(Application.persistentDataPath, ".sample_installed");
        if (File.Exists(markerPath))
        {
            Debug.Log("[RegionalAR] Bundled sample already installed — skipping.");
            yield break;
        }

        string libDir = ScanLibraryDir();
        string volDest = Path.Combine(libDir, SAMPLE_NAME + ".vol");

        // If the .vol already exists in the library, mark done and skip
        if (File.Exists(volDest))
        {
            try { File.WriteAllText(markerPath, "1"); } catch { }
            Debug.Log("[RegionalAR] Sample .vol already in library — marking installed.");
            yield break;
        }

        Debug.Log("[RegionalAR] Installing bundled sample from StreamingAssets...");

        foreach (string ext in SAMPLE_EXTS)
        {
            string srcPath = Path.Combine(Application.streamingAssetsPath, SAMPLE_NAME + ext);
            string dstPath = Path.Combine(libDir, SAMPLE_NAME + ext);

            // On Android, StreamingAssets are inside the APK jar —
            // must use UnityWebRequest. On editor/standalone, direct copy works.
            #if UNITY_ANDROID && !UNITY_EDITOR
            using (var req = UnityWebRequest.Get(srcPath))
            {
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                {
                    try { File.WriteAllBytes(dstPath, req.downloadHandler.data); }
                    catch (Exception e) { Debug.LogWarning($"[RegionalAR] Sample write error ({ext}): {e.Message}"); }
                }
                else
                {
                    Debug.LogWarning($"[RegionalAR] Sample fetch error ({ext}): {req.error}");
                }
            }
            #else
            // Editor / standalone: direct file copy
            if (File.Exists(srcPath))
            {
                try { File.Copy(srcPath, dstPath, overwrite: true); }
                catch (Exception e) { Debug.LogWarning($"[RegionalAR] Sample copy error ({ext}): {e.Message}"); }
            }
            yield return null;
            #endif
        }

        // Register in library index
        if (File.Exists(volDest))
        {
            long sz = new FileInfo(volDest).Length;
            SaveScanToLibrary(SAMPLE_NAME, volDest, sz);
            Debug.Log($"[RegionalAR] Bundled sample installed: {SAMPLE_NAME} ({sz / 1024}KB)");
        }
        else
        {
            Debug.LogWarning("[RegionalAR] Sample .vol not found after copy — install may have failed.");
        }

        // Write marker so we don't repeat on next launch
        try { File.WriteAllText(markerPath, "1"); } catch { }
    }

    // ═════════════════════════════════════════════════════════════
    //  SCAN LIBRARY
    // ═════════════════════════════════════════════════════════════
    static string ScanLibraryDir()
    {
        string dir = Path.Combine(Application.persistentDataPath, "ScanLibrary");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        return dir;
    }

    static string ScanIndexPath()
    {
        return Path.Combine(ScanLibraryDir(), "index.txt");
    }

    void SaveScanToLibrary(string name, string path, long sizeBytes)
    {
        // Simple line-based index: name|path|sizeBytes|date
        string line = $"{name}|{path}|{sizeBytes}|{DateTime.Now:yyyy-MM-dd HH:mm}";
        try
        {
            File.AppendAllText(ScanIndexPath(), line + "\n");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[RegionalAR] SaveScanToLibrary failed: {ex.Message}");
        }
    }

    void LoadScanIndex()
    {
        _scanEntries.Clear();
        string idxPath = ScanIndexPath();
        if (!File.Exists(idxPath)) return;
        try
        {
            string[] lines = File.ReadAllLines(idxPath);
            foreach (string line in lines)
            {
                if (string.IsNullOrEmpty(line)) continue;
                string[] parts = line.Split('|');
                if (parts.Length < 4) continue;
                // Only include if file still exists
                if (!File.Exists(parts[1])) continue;
                _scanEntries.Add(new ScanEntry
                {
                    name      = parts[0],
                    path      = parts[1],
                    sizeBytes = long.TryParse(parts[2], out long sz) ? sz : 0,
                    date      = parts[3]
                });
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[RegionalAR] LoadScanIndex failed: {ex.Message}");
        }
    }

    void LoadScanFromLibrary(int idx)
    {
        if (idx < 0 || idx >= _scanEntries.Count) return;
        var entry = _scanEntries[idx];
        if (!File.Exists(entry.path))
        {
            SetStatus($"Scan file missing: {entry.name}", true);
            return;
        }

        var vr = volumeRenderer ?? FindObjectOfType<VolumeRenderer>();
        if (vr == null) { SetStatus("VolumeRenderer not found!", true); return; }

        try
        {
            vr.ReloadVolumeSync(entry.path);
            _activeScanName = entry.name;

            // Reposition volume in front of user after loading
            vr.PositionInFrontOfUser();

            SetStatus($"Loaded: {entry.name} ({entry.sizeBytes / 1024}KB)");
            // Close library panel
            ToggleLibPanel();
        }
        catch (Exception ex)
        {
            SetStatus($"Load error: {ex.Message}", true);
        }
    }

    void DeleteScanFromLibrary(int idx)
    {
        if (idx < 0 || idx >= _scanEntries.Count) return;
        var entry = _scanEntries[idx];
        try { if (File.Exists(entry.path)) File.Delete(entry.path); } catch { }

        // Rewrite index without this entry
        _scanEntries.RemoveAt(idx);
        try
        {
            var lines = new List<string>();
            foreach (var e in _scanEntries)
                lines.Add($"{e.name}|{e.path}|{e.sizeBytes}|{e.date}");
            File.WriteAllLines(ScanIndexPath(), lines);
        }
        catch { }

        // Refresh library UI
        RefreshLibContent();
    }

#if UNITY_EDITOR
    /// Kick off a coroutine that opens the system keyboard, lets the
    /// user type a new scan name, then renames the entry + all companion
    /// mesh files on disk and refreshes the library panel.
    /// Desktop app only — removed from Quest to avoid keyboard issues.
    void RenameScanInLibrary(int idx)
    {
        if (idx < 0 || idx >= _scanEntries.Count) return;
        StartCoroutine(RenameScanCoroutine(idx));
    }

    IEnumerator RenameScanCoroutine(int idx)
    {
        var entry = _scanEntries[idx];
        string newName = null;

        // Try the Quest system keyboard. If it's unsupported on this
        // device OR never pops up, we fall back to appending " (N)".
        bool kbOpened = false;
        if (TouchScreenKeyboard.isSupported)
        {
            SetStatus($"Rename: type new name for '{entry.name}'");
            TouchScreenKeyboard kb = null;
            try
            {
                kb = TouchScreenKeyboard.Open(
                    entry.name,
                    TouchScreenKeyboardType.Default,
                    autocorrection:   false,
                    multiline:        false,
                    secure:           false,
                    alert:            false,
                    textPlaceholder:  "new scan name");
            }
            catch (Exception kbEx)
            {
                Debug.LogWarning($"[RegionalAR] Keyboard open threw: {kbEx.Message}");
            }

            if (kb != null)
            {
                kbOpened = true;
                // Wait up to 60 seconds for user to Done or Cancel.
                float waited = 0f;
                while (kb.active && kb.status == TouchScreenKeyboard.Status.Visible && waited < 60f)
                {
                    waited += Time.deltaTime;
                    yield return null;
                }
                if (kb.status == TouchScreenKeyboard.Status.Done)
                {
                    newName = SanitizeScanName(kb.text ?? "");
                }
                else if (kb.status == TouchScreenKeyboard.Status.Canceled)
                {
                    SetStatus("Rename cancelled");
                    yield break;
                }
                else
                {
                    Debug.LogWarning($"[RegionalAR] Keyboard timed out or closed with status={kb.status}");
                }
            }
        }

        // Keyboard unsupported / never showed / returned nothing →
        // auto-suffix fallback. Appends _2, _3, … until unique so the
        // user still gets something useful.
        if (string.IsNullOrEmpty(newName))
        {
            if (!kbOpened)
                SetStatus("Keyboard unavailable — auto-renaming with suffix");
            else
                SetStatus("No name entered — auto-renaming with suffix");
            int n = 2;
            while (true)
            {
                string candidate = SanitizeScanName(entry.name) + "_" + n;
                bool taken = false;
                for (int j = 0; j < _scanEntries.Count; j++)
                    if (j != idx && _scanEntries[j].name == candidate) { taken = true; break; }
                if (!taken) { newName = candidate; break; }
                n++;
                if (n > 999) { newName = null; break; }
            }
        }

        if (string.IsNullOrEmpty(newName) || newName == entry.name)
        {
            SetStatus("Rename: nothing changed");
            yield break;
        }

        // Guard against duplicate names in the library
        for (int j = 0; j < _scanEntries.Count; j++)
        {
            if (j != idx && _scanEntries[j].name == newName)
            {
                SetStatus($"Rename failed: '{newName}' already exists", true);
                yield break;
            }
        }

        // Rename the .vol + companion meshes on disk, then update the entry
        try
        {
            string oldBase = Path.ChangeExtension(entry.path, null);
            string newPath = Path.Combine(Path.GetDirectoryName(entry.path), newName + ".vol");
            string newBase = Path.ChangeExtension(newPath, null);

            if (File.Exists(entry.path)) File.Move(entry.path, newPath);
            foreach (string ext in new[] { ".omsh", ".vmsh", ".nmsh", ".mmsh" })
            {
                string oldCompanion = oldBase + ext;
                string newCompanion = newBase + ext;
                if (File.Exists(oldCompanion)) File.Move(oldCompanion, newCompanion);
            }

            // If this was the active scan, update the tracked name too
            if (_activeScanName == entry.name) _activeScanName = newName;

            entry.name = newName;
            entry.path = newPath;
            _scanEntries[idx] = entry;

            // Rewrite the index file
            var lines = new List<string>();
            foreach (var e in _scanEntries)
                lines.Add($"{e.name}|{e.path}|{e.sizeBytes}|{e.date}");
            File.WriteAllLines(ScanIndexPath(), lines);

            SetStatus($"Renamed to '{newName}'");
            RefreshLibContent();
        }
        catch (Exception ex)
        {
            SetStatus($"Rename failed: {ex.Message}", true);
            Debug.LogWarning($"[RegionalAR] Rename failed: {ex.Message}");
        }
    }

    /// Keep filenames safe: alnum + _ + - only. Matches the desktop app's
    /// _sanitize_scan_name so both sides agree.
    static string SanitizeScanName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new System.Text.StringBuilder(raw.Length);
        bool lastUnder = false;
        foreach (char ch in raw)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
            {
                sb.Append(ch);
                lastUnder = (ch == '_');
            }
            else if (!lastUnder)
            {
                sb.Append('_');
                lastUnder = true;
            }
        }
        return sb.ToString().Trim('_', '-');
    }
#endif // UNITY_EDITOR — rename methods

    void DeleteAllScans()
    {
        foreach (var e in _scanEntries)
        {
            try { if (File.Exists(e.path)) File.Delete(e.path); } catch { }
        }
        _scanEntries.Clear();
        try { File.WriteAllText(ScanIndexPath(), ""); } catch { }
        _activeScanName = null;
        RefreshLibContent();
        SetStatus("All scans deleted.");
    }

    void ToggleLibPanel()
    {
        try
        {
            _libOpen = !_libOpen;
            if (_libBG != null) _libBG.SetActive(_libOpen);
            if (_libProxy != null) _libProxy.SetActive(_libOpen);
            SetBtnColsActive(_libTF, _libOpen);
            if (_libOpen)
            {
                CacheCamera();
                SnapPanelInFront(_libTF, _libCanvas, _libProxy, _libProxyCol);
                Camera cam = Cam();
                if (cam != null)
                {
                    _libTF.position += cam.transform.right * -0.35f;  // offset left
                    var ls = _libTF.GetComponent<PanelStabilizer>();
                    if (ls != null) ls.SnapToTarget();
                }
                LoadScanIndex();
                RefreshLibContent();
                // Activate newly created scan buttons (RefreshLibContent creates them inactive)
                SetBtnColsActive(_libTF, true);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[RegionalAR] ToggleLibPanel FAILED: {ex.Message}\n{ex.StackTrace}");
        }
    }

    void RefreshLibContent()
    {
        if (_libContent == null) return;

        // Destroy existing scan entry buttons (but keep the header/close)
        // Scan entries are children of _libContent
        for (int i = _libContent.transform.childCount - 1; i >= 0; i--)
            Destroy(_libContent.transform.GetChild(i).gameObject);

        // Also remove old library scan BtnCols (keep non-lib ones)
        // We tag library scan buttons with names starting with "LibScan_"
        for (int i = _btnCols.Count - 1; i >= 0; i--)
        {
            if (_btnCols[i].name.StartsWith("LibScan_"))
            {
                if (_btnCols[i].col != null) Destroy(_btnCols[i].col.gameObject);
                _btnCols.RemoveAt(i);
            }
        }

        if (_scanEntries.Count == 0)
        {
            MakeLbl(_libContent.transform, "No saved scans yet.\nDownload via WiFi to save.",
                    14, new Color(.6f, .6f, .7f), new Vector2(0, 0), new Vector2(420, 50));
            return;
        }

        // "Delete All" button if many entries
        if (_scanEntries.Count > 1)
        {
            MakeClickableBtn(_libContent.transform, _libTF, "DELETE ALL",
                new Color(0.15f, 0.03f, 0.02f, 0.95f), 10, new Vector2(160, 24), new Vector2(80, 28),
                DeleteAllScans);
        }

        float yStart = -10f;
        float rowH = 40f;
        for (int i = 0; i < _scanEntries.Count && i < 6; i++)  // max 6 visible (fits panel)
        {
            float y = yStart - i * rowH;
            var e = _scanEntries[i];
            string label = $"{e.name}  ({e.sizeBytes / (1024 * 1024)}MB)  {e.date}";
            bool isActive = e.name == _activeScanName;

            int capturedIdx = i;

#if UNITY_EDITOR
            // Desktop layout: [ LOAD (name + date)  ] [REN] [DEL]
            var scanBtn = MakeRect(_libContent.transform, $"LibEntry_{i}");
            SetRectT(scanBtn, new Vector2(-55, y), new Vector2(290, 36));
            scanBtn.AddComponent<Image>().color = isActive
                ? new Color(.1f, .35f, .15f)
                : new Color(.08f, .1f, .22f);
            var lbl = new GameObject("Lbl"); lbl.transform.SetParent(scanBtn.transform, false);
            var lr = lbl.AddComponent<RectTransform>();
            lr.anchorMin = Vector2.zero; lr.anchorMax = Vector2.one;
            lr.offsetMin = new Vector2(8, 2); lr.offsetMax = new Vector2(-8, -2);
            var lt = lbl.AddComponent<Text>();
            SetFont(lt, 12, isActive ? new Color(.5f, 1f, .6f) : Color.white);
            lt.text = label; lt.alignment = TextAnchor.MiddleLeft;

            RegBtnCol(_libTF, new Vector2(-55, y), new Vector2(290, 36),
                      () => LoadScanFromLibrary(capturedIdx), null, $"LibScan_Load_{i}");

            // Rename button (amber R) — desktop only
            var renBtn = MakeRect(_libContent.transform, $"LibRen_{i}");
            SetRectT(renBtn, new Vector2(135, y), new Vector2(40, 36));
            renBtn.AddComponent<Image>().color = new Color(.55f, .42f, .08f);
            MakeLbl(renBtn.transform, "REN", 12, Color.white, Vector2.zero, new Vector2(40, 36));
            RegBtnCol(_libTF, new Vector2(135, y), new Vector2(40, 36),
                      () => RenameScanInLibrary(capturedIdx), null, $"LibScan_Ren_{i}");

            // Delete button (red X)
            var delBtn = MakeRect(_libContent.transform, $"LibDel_{i}");
            SetRectT(delBtn, new Vector2(185, y), new Vector2(40, 36));
            delBtn.AddComponent<Image>().color = new Color(.5f, .1f, .1f);
            MakeLbl(delBtn.transform, "X", 16, Color.white, Vector2.zero, new Vector2(40, 36));
            RegBtnCol(_libTF, new Vector2(185, y), new Vector2(40, 36),
                      () => DeleteScanFromLibrary(capturedIdx), null, $"LibScan_Del_{i}");
#else
            // Quest layout: [ LOAD (name + date)       ] [DEL]  — no rename
            var scanBtn = MakeRect(_libContent.transform, $"LibEntry_{i}");
            SetRectT(scanBtn, new Vector2(-30, y), new Vector2(340, 36));
            scanBtn.AddComponent<Image>().color = isActive
                ? new Color(.1f, .35f, .15f)
                : new Color(.08f, .1f, .22f);
            var lbl = new GameObject("Lbl"); lbl.transform.SetParent(scanBtn.transform, false);
            var lr = lbl.AddComponent<RectTransform>();
            lr.anchorMin = Vector2.zero; lr.anchorMax = Vector2.one;
            lr.offsetMin = new Vector2(8, 2); lr.offsetMax = new Vector2(-8, -2);
            var lt = lbl.AddComponent<Text>();
            SetFont(lt, 12, isActive ? new Color(.5f, 1f, .6f) : Color.white);
            lt.text = label; lt.alignment = TextAnchor.MiddleLeft;

            RegBtnCol(_libTF, new Vector2(-30, y), new Vector2(340, 36),
                      () => LoadScanFromLibrary(capturedIdx), null, $"LibScan_Load_{i}");

            // Delete button (red X)
            var delBtn = MakeRect(_libContent.transform, $"LibDel_{i}");
            SetRectT(delBtn, new Vector2(185, y), new Vector2(40, 36));
            delBtn.AddComponent<Image>().color = new Color(.5f, .1f, .1f);
            MakeLbl(delBtn.transform, "X", 16, Color.white, Vector2.zero, new Vector2(40, 36));
            RegBtnCol(_libTF, new Vector2(185, y), new Vector2(40, 36),
                      () => DeleteScanFromLibrary(capturedIdx), null, $"LibScan_Del_{i}");
#endif
        }

        // Activate all newly created lib buttons if panel is open
        if (_libOpen) SetBtnColsActive(_libTF, true);
    }

    // ═════════════════════════════════════════════════════════════
    //  EVENT SYSTEM
    // ═════════════════════════════════════════════════════════════
    void SetupEventSystem()
    {
        if (FindObjectOfType<EventSystem>() != null) return;
        var es = new GameObject("EventSystem");
        es.AddComponent<EventSystem>();
        es.AddComponent<StandaloneInputModule>();
    }

    // ═════════════════════════════════════════════════════════════
    //  CONTROLS HINT
    // ═════════════════════════════════════════════════════════════
    void BuildHint()
    {
        _hintRoot = new GameObject("RegionalAR_Hint");
        _hintCanvas = _hintRoot.AddComponent<Canvas>();
        _hintCanvas.renderMode = RenderMode.WorldSpace;
        _hintCanvas.sortingOrder = 33;
        _hintRoot.AddComponent<PanelStabilizer>();  // smooth tracking jitter
        Camera cam = Cam();
        if (cam != null) _hintCanvas.worldCamera = cam;

        var rt = _hintRoot.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(440, 340);
        rt.pivot = new Vector2(0.5f, 0.5f);
        _hintRoot.transform.localScale = Vector3.one * 0.0008f;
        _hintTF = _hintRoot.transform;

        CreateProxy(out _hintProxy, out _hintProxyCol, 0.35f, 0.27f, "HintProxy");

        var bg = MakeRect(_hintRoot.transform, "BG");
        FillParent(bg);
        bg.AddComponent<Image>().color = new Color(0.02f, 0.02f, 0.04f, 0.92f);

        // Cyan border
        var hBorder = MakeRect(bg.transform, "HBorder");
        var hbRT = hBorder.GetComponent<RectTransform>();
        hbRT.anchorMin = Vector2.zero; hbRT.anchorMax = Vector2.one;
        hbRT.offsetMin = Vector2.zero; hbRT.offsetMax = Vector2.zero;
        hBorder.AddComponent<Image>().color = new Color(0.00f, 0.74f, 0.83f, 0.40f);
        var hInner = MakeRect(hBorder.transform, "Inner");
        var hiRT = hInner.GetComponent<RectTransform>();
        hiRT.anchorMin = Vector2.zero; hiRT.anchorMax = Vector2.one;
        hiRT.offsetMin = new Vector2(1.5f, 1.5f); hiRT.offsetMax = new Vector2(-1.5f, -1.5f);
        hInner.AddComponent<Image>().color = new Color(0.02f, 0.02f, 0.04f, 0.92f);

        float y = 145f;
        MakeLbl(bg.transform, "BLOCKAR CONTROLS", 20, new Color(0f, 0.74f, 0.83f),
                new Vector2(0, y), new Vector2(400, 36));

        string[] lines = {
            "X BUTTON  =  OPEN / CLOSE PANEL",
            "RIGHT GRIP  =  GRAB & MOVE VOLUME",
            "BOTH GRIPS  =  SCALE VOLUME OR PANEL",
            "EITHER TRIGGER  =  CLICK PANEL BUTTONS",
            "A BUTTON  =  CYCLE CUT PLANES",
            "LEFT STICK  =  MOVE ACTIVE CUT PLANE",
            "L GRIP (NEAR PANEL)  =  GRAB PANEL",
            "B BUTTON  =  RESET EVERYTHING"
        };
        Color cyanText = new Color(0f, 0.74f, 0.83f, 0.65f);
        Color whiteText = new Color(0.90f, 0.90f, 0.92f);
        Color[] lc = {
            whiteText, whiteText, whiteText, whiteText,
            cyanText, cyanText, cyanText, cyanText
        };
        for (int i = 0; i < lines.Length; i++)
        {
            y -= 30f;
            MakeLbl(bg.transform, lines[i], 13, lc[i], new Vector2(0, y), new Vector2(400, 24));
        }

        // Red X close button (top-right)
        MakeClickableBtn(bg.transform, _hintTF, "X",
            new Color(0.60f, 0.08f, 0.05f, 0.95f), 14, new Vector2(198, 152), new Vector2(28, 28),
            () => ToggleHint());

        // Hidden on startup — shown only when the user taps the GUIDE button.
        _hintRoot.SetActive(false);
        if (_hintProxy != null) _hintProxy.SetActive(false);
        SetBtnColsActive(_hintTF, false);
    }

    // ═════════════════════════════════════════════════════════════
    //  CONTROL PANEL BUILDER (tissue toggles + actions)
    // ═════════════════════════════════════════════════════════════
    GameObject _tissueSection;  // tissue layer toggles + marker buttons

    void BuildControlPanel()
    {
        // ── Color palette — Medivis-inspired surgical AR theme ──
        Color panelBG      = new Color(0.02f, 0.02f, 0.04f, 0.95f);   // near-black
        Color borderCyan   = new Color(0.00f, 0.74f, 0.83f, 0.70f);   // cyan outline/border
        Color accentCyan   = new Color(0.00f, 0.74f, 0.83f);          // cyan text/labels
        Color accentOrange = new Color(1.00f, 0.42f, 0.21f);          // orange for active states
        Color textPrimary  = new Color(0.94f, 0.94f, 0.96f);          // near-white
        Color textSecond   = new Color(0.45f, 0.50f, 0.56f);          // muted secondary
        Color btnFill      = new Color(0.04f, 0.04f, 0.06f, 0.95f);   // dark button interior

        var panelRoot = new GameObject("RegionalAR_CtrlPanel");
        _ctrlCanvas = panelRoot.AddComponent<Canvas>();
        _ctrlCanvas.renderMode = RenderMode.WorldSpace;
        _ctrlCanvas.sortingOrder = 30;
        panelRoot.AddComponent<PanelStabilizer>();  // smooth tracking jitter
        _ctrlTF = panelRoot.transform;
        Camera cam = Cam();
        if (cam != null) _ctrlCanvas.worldCamera = cam;
        else _needsCameraRetry = true;

        var crt = panelRoot.GetComponent<RectTransform>();
        crt.sizeDelta = new Vector2(560, 520);
        crt.pivot = new Vector2(0.5f, 0.5f);
        panelRoot.transform.localScale = Vector3.one * 0.001f;

        CreateProxy(out _ctrlProxy, out _ctrlProxyCol, 0.56f, 0.52f, "CtrlProxy");

        _ctrlBG = MakeRect(panelRoot.transform, "CtrlBG");
        FillParent(_ctrlBG);
        _ctrlBG.AddComponent<Image>().color = panelBG;

        // ── Outer border (thin cyan frame) ──────────────────────
        var borderFrame = MakeRect(_ctrlBG.transform, "Border");
        var bfRT = borderFrame.GetComponent<RectTransform>();
        bfRT.anchorMin = Vector2.zero; bfRT.anchorMax = Vector2.one;
        bfRT.offsetMin = Vector2.zero; bfRT.offsetMax = Vector2.zero;
        borderFrame.AddComponent<Image>().color = borderCyan;
        var innerFill = MakeRect(borderFrame.transform, "InnerFill");
        var ifRT = innerFill.GetComponent<RectTransform>();
        ifRT.anchorMin = Vector2.zero; ifRT.anchorMax = Vector2.one;
        ifRT.offsetMin = new Vector2(1.5f, 1.5f); ifRT.offsetMax = new Vector2(-1.5f, -1.5f);
        innerFill.AddComponent<Image>().color = panelBG;

        // ── Header (slim, all-caps, cyan accent line below) ─────
        var headerBG = MakeRect(_ctrlBG.transform, "Header");
        SetRectT(headerBG, new Vector2(0, 206), new Vector2(556, 38));
        headerBG.AddComponent<Image>().color = new Color(0.03f, 0.03f, 0.05f, 0.98f);
        MakeLbl(headerBG.transform, "BLOCKAR", 20, accentCyan, new Vector2(-90, 0), new Vector2(180, 34));
        MakeLbl(headerBG.transform, "CONTROL", 12, textSecond, new Vector2(60, 0), new Vector2(120, 34));

        // Thin cyan separator line below header
        var headerLine = MakeRect(_ctrlBG.transform, "HeaderLine");
        SetRectT(headerLine, new Vector2(0, 186), new Vector2(540, 1.5f));
        headerLine.AddComponent<Image>().color = accentCyan;

        // Moved up from 94 to 140 — the extra room at the top (from the
        // removed grayscale toggle) lets us spread out the tissue rows,
        // opacity slider, and marker buttons without cramping.
        float contentTop = 140f;

        // ── Tissue Layers section ───────────────────────────────
        _tissueSection = MakeRect(_ctrlBG.transform, "TissueSection");
        SetRectT(_tissueSection, new Vector2(0, contentTop - 115f), new Vector2(520, 440));

        MakeLbl(_tissueSection.transform, "TISSUE LAYERS",
                10, accentCyan, new Vector2(0, 102f), new Vector2(520, 18));
        // Thin separator
        var tlLine = MakeRect(_tissueSection.transform, "TLLine");
        SetRectT(tlLine, new Vector2(0, 90f), new Vector2(500, 1f));
        tlLine.AddComponent<Image>().color = new Color(0f, 0.74f, 0.83f, 0.30f);

        var vr = volumeRenderer ?? FindObjectOfType<VolumeRenderer>();
        string[] tNames = { "BONE", "VASCULATURE", "NERVES", "MUSCLE" };
        Color[] tCols = { new Color(.95f, .92f, .85f), new Color(.85f, .15f, .12f),
                          new Color(.95f, .85f, .15f), new Color(.75f, .25f, .38f) };

        for (int i = 0; i < 4; i++)
        {
            string lName = (i < 3 && vr != null && i < vr.layers.Length) ? vr.layers[i].name.ToUpper() : tNames[i];
            Color  lCol  = (i < 3 && vr != null && i < vr.layers.Length) ? vr.layers[i].color : tCols[i];
            // Initial ON state: if a mesh is loaded for this layer, check
            // mesh visibility (which is true by default after load). Only
            // fall back to volume layer state if no mesh exists. This fixes
            // toggles showing "off" when meshes are actually visible.
            bool lOn = true;  // default: on
            if (vr != null)
            {
                if (i == 0) { var m = vr.GetComponent<BoneMeshLoader>();   lOn = (m != null && m.HasMesh) ? m.IsMeshEnabled : (i < vr.layers.Length && vr.layers[i].enabled); }
                else if (i == 1) { var m = vr.GetComponent<VesselMeshLoader>(); lOn = (m != null && m.HasMesh) ? m.IsMeshEnabled : (i < vr.layers.Length && vr.layers[i].enabled); }
                else if (i == 2) { var m = vr.GetComponent<NerveMeshLoader>();  lOn = (m != null && m.HasMesh) ? m.IsMeshEnabled : (i < vr.layers.Length && vr.layers[i].enabled); }
                else if (i == 3) { var m = vr.GetComponent<MuscleMeshLoader>(); lOn = (m != null && m.HasMesh) ? m.IsMeshEnabled : false; }
            }
            float  yPos  = 100f - 28f - i * 44f;  // 44px spacing, shifted up to use top space

            // Outlined button row — cyan border, orange fill when active
            var row = MakeRect(_tissueSection.transform, $"L{i}");
            SetRectT(row, new Vector2(0, yPos), new Vector2(500, 32));
            row.AddComponent<Image>().color = lOn ? accentOrange : borderCyan;
            var rowFill = MakeRect(row.transform, "Fill");
            var rfRT = rowFill.GetComponent<RectTransform>();
            rfRT.anchorMin = Vector2.zero; rfRT.anchorMax = Vector2.one;
            rfRT.offsetMin = new Vector2(1.5f, 1.5f); rfRT.offsetMax = new Vector2(-1.5f, -1.5f);
            var rowImg = rowFill.AddComponent<Image>();
            rowImg.color = lOn ? new Color(0.40f, 0.16f, 0.06f, 0.85f) : btnFill;

            // Color swatch (tissue identification)
            var sw = MakeRect(rowFill.transform, "Sw");
            var swRT = sw.GetComponent<RectTransform>();
            swRT.anchorMin = swRT.anchorMax = swRT.pivot = new Vector2(0, .5f);
            swRT.anchoredPosition = new Vector2(10, 0); swRT.sizeDelta = new Vector2(14, 14);
            sw.AddComponent<Image>().color = lCol;

            // Toggle for state management
            var tog = row.AddComponent<Toggle>();
            tog.isOn = lOn;
            tog.targetGraphic = rowImg;

            int ci = i;
            Image capturedRowImg = rowImg;
            Image capturedBorder = row.GetComponent<Image>();
            tog.onValueChanged.AddListener((bool v) => {
                OnLayerToggle(ci, v);
                capturedRowImg.color = v ? new Color(0.40f, 0.16f, 0.06f, 0.85f) : btnFill;
                capturedBorder.color = v ? accentOrange : borderCyan;
            });

            // Absolute canvas Y = tissueSection center Y + local row Y
            float absY = (contentTop - 115f) + yPos;
            RegBtnCol(_ctrlTF, new Vector2(0, absY), new Vector2(500, 32), null, tog, lName);

            // Centered all-caps label
            MakeLbl(rowFill.transform, lName, 12, textPrimary, new Vector2(0, 0), new Vector2(440, 30));
        }

        // ── Opacity toggle + slider (between tissue toggles and markers) ──
        BuildOpacitySlider(_tissueSection.transform, contentTop,
                           accentCyan, accentOrange, borderCyan, btnFill, textPrimary);

        // ── Teaching markers (three compact buttons below tissue layers) ──
        BuildMarkerControls(_tissueSection.transform, contentTop,
                            accentCyan, accentOrange, borderCyan, btnFill, textPrimary);

        // NOTE: the BONE / VASCULATURE / NERVES tissue rows above drive the
        // bone / vessel / nerve MESHES directly when they're loaded
        // (see OnLayerToggle). They fall back to toggling the volume layer
        // only if the mesh is missing (e.g. non-AI mode).

        // ── Thin separator above action buttons ─────────────────
        var actLine = MakeRect(_ctrlBG.transform, "ActLine");
        SetRectT(actLine, new Vector2(0, -198f), new Vector2(540, 1f));
        actLine.AddComponent<Image>().color = new Color(0f, 0.74f, 0.83f, 0.30f);

        // ── Red X close button (top-right corner) ─────────────
        MakeClickableBtn(_ctrlBG.transform, _ctrlTF, "X",
            new Color(0.60f, 0.08f, 0.05f, 0.95f), 16, new Vector2(254, 206), new Vector2(30, 30),
            () => ToggleCtrlPanel());

        // ── Action Buttons (bottom row — outlined cyan-border style) ──
        float btnY = -223f;
        float bw = 96f;
        float sp = 6f;
        float totalW = 5 * bw + 4 * sp;
        float x0 = -totalW / 2f + bw / 2f;

        MakeClickableBtn(_ctrlBG.transform, _ctrlTF, "WIFI",
            btnFill, 11, new Vector2(x0, btnY), new Vector2(bw, 34),
            () => ToggleWiFiPanel());

        MakeClickableBtn(_ctrlBG.transform, _ctrlTF, "LIBRARY",
            btnFill, 11, new Vector2(x0 + (bw + sp), btnY), new Vector2(bw, 34),
            () => ToggleLibPanel());

        MakeClickableBtn(_ctrlBG.transform, _ctrlTF, "CUT PLANE",
            btnFill, 10, new Vector2(x0 + 2 * (bw + sp), btnY), new Vector2(bw, 34),
            CycleCutPlane);

        // NOTE: Controller/Hands toggle removed for now — re-enable in future build
        // Shift GUIDE button into the freed slot
        MakeClickableBtn(_ctrlBG.transform, _ctrlTF, "GUIDE",
            btnFill, 11, new Vector2(x0 + 3 * (bw + sp), btnY), new Vector2(bw, 34),
            () => ToggleHint());

        // QUIT — red-tinted fill to mark it as a destructive action, occupies
        // the 5th slot reserved in the bottom row layout.
        MakeClickableBtn(_ctrlBG.transform, _ctrlTF, "QUIT",
            new Color(0.55f, 0.08f, 0.06f, 0.95f), 11,
            new Vector2(x0 + 4 * (bw + sp), btnY), new Vector2(bw, 34),
            () => QuitApp());

        _ctrlBG.SetActive(false);
        Debug.Log($"[RegionalAR] Control panel built. Buttons={_btnCols.Count} Sliders={_sliderCols.Count}");
    }

    /// Helper: same as MakeLbl but returns the Text component for later updates
    static Text MakeLblReturn(Transform p, string txt, int sz, Color c, Vector2 pos, Vector2 size)
    {
        var go = new GameObject("Lbl"); go.transform.SetParent(p, false);
        var r = go.AddComponent<RectTransform>();
        r.anchorMin = r.anchorMax = r.pivot = new Vector2(.5f, .5f);
        r.anchoredPosition = pos; r.sizeDelta = size;
        var t = go.AddComponent<Text>();
        SetFont(t, sz, c); t.text = txt; t.alignment = TextAnchor.MiddleCenter;
        return t;
    }

    // ═════════════════════════════════════════════════════════════
    //  WIFI PANEL BUILDER (IP + download + status)
    // ═════════════════════════════════════════════════════════════
    void BuildWiFiPanel()
    {
        var panelRoot = new GameObject("RegionalAR_WiFiPanel");

        // IMPORTANT: Add Canvas FIRST — replaces Transform with RectTransform.
        _wifiCanvas = panelRoot.AddComponent<Canvas>();
        _wifiCanvas.renderMode = RenderMode.WorldSpace;
        _wifiCanvas.sortingOrder = 31;
        panelRoot.AddComponent<PanelStabilizer>();  // smooth tracking jitter
        _wifiTF = panelRoot.transform;   // now holds RectTransform (valid)
        Camera cam = Cam();
        if (cam != null) _wifiCanvas.worldCamera = cam;
        else _needsCameraRetry = true;

        var crt = panelRoot.GetComponent<RectTransform>();
        crt.sizeDelta = new Vector2(480, 240);
        crt.pivot = new Vector2(0.5f, 0.5f);
        panelRoot.transform.localScale = Vector3.one * 0.001f;

        CreateProxy(out _wifiProxy, out _wifiProxyCol, 0.48f, 0.24f, "WiFiProxy");

        _wifiBG = MakeRect(panelRoot.transform, "WiFiBG");
        FillParent(_wifiBG);
        _wifiBG.AddComponent<Image>().color = new Color(0.02f, 0.02f, 0.04f, 0.95f);

        // Cyan border frame
        var wfBorder = MakeRect(_wifiBG.transform, "WfBorder");
        var wfbRT = wfBorder.GetComponent<RectTransform>();
        wfbRT.anchorMin = Vector2.zero; wfbRT.anchorMax = Vector2.one;
        wfbRT.offsetMin = Vector2.zero; wfbRT.offsetMax = Vector2.zero;
        wfBorder.AddComponent<Image>().color = new Color(0.00f, 0.74f, 0.83f, 0.55f);
        var wfInner = MakeRect(wfBorder.transform, "Inner");
        var wfiRT = wfInner.GetComponent<RectTransform>();
        wfiRT.anchorMin = Vector2.zero; wfiRT.anchorMax = Vector2.one;
        wfiRT.offsetMin = new Vector2(1.5f, 1.5f); wfiRT.offsetMax = new Vector2(-1.5f, -1.5f);
        wfInner.AddComponent<Image>().color = new Color(0.02f, 0.02f, 0.04f, 0.95f);

        MakeLbl(_wifiBG.transform, "WIFI TRANSFER",
                18, new Color(0.00f, 0.74f, 0.83f), new Vector2(0, 92), new Vector2(440, 32));

        // IP input
        var inp = MakeRect(_wifiBG.transform, "IPInput");
        SetRectT(inp, new Vector2(-60, 50), new Vector2(280, 44));
        inp.AddComponent<Image>().color = new Color(0.06f, 0.06f, 0.08f);
        _ipInput = inp.AddComponent<InputField>();
        _ipInput.characterLimit = 64;
        var inpT = MakeRect(inp.transform, "Txt").AddComponent<Text>();
        SetFont(inpT, 18, Color.white); StretchRT(inpT.GetComponent<RectTransform>());
        _ipInput.textComponent = inpT;
        _ipInput.text = PlayerPrefs.GetString(PREFS_IP, "");
        var ph = MakeRect(inp.transform, "Ph").AddComponent<Text>();
        SetFont(ph, 16, new Color(.5f, .5f, .5f, .6f)); ph.fontStyle = FontStyle.Italic;
        ph.text = "Searching...";
        StretchRT(ph.GetComponent<RectTransform>()); _ipInput.placeholder = ph;

        // Download button
        var dlGO = MakeClickableBtn(_wifiBG.transform, _wifiTF, "DOWNLOAD",
            new Color(0.04f, 0.04f, 0.06f, 0.95f), 14, new Vector2(170, 50), new Vector2(110, 44),
            StartDownload);
        _downloadBtn = dlGO.GetComponent<Button>();

        // Progress bar
        var pbg = MakeRect(_wifiBG.transform, "ProgBG");
        SetRectT(pbg, new Vector2(0, 16), new Vector2(440, 8));
        pbg.AddComponent<Image>().color = new Color(.04f, .04f, .08f);
        var pf = MakeRect(pbg.transform, "Fill");
        _fillRT = pf.GetComponent<RectTransform>();
        _fillRT.anchorMin = Vector2.zero; _fillRT.anchorMax = new Vector2(0, 1);
        _fillRT.offsetMin = _fillRT.offsetMax = Vector2.zero;
        pf.AddComponent<Image>().color = new Color(0f, 0.74f, 0.83f);

        // Status
        var st = MakeRect(_wifiBG.transform, "Status");
        SetRectT(st, new Vector2(0, -8), new Vector2(440, 22));
        _statusText = st.AddComponent<Text>();
        SetFont(_statusText, 12, new Color(0f, 0.74f, 0.83f));
        _statusText.alignment = TextAnchor.MiddleCenter;
        _statusText.text = "Searching for server...";

        // Red X close button (top-right)
        MakeClickableBtn(_wifiBG.transform, _wifiTF, "X",
            new Color(0.60f, 0.08f, 0.05f, 0.95f), 14, new Vector2(218, 100), new Vector2(28, 28),
            () => ToggleWiFiPanel());

        _wifiBG.SetActive(false);

        Debug.Log("[RegionalAR] WiFi panel built.");
    }

    // ═════════════════════════════════════════════════════════════
    //  LIBRARY PANEL BUILDER
    // ═════════════════════════════════════════════════════════════
    void BuildLibPanel()
    {
        var panelRoot = new GameObject("RegionalAR_LibPanel");

        _libCanvas = panelRoot.AddComponent<Canvas>();
        _libCanvas.renderMode = RenderMode.WorldSpace;
        _libCanvas.sortingOrder = 32;
        panelRoot.AddComponent<PanelStabilizer>();  // smooth tracking jitter
        _libTF = panelRoot.transform;
        Camera cam = Cam();
        if (cam != null) _libCanvas.worldCamera = cam;
        else _needsCameraRetry = true;

        var crt = panelRoot.GetComponent<RectTransform>();
        crt.sizeDelta = new Vector2(500, 400);
        crt.pivot = new Vector2(0.5f, 0.5f);
        panelRoot.transform.localScale = Vector3.one * 0.001f;

        CreateProxy(out _libProxy, out _libProxyCol, 0.50f, 0.40f, "LibProxy");

        _libBG = MakeRect(panelRoot.transform, "LibBG");
        FillParent(_libBG);
        _libBG.AddComponent<Image>().color = new Color(0.02f, 0.02f, 0.04f, 0.95f);

        // Cyan border frame
        var lbBorder = MakeRect(_libBG.transform, "LbBorder");
        var lbbRT = lbBorder.GetComponent<RectTransform>();
        lbbRT.anchorMin = Vector2.zero; lbbRT.anchorMax = Vector2.one;
        lbbRT.offsetMin = Vector2.zero; lbbRT.offsetMax = Vector2.zero;
        lbBorder.AddComponent<Image>().color = new Color(0.00f, 0.74f, 0.83f, 0.55f);
        var lbInner = MakeRect(lbBorder.transform, "Inner");
        var lbiRT = lbInner.GetComponent<RectTransform>();
        lbiRT.anchorMin = Vector2.zero; lbiRT.anchorMax = Vector2.one;
        lbiRT.offsetMin = new Vector2(1.5f, 1.5f); lbiRT.offsetMax = new Vector2(-1.5f, -1.5f);
        lbInner.AddComponent<Image>().color = new Color(0.02f, 0.02f, 0.04f, 0.95f);

        MakeLbl(_libBG.transform, "SCAN LIBRARY",
                18, new Color(0.00f, 0.74f, 0.83f), new Vector2(0, 170), new Vector2(460, 32));

        // Scrollable content area
        _libContent = MakeRect(_libBG.transform, "LibContent");
        SetRectT(_libContent, new Vector2(0, 10), new Vector2(460, 300));

        // Red X close button (top-right)
        MakeClickableBtn(_libBG.transform, _libTF, "X",
            new Color(0.60f, 0.08f, 0.05f, 0.95f), 14, new Vector2(228, 178), new Vector2(28, 28),
            () => ToggleLibPanel());

        _libBG.SetActive(false);

        Debug.Log("[RegionalAR] Library panel built.");
    }

    // ═════════════════════════════════════════════════════════════
    //  PANEL ELEMENT HELPERS
    // ═════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a visual button AND registers a 3D collider for it.
    /// </summary>
    GameObject MakeClickableBtn(Transform parent, Transform panel, string label,
                                Color fillColor, int sz, Vector2 pos, Vector2 size, Action onClick)
    {
        // Outer border (cyan)
        var go = MakeRect(parent, label.Replace(" ", ""));
        SetRectT(go, pos, size);
        go.AddComponent<Image>().color = new Color(0.00f, 0.74f, 0.83f, 0.55f);

        // Inner fill
        var fill = MakeRect(go.transform, "Fill");
        var fRT = fill.GetComponent<RectTransform>();
        fRT.anchorMin = Vector2.zero; fRT.anchorMax = Vector2.one;
        fRT.offsetMin = new Vector2(1.5f, 1.5f); fRT.offsetMax = new Vector2(-1.5f, -1.5f);
        var bgImg = fill.AddComponent<Image>();
        bgImg.color = fillColor;

        var btn = go.AddComponent<Button>();
        btn.onClick.AddListener(() => onClick());
        MakeLbl(fill.transform, label, sz, new Color(0.94f, 0.94f, 0.96f), Vector2.zero, size);

        RegBtnCol(panel, pos, size, onClick, null, label, bgImg, fillColor);
        return go;
    }

    // ═════════════════════════════════════════════════════════════
    //  OPACITY — toggle button (matches MARKERS style) that reveals a
    //  slider when active. Controls mesh + volume opacity (0–1).
    // ═════════════════════════════════════════════════════════════
    Image _opacityModeBG;
    Image _opacityModeBorder;
    GameObject _opacitySliderRow;
    int        _opacitySliderColIdx = -1;
    bool       _opacityOpen;

    void BuildOpacitySlider(Transform tissueSectionTF, float contentTop,
                            Color accentCyan, Color accentOrange,
                            Color borderCyan, Color btnFill, Color textPrimary)
    {
        // ── Toggle button — same style / size as MARKERS ──────────
        const float btnY  = -100f;   // local Y inside tissue section
        const float btnH  = 30f;
        const float btnW  = 155f;
        // Left-aligned to match MARKERS at x = -165
        const float btnX  = -165f;

        var row = MakeRect(tissueSectionTF, "OpacityBtn");
        SetRectT(row, new Vector2(btnX, btnY), new Vector2(btnW, btnH));
        var border = row.AddComponent<Image>();
        border.color = borderCyan;

        var fill = MakeRect(row.transform, "Fill");
        var fRT = fill.GetComponent<RectTransform>();
        fRT.anchorMin = Vector2.zero; fRT.anchorMax = Vector2.one;
        fRT.offsetMin = new Vector2(1.5f, 1.5f); fRT.offsetMax = new Vector2(-1.5f, -1.5f);
        var fillImg = fill.AddComponent<Image>();
        fillImg.color = btnFill;

        MakeLbl(fill.transform, "OPACITY", 12, textPrimary,
                new Vector2(0, 0), new Vector2(btnW - 10, btnH));

        _opacityModeBorder = border;
        _opacityModeBG     = fillImg;

        float absYBtn = (contentTop - 115f) + btnY;
        RegBtnCol(_ctrlTF, new Vector2(btnX, absYBtn), new Vector2(btnW, btnH),
            () => {
                _opacityOpen = !_opacityOpen;
                PaintOpacityButton(_opacityOpen, accentOrange, borderCyan, btnFill);
                ApplyOpacitySliderVisibility();
            },
            null, "OpacityToggle");

        // ── Slider row — hidden until toggle is on ────────────────
        const float sliderY = -100f;  // same row as the button, to the right of it
        const float sliderW = 310f;
        const float sliderH = 22f;
        // Position slider to the right of the button, centered in remaining space
        const float sliderX = 75f;

        // Container GO for the slider elements (show/hide together)
        var sliderContainer = MakeRect(tissueSectionTF, "OpacitySliderRow");
        SetRectT(sliderContainer, Vector2.zero, Vector2.zero);
        var scRT = sliderContainer.GetComponent<RectTransform>();
        scRT.anchorMin = Vector2.zero; scRT.anchorMax = Vector2.one;
        scRT.offsetMin = Vector2.zero; scRT.offsetMax = Vector2.zero;
        _opacitySliderRow = sliderContainer;

        // Slider track background
        var track = MakeRect(sliderContainer.transform, "OpacityTrack");
        SetRectT(track, new Vector2(sliderX, sliderY), new Vector2(sliderW, sliderH));
        var trackImg = track.AddComponent<Image>();
        trackImg.color = btnFill;

        // Fill bar
        var fillGO = MakeRect(track.transform, "Fill");
        var fillRT2 = fillGO.GetComponent<RectTransform>();
        fillRT2.anchorMin = Vector2.zero;
        fillRT2.anchorMax = new Vector2(1f, 1f);
        fillRT2.offsetMin = Vector2.zero;
        fillRT2.offsetMax = Vector2.zero;
        var sliderFillImg = fillGO.AddComponent<Image>();
        sliderFillImg.color = new Color(accentCyan.r, accentCyan.g, accentCyan.b, 0.35f);

        // Knob indicator
        var knob = MakeRect(track.transform, "Knob");
        var knobRT = knob.GetComponent<RectTransform>();
        knobRT.anchorMin = knobRT.anchorMax = new Vector2(1f, 0.5f);
        knobRT.sizeDelta = new Vector2(10f, sliderH + 4f);
        knobRT.anchoredPosition = Vector2.zero;
        var knobImg = knob.AddComponent<Image>();
        knobImg.color = accentCyan;

        // 3D collider for slider
        float absYSlider = (contentTop - 115f) + sliderY;
        RegSliderCol(_ctrlTF, new Vector2(sliderX, absYSlider), new Vector2(sliderW, sliderH + 10f),
                     sliderFillImg, knobRT, 0f, 1f, 1f,
                     (float val) => ApplyGlobalOpacity(val),
                     "Opacity");
        _opacitySliderColIdx = _sliderCols.Count - 1;

        // Start hidden
        _opacityOpen = false;
        _opacitySliderRow.SetActive(false);
    }

    void PaintOpacityButton(bool on, Color accentOrange, Color borderCyan, Color btnFill)
    {
        if (_opacityModeBorder != null)
            _opacityModeBorder.color = on ? accentOrange : borderCyan;
        if (_opacityModeBG != null)
            _opacityModeBG.color = on ? new Color(0.40f, 0.16f, 0.06f, 0.85f) : btnFill;
    }

    void ApplyOpacitySliderVisibility()
    {
        bool show = _opacityOpen && _ctrlOpen;
        if (_opacitySliderRow != null) _opacitySliderRow.SetActive(show);
        if (_opacitySliderColIdx >= 0 && _opacitySliderColIdx < _sliderCols.Count
            && _sliderCols[_opacitySliderColIdx].go != null)
            _sliderCols[_opacitySliderColIdx].go.SetActive(show);
    }

    void ApplyGlobalOpacity(float opacity)
    {
        var vr = volumeRenderer ?? FindObjectOfType<VolumeRenderer>();
        if (vr == null) return;

        // Volume AlphaScale: remap 0-1 → 0-1.5 (default is 1.5 at full)
        vr.AlphaScale = opacity * 1.5f;
        // Push to material immediately
        if (vr.volumeMaterial != null || true)
        {
            // Access the runtime material via the renderer
            var mr = vr.GetComponent<MeshRenderer>();
            if (mr != null && mr.material != null)
                mr.material.SetFloat("_AlphaScale", vr.AlphaScale);
        }

        // Mesh opacity: adjust material color alpha on each mesh
        float meshAlpha = Mathf.Clamp01(opacity);
        var bml = vr.GetComponent<BoneMeshLoader>();
        if (bml != null) SetMeshOpacity(bml.GetMeshMaterial(), meshAlpha);
        var vml = vr.GetComponent<VesselMeshLoader>();
        if (vml != null) SetMeshOpacity(vml.GetMeshMaterial(), meshAlpha);
        var nml = vr.GetComponent<NerveMeshLoader>();
        if (nml != null) SetMeshOpacity(nml.GetMeshMaterial(), meshAlpha);
        var mml = vr.GetComponent<MuscleMeshLoader>();
        if (mml != null) SetMeshOpacity(mml.GetMeshMaterial(), meshAlpha);
    }

    void SetMeshOpacity(Material mat, float alpha)
    {
        if (mat == null) return;
        if (mat.HasProperty("_Opacity"))
            mat.SetFloat("_Opacity", alpha);
        else if (mat.HasProperty("_Color"))
        {
            Color c = mat.GetColor("_Color");
            c.a = alpha;
            mat.SetColor("_Color", c);
        }
    }

    // ═════════════════════════════════════════════════════════════
    //  Teaching markers — three buttons below the tissue layer rows.
    //  MARKERS  → toggles placement mode on the volume's MarkerManager
    //  UNDO     → removes the most recently placed marker
    //  CLEAR    → removes every marker
    // ═════════════════════════════════════════════════════════════
    Image _markerModeBG;
    Image _markerModeBorder;
    // Row visuals + collider GOs for UNDO / CLEAR — hidden unless marker mode is on
    GameObject _undoMarkerRow, _clearMarkerRow;
    int        _undoMarkerBtnIdx = -1, _clearMarkerBtnIdx = -1;
    MarkerManager _markerMgr;
    void BuildMarkerControls(Transform tissueSectionTF, float contentTop,
                             Color accentCyan, Color accentOrange,
                             Color borderCyan, Color btnFill, Color textPrimary)
    {
        var vr = volumeRenderer ?? FindObjectOfType<VolumeRenderer>();
        MarkerManager mm = null;
        if (vr != null)
        {
            mm = vr.GetComponent<MarkerManager>();
            if (mm == null) mm = vr.gameObject.AddComponent<MarkerManager>();
        }
        _markerMgr = mm;

        // Row local Y inside tissueSection
        // 4 tissue rows, opacity at -100, markers at -140.
        const float rowY = -140f;
        const float btnH = 30f;
        const float btnW = 155f;

        // Positions (x) inside the 520-wide section: left / middle / right
        float[] xs = { -165f, 0f, 165f };
        string[] labels = { "MARKERS", "UNDO", "CLEAR" };

        for (int i = 0; i < 3; i++)
        {
            int idx = i;
            var row = MakeRect(tissueSectionTF, $"MarkerBtn_{idx}");
            SetRectT(row, new Vector2(xs[i], rowY), new Vector2(btnW, btnH));
            var border = row.AddComponent<Image>();
            border.color = borderCyan;

            var fill = MakeRect(row.transform, "Fill");
            var fRT = fill.GetComponent<RectTransform>();
            fRT.anchorMin = Vector2.zero; fRT.anchorMax = Vector2.one;
            fRT.offsetMin = new Vector2(1.5f, 1.5f); fRT.offsetMax = new Vector2(-1.5f, -1.5f);
            var fillImg = fill.AddComponent<Image>();
            fillImg.color = btnFill;

            MakeLbl(fill.transform, labels[i], 12, textPrimary,
                    new Vector2(0, 0), new Vector2(btnW - 10, btnH));

            // Absolute canvas Y for the 3D collider:
            //   tissueSection center Y in absolute canvas = contentTop - 115f
            //   plus the row's local Y of -118
            float absY = (contentTop - 115f) + rowY;

            if (idx == 0) // MARKERS toggle
            {
                _markerModeBorder = border;
                _markerModeBG     = fillImg;
                RegBtnCol(_ctrlTF, new Vector2(xs[i], absY), new Vector2(btnW, btnH),
                    () => {
                        if (mm == null)
                        {
                            var vr2 = volumeRenderer ?? FindObjectOfType<VolumeRenderer>();
                            if (vr2 != null) mm = vr2.GetComponent<MarkerManager>() ?? vr2.gameObject.AddComponent<MarkerManager>();
                        }
                        if (mm != null) mm.ToggleMarkerMode();
                        PaintMarkerModeButton(mm != null && mm.IsMarkerMode, accentOrange, borderCyan, btnFill);
                    },
                    null, "Markers");
            }
            else if (idx == 1) // UNDO
            {
                _undoMarkerRow = row;
                RegBtnCol(_ctrlTF, new Vector2(xs[i], absY), new Vector2(btnW, btnH),
                    () => { if (mm != null) mm.UndoLast(); },
                    null, "UndoMarker");
                _undoMarkerBtnIdx = _btnCols.Count - 1;  // RegBtnCol just appended
            }
            else // CLEAR
            {
                _clearMarkerRow = row;
                RegBtnCol(_ctrlTF, new Vector2(xs[i], absY), new Vector2(btnW, btnH),
                    () => { if (mm != null) mm.ClearAll(); },
                    null, "ClearMarkers");
                _clearMarkerBtnIdx = _btnCols.Count - 1;
            }
        }

        // Keep the toggle button color in sync AND show/hide UNDO+CLEAR
        // only while marker mode is on.
        if (mm != null)
        {
            mm.OnMarkerModeChanged += (on) =>
            {
                PaintMarkerModeButton(on, accentOrange, borderCyan, btnFill);
                ApplyMarkerButtonVisibility();
            };
        }
        ApplyMarkerButtonVisibility();
    }

    // Show UNDO + CLEAR only when marker mode is active AND the control
    // panel is open. Called from OnMarkerModeChanged and ToggleCtrlPanel.
    void ApplyMarkerButtonVisibility()
    {
        bool markerOn = _markerMgr != null && _markerMgr.IsMarkerMode;
        bool show     = markerOn && _ctrlOpen;

        if (_undoMarkerRow  != null) _undoMarkerRow.SetActive(show);
        if (_clearMarkerRow != null) _clearMarkerRow.SetActive(show);
        if (_undoMarkerBtnIdx  >= 0 && _undoMarkerBtnIdx  < _btnCols.Count
            && _btnCols[_undoMarkerBtnIdx].go  != null)
            _btnCols[_undoMarkerBtnIdx].go.SetActive(show);
        if (_clearMarkerBtnIdx >= 0 && _clearMarkerBtnIdx < _btnCols.Count
            && _btnCols[_clearMarkerBtnIdx].go != null)
            _btnCols[_clearMarkerBtnIdx].go.SetActive(show);
    }

    void PaintMarkerModeButton(bool on, Color accentOrange, Color borderCyan, Color btnFill)
    {
        if (_markerModeBorder != null) _markerModeBorder.color = on ? accentOrange : borderCyan;
        if (_markerModeBG     != null) _markerModeBG.color     = on ? new Color(0.40f, 0.16f, 0.06f, 0.85f) : btnFill;
    }

    // ═════════════════════════════════════════════════════════════
    //  UI HELPERS (Canvas visuals)
    // ═════════════════════════════════════════════════════════════
    static GameObject MakeRect(Transform parent, string name)
    {
        var go = new GameObject(name); go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>(); return go;
    }
    static void FillParent(GameObject go)
    {
        var r = go.GetComponent<RectTransform>();
        r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
        r.offsetMin = r.offsetMax = Vector2.zero;
    }
    static void SetRectT(GameObject go, Vector2 pos, Vector2 size)
    {
        var r = go.GetComponent<RectTransform>();
        r.anchorMin = r.anchorMax = r.pivot = new Vector2(.5f, .5f);
        r.anchoredPosition = pos; r.sizeDelta = size;
    }
    static void StretchRT(RectTransform r)
    {
        r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
        r.offsetMin = new Vector2(8, 4); r.offsetMax = new Vector2(-8, -4);
    }
    static void MakeLbl(Transform p, string txt, int sz, Color c, Vector2 pos, Vector2 size)
    {
        var go = new GameObject("Lbl"); go.transform.SetParent(p, false);
        var r = go.AddComponent<RectTransform>();
        r.anchorMin = r.anchorMax = r.pivot = new Vector2(.5f, .5f);
        r.anchoredPosition = pos; r.sizeDelta = size;
        var t = go.AddComponent<Text>();
        SetFont(t, sz, c); t.text = txt; t.alignment = TextAnchor.MiddleCenter;
    }
    static void SetFont(Text t, int sz, Color c)
    {
        t.font = Font.CreateDynamicFontFromOSFont("Arial", sz);
        if (t.font == null) t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = sz; t.color = c;
    }
}
