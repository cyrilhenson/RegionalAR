# RegionalAR Desktop

Desktop companion app for [RegionalAR](https://www.meta.com/experiences/) — a Meta Quest 3 AR application for viewing 3D medical volumes (CT/MRI) as holograms in augmented reality.

This desktop app processes DICOM scans into optimized volume files and wirelessly transfers them to the Quest headset. It includes AI-powered anatomical segmentation for automatic identification of bones, blood vessels, nerves, and muscles.

## Features

- **DICOM Processing** — Load CT or MRI DICOM folders and convert to optimized `.vol` format
- **AI Segmentation** — Automatic bone, vessel, nerve, and muscle identification using [TotalSegmentator](https://github.com/wasserth/TotalSegmentator)
- **Mesh Generation** — Exports companion mesh files (`.omsh`, `.vmsh`, `.nmsh`, `.mmsh`) for high-quality rendering
- **HD Mode** — Higher resolution processing with scipy/scikit-image
- **WiFi Transfer** — One-click wireless transfer to Quest 3 via local network
- **Auto-Discovery** — Quest app automatically finds the desktop server via UDP broadcast
- **MRI Support** — Automatic CT vs MRI detection with appropriate windowing and segmentation

## What's Included

The Quest app comes with a built-in **Head & Neck CTA** volume that you can view immediately — no desktop app or license needed. Just put on the headset and start exploring the 3D anatomy in AR.

The desktop companion app is an optional add-on for users who want to import their own DICOM scans (CT/MRI) with AI-powered segmentation.

## Activating the Desktop App

The desktop companion app requires a license key, which is generated from your Quest app.

1. Open **RegionalAR** on your Quest 3
2. Tap **LICENSE** on the control panel
3. You'll see a **code** and a **license key** displayed on screen
4. Open the desktop app on your computer
5. Enter the **code** as the Username and the **license key** in the License Key field
6. Click **Activate** — you only need to do this once

## Quick Start (Desktop)

### Option A: Prebuilt Executable (Windows, no Python needed)

1. Download `RegionalAR.exe` from the [Releases](https://github.com/cyrilhenson/RegionalAR/releases) page
2. Double-click to run
3. Activate with your license key (see above)
4. Browse to a DICOM folder, click **Process & Send**

> Note: The exe bundles all dependencies including PyTorch. AI models (~1.5 GB) download on first use.

### Option B: Run from Source (Windows/Mac/Linux)

1. Install [Python 3.10+](https://www.python.org/downloads/)
2. Install dependencies:

   **Windows (automated):**
   ```
   install_regionalar.bat
   ```

   **Manual:**
   ```bash
   pip install -r requirements.txt
   ```

   For PyTorch CPU-only (smaller download):
   ```bash
   pip install torch --index-url https://download.pytorch.org/whl/cpu
   ```

3. Run the app:
   ```bash
   python dicom_processor.py
   ```

## How It Works

1. **Load DICOM** — Select a folder containing DICOM files (.dcm) from a CT or MRI scan
2. **Process** — The app reads the DICOM series, normalizes the voxel data, and optionally runs AI segmentation
3. **Segment (AI)** — TotalSegmentator identifies anatomical structures and generates separate mesh files for each tissue type
4. **Transfer** — The processed volume is served over WiFi. The Quest 3 app auto-discovers the server and downloads the volume
5. **View in AR** — Put on the Quest 3 and interact with the 3D hologram using hand tracking

## File Structure

```
RegionalAR/
├── dicom_processor.py        # Main GUI app (tkinter) — license-gated
├── license_manager.py        # Offline license key validation
├── dicom_to_vol_cli.py       # CLI converter (no GUI)
├── requirements.txt          # Python dependencies
├── install_regionalar.bat    # Windows one-click installer
├── RegionalAR.bat            # Windows launcher
├── RegionalAR.spec           # PyInstaller build spec
├── Assets/Scripts/
│   ├── LicenseKeyGenerator.cs  # Quest-side license key generation
│   ├── WiFiDownloader.cs       # Control panel + license panel UI
│   ├── VolumeRenderer.cs       # Volume rendering
│   └── ...                     # Other Quest app scripts
└── LICENSING.md              # Internal license system docs
```

## Building the Executable

To rebuild the `.exe` from source:

```bash
pip install pyinstaller
pyinstaller RegionalAR.spec
```

The output will be in `dist/RegionalAR.exe`.

## System Requirements

- **Minimum:** Python 3.10, 8 GB RAM, 2 GB disk space
- **For AI Segmentation:** 16 GB RAM recommended, ~5 GB disk space (PyTorch + models)
- **GPU Acceleration:** Requires an **NVIDIA GPU with CUDA 12.1+** support. AI segmentation will automatically use CUDA when available for significantly faster processing (~1-2 min per scan). If no NVIDIA GPU is detected, processing falls back to **CPU mode**, which is fully functional but slower (~5-10 min per scan). AMD and Intel GPUs are not currently supported for acceleration.
- **Network:** Same WiFi network as Quest 3 for wireless transfer

## Output Format

The app produces `.vol` files in the OVOL format:

| Bytes | Field | Type |
|-------|-------|------|
| 0-3 | Magic `"OVOL"` | char[4] |
| 4-7 | Version | uint32 |
| 8-11 | Width | uint32 |
| 12-15 | Height | uint32 |
| 16-19 | Depth | uint32 |
| 20-23 | DataType (0=uint8) | uint32 |
| 24-27 | Reserved | uint32 |
| 28-31 | Reserved | uint32 |
| 32+ | Voxel data | uint8[] |

## Troubleshooting

**"No DICOM files found"**
Make sure you're selecting the folder that directly contains the `.dcm` files, not a parent folder. Some scanners nest files in subdirectories — browse deeper until you see the individual slice files.

**AI Segmentation not available**
TotalSegmentator requires PyTorch and downloads ~1.5 GB of model weights on first use. Run `install_regionalar.bat` and select the AI segmentation option, or install manually with `pip install torch totalsegmentator`.

**Quest not discovering the desktop app**
Both devices must be on the same Wi-Fi network. Check that your firewall isn't blocking UDP port 8766 or TCP port 8080, and that you're not on a guest/isolated network (some routers block device-to-device traffic). The desktop app should show "Server ready" in the log.

**Volume appears but looks wrong**
- *Too dark/bright* — Try toggling MRI mode if the scan is an MRI (the app auto-detects, but manual override is available)
- *Wrong orientation* — The app uses DICOM orientation tags. If these are missing or incorrect in the source scan, the volume may appear rotated
- *Missing tissue layers* — AI segmentation results depend on scan quality and body region

**License key not accepted**
Double-check that you're entering the code and key exactly as shown on the Quest (case-sensitive). The code goes in the Username field, the license key in the License Key field.

**Build fails with PyInstaller**
Run from inside the RegionalAR folder: `cd Desktop\RegionalAR`, then use `python -m PyInstaller RegionalAR.spec` instead of calling `pyinstaller` directly. If dependencies are missing, run `pip install -r requirements.txt` first.

## License

Proprietary software — all rights reserved. See [LICENSE](LICENSE) for details. A valid license key is required to use the desktop companion app.

## Acknowledgments & Citations

This project relies on the following open-source tools and research. If you use RegionalAR in academic work, please cite the relevant papers.

**AI Segmentation:**

- **TotalSegmentator** — Wasserthal, J., Breit, H.-C., Meyer, M.T., et al. *TotalSegmentator: Robust Segmentation of 104 Anatomic Structures in CT Images.* Radiology: Artificial Intelligence, 2023;5(5):e230024. DOI: [10.1148/ryai.230024](https://pubs.rsna.org/doi/10.1148/ryai.230024) | [GitHub](https://github.com/wasserth/TotalSegmentator)

- **nnU-Net** — Isensee, F., Jaeger, P.F., Kohl, S.A.A., Petersen, J., Maier-Hein, K.H. *nnU-Net: a self-configuring method for deep learning-based biomedical image segmentation.* Nature Methods, 2021;18:203–211. DOI: [10.1038/s41592-020-01008-z](https://doi.org/10.1038/s41592-020-01008-z) | [GitHub](https://github.com/MIC-DKAA/nnUNet)

**Core Libraries:**

- [PyTorch](https://pytorch.org/) — Deep learning framework (CUDA acceleration)
- [SimpleITK](https://simpleitk.org/) — Medical image I/O and resampling
- [scikit-image](https://scikit-image.org/) — Marching cubes mesh extraction
- [trimesh](https://trimesh.org/) — Mesh smoothing and decimation
- [SciPy](https://scipy.org/) — Frangi vesselness filter and morphological operations

**Platform:**

- [Meta Quest 3](https://www.meta.com/quest/) — AR headset platform
- [Oculus Platform SDK](https://developer.oculus.com/) — In-app purchase and entitlement
