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
    private const string MaterialPath = Root + "/Materials/BoxTagCameraFeed.mat";

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
        InstantiateCameraRig();

        // The ONE camera owner in this scene: MRUK PassthroughCameraAccess.
        PassthroughCameraAccess cameraAccess = CreateCameraAccess();

        // World-space panel that shows the feed.
        Renderer screen = CreateFeedPanel();

        // Feed controller: wires the camera access and the panel together.
        var feedGo = new GameObject("BoxTagCameraFeed");
        var feed = feedGo.AddComponent<BoxTagCameraFeed>();
        SetPrivate(feed, "m_cameraAccess", cameraAccess);
        SetPrivate(feed, "m_screen", screen);

        EnsureFolder(Root + "/Scene");
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

    private static void InstantiateCameraRig()
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
                PrefabUtility.InstantiatePrefab(prefab);
                return;
            }
        }
        Debug.LogWarning("[BoxTagAppSetup] OVRCameraRig prefab not found. Add a rig manually.");
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

    private static Renderer CreateFeedPanel()
    {
        GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = "CameraFeedPanel";
        // The panel is positioned by the script at runtime; no collider is needed.
        Object.DestroyImmediate(quad.GetComponent<MeshCollider>());

        Material material = GetOrCreateMaterial();
        Renderer renderer = quad.GetComponent<Renderer>();
        renderer.sharedMaterial = material;
        return renderer;
    }

    private static Material GetOrCreateMaterial()
    {
        var existing = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (existing != null)
        {
            return existing;
        }
        Shader shader = Shader.Find("Unlit/Texture");
        var material = new Material(shader) { name = "BoxTagCameraFeed" };
        AssetDatabase.CreateAsset(material, MaterialPath);
        return material;
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
