// BoxTagAppSetup.cs
// Editor-only helper. Menu: Tools > BoxTagApp > Setup Scene.
// It builds Assets/BoxTagApp/Scene/BoxTagApp.unity from scratch and makes it the only
// enabled scene in Build Settings, so the APK does not start the FRC StartScene.
//
// It can also be run headless (used for batch builds):
//   Unity -batchmode -quit -projectPath <project> -executeMethod BoxTagApp.BoxTagAppSetup.SetupScene

using System.Collections.Generic;
using System.Linq;
using BoxTagApp;
using Meta.XR;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class BoxTagAppSetup
{
    private const string Root = "Assets/BoxTagApp";
    private const string ScenePath = Root + "/Scene/BoxTagApp.unity";

    // Prefab from the sample project that already contains the MRUK PassthroughCameraAccess component.
    private const string CameraPrefabPath =
        "Assets/PassthroughCameraApiSamples/PassthroughCamera/Prefabs/PassthroughCameraAccessPrefab.prefab";

    [MenuItem("Tools/BoxTagApp/Setup Scene")]
    public static void SetupScene()
    {
        EnsureFolder(Root);
        EnsureFolder(Root + "/Scene");
        EnsureFolder(Root + "/Materials");
        EnsureFolder(Root + "/Prefabs");

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // Light so the scene is not black in the editor preview.
        var lightGo = new GameObject("Directional Light");
        lightGo.AddComponent<Light>().type = LightType.Directional;
        lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        // Camera rig (head tracking and the main camera used to place the panel).
        GameObject rig = InstantiateCameraRig();

        // The ONE camera owner in this scene: MRUK PassthroughCameraAccess.
        PassthroughCameraAccess cameraAccess = CreateCameraAccess();

        // Layout: owns every panel (world-space canvases built at runtime) and its text sizes.
        // Values are Inspector fields on this object.
        var layoutGo = new GameObject("BoxTagLayout");
        var layout = layoutGo.AddComponent<BoxTagLayout>();

        // Feed controller: wires the camera access and the layout together.
        var feedGo = new GameObject("BoxTagCameraFeed");
        var feed = feedGo.AddComponent<BoxTagCameraFeed>();
        SetPrivate(feed, "m_cameraAccess", cameraAccess);
        SetPrivate(feed, "m_layout", layout);

        // L2: detector (worker-thread AprilTag tag36h11) and capture controller.
        var detectorGo = new GameObject("BoxTagDetector");
        var detector = detectorGo.AddComponent<BoxTagDetector>();

        // L3: tag ID -> label database (settings asset is created once and kept on later runs).
        var databaseGo = new GameObject("BoxTagDatabase");
        var database = databaseGo.AddComponent<BoxTagDatabase>();
        SetPrivate(database, "m_settings", GetOrCreateDatabaseSettings());
        SetPrivate(database, "m_layout", layout);

        var captureGo = new GameObject("BoxTagCapture");
        var capture = captureGo.AddComponent<BoxTagCapture>();
        SetPrivate(capture, "m_cameraAccess", cameraAccess);
        SetPrivate(capture, "m_detector", detector);
        SetPrivate(capture, "m_database", database);
        SetPrivate(capture, "m_layout", layout);

        EnsureFolder(Root + "/Scene");
        // Passthrough: applied after all objects exist so it also covers the rig's cameras.
        ConfigurePassthrough(rig);

        if (!EditorSceneManager.SaveScene(scene, ScenePath))
        {
            Debug.LogError("[BoxTagAppSetup] Failed to save " + ScenePath);
            return;
        }

        ApplyBuildSettings();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log("[BoxTagAppSetup] Scene built: " + ScenePath +
                  ". It is the only enabled scene in Build Settings.");
    }

    // Puts BoxTagApp at index 0 and enabled. Other scenes stay in the list but are disabled,
    // so they are not built into the APK and nothing is deleted from the project.
    private static void ApplyBuildSettings()
    {
        List<EditorBuildSettingsScene> scenes = EditorBuildSettings.scenes.ToList();
        scenes.RemoveAll(s => s.path == ScenePath);
        foreach (EditorBuildSettingsScene s in scenes)
        {
            s.enabled = false;
        }
        scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
        EditorBuildSettings.scenes = scenes.ToArray();
    }

    // Returns the rig instance, or null if the prefab was not found.
    private static GameObject InstantiateCameraRig()
    {
        string[] guids = AssetDatabase.FindAssets("OVRCameraRig t:Prefab");
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (System.IO.Path.GetFileNameWithoutExtension(path) != "OVRCameraRig")
            {
                continue;
            }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab != null)
            {
                return (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            }
        }
        Debug.LogWarning("[BoxTagAppSetup] OVRCameraRig prefab not found. Add a rig manually.");
        return null;
    }

    // Full passthrough setup. Mirrors CameraViewer.unity from the sample:
    //   OVRManager.isInsightPassthroughEnabled = true,
    //   the CenterEye camera uses Solid Color with alpha 0 (not a skybox),
    //   and no skybox in the scene. The default Unity skybox was what hid the real room.
    private static void ConfigurePassthrough(GameObject rig)
    {
        // 1. Project-level passthrough support (Meta XR / OVRProjectConfig).
        OVRProjectConfig projectConfig = OVRProjectConfig.CachedProjectConfig;
        if (projectConfig.insightPassthroughSupport != OVRProjectConfig.FeatureSupport.Supported)
        {
            projectConfig.insightPassthroughSupport = OVRProjectConfig.FeatureSupport.Supported;
            OVRProjectConfig.CommitProjectConfig(projectConfig);
            Debug.Log("[BoxTagAppSetup] Set OVRProjectConfig.insightPassthroughSupport = Supported");
        }

        if (rig != null)
        {
            // 2. OVRManager on the rig: passthrough on, camera-access permission on startup.
            var manager = rig.GetComponentInChildren<OVRManager>(true);
            if (manager != null)
            {
                var so = new SerializedObject(manager);
                SetBool(so, "isInsightPassthroughEnabled", true);
                SetBool(so, "requestPassthroughCameraAccessPermissionOnStartup", true);
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            else
            {
                Debug.LogWarning("[BoxTagAppSetup] No OVRManager found on the rig; passthrough not enabled.");
            }

            // 3. Every camera under the rig: transparent background so the real room shows through.
            foreach (Camera cam in rig.GetComponentsInChildren<Camera>(true))
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            }
        }

        // 4. Explicit passthrough layer: reconstructed projection, underlay (behind virtual content).
        //    RequestPermissionsOnce also creates a default layer at runtime for non-StartScene scenes.
        var layerGo = new GameObject("OVRPassthroughLayer");
        var layer = layerGo.AddComponent<OVRPassthroughLayer>();
        layer.projectionSurfaceType = OVRPassthroughLayer.ProjectionSurfaceType.Reconstructed;
        layer.overlayType = OVROverlay.OverlayType.Underlay;

        // 5. No skybox and flat ambient light, so nothing from the default environment is drawn.
        RenderSettings.skybox = null;
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
    }

    // Database settings asset. Created on the first run; later runs keep the edited values
    // (for example the Gist URL you paste into the Inspector).
    private static BoxTagDatabaseSettings GetOrCreateDatabaseSettings()
    {
        string path = Root + "/Data/BoxTagDatabaseSettings.asset";
        var existing = AssetDatabase.LoadAssetAtPath<BoxTagDatabaseSettings>(path);
        if (existing != null)
        {
            return existing;
        }
        var settings = ScriptableObject.CreateInstance<BoxTagDatabaseSettings>();
        AssetDatabase.CreateAsset(settings, path);
        return settings;
    }

    private static void SetBool(SerializedObject so, string field, bool value)
    {
        SerializedProperty prop = so.FindProperty(field);
        if (prop == null)
        {
            Debug.LogWarning("[BoxTagAppSetup] OVRManager has no field '" + field + "'");
            return;
        }
        prop.boolValue = value;
    }

    private static PassthroughCameraAccess CreateCameraAccess()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(CameraPrefabPath);
        if (prefab != null)
        {
            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            return instance.GetComponent<PassthroughCameraAccess>();
        }

        Debug.LogWarning("[BoxTagAppSetup] Camera prefab not found at " + CameraPrefabPath +
                         ". Creating a bare PassthroughCameraAccess.");
        var go = new GameObject("PassthroughCameraAccess");
        return go.AddComponent<PassthroughCameraAccess>();
    }

    // Sets a private serialized field. Needed because the feed script's fields are private.
    private static void SetPrivate(Object target, string field, Object value)
    {
        var serialized = new SerializedObject(target);
        serialized.FindProperty(field).objectReferenceValue = value;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path))
        {
            return;
        }
        string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
        string name = System.IO.Path.GetFileName(path);
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, name);
    }
}
