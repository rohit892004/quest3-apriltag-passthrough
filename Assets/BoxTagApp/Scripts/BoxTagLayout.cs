// BoxTagLayout.cs
// Owns every panel and text size and position. Change the numbers in the Inspector (on the
// BoxTagLayout object in BoxTagApp.unity); no code change is needed.
//
// Units are metres. Positions are relative to the user's head at the time of placement.
//
// Controls:
//   - Right grip (hold/press): re-place the panels in front of the current head position.
//   - Panels are placed automatically once a head pose exists.
//
// Panel rules:
//   - Live feed: its width is FeedWidthMeters, its height follows the image aspect (4:3),
//     placed FeedDistanceMeters in front, FeedHeightOffsetMeters from eye level (negative = lower),
//     and tilted by FeedTiltDegrees so the top leans toward the user.
//   - Captured frame: its width is CapturedWidthMeters, its height follows its aspect (4:3),
//     placed to the right of the feed with GapMeters between their edges. It is centred on the
//     feed vertically. (A 4:3 frame at 0.30 m wide is 0.225 m tall, so it cannot match the feed's
//     0.3375 m height without distorting the image.)
//   - Title above the captured frame, detected IDs below it, build label in the feed's bottom-right.
//
// Text: TextMesh is scaled so its height matches the Inspector value (no guessing of TextMesh units).

using UnityEngine;

namespace BoxTagApp
{
    public class BoxTagLayout : MonoBehaviour
    {
        [Header("Live feed panel")]
        [Tooltip("Width of the live feed panel in metres (height follows the 4:3 image aspect).")]
        [SerializeField] private float m_feedWidthMeters = 0.45f;

        [Tooltip("Distance in metres from the head to the feed panel, measured straight ahead.")]
        [SerializeField] private float m_feedDistanceMeters = 1.0f;

        [Tooltip("Vertical offset from eye level in metres. Negative = lower than eye level.")]
        [SerializeField] private float m_feedHeightOffsetMeters = -0.15f;

        [Tooltip("Tilt in degrees. Positive values lean the top of the panel toward the user.")]
        [SerializeField] private float m_feedTiltDegrees = 10f;

        [Header("Captured frame panel")]
        [Tooltip("Width of the captured-frame panel in metres (height follows its aspect).")]
        [SerializeField] private float m_capturedWidthMeters = 0.30f;

        [Tooltip("Gap in metres between the feed panel's right edge and the captured panel's left edge.")]
        [SerializeField] private float m_gapMeters = 0.05f;

        [Header("Text heights (metres)")]
        [Tooltip("Height of the \"Captured frame\" title.")]
        [SerializeField] private float m_titleHeightMeters = 0.02f;

        [Tooltip("Height of each line of detected-ID text.")]
        [SerializeField] private float m_idLineHeightMeters = 0.04f;

        [Tooltip("Height of the small build label in the feed's bottom-right corner.")]
        [SerializeField] private float m_buildHeightMeters = 0.01f;

        [Header("Panels and text (wired by Setup Scene)")]
        [SerializeField] private Transform m_feedPanel;
        [SerializeField] private Transform m_capturedPanel;
        [SerializeField] private TextMesh m_titleText;
        [SerializeField] private TextMesh m_idText;

        private TextMesh m_buildText;
        private float m_feedAspect = 4f / 3f;
        private float m_capturedAspect = 4f / 3f;
        private bool m_feedFlipX;
        private bool m_feedFlipY;
        private bool m_placed;

        // Public read-only values, so other scripts use the same numbers.
        public float FeedWidthMeters => m_feedWidthMeters;
        public float CapturedWidthMeters => m_capturedWidthMeters;

        private void Awake()
        {
            // Starting sizes (4:3) so the panels are not 1 m quads before the camera starts.
            if (m_feedPanel != null)
            {
                ApplyScale(m_feedPanel, m_feedWidthMeters, m_feedAspect, false, false);
            }
            if (m_capturedPanel != null)
            {
                ApplyScale(m_capturedPanel, m_capturedWidthMeters, m_capturedAspect, false, false);
            }
            CreateBuildLabel();
            FitText(m_titleText, m_titleHeightMeters, 1);
            FitText(m_idText, m_idLineHeightMeters, 1);
        }

        private void Update()
        {
            // Place once when a head pose exists, then only on request.
            if (!m_placed && Camera.main != null)
            {
                PlaceAll();
            }

            // Right grip: re-place in front of the current head position.
            if (OVRInput.GetDown(OVRInput.Button.SecondaryHandTrigger))
            {
                PlaceAll();
                Debug.Log("[BoxTag] Panels re-placed in front of the head.");
            }
        }

        /// <summary>Sets the live feed's aspect and flips, then sizes the panel.</summary>
        public void SetFeedAspect(float aspect, bool flipX, bool flipY)
        {
            m_feedAspect = Valid(aspect, m_feedAspect);
            m_feedFlipX = flipX;
            m_feedFlipY = flipY;
            if (m_feedPanel != null)
            {
                ApplyScale(m_feedPanel, m_feedWidthMeters, m_feedAspect, m_feedFlipX, m_feedFlipY);
            }
            LayoutRest();
        }

        /// <summary>Sets the captured frame's aspect and sizes its panel.</summary>
        public void SetCapturedAspect(float aspect)
        {
            m_capturedAspect = Valid(aspect, m_capturedAspect);
            if (m_capturedPanel != null)
            {
                ApplyScale(m_capturedPanel, m_capturedWidthMeters, m_capturedAspect, false, false);
            }
            LayoutRest();
        }

        /// <summary>Sets the detected-ID text and sizes it so each line has the Inspector height.</summary>
        public void SetIdText(string text)
        {
            if (m_idText == null)
            {
                return;
            }
            m_idText.text = text;
            int lines = text.Split('\n').Length;
            FitText(m_idText, m_idLineHeightMeters, lines);
        }

        /// <summary>Places the feed in front of the head, then lays out everything else.</summary>
        public void PlaceAll()
        {
            Transform head = Camera.main != null ? Camera.main.transform : null;
            if (head == null || m_feedPanel == null)
            {
                return;
            }

            // Straight ahead on the floor plane, so looking up or down does not push the panel.
            Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (forward.sqrMagnitude < 0.0001f)
            {
                forward = Vector3.forward;
            }
            forward.Normalize();

            Vector3 position = head.position + forward * m_feedDistanceMeters + Vector3.up * m_feedHeightOffsetMeters;

            // A quad's visible side faces its local -Z, so point -Z at the head.
            // Then tilt the top toward the user (rotation about local X).
            Quaternion facing = Quaternion.LookRotation(position - head.position);
            Quaternion tilt = Quaternion.Euler(-m_feedTiltDegrees, 0f, 0f);
            m_feedPanel.SetPositionAndRotation(position, facing * tilt);

            m_placed = true;
            LayoutRest();
        }

        // Positions the captured panel, the title, the ID text and the build label relative to the feed.
        private void LayoutRest()
        {
            if (m_feedPanel == null)
            {
                return;
            }

            Transform feed = m_feedPanel;
            float feedHalfW = Mathf.Abs(feed.localScale.x) * 0.5f;
            float feedHalfH = Mathf.Abs(feed.localScale.y) * 0.5f;

            if (m_capturedPanel != null)
            {
                float capHalfW = Mathf.Abs(m_capturedPanel.localScale.x) * 0.5f;
                float capHalfH = Mathf.Abs(m_capturedPanel.localScale.y) * 0.5f;

                // Right of the feed, same vertical centre, same tilt.
                Vector3 capPos = feed.position + feed.right * (feedHalfW + m_gapMeters + capHalfW);
                m_capturedPanel.SetPositionAndRotation(capPos, feed.rotation);

                if (m_titleText != null)
                {
                    m_titleText.transform.SetPositionAndRotation(
                        capPos + m_capturedPanel.up * (capHalfH + 0.01f),
                        feed.rotation);
                }

                if (m_idText != null)
                {
                    m_idText.transform.SetPositionAndRotation(
                        capPos - m_capturedPanel.up * (capHalfH + 0.01f),
                        feed.rotation);
                }
            }

            if (m_buildText != null)
            {
                // Bottom-right corner of the feed, just in front of the panel (toward the user).
                Vector3 corner = feed.position
                                 + feed.right * (feedHalfW - 0.01f)
                                 + feed.up * (-feedHalfH + 0.01f)
                                 - feed.forward * 0.005f;
                m_buildText.transform.SetPositionAndRotation(corner, feed.rotation);
            }
        }

        private void CreateBuildLabel()
        {
            var go = new GameObject("BoxTagBuildLabel");
            m_buildText = go.AddComponent<TextMesh>();
            m_buildText.text = "build " + Application.version;
            m_buildText.characterSize = 0.01f;
            m_buildText.fontSize = 100;
            m_buildText.anchor = TextAnchor.LowerRight;
            m_buildText.alignment = TextAlignment.Right;
            m_buildText.color = new Color(1f, 1f, 1f, 0.6f);
            FitText(m_buildText, m_buildHeightMeters, 1);
        }

        // Scales a panel quad: width from the setting, height from the aspect, flips via negative scale.
        private static void ApplyScale(Transform panel, float width, float aspect, bool flipX, bool flipY)
        {
            float height = width / aspect;
            panel.localScale = new Vector3(
                width * (flipX ? -1f : 1f),
                height * (flipY ? -1f : 1f),
                1f);
        }

        // Scales a TextMesh so its block height is lineHeight * lines, measured from its mesh.
        private static void FitText(TextMesh text, float lineHeight, int lines)
        {
            if (text == null)
            {
                return;
            }
            text.transform.localScale = Vector3.one;
            var filter = text.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null)
            {
                return;
            }
            float measured = filter.sharedMesh.bounds.size.y;
            if (measured <= 0f)
            {
                return;
            }
            float scale = (lineHeight * lines) / measured;
            text.transform.localScale = Vector3.one * scale;
        }

        private static float Valid(float aspect, float fallback)
        {
            return aspect > 0f ? aspect : fallback;
        }
    }
}
