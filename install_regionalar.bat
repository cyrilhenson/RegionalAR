@echo off
setlocal enabledelayedexpansion
title RegionalAR Desktop - University Installer
color 0B

echo.
echo  ================================================================
echo    RegionalAR Desktop - Full Installation
echo    Installs Python packages, HD processing, and AI features
echo  ================================================================
echo.

:: Always run from the folder this .bat lives in
cd /d "%~dp0"
set "INSTALL_DIR=%~dp0"
set "LAUNCHER=%INSTALL_DIR%RegionalAR.bat"
set "SHORTCUT_VBS=%INSTALL_DIR%_create_shortcut.vbs"

:: ---- Step 1: Check for Python ------------------------------------------
echo [1/6] Checking for Python...

set "SYS_PY="
python --version >nul 2>&1
if not errorlevel 1 (
    set "SYS_PY=python"
) else (
    py -3 --version >nul 2>&1
    if not errorlevel 1 (
        set "SYS_PY=py -3"
    )
)

if "%SYS_PY%"=="" (
    echo.
    echo  ================================================================
    echo    Python 3.9+ is required but not found.
    echo.
    echo    Download from: https://www.python.org/downloads/
    echo    IMPORTANT: Check "Add Python to PATH" during install!
    echo.
    echo    After installing Python, run this script again.
    echo  ================================================================
    echo.
    pause
    exit /b 1
)

for /f "tokens=*" %%v in ('%SYS_PY% --version 2^>^&1') do echo        Found: %%v

:: ---- Step 2: Upgrade pip -----------------------------------------------
echo.
echo [2/6] Upgrading pip...
%SYS_PY% -m pip install --upgrade pip --user --no-warn-script-location
echo        Done.

:: ---- Step 3: Install base dependencies ---------------------------------
echo.
echo [3/6] Installing base dependencies (numpy, pydicom, SimpleITK)...
echo        This may take 2-3 minutes...
%SYS_PY% -m pip install --user numpy pydicom SimpleITK --no-warn-script-location
if errorlevel 1 (
    echo.
    echo ERROR: Base package installation failed. See error above.
    pause & exit /b 1
)
echo        Base packages installed.

:: ---- Step 4: Install HD dependencies -----------------------------------
echo.
echo [4/6] Installing HD + mesh processing (scipy, scikit-image, trimesh, fast_simplification, blosc2)...
REM No blosc2 version pin — nnU-Net v2 requires 3.x+. If you're stuck on
REM Python 3.9 this will fail; upgrade to Python 3.11 for full AI support.
%SYS_PY% -m pip install --user scipy scikit-image trimesh fast_simplification blosc2 --no-warn-script-location
if errorlevel 1 (
    echo WARNING: HD packages failed. HD mode may not work.
) else (
    echo        HD packages installed.
)

:: ---- Step 5: Install AI segmentation -----------------------------------
echo.
echo [5/6] Installing AI segmentation (TotalSegmentator + PyTorch)...
echo        This is the largest download (~2-3 GB). Please be patient...
echo.

:: Install PyTorch CPU-only first (smaller than full CUDA version)
echo        Step 5a: Installing PyTorch (CPU-only)...
%SYS_PY% -m pip install --user torch --index-url https://download.pytorch.org/whl/cpu --no-warn-script-location
if errorlevel 1 (
    echo        CPU index failed, trying standard PyTorch...
    %SYS_PY% -m pip install --user torch --no-warn-script-location
    if errorlevel 1 (
        echo.
        echo  WARNING: PyTorch install failed. AI features will not work.
        echo  Press any key to continue...
        pause >nul
    )
)
echo.

echo        Step 5b: Installing TotalSegmentator...
%SYS_PY% -m pip install --user TotalSegmentator --no-warn-script-location
if errorlevel 1 (
    echo.
    echo  WARNING: TotalSegmentator install failed.
    echo  AI Segment feature will not be available.
    echo  You can retry later by running this script again.
    echo.
    echo  Press any key to continue...
    pause >nul
) else (
    echo        TotalSegmentator installed.
    echo        Note: AI models (~1.5 GB) download on first use.
)

:: ---- Step 6: Create launcher scripts -----------------------------------
echo.
echo [6/6] Creating launcher...

:: --user installs go to Python's user site-packages, which Python
:: finds automatically. No PYTHONPATH or venv activation needed.
(
echo @echo off
echo cd /d "%INSTALL_DIR%"
echo %SYS_PY% dicom_processor.py
echo if errorlevel 1 pause
) > "%LAUNCHER%"

:: Create a desktop shortcut via VBScript
(
echo Set ws = WScript.CreateObject^("WScript.Shell"^)
echo Set shortcut = ws.CreateShortcut^(ws.SpecialFolders^("Desktop"^) ^& "\RegionalAR Desktop.lnk"^)
echo shortcut.TargetPath = "%LAUNCHER%"
echo shortcut.WorkingDirectory = "%INSTALL_DIR%"
echo shortcut.Description = "RegionalAR - DICOM Volume AR for Quest 3"
echo shortcut.WindowStyle = 7
echo shortcut.Save
) > "%SHORTCUT_VBS%"
cscript //nologo "%SHORTCUT_VBS%"
del "%SHORTCUT_VBS%" 2>nul

echo.
echo  ================================================================
echo    Installation Complete!
echo  ================================================================
echo.
echo    Launcher:  %LAUNCHER%
echo    Shortcut:  Desktop - "RegionalAR Desktop"
echo.
echo    To run:    Double-click "RegionalAR Desktop" on your desktop
echo               or run RegionalAR.bat from this folder.
echo.
echo    Features installed:
echo      [x] Base DICOM processing
echo      [x] WiFi transfer to Quest 3

:: Check what's actually available
%SYS_PY% -c "import scipy" >nul 2>&1
if not errorlevel 1 (
    echo      [x] High Definition mode
) else (
    echo      [ ] High Definition mode - scipy not found
)

%SYS_PY% -c "from totalsegmentator.python_api import totalsegmentator" >nul 2>&1
if not errorlevel 1 (
    echo      [x] AI Segmentation - TotalSegmentator
) else (
    echo      [ ] AI Segmentation - not installed
)

echo.
echo    First run of AI Segment will download ~1.5 GB of models.
echo    Ensure internet access on first use.
echo.

pause
