"""
Rename VHP_DICOM subfolders based on DICOM series metadata.
Scans each subfolder, reads one DICOM header, and renames the folder
to a descriptive name like "Male_CT_1mm" or "Female_MRI_Head".

Usage:  py -3.11 rename_vhp_folders.py
"""

import os
import sys
import re

try:
    import pydicom
except ImportError:
    print("Installing pydicom...")
    import subprocess
    subprocess.check_call([sys.executable, "-m", "pip", "install", "pydicom"])
    import pydicom

DICOM_ROOT = r"D:\VHP_DICOM"


def get_series_info(folder):
    """Read the first DICOM file in a folder and return metadata."""
    for fname in os.listdir(folder):
        fpath = os.path.join(folder, fname)
        if os.path.isfile(fpath):
            try:
                ds = pydicom.dcmread(fpath, stop_before_pixels=True)
                return {
                    "PatientID":         str(getattr(ds, "PatientID", "Unknown")),
                    "PatientName":       str(getattr(ds, "PatientName", "Unknown")),
                    "Modality":          str(getattr(ds, "Modality", "Unknown")),
                    "SeriesDescription": str(getattr(ds, "SeriesDescription", "")),
                    "StudyDescription":  str(getattr(ds, "StudyDescription", "")),
                    "BodyPartExamined":  str(getattr(ds, "BodyPartExamined", "")),
                    "SliceThickness":    str(getattr(ds, "SliceThickness", "")),
                    "Rows":              str(getattr(ds, "Rows", "")),
                    "Columns":           str(getattr(ds, "Columns", "")),
                    "ImageCount":        len([f for f in os.listdir(folder) if os.path.isfile(os.path.join(folder, f))]),
                }
            except Exception:
                continue
    return None


def sanitize(name):
    """Make a filesystem-safe folder name."""
    name = re.sub(r'[^\w\s\-.]', '_', name)
    name = re.sub(r'\s+', '_', name)
    name = re.sub(r'_+', '_', name)
    return name.strip('_')[:80]


def build_descriptive_name(info):
    """Build a human-readable folder name from DICOM metadata."""
    parts = []

    # Patient (male/female)
    pid = info["PatientID"].lower()
    pname = info["PatientName"].lower()
    if "male" in pid or "male" in pname:
        if "female" in pid or "female" in pname:
            parts.append("Female")
        else:
            parts.append("Male")
    elif "female" in pid or "female" in pname:
        parts.append("Female")
    else:
        parts.append(info["PatientID"])

    # Modality
    parts.append(info["Modality"])

    # Series/Study description
    desc = info["SeriesDescription"] or info["StudyDescription"] or info["BodyPartExamined"]
    if desc:
        parts.append(desc)

    # Slice info
    if info["SliceThickness"]:
        parts.append(f"{info['SliceThickness']}mm")

    # Image count
    parts.append(f"{info['ImageCount']}slices")

    return sanitize("_".join(parts))


def main():
    if not os.path.isdir(DICOM_ROOT):
        print(f"Directory not found: {DICOM_ROOT}")
        return

    print(f"Scanning {DICOM_ROOT} for subfolders...\n")

    # Walk to find leaf folders containing DICOM files
    rename_map = {}
    for dirpath, dirnames, filenames in os.walk(DICOM_ROOT):
        # Only process folders that directly contain files (leaf data folders)
        if not filenames:
            continue
        # Skip the root itself
        if dirpath == DICOM_ROOT:
            continue

        info = get_series_info(dirpath)
        if info is None:
            continue

        new_name = build_descriptive_name(info)
        rename_map[dirpath] = (new_name, info)

    if not rename_map:
        print("No DICOM series folders found.")
        return

    print(f"Found {len(rename_map)} series folders:\n")
    print(f"{'Current Path':<60} → {'New Name'}")
    print("=" * 120)

    for old_path, (new_name, info) in sorted(rename_map.items()):
        rel = os.path.relpath(old_path, DICOM_ROOT)
        print(f"  {rel:<58} → {new_name}")
        print(f"    ({info['Modality']}, {info['ImageCount']} files, "
              f"{info['Rows']}x{info['Columns']}, {info['SliceThickness']}mm)")

    print(f"\nRename these folders? (y/n): ", end="")
    choice = input().strip().lower()
    if choice != 'y':
        print("Cancelled.")
        return

    # Rename from deepest to shallowest to avoid path conflicts
    for old_path in sorted(rename_map.keys(), key=lambda p: -p.count(os.sep)):
        new_name, info = rename_map[old_path]
        parent = os.path.dirname(old_path)
        new_path = os.path.join(parent, new_name)

        # Handle duplicates
        if os.path.exists(new_path) and new_path != old_path:
            i = 2
            while os.path.exists(f"{new_path}_{i}"):
                i += 1
            new_path = f"{new_path}_{i}"

        try:
            os.rename(old_path, new_path)
            print(f"  Renamed: {os.path.basename(old_path)} → {os.path.basename(new_path)}")
        except Exception as e:
            print(f"  FAILED: {old_path} → {e}")

    print("\nDone!")


if __name__ == "__main__":
    main()
