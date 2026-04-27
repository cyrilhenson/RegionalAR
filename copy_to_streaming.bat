@echo off
REM Copy regenerated sample volumes from SampleVolumes/ to Assets/StreamingAssets/
REM Run this AFTER: python generate_volumes.py --bodyparts3d

set SRC=SampleVolumes
set DST=Assets\StreamingAssets

echo Copying 8 anatomy volumes to StreamingAssets...

for %%R in (shoulder_right shoulder_left hip_right hip_left thigh_right thigh_left knee_right knee_left) do (
    echo   %%R ...
    copy /Y "%SRC%\%%R.vol"  "%DST%\%%R.vol"  >nul
    copy /Y "%SRC%\%%R.omsh" "%DST%\%%R.omsh" >nul
    copy /Y "%SRC%\%%R.vmsh" "%DST%\%%R.vmsh" >nul
    copy /Y "%SRC%\%%R.nmsh" "%DST%\%%R.nmsh" >nul
    copy /Y "%SRC%\%%R.mmsh" "%DST%\%%R.mmsh" >nul
)

echo.
echo Done! 40 files copied (8 regions x 5 files each).
echo Head-Neck_CTA unchanged (already in StreamingAssets).
echo.
echo SAMPLES_MARKER_VERSION bumped to "3" in WiFiDownloader.cs
echo so the app will re-install these on next launch.
pause
