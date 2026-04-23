@echo off
setlocal enabledelayedexpansion
title BlockAR — Deploy to Quest 3

echo.
echo ============================================================
echo   BlockAR — Deploy to Meta Quest 3
echo ============================================================
echo.

:: ── Paths (edit if needed) ───────────────────────────────────────
::   APK_FILE: where Unity saved your build (must be in this folder or edit the path)
set APK_FILE=%~dp0BlockAR.apk
set DICOM_FOLDER=C:\Users\Laurence\Downloads\Head-Neck CTA Dicom
set VOL_FILE=C:\Users\Laurence\Desktop\head_neck.vol
set QUEST_PATH=/sdcard/Android/data/com.DefaultCompany.BlockAR/files/head_neck.vol
set PROCESSOR=%~dp0dicom_to_vol_cli.py
set PACKAGE=com.DefaultCompany.BlockAR
set ACTIVITY=com.unity3d.player.UnityPlayerActivity
:: ─────────────────────────────────────────────────────────────────

:: ── Step 1: Check ADB ────────────────────────────────────────────
echo [1/6] Checking ADB ...
where adb >nul 2>&1
if errorlevel 1 (
    echo.
    echo ERROR: ADB not found in PATH.
    echo.
    echo   Install Android Platform Tools:
    echo     https://developer.android.com/studio/releases/platform-tools
    echo   Unzip it, then add the folder to your PATH.
    echo.
    echo   Alternatively install Meta Quest Developer Hub which bundles ADB.
    echo.
    pause
    exit /b 1
)
echo       OK — ADB found.

:: ── Step 2: Show connected devices (info only) ───────────────────
echo.
echo [2/6] ADB devices:
adb devices
echo.

:: ── Step 3: Install APK ───────────────────────────────────────────
echo.
echo [3/6] Installing BlockAR APK ...

:: Auto-find APK if default name not found
if not exist "%APK_FILE%" (
    echo       BlockAR.apk not found, searching for any APK in project folder...
    for %%f in ("%~dp0*.apk") do (
        set APK_FILE=%%f
        echo       Found: %%f
        goto :found_apk
    )
    echo.
    echo ERROR: No APK found in %~dp0
    echo   In Unity: File ^> Build Settings ^> Android ^> Build
    echo   Save the APK in your BlockAR project folder.
    echo.
    pause
    exit /b 1
)
:found_apk

echo       Installing: %APK_FILE%
echo       (This takes ~20 seconds — look inside headset for prompts)
echo.
adb install -r "%APK_FILE%"
if errorlevel 1 (
    echo.
    echo ERROR: APK install failed.
    echo   Common fixes:
    echo     - Check the headset for an "Allow installation" popup
    echo     - Try: adb uninstall %PACKAGE%   then run this script again
    echo     - Make sure you used Android platform in Unity Build Settings
    echo.
    pause
    exit /b 1
)
echo.
echo       OK — BlockAR installed on Quest.

:: ── Step 4: Process DICOM and push .vol ──────────────────────────
echo.
echo [4/6] Checking .vol data file ...

if not exist "%VOL_FILE%" (
    echo       head_neck.vol not found — generating from DICOM ...
    goto :generate
)

python -c "
import struct, sys
try:
    with open(r'%VOL_FILE%', 'rb') as f:
        magic = f.read(4)
        f.read(4)
        w = struct.unpack('<I', f.read(4))[0]
        h = struct.unpack('<I', f.read(4))[0]
        d = struct.unpack('<I', f.read(4))[0]
    if magic != b'OVOL' or not (0 < w <= 1024 and 0 < h <= 1024 and 0 < d <= 1024):
        print('INVALID'); sys.exit(1)
    print(f'OK {w}x{h}x{d}')
except: print('INVALID'); sys.exit(1)
" 2>nul
if errorlevel 1 (
    echo       Existing file is invalid — regenerating ...
    goto :generate
)

for /f "tokens=*" %%i in ('python -c "
import struct
with open(r\"%VOL_FILE%\", \"rb\") as f:
    f.read(8)
    w=struct.unpack(\"<I\",f.read(4))[0]; h=struct.unpack(\"<I\",f.read(4))[0]; d=struct.unpack(\"<I\",f.read(4))[0]
print(f\"{w}x{h}x{d}\")
"') do set DIMS=%%i
echo       Found valid head_neck.vol  (%DIMS%)  — skipping regeneration.
goto :push

:generate
if not exist "%DICOM_FOLDER%" (
    echo.
    echo   NOTE: No DICOM folder found and no .vol file.
    echo   The app will show a test sphere instead of your CT scan.
    echo   That is normal for a first test — you can push data later.
    echo   Skipping .vol push.
    goto :launch
)
python "%PROCESSOR%" --input "%DICOM_FOLDER%" --output "%VOL_FILE%"
if errorlevel 1 (
    echo ERROR: Python processing failed. See error above.
    pause
    exit /b 1
)

:push
echo.
echo [5/6] Pushing head_neck.vol to Quest 3 ...
adb shell mkdir -p "/sdcard/Android/data/%PACKAGE%/files/" >nul 2>&1
adb push "%VOL_FILE%" "%QUEST_PATH%"
if errorlevel 1 (
    echo ERROR: ADB push failed. Make sure Quest is still connected.
    pause
    exit /b 1
)
echo       OK — CT data pushed to Quest.

:: ── Step 6: Launch app ────────────────────────────────────────────
:launch
echo.
echo [6/6] Launching BlockAR on Quest ...
adb shell am start -n "%PACKAGE%/%ACTIVITY%"
if errorlevel 1 (
    echo   (Could not auto-launch — open BlockAR manually in the headset.)
) else (
    echo       OK — BlockAR is launching on your Quest now!
)

echo.
echo ============================================================
echo   Done!  Put on your Quest — BlockAR should be loading.
echo ============================================================
echo.
echo   You should see:
echo     - Your real room through passthrough
echo     - A glowing blue test sphere floating in front of you
echo     - A "WiFi Load" button at the bottom centre of your view
echo.
echo   If the sphere is still purple: the shader needs recompiling.
echo     Try in Unity: Edit ^> Clear All Player Prefs, then rebuild.
echo.
echo   If nothing appears: check logcat for errors:
echo     adb logcat -s Unity
echo.
pause
