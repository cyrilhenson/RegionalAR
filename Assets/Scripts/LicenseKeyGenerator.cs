using System;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

/// <summary>
/// Generates offline license keys for the RegionalAR Desktop companion app.
///
/// HOW IT WORKS:
///   1. User purchases RegionalAR on the Quest Store.
///   2. This script generates a license key from a user-chosen seed (e.g. their name).
///   3. The key + seed are displayed in the Quest app's UI.
///   4. User enters both into the desktop app to unlock it.
///
/// The same HMAC-SHA256 secret is shared between this script and the
/// desktop app's license_manager.py. Keep this secret private.
/// </summary>
public class LicenseKeyGenerator : MonoBehaviour
{
    // ── IMPORTANT: This must match _SECRET in license_manager.py ──
    private const string SECRET = "6n09hzVNsk4N44K431AJp5dOs-ciY7RHcACQzre3KMswwMdPBIO118vyOI528H-L";

    /// <summary>
    /// Generate a license key from a user seed string.
    /// </summary>
    /// <param name="userSeed">User-provided seed (e.g. username, shown in app)</param>
    /// <returns>Formatted license key: RGNL-XXXX-XXXX-XXXX-XXXX</returns>
    public static string GenerateKey(string userSeed)
    {
        if (string.IsNullOrWhiteSpace(userSeed))
            return null;

        userSeed = userSeed.Trim().ToLowerInvariant();

        byte[] secretBytes = Encoding.UTF8.GetBytes(SECRET);
        byte[] seedBytes = Encoding.UTF8.GetBytes(userSeed);

        using (var hmac = new HMACSHA256(secretBytes))
        {
            byte[] hash = hmac.ComputeHash(seedBytes);
            // Take first 8 bytes → 16 hex chars
            string hex = BitConverter.ToString(hash, 0, 8).Replace("-", "").ToUpperInvariant();
            // Format as RGNL-XXXX-XXXX-XXXX-XXXX
            return $"RGNL-{hex.Substring(0, 4)}-{hex.Substring(4, 4)}-{hex.Substring(8, 4)}-{hex.Substring(12, 4)}";
        }
    }

    /// <summary>
    /// Validate a license key against a user seed.
    /// </summary>
    public static bool ValidateKey(string userSeed, string key)
    {
        if (string.IsNullOrWhiteSpace(userSeed) || string.IsNullOrWhiteSpace(key))
            return false;

        string expected = GenerateKey(userSeed);
        return string.Equals(key.Trim(), expected, StringComparison.OrdinalIgnoreCase);
    }
}
