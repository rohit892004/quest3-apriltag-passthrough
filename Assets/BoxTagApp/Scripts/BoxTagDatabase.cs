// BoxTagDatabase.cs
// L3: maps tag ID -> label from a database file. No ID or label is written in code.
//
// Load order at startup:
//   1. Bundled default (Resources/BoxTagDefaultTags.json). Loaded immediately, so lookups work
//      from the first frame. Status: "DB: offline default".
//   2. Local cache (persistentDataPath). If present, it replaces the bundled default. Status: "DB: cached".
//   3. Remote URL (settings). If it downloads and parses, it replaces the current data and is saved
//      to the cache. Status: "DB: online".
// If the remote fails, the cache (or the bundled default) stays in use.
//
// Left controller X (OVRInput.Button.Three) reloads the remote file. The result is shown briefly
// as "DB reloaded: N tags", then the normal status returns.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace BoxTagApp
{
    // JSON shape of tags.json. Field names must match the file exactly.
    [Serializable]
    public class BoxTagEntry
    {
        public int id;
        public string family;
        public string label;
    }

    [Serializable]
    public class BoxTagFile
    {
        public BoxTagEntry[] tags;
    }

    public class BoxTagDatabase : MonoBehaviour
    {
        [Tooltip("Settings asset (remote URL, timeout, cache name). Created by Setup Scene.")]
        [SerializeField] private BoxTagDatabaseSettings m_settings;

        [Tooltip("Layout that shows the database status line (set by Setup Scene).")]
        [SerializeField] private BoxTagLayout m_layout;

        // Only tag36h11 is supported in this app version.
        private const string SupportedFamily = "tag36h11";

        private readonly Dictionary<int, string> m_labels = new Dictionary<int, string>();
        private string m_status = "DB: starting";
        private bool m_reloading;

        /// <summary>Number of tags currently loaded.</summary>
        public int TagCount => m_labels.Count;

        private void Awake()
        {
            // Bundled default first, so lookups work immediately.
            TextAsset bundled = Resources.Load<TextAsset>(m_settings.BundledResourceName);
            if (bundled != null && TryParse(bundled.text, out Dictionary<int, string> bundledLabels))
            {
                Apply(bundledLabels, "DB: offline default");
            }
            else
            {
                Debug.LogError("[BoxTag] Bundled database missing or invalid: Resources/" + m_settings.BundledResourceName);
                SetStatus("DB: no data");
            }
        }

        private void Start()
        {
            StartCoroutine(StartupRoutine());
        }

        private void Update()
        {
            // Left controller X: reload the remote database.
            if (OVRInput.GetDown(OVRInput.Button.Three))
            {
                ReloadDatabase();
            }
        }

        /// <summary>Returns the label for a tag ID, or false if the ID is not in the database.</summary>
        public bool TryGetLabel(int id, out string label)
        {
            return m_labels.TryGetValue(id, out label);
        }

        /// <summary>Starts a remote reload (left X). Ignored while one is running.</summary>
        public void ReloadDatabase()
        {
            if (!m_reloading)
            {
                StartCoroutine(ReloadRoutine());
            }
        }

        private IEnumerator StartupRoutine()
        {
            // 2. Cache, if present.
            string cachePath = CachePath();
            if (File.Exists(cachePath))
            {
                string cached = File.ReadAllText(cachePath);
                if (TryParse(cached, out Dictionary<int, string> cachedLabels))
                {
                    Apply(cachedLabels, "DB: cached");
                }
            }

            // 3. Remote.
            bool ok = false;
            yield return FetchRemote(result => ok = result);
            if (!ok)
            {
                Debug.Log("[BoxTag] Remote database not used at startup; status: " + m_status);
            }
        }

        private IEnumerator ReloadRoutine()
        {
            m_reloading = true;
            string previous = m_status;
            bool ok = false;
            yield return FetchRemote(result => ok = result);
            m_reloading = false;

            string message = ok
                ? "DB reloaded: " + m_labels.Count + " tags"
                : "DB reload failed (kept: " + previous.Replace("DB: ", "") + ")";
            Debug.Log("[BoxTag] " + message);
            if (m_layout != null)
            {
                m_layout.SetDatabaseStatus(message);
            }

            // Show the result for 3 seconds, then return to the normal status.
            yield return new WaitForSeconds(3f);
            SetStatus(m_status);
        }

        // Downloads the remote file, parses it, saves it to the cache, and applies it.
        // Calls done(true) only if the new data was applied.
        private IEnumerator FetchRemote(Action<bool> done)
        {
            string url = m_settings.RemoteUrl;
            if (string.IsNullOrEmpty(url) || url.Contains("REPLACE_WITH"))
            {
                Debug.Log("[BoxTag] Remote URL not set in BoxTagDatabaseSettings; skipping download.");
                done(false);
                yield break;
            }

            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.timeout = Mathf.Max(1, m_settings.TimeoutSeconds);
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning("[BoxTag] Remote database download failed: " + request.error);
                    done(false);
                    yield break;
                }

                string json = request.downloadHandler.text;
                if (!TryParse(json, out Dictionary<int, string> labels))
                {
                    Debug.LogWarning("[BoxTag] Remote database is not valid tags.json; keeping current data.");
                    done(false);
                    yield break;
                }

                Apply(labels, "DB: online");
                try
                {
                    File.WriteAllText(CachePath(), json);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[BoxTag] Could not write cache: " + e.Message);
                }
                done(true);
            }
        }

        // Replaces the loaded data and updates the status line.
        private void Apply(Dictionary<int, string> labels, string statusPrefix)
        {
            m_labels.Clear();
            foreach (KeyValuePair<int, string> pair in labels)
            {
                m_labels[pair.Key] = pair.Value;
            }
            SetStatus(statusPrefix + ", " + m_labels.Count + " tags");
            Debug.Log("[BoxTag] Database applied: " + m_status);
        }

        private void SetStatus(string status)
        {
            m_status = status;
            if (m_layout != null)
            {
                m_layout.SetDatabaseStatus(status);
            }
        }

        private string CachePath()
        {
            return Path.Combine(Application.persistentDataPath, m_settings.CacheFileName);
        }

        // Parses tags.json. Only tag36h11 entries with a label are kept. Duplicate IDs: last one wins.
        private static bool TryParse(string json, out Dictionary<int, string> labels)
        {
            labels = new Dictionary<int, string>();
            BoxTagFile file;
            try
            {
                file = JsonUtility.FromJson<BoxTagFile>(json);
            }
            catch (ArgumentException e)
            {
                Debug.LogWarning("[BoxTag] Database JSON parse error: " + e.Message);
                return false;
            }

            if (file == null || file.tags == null)
            {
                return false;
            }

            int skipped = 0;
            foreach (BoxTagEntry entry in file.tags)
            {
                if (entry == null || string.IsNullOrEmpty(entry.label) || entry.family != SupportedFamily)
                {
                    skipped++;
                    continue;
                }
                if (labels.ContainsKey(entry.id))
                {
                    Debug.LogWarning("[BoxTag] Duplicate id " + entry.id + " in database; using the last one.");
                }
                labels[entry.id] = entry.label;
            }
            if (skipped > 0)
            {
                Debug.Log("[BoxTag] Database: skipped " + skipped + " entries (unsupported family or no label).");
            }
            return true;
        }
    }
}
