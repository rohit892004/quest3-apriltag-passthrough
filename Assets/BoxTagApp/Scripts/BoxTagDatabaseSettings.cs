// BoxTagDatabaseSettings.cs
// ScriptableObject with the tag database settings. The asset is created by Setup Scene at
// Assets/BoxTagApp/Data/BoxTagDatabaseSettings.asset. Edit its fields in the Inspector.
//
// The remote file (for example a GitHub Gist raw link) must have this format:
//   { "tags": [ {"id":1,"family":"tag36h11","label":"Box no 1"},
//               {"id":2,"family":"tag36h11","label":"Box no 2"} ] }
//
// The bundled fallback is Resources/BoxTagDefaultTags.json, which ships inside the APK.

using UnityEngine;

namespace BoxTagApp
{
    [CreateAssetMenu(fileName = "BoxTagDatabaseSettings", menuName = "BoxTagApp/Database Settings")]
    public class BoxTagDatabaseSettings : ScriptableObject
    {
        [Tooltip("Raw URL of tags.json (for example a GitHub Gist raw link). Leave the placeholder to use only the cache or the bundled default.")]
        public string RemoteUrl = "REPLACE_WITH_GIST_RAW_URL";

        [Tooltip("Seconds to wait for the remote file before falling back to the cache or the bundled default.")]
        public int TimeoutSeconds = 8;

        [Tooltip("File name for the local cache, stored in Application.persistentDataPath.")]
        public string CacheFileName = "boxtag_tags.json";

        [Tooltip("Bundled fallback, loaded from Resources/BoxTagDefaultTags.json.")]
        public string BundledResourceName = "BoxTagDefaultTags";
    }
}
