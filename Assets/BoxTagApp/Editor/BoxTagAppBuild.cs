// BoxTagAppBuild.cs
// Editor-only helper that builds the BoxTagApp Android APK.
// Menu: Tools > BoxTagApp > Build APK (L1).
//
// Headless (used for CI-style builds, no Editor UI needed):
//   Unity.exe -batchmode -nographics -quit -projectPath <project> ^
//     -executeMethod BoxTagAppBuild.BuildApkHeadless -logFile <log>
//
// Output: Builds/BoxTagApp_L1.apk (Builds/ is git-ignored).

using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class BoxTagAppBuild
{
    private const string BoxTagScene = "Assets/BoxTagApp/Scene/BoxTagApp.unity";
    private const string ApkPath = "Builds/BoxTagApp_L1.apk";

    [MenuItem("Tools/BoxTagApp/Build APK (L1)")]
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

    private static bool Build()
    {
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
