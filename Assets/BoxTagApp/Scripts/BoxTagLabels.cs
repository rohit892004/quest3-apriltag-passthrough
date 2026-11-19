// BoxTagLabels.cs
// L4: a label floats above each real AprilTag and stays on that tag when the user moves.
//
// Tag position, two modes (Left thumbstick click toggles; default DepthRaycast):
//   - DepthRaycast (default): the tag-centre pixel of the captured image is turned into a ray with
//     PassthroughCameraAccess.ViewportPointToRay (using the CAPTURE-TIME camera pose), and the ray is
//     cast against the environment depth (EnvironmentRaycastManager). The hit point is the tag
//     position. It does not depend on the tag size setting.
//     If the raycast fails (no depth, no permission, ray misses), the AprilTag pose is used and the
//     dot is yellow.
//   - AprilTagPose: the AprilTag pose only, world = CapturedFrame.CameraPose * tagPose, where the
//     tagPose comes from BoxTagPoseMath.ToUnityCameraSpace. It depends on the tag size setting.
//   In both modes the AprilTag pose gives the orientation.
//
// Stability:
//   - A new label is placed at its first detection.
//   - A re-detection moves the label only if the new position differs by more than
//     MoveThresholdMeters (3 cm). Otherwise it stays put. A move is an eased lerp, not a jump.
//   - A label whose tag is not detected stays where it was, UNLESS the tag position is inside the
//     captured image (with a margin) and in front of the camera. Then the label fades out over
//     FadeSeconds ("[BoxTag] tag N not seen in view -> removed").
//
// Labels are world-space canvases that always face the user, with a dark rounded background. A thin
// leader bar runs from the label down to a dot at the tag centre. The label sits LabelHeightMeters
// above the tag centre, in world up. The labels live under this object, which sits at the world root
// (identity transform, no parent), not under the camera rig.
//
// Visuals use unlit materials loaded with Resources.Load (Resources/BoxTagDebug*.mat), created by the
// APK build step (BoxTagAppBuild), which also includes the shader in Always Included Shaders.
//
// Controls:
//   - Left Y (handled by BoxTagCapture): debug axes on each tag (red = x, green = y, blue = z, 5 cm x 3 mm).
//     OFF by default.
//   - Left thumbstick click: toggle the pose mode (DepthRaycast / AprilTagPose). Shown on the status row.
//   - Right grip, short tap (under HoldToClearSeconds): re-place the panels.
//   - Right grip, hold for HoldToClearSeconds: clear all labels.
//
// Setup: BoxTagCapture adds this component at runtime and passes in the layout, database and camera.
// No scene edit or Setup Scene run is needed. The tag size is set on BoxTagDetector (m_tagSizeMeters).

using System.Collections.Generic;
using Meta.XR;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BoxTagApp
{
    public class BoxTagLabels : MonoBehaviour
    {
        public enum PoseMode
        {
            DepthRaycast,
            AprilTagPose,
        }

        private const float MmToMetres = 0.001f;
        private const string ScenePermission = "com.oculus.permission.USE_SCENE";

        [Header("Placement (metres)")]
        [Tooltip("Height of the label centre above the tag centre, in world up.")]
        [SerializeField] private float m_labelHeightMeters = 0.05f;

        [Tooltip("Distance in metres from the label centre to where the leader bar starts (about the label's bottom edge).")]
        [SerializeField] private float m_leaderStartMeters = 0.018f;

        [Tooltip("A re-detection moves the label only if the new position differs by more than this (metres).")]
        [SerializeField] private float m_moveThresholdMeters = 0.03f;

        [Tooltip("How fast a label eases toward its target (higher = snappier).")]
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

        [Tooltip("Continuous mode: a tag in view and undetected for this many seconds loses its label.")]
        [SerializeField] private float m_continuousRemoveSeconds = 1f;

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
            public Renderer Dot;
            public GameObject[] Axes;      // x, y, z cubes, children of Anchor
            public Vector3 TargetPosition; // world space
            public Quaternion TargetRotation;
            public string Shown;           // text currently on the label
            public bool Known;
            public bool Fading;
            public float FadeLeft;         // 1 -> 0 while fading
            public string Mode;            // mode of the last placement: depth, apriltag, apriltag-fallback
            public float LastSeen;         // Time.realtimeSinceStartup of the last detection
        }

        private readonly Dictionary<int, TagLabel> m_tags = new Dictionary<int, TagLabel>();
        private BoxTagLayout m_layout;
        private BoxTagDatabase m_database;
        private PassthroughCameraAccess m_cameraAccess;
        private EnvironmentRaycastManager m_environmentRaycast;
        private Material m_matRed, m_matGreen, m_matBlue, m_matWhite, m_matYellow;
        private bool m_axesVisible;
        private PoseMode m_poseMode = PoseMode.DepthRaycast;
        private float m_gripDownTime = -1f;
        private bool m_gripCleared;

        private static Sprite s_roundedSprite;

        /// <summary>Gives the labels the layout, the database (for text) and the camera (for depth rays).</summary>
        public void Initialize(BoxTagLayout layout, BoxTagDatabase database, PassthroughCameraAccess cameraAccess)
        {
            m_layout = layout;
            m_database = database;
            m_cameraAccess = cameraAccess;
        }

        private void Awake()
        {
            m_matRed = LoadDebugMaterial("BoxTagDebugRed");
            m_matGreen = LoadDebugMaterial("BoxTagDebugGreen");
            m_matBlue = LoadDebugMaterial("BoxTagDebugBlue");
            m_matWhite = LoadDebugMaterial("BoxTagDebugWhite");
            m_matYellow = LoadDebugMaterial("BoxTagDebugYellow");

            // Environment depth stays off until the user grants USE_SCENE at runtime. The manifest
            // declares it, but Horizon OS needs the runtime request (the "EnvDepth=0" cause).
#if UNITY_ANDROID
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(ScenePermission))
            {
                UnityEngine.Android.Permission.RequestUserPermission(ScenePermission);
                Debug.Log("[BoxTag] requesting permission " + ScenePermission + " (needed for environment depth)");
            }
#endif

            // The raycast manager also adds and enables EnvironmentDepthManager.
            var raycastGo = new GameObject("BoxTagEnvironmentRaycast");
            raycastGo.transform.SetParent(transform, false);
            m_environmentRaycast = raycastGo.AddComponent<EnvironmentRaycastManager>();
            Debug.Log("[BoxTag] environment depth supported=" + EnvironmentRaycastManager.IsSupported +
                      " pose mode=" + PoseModeName(m_poseMode));

            if (m_layout != null)
            {
                m_layout.SetPoseMode(PoseModeName(m_poseMode));
            }
        }

        /// <summary>
        /// Adds or moves one label per detected tag, then removes labels whose tag is in view of this
        /// capture but was not detected. Called on the main thread after detection.
        /// frame.CameraPose and frame.Intrinsics are the capture-time values, so the result is consistent.
        /// </summary>
        public void ApplyDetections(CapturedFrame frame, List<TagResult> results, bool continuous)
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

                // AprilTag pose in world space: capture-time camera pose x tag pose (camera space).
                Vector3 aprilPosition = TagToWorldPosition(frame.CameraPose, result.Position);
                Quaternion aprilRotation = frame.CameraPose.rotation * result.Rotation;

                // Position: depth raycast by default, the AprilTag pose as the fallback.
                Vector3 position = aprilPosition;
                string mode = "apriltag";
                bool fallback = false;
                if (m_poseMode == PoseMode.DepthRaycast)
                {
                    if (TryDepthPoint(frame, result.Center, out Vector3 depthPosition))
                    {
                        position = depthPosition;
                        mode = "depth";
                    }
                    else
                    {
                        mode = "apriltag-fallback";
                        fallback = true;
                    }
                }

                float distance = Vector3.Distance(position, cameraPosition);
                Debug.Log("[BoxTag] tag " + result.Id + " mode=" + mode +
                          " pixel=(" + result.Center.x.ToString("F1") + "," + result.Center.y.ToString("F1") + ")" +
                          " world=(" + position.x.ToString("F3") + "," + position.y.ToString("F3") + "," + position.z.ToString("F3") + ")" +
                          " dist=" + distance.ToString("F2") + " m reprojErr=" + result.ReprojErrPx.ToString("F2") + " px");

                if (!m_tags.TryGetValue(result.Id, out TagLabel tag))
                {
                    tag = CreateLabel(result.Id);
                    // First sighting: appear at the tag, do not fly in from the origin.
                    tag.Anchor.transform.SetPositionAndRotation(position, aprilRotation);
                    tag.TargetPosition = position;
                    tag.TargetRotation = aprilRotation;
                    m_tags[result.Id] = tag;
                }
                else if (Vector3.Distance(position, tag.TargetPosition) > m_moveThresholdMeters)
                {
                    // Moved by more than the threshold: follow it (eased in Update). Otherwise hold still.
                    tag.TargetPosition = position;
                    tag.TargetRotation = aprilRotation;
                }

                // Seen again while fading: bring it back.
                tag.Fading = false;
                tag.FadeLeft = 1f;
                tag.LastSeen = Time.realtimeSinceStartup;
                tag.Mode = mode;
                tag.Dot.sharedMaterial = fallback ? m_matYellow : m_matWhite;
                RefreshText(tag);
            }

            // Not detected in this capture, but its tag is inside the captured image: the tag is gone.
            foreach (TagLabel tag in m_tags.Values)
            {
                if (tag.Fading || seen.Contains(tag.Id))
                {
                    continue;
                }
                // Continuous mode: only after the tag has been missing for a while (covers a missed frame).
                if (continuous && Time.realtimeSinceStartup - tag.LastSeen < m_continuousRemoveSeconds)
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

        /// <summary>Shows or hides the tag axes (Left Y, through BoxTagCapture). OFF by default.</summary>
        public void SetDebugVisible(bool visible)
        {
            m_axesVisible = visible;
            foreach (TagLabel tag in m_tags.Values)
            {
                foreach (GameObject axis in tag.Axes)
                {
                    axis.GetComponent<Renderer>().enabled = m_axesVisible;
                }
            }
        }

        private void Update()
        {
            HandleThumbstick();
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

                // Ease toward the target (only changes on a re-detection that moved > threshold).
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

        // Depth position of a tag-centre pixel: ray from the capture-time pose, cast into the environment depth.
        // centre is in image pixels (OpenCV: origin top-left). The viewport origin is bottom-left.
        private bool TryDepthPoint(CapturedFrame frame, Vector2 centerTopDown, out Vector3 point)
        {
            point = default;
            if (m_cameraAccess == null || m_environmentRaycast == null || !EnvironmentRaycastManager.IsSupported)
            {
                return false;
            }

            var viewport = new Vector2(centerTopDown.x / frame.Width, 1f - centerTopDown.y / frame.Height);
            Ray ray = m_cameraAccess.ViewportPointToRay(viewport, frame.CameraPose);
            if (m_environmentRaycast.Raycast(ray, out EnvironmentRaycastHit hit))
            {
                point = hit.point;
                return true;
            }
            return false;
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

        private void HandleThumbstick()
        {
            if (OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick))
            {
                m_poseMode = m_poseMode == PoseMode.DepthRaycast ? PoseMode.AprilTagPose : PoseMode.DepthRaycast;
                Debug.Log("[BoxTag] pose mode = " + PoseModeName(m_poseMode) + " (applies to the next capture)");
                if (m_layout != null)
                {
                    m_layout.SetPoseMode(PoseModeName(m_poseMode));
                }
            }
        }

        private static string PoseModeName(PoseMode mode)
        {
            return mode == PoseMode.DepthRaycast ? "DepthRaycast" : "AprilTagPose";
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
            tag.Dot = dot.GetComponent<Renderer>();

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
