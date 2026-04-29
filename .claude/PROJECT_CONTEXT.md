# RegionalAR — Project Context

## Overview
RegionalAR is a Meta Quest 3 AR app for DICOM medical image visualization focused on Regional Anesthesia. Two components:
1. **Python desktop app** — converts DICOM files to `.vol` binary (OVOL format), serves via HTTP with UDP auto-discovery
2. **Unity 2022 LTS Quest 3 app** — renders the volume as a 3D hologram in passthrough AR

## Tech Stack
- Unity 2022.3 LTS with Meta XR SDK v85
- Quest 3 passthrough AR (isInsightPassthroughEnabled, overlayType:1)
- Volume raymarching shader (CGPROGRAM, sampler3D + tex3Dlod)
- Android package: `com.DefaultCompany.RegionalAR`

## Key Files

### Unity Scripts (Assets/Scripts/)
- **VolumeRenderer.cs** — Loads .vol (OVOL) files, drives the VolumeRaymarch shader, manages tissue-layer presets + crop bounds + dual cut planes
- **HandInteraction.cs** — Grab/move/scale/rotate volume with Quest controllers, dual cut plane cycling (A button), proximity-gated grab
- **WiFiDownloader.cs** — Control panel UI (WorldSpace Canvas), WiFi download with UDP auto-discovery, ray-plane intersection for panel interaction, laser pointers, panel drag

### Shader (Assets/Shaders/)
- **VolumeRaymarch.shader** — Volume raymarching with 4 tissue layers, axis-aligned crop, 2 free-angle cut planes. Quest/Vulkan safe (constant loop bound 96, tex3Dlod, no gradient in loop)

### Other
- **deploy_to_quest.bat** — Build deploy script
- **dicom_processor.py / dicom_to_vol_cli.py** — Python DICOM converter + HTTP server + UDP broadcast
- **Assets/Scenes/SampleScene.unity** — Main scene
- **Assets/Materials/RegionalAR_Volume.mat** — Volume material

## Controller Mapping
| Button | Action |
|--------|--------|
| X (left) | Open/close control panel |
| Right Grip | Grab & move volume (proximity-gated) |
| Both Grips | Pinch to scale volume |
| Either Trigger | Click panel buttons |
| A (right) | Cycle cut planes: P1 active -> P2 active -> Both locked -> All off |
| Left Thumbstick | Move active cut plane in/out |
| Point at Panel + L Grip | Drag control panel |
| B (right) | Reset positions (volume + panel + cut planes) |

## Architecture Decisions

### UI Interaction: Ray-Plane Intersection (NOT Physics.Raycast)
Panel interaction uses mathematical ray-plane intersection, not Physics.Raycast + BoxColliders.
- `RayHitPanel()` does ray-plane intersection against the panel's plane in world space
- `FindHitRect()` checks canvas-pixel position against stored `HitRect` structs
- Previous approaches that FAILED: Physics.Raycast + BoxColliders (colliders at canvas scale 0.001 were 1-5mm thick, Quest's Physics.Raycast missed them), EventSystem.RaycastAll, direct canvas-local coordinate math
- `ProcessController()` handles one controller generically — both controllers use identical code path

### Dual Cut Planes
- Shader always runs both cut plane distance checks (no conditional branch — D=1.0 means off)
- A button cycles through 4 states: Plane 1 active -> Plane 2 active -> Both locked -> All off
- When plane is "locked", its local-space normal and dist are snapshotted so it survives volume grab/rotate/scale
- Normal converted via `Quaternion.Inverse(transform.rotation) * worldNormal` (not InverseTransformDirection, which misbehaves after scale changes)
- VolumeRenderer pushes both cut planes to material every frame via Update()
- Visual discs: Plane 1 = cyan, Plane 2 = orange

### WiFi Auto-Discovery (UDP Broadcast)
- Desktop server broadcasts `BLOCKAR_SERVER:{ip}:{port}` every 2 seconds on UDP port 8766
- Quest app listens on UDP port 8766 in a background thread
- When broadcast received, Quest auto-populates the server URL — no manual IP entry needed
- HTTP transfer on port 8765

### Volume Grab Proximity
- `IsNearVolume()` checks MeshRenderer.bounds with 20cm padding
- Only starts grab when controller is actually near the volume
- Once grabbing, movement continues even if hand moves away

### Panel/Hint/Lasers are ROOT Scene Objects
- Not children of Volume — prevents them from moving when volume is grabbed
- No DontDestroyOnLoad — was causing issues with WorldSpace Canvas rendering

### Controller Position
- WiFiDownloader uses OVRCameraRig hand anchors (leftHandAnchor/rightHandAnchor) for world position
- HandInteraction uses OVRInput.GetLocalControllerPosition + rig TransformPoint

## OVOL File Format
32-byte fixed binary header:
- 4 bytes: magic "OVOL"
- 4 bytes: version (uint32, must be 1)
- 4 bytes: width (uint32)
- 4 bytes: height (uint32)
- 4 bytes: depth (uint32)
- 4 bytes: dtype (uint32, 0 = uint8)
- 8 bytes: reserved
- Followed by W*H*D bytes of voxel data

## Tissue Layers (shader uniforms _L0.._L3)
| Layer | Density Range | Default Color | Default On |
|-------|--------------|---------------|------------|
| Bone | 0.55 - 1.00 | Off-white | Yes |
| Vasculature | 0.35 - 0.55 | Red | Yes |
| Nerves | 0.25 - 0.40 | Yellow | No |
| Soft Tissue | 0.10 - 0.35 | Salmon | Yes |

## Known Working
- Volume renders on Quest (test sphere visible)
- Passthrough AR active
- Right grip grabs volume (proximity-gated)
- Both grips scale volume
- B button resets volume + panel + cut plane
- Single cut plane toggles with A, moves with left stick, survives scale changes
- Panel buttons clickable with right trigger (confirmed by user)
- Laser pointers visible on both controllers with hit dots
- Control hints always visible

## Known Issues / In Progress
- Ray-plane panel interaction: rewritten, not yet tested on Quest
- Dual cut planes: code complete, not yet tested on Quest
- Left grip panel drag: ray-plane approach implemented, not yet tested on Quest
- WiFi auto-discovery: UDP broadcast + listener written, not tested end-to-end
- WiFi transfer: not yet tested end-to-end

## Session History
Multiple iterations on panel interaction — grip-based drag approaches consistently failed on Quest despite working code. Switched from Physics.Raycast + BoxColliders to ray-plane intersection math for all panel interaction (buttons, toggles, drag). Cut plane expanded from single to dual planes with A-button cycling and lock/anchor support.
