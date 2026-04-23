"""
RegionalAR - DICOM to .vol converter (command-line, no GUI needed)
================================================================
Converts a folder of DICOM files into an OVOL binary file for
the RegionalAR Meta Quest 3 app.

Usage:
    python dicom_to_vol_cli.py
        (uses the default paths below - just double-click)

    python dicom_to_vol_cli.py --input "C:/path/to/dicom" --output "C:/path/to/out.vol"
        (custom paths)

Output format:
    Bytes  0- 3  : Magic "OVOL"
    Bytes  4- 7  : Version uint32 = 1
    Bytes  8-11  : Width   uint32
    Bytes 12-15  : Height  uint32
    Bytes 16-19  : Depth   uint32
    Bytes 20-23  : DataType uint32 = 2 (float32)
    Bytes 24-27  : NumPresets uint32 = 4
    Bytes 28-31  : Reserved uint32 = 0
    Bytes 32+    : float32 voxel array (X fast, Z slow)
"""

import sys
import os
import struct
import argparse
import numpy as np

# ── Default paths — edit these if needed ─────────────────────────────────────
DEFAULT_DICOM_FOLDER = r"C:\Users\Laurence\Downloads\Head-Neck CTA Dicom"
DEFAULT_OUTPUT_FILE  = r"C:\Users\Laurence\Desktop\head_neck.vol"
# ─────────────────────────────────────────────────────────────────────────────

TARGET_SIZE = 256   # resample to this cube (256³ ≈ 64 MB as float32)
HU_MIN      = -1024 # air
HU_MAX      =  3071 # dense cortical bone


def log(msg):
    print(msg, flush=True)


def check_deps():
    missing = []
    for pkg in ("pydicom", "SimpleITK", "numpy"):
        try:
            __import__(pkg if pkg != "SimpleITK" else "SimpleITK")
        except ImportError:
            missing.append(pkg)
    if missing:
        log(f"\n❌ Missing Python packages: {', '.join(missing)}")
        log("Install them by running:")
        log(f"    pip install {' '.join(missing)} --break-system-packages\n")
        sys.exit(1)


def find_dicom_series(folder):
    import SimpleITK as sitk
    reader = sitk.ImageSeriesReader()
    series_ids = reader.GetGDCMSeriesIDs(folder)
    if not series_ids:
        # Try walking sub-folders
        for root, dirs, files in os.walk(folder):
            ids = reader.GetGDCMSeriesIDs(root)
            if ids:
                log(f"  Found DICOM series in sub-folder: {root}")
                return root, ids[0]
        log(f"❌ No DICOM series found in: {folder}")
        sys.exit(1)
    log(f"  Found {len(series_ids)} series; using first one.")
    return folder, series_ids[0]


def load_volume(folder):
    import SimpleITK as sitk
    log("  Loading DICOM series …")
    search_folder, series_id = find_dicom_series(folder)
    reader = sitk.ImageSeriesReader()
    dicom_names = reader.GetGDCMSeriesFileNames(search_folder, series_id)
    log(f"  {len(dicom_names)} slices found.")
    reader.SetFileNames(dicom_names)
    image = reader.Execute()
    log(f"  Original size   : {image.GetSize()}")
    log(f"  Original spacing: {[f'{s:.2f}' for s in image.GetSpacing()]} mm")
    return image


def resample_isotropic(image, target_size):
    import SimpleITK as sitk
    orig_size    = np.array(image.GetSize(),    dtype=float)
    orig_spacing = np.array(image.GetSpacing(), dtype=float)
    orig_extent  = orig_size * orig_spacing          # mm

    # Fit into a cube while preserving aspect ratio
    max_extent = orig_extent.max()
    new_spacing_mm = max_extent / target_size
    new_size = np.round(orig_extent / new_spacing_mm).astype(int)
    new_size = np.minimum(new_size, target_size)     # cap each axis

    log(f"  Resampling to   : {new_size.tolist()} voxels (isotropic {new_spacing_mm:.2f} mm)")

    resample = sitk.ResampleImageFilter()
    resample.SetOutputSpacing([new_spacing_mm] * 3)
    resample.SetSize(new_size.tolist())
    resample.SetOutputDirection(image.GetDirection())
    resample.SetOutputOrigin(image.GetOrigin())
    resample.SetTransform(sitk.Transform())
    resample.SetDefaultPixelValue(-1024)
    resample.SetInterpolator(sitk.sitkLinear)
    return resample.Execute(image), new_size


def normalise_to_float32(image, new_size):
    import SimpleITK as sitk
    arr = sitk.GetArrayFromImage(image)          # shape: (Z, Y, X)
    arr = arr.astype(np.float32)
    arr = np.clip(arr, HU_MIN, HU_MAX)
    arr = (arr - HU_MIN) / (HU_MAX - HU_MIN)    # → [0, 1]

    # Pad to target_size³ so the Quest texture is always the same shape
    W, H, D = int(new_size[0]), int(new_size[1]), int(new_size[2])
    padded = np.zeros((TARGET_SIZE, TARGET_SIZE, TARGET_SIZE), dtype=np.float32)
    padded[:D, :H, :W] = arr[:D, :H, :W]        # arr is Z,Y,X; our cube is D,H,W same

    # Unity wants X fast, Z slow → transpose to (D, H, W) then reorder to (W, H, D)
    # SimpleITK gives us (Z, Y, X) = (D, H, W); Unity wants (X, Y, Z) = (W, H, D)
    out = np.transpose(padded, (2, 1, 0))        # (W, H, D) = (X, Y, Z)
    return out.flatten()


def write_ovol(voxels, output_path, width, height, depth):
    log(f"  Writing {len(voxels):,} voxels → {output_path}")
    os.makedirs(os.path.dirname(output_path), exist_ok=True) if os.path.dirname(output_path) else None
    with open(output_path, "wb") as f:
        # 32-byte header
        f.write(b"OVOL")                         # 4 bytes magic
        f.write(struct.pack("<I", 1))             # 4 bytes version
        f.write(struct.pack("<I", width))         # 4 bytes width
        f.write(struct.pack("<I", height))        # 4 bytes height
        f.write(struct.pack("<I", depth))         # 4 bytes depth
        f.write(struct.pack("<I", 2))             # 4 bytes dtype (2 = float32)
        f.write(struct.pack("<I", 4))             # 4 bytes numPresets
        f.write(struct.pack("<I", 0))             # 4 bytes reserved
        # Voxel data
        f.write(voxels.astype(np.float32).tobytes())
    size_mb = os.path.getsize(output_path) / 1024 / 1024
    log(f"  File size: {size_mb:.1f} MB")


def verify_ovol(path):
    with open(path, "rb") as f:
        magic   = f.read(4)
        version = struct.unpack("<I", f.read(4))[0]
        w       = struct.unpack("<I", f.read(4))[0]
        h       = struct.unpack("<I", f.read(4))[0]
        d       = struct.unpack("<I", f.read(4))[0]
        dtype   = struct.unpack("<I", f.read(4))[0]
    log(f"  Verify → magic={magic}, version={version}, "
        f"dims={w}×{h}×{d}, dtype={dtype}")
    assert magic == b"OVOL", "magic mismatch!"
    assert 0 < w <= 1024 and 0 < h <= 1024 and 0 < d <= 1024, "bad dims"
    assert dtype == 2, "dtype should be 2 (float32)"
    log("  ✅ File verified OK")


def main():
    parser = argparse.ArgumentParser(description="RegionalAR DICOM → OVOL converter")
    parser.add_argument("--input",  default=DEFAULT_DICOM_FOLDER,
                        help="Path to folder containing DICOM files")
    parser.add_argument("--output", default=DEFAULT_OUTPUT_FILE,
                        help="Output .vol file path")
    args = parser.parse_args()

    log("\n╔══════════════════════════════════════════╗")
    log("║   RegionalAR DICOM → .vol converter        ║")
    log("╚══════════════════════════════════════════╝\n")

    log("Checking dependencies …")
    check_deps()
    log("  ✅ All packages available\n")

    dicom_folder = args.input
    output_file  = args.output

    log(f"DICOM folder : {dicom_folder}")
    log(f"Output file  : {output_file}\n")

    if not os.path.isdir(dicom_folder):
        log(f"❌ DICOM folder not found: {dicom_folder}")
        log("Edit DEFAULT_DICOM_FOLDER at the top of this script, or use --input flag.")
        input("\nPress Enter to exit …")
        sys.exit(1)

    log("Step 1 / 4  Loading DICOM …")
    image = load_volume(dicom_folder)

    log("\nStep 2 / 4  Resampling to isotropic …")
    resampled, new_size = resample_isotropic(image, TARGET_SIZE)

    log("\nStep 3 / 4  Normalising HU values …")
    voxels = normalise_to_float32(resampled, new_size)
    log(f"  Value range: min={voxels.min():.4f}  max={voxels.max():.4f}")

    log("\nStep 4 / 4  Writing OVOL file …")
    # After padding + reshape the volume is always TARGET_SIZE³
    write_ovol(voxels, output_file, TARGET_SIZE, TARGET_SIZE, TARGET_SIZE)

    log("\nVerifying …")
    verify_ovol(output_file)

    log(f"\n🎉 Done!  File saved to:\n   {output_file}\n")
    log("Next step — run deploy_to_quest.bat (or run the adb command manually):\n")
    log(f'  adb push "{output_file}" '
        r'/sdcard/Android/data/com.DefaultCompany.RegionalAR/files/head_neck.vol')
    log("")

    input("Press Enter to exit …")


if __name__ == "__main__":
    main()
