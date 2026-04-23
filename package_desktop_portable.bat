@echo off
REM BlockAR — Portable Desktop Package Builder
REM
REM Produces  dist\BlockAR-Desktop-Portable.zip  containing everything a
REM user needs to run the processor on any Windows machine with NO Python
REM install required. The .exe is already a PyInstaller build that bundles
REM Python + numpy + scipy + SimpleITK + pydicom + skimage.
REM
REM Contents of the zip:
REM   BlockAR.exe         — standalone processor (56 MB)
REM   Run BlockAR.bat     — double-click launcher (just runs the .exe)
REM   README.txt          — install + usage notes for recipients
REM   Sample.vol          — optional sample volume file (if present)
REM
REM Prereq: dist\BlockAR.exe already built. If missing, run
REM         build_desktopapp_exe.bat first.

setlocal EnableDelayedExpansion
cd /d "%~dp0"

if not exist "dist\BlockAR.exe" (
    echo ERROR: dist\BlockAR.exe not found.
    echo Run  build_desktopapp_exe.bat  first to compile the executable.
    exit /b 1
)

set STAGE=dist\BlockAR-Desktop-Portable
if exist "!STAGE!" rmdir /s /q "!STAGE!"
mkdir "!STAGE!"

echo [package] Copying BlockAR.exe...
copy /y "dist\BlockAR.exe" "!STAGE!\BlockAR.exe" >nul

echo [package] Writing launcher...
> "!STAGE!\Run BlockAR.bat" echo @echo off
>> "!STAGE!\Run BlockAR.bat" echo cd /d "%%~dp0"
>> "!STAGE!\Run BlockAR.bat" echo start "" "BlockAR.exe"

echo [package] Writing README...
> "!STAGE!\README.txt" echo BlockAR Desktop - Portable Build
>> "!STAGE!\README.txt" echo ================================
>> "!STAGE!\README.txt" echo.
>> "!STAGE!\README.txt" echo WHAT THIS IS
>> "!STAGE!\README.txt" echo   A standalone DICOM-to-volume processor for the BlockAR Quest app.
>> "!STAGE!\README.txt" echo   Converts DICOM folders into .vol files, serves them over WiFi, or
>> "!STAGE!\README.txt" echo   saves them to a library folder the headset can load.
>> "!STAGE!\README.txt" echo.
>> "!STAGE!\README.txt" echo HOW TO RUN
>> "!STAGE!\README.txt" echo   1. Unzip this folder anywhere (Desktop, USB drive, Documents, ...)
>> "!STAGE!\README.txt" echo   2. Double-click "Run BlockAR.bat" (or BlockAR.exe directly)
>> "!STAGE!\README.txt" echo   3. No Python install required - everything is bundled.
>> "!STAGE!\README.txt" echo.
>> "!STAGE!\README.txt" echo SYSTEM REQUIREMENTS
>> "!STAGE!\README.txt" echo   Windows 10 or 11, 64-bit
>> "!STAGE!\README.txt" echo   ~200 MB free disk space (exe + temp files)
>> "!STAGE!\README.txt" echo   For AI segmentation: an extra ~4 GB for the TotalSegmentator
>> "!STAGE!\README.txt" echo   model (downloaded on first use).
>> "!STAGE!\README.txt" echo.
>> "!STAGE!\README.txt" echo FIRST-RUN NOTES
>> "!STAGE!\README.txt" echo   - Windows SmartScreen may warn about the unsigned exe. Click
>> "!STAGE!\README.txt" echo     "More info" then "Run anyway" if you trust the source.
>> "!STAGE!\README.txt" echo   - An antivirus may briefly scan the exe; this is normal because
>> "!STAGE!\README.txt" echo     PyInstaller bundles look unusual. It will only happen once.
>> "!STAGE!\README.txt" echo.
>> "!STAGE!\README.txt" echo DISCLAIMER
>> "!STAGE!\README.txt" echo   BlockAR is for educational use only. It is not a medical device.

REM Optional: include a sample .vol if one exists at repo root
if exist "Sample.vol" (
    echo [package] Including Sample.vol...
    copy /y "Sample.vol" "!STAGE!\Sample.vol" >nul
)

echo [package] Creating zip...
set ZIPFILE=dist\BlockAR-Desktop-Portable.zip
if exist "!ZIPFILE!" del "!ZIPFILE!"
powershell -NoProfile -Command ^
    "Compress-Archive -Path 'dist\BlockAR-Desktop-Portable\*' -DestinationPath '!ZIPFILE!' -Force"

if errorlevel 1 (
    echo [package] FAILED to create zip.
    exit /b 1
)

for %%F in ("!ZIPFILE!") do set SIZE=%%~zF
set /a SIZEMB=!SIZE!/1048576

echo.
echo ================================================================
echo   DONE
echo ================================================================
echo   Zip:   !ZIPFILE!  (!SIZEMB! MB)
echo   Send this one file to anyone - they just unzip and run.
echo ================================================================
endlocal
