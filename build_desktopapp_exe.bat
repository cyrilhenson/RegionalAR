@echo off
setlocal enabledelayedexpansion
title BlockAR — Build Standalone .exe

:: Always run from the folder this .bat file lives in
cd /d "%~dp0"

echo.
echo ================================================================
echo   BlockAR Desktop — Build Standalone .exe
echo ================================================================
echo Working directory: %CD%
echo.

:: ── Check Python ─────────────────────────────────────────────────────────────
python --version >nul 2>&1
if errorlevel 1 (
    echo ERROR: Python not found. Install from https://python.org
    pause & exit /b 1
)
for /f "tokens=*" %%v in ('python --version 2^>^&1') do echo Python: %%v

:: ── Check required source file ────────────────────────────────────────────────
if not exist "dicom_processor.py" (
    echo ERROR: dicom_processor.py not found in %CD%
    pause & exit /b 1
)

:: ── Install PyInstaller + everything the pipeline needs ────────────────────
:: (no --quiet so install failures are visible, not silent)
echo.
echo [1/3] Installing PyInstaller + HD/mesh packages...
python -m pip install --upgrade pip
:: No version pin on blosc2 — nnU-Net v2 needs 3.x (and on Python 3.11+
:: both blosc2 3.x and 4.x wheels are available). If you're still on
:: Python 3.9 this will fail with "no matching distribution" — upgrade
:: to Python 3.11 instead of pinning blosc2 back, since nnU-Net itself
:: won't work against an old blosc2.
python -m pip install pyinstaller scipy scikit-image trimesh fast_simplification blosc2 pandas
if errorlevel 1 python -m pip install pyinstaller scipy scikit-image trimesh fast_simplification blosc2 pandas --break-system-packages

:: ── Verify imports — only blocks build if PyInstaller itself is broken ──
echo.
echo Python:
python --version
python -c "import sys; print('  executable =', sys.executable)"
echo.
echo Checking imports:
for %%P in (PyInstaller scipy skimage trimesh fast_simplification blosc2 pandas totalsegmentator nnunetv2) do (
    python -c "import %%P" 2>nul && (echo   [OK]   %%P) || (echo   [WARN] %%P is not importable - if AI, the exe will fall back to HD at runtime)
)
:: Hard requirement: PyInstaller itself
python -c "import PyInstaller" 2>nul
if errorlevel 1 (
    echo.
    echo FATAL: PyInstaller not importable in this Python env. Fix with:
    echo   python -m pip install pyinstaller
    echo.
    pause & exit /b 1
)

:: ── Clean previous build ─────────────────────────────────────────────────────
echo.
echo [2/3] Cleaning previous build...
if exist build  rmdir /s /q build
if exist dist   rmdir /s /q dist
echo       Done.

:: ── Run PyInstaller (command-line, no spec file — more reliable) ─────────────
echo.
echo [3/3] Building BlockAR.exe — output below...
echo       (this takes 3-6 minutes, lots of text is normal)
echo.

python -m PyInstaller ^
    --onefile ^
    --windowed ^
    --name BlockAR ^
    --collect-all SimpleITK ^
    --collect-all pydicom ^
    --collect-all numpy ^
    --collect-all scipy ^
    --collect-all skimage ^
    --collect-all trimesh ^
    --collect-all fast_simplification ^
    --collect-all pandas ^
    --collect-all totalsegmentator ^
    --collect-all nnunetv2 ^
    --collect-all blosc2 ^
    --collect-all acvl_utils ^
    --collect-all dynamic_network_architectures ^
    --collect-all batchgenerators ^
    --collect-all batchgeneratorsv2 ^
    --collect-all connected_components_3d ^
    --collect-all dicom2nifti ^
    --collect-all nibabel ^
    --collect-all einops ^
    --collect-all huggingface_hub ^
    --collect-all matplotlib ^
    --collect-submodules blosc2 ^
    --collect-binaries blosc2 ^
    --collect-data blosc2 ^
    --hidden-import blosc2 ^
    --hidden-import blosc2.core ^
    --hidden-import blosc2.schunk ^
    --hidden-import blosc2.ndarray ^
    --hidden-import blosc2.c2array ^
    --hidden-import blosc2.lazyexpr ^
    --hidden-import blosc2.proxy ^
    --hidden-import nnunetv2 ^
    --hidden-import nnunetv2.inference.predict_from_raw_data ^
    --hidden-import nnunetv2.utilities.helpers ^
    --hidden-import totalsegmentator.python_api ^
    --hidden-import totalsegmentator.map_to_binary ^
    --hidden-import totalsegmentator.libs ^
    --hidden-import torch ^
    --hidden-import torchvision ^
    --hidden-import tkinter ^
    --hidden-import tkinter.ttk ^
    --hidden-import tkinter.filedialog ^
    --hidden-import tkinter.messagebox ^
    --hidden-import tkinter.scrolledtext ^
    --hidden-import _tkinter ^
    --hidden-import pydicom.encoders ^
    --hidden-import scipy.ndimage ^
    --hidden-import scipy.ndimage._filters ^
    --hidden-import scipy.ndimage._morphology ^
    --hidden-import skimage.measure ^
    --hidden-import trimesh ^
    --hidden-import trimesh.smoothing ^
    --exclude-module IPython ^
    --noconfirm ^
    dicom_processor.py

if errorlevel 1 (
    echo.
    echo ================================================================
    echo   BUILD FAILED — scroll up to read the error message
    echo ================================================================
    echo.
    echo Try running this in a Command Prompt window for more detail:
    echo   cd /d "%CD%"
    echo   python -m PyInstaller --onefile --windowed --name BlockAR dicom_processor.py
    echo.
    pause & exit /b 1
)

:: ── Done ─────────────────────────────────────────────────────────────────────
if not exist "dist\BlockAR.exe" (
    echo ERROR: dist\BlockAR.exe was not created even though PyInstaller succeeded.
    pause & exit /b 1
)

for %%F in ("dist\BlockAR.exe") do set SIZE_BYTES=%%~zF
set /a SIZE_MB=!SIZE_BYTES! / 1048576

echo.
echo ================================================================
echo   Build complete!
echo ================================================================
echo   Output : dist\BlockAR.exe
echo   Size   : ~!SIZE_MB! MB
echo.
echo   Copy dist\BlockAR.exe to any Windows PC — no Python needed.
echo.

set /p OPEN="Open dist folder? (y/n): "
if /i "!OPEN!"=="y" explorer dist

pause
