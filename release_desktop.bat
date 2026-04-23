@echo off
REM BlockAR — One-click desktop release
REM
REM Rebuilds  dist\BlockAR.exe  from the current dicom_processor.py,
REM then wraps it into  dist\BlockAR-Desktop-Portable.zip.
REM
REM Use this any time you've edited dicom_processor.py and want a fresh
REM distributable zip. Typical runtime: 3-20 minutes depending on whether
REM the build cache is warm (rebuild only) or cold (full re-collection).

setlocal
cd /d "%~dp0"

echo.
echo ================================================================
echo   BlockAR Desktop — FULL RELEASE (rebuild exe + repackage zip)
echo ================================================================
echo.

REM ── Pre-flight cleanup ───────────────────────────────────────────
REM Kill any running BlockAR.exe (file is locked by the OS otherwise),
REM then delete the stale exe + build cache so PyInstaller produces a
REM genuinely fresh binary. Errors are silenced because the first time
REM you run a clean build there's nothing to kill or delete.
echo [0/2] Pre-flight cleanup (kill running exe, clear stale build) ...
taskkill /F /IM BlockAR.exe /T >nul 2>&1
if exist "dist\BlockAR.exe"                  del /F "dist\BlockAR.exe" >nul 2>&1
if exist "dist\BlockAR-Desktop-Portable.zip" del /F "dist\BlockAR-Desktop-Portable.zip" >nul 2>&1
if exist "dist\BlockAR-Desktop-Portable"     rmdir /s /q "dist\BlockAR-Desktop-Portable" >nul 2>&1
echo       Done.
echo.

echo [1/2] Rebuilding BlockAR.exe from dicom_processor.py ...
call "%~dp0build_desktopapp_exe.bat"
if errorlevel 1 (
    echo.
    echo [release] FAILED at step 1 (exe build). Aborting.
    exit /b 1
)

echo.
echo [2/2] Packaging into portable zip ...
call "%~dp0package_desktop_portable.bat"
if errorlevel 1 (
    echo.
    echo [release] FAILED at step 2 (zip package). Aborting.
    exit /b 1
)

echo.
echo ================================================================
echo   RELEASE COMPLETE
echo ================================================================
echo   Fresh zip:  dist\BlockAR-Desktop-Portable.zip
echo   Fresh exe:  dist\BlockAR.exe
echo ================================================================
endlocal
