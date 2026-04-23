"""
RegionalAR Desktop — Offline License Key Validation
====================================================
Uses HMAC-SHA256 to generate and validate license keys.

HOW IT WORKS:
  1. User purchases RegionalAR on Meta Quest Store.
  2. The Quest app reads the user's Oculus ID and generates a license key
     using the shared secret (see LicenseKeyGenerator.cs for Unity side).
  3. User enters the key in the desktop app on first launch.
  4. The desktop app validates the key offline using the same HMAC secret.

KEY FORMAT:
  RGNL-XXXX-XXXX-XXXX-XXXX  (20 hex chars in 4 groups, prefixed with RGNL)

The key is derived from:  HMAC-SHA256(secret, user_seed)
where user_seed is any short string the user provides (e.g. their Oculus
username or a code shown in the Quest app).

For simplicity, the current implementation uses a master unlock key plus
per-user keys. The master key allows you (the developer) to unlock any
installation for testing or support.
"""

import hashlib
import hmac
import json
import os
import sys

# ── Shared secret (CHANGE THIS before shipping!) ──
# This same secret must be embedded in the Quest app's LicenseKeyGenerator.cs.
# Keep it private — if someone extracts it they can generate keys.
_SECRET = b"6n09hzVNsk4N44K431AJp5dOs-ciY7RHcACQzre3KMswwMdPBIO118vyOI528H-L"

# ── Master unlock key (for developer/testing) ──
_MASTER_KEY = "RGNL-1D92-1B9C-8C9D-614E"

# ── License file location ──
def _license_path():
    """Store the license file next to the executable or script."""
    if getattr(sys, 'frozen', False):
        base = os.path.dirname(sys.executable)
    else:
        base = os.path.dirname(os.path.abspath(__file__))
    return os.path.join(base, ".regionalar_license")


def generate_key(user_seed: str) -> str:
    """
    Generate a license key from a user seed string.
    This runs on the Quest side (C# equivalent) and is included here
    for testing and manual key generation.
    """
    user_seed = user_seed.strip().lower()
    mac = hmac.new(_SECRET, user_seed.encode('utf-8'), hashlib.sha256)
    hex_digest = mac.hexdigest()[:16].upper()
    # Format as RGNL-XXXX-XXXX-XXXX-XXXX
    return f"RGNL-{hex_digest[:4]}-{hex_digest[4:8]}-{hex_digest[8:12]}-{hex_digest[12:16]}"


def validate_key(user_seed: str, key: str) -> bool:
    """
    Validate a license key against a user seed.
    Returns True if the key is valid for this seed, or if it's the master key.
    """
    key = key.strip().upper()

    # Check master key
    if key == _MASTER_KEY:
        return True

    # Check HMAC-derived key
    expected = generate_key(user_seed)
    return hmac.compare_digest(key, expected)


def is_licensed() -> bool:
    """Check if a valid license file exists."""
    path = _license_path()
    if not os.path.exists(path):
        return False
    try:
        with open(path, 'r') as f:
            data = json.load(f)
        seed = data.get('seed', '')
        key = data.get('key', '')
        return validate_key(seed, key)
    except (json.JSONDecodeError, KeyError, IOError):
        return False


def save_license(user_seed: str, key: str) -> bool:
    """Validate and save license to disk. Returns True if valid."""
    if not validate_key(user_seed, key):
        return False
    path = _license_path()
    with open(path, 'w') as f:
        json.dump({'seed': user_seed.strip().lower(), 'key': key.strip().upper()}, f)
    return True


def clear_license():
    """Remove saved license (for testing)."""
    path = _license_path()
    if os.path.exists(path):
        os.remove(path)


# ── CLI for manual key generation ──
if __name__ == '__main__':
    import argparse
    parser = argparse.ArgumentParser(description="RegionalAR License Key Manager")
    sub = parser.add_subparsers(dest='cmd')

    gen = sub.add_parser('generate', help='Generate a key for a user seed')
    gen.add_argument('seed', help='User seed (e.g. Oculus username)')

    val = sub.add_parser('validate', help='Validate a key')
    val.add_argument('seed', help='User seed')
    val.add_argument('key', help='License key')

    chk = sub.add_parser('check', help='Check if current machine is licensed')

    args = parser.parse_args()

    if args.cmd == 'generate':
        key = generate_key(args.seed)
        print(f"Seed: {args.seed}")
        print(f"Key:  {key}")
    elif args.cmd == 'validate':
        ok = validate_key(args.seed, args.key)
        print(f"Valid: {ok}")
    elif args.cmd == 'check':
        print(f"Licensed: {is_licensed()}")
        print(f"File: {_license_path()}")
    else:
        parser.print_help()
