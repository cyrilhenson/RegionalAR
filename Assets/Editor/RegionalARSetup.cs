/// RegionalAR - Setup Scene (Editor utility)
/// Run via  RegionalAR → Setup Scene  in the Unity menu bar.
/// Creates the material, wires the shader, and configures the scene.

using UnityEngine;
using UnityEditor;
using System.IO;

public class RegionalARSetup
{
    private const string MATERIAL_PATH = "Assets/Materials/RegionalAR_Volume.mat";
    private const string SHADER_NAME   = "RegionalAR/VolumeRaymarch";

    [MenuItem("RegionalAR/Setup Scene")]
    static void SetupScene()
    {
        // ── 1. Find shader ─────────────────────────────────────────
        Shader shader = Shader.Find(SHADER_NAME);
        if (shader == null)
        {
            EditorUtility.DisplayDialog("RegionalAR Setup",
                $"Shader '{SHADER_NAME}' not found.\n\n" +
                "Make sure  Assets/Shaders/VolumeRaymarch.shader  is in the project.",
                "OK");
            return;
        }

        // ── 2. Create / refresh material ───────────────────────────
        Directory.CreateDirectory("Assets/Materials");

        Material mat = AssetDatabase.LoadAssetAtPath<Material>(MATERIAL_PATH);
        if (mat == null)
        {
            mat = new Material(shader) { name = "RegionalAR_Volume" };
            AssetDatabase.CreateAsset(mat, MATERIAL_PATH);
        }
        else
        {
            mat.shader = shader;
        }

        // Default property values
        mat.SetFloat("_AlphaScale", 1.5f);
        mat.SetInt  ("_MaxSteps",   128);
        mat.SetFloat("_Threshold",  0.02f);
        EditorUtility.SetDirty(mat);
        AssetDatabase.SaveAssets();

        // ── 3. Find or create Volume GameObject ────────────────────
        GameObject volGO = GameObject.Find("Volume");
        if (volGO == null)
        {
            volGO      = GameObject.CreatePrimitive(PrimitiveType.Cube);
            volGO.name = "Volume";
        }

        // ── 4. Attach / configure scripts ──────────────────────────
        VolumeRenderer vr = volGO.GetComponent<VolumeRenderer>()
                         ?? volGO.AddComponent<VolumeRenderer>();
        vr.volumeMaterial = mat;
        EditorUtility.SetDirty(vr);

        if (volGO.GetComponent<WiFiDownloader>() == null)
            volGO.AddComponent<WiFiDownloader>();

        if (volGO.GetComponent<HandInteraction>() == null)
            volGO.AddComponent<HandInteraction>();

        // ── 5. Assign material to MeshRenderer ─────────────────────
        MeshRenderer mr = volGO.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            mr.sharedMaterial = mat;
            EditorUtility.SetDirty(mr);
        }

        // ── 6. Mark scene dirty so user is prompted to save ────────
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());

        Debug.Log("[RegionalAR] Setup complete.");

        EditorUtility.DisplayDialog("RegionalAR Setup Complete",
            "Scene is ready!\n\n" +
            "• Volume GameObject configured\n" +
            "• VolumeRenderer  ✓\n" +
            "• WiFiDownloader  ✓\n" +
            "• HandInteraction ✓\n" +
            "• RegionalAR_Volume material  ✓\n\n" +
            "Save the scene (Ctrl+S), then build and deploy to Quest 3.",
            "OK");
    }

    [MenuItem("RegionalAR/About")]
    static void About()
    {
        EditorUtility.DisplayDialog("RegionalAR",
            "RegionalAR — Regional Anesthesia Volume Renderer\n\n" +
            "Meta Quest 3 AR holographic DICOM viewer.\n\n" +
            "WiFi transfer: port 8765\n" +
            "ADB push path: /sdcard/Android/data/com.DefaultCompany.RegionalAR/files/head_neck.vol",
            "Close");
    }
}
