"""
RegionalAR Desktop - DICOM Processor
====================================
One-click workflow: Browse a DICOM folder → Process & Send to Quest 3.
The Quest auto-discovers this server via UDP broadcast — no IP entry needed.

HOW TO RUN:
  python dicom_processor.py

REQUIREMENTS (install once):
  pip install pydicom numpy SimpleITK
"""

import os
import sys
import struct
import threading
import socket
import time
import tkinter as tk
from tkinter import ttk, filedialog, messagebox, scrolledtext
import http.server
import socketserver

# ── stdout/stderr safety net for PyInstaller --windowed builds ──
# When the exe is built with --windowed (no console attached), Python
# sets sys.stdout / sys.stderr to None. Any library that calls
# print(...) or sys.stdout.write(...) — and there are many in
# TotalSegmentator / nnU-Net / torch — will crash with
# "'NoneType' object has no attribute 'write'". This shim gives them
# something safe to write to.
class _NullStream:
    def write(self, *a, **k): return 0
    def flush(self, *a, **k): pass
    def isatty(self):          return False
    def writable(self):        return True
    def fileno(self):          raise OSError("no fileno on null stream")
if sys.stdout is None: sys.stdout = _NullStream()
if sys.stderr is None: sys.stderr = _NullStream()

import numpy as np
import pydicom
import SimpleITK as sitk

# ── Optional dependency detection ────────────────────────────────
_HAS_SCIPY = False
_HAS_SKIMAGE = False
_HAS_TOTALSEG = False

try:
    from scipy import ndimage
    from scipy.ndimage import distance_transform_edt
    _HAS_SCIPY = True
except ImportError:
    pass

try:
    from skimage import measure
    _HAS_SKIMAGE = True
except ImportError:
    pass

try:
    from totalsegmentator.python_api import totalsegmentator as _ts_api
    _HAS_TOTALSEG = True
except ImportError:
    pass

# Optional: TotalSegmentator's class-name map for the "total" and "total_mr"
# tasks. Lets us build bone/vessel masks by NAME instead of brittle label IDs.
_TS_CLASS_MAP_TOTAL = None
_TS_CLASS_MAP_MR    = None
try:
    from totalsegmentator.map_to_binary import class_map as _ts_class_map
    if isinstance(_ts_class_map, dict):
        if "total" in _ts_class_map:
            _TS_CLASS_MAP_TOTAL = _ts_class_map["total"]
        if "total_mr" in _ts_class_map:
            _TS_CLASS_MAP_MR = _ts_class_map["total_mr"]
except Exception:
    pass

# Substring patterns — any TotalSegmentator label whose name contains one
# of these tokens is treated as that anatomical category. This is version-
# agnostic (label IDs drift across TotalSegmentator releases, names don't).
_AI_BONE_NAME_TOKENS = (
    "vertebrae_", "rib_", "sternum", "clavicula", "scapula",
    "humerus", "femur", "hip_", "sacrum", "skull", "costal_cartilages",
    "patella", "tibia", "fibula", "radius", "ulna", "carpal",
)
_AI_MUSCLE_NAME_TOKENS = (
    # Discrete muscle groups
    "gluteus_maximus", "gluteus_medius", "gluteus_minimus",
    "autochthon", "iliopsoas",
    # Soft-tissue / muscular landmarks (head, neck, trunk)
    "esophagus", "trachea", "thyroid_gland",
    "heart",     # cardiac muscle — major mediastinal landmark
    "stomach",   # smooth muscle organ — abdominal landmark
    "urinary_bladder",  # pelvic landmark
)
_AI_VESSEL_NAME_TOKENS = (
    "aorta", "vena_cava", "iliac_artery", "iliac_vena", "iliac_vein",
    "portal_vein_and_splenic_vein", "pulmonary_vein", "pulmonary_artery",
    "brachiocephalic_trunk", "brachiocephalic_vein",
    "subclavian_artery", "subclavian_vein",
    "common_carotid_artery", "vertebral_artery",
    "atrial_appendage",   # part of great-vessel anatomy
)

def _ai_labels_matching(tokens, mri_mode=False):
    """Return the set of integer labels whose class name contains any of
    the given substrings. Empty set if the class map isn't available."""
    class_map = (_TS_CLASS_MAP_MR if mri_mode else _TS_CLASS_MAP_TOTAL)
    # Fall back to CT map if MRI map isn't available (labels overlap)
    if class_map is None:
        class_map = _TS_CLASS_MAP_TOTAL
    if class_map is None:
        return set()
    ids = set()
    for lbl_id, name in class_map.items():
        lname = str(name).lower()
        for tok in tokens:
            if tok in lname:
                ids.add(int(lbl_id))
                break
    return ids

# trimesh is optional — enables Taubin smoothing + quadric decimation.
# Without it we still emit a raw marching-cubes mesh (less pretty but valid).
_HAS_TRIMESH = False
try:
    import trimesh as _trimesh
    _HAS_TRIMESH = True
except ImportError:
    pass


def _check_hd_available():
    return _HAS_SCIPY and _HAS_SKIMAGE

def _check_ai_available():
    return _HAS_TOTALSEG


def _sanitize_scan_name(name, fallback="head_neck"):
    """Turn an arbitrary string (e.g. DICOM folder name) into a safe
    filename: keep alnum, hyphen, underscore; replace anything else with
    underscore; collapse multiple underscores; trim. Returns `fallback`
    if nothing usable remains."""
    if not name:
        return fallback
    out = []
    last_under = False
    for ch in str(name):
        if ch.isalnum() or ch in ("-", "_"):
            out.append(ch); last_under = (ch == "_")
        else:
            if not last_under:
                out.append("_"); last_under = True
    cleaned = "".join(out).strip("_-")
    return cleaned or fallback


# ─────────────────────────────────────────────────────────────────
#  DICOM LOADING
# ─────────────────────────────────────────────────────────────────

VOLUME_FILE_EXTS = ('.nrrd', '.nii', '.nii.gz', '.mha', '.mhd')

def _is_volume_file(path):
    """Check if a path points to a single-file volume (NRRD, NIfTI, etc.)."""
    low = path.lower()
    return any(low.endswith(ext) for ext in VOLUME_FILE_EXTS)


def load_volume_file(file_path, log):
    """Load a single-file volume (NRRD, NIfTI, MHA) via SimpleITK.

    Handles common pitfalls with Slicer-exported volumes:
      - Non-identity direction matrices → reorient to RAI (standard axial)
      - Unsigned int data without HU offset → apply -1024 shift
      - Unusual value ranges → log warnings for diagnosis
    """
    log(f"Loading volume file: {os.path.basename(file_path)}")
    sitk_image = sitk.ReadImage(file_path)

    # Log raw metadata for diagnostics
    pixel_type = sitk_image.GetPixelIDTypeAsString()
    direction  = sitk_image.GetDirection()
    origin     = sitk_image.GetOrigin()
    spacing    = sitk_image.GetSpacing()  # (x, y, z)
    log(f"  Pixel type: {pixel_type}")
    log(f"  Size: {sitk_image.GetSize()}, Spacing: {[f'{s:.3f}' for s in spacing]}mm")
    log(f"  Origin: {[f'{o:.1f}' for o in origin]}")

    # Check direction matrix — if not identity (or near-identity),
    # reorient to RAI so the volume axes match what the pipeline expects.
    is_identity = all(
        abs(direction[i*3+j] - (1.0 if i == j else 0.0)) < 0.01
        for i in range(3) for j in range(3))
    if not is_identity:
        log("  Direction matrix is non-identity — reorienting to RAI...")
        log(f"  Direction: [{', '.join(f'{d:.2f}' for d in direction)}]")
        try:
            sitk_image = sitk.DICOMOrient(sitk_image, 'RAI')
            log(f"  Reoriented. New size: {sitk_image.GetSize()}")
        except Exception as e:
            log(f"  Warning: DICOMOrient failed ({e}), using original orientation")

    volume = sitk.GetArrayFromImage(sitk_image).astype(np.float32)

    # Detect and fix unsigned data that hasn't been shifted to HU.
    # Proper CT HU data: air ≈ -1000, water = 0, bone ≈ +1000-3000.
    # Unsigned raw data: air ≈ 0, water ≈ 1024, bone ≈ 2000-4000.
    vmin, vmax = float(volume.min()), float(volume.max())
    log(f"  Raw value range: {vmin:.0f} to {vmax:.0f}")

    if vmin >= 0 and vmax > 2000:
        # Likely unsigned data — apply standard CT offset
        log("  Detected unsigned data (min >= 0) — applying -1024 HU offset")
        volume = volume - 1024.0
    elif vmin >= 0 and vmax <= 255:
        log("  WARNING: Data looks like 8-bit (0-255) — may be a label map, not CT!")
        log("  Make sure you exported the CT volume, not a segmentation.")
    elif vmin < -2000 or vmax > 5000:
        log(f"  WARNING: Unusual HU range ({vmin:.0f} to {vmax:.0f})")
        log("  Values outside typical CT range — check Slicer export settings.")

    log(f"Volume shape: {volume.shape}  HU range: {volume.min():.0f} to {volume.max():.0f}")
    return volume, spacing


def load_dicom_series(folder_path, log):
    dcm_files = sorted([
        os.path.join(folder_path, f)
        for f in os.listdir(folder_path)
        if f.lower().endswith('.dcm')
    ])
    if not dcm_files:
        raise ValueError(f"No .dcm files found in: {folder_path}")

    log(f"Found {len(dcm_files)} DICOM slices")

    reader    = sitk.ImageSeriesReader()
    series_ids = reader.GetGDCMSeriesIDs(folder_path)
    if series_ids:
        series_files = reader.GetGDCMSeriesFileNames(folder_path, series_ids[0])
        reader.SetFileNames(series_files)
    else:
        reader.SetFileNames(dcm_files)

    log("Reading slices... (this may take ~20 seconds)")
    sitk_image = reader.Execute()

    volume  = sitk.GetArrayFromImage(sitk_image).astype(np.float32)
    spacing = sitk_image.GetSpacing()                 # (x, y, z)

    first_dcm = pydicom.dcmread(dcm_files[0], stop_before_pixels=True)
    intercept = float(getattr(first_dcm, 'RescaleIntercept', 0))
    slope     = float(getattr(first_dcm, 'RescaleSlope',     1))

    if volume.min() > -200 and intercept != 0:
        volume = volume * slope + intercept

    log(f"Volume shape: {volume.shape}  HU range: {volume.min():.0f} to {volume.max():.0f}")
    return volume, spacing


# ─────────────────────────────────────────────────────────────────
#  HIGH DEFINITION PROCESSING
# ─────────────────────────────────────────────────────────────────

def _clahe_3d(volume, clip_limit=2.0, num_bins=256, log=None):
    """
    3D Contrast-Limited Adaptive Histogram Equalization.
    Applies CLAHE slice-by-slice along the axial plane, then averages
    with the original to avoid over-enhancement. This enhances local
    contrast within each tissue type without amplifying noise.
    """
    if log:
        log("Enhancement: Applying CLAHE (contrast enhancement)...")

    # Normalize to 0-1 for CLAHE
    vmin, vmax = volume.min(), volume.max()
    if vmax - vmin < 1e-6:
        return volume
    norm = (volume - vmin) / (vmax - vmin)

    enhanced = np.zeros_like(norm)
    for z in range(norm.shape[0]):
        sl = norm[z]
        # Per-slice histogram equalization with clipping
        hist, bin_edges = np.histogram(sl.ravel(), bins=num_bins, range=(0, 1))
        # Clip histogram and redistribute
        excess = np.sum(np.maximum(hist - clip_limit * sl.size / num_bins, 0))
        hist = np.minimum(hist, int(clip_limit * sl.size / num_bins))
        hist += int(excess / num_bins)
        cdf = np.cumsum(hist).astype(np.float64)
        cdf_min = cdf[cdf > 0].min() if np.any(cdf > 0) else 0
        cdf_max = cdf[-1]
        if cdf_max - cdf_min > 0:
            cdf = (cdf - cdf_min) / (cdf_max - cdf_min)
        indices = np.clip((sl * (num_bins - 1)).astype(int), 0, num_bins - 1)
        enhanced[z] = cdf[indices]

    # Blend 60% enhanced + 40% original to avoid over-enhancement
    blended = 0.6 * enhanced + 0.4 * norm
    result = blended * (vmax - vmin) + vmin

    if log:
        log("Enhancement: CLAHE complete")
    return result


def _anisotropic_diffusion(volume, niter=8, kappa=50, gamma=0.1, log=None):
    """
    Perona-Malik anisotropic diffusion — smooths homogeneous regions
    while preserving edges. Much better than Gaussian blur for anatomy
    because it respects tissue boundaries.

    Args:
        niter: number of iterations (more = smoother)
        kappa: edge sensitivity (higher = smoother edges)
        gamma: diffusion rate per step (0 < gamma <= 0.25 for stability)
    """
    if log:
        log(f"Enhancement: Anisotropic diffusion ({niter} iterations)...")

    vol = volume.astype(np.float64)
    for i in range(niter):
        # Compute gradients in all 6 directions (3D)
        # Forward differences
        dN = np.zeros_like(vol); dN[:-1] = vol[1:] - vol[:-1]
        dS = np.zeros_like(vol); dS[1:]  = vol[:-1] - vol[1:]
        dE = np.zeros_like(vol); dE[:, :-1] = vol[:, 1:] - vol[:, :-1]
        dW = np.zeros_like(vol); dW[:, 1:]  = vol[:, :-1] - vol[:, 1:]
        dU = np.zeros_like(vol); dU[:, :, :-1] = vol[:, :, 1:] - vol[:, :, :-1]
        dD = np.zeros_like(vol); dD[:, :, 1:]  = vol[:, :, :-1] - vol[:, :, 1:]

        # Perona-Malik edge-stopping function (exponential)
        cN = np.exp(-(dN / kappa) ** 2)
        cS = np.exp(-(dS / kappa) ** 2)
        cE = np.exp(-(dE / kappa) ** 2)
        cW = np.exp(-(dW / kappa) ** 2)
        cU = np.exp(-(dU / kappa) ** 2)
        cD = np.exp(-(dD / kappa) ** 2)

        vol += gamma * (cN * dN + cS * dS + cE * dE + cW * dW + cU * dU + cD * dD)

    if log:
        log("Enhancement: Anisotropic diffusion complete")
    return vol.astype(np.float32)


def hd_smooth_volume(volume, spacing, log):
    """
    High Definition preprocessing pipeline — runs on full-resolution DICOM
    BEFORE downsampling.

    Pipeline:
      1. CLAHE — per-slice adaptive contrast enhancement
      2. Anisotropic diffusion — edge-preserving denoising (better than Gaussian)
      3. Per-tissue SDF reconstruction with EXCLUSIVE label assignment
         Each voxel is assigned to exactly one tissue type (highest confidence
         wins), eliminating bone-in-vessel overlap artifacts.
    """
    if not _HAS_SCIPY:
        log("HD: scipy not installed — skipping. Run install_regionalar.bat to enable.")
        return volume

    vol = volume.astype(np.float32)

    # Step 1: CLAHE contrast enhancement
    vol = _clahe_3d(vol, clip_limit=2.0, log=log)

    # Step 2: Anisotropic diffusion (edge-preserving smoothing)
    vol = _anisotropic_diffusion(vol, niter=8, kappa=50, gamma=0.1, log=log)
    log(f"HD: Denoised range: {vol.min():.0f} to {vol.max():.0f}")

    # Step 3: Per-tissue SDF with exclusive assignment
    if _HAS_SKIMAGE and _HAS_SCIPY:

        # HU thresholds for tissue types (ordered by priority: bone > vessel > muscle)
        # Note: Nerves are only estimated in AI mode (they're invisible on CT
        # without AI landmark-based estimation). Non-AI path shows 3 tissue types.
        tissues = [
            ("Bone",         700, 2048, 1.5),  # HU 700+ = cortical bone
            ("Vasculature",  150,  400, 1.0),  # HU 150-400 = contrast vessels
            # Muscle layer removed — diffuse red haze was not clinically useful.
            # Non-AI path shows 2 tissue types (bone + vessels). Nerves require
            # AI landmark-based estimation and are only visible in AI mode.
        ]

        # Compute SDF confidence for all tissues first
        sdf_maps = []
        for name, hu_lo, hu_hi, weight in tissues:
            log(f"HD: Extracting {name} (HU {hu_lo}-{hu_hi})...")
            mask = (vol >= hu_lo) & (vol <= hu_hi)
            voxel_count = np.sum(mask)

            if voxel_count < 100:
                log(f"HD: {name} — too few voxels ({voxel_count}), skipping")
                sdf_maps.append(None)
                continue

            dist_in  = distance_transform_edt(mask)
            dist_out = distance_transform_edt(~mask)
            sdf = dist_in - dist_out
            # Sigmoid confidence — how strongly this voxel belongs to this tissue
            confidence = 1.0 / (1.0 + np.exp(-sdf * 1.5))
            sdf_maps.append((confidence, weight, name, voxel_count))

        # EXCLUSIVE ASSIGNMENT: each voxel goes to the tissue with highest
        # weighted confidence. This prevents bone bleeding into vessels.
        best_tissue = np.full(vol.shape, -1, dtype=np.int8)
        best_score  = np.full(vol.shape, -1.0, dtype=np.float32)

        for i, entry in enumerate(sdf_maps):
            if entry is None:
                continue
            confidence, weight, name, count = entry
            score = confidence * weight
            better = score > best_score
            best_tissue[better] = i
            best_score[better] = score[better]

        # Build result: each voxel gets its density modulated by its
        # winning tissue's smooth mask only
        result = np.copy(vol)
        for i, entry in enumerate(sdf_maps):
            if entry is None:
                continue
            confidence, weight, name, count = entry
            owned = best_tissue == i
            # Smooth the boundaries of owned regions
            result[owned] = vol[owned] * confidence[owned]
            log(f"HD: {name} — {np.sum(owned):,} voxels assigned")

        log("HD: Exclusive tissue assignment complete")
        return result
    else:
        log("HD: scikit-image not installed — using denoised volume only")
        return vol


def hd_ai_segment(volume, spacing, dicom_folder, log, mri_mode=False):
    """
    AI Segmentation using TotalSegmentator — labels each voxel with its
    anatomical structure (bone, artery, vein, muscle, nerve, etc.).
    Supports both CT (default) and MRI (mri_mode=True) via TotalSegmentator's
    total_mr task.

    Pipeline (preserves original HU accuracy):
      1. TotalSegmentator labels each voxel with one of 104 structures
      2. Body mask — remove everything outside the body
      3. Bone densification — fill spongy bone interiors
      4. Vessel filtering + tube morphology — clean vessels, remove capillaries
      5. Nerve estimation — use anatomical landmarks to estimate major nerve
         locations, remap to unique HU range (110-150) for shader separation
      6. Label-based assignment — use AI labels as ground truth (NO confidence
         multiplication that distorts HU values)
      7. Final body mask — absolute last step to prevent bleed

    NOTE: CLAHE and anisotropic diffusion are intentionally OMITTED here.
    They spread HU values across boundaries causing muscle bleed and HU
    distortion. The non-AI HD path uses them because it lacks AI labels
    for clean boundaries.

    Requires: pip install TotalSegmentator
    Also requires a ~1.5GB model download on first run.
    """
    if not _HAS_TOTALSEG:
        log("AI: TotalSegmentator not installed.")
        log("    Run install_regionalar.bat or click 'Install AI' in the app.")
        return None

    if not _HAS_SCIPY:
        log("AI: scipy required for AI smoothing but not installed.")
        return None

    try:
        import tempfile

        log("AI: Running TotalSegmentator (this may take 1-3 minutes)...")
        log("AI: First run downloads ~1.5 GB of models — please wait...")

        # TotalSegmentator works on NIfTI, so convert the DICOM-loaded
        # SimpleITK image to a temporary .nii.gz
        sitk_vol = sitk.GetImageFromArray(volume)
        sitk_vol.SetSpacing(spacing)

        with tempfile.TemporaryDirectory() as tmpdir:
            input_path = os.path.join(tmpdir, "input.nii.gz")
            output_path = os.path.join(tmpdir, "segmentation.nii.gz")
            sitk.WriteImage(sitk_vol, input_path)

            # Run segmentation in FULL mode (fast=False).
            #
            # Fast mode returns only ~37 structures and notably omits
            # spinal_cord, subclavian_artery, and several other labels our
            # nerve/plexus estimation depends on. Full mode returns the
            # complete 117-structure set used by the 'total' task.
            #
            # ml=True consolidates the output into a single multi-label
            # .nii.gz file at output_path; without it TotalSegmentator
            # writes a folder of individual binary masks which our
            # downstream loader can't read.
            #
            # Runtime: ~3-5 min on CPU (vs ~1 min fast). Worth it for
            # anatomical accuracy.
            #
            # MRI mode: use "total_mr" task (80 structures, any MRI sequence).
            # CT mode:  use default "total" task (117 structures).
            if mri_mode:
                log("AI: Using TotalSegmentator MRI model (total_mr task)")
                _ts_api(input_path, output_path, fast=False, ml=True,
                        task="total_mr")
            else:
                _ts_api(input_path, output_path, fast=False, ml=True)

            seg_arr = sitk.GetArrayFromImage(sitk.ReadImage(output_path))
            unique_labels = np.unique(seg_arr)
            n_structures = len(unique_labels) - (1 if 0 in unique_labels else 0)
            log(f"AI: Found {n_structures} anatomical structures")

            # ── Step 1: Body mask ──────────────────────────────────────
            log("AI: Creating body mask...")
            ai_body_mask = seg_arr > 0
            ai_body_mask = ndimage.binary_dilation(ai_body_mask, iterations=1)
            ai_coverage = np.sum(ai_body_mask) / ai_body_mask.size

            vol = volume.astype(np.float32)

            # If TotalSegmentator found very few structures (< 2% coverage),
            # the AI labels don't cover the body region well (e.g. lower
            # extremities, hands). Fall back to HU-based body mask so we
            # don't zero out actual anatomy.
            MIN_AI_COVERAGE = 0.02
            if ai_coverage < MIN_AI_COVERAGE:
                log(f"AI: Label coverage too sparse ({ai_coverage*100:.1f}% < "
                    f"{MIN_AI_COVERAGE*100:.0f}%) — using HU-based body mask")
                # Tissue is anything above air (-900 HU); fill holes to
                # capture bone marrow cavities and soft-tissue pockets
                body_mask = vol > -900.0
                body_mask = ndimage.binary_fill_holes(body_mask)
                body_mask = ndimage.binary_closing(body_mask, iterations=3)
                body_mask = ndimage.binary_dilation(body_mask, iterations=1)
            else:
                body_mask = ai_body_mask

            vol[~body_mask] = -1024.0
            log(f"AI: Body mask — {np.sum(body_mask):,} body, "
                f"{np.sum(~body_mask):,} background zeroed")

            # ── Step 2: Bone densification ─────────────────────────────
            log("AI: Densifying bone structures...")
            bone_mask = vol > 300
            bone_closed = ndimage.binary_closing(bone_mask, iterations=2)
            bone_boost = np.where(bone_closed & (vol > 200),
                                  np.clip(vol * 1.4 + 200, vol, 2048.0),
                                  vol)
            vol = np.where(bone_closed, bone_boost, vol)
            log(f"AI: Bone densified — {np.sum(bone_closed):,} voxels")

            # ── Step 3: Vessel filtering + tube morphology ─────────────
            log("AI: Filtering vessels and building tube morphology...")
            vessel_mask = (vol > 150) & (vol < 400) & body_mask
            large_vessel_mask = np.zeros_like(vessel_mask)

            if _HAS_SKIMAGE:
                from skimage import measure as sk_measure
                from skimage import morphology as sk_morph

                labeled_vessels = sk_measure.label(vessel_mask)
                props = sk_measure.regionprops(labeled_vessels)

                # Phase A: Remove capillaries (<8000 voxels)
                min_vessel_size = 8000
                small_vessel_mask = np.zeros_like(vessel_mask)
                removed_count = 0
                kept_count = 0
                for prop in props:
                    if prop.area < min_vessel_size:
                        small_vessel_mask[labeled_vessels == prop.label] = True
                        removed_count += 1
                    else:
                        large_vessel_mask[labeled_vessels == prop.label] = True
                        kept_count += 1
                vol[small_vessel_mask] = 40.0
                log(f"AI: Removed {removed_count} small vessel clusters, "
                    f"kept {kept_count} major vessels")

                # Phase B: Densify large vessels
                if np.any(large_vessel_mask):
                    boosted = np.clip(vol[large_vessel_mask] * 1.3 + 50, 200, 380)
                    vol[large_vessel_mask] = boosted
                    log(f"AI: Densified {np.sum(large_vessel_mask):,} vessel voxels")

                    # Phase C: Tube morphology
                    try:
                        selem = sk_morph.ball(2)
                        vessel_closed = ndimage.binary_closing(
                            large_vessel_mask, structure=selem, iterations=2)
                        vessel_closed = vessel_closed & body_mask
                        new_vv = vessel_closed & ~large_vessel_mask
                        vol[new_vv] = 280.0
                        large_vessel_mask = vessel_closed
                        log(f"AI: Tube closing added {np.sum(new_vv):,} fill voxels")
                    except Exception as e:
                        log(f"AI: Tube morphology skipped: {e}")

                    # Gaussian smooth vessel surfaces
                    vs = ndimage.gaussian_filter(vol * large_vessel_mask, sigma=1.5)
                    vn = ndimage.gaussian_filter(
                        large_vessel_mask.astype(np.float32), sigma=1.5)
                    vn = np.where(vn > 0.01, vn, 1.0)
                    vol = np.where(large_vessel_mask, vs / vn, vol)
                    log("AI: Vessel walls smoothed")
            else:
                log("AI: scikit-image not available, skipping vessel filtering")

            # ── Step 4: Nerve estimation from anatomical landmarks ─────
            # Nerves are nearly invisible on CT (~30 HU, same as muscle).
            # Instead of relying on HU thresholds, we use TotalSegmentator
            # labels to ESTIMATE where major nerves should be based on
            # known anatomical relationships:
            #   - Spinal cord: directly labeled (label 76)
            #   - Nerve roots: emerge from vertebral foramina (between
            #     vertebral bodies and spinal cord)
            #   - Brachial plexus: lateral to C4-T1 vertebrae
            #   - Major peripheral nerves: adjacent to known vessels
            #
            # All nerve voxels get remapped to HU 130 (center of the
            # dedicated nerve shader range 110-150, non-overlapping with
            # muscle range which ends at 110).
            log("AI: Estimating nerve locations from anatomical landmarks...")

            nerve_mask = np.zeros(vol.shape, dtype=bool)

            # TotalSegmentator label IDs for neural and related structures
            # (standard 104-label task). IDs may vary by version — we check
            # which ones are present.
            # Resolve labels by NAME via the class map — IDs drift between
            # TotalSegmentator versions, so hardcoded ints break silently.
            # _ai_labels_matching returns an empty set if the class map
            # isn't available, and we log both outcomes.
            _spinal_cord_ids = _ai_labels_matching(("spinal_cord",), mri_mode)
            _vertebrae_ids   = _ai_labels_matching(("vertebrae_",), mri_mode)
            log(f"AI: Label-map lookup — spinal_cord IDs: {sorted(_spinal_cord_ids)}, "
                f"vertebrae IDs: {len(_vertebrae_ids)} labels")
            SPINAL_CORD      = next(iter(_spinal_cord_ids), -1)  # -1 if not found
            VERTEBRAE_LABELS = _vertebrae_ids if _vertebrae_ids else set(range(16, 40))

            # 4a: Spinal cord (directly labeled)
            spinal_cord_mask = (
                np.isin(seg_arr, list(_spinal_cord_ids)) if _spinal_cord_ids
                else np.zeros_like(seg_arr, dtype=bool))
            if np.any(spinal_cord_mask):
                nerve_mask |= spinal_cord_mask
                log(f"AI: Spinal cord — {np.sum(spinal_cord_mask):,} voxels")

                # 4b: Nerve roots — tissue between spinal cord and vertebrae
                # Dilate spinal cord outward to find the zone where nerve
                # roots emerge (intervertebral foramina region)
                cord_dilated = ndimage.binary_dilation(spinal_cord_mask, iterations=8)

                # Find vertebral bone near the spinal cord
                vertebrae_mask = np.isin(seg_arr, list(VERTEBRAE_LABELS))
                if np.any(vertebrae_mask):
                    vert_dilated = ndimage.binary_dilation(vertebrae_mask, iterations=5)
                    # Nerve root zone: between cord and vertebrae, not bone, not vessel
                    root_zone = (cord_dilated & vert_dilated
                                 & ~bone_closed & ~large_vessel_mask
                                 & body_mask & ~spinal_cord_mask)
                    nerve_mask |= root_zone
                    log(f"AI: Nerve root zone — {np.sum(root_zone):,} voxels "
                        f"(between cord and vertebrae)")

                # 4c: Major nerve plexuses — extend from nerve roots along
                # known anatomical corridors. Brachial plexus emerges from
                # C5-T1 and extends 5-15 cm laterally; lumbar plexus from
                # L1-L4 runs through psoas. At typical CT resolution
                # (~1 mm voxels) 35 iterations reaches ~3.5 cm — enough to
                # capture plexus trunks + proximal divisions.
                if np.any(nerve_mask):
                    plexus_zone = ndimage.binary_dilation(nerve_mask, iterations=35)
                    plexus_zone = (plexus_zone & body_mask
                                   & ~bone_closed & ~large_vessel_mask)
                    nerve_mask |= plexus_zone
                    log(f"AI: Plexus estimation (35-iter dilation) — "
                        f"{np.sum(plexus_zone):,} additional voxels")

                # 4c-bis: Vessel-anchored plexus corridor.
                # Subclavian artery / brachiocephalic trunk run RIGHT
                # alongside brachial plexus cords; iliac arteries similarly
                # escort the lumbosacral plexus. A 10-iteration halo
                # around these named vessels catches the distal plexus.
                # Extensive diagnostic logging so we can see what
                # actually matched in the TotalSegmentator label map.
                try:
                    _plexus_vessel_tokens = (
                        "subclavian_artery", "subclavian_vein",
                        "brachiocephalic_trunk", "brachiocephalic_vein",
                        "common_carotid_artery",   # vagus nerve runs here
                        "iliac_artery", "iliac_vena", "iliac_vein",
                    )
                    _plexus_vessel_ids = _ai_labels_matching(_plexus_vessel_tokens, mri_mode)
                    log(f"AI: Plexus vessel-anchor label lookup → "
                        f"{len(_plexus_vessel_ids)} label IDs matched "
                        f"{_plexus_vessel_tokens}")
                    # Also dump the matched label names so we can confirm
                    # the expected vessels are actually being captured
                    _active_map = (_TS_CLASS_MAP_MR if mri_mode else _TS_CLASS_MAP_TOTAL) or _TS_CLASS_MAP_TOTAL
                    if _active_map is not None and _plexus_vessel_ids:
                        matched_names = sorted(
                            str(_active_map.get(lid, f"?{lid}"))
                            for lid in _plexus_vessel_ids)
                        log(f"AI:   matched label names: {matched_names}")
                    if _plexus_vessel_ids:
                        vess_anchor = np.isin(seg_arr, list(_plexus_vessel_ids))
                        n_anchor = int(np.sum(vess_anchor))
                        log(f"AI:   anchor-vessel voxels in this scan: {n_anchor:,}")
                        if n_anchor > 0:
                            vess_halo = ndimage.binary_dilation(vess_anchor,
                                                                iterations=10)
                            vess_halo = (vess_halo & body_mask
                                         & ~bone_closed & ~vess_anchor
                                         & ~large_vessel_mask)
                            n_before = int(np.sum(nerve_mask))
                            nerve_mask |= vess_halo
                            n_after = int(np.sum(nerve_mask))
                            log(f"AI: Vessel-anchored plexus corridor — "
                                f"added {n_after - n_before:,} voxels "
                                f"(10-iter halo around {n_anchor:,} anchor voxels)")
                        else:
                            log("AI:   anchor vessels labeled but EMPTY in this scan "
                                "— check scan coverage includes thoracic outlet")
                    else:
                        log("AI:   no vessel labels matched — class map may be "
                            "unavailable (see _TS_CLASS_MAP_TOTAL check above)")
                except Exception as e:
                    log(f"AI: Vessel-anchored plexus corridor skipped ({e})")
            else:
                log("AI: No spinal cord label found — nerve estimation limited")

            # 4d: Also check for nerves running alongside major vessels
            # (e.g., vagus nerve alongside carotid, phrenic alongside subclavian)
            if np.any(large_vessel_mask):
                vessel_adjacent = ndimage.binary_dilation(large_vessel_mask, iterations=2)
                vessel_adjacent = (vessel_adjacent & ~large_vessel_mask
                                   & ~bone_closed & body_mask)
                # Only a thin sheath around vessels — mark as potential nerve
                nerve_perivascular = vessel_adjacent & ~nerve_mask
                nerve_mask |= nerve_perivascular
                log(f"AI: Perivascular nerve estimate — "
                    f"{np.sum(nerve_perivascular):,} voxels")

            # Apply tube morphology and smoothing to nerve mask
            if _HAS_SKIMAGE and np.any(nerve_mask):
                from skimage import morphology as sk_morph
                try:
                    selem = sk_morph.ball(3)  # larger ball for thicker nerve trunks
                    nerve_closed = ndimage.binary_closing(
                        nerve_mask, structure=selem, iterations=3)
                    nerve_closed = nerve_closed & body_mask & ~bone_closed
                    nerve_mask = nerve_closed
                    log(f"AI: Nerve tube morphology — {np.sum(nerve_mask):,} total voxels")
                except Exception as e:
                    log(f"AI: Nerve tube morphology skipped: {e}")

            # Remap all nerve voxels to HU 130 (center of shader nerve range 110-150)
            # This puts them in a dedicated display band separate from muscle
            if np.any(nerve_mask):
                vol[nerve_mask] = 140.0  # upper end of nerve range for bright rendering
                # Smooth nerve region for clean rendering
                ns = ndimage.gaussian_filter(vol * nerve_mask, sigma=1.5)
                nn = ndimage.gaussian_filter(nerve_mask.astype(np.float32), sigma=1.5)
                nn = np.where(nn > 0.01, nn, 1.0)
                vol = np.where(nerve_mask, ns / nn, vol)
                log(f"AI: Remapped {np.sum(nerve_mask):,} nerve voxels to HU 140 "
                    f"(shader nerve range, bright)")
            else:
                log("AI: No nerve structures estimated")

            # ── Step 5: Label-based boundary smoothing ─────────────────
            # Use TotalSegmentator labels for clean boundaries between
            # structures. IMPORTANT: we do NOT multiply by SDF confidence
            # (that was distorting HU values at borders). Instead, we just
            # use the labels to assign ownership — each voxel keeps its
            # actual HU value.
            log("AI: Applying label-based boundary smoothing...")

            # Light Gaussian smooth at structure borders only (not globally)
            # to reduce jagged edges from the segmentation
            result = vol.copy()
            for label_id in unique_labels:
                if label_id == 0:
                    continue
                mask = seg_arr == label_id
                count = np.sum(mask)
                if count < 50:
                    continue

                # Smooth only at the boundary of each structure (2-voxel border)
                eroded = ndimage.binary_erosion(mask, iterations=2)
                border = mask & ~eroded
                if np.any(border):
                    # Light smooth at borders to reduce jagged edges
                    local_smooth = ndimage.gaussian_filter(vol * mask, sigma=1.0)
                    local_norm = ndimage.gaussian_filter(mask.astype(np.float32), sigma=1.0)
                    local_norm = np.where(local_norm > 0.01, local_norm, 1.0)
                    result[border] = local_smooth[border] / local_norm[border]

            log("AI: Border smoothing complete")

            # ── Step 6: Anatomical mask (absolute last step) ───────────
            # Only apply the strict anatomical mask when AI had good
            # coverage.  When coverage was sparse the strict mask would
            # erase most real anatomy.
            if ai_coverage >= MIN_AI_COVERAGE:
                strict_body = seg_arr > 0  # undilated — only actual labeled voxels
                anatomical_mask = strict_body | bone_closed | large_vessel_mask | nerve_mask
                result[~anatomical_mask] = -1024.0

                n_outside = np.sum(body_mask & ~anatomical_mask)
                log(f"AI: Anatomical mask applied — {n_outside:,} loose body-mask "
                    f"voxels removed (muscle bleed fix)")
            else:
                log("AI: Skipping strict anatomical mask (sparse AI coverage)")

            # ── Step 7: Build label-based bone/vessel masks for mesh export ─
            # Pure HU thresholds can't separate calcified vessel walls from
            # bone, or cancellous bone from contrast vessels. TotalSegmentator
            # labels know exactly what each voxel IS, so we use them directly
            # for mesh extraction and avoid every HU-overlap pitfall.
            bone_ids   = _ai_labels_matching(_AI_BONE_NAME_TOKENS, mri_mode)
            vessel_ids = _ai_labels_matching(_AI_VESSEL_NAME_TOKENS, mri_mode)
            muscle_ids = _ai_labels_matching(_AI_MUSCLE_NAME_TOKENS, mri_mode)
            ai_bone_mask   = None
            ai_vessel_mask = None
            ai_muscle_mask = None
            if bone_ids:
                ai_bone_mask = np.isin(seg_arr, list(bone_ids))
                log(f"AI: Bone mask from labels — "
                    f"{len(bone_ids)} bone structures, "
                    f"{int(np.sum(ai_bone_mask)):,} voxels")
            else:
                log("AI: No bone labels found in class map — mesh will fall back to HU")
            if vessel_ids:
                ai_vessel_mask = np.isin(seg_arr, list(vessel_ids))
                log(f"AI: Vessel mask from labels — "
                    f"{len(vessel_ids)} vessel structures, "
                    f"{int(np.sum(ai_vessel_mask)):,} voxels")
            else:
                log("AI: No vessel labels found in class map — mesh will fall back to HU")
            if muscle_ids:
                ai_muscle_mask = np.isin(seg_arr, list(muscle_ids))
                log(f"AI: Muscle mask from labels — "
                    f"{len(muscle_ids)} muscle groups, "
                    f"{int(np.sum(ai_muscle_mask)):,} voxels")
            else:
                log("AI: No muscle labels found in class map — muscle mesh skipped")

            # Also return the nerve mask — it's the same one hd_ai_segment
            # already built from anatomical landmarks (spinal cord + root
            # zones + plexus estimation). Meshing it gives Phase 3 nerves.
            log("AI: Processing complete")

            # When AI coverage was sparse, don't trust the label-based
            # bone/vessel masks — let mesh extraction fall back to HU.
            sparse = ai_coverage < MIN_AI_COVERAGE
            if sparse:
                log("AI: Sparse coverage — bone/vessel/nerve/muscle masks cleared "
                    "so mesh extraction uses HU fallback")

            return {
                "volume":      result,
                "bone_mask":   None if sparse else ai_bone_mask,
                "vessel_mask": None if sparse else ai_vessel_mask,
                "nerve_mask":  None if sparse else (
                    nerve_mask if (nerve_mask is not None and
                                  int(np.sum(nerve_mask)) > 0) else None),
                "muscle_mask": None if sparse else ai_muscle_mask,
            }

    except Exception as e:
        log("")
        log("!!" + "=" * 60)
        log(f"!! AI MODE FAILED: {e}")
        log("!! Falling back to HD processing (HU thresholds only).")
        log("!! Bone/vessel meshes will use HU thresholds and may have")
        log("!! false positives (spongy bone, calcified walls). To get")
        log("!! label-based masks, rebuild the desktop exe with:")
        log("!!   pip install blosc2 pandas totalsegmentator")
        log("!!   release_desktop.bat")
        log("!!" + "=" * 60)
        log("")
        return None


# ─────────────────────────────────────────────────────────────────
#  PROCESSING
# ─────────────────────────────────────────────────────────────────

QUEST_TARGET_BASE = 128   # 128³ =  2M voxels — standard mode (noisy data)
QUEST_TARGET_AI   = 256   # 256³ = 16M voxels — AI mode (clean data, 8x detail)

def process_volume(volume, spacing, log, hd_mode=False, ai_segment=False,
                   dicom_folder=None, mesh_output_path=None, mri_mode=False):
    # ── MRI vs CT value normalization ──
    # CT uses fixed Hounsfield Units; MRI uses percentile-based normalization
    # because signal intensity varies by sequence and scanner.
    if mri_mode:
        log("MRI Mode: using percentile-based normalization")
        p_low  = np.percentile(volume, 0.5)
        p_high = np.percentile(volume, 99.5)
        log(f"  MRI intensity range: {volume.min():.0f} to {volume.max():.0f}")
        log(f"  Clipping to 0.5th-99.5th percentile: {p_low:.0f} to {p_high:.0f}")
        vol = np.clip(volume, p_low, p_high)
        # Remap to pseudo-HU range so the rest of the pipeline
        # (resampling, padding, uint8 export) works unchanged.
        # Map: p_low → -1024, p_high → 2048
        HU_MIN, HU_MAX = -1024.0, 2048.0
        if p_high > p_low:
            vol = (vol - p_low) / (p_high - p_low) * (HU_MAX - HU_MIN) + HU_MIN
        else:
            vol = np.full_like(vol, HU_MIN)
    else:
        HU_MIN, HU_MAX = -1024.0, 2048.0
        vol = np.clip(volume, HU_MIN, HU_MAX)

    # ── HD / AI preprocessing (runs on full-res volume BEFORE downsampling) ──
    used_ai = False
    ai_bone_mask   = None       # carried into mesh extraction if AI ran
    ai_vessel_mask = None
    ai_nerve_mask  = None
    ai_muscle_mask = None
    if ai_segment and dicom_folder:
        ai_result = hd_ai_segment(vol, spacing, dicom_folder, log,
                                  mri_mode=mri_mode)
        if isinstance(ai_result, dict):
            vol = ai_result["volume"]
            ai_bone_mask   = ai_result.get("bone_mask")
            ai_vessel_mask = ai_result.get("vessel_mask")
            ai_nerve_mask  = ai_result.get("nerve_mask")
            ai_muscle_mask = ai_result.get("muscle_mask")
            used_ai = True
        elif ai_result is not None:
            # Backward compat if a caller returns a bare array
            vol = ai_result
            used_ai = True
        elif hd_mode:
            vol = hd_smooth_volume(vol, spacing, log)
    elif hd_mode:
        vol = hd_smooth_volume(vol, spacing, log)

    # AI-processed volumes are clean enough for 256³ (8x more detail).
    # Raw/HD volumes stay at 128³ to keep noisy data from tanking GPU.
    target = QUEST_TARGET_AI if used_ai else QUEST_TARGET_BASE
    target_mb = (target ** 3) / (1024 * 1024)
    log(f"Target resolution: {target}³ ({target_mb:.1f} MB) "
        f"{'[AI enhanced]' if used_ai else '[standard]'}")

    # Resample all three axes to target³ for Quest 3 GPU budget
    sitk_vol = sitk.GetImageFromArray(vol)
    sitk_vol.SetSpacing((spacing[0], spacing[1], spacing[2]))
    orig_sz = sitk_vol.GetSize()    # (X, Y, Z) in SimpleITK
    orig_sp = sitk_vol.GetSpacing()

    needs_resample = any(s > target for s in orig_sz)
    if needs_resample:
        # Compute new isotropic spacing to fit target cube
        max_phys = max(orig_sz[i] * orig_sp[i] for i in range(3))
        new_sp = max_phys / target
        new_sz = [min(target, int(round(orig_sz[i] * orig_sp[i] / new_sp))) for i in range(3)]
        # Ensure at least 1 and at most target in each dim
        new_sz = [max(1, min(target, s)) for s in new_sz]

        log(f"Resampling {orig_sz} -> {tuple(new_sz)} for Quest 3 GPU ({target}³ target)")
        rs = sitk.ResampleImageFilter()
        rs.SetSize(new_sz)
        rs.SetOutputSpacing([new_sp] * 3)
        rs.SetOutputOrigin(sitk_vol.GetOrigin())
        rs.SetOutputDirection(sitk_vol.GetDirection())
        rs.SetInterpolator(sitk.sitkLinear)
        rs.SetDefaultPixelValue(float(HU_MIN))
        vol = sitk.GetArrayFromImage(rs.Execute(sitk_vol))
    else:
        log(f"Volume already small enough: {orig_sz}")

    # Resample the AI bone/vessel masks onto the same 256³ cube using
    # nearest-neighbor so label boundaries stay crisp (no partial-voxel
    # bleeding). Done BEFORE padding so we share the same center offsets.
    def _resample_mask_to_cube(m):
        if m is None: return None
        sitk_m = sitk.GetImageFromArray(m.astype(np.uint8))
        sitk_m.SetSpacing((spacing[0], spacing[1], spacing[2]))
        if needs_resample:
            rs_m = sitk.ResampleImageFilter()
            rs_m.SetSize(new_sz)
            rs_m.SetOutputSpacing([new_sp] * 3)
            rs_m.SetOutputOrigin(sitk_m.GetOrigin())
            rs_m.SetOutputDirection(sitk_m.GetDirection())
            rs_m.SetInterpolator(sitk.sitkNearestNeighbor)
            rs_m.SetDefaultPixelValue(0)
            arr = sitk.GetArrayFromImage(rs_m.Execute(sitk_m))
        else:
            arr = sitk.GetArrayFromImage(sitk_m)
        return arr.astype(bool)

    ai_bone_mask_cube   = _resample_mask_to_cube(ai_bone_mask)   if used_ai else None
    ai_vessel_mask_cube = _resample_mask_to_cube(ai_vessel_mask) if used_ai else None
    ai_nerve_mask_cube  = _resample_mask_to_cube(ai_nerve_mask)  if used_ai else None
    ai_muscle_mask_cube = _resample_mask_to_cube(ai_muscle_mask) if used_ai else None

    # Pad to exact cube if needed
    D, H, W = vol.shape
    if D != target or H != target or W != target:
        padded = np.full((target, target, target),
                         HU_MIN, dtype=vol.dtype)
        d, h, w = min(D, target), min(H, target), min(W, target)
        od, oh, ow = (target - d) // 2, (target - h) // 2, (target - w) // 2
        padded[od:od+d, oh:oh+h, ow:ow+w] = vol[:d, :h, :w]
        vol = padded
        log(f"Padded to {target}³ cube")

        # Pad the masks identically so they share the volume's frame
        def _pad_mask_to_cube(m):
            if m is None: return None
            p = np.zeros((target, target, target), dtype=bool)
            md, mh, mw = m.shape
            ld, lh, lw = min(md, target), min(mh, target), min(mw, target)
            od_, oh_, ow_ = (target - ld) // 2, (target - lh) // 2, (target - lw) // 2
            p[od_:od_+ld, oh_:oh_+lh, ow_:ow_+lw] = m[:ld, :lh, :lw]
            return p
        ai_bone_mask_cube   = _pad_mask_to_cube(ai_bone_mask_cube)
        ai_vessel_mask_cube = _pad_mask_to_cube(ai_vessel_mask_cube)
        ai_nerve_mask_cube  = _pad_mask_to_cube(ai_nerve_mask_cube)
        ai_muscle_mask_cube = _pad_mask_to_cube(ai_muscle_mask_cube)

    # ── Mesh extraction (desktop-side, Phase 1 + 2 of mesh rendering) ──
    # Uses the full-precision HU cube BEFORE uint8 quantization. Runs in
    # ALL processing modes (raw / HD / AI). When AI label masks exist they
    # take priority over HU thresholding — AI knows what each voxel IS, so
    # calcified vessels stay vessels and spongy-bone HU stays bone.
    if mesh_output_path and _HAS_SKIMAGE:
        log(f"MESH: Starting mesh extraction "
            f"(AI={used_ai}, trimesh={_HAS_TRIMESH}, "
            f"ai_bone_mask={ai_bone_mask_cube is not None}, "
            f"ai_vessel_mask={ai_vessel_mask_cube is not None}, "
            f"ai_nerve_mask={ai_nerve_mask_cube is not None}, "
            f"ai_muscle_mask={ai_muscle_mask_cube is not None})...")
        extract_bone_mesh(vol, target, mesh_output_path, log,
                          ai_mask=ai_bone_mask_cube, mri_mode=mri_mode)
        extract_vessel_mesh(vol, target, mesh_output_path, log,
                            ai_mask=ai_vessel_mask_cube, mri_mode=mri_mode)
        extract_nerve_mesh(vol, target, mesh_output_path, log,
                           ai_mask=ai_nerve_mask_cube, mri_mode=mri_mode)
        extract_muscle_mesh(vol, target, mesh_output_path, log,
                            ai_mask=ai_muscle_mask_cube, mri_mode=mri_mode)
    elif mesh_output_path and not _HAS_SKIMAGE:
        log("MESH: scikit-image not installed — mesh extraction skipped.")

    log("Normalising to 8-bit uint8")
    vol_norm = (vol - HU_MIN) / (HU_MAX - HU_MIN)
    processed = (np.clip(vol_norm, 0.0, 1.0) * 255).astype(np.uint8)
    log(f"Final shape: {processed.shape}  (D x H x W = Z x Y x X)")
    return processed


# ─────────────────────────────────────────────────────────────────
#  BONE MESH EXTRACTION  (Phase 1 of mesh-based rendering)
#
#  Philosophy: do every heavy step HERE, on the desktop. The Quest
#  just receives a ready-to-upload binary mesh and draws it as solid
#  geometry — no voxel raymarching of the bone channel required.
#
#  Pipeline:
#    1. Threshold the AI-processed cube volume at the bone HU level.
#    2. Gaussian pre-smooth the mask (reduces MC staircasing).
#    3. Marching cubes → raw mesh (potentially 500k-1M tris).
#    4. Taubin smoothing (if trimesh present) — shrinkage-free.
#    5. Quadric decimation to ~150k tris.
#    6. Export as .omsh binary file next to the .vol.
#
#  .omsh binary format (little-endian):
#    [ 4B ] magic        = "OMSH"
#    [ 4B ] version      = 1 (uint32)
#    [ 4B ] num_verts    (uint32)
#    [ 4B ] num_tris     (uint32)
#    [ num_verts * 12B ] vertex positions (float32 xyz)
#    [ num_verts * 12B ] vertex normals   (float32 xyz)
#    [ num_tris  * 12B ] triangle indices (uint32  abc)
#
#  Coordinates are normalized to the same [-0.5, 0.5]³ cube the
#  volume renderer uses, so the mesh drops straight in alongside
#  the volume with no extra transform needed.
# ─────────────────────────────────────────────────────────────────

# Target decimated triangle budget. 150k is comfortable on Quest 3
# (Snapdragon XR2 Gen 2 handles 250-400k tris at 72Hz with room
# for the volume raymarch alongside).
_MESH_TARGET_TRIS   = 150_000

# Higher HU floor than the volume-rendering bone threshold (was 300 HU).
# Contrast-enhanced arteries routinely hit 200-400 HU and were being picked
# up as bone. 400 HU is a safer "this is definitely cortex, not vessel"
# cutoff while still capturing spongy vertebral body bone.
_MESH_BONE_HU_LEVEL   = 400.0

# Connected-component filter: drop bone clusters smaller than this many voxels.
# At 256³ with typical head/neck FOV a rib is ~500-1500 voxels and a clavicle
# ~500-800 voxels, so the threshold must stay well below that. 300 catches
# stray vessel fragments while preserving every thin bone.
_MESH_MIN_COMPONENT_VOX = 300

# ── Vessel mesh extraction parameters (Phase 2) ─────────────
# Contrast-enhanced arteries sit around 150-400 HU; we capture the whole
# range. Below 150 bleeds into muscle, above 400 bleeds into bone.
_VMESH_HU_MIN = 150.0
_VMESH_HU_MAX = 400.0
# Vessels are much thinner than bone — lower CC threshold preserves
# smaller branches. 150 voxels ≈ a 3 × 3 × 16 vessel segment at 256³.
_VMESH_MIN_COMPONENT_VOX = 150
# Separate triangle budget — vessels are tubular and detail-heavy so
# they're allowed a bit more of the Quest's triangle budget.
_VMESH_TARGET_TRIS = 120_000


def extract_bone_mesh(cube_volume_float, target_cube_size, output_path, log,
                      ai_mask=None, mri_mode=False):
    """Extract a bone surface mesh and save as .omsh next to output_path.

    ai_mask: optional bool array (D,H,W) = anatomical bone mask from AI
             segmentation. When provided, it REPLACES HU thresholding —
             meshing follows anatomical labels exactly and avoids every
             HU-overlap problem (calcified vessels, spongy bone, etc.).
    """
    if not _HAS_SKIMAGE:
        log("MESH: scikit-image missing — bone mesh extraction skipped.")
        return None

    try:
        log("MESH: Building bone mask...")
        vol_for_mc = cube_volume_float
        if _HAS_SCIPY:
            # Higher sigma = smoother input = smoother mesh output.
            # Was 0.8; 1.3 produces noticeably matte/rounded cortex
            # without losing anatomical detail.
            vol_for_mc = ndimage.gaussian_filter(vol_for_mc, sigma=1.3)

        if ai_mask is not None and int(np.sum(ai_mask)) > 5000:
            raw_mask = ai_mask
            n_raw = int(np.sum(raw_mask))
            log(f"MESH: Using AI anatomical bone mask: {n_raw:,} voxels")
        elif mri_mode:
            # MRI: HU thresholds are meaningless — if AI didn't find bone, skip
            if ai_mask is not None:
                log("MESH: AI bone mask too small for MRI — skipping (no HU fallback)")
            else:
                log("MESH: No AI bone mask in MRI mode — skipping")
            return None
        else:
            if ai_mask is not None:
                log("MESH: AI bone mask too small — falling back to HU threshold")
            raw_mask = vol_for_mc >= _MESH_BONE_HU_LEVEL
            n_raw = int(np.sum(raw_mask))
            if n_raw < 5000:
                log(f"MESH: only {n_raw} bone-level voxels — skipping mesh export.")
                return None
            log(f"MESH: Raw bone-HU voxels: {n_raw:,}")

        # Step 1b: connected-component cleanup only — no morphological
        # opening. Opening with ball(1) would erase ribs and clavicles
        # (those are often only 2 voxels thick). We rely on the HU floor
        # of 400 to filter most vessel voxels, and the CC filter below
        # to drop any remaining isolated vessel blobs.
        if _HAS_SKIMAGE:
            try:
                from skimage import measure as sk_measure
                labeled = sk_measure.label(raw_mask, connectivity=1)
                props   = sk_measure.regionprops(labeled)
                keep_mask = np.zeros_like(raw_mask)
                kept = dropped = 0
                largest = 0
                for p in props:
                    if p.area >= _MESH_MIN_COMPONENT_VOX:
                        keep_mask[labeled == p.label] = True
                        kept += 1
                        if p.area > largest: largest = p.area
                    else:
                        dropped += 1
                log(f"MESH: Kept {kept} components (largest {largest:,} vox), "
                    f"dropped {dropped} small (<{_MESH_MIN_COMPONENT_VOX} vox)")
                clean_mask = keep_mask
            except Exception as e:
                log(f"MESH: CC cleanup skipped ({e}) — using raw threshold")
                clean_mask = raw_mask
        else:
            clean_mask = raw_mask

        # Build a scalar field for marching cubes where the level surface
        # of +0.5 sits right at the cleaned mask's boundary. Using the mask
        # directly means MC sees a clean binary volume, not the messy HU
        # cube, so the surface follows the filtered bone exactly.
        mc_field = clean_mask.astype(np.float32)
        # Light Gaussian on the binary mask creates smooth level-set
        # crossings for MC — avoids the blocky "voxel art" look.
        if _HAS_SCIPY:
            mc_field = ndimage.gaussian_filter(mc_field, sigma=0.8)

        # Step 2: marching cubes on the cleaned binary-boundary field.
        # Level=0.5 sits exactly at the (smoothed) mask edge, so the mesh
        # follows our filtered bone shape not the raw noisy HU cube.
        verts_zyx, faces, mc_normals, _ = measure.marching_cubes(
            mc_field,
            level=0.5,
            allow_degenerate=False,
            step_size=1,
        )
        log(f"MESH: Marching cubes → {len(verts_zyx):,} verts, {len(faces):,} tris")

        # Step 3: normalize coords to [-0.5, 0.5]³ cube matching the
        # Unity volume bounds. Flip Z→X so axis handedness matches the
        # 3D texture sampling order used by the raymarch shader.
        T = float(target_cube_size)
        verts = np.stack([
            verts_zyx[:, 2] / T - 0.5,   # Unity X ← array W (col)
            verts_zyx[:, 1] / T - 0.5,   # Unity Y ← array H (row)
            verts_zyx[:, 0] / T - 0.5,   # Unity Z ← array D (slice)
        ], axis=1).astype(np.float32)

        # Swapping axes flipped handedness → reverse winding so front
        # faces point outward in Unity (CCW = front by default).
        faces = np.stack([faces[:, 0], faces[:, 2], faces[:, 1]], axis=1)

        # Step 4 + 5: Taubin smoothing + decimation (trimesh if available)
        if _HAS_TRIMESH:
            mesh = _trimesh.Trimesh(vertices=verts, faces=faces, process=False)

            # Taubin smoothing — prevents shrinkage, gives the matte
            # stone-like bone surface commercial apps render.
            # 25 iterations produces visibly rounder cortex than the
            # earlier 10; still no shrinkage because lamb/nu are balanced.
            try:
                _trimesh.smoothing.filter_taubin(mesh, lamb=0.5, nu=-0.53,
                                                 iterations=25)
                log("MESH: Taubin smoothing applied (25 iterations)")
            except Exception as e:
                log(f"MESH: Taubin smoothing skipped ({e})")

            # Quadric decimation to target tri budget. Newer trimesh takes
            # a target_reduction fraction (0..1); older versions took a
            # target face count. Try the new API first, fall back on older.
            if len(mesh.faces) > _MESH_TARGET_TRIS:
                original_count = len(mesh.faces)
                reduction = max(0.0, min(0.95,
                    1.0 - (_MESH_TARGET_TRIS / original_count)))
                decimated = False
                try:
                    mesh = mesh.simplify_quadric_decimation(reduction)
                    decimated = True
                    log(f"MESH: Decimated to {len(mesh.faces):,} tris "
                        f"(reduction={reduction:.2f})")
                except Exception as e:
                    # Fall back to legacy API if the new one doesn't accept a fraction
                    try:
                        mesh = mesh.simplify_quadric_decimation(
                            face_count=_MESH_TARGET_TRIS)
                        decimated = True
                        log(f"MESH: Decimated to {len(mesh.faces):,} tris "
                            f"(legacy face_count API)")
                    except Exception as e2:
                        log(f"MESH: Decimation skipped ({e}; fallback: {e2})")

            verts = np.asarray(mesh.vertices, dtype=np.float32)
            faces = np.asarray(mesh.faces,    dtype=np.uint32)
            # trimesh computes smooth per-vertex normals for us
            normals = np.asarray(mesh.vertex_normals, dtype=np.float32)
        else:
            # No trimesh — compute per-vertex normals by area-weighted
            # accumulation from face normals. Slightly rougher but valid.
            log("MESH: trimesh not installed — skipping smooth/decimate. "
                "pip install trimesh for higher quality.")
            normals = _per_vertex_normals(verts, faces)
            faces = faces.astype(np.uint32)

        # Step 6: write .omsh binary
        base, _ = os.path.splitext(output_path)
        mesh_path = base + ".omsh"
        _write_omsh(mesh_path, verts, normals, faces)
        size_mb = os.path.getsize(mesh_path) / 1024 / 1024
        log(f"MESH: Exported {os.path.basename(mesh_path)} "
            f"({len(verts):,} verts, {len(faces):,} tris, {size_mb:.1f} MB)")
        return mesh_path

    except Exception as e:
        log(f"MESH: Bone mesh extraction FAILED — {e}")
        return None


# ─────────────────────────────────────────────────────────────────
#  NERVE MESH EXTRACTION  (Phase 3 of mesh-based rendering)
#
#  Writes a .nmsh file next to the .vol using the same binary format
#  as .omsh / .vmsh. Nerves aren't visible on CT (native HU ~30
#  overlaps muscle), so we rely ENTIRELY on the AI-estimated nerve
#  mask that hd_ai_segment builds from anatomical landmarks
#  (spinal cord + root zones + plexus estimation + perivascular).
#
#  No Frangi fallback — without AI, there's no usable nerve signal.
# ─────────────────────────────────────────────────────────────────

# Nerves branch finely, so keep the CC threshold low and the tri
# budget modest (fewer tris = cleaner tubular look).
_NMESH_MIN_COMPONENT_VOX = 100
_NMESH_TARGET_TRIS       = 80_000

def extract_nerve_mesh(cube_volume_float, target_cube_size, output_path, log,
                       ai_mask=None, mri_mode=False):
    """Extract a nerve mesh and save as <basename>.nmsh next to the .vol.
    AI-only: silently skips when no AI mask is provided (non-AI mode)."""
    if not _HAS_SKIMAGE:
        log("NMESH: scikit-image missing — nerve mesh extraction skipped.")
        return None
    if ai_mask is None or int(np.sum(ai_mask)) < 500:
        log("NMESH: No AI nerve mask (need AI mode) — nerve mesh skipped.")
        return None

    try:
        log(f"NMESH: Using AI anatomical nerve mask: {int(np.sum(ai_mask)):,} voxels")

        # CC filter to drop stray fragments
        from skimage import measure as sk_measure
        labeled = sk_measure.label(ai_mask, connectivity=1)
        props   = sk_measure.regionprops(labeled)
        keep = np.zeros_like(ai_mask)
        kept = dropped = 0
        largest = 0
        for p in props:
            if p.area >= _NMESH_MIN_COMPONENT_VOX:
                keep[labeled == p.label] = True
                kept += 1
                if p.area > largest: largest = p.area
            else:
                dropped += 1
        log(f"NMESH: Kept {kept} components (largest {largest:,} vox), "
            f"dropped {dropped} (<{_NMESH_MIN_COMPONENT_VOX} vox)")
        if kept == 0:
            log("NMESH: nothing left to mesh — aborting export.")
            return None

        # Marching cubes on smooth binary field
        mc_field = keep.astype(np.float32)
        if _HAS_SCIPY:
            mc_field = ndimage.gaussian_filter(mc_field, sigma=1.0)

        verts_zyx, faces, _, _ = measure.marching_cubes(
            mc_field, level=0.5, allow_degenerate=False, step_size=1)
        log(f"NMESH: Marching cubes → {len(verts_zyx):,} verts, {len(faces):,} tris")

        # Normalise + flip winding (same convention as bone/vessel)
        T = float(target_cube_size)
        verts = np.stack([
            verts_zyx[:, 2] / T - 0.5,
            verts_zyx[:, 1] / T - 0.5,
            verts_zyx[:, 0] / T - 0.5,
        ], axis=1).astype(np.float32)
        faces = np.stack([faces[:, 0], faces[:, 2], faces[:, 1]], axis=1)

        # Smoothing + decimation
        if _HAS_TRIMESH:
            mesh = _trimesh.Trimesh(vertices=verts, faces=faces, process=False)
            try:
                # More iterations than vessels — nerves look better very smooth
                _trimesh.smoothing.filter_taubin(mesh, lamb=0.5, nu=-0.53,
                                                 iterations=15)
                log("NMESH: Taubin smoothing applied (15 iterations)")
            except Exception as e:
                log(f"NMESH: Taubin smoothing skipped ({e})")

            if len(mesh.faces) > _NMESH_TARGET_TRIS:
                original_count = len(mesh.faces)
                reduction = max(0.0, min(0.95,
                    1.0 - (_NMESH_TARGET_TRIS / original_count)))
                try:
                    mesh = mesh.simplify_quadric_decimation(reduction)
                    log(f"NMESH: Decimated to {len(mesh.faces):,} tris "
                        f"(reduction={reduction:.2f})")
                except Exception as e:
                    try:
                        mesh = mesh.simplify_quadric_decimation(
                            face_count=_NMESH_TARGET_TRIS)
                        log(f"NMESH: Decimated (legacy face_count API)")
                    except Exception as e2:
                        log(f"NMESH: Decimation skipped ({e}; fallback: {e2})")

            verts   = np.asarray(mesh.vertices,      dtype=np.float32)
            faces   = np.asarray(mesh.faces,         dtype=np.uint32)
            normals = np.asarray(mesh.vertex_normals, dtype=np.float32)
        else:
            log("NMESH: trimesh unavailable — skipping smooth/decimate.")
            normals = _per_vertex_normals(verts, faces)
            faces   = faces.astype(np.uint32)

        base, _ = os.path.splitext(output_path)
        mesh_path = base + ".nmsh"
        _write_omsh(mesh_path, verts, normals, faces)
        size_mb = os.path.getsize(mesh_path) / 1024 / 1024
        log(f"NMESH: Exported {os.path.basename(mesh_path)} "
            f"({len(verts):,} verts, {len(faces):,} tris, {size_mb:.1f} MB)")
        return mesh_path

    except Exception as e:
        log(f"NMESH: Nerve mesh extraction FAILED — {e}")
        return None


# ─────────────────────────────────────────────────────────────────
#  MUSCLE MESH EXTRACTION  (Phase 4 of mesh-based rendering)
#
#  Writes a companion .mmsh next to the .vol, same binary format as
#  .omsh. Muscles only exist when AI mode found muscle labels
#  (gluteus, iliopsoas, autochthon). Each muscle group is a separate
#  connected component, giving an anatomy-atlas look with natural gaps.
# ─────────────────────────────────────────────────────────────────
_MMESH_MIN_COMPONENT_VOX = 200
_MMESH_TARGET_TRIS       = 120_000


def extract_muscle_mesh(cube_volume_float, target_cube_size, output_path, log,
                        ai_mask=None, mri_mode=False):
    """Extract discrete muscle group meshes and save as <basename>.mmsh.
    AI-only: silently skips when no AI mask is provided."""
    if not _HAS_SKIMAGE:
        log("MMESH: scikit-image missing — muscle mesh extraction skipped.")
        return None
    if ai_mask is None or int(np.sum(ai_mask)) < 500:
        log("MMESH: No AI muscle mask (need AI mode) — muscle mesh skipped.")
        return None

    try:
        log(f"MMESH: Using AI anatomical muscle mask: {int(np.sum(ai_mask)):,} voxels")

        # CC filter — each muscle group is naturally its own connected component
        from skimage import measure as sk_measure
        labeled = sk_measure.label(ai_mask, connectivity=1)
        props   = sk_measure.regionprops(labeled)
        keep = np.zeros_like(ai_mask)
        kept = dropped = 0
        largest = 0
        for p in props:
            if p.area >= _MMESH_MIN_COMPONENT_VOX:
                keep[labeled == p.label] = True
                kept += 1
                if p.area > largest: largest = p.area
            else:
                dropped += 1
        log(f"MMESH: Kept {kept} muscle groups (largest {largest:,} vox), "
            f"dropped {dropped} (<{_MMESH_MIN_COMPONENT_VOX} vox)")
        if kept == 0:
            log("MMESH: nothing left to mesh — aborting export.")
            return None

        # Marching cubes on smooth binary field
        mc_field = keep.astype(np.float32)
        if _HAS_SCIPY:
            mc_field = ndimage.gaussian_filter(mc_field, sigma=1.2)

        verts_zyx, faces, _, _ = measure.marching_cubes(
            mc_field, level=0.5, allow_degenerate=False, step_size=1)
        log(f"MMESH: Marching cubes → {len(verts_zyx):,} verts, {len(faces):,} tris")

        # Normalise + flip winding (same convention as bone/vessel/nerve)
        T = float(target_cube_size)
        verts = np.stack([
            verts_zyx[:, 2] / T - 0.5,
            verts_zyx[:, 1] / T - 0.5,
            verts_zyx[:, 0] / T - 0.5,
        ], axis=1).astype(np.float32)
        faces = np.stack([faces[:, 0], faces[:, 2], faces[:, 1]], axis=1)

        # Smoothing + decimation
        if _HAS_TRIMESH:
            mesh = _trimesh.Trimesh(vertices=verts, faces=faces, process=False)
            try:
                _trimesh.smoothing.filter_taubin(mesh, lamb=0.5, nu=-0.53,
                                                  iterations=20)
                log("MMESH: Taubin smoothing applied (20 iterations)")
            except Exception as e:
                log(f"MMESH: Taubin smoothing skipped ({e})")

            if len(mesh.faces) > _MMESH_TARGET_TRIS:
                original_count = len(mesh.faces)
                reduction = max(0.0, min(0.95,
                    1.0 - (_MMESH_TARGET_TRIS / original_count)))
                try:
                    mesh = mesh.simplify_quadric_decimation(reduction)
                    log(f"MMESH: Decimated to {len(mesh.faces):,} tris "
                        f"(reduction={reduction:.2f})")
                except Exception as e:
                    try:
                        mesh = mesh.simplify_quadric_decimation(
                            face_count=_MMESH_TARGET_TRIS)
                        log(f"MMESH: Decimated (legacy face_count API)")
                    except Exception as e2:
                        log(f"MMESH: Decimation skipped ({e}; fallback: {e2})")

            verts   = np.asarray(mesh.vertices,      dtype=np.float32)
            faces   = np.asarray(mesh.faces,         dtype=np.uint32)
            normals = np.asarray(mesh.vertex_normals, dtype=np.float32)
        else:
            log("MMESH: trimesh unavailable — skipping smooth/decimate.")
            normals = _per_vertex_normals(verts, faces)
            faces   = faces.astype(np.uint32)

        base, _ = os.path.splitext(output_path)
        mesh_path = base + ".mmsh"
        _write_omsh(mesh_path, verts, normals, faces)
        size_mb = os.path.getsize(mesh_path) / 1024 / 1024
        log(f"MMESH: Exported {os.path.basename(mesh_path)} "
            f"({len(verts):,} verts, {len(faces):,} tris, {size_mb:.1f} MB)")
        return mesh_path

    except Exception as e:
        log(f"MMESH: Muscle mesh extraction FAILED — {e}")
        return None


def _per_vertex_normals(verts, faces):
    """Fallback normal computation when trimesh isn't available.
    Area-weighted face normal accumulation per vertex."""
    v0 = verts[faces[:, 0]]
    v1 = verts[faces[:, 1]]
    v2 = verts[faces[:, 2]]
    face_n = np.cross(v1 - v0, v2 - v0)           # area-weighted (not unit)
    normals = np.zeros_like(verts)
    np.add.at(normals, faces[:, 0], face_n)
    np.add.at(normals, faces[:, 1], face_n)
    np.add.at(normals, faces[:, 2], face_n)
    lens = np.linalg.norm(normals, axis=1, keepdims=True)
    lens = np.where(lens > 1e-8, lens, 1.0)
    return (normals / lens).astype(np.float32)


def _write_omsh(path, verts, normals, faces):
    """Write the .omsh / .vmsh binary file. Same format for both.
    See format notes above (same as bone mesh)."""
    verts   = np.ascontiguousarray(verts,   dtype=np.float32)
    normals = np.ascontiguousarray(normals, dtype=np.float32)
    faces   = np.ascontiguousarray(faces,   dtype=np.uint32)
    header  = struct.pack('<4sIII', b'OMSH', 1, len(verts), len(faces))
    with open(path, 'wb') as f:
        f.write(header)
        f.write(verts.tobytes())
        f.write(normals.tobytes())
        f.write(faces.tobytes())


# ─────────────────────────────────────────────────────────────────
#  VESSEL MESH EXTRACTION  (Phase 2 of mesh-based rendering)
#
#  Writes a companion .vmsh next to the .vol, same binary format as
#  .omsh so the Unity loader can parse both without new code.
#
#  Pipeline:
#    1. Threshold cube volume at contrast-vessel HU range (150-400).
#    2. Optional Frangi vesselness filter to enhance tubular structures
#       (falls back to plain threshold if skimage doesn't have it).
#    3. Connected-component filter drops isolated vessel fragments.
#    4. Marching cubes on binary-boundary field.
#    5. Taubin smoothing + quadric decimation (same knobs as bone).
#    6. Write .vmsh.
# ─────────────────────────────────────────────────────────────────
def extract_vessel_mesh(cube_volume_float, target_cube_size, output_path, log,
                        ai_mask=None, mri_mode=False):
    """Extract a vessel tube mesh and save as <basename>.vmsh next to the .vol.

    ai_mask: optional bool array (D,H,W) = anatomical vessel mask from AI
             segmentation. When provided, it REPLACES HU thresholding and
             the Frangi vesselness step — AI labels know which voxels are
             the aorta, carotids, vena cava, etc., with no HU overlap
             confusion from calcifications or bony trabeculae.
    """
    if not _HAS_SKIMAGE:
        log("VMESH: scikit-image missing — vessel mesh extraction skipped.")
        return None

    try:
        log("VMESH: Building vessel mask...")
        # Pre-smooth trims jagged slice boundaries for the HU fallback
        vol = cube_volume_float
        if _HAS_SCIPY:
            vol = ndimage.gaussian_filter(vol, sigma=0.8)

        # If AI gave us an anatomical vessel mask, start with that — it
        # captures the major vessels by label with zero false positives.
        # Then ALSO run Frangi on the HU-thresholded volume to pick up
        # medium-sized tubular branches TotalSegmentator doesn't label
        # individually (e.g. secondary branches of the carotid, smaller
        # branches of the aortic arch). The resulting union gives both
        # the clean major trunks and the medium branchy detail.
        if ai_mask is not None and int(np.sum(ai_mask)) > 2000:
            tube_mask = ai_mask.copy()
            n_major = int(np.sum(tube_mask))
            log(f"VMESH: AI anatomical vessel mask (major): {n_major:,} voxels")

            # Supplementary: Frangi-filtered HU tubes OUTSIDE the AI mask.
            # Narrower HU range (170-380) trims calcifications so they
            # don't get falsely promoted to vessel.
            try:
                from skimage.filters import frangi
                log("VMESH: Adding medium-vessel branches via Frangi...")
                supp_raw = (vol >= 170.0) & (vol <= 380.0) & ~tube_mask
                if int(np.sum(supp_raw)) > 2000:
                    vess = frangi(vol, sigmas=range(1, 4),
                                  alpha=0.5, beta=0.5, gamma=15,
                                  black_ridges=False)
                    if vess.max() > 0:
                        vess = vess / vess.max()
                    # Stricter tubularity than the no-AI fallback (0.25 vs 0.10)
                    # to avoid picking up bone trabeculae and other non-tubular
                    # stuff that happens to fall in the HU range.
                    supp = supp_raw & (vess > 0.25)
                    tube_mask = tube_mask | supp
                    log(f"VMESH: Frangi supplement added {int(np.sum(supp)):,} "
                        f"medium-vessel voxels (total {int(np.sum(tube_mask)):,})")
                else:
                    log("VMESH: No qualifying voxels for Frangi supplement")
            except ImportError:
                log("VMESH: Frangi unavailable — major vessels only")
            except Exception as e:
                log(f"VMESH: Frangi supplement failed ({e}) — major vessels only")
            n_tube = int(np.sum(tube_mask))
        elif mri_mode:
            # MRI: HU thresholds and Frangi on pseudo-HU are meaningless
            if ai_mask is not None:
                log("VMESH: AI vessel mask too small for MRI — skipping (no HU fallback)")
            else:
                log("VMESH: No AI vessel mask in MRI mode — skipping")
            return None
        else:
            if ai_mask is not None:
                log("VMESH: AI vessel mask too small — falling back to HU + Frangi")

            raw_mask = (vol >= _VMESH_HU_MIN) & (vol <= _VMESH_HU_MAX)
            n_raw = int(np.sum(raw_mask))
            if n_raw < 2000:
                log(f"VMESH: only {n_raw} vessel-HU voxels — skipping mesh export.")
                return None
            log(f"VMESH: Raw vessel-HU voxels: {n_raw:,}")

            # Frangi vesselness — used only when there's no AI mask, since
            # it's slow and AI labels already produce clean tubes.
            try:
                from skimage.filters import frangi
                log("VMESH: Running Frangi vesselness filter...")
                vess = frangi(vol, sigmas=range(1, 5),
                              alpha=0.5, beta=0.5, gamma=15,
                              black_ridges=False)
                if vess.max() > 0:
                    vess = vess / vess.max()
                tube_mask = raw_mask & (vess > 0.10)
                n_tube = int(np.sum(tube_mask))
                log(f"VMESH: After Frangi tubular filter: {n_tube:,} "
                    f"(dropped {n_raw - n_tube:,} non-tubular)")
                if n_tube < 1500:
                    log("VMESH: too little vessel left after Frangi — "
                        "falling back to raw threshold.")
                    tube_mask = raw_mask
            except ImportError:
                log("VMESH: Frangi filter unavailable — using HU threshold only.")
                tube_mask = raw_mask
            except Exception as e:
                log(f"VMESH: Frangi failed ({e}) — using HU threshold only.")
                tube_mask = raw_mask

        # ── Connected-component filter ──────────────────────────
        from skimage import measure as sk_measure
        labeled = sk_measure.label(tube_mask, connectivity=1)
        props   = sk_measure.regionprops(labeled)
        keep_mask = np.zeros_like(tube_mask)
        kept = dropped = 0
        largest = 0
        for p in props:
            if p.area >= _VMESH_MIN_COMPONENT_VOX:
                keep_mask[labeled == p.label] = True
                kept += 1
                if p.area > largest: largest = p.area
            else:
                dropped += 1
        log(f"VMESH: Kept {kept} components (largest {largest:,} vox), "
            f"dropped {dropped} (<{_VMESH_MIN_COMPONENT_VOX} vox)")
        if kept == 0:
            log("VMESH: nothing left to mesh — aborting export.")
            return None

        # ── Marching cubes on smooth binary field ───────────────
        mc_field = keep_mask.astype(np.float32)
        if _HAS_SCIPY:
            mc_field = ndimage.gaussian_filter(mc_field, sigma=0.8)

        verts_zyx, faces, _, _ = measure.marching_cubes(
            mc_field, level=0.5, allow_degenerate=False, step_size=1)
        log(f"VMESH: Marching cubes → {len(verts_zyx):,} verts, {len(faces):,} tris")

        # Normalise to [-0.5, 0.5]^3 and flip winding (same as bone)
        T = float(target_cube_size)
        verts = np.stack([
            verts_zyx[:, 2] / T - 0.5,
            verts_zyx[:, 1] / T - 0.5,
            verts_zyx[:, 0] / T - 0.5,
        ], axis=1).astype(np.float32)
        faces = np.stack([faces[:, 0], faces[:, 2], faces[:, 1]], axis=1)

        # ── Taubin smoothing + decimation ───────────────────────
        if _HAS_TRIMESH:
            mesh = _trimesh.Trimesh(vertices=verts, faces=faces, process=False)
            try:
                # Slightly lighter Taubin than bone — vessels are thin and
                # aggressive smoothing can collapse thin branches.
                _trimesh.smoothing.filter_taubin(mesh, lamb=0.5, nu=-0.53,
                                                 iterations=6)
                log("VMESH: Taubin smoothing applied (6 iterations)")
            except Exception as e:
                log(f"VMESH: Taubin smoothing skipped ({e})")

            if len(mesh.faces) > _VMESH_TARGET_TRIS:
                original_count = len(mesh.faces)
                reduction = max(0.0, min(0.95,
                    1.0 - (_VMESH_TARGET_TRIS / original_count)))
                try:
                    mesh = mesh.simplify_quadric_decimation(reduction)
                    log(f"VMESH: Decimated to {len(mesh.faces):,} tris "
                        f"(reduction={reduction:.2f})")
                except Exception as e:
                    try:
                        mesh = mesh.simplify_quadric_decimation(
                            face_count=_VMESH_TARGET_TRIS)
                        log(f"VMESH: Decimated to {len(mesh.faces):,} tris "
                            f"(legacy face_count API)")
                    except Exception as e2:
                        log(f"VMESH: Decimation skipped ({e}; fallback: {e2})")

            verts   = np.asarray(mesh.vertices,      dtype=np.float32)
            faces   = np.asarray(mesh.faces,         dtype=np.uint32)
            normals = np.asarray(mesh.vertex_normals, dtype=np.float32)
        else:
            log("VMESH: trimesh unavailable — skipping smooth/decimate.")
            normals = _per_vertex_normals(verts, faces)
            faces   = faces.astype(np.uint32)

        # ── Write .vmsh next to the .vol ────────────────────────
        base, _ = os.path.splitext(output_path)
        mesh_path = base + ".vmsh"
        _write_omsh(mesh_path, verts, normals, faces)
        size_mb = os.path.getsize(mesh_path) / 1024 / 1024
        log(f"VMESH: Exported {os.path.basename(mesh_path)} "
            f"({len(verts):,} verts, {len(faces):,} tris, {size_mb:.1f} MB)")
        return mesh_path

    except Exception as e:
        log(f"VMESH: Vessel mesh extraction FAILED — {e}")
        return None


# ─────────────────────────────────────────────────────────────────
#  EXPORT  —  OVOL header
#
#  v1 (32 bytes): magic(4) ver(4) W(4) H(4) D(4) dtype(4) presets(4) reserved(4)
#  v2 (44 bytes): v1 header + physX(4f) physY(4f) physZ(4f)
#     physX/Y/Z = physical extent in mm along each axis.
#     Quest uses this to apply non-uniform scale so anatomy looks correct.
# ─────────────────────────────────────────────────────────────────

def export_ovol(volume, output_path, log):
    """Export volume as .vol file (OVOL v1 — 32-byte header)."""
    D, H, W = volume.shape        # (Z, Y, X)

    header = struct.pack('<4sIIIIIII',
        b'OVOL',    # magic
        1,          # version 1
        W, H, D,    # width, height, depth
        0,          # dtype uint8
        3,          # numPresets
        0           # reserved
    )

    with open(output_path, 'wb') as f:
        f.write(header)
        f.write(volume.tobytes())

    size_mb = os.path.getsize(output_path) / 1024 / 1024
    log(f"Exported: {os.path.basename(output_path)}  ({size_mb:.1f} MB)")
    return size_mb


# ─────────────────────────────────────────────────────────────────
#  WIFI SERVER  — serves the .vol at  GET /volume.vol
# ─────────────────────────────────────────────────────────────────

_server_instance = None
_served_vol_path = None


def _get_local_ip():
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(("8.8.8.8", 80))
        ip = s.getsockname()[0]
        s.close()
        return ip
    except Exception:
        return "127.0.0.1"


class _VolHandler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        global _served_vol_path
        if self.path in ('/volume.vol', '/') and _served_vol_path and os.path.exists(_served_vol_path):
            size = os.path.getsize(_served_vol_path)
            # Extract the scan name from the .vol filename for the Quest to use
            vol_basename = os.path.splitext(os.path.basename(_served_vol_path))[0]
            self.send_response(200)
            self.send_header('Content-Type',        'application/octet-stream')
            self.send_header('Content-Length',      str(size))
            self.send_header('Content-Disposition', 'attachment; filename="volume.vol"')
            self.send_header('X-Scan-Name',         vol_basename)
            self.end_headers()
            with open(_served_vol_path, 'rb') as f:
                while True:
                    chunk = f.read(65536)
                    if not chunk:
                        break
                    self.wfile.write(chunk)
        elif self.path in ('/volume.omsh', '/volume.vmsh', '/volume.nmsh',
                           '/volume.mmsh'):
            # Companion mesh files (bone / vessels / nerves / muscles).
            # Any may be absent for older volumes or when AI mode wasn't used.
            if   self.path == '/volume.omsh': ext, fname = '.omsh', 'volume.omsh'
            elif self.path == '/volume.vmsh': ext, fname = '.vmsh', 'volume.vmsh'
            elif self.path == '/volume.nmsh': ext, fname = '.nmsh', 'volume.nmsh'
            else:                             ext, fname = '.mmsh', 'volume.mmsh'
            mesh_path = None
            if _served_vol_path:
                base, _ = os.path.splitext(_served_vol_path)
                candidate = base + ext
                if os.path.exists(candidate):
                    mesh_path = candidate
            if mesh_path:
                size = os.path.getsize(mesh_path)
                self.send_response(200)
                self.send_header('Content-Type',   'application/octet-stream')
                self.send_header('Content-Length', str(size))
                self.send_header('Content-Disposition',
                                 f'attachment; filename="{fname}"')
                self.end_headers()
                with open(mesh_path, 'rb') as f:
                    while True:
                        chunk = f.read(65536)
                        if not chunk:
                            break
                        self.wfile.write(chunk)
            else:
                self.send_response(404)
                self.end_headers()
        elif self.path == '/info':
            import json
            mesh_ok = False
            if _served_vol_path:
                base, _ = os.path.splitext(_served_vol_path)
                mesh_ok = os.path.exists(base + '.omsh')
            info = json.dumps({
                'status':     'ready',
                'filename':   'volume.vol',
                'size_bytes': os.path.getsize(_served_vol_path) if _served_vol_path else 0,
                'has_mesh':   mesh_ok,
            }).encode()
            self.send_response(200)
            self.send_header('Content-Type',   'application/json')
            self.send_header('Content-Length', str(len(info)))
            self.end_headers()
            self.wfile.write(info)
        else:
            self.send_response(404)
            self.end_headers()

    def log_message(self, fmt, *args):
        pass


def start_wifi_server(vol_path, port=8765):
    global _server_instance, _served_vol_path
    _served_vol_path = vol_path

    if _server_instance:
        try:
            _server_instance.shutdown()
        except Exception:
            pass

    socketserver.TCPServer.allow_reuse_address = True
    _server_instance = socketserver.TCPServer(('0.0.0.0', port), _VolHandler)
    t = threading.Thread(target=_server_instance.serve_forever, daemon=True)
    t.start()

    start_udp_broadcast(_get_local_ip(), port)

    return _get_local_ip(), port


# ─────────────────────────────────────────────────────────────────
#  UDP AUTO-DISCOVERY BROADCAST
# ─────────────────────────────────────────────────────────────────

_udp_broadcast_running = False

def start_udp_broadcast(ip, http_port, udp_port=8766):
    """Broadcasts BLOCKAR_SERVER:{ip}:{port} every 2 seconds on UDP."""
    global _udp_broadcast_running
    _udp_broadcast_running = True
    msg = f"BLOCKAR_SERVER:{ip}:{http_port}".encode('utf-8')

    def broadcast_loop():
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        sock.settimeout(1.0)
        while _udp_broadcast_running:
            try:
                sock.sendto(msg, ('<broadcast>', udp_port))
            except Exception:
                pass
            time.sleep(2)
        sock.close()

    t = threading.Thread(target=broadcast_loop, daemon=True)
    t.start()


# ─────────────────────────────────────────────────────────────────
#  GUI  —  One-click workflow
# ─────────────────────────────────────────────────────────────────

BG    = "#1a1a2e"
CARD  = "#16213e"
ACC   = "#0f3460"
BLUE  = "#4a9eff"
GREEN = "#3ddc84"
GRAY  = "#8892a4"
WHITE = "#e8eaf0"
PAD   = 18


def _is_frozen():
    """Returns True if running from a PyInstaller .exe bundle."""
    return getattr(sys, 'frozen', False)


def _install_packages(packages, log_fn, done_fn):
    """Install pip packages in a background thread, then call done_fn on success."""
    import subprocess

    def run():
        for pkg in packages:
            log_fn(f"Installing {pkg}...")
            try:
                subprocess.check_call(
                    [sys.executable, "-m", "pip", "install", pkg, "--quiet"],
                    stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
            except subprocess.CalledProcessError as e:
                log_fn(f"Failed to install {pkg}: {e}")
                return
        log_fn("Installation complete! Restart the app to activate.")
        done_fn()

    threading.Thread(target=run, daemon=True).start()


class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("RegionalAR Desktop")
        self.geometry("720x580")
        self.resizable(False, False)
        self.configure(bg=BG)

        self._input_folder = None
        self._input_file   = None   # single volume file (NRRD, NIfTI, etc.)
        self._vol_path     = None
        self._server_running = False
        self._hd_mode      = tk.BooleanVar(value=False)
        self._ai_segment   = tk.BooleanVar(value=False)
        self._mri_mode     = tk.BooleanVar(value=False)

        self._build_ui()

    def _build_ui(self):
        # ── Header ────────────────────────────────
        hdr = tk.Frame(self, bg=BG)
        hdr.pack(fill='x', padx=PAD, pady=(PAD, 6))
        tk.Label(hdr, text="RegionalAR Desktop", font=("Segoe UI", 22, "bold"),
                 bg=BG, fg=WHITE).pack(side='left')
        tk.Label(hdr, text="DICOM to Quest 3", font=("Segoe UI", 11),
                 bg=BG, fg=GRAY).pack(side='left', padx=(12, 0), pady=(8, 0))
        ttk.Separator(self).pack(fill='x', padx=PAD, pady=4)

        # ── Step 1: Select input ───────────
        card1 = self._card()
        tk.Label(card1, text="1. Select DICOM Folder or Volume File",
                 font=("Segoe UI", 12, "bold"), bg=CARD, fg=WHITE).pack(
                     anchor='w', padx=14, pady=(12, 4))
        tk.Label(card1, text="Choose the folder containing your .dcm files",
                 font=("Segoe UI", 9), bg=CARD, fg=GRAY).pack(anchor='w', padx=14)

        row1 = tk.Frame(card1, bg=CARD)
        row1.pack(fill='x', padx=14, pady=(8, 14))
        self.folder_var = tk.StringVar(value="No folder selected")
        tk.Label(row1, textvariable=self.folder_var, font=("Segoe UI", 9),
                 bg=CARD, fg=BLUE, wraplength=420, justify='left').pack(
                     side='left', fill='x', expand=True)
        self._btn(row1, "Browse...", self._browse).pack(side='right')

        # ── Scan name (what the .vol and companion meshes get called) ──
        # Default is derived from the DICOM folder name when the user
        # browses. They can override here before pressing Process.
        name_row = tk.Frame(card1, bg=CARD)
        name_row.pack(fill='x', padx=14, pady=(0, 14))
        tk.Label(name_row, text="Scan name:", font=("Segoe UI", 9),
                 bg=CARD, fg=GRAY).pack(side='left')
        self.scan_name_var = tk.StringVar(value="head_neck")
        self.scan_name_entry = tk.Entry(
            name_row, textvariable=self.scan_name_var,
            font=("Segoe UI", 10), bg="#0f1621", fg=WHITE,
            insertbackground=WHITE, relief='flat', width=30)
        self.scan_name_entry.pack(side='left', padx=(8, 0), fill='x', expand=True)
        tk.Label(name_row, text=".vol", font=("Segoe UI", 9),
                 bg=CARD, fg=GRAY).pack(side='left', padx=(4, 0))

        # ── Step 2: Process & Send (one button) ──
        card2 = self._card()
        tk.Label(card2, text="2. Process & Send to Quest",
                 font=("Segoe UI", 12, "bold"), bg=CARD, fg=WHITE).pack(
                     anchor='w', padx=14, pady=(12, 4))
        tk.Label(card2,
                 text="Converts DICOM, starts WiFi server, and broadcasts to Quest automatically",
                 font=("Segoe UI", 9), bg=CARD, fg=GRAY).pack(anchor='w', padx=14)

        # ── Quality options row ──────────────────
        opts_row = tk.Frame(card2, bg=CARD)
        opts_row.pack(fill='x', padx=14, pady=(8, 2))

        self.go_btn = tk.Button(
            opts_row, text="Process & Send", font=("Segoe UI", 13, "bold"),
            bg="#1d6fa4", fg=WHITE, bd=0, padx=30, pady=12,
            cursor='hand2', activebackground="#2a8fcf", activeforeground=WHITE,
            command=self._go)
        self.go_btn.pack(side='left')

        # HD mode is always enabled (no checkbox needed).
        # If scipy is missing, auto-install it.
        if not _check_hd_available():
            try:
                import subprocess, sys
                subprocess.check_call([sys.executable, "-m", "pip", "install", "scipy", "--quiet"])
            except Exception:
                pass  # will degrade gracefully at runtime

        # AI segment checkbox
        ai_frame = tk.Frame(opts_row, bg=CARD)
        ai_frame.pack(side='left', padx=(16, 0))
        ai_available = _check_ai_available()
        ai_state = 'normal' if ai_available else 'disabled'
        ai_cb = tk.Checkbutton(ai_frame, text="AI Segment",
                       variable=self._ai_segment, font=("Segoe UI", 10),
                       bg=CARD, fg=BLUE if ai_available else GRAY,
                       selectcolor=ACC, state=ai_state,
                       activebackground=CARD, activeforeground=BLUE,
                       cursor='hand2' if ai_available else 'arrow')
        ai_cb.pack(anchor='w')
        if ai_available:
            tk.Label(ai_frame, text="TotalSegmentator ready",
                     font=("Segoe UI", 8), bg=CARD, fg=GREEN).pack(anchor='w')
        else:
            ai_install_row = tk.Frame(ai_frame, bg=CARD)
            ai_install_row.pack(anchor='w')
            tk.Label(ai_install_row, text="Not installed ",
                     font=("Segoe UI", 8), bg=CARD, fg=GRAY).pack(side='left')
            self._ai_install_btn = tk.Button(
                ai_install_row, text="Install AI (~3 GB)", font=("Segoe UI", 8),
                bg=ACC, fg=BLUE, bd=0, padx=6, pady=1, cursor='hand2',
                command=self._install_ai)
            self._ai_install_btn.pack(side='left')

        # Scan type toggle: CT ↔ MRI
        scan_type_frame = tk.Frame(opts_row, bg=CARD)
        scan_type_frame.pack(side='left', padx=(20, 0))
        tk.Label(scan_type_frame, text="Scan Type",
                 font=("Segoe UI", 9), bg=CARD, fg=GRAY).pack(anchor='w')
        toggle_row = tk.Frame(scan_type_frame, bg=CARD)
        toggle_row.pack(anchor='w')
        self._ct_label = tk.Label(toggle_row, text="CT", font=("Segoe UI", 11, "bold"),
                                  bg=CARD, fg=BLUE, cursor='hand2')
        self._ct_label.pack(side='left')
        self._scan_toggle_btn = tk.Button(
            toggle_row, text="◀ CT", font=("Segoe UI", 9, "bold"),
            bg='#1a3a5c', fg=BLUE, bd=1, relief='ridge',
            width=10, cursor='hand2',
            command=self._toggle_scan_type)
        self._scan_toggle_btn.pack(side='left', padx=6)
        self._mri_label = tk.Label(toggle_row, text="MRI", font=("Segoe UI", 11),
                                   bg=CARD, fg=GRAY, cursor='hand2')
        self._mri_label.pack(side='left')
        self._ct_label.bind("<Button-1>", lambda e: self._set_scan_type(False))
        self._mri_label.bind("<Button-1>", lambda e: self._set_scan_type(True))

        self.progress = ttk.Progressbar(card2, length=570, mode='determinate')
        self.progress.pack(padx=14, pady=(0, 4))
        self.status_var = tk.StringVar(value="Ready — select a DICOM folder above.")
        tk.Label(card2, textvariable=self.status_var, font=("Segoe UI", 9),
                 bg=CARD, fg=GRAY).pack(anchor='w', padx=14, pady=(0, 10))

        # ── Status panel ──────────────────────────
        card3 = self._card()
        tk.Label(card3, text="Server Status",
                 font=("Segoe UI", 11, "bold"), bg=CARD, fg=WHITE).pack(
                     anchor='w', padx=14, pady=(12, 4))

        self.server_status_var = tk.StringVar(value="Not running")
        self.server_lbl = tk.Label(card3, textvariable=self.server_status_var,
                 font=("Segoe UI", 10), bg=CARD, fg=GRAY)
        self.server_lbl.pack(anchor='w', padx=14, pady=(0, 4))

        self.discovery_var = tk.StringVar(value="")
        tk.Label(card3, textvariable=self.discovery_var,
                 font=("Segoe UI", 9), bg=CARD, fg=GRAY).pack(
                     anchor='w', padx=14, pady=(0, 12))

        # ── Log ───────────────────────────────────
        tk.Label(self, text="Log", font=("Segoe UI", 9, "bold"),
                 bg=BG, fg=GRAY).pack(anchor='w', padx=PAD, pady=(4, 0))
        self.log_box = scrolledtext.ScrolledText(
            self, height=5, font=("Consolas", 9),
            bg="#0d0f1a", fg="#90caf9", relief='flat', bd=0,
            state='disabled', wrap='word')
        self.log_box.pack(fill='x', padx=PAD, pady=(2, PAD))

    # ── Helpers ───────────────────────────────────
    def _card(self):
        frame = tk.Frame(self, bg=CARD, bd=0,
                         highlightthickness=1, highlightbackground=ACC)
        frame.pack(fill='x', padx=PAD, pady=5)
        return frame

    def _btn(self, parent, text, cmd, state='normal'):
        return tk.Button(parent, text=text, font=("Segoe UI", 9),
                         bg=ACC, fg=WHITE, bd=0, padx=12, pady=5,
                         cursor='hand2', activebackground="#1a4a7a",
                         activeforeground=WHITE, state=state, command=cmd)

    def _log(self, msg):
        self.log_box.config(state='normal')
        self.log_box.insert('end', msg + "\n")
        self.log_box.see('end')
        self.log_box.config(state='disabled')
        self.status_var.set(msg)
        self.update_idletasks()
        # Mirror every log line to a persistent file so issues can be
        # reviewed after processing finishes — the log box scrolls past
        # fast and closes on exit. The file lives next to the .exe
        # (same folder the RegionalAR app was launched from).
        try:
            if not hasattr(self, '_log_file_path'):
                # Use exe folder in frozen PyInstaller builds, else cwd
                base = os.path.dirname(sys.executable) \
                       if getattr(sys, 'frozen', False) else os.getcwd()
                self._log_file_path = os.path.join(base, 'RegionalAR.log')
                # Truncate at app start
                with open(self._log_file_path, 'w', encoding='utf-8') as f:
                    f.write(f"--- RegionalAR log started {time.strftime('%Y-%m-%d %H:%M:%S')} ---\n")
            with open(self._log_file_path, 'a', encoding='utf-8') as f:
                f.write(msg + "\n")
        except Exception:
            pass

    def _set_progress(self, pct):
        self.progress['value'] = pct
        self.update_idletasks()

    # ── In-app package installation ─────────────────
    def _show_install_guide(self, feature_name):
        """Show the correct install instructions depending on how the app is running."""
        if _is_frozen():
            messagebox.showinfo(
                f"{feature_name} — Install Required",
                f"{feature_name} cannot be installed from the standalone .exe.\n\n"
                "To enable all features:\n\n"
                "1. Locate install_regionalar.bat in the RegionalAR folder\n"
                "2. Double-click it to run the full installer\n"
                "3. It installs Python, HD, and AI packages automatically\n"
                "4. Use the 'RegionalAR Desktop' shortcut it creates\n\n"
                "This only needs to be done once per PC.")
            self._log(f"{feature_name}: Run install_regionalar.bat for full features")
        else:
            return False  # not frozen, proceed with pip install
        return True  # frozen, showed message instead

    def _install_hd(self):
        if self._show_install_guide("High Definition"):
            return
        self._log("Installing HD packages (scipy, scikit-image)...")
        _install_packages(
            ["scipy", "scikit-image"],
            lambda msg: self.after(0, self._log, msg),
            lambda: self.after(0, lambda: messagebox.showinfo(
                "HD Installed",
                "High Definition packages installed!\n\n"
                "Please restart RegionalAR Desktop to enable HD mode."))
        )

    def _install_ai(self):
        if self._show_install_guide("AI Segment"):
            return
        if hasattr(self, '_ai_install_btn'):
            self._ai_install_btn.config(state='disabled', text="Installing...")
        self._log("Installing AI packages (this downloads ~3 GB)...")
        self._log("Installing: scipy, scikit-image, torch, TotalSegmentator...")

        packages = ["scipy", "scikit-image", "TotalSegmentator"]
        _install_packages(
            packages,
            lambda msg: self.after(0, self._log, msg),
            lambda: self.after(0, self._ai_install_done)
        )

    def _ai_install_done(self):
        if hasattr(self, '_ai_install_btn'):
            self._ai_install_btn.config(text="Installed!")
        messagebox.showinfo(
            "AI Installed",
            "TotalSegmentator installed!\n\n"
            "Please restart RegionalAR Desktop to enable AI Segment.\n\n"
            "Note: The first AI segmentation will download\n"
            "~1.5 GB of neural network models.")

    # ── Browse ────────────────────────────────────
    def _browse(self):
        # Let user pick either a DICOM folder OR a single volume file
        # (NRRD, NIfTI, MHA). Try file picker first via a dialog choice.
        choice = messagebox.askyesnocancel(
            "Input Type",
            "Yes  →  Select a DICOM folder\n"
            "No   →  Select a volume file (NRRD, NIfTI, MHA)\n"
            "Cancel  →  Cancel")
        if choice is None:
            return

        if choice:  # Yes → DICOM folder
            folder = filedialog.askdirectory(title="Select DICOM folder")
            if not folder:
                return
            count = sum(1 for f in os.listdir(folder) if f.lower().endswith('.dcm'))
            if count == 0:
                messagebox.showerror("No DICOM files", f"No .dcm files found in:\n{folder}")
                return
            self._input_folder = folder
            self._input_file = None
            self.folder_var.set(f"{folder}  ({count} files)")
            self.status_var.set(f"Ready — {count} DICOM files selected. Click 'Process & Send'.")
            self._log(f"Selected: {folder} ({count} .dcm files)")
            suggested = _sanitize_scan_name(os.path.basename(folder.rstrip("/\\")))
        else:  # No → volume file
            filetypes = [
                ("Volume files", "*.nrrd *.nii *.nii.gz *.mha *.mhd"),
                ("NRRD", "*.nrrd"),
                ("NIfTI", "*.nii *.nii.gz"),
                ("MetaImage", "*.mha *.mhd"),
                ("All files", "*.*"),
            ]
            filepath = filedialog.askopenfilename(
                title="Select volume file", filetypes=filetypes)
            if not filepath:
                return
            if not _is_volume_file(filepath):
                messagebox.showerror("Unsupported format",
                    f"Not a recognized volume file:\n{filepath}\n\n"
                    f"Supported: {', '.join(VOLUME_FILE_EXTS)}")
                return
            self._input_file = filepath
            self._input_folder = None
            fname = os.path.basename(filepath)
            self.folder_var.set(f"{filepath}")
            self.status_var.set(f"Ready — {fname} selected. Click 'Process & Send'.")
            self._log(f"Selected volume file: {filepath}")
            # Strip extension(s) for suggested name
            base = os.path.basename(filepath)
            for ext in VOLUME_FILE_EXTS:
                if base.lower().endswith(ext):
                    base = base[:-len(ext)]
                    break
            suggested = _sanitize_scan_name(base)

        if suggested:
            self.scan_name_var.set(suggested)

    # ── Scan type toggle (CT ↔ MRI) ─────────────
    def _toggle_scan_type(self):
        self._set_scan_type(not self._mri_mode.get())

    def _set_scan_type(self, mri):
        self._mri_mode.set(mri)
        if mri:
            self._scan_toggle_btn.config(text="MRI ▶", bg='#3a1a5c', fg='#e0a0ff')
            self._mri_label.config(font=("Segoe UI", 11, "bold"), fg='#e0a0ff')
            self._ct_label.config(font=("Segoe UI", 11), fg=GRAY)
        else:
            self._scan_toggle_btn.config(text="◀ CT", bg='#1a3a5c', fg=BLUE)
            self._ct_label.config(font=("Segoe UI", 11, "bold"), fg=BLUE)
            self._mri_label.config(font=("Segoe UI", 11), fg=GRAY)

    # ── One-click Go ──────────────────────────────
    def _go(self):
        if not self._input_folder and not self._input_file:
            messagebox.showwarning("No input", "Select a DICOM folder or volume file first.")
            return

        self.go_btn.config(state='disabled', text="Processing...")
        self._set_progress(0)

        def run():
            try:
                scan_name = _sanitize_scan_name(self.scan_name_var.get())

                # Determine output path and input source
                if self._input_file:
                    output_path = os.path.join(
                        os.path.dirname(self._input_file), scan_name + ".vol")
                    source_folder = os.path.dirname(self._input_file)
                else:
                    output_path = os.path.join(
                        os.path.dirname(self._input_folder), scan_name + ".vol")
                    source_folder = self._input_folder

                self.after(0, self._set_progress, 10)

                # Load from volume file or DICOM folder
                if self._input_file:
                    self.after(0, self._log, f"Loading volume file: {os.path.basename(self._input_file)}")
                    volume, spacing = load_volume_file(
                        self._input_file,
                        lambda msg: self.after(0, self._log, msg))
                else:
                    self.after(0, self._log, "Loading DICOM series...")
                    volume, spacing = load_dicom_series(
                        self._input_folder,
                        lambda msg: self.after(0, self._log, msg))

                hd = True  # HD always enabled — no reason to skip it
                ai = self._ai_segment.get()
                mri = self._mri_mode.get()
                mode_str = " (HD)"
                mode_str += " (AI)" if ai else ""
                mode_str += " (MRI)" if mri else ""
                self.after(0, self._log, f"Processing volume{mode_str}...")
                self.after(0, self._set_progress, 50)
                processed = process_volume(
                    volume, spacing,
                    lambda msg: self.after(0, self._log, msg),
                    hd_mode=hd, ai_segment=ai,
                    dicom_folder=self._input_folder or os.path.dirname(self._input_file),
                    mesh_output_path=output_path,
                    mri_mode=mri)

                self.after(0, self._log, "Exporting .vol file...")
                self.after(0, self._set_progress, 80)
                size_mb = export_ovol(
                    processed, output_path,
                    lambda msg: self.after(0, self._log, msg))

                self._vol_path = output_path

                # Auto-start WiFi server + UDP broadcast
                self.after(0, self._log, "Starting WiFi server...")
                self.after(0, self._set_progress, 90)
                ip, port = start_wifi_server(output_path, port=8765)

                self.after(0, self._all_done, ip, port, size_mb)

            except Exception as e:
                self.after(0, self._go_error, str(e))

        threading.Thread(target=run, daemon=True).start()

    def _all_done(self, ip, port, size_mb):
        self._server_running = True
        self._set_progress(100)
        self.go_btn.config(state='normal', text="Process & Send")
        self.status_var.set(f"Done! {size_mb:.1f} MB — server running, broadcasting to Quest.")

        url = f"http://{ip}:{port}/volume.vol"
        self.server_status_var.set(f"Serving at {url}")
        self.server_lbl.config(fg=GREEN)
        self.discovery_var.set(
            "Broadcasting on your network — Quest will find this server automatically.\n"
            "Open RegionalAR on your Quest and tap 'WiFi Load'.")

        self._log(f"WiFi server running: {url}")
        self._log(f"UDP auto-discovery broadcasting on port 8766")
        self._log("Ready for Quest to connect!")

        messagebox.showinfo("Ready for Quest!",
            f"Volume processed ({size_mb:.1f} MB) and server started!\n\n"
            f"On your Quest 3:\n"
            f"1. Open RegionalAR\n"
            f"2. Tap 'WiFi Load'\n"
            f"3. It will find this PC automatically\n\n"
            f"(Server: {url})")

    def _go_error(self, err):
        self.go_btn.config(state='normal', text="Process & Send")
        self._log(f"ERROR: {err}")
        messagebox.showerror("Processing failed", f"An error occurred:\n\n{err}")


# ─────────────────────────────────────────────────────────────────
#  ENTRY POINT
# ─────────────────────────────────────────────────────────────────

if __name__ == "__main__":
    # CRITICAL for PyInstaller on Windows: torch / nnU-Net / TotalSegmentator
    # internally use multiprocessing. Windows' 'spawn' start method re-runs
    # the entire frozen exe to launch each worker, which would open another
    # GUI window unless we tell multiprocessing we're a frozen app. Must be
    # the FIRST thing in __main__, before anything else.
    import multiprocessing
    multiprocessing.freeze_support()

    app = App()
    app.mainloop()
