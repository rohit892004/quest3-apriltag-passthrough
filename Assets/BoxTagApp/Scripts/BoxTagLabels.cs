// BoxTagLabels.cs
// L4: a label floats above each real AprilTag and stays on that tag when the user moves.
//
// How a tag gets its world pose:
//   world = CapturedFrame.CameraPose * tagPose
//   tagPose = BoxTagDetector result, already converted to Unity camera space by
//             BoxTagPoseMath.ToUnityCameraSpace (the one OpenCV -> Unity conversion).
//   CameraPose is copied at capture time, so the tag stays where it was seen, not where the head is.
//
// Labels:
//   - One label per tag ID. A new detection moves the existing label smoothly (no jump).
//   - A tag that is not detected again keeps its label where it was, UNLESS its tag position is
//     inside the captured image (with a margin) and in front of the camera. Then the tag is
//     treated as gone (covered, removed, or moved) and its label fades out over FadeSeconds.
//     A tag outside the captured image keeps its label: you just looked away.
//   - Label text comes from the database (Gist). Unknown IDs show "ID 5 (not in DB)" in orange.
//     Text is re-read every frame, so a Left X reload updates existing labels too.
//   - Labels are world-space canvases that always face the user (billboard), with a dark rounded
//     background. A thin leader bar runs from the label down to a dot at the tag centre.
//
// Visuals use unlit materials loaded with Resources.Load (Resources/BoxTagDebug*.mat). They are
// created by the APK build step (BoxTagAppBuild), which also puts the shader in Always Included
// Shaders. Shader.Find is not used at runtime, because a stripped shader gives pink objects.
//
// Controls:
//   - Left Y: show or hide the debug axes on each tag (red = x, green = y, blue = z, 5 cm x 3 mm).
//   - Right grip, short tap (released in under HoldToClearSeconds): re-place the panels.
//   - Right grip, hold for HoldToClearSeconds: clear all labels.
//
// Setup: BoxTagCapture adds this component at runtime and passes in the layout and database.
// No scene edit or Setup Scene run is needed. The tag size is set on BoxTagDetector (m_tagSizeMeters).

using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BoxTagApp
{
    public class BoxTagLabels : MonoBehaviour
    {
        private const float MmToMetres = 0.001f;

        [Header("Placement (metres)")]
        [Tooltip("Height of the label centre above the tag centre, in world up.")]
        [SerializeField] private float m_labelHeightMeters = 0.05f;

        [Tooltip("Distance in metres from the label centre to where the leader bar starts (about the label's bottom edge).")]
        [SerializeField] private float m_leaderStartMeters = 0.018f;

        [Tooltip("How fast a label eases toward a new detection (higher = snappier). Position and rotation both ease.")]
        [SerializeField] private float m_smoothingPerSecond = 8f;

        [Header("Debug axes and leader (metres)")]
        [Tooltip("Length of each debug axis (Left Y shows them). 5 cm.")]
        [SerializeField] private float m_axisLengthMeters = 0.05f;

        [Tooltip("Thickness of the debug axes and the leader bar. 3 mm.")]
        [SerializeField] private float m_barThicknessMeters = 0.003f;

        [Tooltip("Diameter of the dot at the tag centre.")]
        [SerializeField] private float m_dotSizeMeters = 0.008f;

        [Header("Label look (mm = canvas units)")]
        [Tooltip("Label text size in mm (30 = about 3 cm).")]
        [SerializeField] private float m_labelFontSizeMm = 30f;

        [Tooltip("Padding in mm inside the label background, on every side.")]
        [SerializeField] private int m_labelPaddingMm = 3;

        [Tooltip("Background colour of each label (black, alpha 0.7 = 70% opaque).")]
        [SerializeField] private Color m_backgroundColor = new Color(0f, 0f, 0f, 0.7f);

        [Tooltip("Text colour for known tags.")]
        [SerializeField] private Color m_textColor = Color.white;

        [Tooltip("Text colour for IDs that are not in the database.")]
        [SerializeField] private Color m_unknownColor = new Color(1f, 0.55f, 0f, 1f);

        [Header("Removal and right grip")]
        [Tooltip("A tag inside the captured image by at least this many pixels, and not detected, loses its label.")]
        [SerializeField] private float m_viewMarginPx = 16f;

        [Tooltip("Seconds a label takes to fade out when its tag is gone.")]
        [SerializeField] private float m_fadeSeconds = 0.3f;

        [Tooltip("Seconds the right grip must be held to clear all labels. A shorter tap re-places the panels.")]
        [SerializeField] private float m_holdToClearSeconds = 1f;

        private sealed class TagLabel
        {
            public int Id;
            public GameObject Anchor;      // tag centre (eased); holds the dot and the debug axes
            public GameObject Root;        // the label canvas (billboard)
            public CanvasGroup Fade;
            public TMP_Text Text;
            public GameObject Leader;      // bar from the label down to the tag centre (world space)
            public GameObject[] Axes;      // x, y, z cubes, children of Anchor
            public Vector3 TargetPosition; // latest detection, world space
            public Quaternion TargetRotation;
            public string Shown;           // text currently on the label
            public bool Known;
            public bool Fading;
            public float FadeLeft;         // 1 -> 0 while fading
        }

        private readonly Dictionary<int, TagLabel> m_tags = new Dictionary<int, TagLabel>();
        private BoxTagLayout m_layout;
        private BoxTagDatabase m_database;
        private Material m_matRed, m_matGreen, m_matBlue, m_matWhite;
        private bool m_axesVisible;
        private float m_gripDownTime = -1f;
        private bool m_gripCleared;

        private static Sprite s_roundedSprite;

        /// <summary>Gives the labels the layout (for re-placing on a tap) and the database (for text).</summary>
        public void Initialize(BoxTagLayout layout, BoxTagDatabase database)
        {
            m_layout = layout;
            m_database = database;
        }

        private void Awake()
        {
            m_matRed = LoadDebugMaterial("BoxTagDebugRed");
            m_matGreen = LoadDebugMaterial("BoxTagDebugGreen");
            m_matBlue = LoadDebugMaterial("BoxTagDebugBlue");
            m_matWhite = LoadDebugMaterial("BoxTagDebugWhite");
        }

        /// <summary>
        /// Adds or moves one label per detected tag, then removes labels whose tag is in view of this
        /// capture but was not detected. Called on the main thread after detection.
        /// frame.CameraPose and frame.Intrinsics are the capture-time values, so the result is consistent.
        /// </summary>
        public void ApplyDetections(CapturedFrame frame, List<TagResult> results)
        {
            if (frame == null || results == null)
            {
                return;
            }

            var seen = new HashSet<int>();
            Vector3 cameraPosition = frame.CameraPose.position;
            foreach (TagResult result in results)
            {
                seen.Add(result.Id);

                // Tag pose in world space: capture-time camera pose x tag pose (camera space).
                Vector3 tagPosition = TagToWorldPosition(frame.CameraPose, result.Position);
                Quaternion tagRotation = frame.CameraPose.rotation * result.Rotation;
                float distance = Vector3.Distance(tagPosition, cameraPosition);

                Debug.Log("[BoxTag] tag ID=" + result.Id + " world=" + tagPosition.ToString("F3") +
                          " distance=" + distance.ToString("F2") + " m (from capture camera)");

                if (!m_tags.TryGetValue(result.Id, out TagLabel tag))
                {
                    tag = CreateLabel(result.Id);
                    // First sighting: appear at the tag, do not fly in from the origin.
                    tag.Anchor.transform.SetPositionAndRotation(tagPosition, tagRotation);
                    m_tags[result.Id] = tag;
                }

                // Seen again while fading: bring it back.
                tag.Fading = false;
                tag.FadeLeft = 1f;
                tag.TargetPosition = tagPosition;
                tag.TargetRotation = tagRotation;
                RefreshText(tag);
            }

            // Not detected in this capture, but its tag is inside the captured image: the tag is gone.
            foreach (TagLabel tag in m_tags.Values)
            {
                if (tag.Fading || seen.Contains(tag.Id))
                {
                    continue;
                }
                if (IsInCapturedImage(frame, tag.TargetPosition))
                {
                    tag.Fading = true;
                    tag.FadeLeft = 1f;
                    Debug.Log("[BoxTag] tag " + tag.Id + " not seen in view -> removed");
                }
            }
        }

        /// <summary>Removes every label and its visuals.</summary>
        public void ClearAll()
        {
            foreach (TagLabel tag in m_tags.Values)
            {
                DestroyLabel(tag);
            }
            m_tags.Clear();
            Debug.Log("[BoxTag] All labels cleared.");
        }

        private void Update()
        {
            HandleLeftY();
            HandleRightGrip();

            if (m_tags.Count == 0)
            {
                return;
            }

            Camera cam = Camera.main;
            float dt = Time.deltaTime;
            float ease = 1f - Mathf.Exp(-m_smoothingPerSecond * dt);
            List<int> finished = null;

            foreach (TagLabel tag in m_tags.Values)
            {
                RefreshText(tag);

                float s = 1f;
                if (tag.Fading)
                {
                    tag.FadeLeft -= dt / Mathf.Max(0.01f, m_fadeSeconds);
                    if (tag.FadeLeft <= 0f)
                    {
                        finished ??= new List<int>();
                        finished.Add(tag.Id);
                        continue;
                    }
                    s = Mathf.Clamp01(tag.FadeLeft);
                }

                // Ease toward the latest detection.
                Transform anchor = tag.Anchor.transform;
                anchor.position = Vector3.Lerp(anchor.position, tag.TargetPosition, ease);
                anchor.rotation = Quaternion.Slerp(anchor.rotation, tag.TargetRotation, ease);
                anchor.localScale = Vector3.one * s;

                // Label above the tag (world up), facing the user.
                Vector3 labelPosition = anchor.position + Vector3.up * m_labelHeightMeters;
                Transform root = tag.Root.transform;
                root.position = labelPosition;
                tag.Fade.alpha = s;
                if (cam != null)
                {
                    Vector3 away = labelPosition - cam.transform.position;
                    if (away.sqrMagnitude > 0.0001f)
                    {
                        root.rotation = Quaternion.LookRotation(away); // canvas front (-Z) faces the user
                    }
                }

                // Leader bar from the label's bottom edge to the tag centre.
                Vector3 start = labelPosition + Vector3.down * m_leaderStartMeters;
                Vector3 end = anchor.position;
                Vector3 delta = end - start;
                float length = delta.magnitude;
                Transform leader = tag.Leader.transform;
                if (length > 0.0001f)
                {
                    leader.position = (start + end) * 0.5f;
                    leader.rotation = Quaternion.LookRotation(delta / length);
                }
                leader.localScale = new Vector3(m_barThicknessMeters * s, m_barThicknessMeters * s, Mathf.Max(0.0001f, length));
            }

            if (finished != null)
            {
                foreach (int id in finished)
                {
                    DestroyLabel(m_tags[id]);
                    m_tags.Remove(id);
                }
            }
        }

        // Camera-space tag position -> world: cam.position + cam.rotation * p.
        // The camera-space tag pose itself comes from BoxTagPoseMath.ToUnityCameraSpace.
        private static Vector3 TagToWorldPosition(Pose cameraPose, Vector3 tagPositionInCamera)
        {
            return cameraPose.position + cameraPose.rotation * tagPositionInCamera;
        }

        /// <summary>
        /// True if a world point projects inside the captured image (with margin) and in front of the camera.
        /// Uses the same intrinsics as the detection, in OpenCV convention (y down, origin top-left),
        /// so this matches where the detector looks.
        /// </summary>
        private bool IsInCapturedImage(CapturedFrame frame, Vector3 worldPoint)
        {
            Vector3 p = Quaternion.Inverse(frame.CameraPose.rotation) * (worldPoint - frame.CameraPose.position);
            if (p.z <= 0.05f)
            {
                return false; // behind or too close to the camera
            }

            var k = frame.Intrinsics;
            float u = (float)(k.Fx * p.x / p.z + k.Cx);
            float v = (float)(k.Fy * (-p.y) / p.z + k.Cy); // Unity y is up; OpenCV v is down
            float m = m_viewMarginPx;
            return u >= m && u <= frame.Width - m && v >= m && v <= frame.Height - m;
        }

        // Label text: database name, or "ID n (not in DB)" in orange. Re-read every frame,
        // so a database reload updates labels that are already on screen.
        private void RefreshText(TagLabel tag)
        {
            string label = null;
            bool known = m_database != null && m_database.TryGetLabel(tag.Id, out label);
            string text = known ? label : "ID " + tag.Id + " (not in DB)";
            if (known == tag.Known && text == tag.Shown)
            {
                return;
            }

            tag.Known = known;
            tag.Shown = text;
            tag.Text.text = text;
            tag.Text.color = known ? m_textColor : m_unknownColor;
        }

        private void HandleLeftY()
        {
            if (OVRInput.GetDown(OVRInput.Button.Four))
            {
                m_axesVisible = !m_axesVisible;
                foreach (TagLabel tag in m_tags.Values)
                {
                    foreach (GameObject axis in tag.Axes)
                    {
                        axis.GetComponent<Renderer>().enabled = m_axesVisible;
                    }
                }
                Debug.Log(m_axesVisible ? "[BoxTag] Debug axes ON" : "[BoxTag] Debug axes OFF");
            }
        }

        private void HandleRightGrip()
        {
            if (OVRInput.GetDown(OVRInput.Button.SecondaryHandTrigger))
            {
                m_gripDownTime = Time.realtimeSinceStartup;
                m_gripCleared = false;
            }

            if (m_gripDownTime < 0f)
            {
                return;
            }

            float held = Time.realtimeSinceStartup - m_gripDownTime;
            if (!m_gripCleared && held >= m_holdToClearSeconds && OVRInput.Get(OVRInput.Button.SecondaryHandTrigger))
            {
                m_gripCleared = true;
                ClearAll();
            }

            if (OVRInput.GetUp(OVRInput.Button.SecondaryHandTrigger))
            {
                if (!m_gripCleared && m_layout != null)
                {
                    m_layout.PlaceAll();
                    Debug.Log("[BoxTag] Panels re-placed in front of the head.");
                }
                m_gripDownTime = -1f;
            }
        }

        private TagLabel CreateLabel(int id)
        {
            var tag = new TagLabel { Id = id, FadeLeft = 1f };

            // Anchor: the tag centre. Its children are the dot and the tag's own x/y/z axes,
            // so the axes show the tag's real orientation.
            tag.Anchor = new GameObject("BoxTagTag_" + id);
            tag.Anchor.transform.SetParent(transform, false);

            GameObject dot = MakeCube(PrimitiveType.Sphere, tag.Anchor.transform, "Dot", m_matWhite);
            dot.transform.localScale = Vector3.one * m_dotSizeMeters;

            float len = m_axisLengthMeters;
            float t = m_barThicknessMeters;
            tag.Axes = new GameObject[3];
            tag.Axes[0] = MakeAxis(tag.Anchor.transform, "AxisX", m_matRed, new Vector3(len * 0.5f, 0f, 0f), new Vector3(len, t, t));
            tag.Axes[1] = MakeAxis(tag.Anchor.transform, "AxisY", m_matGreen, new Vector3(0f, len * 0.5f, 0f), new Vector3(t, len, t));
            tag.Axes[2] = MakeAxis(tag.Anchor.transform, "AxisZ", m_matBlue, new Vector3(0f, 0f, len * 0.5f), new Vector3(t, t, len));
            foreach (GameObject axis in tag.Axes)
            {
                axis.GetComponent<Renderer>().enabled = m_axesVisible;
            }

            // Leader bar: world space, positioned each frame in Update.
            tag.Leader = MakeCube(PrimitiveType.Cube, transform, "Leader_" + id, m_matWhite);

            // Label canvas: rounded dark background, text sized to the content, centred above the tag.
            tag.Root = new GameObject("BoxTagLabel_" + id, typeof(RectTransform));
            tag.Root.transform.SetParent(transform, false);
            var canvas = tag.Root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 5;
            var rect = (RectTransform)tag.Root.transform;
            rect.localScale = Vector3.one * MmToMetres; // 1 canvas unit = 1 mm
            tag.Fade = tag.Root.AddComponent<CanvasGroup>();

            var background = tag.Root.AddComponent<Image>();
            background.sprite = RoundedSprite();
            background.type = Image.Type.Sliced;
            background.color = m_backgroundColor;
            background.raycastTarget = false;

            int pad = m_labelPaddingMm;
            var group = tag.Root.AddComponent<HorizontalLayoutGroup>();
            group.padding = new RectOffset(pad, pad, pad, pad);
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = false;

            var fitter = tag.Root.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(tag.Root.transform, false);
            tag.Text = textGo.AddComponent<TextMeshProUGUI>();
            tag.Text.fontSize = m_labelFontSizeMm;
            tag.Text.alignment = TextAlignmentOptions.Center;
            tag.Text.enableWordWrapping = false;
            tag.Text.raycastTarget = false;
            tag.Text.color = m_textColor;

            return tag;
        }

        // A primitive with its collider removed and the given debug material.
        private static GameObject MakeCube(PrimitiveType type, Transform parent, string name, Material material)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            if (material != null)
            {
                go.GetComponent<Renderer>().sharedMaterial = material;
            }
            return go;
        }

        // One tag axis: a cube from the tag centre along an axis, centred at 'center'.
        private static GameObject MakeAxis(Transform parent, string name, Material material, Vector3 center, Vector3 size)
        {
            GameObject go = MakeCube(PrimitiveType.Cube, parent, name, material);
            go.transform.localPosition = center;
            go.transform.localScale = size;
            return go;
        }

        private void DestroyLabel(TagLabel tag)
        {
            Destroy(tag.Root);
            Destroy(tag.Anchor);
            Destroy(tag.Leader);
        }

        // Loads an unlit material from Resources (created by the build step). Logs an error if it is missing.
        private static Material LoadDebugMaterial(string name)
        {
            Material material = Resources.Load<Material>(name);
            if (material == null)
            {
                Debug.LogError("[BoxTag] material '" + name + "' failed to load from Resources. " +
                               "Build with BoxTagAppBuild (creates the materials and includes the shader).");
            }
            return material;
        }

        // A small rounded-corner sprite, made at runtime, so no image asset is needed.
        // Used as a 9-sliced background, so the corners stay round at any size.
        private static Sprite RoundedSprite()
        {
            if (s_roundedSprite != null)
            {
                return s_roundedSprite;
            }

            const int size = 64;
            const float radius = 16f;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Distance from the pixel to the nearest point of the inner rectangle.
                    float px = x + 0.5f, py = y + 0.5f;
                    float cx = Mathf.Clamp(px, radius, size - radius);
                    float cy = Mathf.Clamp(py, radius, size - radius);
                    float d = Mathf.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
                    float alpha = Mathf.Clamp01(radius - d + 0.5f); // soft edge
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply();

            s_roundedSprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f),
                100f, 0, SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
            return s_roundedSprite;
        }
    }
}
