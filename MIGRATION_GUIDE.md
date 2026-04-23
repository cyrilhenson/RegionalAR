# RegionalAR — Migration to a New Computer

Step-by-step for moving the full RegionalAR development environment (Unity
project + Python DICOM pipeline + snapshots/logs) onto a fresh Windows
machine. Compiled during the April 2026 build session — keep this file
in the project root so it migrates with the work.

---

## 1. What to copy (essential source)

From `C:\Users\Laurence\Desktop\RegionalAR`:

- `Assets/` — all Unity C# scripts and shaders (the bulk of the work)
- `Packages/` — Unity package manifest (lets Unity re-download deps)
- `ProjectSettings/` — Unity project config
- `dicom_processor.py` — Python pipeline
- `RegionalAR.spec` — PyInstaller config
- All `.bat` files (`release_desktop.bat`, `build_desktopapp_exe.bat`,
  `install_blockar.bat`, `deploy_to_quest.bat`, `package_desktop_portable.bat`)
- `build_log/` — rollback snapshots (keep these; they're your recovery net)
- `app_icon_512.png`, `RegionalAR.sln`, `RegionalAR.apk` (optional; can rebuild)
- `MIGRATION_GUIDE.md` (this file)

## 2. What NOT to copy — regenerates itself

- `Library/` — Unity cache, 5-10 GB, regenerates on first open
- `Temp/` — Unity temp files
- `build/` — PyInstaller intermediate
- `dist/` — PyInstaller output (rebuild from spec)

## 3. Make the migration zip

From a cmd window:

```
cd /d C:\Users\Laurence\Desktop
powershell "Compress-Archive -Path 'RegionalAR\Assets','RegionalAR\Packages','RegionalAR\ProjectSettings','RegionalAR\build_log','RegionalAR\*.py','RegionalAR\*.bat','RegionalAR\*.spec','RegionalAR\*.sln','RegionalAR\*.png','RegionalAR\*.apk','RegionalAR\MIGRATION_GUIDE.md' -DestinationPath RegionalAR_migrate.zip -Force"
```

Result is ~10-50 MB. Transfer via USB, OneDrive, or Google Drive.

## 4. On the new machine — install order

1. **Unity Hub + Unity** (same version as the source machine — check
   Unity → Help → About Unity on the old machine first). During Unity
   install, include:
   - **Android Build Support** module
   - **IL2CPP** submodule (mandatory for Quest builds)

2. **Meta XR SDK** — Unity pulls this automatically from
   `Packages/manifest.json` when you open the project. Nothing manual
   to install.

3. **Python 3.11** — install from python.org, currently 3.11.9 (or
   newest 3.11 patch with a Windows installer). On the first installer
   screen, **check "Add python.exe to PATH"**. Do NOT install the
   newest Python 3.13/3.14 — medical-imaging packages lag behind
   by 6-12 months. 3.11 is the safe sweet spot.

4. **Python deps** — one command:
   ```
   python -m pip install numpy pydicom SimpleITK scipy scikit-image trimesh fast_simplification blosc2 pandas pyinstaller TotalSegmentator
   ```
   Watch for the `Successfully installed ...` line. Note: TotalSegmentator
   pulls in ~2 GB of torch + nnU-Net + matplotlib deps.

5. **Meta Quest Developer Hub** (or just Android platform-tools for ADB)
   — for deploying APKs to the headset. MQDH is the easier path.

## 5. First-run on the new machine

1. Unzip `RegionalAR_migrate.zip` to e.g. `C:\Users\<you>\Desktop\RegionalAR`
2. Launch Unity Hub → Add → point to the folder → Unity opens and
   regenerates `Library/` (5-15 min depending on disk speed)
3. In a cmd window inside the folder, run:
   ```
   release_desktop.bat
   ```
   to rebuild `dist\RegionalAR.exe` from source (same 15-20 min wait).
   Fresh builds need torch + nnU-Net bundling.
4. Lock in a known-good starting point:
   ```
   build_log\snapshot.bat fresh-machine-setup
   ```
5. Build the Quest APK in Unity: File → Build Settings → Android →
   Build (output should go next to the project as `RegionalAR.apk`).
6. Deploy to Quest via MQDH or ADB.

## 6. Claude / Cowork conversation migration

Conversations are tied to your Cowork account, not to the machine:

- Install Cowork on the new computer (or use Claude in-browser at
  claude.ai)
- Sign in with the same account
- Your conversation history will be there in the sidebar, including
  the RegionalAR build sessions
- For an offline record, right-click any conversation in the sidebar →
  Export → saves as markdown

## 7. Common first-run issues

**"Python 3.11 but `pip` installs to Python 3.9"** — classic
PATH-ordering problem. Always use `python -m pip install ...` instead
of bare `pip install ...`. The `-m pip` form forces pip to use whatever
Python `python` resolves to, not whatever `pip.exe` was found first
in PATH.

**"No module named blosc2"** after build — means PyInstaller couldn't
find it in the env it built against. Check with:
```
python -c "import blosc2, sys; print(blosc2.__version__, sys.executable)"
```
The `sys.executable` path must contain `Python311`, not `Python39`.

**"'NoneType' object has no attribute 'write'"** during AI mode — the
stdout shim at the top of `dicom_processor.py` should prevent this. If
it reappears, the shim got deleted; see the `_NullStream` class near
line 30 of the file.

**TotalSegmentator prompts for model download on every run** — the
models cache in `%USERPROFILE%\.totalsegmentator`. On a new machine,
the first run downloads ~1.5 GB and takes 5-10 minutes. Subsequent
runs are fast.

## 8. Rebuilding from a snapshot

If the new machine's code gets into a weird state:
```
cd build_log
rollback.bat <some-snapshot-name>.zip
```
It auto-snapshots the current state first (as `pre-rollback-*`) so
you can undo the rollback if needed.

---

*Last updated: April 2026 — during Phase 2 (vessel mesh) work. If
significant architectural changes happen (Phase 3, new dependencies,
Python version bump, etc.), append a dated section rather than
rewriting — past sessions may need the old steps.*
