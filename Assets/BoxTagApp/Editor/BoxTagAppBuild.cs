// BoxTagAppBuild.cs
// Editor-only helper that builds the BoxTagApp Android APK.
// Menu: Tools > BoxTagApp > Build APK.
//
// Headless (used for CI-style builds, no Editor UI needed):
//   Unity.exe -batchmode -nographics -quit -projectPath <project> ^
//     -executeMethod BoxTagAppBuild.BuildApkHeadless -logFile <log>
//
// Output: Builds/BoxTagApp_L4.apk (Builds/ is git-ignored).

using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class BoxTagAppBuild
{
    private const string BoxTagScene = "Assets/BoxTagApp/Scene/BoxTagApp.unity";
    private const string ApkPath = "Builds/BoxTagApp_L4.apk";

    // App identity. PLACEHOLDER package name: change "assignment" to your own name, e.g.
    // "com.yourname.boxtagapp". This is the only place to change it. Changing it installs a
    // separate app (it does not replace com.samples.passthroughcamera).
    private const string AndroidPackageName = "com.asas.boxtagapp";
    private const string ProductName = "BoxTag App";

    [MenuItem("Tools/BoxTagApp/Build APK")]
    public static void BuildApkMenu()
    {
        Build();
    }

    // Entry point for -executeMethod. Exit code 0 = success, 1 = failure.
    public static void BuildApkHeadless()
    {
        bool ok = Build();
        EditorApplication.Exit(ok ? 0 : 1);
    }

    // Debug materials for BoxTagLabels (loaded with Resources.Load at runtime; Shader.Find is not used there).
    private const string DebugMaterialFolder = "Assets/BoxTagApp/Resources";
    private const string DebugShaderName = "Unlit/Color"; // Built-in render pipeline (the project has no URP)

    private static readonly (string name, Color color)[] DebugMaterials =
    {
        ("BoxTagDebugRed", Color.red),
        ("BoxTagDebugGreen", Color.green),
        ("BoxTagDebugBlue", new Color(0.3f, 0.5f, 1f)),
        ("BoxTagDebugWhite", Color.white),
    };

    // Creates or updates the debug materials, and puts their shader in Always Included Shaders,
    // so the build cannot strip it (a stripped shader shows as pink).
    private static bool EnsureDebugMaterials()
    {
        Shader shader = Shader.Find(DebugShaderName);
        if (shader == null)
        {
            Debug.LogError("[BoxTagAppBuild] FAILED: shader '" + DebugShaderName + "' not found.");
            return false;
        }

        // Always Included Shaders lives in ProjectSettings/GraphicsSettings.asset.
        Object[] graphicsAssets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
        if (graphicsAssets.Length == 0)
        {
            Debug.LogError("[BoxTagAppBuild] FAILED: ProjectSettings/GraphicsSettings.asset not found.");
            return false;
        }
        var so = new SerializedObject(graphicsAssets[0]);
        SerializedProperty included = so.FindProperty("m_AlwaysIncludedShaders");
        bool present = false;
        for (int i = 0; i < included.arraySize; i++)
        {
            if (included.GetArrayElementAtIndex(i).objectReferenceValue == shader)
            {
                present = true;
                break;
            }
        }
        if (!present)
        {
            included.InsertArrayElementAtIndex(included.arraySize);
            included.GetArrayElementAtIndex(included.arraySize - 1).objectReferenceValue = shader;
            so.ApplyModifiedPropertiesWithoutUndo();
            Debug.Log("[BoxTagAppBuild] Added " + DebugShaderName + " to Always Included Shaders.");
        }

        Directory.CreateDirectory(DebugMaterialFolder);
        foreach (var (name, color) in DebugMaterials)
        {
            string path = DebugMaterialFolder + "/" + name + ".mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(material, path);
            }
            material.shader = shader;
            material.color = color;
            EditorUtility.SetDirty(material);
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        return true;
    }

    private static bool Build()
    {
        if (!EnsureDebugMaterials())
        {
            return false;
        }

        // Safety check: the APK must contain exactly one enabled scene, BoxTagApp.
        // The FRC StartScene and the sample scenes are disabled in Build Settings.
        string[] enabledScenes = EditorBuildSettings.scenes
            .Where(s => s.enabled)
            .Select(s => s.path)
            .ToArray();

        if (enabledScenes.Length != 1 || enabledScenes[0] != BoxTagScene)
        {
            Debug.LogError("[BoxTagAppBuild] FAILED: enabled build scenes must be exactly [" +
                           BoxTagScene + "]. Found: [" + string.Join(", ", enabledScenes) + "]");
            return false;
        }

        // Switch to Android first if needed (headless builds start with the host target).
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
        {
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
        }

        // Own app identity (not the sample's package). Written to ProjectSettings/ProjectSettings.asset.
        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, AndroidPackageName);
        PlayerSettings.productName = ProductName;

        Directory.CreateDirectory(Path.GetDirectoryName(ApkPath));

        var options = new BuildPlayerOptions
        {
            scenes = enabledScenes,
            locationPathName = ApkPath,
            target = BuildTarget.Android,
            targetGroup = BuildTargetGroup.Android,
            // Development build: keeps Debug.Log and logcat output so the L2+ tests can be checked.
            options = BuildOptions.Development,
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary summary = report.summary;

        if (summary.result == BuildResult.Succeeded)
        {
            Debug.Log("[BoxTagAppBuild] SUCCESS: " + ApkPath + " size=" + summary.totalSize +
                      " bytes, errors=" + summary.totalErrors + ", warnings=" + summary.totalWarnings);
            return true;
        }

        Debug.LogError("[BoxTagAppBuild] FAILED: result=" + summary.result +
                       ", errors=" + summary.totalErrors);
        return false;
    }
}
