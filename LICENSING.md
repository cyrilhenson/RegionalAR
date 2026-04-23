# RegionalAR — License Key System

Internal documentation for the offline license key system.

## How It Works

The license system uses HMAC-SHA256 to generate deterministic keys from a
shared secret + user seed. No server is required — validation happens
entirely offline.

### Flow

1. User purchases RegionalAR on the Meta Quest Store.
2. In the Quest app, user taps "Desktop License" and enters a username.
3. The Quest app generates a license key using `LicenseKeyGenerator.GenerateKey(username)`.
4. User sees their key displayed as: `RGNL-XXXX-XXXX-XXXX-XXXX`
5. User opens the RegionalAR Desktop app, enters their username + key.
6. Desktop app validates using the same HMAC secret and saves to `.regionalar_license`.
7. On subsequent launches, the desktop app reads the saved license file — no re-entry needed.

### Key Format

```
RGNL-XXXX-XXXX-XXXX-XXXX
```

Derived from: `HMAC-SHA256(secret, lowercase(seed))` → first 16 hex chars, grouped in 4.

### Files

| File | Purpose |
|------|---------|
| `Assets/Scripts/LicenseKeyGenerator.cs` | Quest-side key generation (C#) |
| `RegionalAR-Desktop/license_manager.py` | Desktop-side key validation (Python) |
| `RegionalAR-Desktop/dicom_processor.py` | License gate at startup (lines at bottom) |

### Shared Secret

Both files contain the same HMAC secret:

```
RegionalAR-2026-LicenseKey-Secret-CHANGE-ME
```

**IMPORTANT:** Change this to a unique random string before shipping the paid version. The secret must match in both `LicenseKeyGenerator.cs` and `license_manager.py`.

### Master Key

For developer testing and support, a master key is defined in `license_manager.py`:

```
RGNL-MSTR-DEV0-2026-XKEY
```

This unlocks any installation regardless of the seed entered. Change it before shipping.

### Quest-Side Integration

To add the license key UI to the Quest app, call the static method from
your UI code (e.g. a button in the control panel):

```csharp
string username = // get from user input field
string key = LicenseKeyGenerator.GenerateKey(username);
// Display key to user in a text field they can read
```

### CLI Usage (Desktop)

Generate a key manually:
```bash
python license_manager.py generate <username>
```

Validate a key:
```bash
python license_manager.py validate <username> <key>
```

Check if current machine is licensed:
```bash
python license_manager.py check
```

### Test Keys

| Seed | Key |
|------|-----|
| testuser | RGNL-7532-B1DC-EB4A-0EE6 |
| laurence | RGNL-F038-01C3-441C-F114 |
| demo | RGNL-5E38-5A71-A8CE-44A6 |

These are generated from the default secret. They will change when you update the secret.
