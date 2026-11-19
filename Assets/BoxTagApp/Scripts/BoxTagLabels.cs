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
//   - A tag that is not detected again keeps its label where it was.
//   - Label text comes from the database (Gist). Unknown IDs show "ID 5 (not in DB)" in orange.
//     Text is re-read every frame, so a Left X reload updates existing labels too.
//   - Labels are world-space canvases that always face the user (billboard), with a dark rounded
//     background. A thin leader line runs from the label down to a dot at the tag centre.
//   - The label sits LabelHeightMeters above the tag centre, in world up.
//
// Controls:
//   - Left Y: show or hide a small debug axis on each tag (red = x, green = y, blue = z).
//     Use it to check position and rotation before trusting the labels.
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

        [Tooltip("Distance in metres from the label centre to where the leader line starts (about the label's bottom edge).")]
        [SerializeField] private float m_leaderStartMeters = 0.018f;

        [Tooltip("How fast a label eases toward a new detection (higher = snappier). Position and rotation both ease.")]
        [SerializeField] private float m_smoothingPerSecond = 8f;

        [Header("Debug and lines (metres)")]
        [Tooltip("Length of each debug axis line (Left Y shows them).")]
        [SerializeField] private float m_axisLengthMeters = 0.03f;

        [Tooltip("Thickness of the leader line and the axis lines.")]
        [SerializeField] private float m_lineWidthMeters = 0.002f;

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

        [Header("Right grip")]
        [Tooltip("Seconds the right grip must be held to clear all labels. A shorter tap re-places the panels.")]
        [SerializeField] private float m_holdToClearSeconds = 1f;

        private sealed class TagLabel
        {
            public int Id;
            public GameObject Anchor;      // tag centre (eased); holds the dot and the leader and axis lines
            public GameObject Root;        // the label canvas (billboard)
            public TMP_Text Text;
            public LineRenderer Leader;
            public LineRenderer[] Axes;    // x, y, z
            public Vector3 TargetPosition; // latest detection, world space
            public Quaternion TargetRotation;
            public string Shown;           // text currently on the label
            public bool Known;
        }

        private readonly Dictionary<int, TagLabel> m_tags = new Dictionary<int, TagLabel>();
        private BoxTagLayout m_layout;
        private BoxTagDatabase m_database;
        private Material m_lineMaterial;
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

        /// <summary>
        /// Adds or moves one label per detected tag. Called on the main thread after detection.
        /// frame.CameraPose is the pose at capture time, so the tags stay where they were seen.
        /// </summary>
        public void ApplyDetections(CapturedFrame frame, List<TagResult> results)
        {
            if (frame == null || results == null)
            {
                return;
            }

            Vector3 cameraPosition = frame.CameraPose.position;
            foreach (TagResult result in results)
            {
                // Tag pose in world space: capture-time camera pose x tag pose (camera space).
                Pose tagWorld = TagToWorld(frame.CameraPose, result.Position, result.Rotation);
                float distance = Vector3.Distance(tagWorld.position, cameraPosition);

                Debug.Log("[BoxTag] tag ID=" + result.Id + " world=" + tagWorld.position.ToString("F3") +
                          " distance=" + distance.ToString("F2") + " m (from capture camera)");

                if (!m_tags.TryGetValue(result.Id, out TagLabel tag))
                {
                    tag = CreateLabel(result.Id);
                    // First sighting: appear at the tag, do not fly in from the origin.
                    tag.Anchor.transform.SetPositionAndRotation(tagWorld.position, tagWorld.rotation);
                    m_tags[result.Id] = tag;
                }

                tag.TargetPosition = tagWorld.position;
                tag.TargetRotation = tagWorld.rotation;
                RefreshText(tag);
            }
        }

        /// <summary>Removes every label and its lines.</summary>
        public void ClearAll()
        {
            foreach (TagLabel tag in m_tags.Values)
            {
                Destroy(tag.Root);
                Destroy(tag.Anchor);
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
            float ease = 1f - Mathf.Exp(-m_smoothingPerSecond * Time.deltaTime);
            foreach (TagLabel tag in m_tags.Values)
            {
                RefreshText(tag);

                // Ease toward the latest detection.
                Transform anchor = tag.Anchor.transform;
                anchor.position = Vector3.Lerp(anchor.position, tag.TargetPosition, ease);
                anchor.rotation = Quaternion.Slerp(anchor.rotation, tag.TargetRotation, ease);

                // Label above the tag (world up), facing the user.
                Vector3 labelPosition = anchor.position + Vector3.up * m_labelHeightMeters;
                Transform root = tag.Root.transform;
                root.position = labelPosition;
                if (cam != null)
                {
                    Vector3 away = labelPosition - cam.transform.position;
                    if (away.sqrMagnitude > 0.0001f)
                    {
                        root.rotation = Quaternion.LookRotation(away); // canvas front (-Z) faces the user
                    }
                }

                // Leader line from the label's bottom edge to the tag centre.
                tag.Leader.SetPosition(0, labelPosition + Vector3.down * m_leaderStartMeters);
                tag.Leader.SetPosition(1, anchor.position);

                // Debug axes (Left Y).
                tag.Axes[0].SetPosition(0, anchor.position);
                tag.Axes[0].SetPosition(1, anchor.position + anchor.right * m_axisLengthMeters);
                tag.Axes[1].SetPosition(0, anchor.position);
                tag.Axes[1].SetPosition(1, anchor.position + anchor.up * m_axisLengthMeters);
                tag.Axes[2].SetPosition(0, anchor.position);
                tag.Axes[2].SetPosition(1, anchor.position + anchor.forward * m_axisLengthMeters);
            }
        }

        /// <summary>
        /// Camera-space tag pose -> world pose, using the capture-time camera pose:
        ///   position = cam.position + cam.rotation * tagPosition
        ///   rotation = cam.rotation * tagRotation
        /// The tag pose itself was already converted from OpenCV by BoxTagPoseMath.ToUnityCameraSpace.
        /// </summary>
        public static Pose TagToWorld(Pose cameraPose, Vector3 tagPositionInCamera, Quaternion tagRotationInCamera)
        {
            return new Pose(
                cameraPose.position + cameraPose.rotation * tagPositionInCamera,
                cameraPose.rotation * tagRotationInCamera);
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
                    foreach (LineRenderer axis in tag.Axes)
                    {
                        axis.enabled = m_axesVisible;
                    }
                }
                Debug.Log("[BoxTag] Debug axes " + (m_axesVisible ? "shown" : "hidden") + ".");
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
            var tag = new TagLabel { Id = id };

            // Anchor: the tag centre, with the dot as its child.
            tag.Anchor = new GameObject("BoxTagTag_" + id);
            tag.Anchor.transform.SetParent(transform, false);
            GameObject dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            dot.name = "Dot";
            Destroy(dot.GetComponent<Collider>());
            dot.transform.SetParent(tag.Anchor.transform, false);
            dot.transform.localScale = Vector3.one * m_dotSizeMeters;

            // Leader line and axes live in world space, so their points are set in world coordinates.
            tag.Leader = MakeLine(tag.Anchor.transform, "Leader", new Color(1f, 1f, 1f, 0.8f));
            tag.Axes = new LineRenderer[3];
            tag.Axes[0] = MakeLine(tag.Anchor.transform, "AxisX", Color.red);
            tag.Axes[1] = MakeLine(tag.Anchor.transform, "AxisY", Color.green);
            tag.Axes[2] = MakeLine(tag.Anchor.transform, "AxisZ", new Color(0.3f, 0.5f, 1f));
            foreach (LineRenderer axis in tag.Axes)
            {
                axis.enabled = m_axesVisible;
            }

            // Label canvas: rounded dark background, text sized to the content, centred on the anchor.
            tag.Root = new GameObject("BoxTagLabel_" + id, typeof(RectTransform));
            tag.Root.transform.SetParent(transform, false);
            var canvas = tag.Root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 5;
            var rect = (RectTransform)tag.Root.transform;
            rect.localScale = Vector3.one * MmToMetres; // 1 canvas unit = 1 mm

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

            m_tags[id] = tag;
            return tag;
        }

        private LineRenderer MakeLine(Transform parent, string name, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.startWidth = m_lineWidthMeters;
            line.endWidth = m_lineWidthMeters;
            line.numCapVertices = 2;
            line.material = LineMaterial();
            line.startColor = color;
            line.endColor = color;
            return line;
        }

        private Material LineMaterial()
        {
            if (m_lineMaterial == null)
            {
                Shader shader = Shader.Find("Sprites/Default");
                if (shader == null)
                {
                    Debug.LogError("[BoxTag] Shader Sprites/Default not found; lines will not draw.");
                    return null;
                }
                m_lineMaterial = new Material(shader);
            }
            return m_lineMaterial;
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
