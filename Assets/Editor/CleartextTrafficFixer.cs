/// RegionalAR — Post-build script to ensure cleartext HTTP is allowed on Android.
/// This runs AFTER Unity and Meta SDK merge their manifests, so our change sticks.
/// Required for WiFi volume transfer from dicom_processor.py (HTTP on local network).

#if UNITY_ANDROID
using UnityEditor.Android;
using UnityEngine;
using System.IO;
using System.Text.RegularExpressions;

public class CleartextTrafficFixer : IPostGenerateGradleAndroidProject
{
    public int callbackOrder => 99;  // run late, after other processors

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        // The final merged manifest is at: {path}/src/main/AndroidManifest.xml
        string manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");

        if (!File.Exists(manifestPath))
        {
            // Try the launcher module path (Unity 2022+ structure)
            string launcherPath = path.Replace("unityLibrary", "launcher");
            manifestPath = Path.Combine(launcherPath, "src", "main", "AndroidManifest.xml");
        }

        if (!File.Exists(manifestPath))
        {
            Debug.LogWarning("[RegionalAR] CleartextTrafficFixer: Could not find AndroidManifest.xml");
            return;
        }

        string manifest = File.ReadAllText(manifestPath);

        // Check if usesCleartextTraffic is already set to true
        if (manifest.Contains("android:usesCleartextTraffic=\"true\""))
        {
            Debug.Log("[RegionalAR] CleartextTrafficFixer: cleartext already enabled.");
            return;
        }

        // If it's set to false, replace it
        if (manifest.Contains("android:usesCleartextTraffic=\"false\""))
        {
            manifest = manifest.Replace(
                "android:usesCleartextTraffic=\"false\"",
                "android:usesCleartextTraffic=\"true\"");
        }
        // If the attribute is missing entirely, add it to the <application> tag
        else if (!manifest.Contains("android:usesCleartextTraffic"))
        {
            manifest = Regex.Replace(
                manifest,
                @"<application\b",
                "<application android:usesCleartextTraffic=\"true\"");
        }

        File.WriteAllText(manifestPath, manifest);
        Debug.Log($"[RegionalAR] CleartextTrafficFixer: enabled cleartext HTTP in {manifestPath}");

        // Also fix the unityLibrary manifest if we're in the launcher
        string unityLibManifest = Path.Combine(
            Directory.GetParent(path).FullName,
            "unityLibrary", "src", "main", "AndroidManifest.xml");
        if (File.Exists(unityLibManifest) && unityLibManifest != manifestPath)
        {
            string ulManifest = File.ReadAllText(unityLibManifest);
            if (ulManifest.Contains("android:usesCleartextTraffic=\"false\""))
            {
                ulManifest = ulManifest.Replace(
                    "android:usesCleartextTraffic=\"false\"",
                    "android:usesCleartextTraffic=\"true\"");
                File.WriteAllText(unityLibManifest, ulManifest);
                Debug.Log($"[RegionalAR] CleartextTrafficFixer: also fixed unityLibrary manifest");
            }
            else if (!ulManifest.Contains("android:usesCleartextTraffic"))
            {
                ulManifest = Regex.Replace(
                    ulManifest,
                    @"<application\b",
                    "<application android:usesCleartextTraffic=\"true\"");
                File.WriteAllText(unityLibManifest, ulManifest);
                Debug.Log($"[RegionalAR] CleartextTrafficFixer: added cleartext to unityLibrary manifest");
            }
        }
    }
}
#endif
