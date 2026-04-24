@echo off
echo ============================================
echo  RegionalAR — Build Standalone Executable
echo ============================================
echo.

:: Check PyInstaller is installed
where pyinstaller >nul 2>&1
if %errorlevel% neq 0 (
    echo PyInstaller not found. Installing...
    pip install pyinstaller
    echo.
)

echo Building RegionalAR.exe from RegionalAR.spec...
echo This may take a few minutes.
echo.
pyinstaller RegionalAR.spec

if %errorlevel% equ 0 (
    echo.
    echo ============================================
    echo  BUILD SUCCESSFUL
    echo  Output: dist\RegionalAR.exe
    echo ============================================
) else (
    echo.
    echo ============================================
    echo  BUILD FAILED — check errors above
    echo ============================================
)

echo.
pause
