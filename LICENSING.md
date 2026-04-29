# RegionalAR — License Key System

Internal documentation for the offline license key system.

## How It Works

The license system uses HMAC-SHA256 to generate deterministic keys from a
shared secret + user code. No server is required — validation happens
entirely offline.

### Flow

1. User purchases RegionalAR on the Meta Quest Store.
2. In the Quest app, user taps **LICENSE** on the control panel.
3. The Quest app auto-generates a random 6-character code (e.g. `K7XM3P`) and saves it locally.
4. The license panel displays both the **code** and the **license key** (`RGNL-XXXX-XXXX-XXXX-XXXX`).
5. User opens the RegionalAR Desktop app and enters:
   - **Username** = the 6-character code from the Quest app
   - **License Key** = the key shown on the Quest app
6. Desktop app validates using the same HMAC secret and saves to `.regionalar_license`.
7. On subsequent launches, the desktop app reads the saved license file — no re-entry needed.

### User Instructions (shown in Quest app)

1. Open the desktop app
2. Enter **YOUR CODE** as the username
3. Enter the **LICENSE KEY**
4. Click **Activate**

Users can tap **NEW CODE** to regenerate a fresh code/key pair if needed.

### Key Format

```
RGNL-XXXX-XXXX-XXXX-XXXX
```

Derived from: `HMAC-SHA256(secret, lowercase(code))` → first 16 hex chars, grouped in 4.

### Files

| File | Purpose |
|------|---------|
| `Assets/Scripts/LicenseKeyGenerator.cs` | Quest-side key generation (C#) |
| `Assets/Scripts/WiFiDownloader.cs` | License panel UI in Quest app (BuildLicensePanel) |
| `license_manager.py` | Desktop-side key validation (Python) |
| `dicom_processor.py` | License gate at startup (license dialog + gate) |

### Shared Secret

Both `LicenseKeyGenerator.cs` and `license_manager.py` contain the same HMAC secret:

```
6n09hzVNsk4N44K431AJp5dOs-ciY7RHcACQzre3KMswwMdPBIO118vyOI528H-L
```

**IMPORTANT:** Keep this secret private. If someone extracts it, they can generate valid keys.

### Master Key

For developer testing and support, a master key is defined in `license_manager.py`:

```
RGNL-1D92-1B9C-8C9D-614E
```

This unlocks any installation regardless of the username entered. Do not share with users.

### CLI Usage (Desktop)

Generate a key manually:
```bash
python license_manager.py generate <code>
```

Validate a key:
```bash
python license_manager.py validate <code> <key>
```

Check if current machine is licensed:
```bash
python license_manager.py check
```
