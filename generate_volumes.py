#!/usr/bin/env python3
"""
RegionalAR — Anatomy Volume Generator
======================================
Generates 9 sample anatomy volumes (shoulder, hip, thigh, knee × left/right
+ spine midline) from BodyParts3D OBJ mesh files + procedural fallbacks.

Pipeline:
  1. Download BodyParts3D OBJ archive (1,523 structures, CC license)
  2. Filter structures by body region and tissue type
  3. Voxelize each tissue layer into a 256³ grid
  4. Combine into a single uint8 OVOL volume (.vol)
  5. Export companion mesh files (.omsh, .vmsh, .nmsh, .mmsh)
  6. Mirror X-axis for left/right pairs

Output format matches dicom_processor.py exactly:
  - .vol  = OVOL v1 header (32 bytes) + uint8[256³]
  - .omsh = bone mesh    (magic "OMSH", float32 verts/normals, uint32 faces)
  - .vmsh = vessel mesh  (same format)
  - .nmsh = nerve mesh   (same format)
  - .mmsh = muscle mesh  (same format)

Usage:
  python generate_volumes.py                    # Full pipeline (downloads data)
  python generate_volumes.py --catalog          # List available structures
  python generate_volumes.py --test             # Generate test volumes (no download)
  python generate_volumes.py --region shoulder  # Generate only one region

Dependencies: numpy (+ requests, zipfile from stdlib)
              Optional: trimesh, scipy (for higher-quality results)
"""

import os
import sys
import struct
import math
import zipfile
import io
import json
import time
import argparse
from pathlib import Path

import numpy as np

# Optional imports — degrade gracefully
try:
    import trimesh
    _HAS_TRIMESH = True
except ImportError:
    _HAS_TRIMESH = False

try:
    from scipy import ndimage
    _HAS_SCIPY = True
except ImportError:
    _HAS_SCIPY = False

try:
    import requests
    _HAS_REQUESTS = True
except ImportError:
    _HAS_REQUESTS = False

# ─────────────────────────────────────────────────────────────────
#  CONFIGURATION
# ─────────────────────────────────────────────────────────────────

VOLUME_SIZE = 256          # Voxel grid resolution (256³)
MESH_TARGET_TRIS = 150_000 # Max triangles per tissue mesh for Quest 3

# Tissue intensity values in the uint8 volume.
# These MUST match the density ranges in VolumeRenderer.cs:
#   Bone:        densityMin=0.561  densityMax=1.000  → uint8 143–255
#   Vasculature: densityMin=0.382  densityMax=0.464  → uint8  97–118
#   Nerves:      densityMin=0.369  densityMax=0.382  → uint8  94– 97
#   Muscle:      densityMin=0.356  densityMax=0.369  → uint8  91– 94
# Density = uint8 / 255.  Pick the midpoint of each range.
INTENSITY_BONE   = 220     # 220/255=0.863  (in Bone range)
INTENSITY_VESSEL = 108     # 108/255=0.424  (in Vasculature range)
INTENSITY_NERVE  =  96     #  96/255=0.376  (in Nerves range)
INTENSITY_MUSCLE =  93     #  93/255=0.365  (in Muscle range)
INTENSITY_BG     = 0       # Background

# BodyParts3D download URLs (tried in order)
BP3D_URLS = [
    # GitHub mirror (STL files, ~200 MB) — most reliable
    "https://github.com/Kevin-Mattheus-Moerman/BodyParts3D/archive/refs/heads/main.zip",
    # FTP archive (OBJ files, ~136 MB) — original source
    "ftp://ftp.biosciencedbc.jp/archive/bodyparts3d/LATEST/isa_BP3D_4.0_obj_99.zip",
    # HTTPS archive (OBJ files) — may be deprecated
    "https://dbarchive.biosciencedbc.jp/data/bodyparts3d/isa_BP3D_4.0_obj_99.zip",
]

# ─────────────────────────────────────────────────────────────────
#  BODY REGION → STRUCTURE MAPPING
#
#  Each region maps to structures by FMA ID or name substring.
#  BodyParts3D files are named like: FMA7163_Rectus_femoris.obj
#  We match by FMA ID where known, fallback to name substring.
# ─────────────────────────────────────────────────────────────────

REGION_STRUCTURES = {
    "shoulder": {
        "bone": {
            "keywords": ["scapula", "clavicle", "humerus", "acromion",
                         "coracoid", "glenoid"],
            "fma_ids": [13394, 13321, 13303],  # scapula, clavicle, humerus
        },
        "muscle": {
            "keywords": ["deltoid", "supraspinatus", "infraspinatus",
                         "subscapularis", "teres_minor", "teres_major",
                         "trapezius", "pectoralis", "biceps_brachii",
                         "triceps_brachii", "latissimus", "rhomboid",
                         "levator_scapulae", "serratus_anterior",
                         "coracobrachialis"],
            "fma_ids": [],
        },
        "vessel": {
            "keywords": ["subclavian_artery", "axillary_artery",
                         "brachial_artery", "subclavian_vein",
                         "axillary_vein", "cephalic_vein",
                         "basilic_vein", "suprascapular_artery",
                         "circumflex_humeral", "thoracoacromial"],
            "fma_ids": [],
        },
        "nerve": {
            "keywords": ["brachial_plexus", "axillary_nerve",
                         "suprascapular_nerve", "musculocutaneous",
                         "radial_nerve", "median_nerve", "ulnar_nerve",
                         "long_thoracic", "subscapular_nerve",
                         "thoracodorsal"],
            "fma_ids": [],
        },
    },
    "hip": {
        "bone": {
            "keywords": ["hip_bone", "ilium", "ischium", "pubis",
                         "acetabulum", "femur", "femoral_head",
                         "femoral_neck", "greater_trochanter",
                         "lesser_trochanter", "sacrum", "coccyx",
                         "os_coxae", "innominate"],
            "fma_ids": [16580, 16581, 16585, 16586, 9611],
        },
        "muscle": {
            "keywords": ["gluteus_maximus", "gluteus_medius", "gluteus_minimus",
                         "iliopsoas", "psoas_major", "iliacus",
                         "piriformis", "obturator_internus", "obturator_externus",
                         "gemellus_superior", "gemellus_inferior",
                         "quadratus_femoris", "tensor_fasciae",
                         "sartorius", "rectus_femoris",
                         "pectineus", "adductor_longus", "adductor_brevis",
                         "adductor_magnus", "gracilis"],
            "fma_ids": [],
        },
        "vessel": {
            "keywords": ["common_iliac", "internal_iliac", "external_iliac",
                         "femoral_artery", "femoral_vein",
                         "deep_femoral", "profunda_femoris",
                         "obturator_artery", "superior_gluteal_artery",
                         "inferior_gluteal_artery", "great_saphenous",
                         "iliac_vein", "circumflex_iliac"],
            "fma_ids": [],
        },
        "nerve": {
            "keywords": ["sciatic_nerve", "femoral_nerve", "obturator_nerve",
                         "superior_gluteal_nerve", "inferior_gluteal_nerve",
                         "pudendal_nerve", "lateral_femoral_cutaneous",
                         "lumbosacral", "sacral_plexus", "lumbar_plexus"],
            "fma_ids": [],
        },
    },
    "thigh": {
        "bone": {
            "keywords": ["femur", "femoral_shaft", "femoral_condyle",
                         "linea_aspera", "patella"],
            "fma_ids": [9611],  # femur
        },
        "muscle": {
            "keywords": ["quadriceps", "rectus_femoris", "vastus_lateralis",
                         "vastus_medialis", "vastus_intermedius",
                         "biceps_femoris", "semitendinosus", "semimembranosus",
                         "sartorius", "gracilis", "adductor_longus",
                         "adductor_brevis", "adductor_magnus",
                         "tensor_fasciae", "pectineus"],
            "fma_ids": [],
        },
        "vessel": {
            "keywords": ["femoral_artery", "femoral_vein",
                         "deep_femoral", "profunda_femoris",
                         "great_saphenous", "popliteal",
                         "perforating_artery", "descending_genicular"],
            "fma_ids": [],
        },
        "nerve": {
            "keywords": ["sciatic_nerve", "femoral_nerve",
                         "saphenous_nerve", "obturator_nerve",
                         "posterior_cutaneous_nerve_of_thigh",
                         "tibial_nerve", "common_peroneal",
                         "common_fibular"],
            "fma_ids": [],
        },
    },
    "knee": {
        "bone": {
            "keywords": ["patella", "femoral_condyle", "tibial_plateau",
                         "tibia", "fibula", "tibial_tuberosity",
                         "intercondylar", "femur_distal"],
            "fma_ids": [24485, 9611, 24476],  # patella, femur, tibia
        },
        "muscle": {
            "keywords": ["quadriceps", "vastus_medialis", "vastus_lateralis",
                         "popliteus", "gastrocnemius", "plantaris",
                         "biceps_femoris", "semitendinosus", "semimembranosus",
                         "sartorius", "gracilis", "tibialis_anterior"],
            "fma_ids": [],
        },
        "vessel": {
            "keywords": ["popliteal_artery", "popliteal_vein",
                         "anterior_tibial", "posterior_tibial",
                         "genicular", "great_saphenous",
                         "small_saphenous", "peroneal_artery",
                         "fibular_artery"],
            "fma_ids": [],
        },
        "nerve": {
            "keywords": ["tibial_nerve", "common_peroneal",
                         "common_fibular", "saphenous_nerve",
                         "deep_peroneal", "superficial_peroneal",
                         "popliteal_nerve", "sural_nerve",
                         "lateral_sural_cutaneous"],
            "fma_ids": [],
        },
    },
    "spine": {
        "bone": {
            # Vertebral column C2–sacrum (axis through coccyx)
            "keywords": ["cervical_vertebra", "thoracic_vertebra",
                         "lumbar_vertebra", "sacrum", "coccyx",
                         "vertebra", "vertebral_body", "vertebral_arch",
                         "spinous_process", "transverse_process",
                         "intervertebral_disc", "axis_c2", "c2", "c3",
                         "c4", "c5", "c6", "c7", "t1", "t2", "t3",
                         "t4", "t5", "t6", "t7", "t8", "t9", "t10",
                         "t11", "t12", "l1", "l2", "l3", "l4", "l5"],
            "fma_ids": [9915, 9139, 9921, 16580],  # vertebral column, sacrum
        },
        "muscle": {
            # Immediate paraspinal muscles only
            "keywords": ["erector_spinae", "multifidus", "longissimus",
                         "iliocostalis", "spinalis", "semispinalis",
                         "rotatores", "interspinales", "intertransversarii",
                         "quadratus_lumborum", "psoas_major"],
            "fma_ids": [],
        },
        "vessel": {
            # Major vertebral/spinal arteries
            "keywords": ["vertebral_artery", "spinal_artery",
                         "anterior_spinal", "posterior_spinal",
                         "segmental_artery", "lumbar_artery",
                         "intercostal_artery", "vertebral_vein",
                         "basivertebral_vein", "internal_vertebral"],
            "fma_ids": [3956],  # vertebral artery
        },
        "nerve": {
            # Spinal nerve roots and cord
            "keywords": ["spinal_cord", "spinal_nerve", "nerve_root",
                         "dorsal_root", "ventral_root", "cauda_equina",
                         "cervical_nerve", "thoracic_nerve",
                         "lumbar_nerve", "sacral_nerve",
                         "dural_sac", "conus_medullaris"],
            "fma_ids": [7647],  # spinal cord
        },
    },
}

# ─────────────────────────────────────────────────────────────────
#  OBJ PARSER (pure Python + numpy, no trimesh needed)
# ─────────────────────────────────────────────────────────────────

def load_obj(filepath):
    """Parse a Wavefront OBJ file → (vertices Nx3 float32, faces Mx3 int32).
    Handles 'v' vertex lines and 'f' face lines (triangulates quads)."""
    verts = []
    faces = []
    with open(filepath, 'r', errors='replace') as f:
        for line in f:
            line = line.strip()
            if line.startswith('v '):
                parts = line.split()
                verts.append([float(parts[1]), float(parts[2]), float(parts[3])])
            elif line.startswith('f '):
                parts = line.split()[1:]
                # OBJ face indices: can be "v", "v/vt", "v/vt/vn", "v//vn"
                idx = []
                for p in parts:
                    idx.append(int(p.split('/')[0]) - 1)  # 1-indexed → 0-indexed
                # Triangulate polygon fans
                for i in range(1, len(idx) - 1):
                    faces.append([idx[0], idx[i], idx[i+1]])

    if not verts:
        return None, None
    return np.array(verts, dtype=np.float32), np.array(faces, dtype=np.int32)


def load_stl(filepath):
    """Parse a binary or ASCII STL file → (vertices Nx3, faces Mx3)."""
    with open(filepath, 'rb') as f:
        header = f.read(80)
        # Check if ASCII
        try:
            header_str = header.decode('ascii', errors='ignore')
            if header_str.strip().startswith('solid'):
                # Might be ASCII — but binary STL can also start with "solid"
                # Check if the next chunk looks like ASCII
                f.seek(0)
                content = f.read().decode('ascii', errors='replace')
                if 'facet normal' in content:
                    return _parse_stl_ascii(content)
        except:
            pass
        # Binary STL
        f.seek(80)
        num_tris = struct.unpack('<I', f.read(4))[0]
        verts = []
        faces = []
        for i in range(num_tris):
            data = f.read(50)  # 12 normal + 36 vertices + 2 attrib
            if len(data) < 50:
                break
            vals = struct.unpack('<12f', data[:48])
            # Skip normal (vals[0:3]), read 3 vertices
            v0 = vals[3:6]
            v1 = vals[6:9]
            v2 = vals[9:12]
            base = len(verts)
            verts.extend([v0, v1, v2])
            faces.append([base, base+1, base+2])

    if not verts:
        return None, None
    return np.array(verts, dtype=np.float32), np.array(faces, dtype=np.int32)


def _parse_stl_ascii(content):
    """Parse ASCII STL content."""
    import re
    verts = []
    faces = []
    for m in re.finditer(r'vertex\s+([\-\d.eE+]+)\s+([\-\d.eE+]+)\s+([\-\d.eE+]+)', content):
        verts.append([float(m.group(1)), float(m.group(2)), float(m.group(3))])
    # Every 3 consecutive vertices form a triangle
    for i in range(0, len(verts), 3):
        if i + 2 < len(verts):
            faces.append([i, i+1, i+2])
    if not verts:
        return None, None
    return np.array(verts, dtype=np.float32), np.array(faces, dtype=np.int32)


# ─────────────────────────────────────────────────────────────────
#  MESH UTILITIES
# ─────────────────────────────────────────────────────────────────

def normalize_mesh_to_unit_cube(verts):
    """Scale and translate vertices to fit in [-0.5, 0.5]³."""
    vmin = verts.min(axis=0)
    vmax = verts.max(axis=0)
    extent = vmax - vmin
    max_extent = extent.max()
    if max_extent < 1e-10:
        return verts
    center = (vmin + vmax) / 2.0
    return (verts - center) / max_extent


def compute_vertex_normals(verts, faces):
    """Compute smooth vertex normals by averaging face normals."""
    v0 = verts[faces[:, 0]]
    v1 = verts[faces[:, 1]]
    v2 = verts[faces[:, 2]]
    face_normals = np.cross(v1 - v0, v2 - v0)

    vert_normals = np.zeros_like(verts)
    np.add.at(vert_normals, faces[:, 0], face_normals)
    np.add.at(vert_normals, faces[:, 1], face_normals)
    np.add.at(vert_normals, faces[:, 2], face_normals)

    norms = np.linalg.norm(vert_normals, axis=1, keepdims=True)
    norms = np.where(norms > 1e-8, norms, 1.0)
    return (vert_normals / norms).astype(np.float32)


def decimate_mesh_simple(verts, faces, target_tris):
    """Simple mesh decimation by uniform sampling if over budget.
    For production quality, use trimesh.simplify_quadric_decimation."""
    if _HAS_TRIMESH and len(faces) > target_tris:
        try:
            mesh = trimesh.Trimesh(vertices=verts, faces=faces)
            mesh = mesh.simplify_quadric_decimation(target_tris)
            return mesh.vertices.astype(np.float32), mesh.faces.astype(np.int32)
        except:
            pass

    if len(faces) <= target_tris:
        return verts, faces

    # Fallback: random face sampling (preserves connectivity poorly but
    # keeps triangle count manageable for Quest GPU)
    indices = np.random.choice(len(faces), target_tris, replace=False)
    indices.sort()
    selected_faces = faces[indices]

    # Remap vertex indices to compact array
    unique_verts = np.unique(selected_faces.flatten())
    remap = np.full(len(verts), -1, dtype=np.int32)
    remap[unique_verts] = np.arange(len(unique_verts), dtype=np.int32)
    new_faces = remap[selected_faces]
    new_verts = verts[unique_verts]
    return new_verts.astype(np.float32), new_faces.astype(np.int32)


def merge_meshes(mesh_list):
    """Merge a list of (verts, faces) tuples into one mesh."""
    if not mesh_list:
        return None, None
    all_verts = []
    all_faces = []
    offset = 0
    for verts, faces in mesh_list:
        if verts is None or faces is None:
            continue
        all_verts.append(verts)
        all_faces.append(faces + offset)
        offset += len(verts)
    if not all_verts:
        return None, None
    return np.concatenate(all_verts, axis=0), np.concatenate(all_faces, axis=0)


def mirror_mesh_x(verts, faces):
    """Mirror mesh across X=0 plane. Flip X coords and reverse face winding."""
    new_verts = verts.copy()
    new_verts[:, 0] = -new_verts[:, 0]
    # Reverse winding order so normals point outward
    new_faces = faces.copy()
    new_faces[:, 1], new_faces[:, 2] = faces[:, 2].copy(), faces[:, 1].copy()
    return new_verts, new_faces


# ─────────────────────────────────────────────────────────────────
#  VOXELIZATION (pure numpy — no trimesh/Open3D required)
#
#  Strategy: for each Z-slice, rasterize triangles using scanline.
#  This is slower than GPU voxelization but works everywhere.
# ─────────────────────────────────────────────────────────────────

def voxelize_mesh(verts, faces, grid_size=256, fill_interior=True):
    """Voxelize a triangle mesh into a boolean 3D grid.

    Args:
        verts: Nx3 float array, expected in [-0.5, 0.5]³ range
        faces: Mx3 int array of triangle indices
        grid_size: voxel grid resolution per axis
        fill_interior: if True, flood-fill interior of closed surfaces

    Returns:
        bool array of shape (grid_size, grid_size, grid_size)
    """
    grid = np.zeros((grid_size, grid_size, grid_size), dtype=np.bool_)

    # Map vertex coords from [-0.5, 0.5] to [1, grid_size-2] (leave 1-voxel border)
    scale = grid_size - 3
    offset = grid_size / 2.0
    mapped = verts * scale + offset

    v0 = mapped[faces[:, 0]]
    v1 = mapped[faces[:, 1]]
    v2 = mapped[faces[:, 2]]

    # For each triangle, rasterize into the grid
    # Use bounding box per triangle, then point-in-triangle test
    num_tris = len(faces)

    # Process in batches for memory efficiency
    batch_size = 10000
    for batch_start in range(0, num_tris, batch_size):
        batch_end = min(batch_start + batch_size, num_tris)
        for ti in range(batch_start, batch_end):
            _rasterize_triangle(grid, v0[ti], v1[ti], v2[ti])

    # Surface voxelization done. Now fill interior if requested.
    if fill_interior and np.any(grid):
        grid = _flood_fill_interior(grid)

    return grid


def _rasterize_triangle(grid, p0, p1, p2):
    """Rasterize a single triangle into the voxel grid using
    3D DDA (thick line rasterization along edges + scanline fill)."""
    gs = grid.shape[0]

    # Bounding box
    mins = np.minimum(np.minimum(p0, p1), p2).astype(int)
    maxs = np.maximum(np.maximum(p0, p1), p2).astype(int) + 1
    mins = np.clip(mins, 0, gs - 1)
    maxs = np.clip(maxs, 0, gs - 1)

    # For small triangles, just fill the bounding box voxels that are near the surface
    for z in range(mins[2], maxs[2] + 1):
        for y in range(mins[1], maxs[1] + 1):
            for x in range(mins[0], maxs[0] + 1):
                if x < 0 or x >= gs or y < 0 or y >= gs or z < 0 or z >= gs:
                    continue
                pt = np.array([x + 0.5, y + 0.5, z + 0.5])
                if _point_near_triangle(pt, p0, p1, p2, threshold=1.0):
                    grid[z, y, x] = True


def _point_near_triangle(pt, p0, p1, p2, threshold=1.0):
    """Check if a point is within `threshold` distance of a triangle."""
    # Project point onto triangle plane
    edge1 = p1 - p0
    edge2 = p2 - p0
    normal = np.cross(edge1, edge2)
    normal_len = np.linalg.norm(normal)
    if normal_len < 1e-10:
        return False

    normal = normal / normal_len
    dist_to_plane = abs(np.dot(pt - p0, normal))
    if dist_to_plane > threshold:
        return False

    # Project onto plane and check barycentric coords
    proj = pt - dist_to_plane * normal * np.sign(np.dot(pt - p0, normal))
    v0 = edge1
    v1 = edge2
    v2 = proj - p0

    d00 = np.dot(v0, v0)
    d01 = np.dot(v0, v1)
    d11 = np.dot(v1, v1)
    d20 = np.dot(v2, v0)
    d21 = np.dot(v2, v1)

    denom = d00 * d11 - d01 * d01
    if abs(denom) < 1e-10:
        return False

    u = (d11 * d20 - d01 * d21) / denom
    v = (d00 * d21 - d01 * d20) / denom

    # Point is in triangle if u >= 0, v >= 0, u + v <= 1
    # Allow small margin for thick surface
    margin = threshold / max(np.linalg.norm(edge1), np.linalg.norm(edge2), 1.0) * 0.5
    return (u >= -margin) and (v >= -margin) and (u + v <= 1.0 + margin)


def _flood_fill_interior(grid):
    """Fill interior of a voxelized surface using Z-axis ray parity."""
    gs = grid.shape[0]
    filled = grid.copy()

    # For each (x, y) column, count surface crossings along Z
    for y in range(gs):
        for x in range(gs):
            col = grid[:, y, x]
            inside = False
            was_surface = False
            for z in range(gs):
                if col[z]:
                    if not was_surface:
                        inside = not inside
                    was_surface = True
                else:
                    was_surface = False
                    if inside:
                        filled[z, y, x] = True

    return filled


def _dilate_3d(mask, iterations=1):
    """Simple 3D binary dilation using 6-connectivity (no scipy needed)."""
    result = mask.copy()
    for _ in range(iterations):
        dilated = result.copy()
        dilated[1:, :, :] |= result[:-1, :, :]
        dilated[:-1, :, :] |= result[1:, :, :]
        dilated[:, 1:, :] |= result[:, :-1, :]
        dilated[:, :-1, :] |= result[:, 1:, :]
        dilated[:, :, 1:] |= result[:, :, :-1]
        dilated[:, :, :-1] |= result[:, :, 1:]
        result = dilated
    return result


def voxelize_mesh_fast(verts, faces, grid_size=256, fill_interior=True):
    """Faster voxelization using Z-buffer ray casting along each axis.
    Projects triangles onto XY, XZ, YZ planes and marks surface voxels,
    then uses parity fill. Much faster than per-triangle rasterization.

    Args:
        fill_interior: if True, fill the inside of closed surfaces using
                       multi-axis parity voting. Set False for tube-like
                       structures (vessels, nerves) that aren't watertight.
    """
    grid = np.zeros((grid_size, grid_size, grid_size), dtype=np.bool_)

    # Map vertices to grid coordinates
    scale = grid_size - 3
    offset = grid_size / 2.0
    mapped = verts * scale + offset

    v0 = mapped[faces[:, 0]]
    v1 = mapped[faces[:, 1]]
    v2 = mapped[faces[:, 2]]

    # For each Z slice, find triangles that intersect it, then
    # rasterize their cross-section as a 2D polygon
    tri_z_min = np.minimum(np.minimum(v0[:, 2], v1[:, 2]), v2[:, 2])
    tri_z_max = np.maximum(np.maximum(v0[:, 2], v1[:, 2]), v2[:, 2])

    print(f"    Voxelizing {len(faces)} triangles into {grid_size}³ grid...")
    report_interval = max(1, grid_size // 10)

    for z in range(grid_size):
        if z % report_interval == 0:
            print(f"    Slice {z}/{grid_size}...")

        # Find triangles intersecting this Z slice
        mask = (tri_z_min <= z + 0.5) & (tri_z_max >= z + 0.5)
        if not np.any(mask):
            continue

        zval = z + 0.5
        sel_v0 = v0[mask]
        sel_v1 = v1[mask]
        sel_v2 = v2[mask]

        # For each intersecting triangle, compute the line segment
        # where the Z=zval plane intersects the triangle, then
        # mark those pixels in the XY slice
        slice_2d = grid[z]

        for i in range(len(sel_v0)):
            pts = _triangle_z_intersection(sel_v0[i], sel_v1[i], sel_v2[i], zval)
            if len(pts) >= 2:
                _draw_line_2d(slice_2d, pts[0], pts[1], grid_size)
            elif len(pts) == 1:
                ix, iy = int(pts[0][0]), int(pts[0][1])
                if 0 <= ix < grid_size and 0 <= iy < grid_size:
                    slice_2d[iy, ix] = True

    if not fill_interior:
        # For tube-like structures, thicken the surface by 2 voxels
        # so thin tubes are clearly visible in volume rendering
        print("    Thickening surface (no fill)...")
        grid = _dilate_3d(grid, iterations=2)
        return grid

    # Fill interior using multi-axis parity voting.
    # Single-axis scanline fill creates streak artifacts when the surface
    # has gaps. Instead, we cast rays along all 3 axes and only fill a
    # voxel if at least 2 out of 3 axes agree it's interior.
    print("    Filling interior volumes...")

    def _scanline_fill_axis(grid_3d, axis):
        """Return interior mask by scanline parity along the given axis."""
        gs = grid_3d.shape[0]
        interior = np.zeros_like(grid_3d)
        for a in range(gs):
            for b in range(gs):
                if axis == 0:
                    line = grid_3d[:, a, b]
                elif axis == 1:
                    line = grid_3d[a, :, b]
                else:
                    line = grid_3d[a, b, :]

                inside = False
                was_on = False
                for i in range(gs):
                    if line[i]:
                        if not was_on:
                            inside = not inside
                        was_on = True
                    else:
                        was_on = False
                        if inside:
                            if axis == 0:
                                interior[i, a, b] = True
                            elif axis == 1:
                                interior[a, i, b] = True
                            else:
                                interior[a, b, i] = True
        return interior

    fill_x = _scanline_fill_axis(grid, 0)
    fill_y = _scanline_fill_axis(grid, 1)
    fill_z = _scanline_fill_axis(grid, 2)

    # Majority vote: fill only where >= 2 axes agree
    votes = fill_x.astype(np.int8) + fill_y.astype(np.int8) + fill_z.astype(np.int8)
    grid = grid | (votes >= 2)

    return grid


def _triangle_z_intersection(v0, v1, v2, z):
    """Find intersection points of triangle with plane Z=z.
    Returns list of 2D points (x, y)."""
    edges = [(v0, v1), (v1, v2), (v2, v0)]
    pts = []
    for a, b in edges:
        if (a[2] <= z and b[2] >= z) or (a[2] >= z and b[2] <= z):
            dz = b[2] - a[2]
            if abs(dz) < 1e-10:
                # Edge is in the plane — add both endpoints
                pts.append((a[0], a[1]))
                pts.append((b[0], b[1]))
            else:
                t = (z - a[2]) / dz
                t = max(0.0, min(1.0, t))
                x = a[0] + t * (b[0] - a[0])
                y = a[1] + t * (b[1] - a[1])
                pts.append((x, y))
    return pts


def _draw_line_2d(slice_2d, p0, p1, gs):
    """Draw a line on a 2D boolean grid using Bresenham's."""
    x0, y0 = int(p0[0]), int(p0[1])
    x1, y1 = int(p1[0]), int(p1[1])

    dx = abs(x1 - x0)
    dy = abs(y1 - y0)
    sx = 1 if x0 < x1 else -1
    sy = 1 if y0 < y1 else -1
    err = dx - dy

    while True:
        if 0 <= x0 < gs and 0 <= y0 < gs:
            slice_2d[y0, x0] = True
        if x0 == x1 and y0 == y1:
            break
        e2 = 2 * err
        if e2 > -dy:
            err -= dy
            x0 += sx
        if e2 < dx:
            err += dx
            y0 += sy


# ─────────────────────────────────────────────────────────────────
#  OVOL EXPORT (matches dicom_processor.py exactly)
# ─────────────────────────────────────────────────────────────────

def export_ovol(volume_uint8, output_path):
    """Export volume as .vol file (OVOL v1 — 32-byte header)."""
    D, H, W = volume_uint8.shape  # Z, Y, X

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
        f.write(volume_uint8.tobytes())

    size_mb = os.path.getsize(output_path) / 1024 / 1024
    print(f"  Exported: {os.path.basename(output_path)} ({size_mb:.1f} MB)")
    return size_mb


# ─────────────────────────────────────────────────────────────────
#  MESH EXPORT (matches _write_omsh in dicom_processor.py)
# ─────────────────────────────────────────────────────────────────

def write_mesh_file(path, verts, faces, magic=b'OMSH'):
    """Write .omsh / .vmsh / .nmsh / .mmsh binary file.
    Format: magic(4) version(4) num_verts(4) num_tris(4)
            verts(N*12) normals(N*12) faces(M*12)
    Coordinates in [-0.5, 0.5]³ matching volume cube."""
    normals = compute_vertex_normals(verts, faces)
    verts   = np.ascontiguousarray(verts,   dtype=np.float32)
    normals = np.ascontiguousarray(normals, dtype=np.float32)
    faces_u = np.ascontiguousarray(faces,   dtype=np.uint32)

    header = struct.pack('<4sIII', magic, 1, len(verts), len(faces_u))
    with open(path, 'wb') as f:
        f.write(header)
        f.write(verts.tobytes())
        f.write(normals.tobytes())
        f.write(faces_u.tobytes())

    size_kb = os.path.getsize(path) / 1024
    print(f"  Exported: {os.path.basename(path)} ({len(verts)} verts, "
          f"{len(faces_u)} tris, {size_kb:.0f} KB)")


# ─────────────────────────────────────────────────────────────────
#  PROCEDURAL ANATOMY GENERATOR (for testing without downloads)
#
#  Creates anatomically-shaped geometry for each tissue type:
#  - Bones: cylinders + spherical joints
#  - Muscles: elongated ellipsoids wrapping bones
#  - Vessels: branching tubes
#  - Nerves: thinner branching tubes
# ─────────────────────────────────────────────────────────────────

def make_sphere(center, radius, rings=16, sectors=16):
    """Generate a UV sphere mesh."""
    verts = []
    faces = []

    for i in range(rings + 1):
        phi = math.pi * i / rings
        for j in range(sectors + 1):
            theta = 2.0 * math.pi * j / sectors
            x = center[0] + radius * math.sin(phi) * math.cos(theta)
            y = center[1] + radius * math.sin(phi) * math.sin(theta)
            z = center[2] + radius * math.cos(phi)
            verts.append([x, y, z])

    for i in range(rings):
        for j in range(sectors):
            a = i * (sectors + 1) + j
            b = a + sectors + 1
            faces.append([a, b, a + 1])
            faces.append([a + 1, b, b + 1])

    return np.array(verts, dtype=np.float32), np.array(faces, dtype=np.int32)


def make_cylinder(p0, p1, radius, segments=16):
    """Generate a capped cylinder between two points."""
    p0 = np.array(p0, dtype=np.float32)
    p1 = np.array(p1, dtype=np.float32)
    axis = p1 - p0
    length = np.linalg.norm(axis)
    if length < 1e-10:
        return make_sphere(p0, radius, 8, 8)

    axis_n = axis / length
    # Find perpendicular vectors
    if abs(axis_n[0]) < 0.9:
        perp = np.cross(axis_n, [1, 0, 0])
    else:
        perp = np.cross(axis_n, [0, 1, 0])
    perp = perp / np.linalg.norm(perp)
    perp2 = np.cross(axis_n, perp)

    verts = []
    faces = []

    # Bottom cap center
    verts.append(p0.tolist())
    # Bottom ring
    for i in range(segments):
        theta = 2 * math.pi * i / segments
        pt = p0 + radius * (math.cos(theta) * perp + math.sin(theta) * perp2)
        verts.append(pt.tolist())
    # Top ring
    for i in range(segments):
        theta = 2 * math.pi * i / segments
        pt = p1 + radius * (math.cos(theta) * perp + math.sin(theta) * perp2)
        verts.append(pt.tolist())
    # Top cap center
    top_center = len(verts)
    verts.append(p1.tolist())

    # Bottom cap faces
    for i in range(segments):
        j = (i + 1) % segments
        faces.append([0, 1 + j, 1 + i])

    # Side faces
    for i in range(segments):
        j = (i + 1) % segments
        b0 = 1 + i
        b1 = 1 + j
        t0 = 1 + segments + i
        t1 = 1 + segments + j
        faces.append([b0, t0, b1])
        faces.append([b1, t0, t1])

    # Top cap faces
    for i in range(segments):
        j = (i + 1) % segments
        faces.append([top_center, 1 + segments + i, 1 + segments + j])

    return np.array(verts, dtype=np.float32), np.array(faces, dtype=np.int32)


def make_ellipsoid(center, radii, rings=12, sectors=12):
    """Generate an ellipsoid mesh with different radii per axis."""
    verts = []
    faces = []
    rx, ry, rz = radii

    for i in range(rings + 1):
        phi = math.pi * i / rings
        for j in range(sectors + 1):
            theta = 2.0 * math.pi * j / sectors
            x = center[0] + rx * math.sin(phi) * math.cos(theta)
            y = center[1] + ry * math.sin(phi) * math.sin(theta)
            z = center[2] + rz * math.cos(phi)
            verts.append([x, y, z])

    for i in range(rings):
        for j in range(sectors):
            a = i * (sectors + 1) + j
            b = a + sectors + 1
            faces.append([a, b, a + 1])
            faces.append([a + 1, b, b + 1])

    return np.array(verts, dtype=np.float32), np.array(faces, dtype=np.int32)


def make_tube(points, radius, segments=8):
    """Generate a tube following a path of 3D points with smooth interpolation."""
    if len(points) < 2:
        return None, None

    # Interpolate path for smoother tubes (Catmull-Rom style subdivision)
    smooth_points = []
    for i in range(len(points) - 1):
        p0 = np.array(points[i], dtype=np.float32)
        p1 = np.array(points[i + 1], dtype=np.float32)
        # Add 3 intermediate points per segment
        for t in [0.0, 0.33, 0.67]:
            smooth_points.append((p0 + t * (p1 - p0)).tolist())
    smooth_points.append(points[-1])

    meshes = []
    for i in range(len(smooth_points) - 1):
        # Taper radius slightly at endpoints for more natural look
        t_start = i / max(len(smooth_points) - 1, 1)
        t_end = (i + 1) / max(len(smooth_points) - 1, 1)
        taper = lambda t: radius * (1.0 - 0.3 * abs(2.0 * t - 1.0))
        r = (taper(t_start) + taper(t_end)) / 2.0
        cyl_v, cyl_f = make_cylinder(smooth_points[i], smooth_points[i+1], r, segments)
        meshes.append((cyl_v, cyl_f))
    # Add sphere caps at endpoints
    sv, sf = make_sphere(smooth_points[0], radius * 0.8, 6, 6)
    meshes.append((sv, sf))
    sv, sf = make_sphere(smooth_points[-1], radius * 0.6, 6, 6)
    meshes.append((sv, sf))
    return merge_meshes(meshes)


# ─── Procedural anatomy per region ────────────────────────────

def generate_shoulder_anatomy():
    """Generate procedural shoulder anatomy meshes."""
    bones, muscles, vessels, nerves = [], [], [], []

    # -- Bones --
    # Humerus (long bone going down)
    v, f = make_cylinder([0, -0.05, 0.15], [0, -0.05, -0.25], 0.04, 16)
    bones.append((v, f))
    # Humeral head (ball joint)
    v, f = make_sphere([0, -0.05, 0.18], 0.065, 16, 16)
    bones.append((v, f))
    # Scapula (flat triangular bone)
    v, f = make_ellipsoid([-0.08, 0.04, 0.15], [0.12, 0.02, 0.15], 12, 12)
    bones.append((v, f))
    # Clavicle
    v, f = make_cylinder([-0.15, -0.02, 0.28], [0.12, -0.02, 0.25], 0.018, 12)
    bones.append((v, f))
    # Acromion
    v, f = make_ellipsoid([-0.02, -0.01, 0.26], [0.04, 0.015, 0.02], 8, 8)
    bones.append((v, f))

    # -- Muscles --
    # Deltoid (cap over shoulder)
    v, f = make_ellipsoid([0.0, -0.06, 0.10], [0.10, 0.08, 0.12], 12, 12)
    muscles.append((v, f))
    # Supraspinatus
    v, f = make_ellipsoid([-0.06, 0.02, 0.24], [0.08, 0.02, 0.025], 10, 10)
    muscles.append((v, f))
    # Infraspinatus
    v, f = make_ellipsoid([-0.08, 0.06, 0.12], [0.09, 0.025, 0.08], 10, 10)
    muscles.append((v, f))
    # Subscapularis
    v, f = make_ellipsoid([-0.06, -0.01, 0.12], [0.07, 0.02, 0.08], 10, 10)
    muscles.append((v, f))
    # Biceps
    v, f = make_ellipsoid([0.02, -0.07, -0.05], [0.035, 0.04, 0.12], 10, 10)
    muscles.append((v, f))
    # Triceps
    v, f = make_ellipsoid([-0.02, 0.02, -0.05], [0.035, 0.04, 0.12], 10, 10)
    muscles.append((v, f))
    # Pectoralis (partial)
    v, f = make_ellipsoid([0.08, -0.06, 0.15], [0.06, 0.03, 0.10], 10, 10)
    muscles.append((v, f))
    # Teres minor
    v, f = make_ellipsoid([-0.05, 0.04, 0.06], [0.05, 0.015, 0.02], 8, 8)
    muscles.append((v, f))

    # -- Vessels --
    # Axillary artery
    v, f = make_tube([[0.05, -0.03, 0.28], [0.02, -0.04, 0.22],
                       [0.0, -0.04, 0.15], [0.0, -0.05, 0.05]], 0.012, 8)
    vessels.append((v, f))
    # Brachial artery (continues down)
    v, f = make_tube([[0.0, -0.05, 0.05], [0.01, -0.05, -0.05],
                       [0.01, -0.05, -0.20]], 0.010, 8)
    vessels.append((v, f))
    # Cephalic vein
    v, f = make_tube([[0.06, -0.08, -0.15], [0.05, -0.07, 0.0],
                       [0.04, -0.06, 0.15], [0.03, -0.04, 0.25]], 0.008, 8)
    vessels.append((v, f))
    # Circumflex humeral
    v, f = make_tube([[0.0, -0.04, 0.15], [-0.04, -0.02, 0.17],
                       [-0.06, 0.02, 0.16]], 0.006, 6)
    vessels.append((v, f))

    # -- Nerves --
    # Brachial plexus bundle
    v, f = make_tube([[0.06, -0.01, 0.30], [0.04, -0.02, 0.25],
                       [0.02, -0.03, 0.20], [0.01, -0.04, 0.15]], 0.010, 6)
    nerves.append((v, f))
    # Axillary nerve
    v, f = make_tube([[0.01, -0.04, 0.15], [-0.02, -0.02, 0.17],
                       [-0.05, 0.01, 0.15]], 0.005, 6)
    nerves.append((v, f))
    # Radial nerve
    v, f = make_tube([[0.01, -0.04, 0.15], [0.0, 0.0, 0.05],
                       [-0.01, 0.01, -0.10]], 0.005, 6)
    nerves.append((v, f))
    # Median nerve
    v, f = make_tube([[0.02, -0.03, 0.20], [0.02, -0.05, 0.10],
                       [0.02, -0.05, -0.10], [0.02, -0.05, -0.25]], 0.004, 6)
    nerves.append((v, f))

    return {
        "bone": merge_meshes(bones),
        "muscle": merge_meshes(muscles),
        "vessel": merge_meshes(vessels),
        "nerve": merge_meshes(nerves),
    }


def generate_hip_anatomy():
    """Generate procedural hip anatomy meshes."""
    bones, muscles, vessels, nerves = [], [], [], []

    # -- Bones --
    # Pelvis (simplified as wide ellipsoid)
    v, f = make_ellipsoid([0, 0, 0.18], [0.18, 0.10, 0.12], 14, 14)
    bones.append((v, f))
    # Femoral head
    v, f = make_sphere([0.08, 0, 0.06], 0.05, 14, 14)
    bones.append((v, f))
    # Femoral neck + shaft
    v, f = make_cylinder([0.08, 0, 0.06], [0.06, 0, -0.30], 0.03, 14)
    bones.append((v, f))
    # Greater trochanter
    v, f = make_ellipsoid([0.10, 0, 0.02], [0.03, 0.025, 0.04], 10, 10)
    bones.append((v, f))
    # Sacrum
    v, f = make_ellipsoid([-0.08, 0.02, 0.22], [0.05, 0.03, 0.06], 10, 10)
    bones.append((v, f))

    # -- Muscles --
    # Gluteus maximus
    v, f = make_ellipsoid([-0.02, 0.10, 0.10], [0.12, 0.06, 0.12], 12, 12)
    muscles.append((v, f))
    # Gluteus medius
    v, f = make_ellipsoid([0.02, 0.06, 0.18], [0.10, 0.04, 0.08], 10, 10)
    muscles.append((v, f))
    # Iliopsoas
    v, f = make_ellipsoid([0.06, -0.04, 0.08], [0.04, 0.03, 0.12], 10, 10)
    muscles.append((v, f))
    # Adductors
    v, f = make_ellipsoid([0.04, -0.02, -0.08], [0.04, 0.06, 0.14], 10, 10)
    muscles.append((v, f))
    # Tensor fasciae latae
    v, f = make_ellipsoid([0.12, -0.02, 0.05], [0.025, 0.02, 0.08], 8, 8)
    muscles.append((v, f))
    # Piriformis
    v, f = make_ellipsoid([-0.02, 0.04, 0.10], [0.06, 0.02, 0.025], 8, 8)
    muscles.append((v, f))
    # Rectus femoris (upper part)
    v, f = make_ellipsoid([0.06, -0.04, -0.10], [0.035, 0.04, 0.14], 10, 10)
    muscles.append((v, f))

    # -- Vessels --
    # Femoral artery
    v, f = make_tube([[0.04, -0.04, 0.15], [0.05, -0.04, 0.05],
                       [0.05, -0.03, -0.10], [0.04, -0.02, -0.25]], 0.012, 8)
    vessels.append((v, f))
    # Deep femoral artery
    v, f = make_tube([[0.05, -0.04, 0.05], [0.07, -0.01, -0.05],
                       [0.08, 0.02, -0.15]], 0.008, 6)
    vessels.append((v, f))
    # Femoral vein
    v, f = make_tube([[0.03, -0.03, 0.15], [0.04, -0.03, 0.05],
                       [0.04, -0.02, -0.10], [0.03, -0.01, -0.25]], 0.010, 8)
    vessels.append((v, f))
    # Great saphenous vein
    v, f = make_tube([[0.04, -0.06, -0.25], [0.05, -0.06, -0.10],
                       [0.06, -0.06, 0.05], [0.05, -0.05, 0.15]], 0.006, 6)
    vessels.append((v, f))

    # -- Nerves --
    # Sciatic nerve
    v, f = make_tube([[-0.02, 0.06, 0.06], [0.02, 0.06, -0.02],
                       [0.04, 0.04, -0.15], [0.04, 0.03, -0.28]], 0.010, 6)
    nerves.append((v, f))
    # Femoral nerve
    v, f = make_tube([[0.04, -0.03, 0.18], [0.05, -0.04, 0.10],
                       [0.05, -0.04, 0.0], [0.06, -0.04, -0.10]], 0.006, 6)
    nerves.append((v, f))
    # Obturator nerve
    v, f = make_tube([[0.0, -0.02, 0.12], [0.03, -0.02, 0.05],
                       [0.03, -0.02, -0.05]], 0.004, 6)
    nerves.append((v, f))

    return {
        "bone": merge_meshes(bones),
        "muscle": merge_meshes(muscles),
        "vessel": merge_meshes(vessels),
        "nerve": merge_meshes(nerves),
    }


def generate_thigh_anatomy():
    """Generate procedural thigh anatomy meshes."""
    bones, muscles, vessels, nerves = [], [], [], []

    # -- Bones --
    # Femoral shaft
    v, f = make_cylinder([0, 0, 0.35], [0, 0, -0.30], 0.035, 16)
    bones.append((v, f))
    # Femoral condyles (distal)
    v, f = make_ellipsoid([0.02, 0, -0.30], [0.04, 0.035, 0.03], 10, 10)
    bones.append((v, f))
    v, f = make_ellipsoid([-0.02, 0, -0.30], [0.04, 0.035, 0.03], 10, 10)
    bones.append((v, f))

    # -- Muscles --
    # Rectus femoris (center front)
    v, f = make_ellipsoid([0, -0.05, 0.05], [0.04, 0.045, 0.25], 12, 12)
    muscles.append((v, f))
    # Vastus lateralis
    v, f = make_ellipsoid([0.06, -0.02, 0.0], [0.04, 0.04, 0.22], 12, 12)
    muscles.append((v, f))
    # Vastus medialis
    v, f = make_ellipsoid([-0.05, -0.03, -0.02], [0.035, 0.04, 0.20], 12, 12)
    muscles.append((v, f))
    # Vastus intermedius
    v, f = make_ellipsoid([0, -0.03, 0.02], [0.03, 0.03, 0.20], 10, 10)
    muscles.append((v, f))
    # Biceps femoris (posterior lateral)
    v, f = make_ellipsoid([0.05, 0.05, 0.0], [0.035, 0.035, 0.22], 12, 12)
    muscles.append((v, f))
    # Semitendinosus (posterior medial)
    v, f = make_ellipsoid([-0.03, 0.05, 0.0], [0.025, 0.025, 0.24], 10, 10)
    muscles.append((v, f))
    # Semimembranosus
    v, f = make_ellipsoid([-0.04, 0.04, 0.02], [0.03, 0.03, 0.20], 10, 10)
    muscles.append((v, f))
    # Sartorius (diagonal strap)
    v, f = make_tube([[-0.04, -0.06, 0.30], [-0.02, -0.06, 0.15],
                       [-0.04, -0.04, 0.0], [-0.06, -0.02, -0.15],
                       [-0.06, 0.0, -0.28]], 0.015, 8)
    muscles.append((v, f))
    # Gracilis
    v, f = make_ellipsoid([-0.06, 0.0, 0.0], [0.015, 0.02, 0.25], 8, 8)
    muscles.append((v, f))
    # Adductor magnus
    v, f = make_ellipsoid([-0.04, 0.02, 0.10], [0.04, 0.04, 0.18], 10, 10)
    muscles.append((v, f))

    # -- Vessels --
    # Femoral artery
    v, f = make_tube([[0, -0.04, 0.35], [0.01, -0.04, 0.20],
                       [0.01, -0.03, 0.0], [0, 0, -0.15],
                       [0, 0.02, -0.28]], 0.012, 8)
    vessels.append((v, f))
    # Deep femoral artery
    v, f = make_tube([[0.01, -0.04, 0.20], [0.04, -0.01, 0.10],
                       [0.05, 0.02, -0.05]], 0.008, 6)
    vessels.append((v, f))
    # Femoral vein
    v, f = make_tube([[-0.01, -0.03, 0.35], [-0.01, -0.03, 0.15],
                       [0, -0.02, 0.0], [0.01, 0.01, -0.15],
                       [0.01, 0.03, -0.28]], 0.010, 8)
    vessels.append((v, f))
    # Perforating arteries
    for z_off in [0.15, 0.0, -0.12]:
        v, f = make_tube([[0.03, -0.02, z_off], [0.06, 0.02, z_off - 0.03]], 0.005, 6)
        vessels.append((v, f))

    # -- Nerves --
    # Sciatic nerve (posterior)
    v, f = make_tube([[0.02, 0.06, 0.35], [0.02, 0.05, 0.15],
                       [0.02, 0.04, 0.0], [0.02, 0.04, -0.15],
                       [0.02, 0.04, -0.28]], 0.010, 6)
    nerves.append((v, f))
    # Femoral nerve (anterior)
    v, f = make_tube([[0.02, -0.05, 0.35], [0.02, -0.05, 0.25],
                       [0.01, -0.04, 0.15], [0.0, -0.04, 0.05]], 0.006, 6)
    nerves.append((v, f))
    # Saphenous nerve (branch of femoral, medial)
    v, f = make_tube([[0.0, -0.04, 0.05], [-0.03, -0.05, -0.05],
                       [-0.05, -0.05, -0.20], [-0.05, -0.04, -0.30]], 0.004, 6)
    nerves.append((v, f))

    return {
        "bone": merge_meshes(bones),
        "muscle": merge_meshes(muscles),
        "vessel": merge_meshes(vessels),
        "nerve": merge_meshes(nerves),
    }


def generate_knee_anatomy():
    """Generate procedural knee anatomy meshes."""
    bones, muscles, vessels, nerves = [], [], [], []

    # -- Bones --
    # Distal femur
    v, f = make_cylinder([0, 0, 0.30], [0, 0, 0.02], 0.035, 14)
    bones.append((v, f))
    # Femoral condyles
    v, f = make_ellipsoid([0.03, 0, 0.0], [0.035, 0.035, 0.035], 12, 12)
    bones.append((v, f))
    v, f = make_ellipsoid([-0.03, 0, 0.0], [0.035, 0.035, 0.035], 12, 12)
    bones.append((v, f))
    # Patella
    v, f = make_ellipsoid([0, -0.06, 0.02], [0.025, 0.015, 0.03], 10, 10)
    bones.append((v, f))
    # Proximal tibia
    v, f = make_cylinder([0, 0, -0.02], [0, 0, -0.32], 0.032, 14)
    bones.append((v, f))
    # Tibial plateau
    v, f = make_ellipsoid([0, 0, -0.02], [0.05, 0.04, 0.02], 10, 10)
    bones.append((v, f))
    # Fibula
    v, f = make_cylinder([0.06, 0.01, -0.04], [0.05, 0.01, -0.32], 0.012, 10)
    bones.append((v, f))

    # -- Muscles --
    # Quadriceps tendon / patellar tendon region
    v, f = make_ellipsoid([0, -0.05, 0.10], [0.04, 0.03, 0.08], 10, 10)
    muscles.append((v, f))
    # Vastus medialis (low fibers)
    v, f = make_ellipsoid([-0.05, -0.03, 0.06], [0.03, 0.03, 0.08], 10, 10)
    muscles.append((v, f))
    # Vastus lateralis (low fibers)
    v, f = make_ellipsoid([0.05, -0.03, 0.06], [0.03, 0.03, 0.08], 10, 10)
    muscles.append((v, f))
    # Gastrocnemius medial head
    v, f = make_ellipsoid([-0.03, 0.04, -0.10], [0.035, 0.035, 0.12], 10, 10)
    muscles.append((v, f))
    # Gastrocnemius lateral head
    v, f = make_ellipsoid([0.03, 0.04, -0.10], [0.035, 0.035, 0.12], 10, 10)
    muscles.append((v, f))
    # Popliteus
    v, f = make_ellipsoid([0, 0.03, -0.04], [0.04, 0.02, 0.03], 8, 8)
    muscles.append((v, f))
    # Hamstring tendons (posterior, upper)
    v, f = make_ellipsoid([0, 0.05, 0.05], [0.04, 0.02, 0.06], 10, 10)
    muscles.append((v, f))
    # Tibialis anterior (upper portion)
    v, f = make_ellipsoid([0.02, -0.04, -0.15], [0.025, 0.025, 0.10], 10, 10)
    muscles.append((v, f))

    # -- Vessels --
    # Popliteal artery (behind knee)
    v, f = make_tube([[0, 0.04, 0.20], [0, 0.05, 0.10],
                       [0, 0.05, 0.0], [0, 0.04, -0.08]], 0.010, 8)
    vessels.append((v, f))
    # Anterior tibial artery
    v, f = make_tube([[0, 0.04, -0.08], [0.02, 0.0, -0.12],
                       [0.02, -0.03, -0.20], [0.02, -0.03, -0.30]], 0.007, 6)
    vessels.append((v, f))
    # Posterior tibial artery
    v, f = make_tube([[0, 0.04, -0.08], [-0.01, 0.03, -0.15],
                       [-0.01, 0.03, -0.25]], 0.007, 6)
    vessels.append((v, f))
    # Popliteal vein
    v, f = make_tube([[0.01, 0.05, 0.20], [0.01, 0.06, 0.10],
                       [0.01, 0.06, 0.0], [0.01, 0.05, -0.08]], 0.009, 8)
    vessels.append((v, f))
    # Genicular arteries (small branches around knee)
    v, f = make_tube([[0, 0.05, 0.02], [0.05, 0.03, 0.01],
                       [0.06, 0.0, 0.0]], 0.004, 6)
    vessels.append((v, f))
    v, f = make_tube([[0, 0.05, -0.02], [-0.05, 0.03, -0.01],
                       [-0.06, 0.0, -0.02]], 0.004, 6)
    vessels.append((v, f))

    # -- Nerves --
    # Tibial nerve
    v, f = make_tube([[0, 0.06, 0.20], [0, 0.06, 0.10],
                       [0, 0.06, 0.0], [-0.01, 0.05, -0.10],
                       [-0.01, 0.04, -0.25]], 0.006, 6)
    nerves.append((v, f))
    # Common peroneal nerve
    v, f = make_tube([[0.02, 0.06, 0.10], [0.04, 0.05, 0.02],
                       [0.06, 0.03, -0.04], [0.06, 0.01, -0.08]], 0.005, 6)
    nerves.append((v, f))
    # Deep peroneal nerve
    v, f = make_tube([[0.06, 0.01, -0.08], [0.04, -0.02, -0.15],
                       [0.03, -0.03, -0.25]], 0.004, 6)
    nerves.append((v, f))
    # Saphenous nerve
    v, f = make_tube([[-0.04, -0.04, 0.15], [-0.05, -0.04, 0.05],
                       [-0.06, -0.03, -0.05], [-0.06, -0.03, -0.20]], 0.004, 6)
    nerves.append((v, f))

    return {
        "bone": merge_meshes(bones),
        "muscle": merge_meshes(muscles),
        "vessel": merge_meshes(vessels),
        "nerve": merge_meshes(nerves),
    }


def generate_spine_anatomy():
    """Generate procedural spine anatomy meshes (C2–sacrum).

    All dimensions scaled ~2.5x from anatomical proportions to fill the
    [-0.5,0.5]³ voxel volume comparably to the other procedural regions
    (shoulder, hip, etc.), which use similarly inflated radii.
    """
    bones, muscles, vessels, nerves = [], [], [], []

    # -- Bones: vertebral column C2–sacrum --
    # 24 vertebrae (7 cervical from C2, 12 thoracic, 5 lumbar) + sacrum
    # Spine runs from z=+0.40 (C2) down to z~-0.40 (sacrum)
    vertebra_z = []
    z = 0.40
    # C2–C7 (6 cervical)
    for i in range(6):
        # Vertebral body
        v, f = make_ellipsoid([0, 0, z], [0.045, 0.055, 0.025], 12, 12)
        bones.append((v, f))
        # Spinous process
        v2, f2 = make_ellipsoid([0, 0.040, z], [0.015, 0.030, 0.012], 8, 8)
        bones.append((v2, f2))
        # Transverse processes (cervical — smaller)
        v3, f3 = make_ellipsoid([0.050, 0.020, z], [0.025, 0.012, 0.010], 8, 8)
        bones.append((v3, f3))
        v4, f4 = make_ellipsoid([-0.050, 0.020, z], [0.025, 0.012, 0.010], 8, 8)
        bones.append((v4, f4))
        vertebra_z.append(z)
        z -= 0.032

    # T1–T12 (12 thoracic, medium)
    for i in range(12):
        v, f = make_ellipsoid([0, 0, z], [0.055, 0.065, 0.028], 12, 12)
        bones.append((v, f))
        # Spinous process (thoracic — angled down)
        v2, f2 = make_ellipsoid([0, 0.050, z - 0.008], [0.015, 0.035, 0.012], 8, 8)
        bones.append((v2, f2))
        # Transverse processes
        v3, f3 = make_ellipsoid([0.070, 0.025, z], [0.035, 0.014, 0.012], 8, 8)
        bones.append((v3, f3))
        v4, f4 = make_ellipsoid([-0.070, 0.025, z], [0.035, 0.014, 0.012], 8, 8)
        bones.append((v4, f4))
        vertebra_z.append(z)
        z -= 0.034

    # L1–L5 (5 lumbar, largest)
    for i in range(5):
        v, f = make_ellipsoid([0, 0, z], [0.070, 0.080, 0.032], 12, 12)
        bones.append((v, f))
        # Spinous process (lumbar — broad and flat)
        v2, f2 = make_ellipsoid([0, 0.055, z], [0.020, 0.038, 0.014], 8, 8)
        bones.append((v2, f2))
        # Transverse processes (lumbar — long)
        v3, f3 = make_ellipsoid([0.085, 0.020, z], [0.040, 0.014, 0.014], 8, 8)
        bones.append((v3, f3))
        v4, f4 = make_ellipsoid([-0.085, 0.020, z], [0.040, 0.014, 0.014], 8, 8)
        bones.append((v4, f4))
        vertebra_z.append(z)
        z -= 0.036

    # Sacrum (fused triangular mass)
    sacrum_z = z
    v, f = make_ellipsoid([0, 0, sacrum_z], [0.080, 0.060, 0.075], 12, 12)
    bones.append((v, f))

    # -- Muscles: paraspinal (erector spinae, multifidus bilaterally) --
    for side in [1, -1]:
        # Iliocostalis (lateral column — thick)
        v, f = make_tube([
            [side * 0.10, 0.035, 0.35],
            [side * 0.11, 0.040, 0.10],
            [side * 0.10, 0.045, -0.10],
            [side * 0.09, 0.035, -0.35],
        ], 0.035, 10)
        muscles.append((v, f))
        # Longissimus (intermediate column)
        v, f = make_tube([
            [side * 0.065, 0.050, 0.35],
            [side * 0.070, 0.058, 0.10],
            [side * 0.068, 0.060, -0.10],
            [side * 0.065, 0.050, -0.35],
        ], 0.030, 10)
        muscles.append((v, f))
        # Multifidus (deep, close to spinous processes)
        v, f = make_tube([
            [side * 0.030, 0.055, 0.25],
            [side * 0.035, 0.065, 0.0],
            [side * 0.035, 0.068, -0.20],
            [side * 0.030, 0.055, -0.38],
        ], 0.022, 10)
        muscles.append((v, f))

    # Quadratus lumborum (bilateral, lumbar region only)
    for side in [1, -1]:
        v, f = make_ellipsoid([side * 0.11, 0.010, -0.22],
                               [0.035, 0.025, 0.10], 10, 10)
        muscles.append((v, f))

    # -- Vessels: vertebral arteries (bilateral) --
    for side in [1, -1]:
        v, f = make_tube([
            [side * 0.040, -0.025, 0.40],
            [side * 0.035, -0.020, 0.30],
            [side * 0.030, -0.012, 0.15],
        ], 0.012, 8)
        vessels.append((v, f))

    # Anterior spinal artery (midline, along ventral cord)
    v, f = make_tube([
        [0, -0.030, 0.40], [0, -0.030, 0.15],
        [0, -0.025, -0.05], [0, -0.020, -0.30],
    ], 0.008, 8)
    vessels.append((v, f))

    # Segmental / lumbar arteries (bilateral branches at thoracic levels)
    for i, vz in enumerate(vertebra_z[6:18]):  # thoracic region
        if i % 2 == 0:
            for side in [1, -1]:
                v, f = make_tube([
                    [0, -0.012, vz],
                    [side * 0.080, -0.012, vz],
                ], 0.007, 6)
                vessels.append((v, f))

    # -- Nerves: spinal cord + nerve roots --
    # Spinal cord (midline — substantial tube)
    v, f = make_tube([
        [0, 0.012, 0.40], [0, 0.012, 0.20],
        [0, 0.010, 0.0], [0, 0.008, -0.15],
        [0, 0.005, -0.22],  # conus medullaris
    ], 0.018, 10)
    nerves.append((v, f))

    # Cauda equina (fans out below conus)
    for offset in [-0.025, -0.010, 0.010, 0.025]:
        v, f = make_tube([
            [offset * 0.5, 0.005, -0.22],
            [offset, 0.003, -0.30],
            [offset * 1.5, 0.0, -0.40],
        ], 0.010, 8)
        nerves.append((v, f))

    # Spinal nerve roots (bilateral, exiting at each vertebral level)
    for vz in vertebra_z[::2]:
        for side in [1, -1]:
            v, f = make_tube([
                [0, 0.012, vz],
                [side * 0.055, 0.008, vz - 0.010],
                [side * 0.095, 0.0, vz - 0.020],
            ], 0.008, 8)
            nerves.append((v, f))

    return {
        "bone": merge_meshes(bones),
        "muscle": merge_meshes(muscles),
        "vessel": merge_meshes(vessels),
        "nerve": merge_meshes(nerves),
    }


PROCEDURAL_GENERATORS = {
    "shoulder": generate_shoulder_anatomy,
    "hip": generate_hip_anatomy,
    "thigh": generate_thigh_anatomy,
    "knee": generate_knee_anatomy,
    "spine": generate_spine_anatomy,
}


# ─────────────────────────────────────────────────────────────────
#  BODYPARTS3D LOADER
# ─────────────────────────────────────────────────────────────────

def download_bodyparts3d(cache_dir):
    """Download and extract BodyParts3D mesh files (OBJ or STL)."""
    zip_path = os.path.join(cache_dir, "bodyparts3d.zip")
    extract_dir = os.path.join(cache_dir, "bodyparts3d")

    if os.path.isdir(extract_dir):
        # Count mesh files recursively
        mesh_count = 0
        for root, dirs, files in os.walk(extract_dir):
            mesh_count += sum(1 for f in files if f.lower().endswith(('.obj', '.stl')))
        if mesh_count > 100:
            print(f"  BodyParts3D already extracted at {extract_dir} ({mesh_count} meshes)")
            return extract_dir

    if not os.path.exists(zip_path):
        print(f"  Downloading BodyParts3D...")
        downloaded = False
        for url in BP3D_URLS:
            print(f"  Trying: {url}")
            try:
                if url.startswith("ftp://"):
                    import urllib.request
                    print(f"  Downloading via FTP...")
                    urllib.request.urlretrieve(url, zip_path)
                elif _HAS_REQUESTS:
                    r = requests.get(url, stream=True, timeout=120,
                                     allow_redirects=True)
                    r.raise_for_status()
                    total = int(r.headers.get('content-length', 0))
                    with open(zip_path, 'wb') as f:
                        dl = 0
                        for chunk in r.iter_content(chunk_size=65536):
                            f.write(chunk)
                            dl += len(chunk)
                            if total:
                                pct = 100 * dl / total
                                print(f"\r  {dl/1024/1024:.1f} / {total/1024/1024:.1f} MB ({pct:.0f}%)", end='', flush=True)
                            else:
                                print(f"\r  {dl/1024/1024:.1f} MB downloaded...", end='', flush=True)
                    print()
                else:
                    import urllib.request
                    urllib.request.urlretrieve(url, zip_path)
                downloaded = True
                print(f"  Download complete: {os.path.getsize(zip_path)/1024/1024:.1f} MB")
                break
            except Exception as e:
                print(f"  Failed: {e}")
                if os.path.exists(zip_path):
                    os.remove(zip_path)
                continue

        if not downloaded:
            print("\n  ERROR: Could not download BodyParts3D from any source.")
            print("  Please download manually:")
            print("    Option 1: https://github.com/Kevin-Mattheus-Moerman/BodyParts3D")
            print("              (Code → Download ZIP)")
            print("    Option 2: ftp://ftp.biosciencedbc.jp/archive/bodyparts3d/LATEST/isa_BP3D_4.0_obj_99.zip")
            print(f"  Save the zip to: {zip_path}")
            print("  Then re-run this script.")
            return None

    print(f"  Extracting...")
    os.makedirs(extract_dir, exist_ok=True)
    with zipfile.ZipFile(zip_path, 'r') as zf:
        zf.extractall(extract_dir)

    # Count what we got
    mesh_count = 0
    for root, dirs, files in os.walk(extract_dir):
        mesh_count += sum(1 for f in files if f.lower().endswith(('.obj', '.stl')))
    print(f"  Extracted to {extract_dir} ({mesh_count} mesh files)")
    return extract_dir


def _load_fma_name_map(bp3d_dir):
    """Load FMA ID → English name mappings from .txt files in BodyParts3D.
    The GitHub mirror stores txt files with lines like:
        FMA12521\tDeltoid\t三角筋
    or similar tab/comma-separated formats."""
    fma_map = {}
    for root, dirs, files in os.walk(bp3d_dir):
        for f in files:
            if f.lower().endswith('.txt'):
                path = os.path.join(root, f)
                try:
                    with open(path, 'r', encoding='utf-8', errors='replace') as fh:
                        for line in fh:
                            line = line.strip()
                            if not line:
                                continue
                            # Try various separators
                            for sep in ['\t', ',', '|']:
                                parts = line.split(sep)
                                if len(parts) >= 2:
                                    # Look for FMA ID in any column
                                    for i, p in enumerate(parts):
                                        p = p.strip()
                                        if p.upper().startswith('FMA') and any(c.isdigit() for c in p):
                                            fma_id = p.strip()
                                            # Take the English name from another column
                                            names = [parts[j].strip() for j in range(len(parts))
                                                     if j != i and parts[j].strip()
                                                     and not parts[j].strip().startswith('FMA')]
                                            if names:
                                                fma_map[fma_id.upper()] = names[0]
                                            break
                except:
                    continue
    return fma_map


def find_mesh_files(bp3d_dir):
    """Recursively find all OBJ and STL files in the BodyParts3D directory.
    Returns dict of {display_name: file_path}.
    For the GitHub mirror (STL files named FMA12521.stl), we resolve
    FMA IDs to English names using the bundled .txt mapping files."""

    # First, build FMA ID → name mapping
    fma_map = _load_fma_name_map(bp3d_dir)
    if fma_map:
        print(f"  Loaded {len(fma_map)} FMA name mappings")

    mesh_files = {}
    for root, dirs, files in os.walk(bp3d_dir):
        for f in files:
            if f.lower().endswith(('.obj', '.stl')):
                stem = os.path.splitext(f)[0]
                filepath = os.path.join(root, f)

                # Try to resolve FMA ID to a human-readable name
                display_name = stem
                stem_upper = stem.upper()
                if stem_upper in fma_map:
                    display_name = fma_map[stem_upper]
                elif stem_upper.startswith('FMA'):
                    # Try without leading zeros etc.
                    for key, val in fma_map.items():
                        if key == stem_upper:
                            display_name = val
                            break

                # Use the resolved name (or original stem) as key
                # Append FMA ID to avoid collisions
                if display_name != stem:
                    key = f"{display_name}__{stem}"
                else:
                    key = stem
                mesh_files[key] = filepath

    return mesh_files


import re as _re

def match_structure(filename, region_spec):
    """Check if a mesh filename matches a region's structure specification.
    Handles both original OBJ names (e.g. 'Deltoid_muscle') and
    resolved GitHub mirror names (e.g. 'Deltoid__FMA12521').
    Uses word-boundary matching to avoid false positives like
    'subscapularis' matching 'scapula'."""
    name_lower = filename.lower().replace(' ', '_').replace('-', '_')

    # Check FMA IDs (exact ID match is always safe)
    for fma_id in region_spec.get("fma_ids", []):
        if f"fma{fma_id}" in name_lower or f"fma_{fma_id}" in name_lower:
            return True

    # Check keywords with word-boundary matching to avoid false positives
    # e.g. "scapula" should NOT match "subscapularis"
    for kw in region_spec.get("keywords", []):
        kw_lower = kw.lower().replace(' ', '_').replace('-', '_')
        # Use word boundaries: the keyword must appear as a whole word
        # (delimited by underscores, start/end of string, or non-alpha chars)
        pattern = r'(?:^|_)' + _re.escape(kw_lower) + r'(?:_|$|[^a-z])'
        if _re.search(pattern, name_lower):
            return True

    return False


def _is_right_side(name):
    """Check if a BodyParts3D structure name refers to the right side.
    Returns True for right-side, False for left-side, None for midline/unspecified."""
    nl = name.lower()
    if 'right' in nl or '_r_' in nl:
        return True
    if 'left' in nl or '_l_' in nl:
        return False
    return None  # midline or unspecified


def load_region_from_bodyparts3d(bp3d_dir, region_name):
    """Load meshes for a region from BodyParts3D OBJ/STL files.
    For paired regions: only loads RIGHT-side structures (plus midline).
    For midline regions (spine): loads ALL sides (both L+R included).
    The caller generates left by mirroring right for paired regions."""
    print(f"\n  Loading {region_name} structures from BodyParts3D...")
    obj_files = find_mesh_files(bp3d_dir)
    print(f"  Found {len(obj_files)} total mesh files")

    region_spec = REGION_STRUCTURES[region_name]
    is_midline = region_name in MIDLINE_REGIONS
    result = {}

    for tissue_type in ["bone", "muscle", "vessel", "nerve"]:
        meshes = []
        matched_names = []
        skipped_left = 0

        for name, path in obj_files.items():
            if match_structure(name, region_spec[tissue_type]):
                # Midline regions include both sides; paired regions
                # skip left-side structures (left generated by mirroring)
                if not is_midline:
                    side = _is_right_side(name)
                    if side is False:  # left-side → skip
                        skipped_left += 1
                        continue

                if path.lower().endswith('.stl'):
                    verts, faces = load_stl(path)
                else:
                    verts, faces = load_obj(path)
                if verts is not None and len(verts) > 3:
                    meshes.append((verts, faces))
                    matched_names.append(name)

        if meshes:
            merged_v, merged_f = merge_meshes(meshes)
            result[tissue_type] = (merged_v, merged_f)
            print(f"    {tissue_type}: {len(matched_names)} structures "
                  f"(+{skipped_left} left-side skipped), "
                  f"{len(merged_v)} verts, {len(merged_f)} tris")
            for n in matched_names[:5]:
                print(f"      - {n}")
            if len(matched_names) > 5:
                print(f"      ... and {len(matched_names) - 5} more")
        else:
            result[tissue_type] = (None, None)
            print(f"    {tissue_type}: no matching structures found "
                  f"({skipped_left} left-side skipped)")

    return result


# ─────────────────────────────────────────────────────────────────
#  MAIN PIPELINE
# ─────────────────────────────────────────────────────────────────

def generate_region_volume(region_name, tissue_meshes, output_dir, grid_size=256):
    """Generate a complete volume + mesh files for one body region.

    Args:
        region_name: e.g. "shoulder_right"
        tissue_meshes: dict with keys "bone","muscle","vessel","nerve"
                       each value is (verts, faces) or (None, None)
        output_dir: where to write .vol and .omsh/.vmsh/.nmsh/.mmsh
        grid_size: voxel resolution
    """
    os.makedirs(output_dir, exist_ok=True)
    vol_path = os.path.join(output_dir, f"{region_name}.vol")

    print(f"\n{'='*60}")
    print(f"  Generating: {region_name}")
    print(f"{'='*60}")

    # Use BONE bounding box (with padding) as the normalization reference.
    # Bones define the anatomical region of interest. Using the combined
    # bbox of ALL tissues causes problems because distant muscles (e.g.
    # latissimus dorsi) stretch the bbox so much that core structures
    # occupy only a tiny fraction of the 256³ voxel grid.
    bone_v, bone_f = tissue_meshes.get("bone", (None, None))

    all_verts = []
    for tissue_type in ["bone", "muscle", "vessel", "nerve"]:
        v, f = tissue_meshes.get(tissue_type, (None, None))
        if v is not None:
            all_verts.append(v)

    if not all_verts:
        print(f"  ERROR: No meshes for {region_name} — skipping")
        return

    if bone_v is not None and len(bone_v) > 0:
        # Use bone bbox with 30% padding so nearby soft tissue is included
        bmin = bone_v.min(axis=0)
        bmax = bone_v.max(axis=0)
        bone_extent = (bmax - bmin).max()
        padding = bone_extent * 0.30
        center = (bmin + bmax) / 2.0
        extent = bone_extent + padding * 2
        print(f"  Normalization: bone bbox + 30% padding "
              f"(extent={extent:.1f}, center={center})")
    else:
        # Fallback to combined bbox (no bones available)
        combined = np.concatenate(all_verts, axis=0)
        vmin = combined.min(axis=0)
        vmax = combined.max(axis=0)
        center = (vmin + vmax) / 2.0
        extent = (vmax - vmin).max()

    if extent < 1e-10:
        print(f"  ERROR: Zero-extent mesh — skipping")
        return

    # Normalize all meshes to [-0.45, 0.45]³ (slight margin from edge)
    # Structures outside this range get clipped during voxelization
    def normalize(v):
        return (v - center) / extent * 0.9

    # ── Voxelize each tissue layer ──
    volume = np.zeros((grid_size, grid_size, grid_size), dtype=np.uint8)

    # Tissue layering order: muscle first (lowest priority), then nerve,
    # vessel, bone last (highest priority — always visible on top).
    # This matches how CT works: bone is brightest and overwrites soft tissue.
    # fill_interior: True for solid structures (bone, muscle), False for
    # tube-like structures (vessels, nerves) which aren't watertight.
    tissue_config = [
        ("muscle", INTENSITY_MUSCLE, b'OMSH', '.mmsh', True),
        ("nerve",  INTENSITY_NERVE,  b'OMSH', '.nmsh', True),
        ("vessel", INTENSITY_VESSEL, b'OMSH', '.vmsh', True),
        ("bone",   INTENSITY_BONE,   b'OMSH', '.omsh', True),
    ]

    # Voxelize all tissues, store masks, then composite
    tissue_masks = {}
    for tissue_type, intensity, magic, ext, do_fill in tissue_config:
        v, f = tissue_meshes.get(tissue_type, (None, None))
        if v is None or f is None:
            print(f"  Skipping {tissue_type} (no mesh)")
            continue

        norm_v = normalize(v)
        print(f"\n  Voxelizing {tissue_type} ({len(f)} triangles)...")

        t0 = time.time()
        mask = voxelize_mesh_fast(norm_v, f, grid_size, fill_interior=do_fill)
        t1 = time.time()
        n_voxels = int(np.sum(mask))

        # Thicken soft tissues: BodyParts3D meshes are anatomically
        # accurate but individual structures (muscle bellies, vessels,
        # nerves) can be thin relative to the 256³ grid. Always dilate
        # to ensure visibility in volume rendering.
        if tissue_type == "muscle" and n_voxels > 0:
            print(f"    Thickening muscle ({n_voxels:,} vox)...")
            mask = _dilate_3d(mask, iterations=3)
            n_voxels = int(np.sum(mask))
        elif tissue_type in ("vessel", "nerve") and n_voxels > 0:
            print(f"    Thickening {tissue_type} ({n_voxels:,} vox)...")
            mask = _dilate_3d(mask, iterations=3)
            n_voxels = int(np.sum(mask))

        print(f"    {tissue_type}: {n_voxels:,} voxels ({t1-t0:.1f}s)")
        tissue_masks[tissue_type] = mask

        # Export companion mesh file
        mesh_path = os.path.join(output_dir, f"{region_name}{ext}")
        norm_v_clipped = np.clip(norm_v, -0.5, 0.5)
        dec_v, dec_f = decimate_mesh_simple(norm_v_clipped, f, MESH_TARGET_TRIS)
        write_mesh_file(mesh_path, dec_v, dec_f, magic=magic)

    # Composite: paint in priority order (later layers overwrite earlier)
    for tissue_type, intensity, magic, ext, _ in tissue_config:
        mask = tissue_masks.get(tissue_type)
        if mask is not None:
            volume[mask] = intensity

    # Add soft gradient falloff around structures for more realistic
    # volume rendering (structures don't just hard-cut to black)
    print("  Adding intensity falloff around structures...")
    occupied = volume > 0
    if np.any(occupied):
        # Simple 3D dilation to create a soft border
        border = _dilate_3d(occupied, iterations=2)
        # Set border voxels (not already occupied) to dim value
        border_only = border & ~occupied
        volume[border_only] = 35  # faint tissue background

    # ── Apply Gaussian smoothing if scipy available ──
    if _HAS_SCIPY:
        print("  Applying Gaussian smoothing...")
        vol_float = volume.astype(np.float32)
        vol_float = ndimage.gaussian_filter(vol_float, sigma=0.8)
        volume = np.clip(vol_float, 0, 255).astype(np.uint8)

    # ── Export OVOL ──
    export_ovol(volume, vol_path)

    return vol_path


MIDLINE_REGIONS = {"spine"}  # regions that produce a single volume (no L/R mirror)

def generate_all_volumes(output_dir, use_bodyparts3d=False, bp3d_cache=None,
                         regions=None, grid_size=256):
    """Generate anatomy volumes for all requested regions.
    Paired regions (shoulder, hip, thigh, knee) produce left+right via mirroring.
    Midline regions (spine) produce a single volume.

    Args:
        output_dir: where to write output files
        use_bodyparts3d: if True, download and use real BodyParts3D meshes
        bp3d_cache: cache directory for BodyParts3D download
        regions: list of regions to generate (default: all 5)
        grid_size: voxel resolution
    """
    if regions is None:
        regions = ["shoulder", "hip", "thigh", "knee", "spine"]

    os.makedirs(output_dir, exist_ok=True)

    bp3d_dir = None
    if use_bodyparts3d:
        if bp3d_cache is None:
            bp3d_cache = os.path.join(output_dir, ".cache")
        os.makedirs(bp3d_cache, exist_ok=True)
        bp3d_dir = download_bodyparts3d(bp3d_cache)

    for region in regions:
        print(f"\n{'#'*60}")
        print(f"  REGION: {region.upper()}")
        print(f"{'#'*60}")

        # Load or generate meshes
        if bp3d_dir:
            tissue_meshes = load_region_from_bodyparts3d(bp3d_dir, region)
            # Fill gaps with procedural geometry, transformed to match
            # the BodyParts3D coordinate system
            gen_func = PROCEDURAL_GENERATORS.get(region)
            if gen_func:
                # Compute BodyParts3D bounding box from available meshes
                bp3d_verts = []
                for tissue in ["bone", "muscle", "vessel", "nerve"]:
                    v, f = tissue_meshes.get(tissue, (None, None))
                    if v is not None:
                        bp3d_verts.append(v)

                if bp3d_verts:
                    bp3d_all = np.concatenate(bp3d_verts, axis=0)
                    bp3d_center = (bp3d_all.min(axis=0) + bp3d_all.max(axis=0)) / 2.0
                    bp3d_extent = (bp3d_all.max(axis=0) - bp3d_all.min(axis=0)).max()
                else:
                    bp3d_center = np.zeros(3)
                    bp3d_extent = 1.0

                proc = gen_func()
                for tissue in ["bone", "muscle", "vessel", "nerve"]:
                    bp3d_v, bp3d_f = tissue_meshes.get(tissue, (None, None))
                    # Count how many BP3D structures matched (rough heuristic:
                    # each structure is a separate connected component, but
                    # we can estimate by checking if the mesh is very sparse)
                    bp3d_is_sparse = (bp3d_v is None or
                                      (tissue in ("vessel", "nerve") and
                                       bp3d_f is not None and len(bp3d_f) < 5000))
                    if bp3d_is_sparse:
                        pv, pf = proc[tissue]
                        if pv is not None:
                            # Transform procedural meshes from [-0.3,0.3] space
                            # into the BodyParts3D coordinate system
                            pv_transformed = pv * (bp3d_extent / 0.9) + bp3d_center
                            if bp3d_v is not None:
                                # Merge sparse BP3D data with procedural fill
                                merged_v = np.concatenate([bp3d_v, pv_transformed])
                                merged_f = np.concatenate([bp3d_f,
                                    pf + len(bp3d_v)])
                                tissue_meshes[tissue] = (merged_v, merged_f)
                                print(f"  Supplementing sparse {tissue} "
                                      f"({len(bp3d_f)} BP3D tris) with "
                                      f"procedural geometry")
                            else:
                                tissue_meshes[tissue] = (pv_transformed, pf)
                                print(f"  Filling {tissue} gap with procedural "
                                      f"geometry (transformed to BP3D coords)")
                        else:
                            print(f"  No procedural {tissue} available")
        else:
            gen_func = PROCEDURAL_GENERATORS[region]
            tissue_meshes = gen_func()

        if region in MIDLINE_REGIONS:
            # Midline regions produce a single volume (no left/right)
            generate_region_volume(region, tissue_meshes,
                                   output_dir, grid_size)
        else:
            # Paired regions: generate right, then mirror for left
            right_name = f"{region}_right"
            vol_path = generate_region_volume(right_name, tissue_meshes,
                                              output_dir, grid_size)

            if vol_path:
                left_name = f"{region}_left"
                print(f"\n  Mirroring {right_name} → {left_name}...")
                mirrored = {}
                for tissue in ["bone", "muscle", "vessel", "nerve"]:
                    v, f = tissue_meshes.get(tissue, (None, None))
                    if v is not None:
                        mv, mf = mirror_mesh_x(v, f)
                        mirrored[tissue] = (mv, mf)
                    else:
                        mirrored[tissue] = (None, None)

                generate_region_volume(left_name, mirrored, output_dir, grid_size)

    # ── Summary ──
    print(f"\n{'='*60}")
    print(f"  GENERATION COMPLETE")
    print(f"{'='*60}")
    vol_files = sorted([f for f in os.listdir(output_dir) if f.endswith('.vol')])
    mesh_files = sorted([f for f in os.listdir(output_dir)
                         if f.endswith(('.omsh', '.vmsh', '.nmsh', '.mmsh'))])
    print(f"  Volume files ({len(vol_files)}):")
    for vf in vol_files:
        size = os.path.getsize(os.path.join(output_dir, vf)) / 1024 / 1024
        print(f"    {vf} ({size:.1f} MB)")
    print(f"  Mesh files ({len(mesh_files)}):")
    for mf in mesh_files:
        size = os.path.getsize(os.path.join(output_dir, mf)) / 1024
        print(f"    {mf} ({size:.0f} KB)")


# ─────────────────────────────────────────────────────────────────
#  CLI
# ─────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(
        description="Generate anatomy volumes for RegionalAR")
    parser.add_argument("--output", "-o", default="./SampleVolumes",
                        help="Output directory")
    parser.add_argument("--test", action="store_true",
                        help="Use procedural geometry (no download needed)")
    parser.add_argument("--bodyparts3d", action="store_true",
                        help="Download and use BodyParts3D meshes")
    parser.add_argument("--catalog", action="store_true",
                        help="List available BodyParts3D structures and exit")
    parser.add_argument("--region", type=str, default=None,
                        help="Generate only this region (shoulder/hip/thigh/knee/spine)")
    parser.add_argument("--grid-size", type=int, default=256,
                        help="Voxel grid resolution (default: 256)")
    parser.add_argument("--cache", type=str, default=None,
                        help="Cache directory for downloaded data")
    args = parser.parse_args()

    if args.catalog:
        if not args.cache:
            args.cache = os.path.join(args.output, ".cache")
        bp3d_dir = download_bodyparts3d(args.cache)
        if bp3d_dir:
            mesh_files = find_mesh_files(bp3d_dir)
            print(f"\nTotal mesh files: {len(mesh_files)}")
            for name in sorted(mesh_files.keys()):
                print(f"  {name}")
        return

    regions = [args.region] if args.region else None
    use_bp3d = args.bodyparts3d and not args.test

    print("╔════════════════════════════════════════════════════════════╗")
    print("║  RegionalAR — Anatomy Volume Generator                    ║")
    print("╚════════════════════════════════════════════════════════════╝")
    print(f"  Mode:       {'Procedural (test)' if not use_bp3d else 'BodyParts3D + procedural'}")
    print(f"  Grid size:  {args.grid_size}³")
    print(f"  Regions:    {', '.join(regions) if regions else 'all 5'}")
    print(f"  Output:     {args.output}")
    print(f"  trimesh:    {'YES' if _HAS_TRIMESH else 'NO (basic decimation)'}")
    print(f"  scipy:      {'YES' if _HAS_SCIPY else 'NO (no smoothing)'}")
    print()

    t_start = time.time()
    generate_all_volumes(
        output_dir=args.output,
        use_bodyparts3d=use_bp3d,
        bp3d_cache=args.cache,
        regions=regions,
        grid_size=args.grid_size,
    )
    elapsed = time.time() - t_start
    print(f"\n  Total time: {elapsed/60:.1f} minutes")


if __name__ == "__main__":
    main()
