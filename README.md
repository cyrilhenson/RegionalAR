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

## Quick Start

### Option A: Prebuilt Executable (Windows, no Python needed)

1. Download `RegionalAR.exe` from the [Releases](../../releases) page
2. Double-click to run
3. Browse to a DICOM folder, click **Process & Send**

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
RegionalAR-Desktop/
├── dicom_processor.py        # Main GUI app (tkinter)
├── dicom_to_vol_cli.py       # CLI converter (no GUI)
├── scan_vhp.py               # Visible Human Project scanner utility
├── rename_vhp_folders.py     # VHP folder renaming utility
├── requirements.txt          # Python dependencies
├── install_regionalar.bat    # Windows one-click installer
├── RegionalAR.bat            # Windows launcher
├── RegionalAR.spec           # PyInstaller build spec
└── RegionalAR.exe            # Prebuilt Windows executable
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
- **For CUDA acceleration:** NVIDIA GPU with CUDA 12.1+ support
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

## License

This project is provided as-is for research and educational use.

## Acknowledgments

- [TotalSegmentator](https://github.com/wasserth/TotalSegmentator) by Wasserthal et al. for AI anatomical segmentation
- [Meta Quest 3](https://www.meta.com/quest/) for the AR platform
- Built with [PyTorch](https://pytorch.org/), [SimpleITK](https://simpleitk.org/), and [nnU-Net](https://github.com/MIC-DKAA/nnUNet)
