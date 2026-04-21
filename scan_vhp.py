"""
Scan VHP_DICOM folder and list what's inside by reading DICOM headers.
Requires: py -3.11 -m pip install pydicom

Usage:  py -3.11 scan_vhp.py
"""

import os
import sys

try:
    import pydicom
except ImportError:
    print("Installing pydicom...")
    import subprocess
    subprocess.check_call([sys.executable, "-m", "pip", "install", "pydicom"])
    import pydicom

dicom_root = r"D:\VHP_DICOM"

# Collect unique series info
series_info = {}

print(f"Scanning {dicom_root} for DICOM files...\n")

file_count = 0
for dirpath, dirnames, filenames in os.walk(dicom_root):
    for fname in filenames:
        fpath = os.path.join(dirpath, fname)
        try:
            ds = pydicom.dcmread(fpath, stop_before_pixels=True)
            uid = getattr(ds, "SeriesInstanceUID", "unknown")
            if uid not in series_info:
                series_info[uid] = {
                    "PatientID":         getattr(ds, "PatientID", "?"),
                    "PatientName":       str(getattr(ds, "PatientName", "?")),
                    "Modality":          getattr(ds, "Modality", "?"),
                    "SeriesDescription": getattr(ds, "SeriesDescription", ""),
                    "StudyDescription":  getattr(ds, "StudyDescription", ""),
                    "BodyPartExamined":  getattr(ds, "BodyPartExamined", ""),
                    "Rows":              getattr(ds, "Rows", "?"),
                    "Columns":           getattr(ds, "Columns", "?"),
                    "SliceThickness":    getattr(ds, "SliceThickness", "?"),
                    "ImageCount":        0,
                    "SampleFile":        fpath,
                    "Folder":            dirpath,
                }
            series_info[uid]["ImageCount"] += 1
            file_count += 1
            if file_count % 500 == 0:
                print(f"  ...scanned {file_count} files, found {len(series_info)} series so far")
        except Exception:
            pass  # skip non-DICOM files

print(f"\nDone! Scanned {file_count} DICOM files.\n")
print("=" * 80)
print(f"Found {len(series_info)} unique series:\n")

for i, (uid, info) in enumerate(sorted(series_info.items(), key=lambda x: x[1]["Modality"]), 1):
    print(f"--- Series {i} ---")
    print(f"  Patient:      {info['PatientID']} / {info['PatientName']}")
    print(f"  Modality:     {info['Modality']}")
    print(f"  Study:        {info['StudyDescription']}")
    print(f"  Series:       {info['SeriesDescription']}")
    print(f"  Body Part:    {info['BodyPartExamined']}")
    print(f"  Image Size:   {info['Rows']} x {info['Columns']}")
    print(f"  Slice Thick:  {info['SliceThickness']} mm")
    print(f"  # of Slices:  {info['ImageCount']}")
    print(f"  Folder:       {info['Folder']}")
    print()
