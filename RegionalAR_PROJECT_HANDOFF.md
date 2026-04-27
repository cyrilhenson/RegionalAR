# RegionalAR — Complete Project Handoff

> This document captures everything needed to continue development on RegionalAR. Paste this into a new Claude session to provide full project context.

---

## 1. What Is RegionalAR?

RegionalAR is a **Meta Quest 3 AR application** for viewing 3D medical imaging volumes (CT/MRI scans) as interactive holograms in augmented reality. It consists of two components:

- **Quest App** (Unity/C#) — AR viewer with hand tracking, tissue layer toggles, cut planes, volume rendering, and wireless volume download
- **Desktop App** (Python/tkinter) — DICOM/NRRD processor with AI-powered tissue segmentation (TotalSegmentator), mesh generation, and wireless transfer to Quest

### Business Model
- Quest app is **free** on the Meta Quest Store with a bundled sample Head & Neck CTA volume
- Desktop companion app is a **$9.99 IAP add-on** (SKU: `desktop_license`, Durable type)
- After purchase, the Quest app displays a code + license key; user enters these in the desktop app to activate
- License validation is offline using HMAC-SHA256 (shared secret between Quest C# and desktop Python)

---

## 2. Repository & Distribution

- **GitHub**: https://github.com/cyrilhenson/RegionalAR (public)
- **GitHub Pages**: https://cyrilhenson.github.io/RegionalAR/ (privacy policy hosted here)
- **Privacy Policy URL**: https://cyrilhenson.github.io/RegionalAR/privacy-policy.html
- **Desktop App Download**: https://github.com/cyrilhenson/RegionalAR/releases
- **Meta Quest Store**: App submitted for review (RegionalAR)
- **Meta Developer Dashboard**: IAP add-on configured (SKU: `desktop_license`, Durable, $9.99)
- **Owner**: Laurence Henson (laurence.cyril.henson@gmail.com, GitHub: cyrilhenson)

---

## 3. Project File Structure

### Desktop App (Python) — Project Root
```
RegionalAR/
├── dicom_processor.py          # Main GUI app (tkinter) — license-gated, DICOM processing, WiFi server
├── license_manager.py          # Offline HMAC-SHA256 license validation
├── dicom_to_vol_cli.py         # CLI volume converter (no GUI)
├── requirements.txt            # Python dependencies
├── install_regionalar.bat      # Guided Windows installer
├── RegionalAR.bat              # Windows launcher
├── RegionalAR.spec             # PyInstaller build config → dist/RegionalAR.exe
├── build_exe.bat               # Build script (use `python -m PyInstaller RegionalAR.spec` instead)
├── index.html                  # Web interface for remote status
├── privacy-policy.html         # Privacy policy (also on GitHub Pages)
├── README.md                   # Full documentation with troubleshooting
├── LICENSING.md                # Internal license system documentation
├── MASTER_KEY.txt              # Developer master key (in .gitignore, NOT committed)
├── STORE_LISTING.md            # Meta Quest Store descriptions
├── META_STORE_CHECKLIST.md     # Store submission asset checklist
├── LICENSE                     # Proprietary license file
├── .gitignore                  # Excludes .exe, .vol, MASTER_KEY.txt, .regionalar_license, etc.
├── StoreAssets/                # Meta Quest Store submission images
│   ├── icon_512x512.png
│   ├── icon_1024x1024.png
│   ├── logo_1440x1440_transparent.png
│   ├── cover_landscape_2560x1440.png
│   ├── cover_square_1440x1440.png
│   ├── cover_portrait_1080x1920.png
│   ├── cover_mini_landscape_1080x360.png   # NOTE: 1080x360 for add-on (not 1200x675)
│   ├── hero_3000x900.png
│   ├── screenshot_1.png through screenshot_5.png   # 2560x1440 real Quest captures
│   └── trailer_cover_2560x1440.png
└── dist/
    └── RegionalAR.exe          # Standalone executable (~350 MB)
```

### Quest App (Unity/C#) — Assets/Scripts/
```
Assets/Scripts/
├── WiFiDownloader.cs           # CENTRAL SCRIPT: All UI panels, WiFi transfer, IAP, license management
├── VolumeRenderer.cs           # Volume rendering (3D texture, raymarching)
├── HandInteraction.cs          # Hand tracking grab/rotate/scale
├── BoneMeshLoader.cs           # Loads .bmsh bone meshes
├── VesselMeshLoader.cs         # Loads .vmsh vasculature meshes
├── NerveMeshLoader.cs          # Loads .nmsh nerve meshes
├── MuscleMeshLoader.cs         # Loads .omsh muscle meshes
├── LicenseKeyGenerator.cs      # Generates license keys (HMAC-SHA256, matches license_manager.py)
├── MarkerManager.cs            # AR marker/annotation system
├── PanelStabilizer.cs          # Keeps UI panels stable in AR
├── PerformanceManager.cs       # Performance optimization
└── SpatialAnchorStabilizer.cs  # Spatial anchoring for volume positioning

Assets/Shaders/
└── VolumeRaymarch.shader       # Volume raymarching shader with cut plane support
```

---

## 4. Key Technical Details

### WiFiDownloader.cs (The Central Script)
This is the most important file — it manages everything:
- **5 world-space canvases**: _ctrlCanvas (control panel), _wifiCanvas, _libCanvas, _licCanvas (license), _hintCanvas
- Each canvas has `CanvasScaler` with `dynamicPixelsPerUnit = 2.5f` for sharper text at VR distances
- **IAP flow**: `IAP.LaunchCheckoutFlow("desktop_license")` → OnCheckoutComplete → generates code + license key → shows in license panel
- **License panel** shows: user code, license key, download URL (`github.com/cyrilhenson/RegionalAR/releases`), and activation instructions
- **WiFi**: UDP broadcast listener on port 8766 for `BLOCKAR_SERVER:{ip}:{port}` discovery, HTTP download on port 8765
- **Sample volume**: Bundled Head & Neck CTA in StreamingAssets (`Head-Neck_CTA`)

### Wire Protocol (DO NOT RENAME)
- Desktop broadcasts `BLOCKAR_SERVER:{ip}:{port}` via UDP every 2 seconds on port 8766
- Quest listens for this broadcast to auto-discover the desktop server
- **The `BLOCKAR_SERVER` string is a wire protocol identifier used by both apps — changing it on one side breaks discovery**

### VolumeRaymarch.shader
- Cut plane support: cleanly discards clipped voxels (no slice cross-section rendering)
- Previously had a bug where slicing revealed blocky volume-rendered bone data — fixed by removing the slice rendering code entirely and just doing clean clipping
- The meshes (bone, vessel, nerve, muscle) provide the visual; the volume just needs to clip away

### License System
- **Quest side** (`LicenseKeyGenerator.cs`): Generates a random user code → HMAC-SHA256 with shared secret → formats as `RGNL-XXXX-XXXX-XXXX-XXXX`
- **Desktop side** (`license_manager.py`): Same HMAC validation, stores activation in `.regionalar_license` JSON file
- **Shared secret**: Embedded in both `LicenseKeyGenerator.cs` and `license_manager.py` (see source for value)
- **Master key**: `RGNL-1D92-1B9C-8C9D-614E` (for developer testing, stored in MASTER_KEY.txt, gitignored)

### OVOL Volume Format
```
Bytes  | Field          | Type
0-3    | Magic "OVOL"   | char[4]
4-7    | Version        | uint32
8-11   | Width          | uint32
12-15  | Height         | uint32
16-19  | Depth          | uint32
20-23  | DataType (0)   | uint32
24-27  | Reserved       | uint32
28-31  | Reserved       | uint32
32+    | Voxel data     | uint8[]
```

### Build Details
- **Quest APK**: Unity, ARM64-only, Bundle Version Code must increment for each upload (currently at 3+)
- **Desktop .exe**: PyInstaller via `python -m PyInstaller RegionalAR.spec`, outputs to `dist/RegionalAR.exe`
- **Python**: 3.11, dependencies in requirements.txt
- **pip/pyinstaller not on PATH**: Use `python -m pip` and `python -m PyInstaller` instead

---

## 5. Meta Quest Store Status

### Main App
- Submitted for review
- All VRC checks addressed
- Website URL: https://github.com/cyrilhenson/RegionalAR
- Privacy Policy: https://cyrilhenson.github.io/RegionalAR/privacy-policy.html

### IAP Add-On
- SKU: `desktop_license`
- Type: Durable
- Price: $9.99
- Application: RegionalAR
- Show in Store: Yes
- Assets uploaded (logo, icon, covers, hero, 5 screenshots)
- Data Use Checkup completed (User ID requested, data controller: Laurence Henson)

### Store Asset VRC Requirements (Lessons Learned)
- **VRC.Quest.Asset.2**: Cover art must have clear logo WITHOUT extraneous text, taglines, or banners
- **VRC.Quest.Asset.3**: No text in bleed areas (edges) of cover art
- **VRC.Quest.Asset.5**: Screenshots must be real in-app captures with NO added logos, text, or iconography
- **VRC.Quest.Asset.6**: No HMDs, controllers, or logos for other VR platforms in screenshots
- **VRC.Quest.Asset.7**: Optional trailer must be 16:9, under 2 minutes
- **Mini landscape for add-ons is 1080×360** (not 1200×675 which is for the main app)

---

## 6. All Changes Made (Chronological Summary)

1. **Opacity label style** — Matched to other menu options
2. **Removed Rename option** — From Quest app control panel
3. **MRI support** — Added MRI mode toggle, normalization, windowing, TotalSegmentator `total_mr` task
4. **OVOL v2 aspect ratio** — Computed `phys_extent_mm`, threaded through export, updated LoadOVOL (later reverted)
5. **Reverted OVOL v2 aspect ratio** — Caused issues, rolled back
6. **Removed auto-load on startup** — Only load when user selects a volume
7. **Dead code cleanup** — Removed unused code before alpha publish
8. **Bundled sample volume** — Head & Neck CTA via StreamingAssets
9. **Store assets v1** — Generated programmatically, rejected as too "janky"
10. **Store assets v2** — Redesigned with professional medical aesthetic (concentric ring icon, smooth gradients)
11. **Renamed BLOCKAR → REGIONALAR** — In Quest UI labels (kept `BLOCKAR_SERVER` wire protocol unchanged)
12. **Privacy policy** — Created and hosted on GitHub Pages
13. **XROrigin fix** — Removed to fix volume positioning, then fixed spatial anchor conflict
14. **Git repository** — Initialized, packaged desktop app, created LICENSE
15. **License system** — Added HMAC-SHA256 key generation (Quest) + validation (desktop)
16. **Cut plane fix** — Removed blocky slice cross-section rendering from VolumeRaymarch.shader
17. **Text sharpness** — Added CanvasScaler with dynamicPixelsPerUnit=2.5f to all 5 canvases
18. **APK version code** — Bumped for re-upload (must increment each time)
19. **Download URL in license panel** — Added `github.com/cyrilhenson/RegionalAR/releases` with instructions
20. **Store assets v3** — Stripped text from covers/hero for VRC compliance, processed real Quest screenshots
21. **Fixed mini landscape size** — 1080×360 for add-on (was 1200×675)
22. **Desktop app rename cleanup** — `install_blockar.bat` → `install_regionalar.bat` in log messages
23. **README + troubleshooting** — Updated with correct GitHub URL, added troubleshooting section
24. **Added MASTER_KEY.txt to .gitignore** — Prevent accidental commit of developer key
25. **GitHub release v1.1** — Published .exe and source code on github.com/cyrilhenson/RegionalAR/releases

---

## 7. Known Issues & Gotchas

- **Control panel text still slightly blurry at distance** — dynamicPixelsPerUnit=2.5f helps but doesn't fully resolve. User accepted current state.
- **pip/pyinstaller not on Windows PATH** — Always use `python -m pip` and `python -m PyInstaller`
- **Build version code** — Must increment for every Meta Store upload. Check current value in Unity Player Settings → Android → Other Settings.
- **BLOCKAR_SERVER protocol string** — Must match between desktop Python and Quest C#. Do NOT rename.
- **Store asset sizes differ between main app and add-on** — Mini landscape is 1080×360 for add-ons, different from main app requirements.
- **GitHub repo is under `cyrilhenson`** not `LaurenceHenson` — This was corrected in the license panel code.
- **.exe is ~350 MB** — Includes PyTorch and all dependencies. GitHub releases has a 2 GB limit so this is fine.

---

## 8. Pending / Future Work

- **Expand volume library** — Source additional DICOM datasets. Best options: Visible Human Project (public domain, full body), Open Anatomy Project (curated atlases). Avoid TCIA/OsiriX (academic-only, no commercial use).
- **Replace placeholder screenshots** — If Meta rejects the current screenshots, capture new ones showing different features (cut plane, WiFi transfer panel, etc.) without controllers visible.
- **Test full purchase flow end-to-end** — Purchase IAP → see license panel → download from GitHub → activate desktop app.
- **GitHub Pages** — Set up if not already done (Settings → Pages → Deploy from branch → main, root /).
- **Consider additional body regions** — Chest, abdomen, knee, etc. using open datasets.
- **Performance optimization** — Volume rendering on Quest could be optimized further for larger datasets.

---

## 9. Environment & Tools

- **Unity**: Quest 3 project with Oculus SDK
- **Python**: 3.11 on Windows, tkinter GUI
- **AI**: TotalSegmentator (nnU-Net v2 backend), PyTorch (CPU or CUDA)
- **Medical imaging**: pydicom, SimpleITK, numpy
- **Mesh processing**: scipy, scikit-image, trimesh, fast_simplification, blosc2
- **Build**: PyInstaller for .exe, Unity for .apk (ARM64 only)
- **Store**: Meta Developer Dashboard, GitHub Releases

---

*Generated from the full development history of RegionalAR. This document should be sufficient to resume development in a new Claude session.*
