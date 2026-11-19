// BoxTagLayout.cs
// Owns every panel and text size and position. Change the numbers in the Inspector (on the
// BoxTagLayout object in BoxTagApp.unity); no code change is needed.
//
// Units are metres. Positions are relative to the user's head at the time of placement.
//
// Controls:
//   - Right grip: re-place the panels in front of the current head position.
//   - Panels are placed automatically once a head pose exists.
//
// Panel rules:
//   - Live feed: width FeedWidthMeters, height from the 4:3 image aspect, placed FeedDistanceMeters
//     straight ahead, FeedHeightOffsetMeters from eye level (negative = lower), tilted by
//     FeedTiltDegrees so the top leans toward the user.
//   - Captured frame: width CapturedWidthMeters, height from its aspect, to the right of the feed
//     with GapMeters between their edges. Centred vertically on the feed (a 4:3 frame at 0.30 m
//     wide is 0.225 m tall, so it cannot match the feed's height without distortion).
//   - Title above the captured frame; detected IDs below it.
//   - Build label bottom-right of the feed; database status bottom-left of the feed.
//
// Text sizing: each TextMesh is scaled so its measured rendered height matches the Inspector
// value, and its width never exceeds the panel's width. Measurement happens one frame after the
// text changes, because TextMesh regenerates its mesh lazily. The result is logged as
// "[BoxTag] text ... rendered size", so the scale can be checked against the log.

using System.Collections;
using System.Collections.Generic;
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

        [Header("Text heights (metres, rendered)")]
        [Tooltip("Height of the \"Captured frame\" title.")]
        [SerializeField] private float m_titleHeightMeters = 0.01f;

        [Tooltip("Height of each line of detected-ID text.")]
        [SerializeField] private float m_idLineHeightMeters = 0.02f;

        [Tooltip("Height of the small build label (bottom-right of the feed).")]
        [SerializeField] private float m_buildHeightMeters = 0.006f;

        [Tooltip("Height of the database status line (bottom-left of the feed).")]
        [SerializeField] private float m_databaseHeightMeters = 0.008f;

        [Header("Panels and text (wired by Setup Scene)")]
        [SerializeField] private Transform m_feedPanel;
        [SerializeField] private Transform m_capturedPanel;
        [SerializeField] private TextMesh m_titleText;
        [SerializeField] private TextMesh m_idText;

        private TextMesh m_buildText;
        private TextMesh m_databaseText;
        private float m_feedAspect = 4f / 3f;
        private float m_capturedAspect = 4f / 3f;
        private bool m_feedFlipX;
        private bool m_feedFlipY;
        private bool m_placed;
        private readonly Dictionary<TextMesh, Coroutine> m_fitRoutines = new Dictionary<TextMesh, Coroutine>();

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

            m_buildText = CreateLabel("BoxTagBuildLabel", TextAnchor.LowerRight, TextAlignment.Right,
                new Color(1f, 1f, 1f, 0.6f));
            m_buildText.text = "build " + Application.version;

            m_databaseText = CreateLabel("BoxTagDatabaseStatus", TextAnchor.LowerLeft, TextAlignment.Left,
                new Color(1f, 1f, 1f, 0.8f));
            m_databaseText.text = "DB: starting";

            FitWhenReady(m_buildText, m_buildHeightMeters, 1, FeedWidth * 0.4f);
            FitWhenReady(m_databaseText, m_databaseHeightMeters, 1, FeedWidth * 0.6f);
            FitWhenReady(m_titleText, m_titleHeightMeters, 1, CapturedWidth);
            FitWhenReady(m_idText, m_idLineHeightMeters, 1, CapturedWidth);
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

        /// <summary>Sets the detected-ID text. Each line gets the Inspector height and stays within the panel width.</summary>
        public void SetIdText(string text)
        {
            if (m_idText == null)
            {
                return;
            }
            m_idText.text = text;
            int lines = text.Split('\n').Length;
            FitWhenReady(m_idText, m_idLineHeightMeters, lines, CapturedWidth);
        }

        /// <summary>Sets the database status line (bottom-left of the feed).</summary>
        public void SetDatabaseStatus(string text)
        {
            if (m_databaseText == null)
            {
                return;
            }
            m_databaseText.text = text;
            FitWhenReady(m_databaseText, m_databaseHeightMeters, 1, FeedWidth * 0.6f);
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

        private float FeedWidth => Mathf.Abs(m_feedPanel != null ? m_feedPanel.localScale.x : m_feedWidthMeters);
        private float CapturedWidth => m_capturedWidthMeters * 0.95f;

        // Positions the captured panel, title, ID text and corner labels relative to the feed.
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
                float capHalfH = Mathf.Abs(m_capturedPanel.localScale.y) * 0.5f;
                float capHalfW = Mathf.Abs(m_capturedPanel.localScale.x) * 0.5f;

                // Right of the feed, same vertical centre, same tilt.
                Vector3 capPos = feed.position + feed.right * (feedHalfW + m_gapMeters + capHalfW);
                m_capturedPanel.SetPositionAndRotation(capPos, feed.rotation);

                if (m_titleText != null)
                {
                    m_titleText.transform.SetPositionAndRotation(
                        capPos + feed.up * (capHalfH + 0.01f), feed.rotation);
                }

                if (m_idText != null)
                {
                    m_idText.transform.SetPositionAndRotation(
                        capPos - feed.up * (capHalfH + 0.01f), feed.rotation);
                }
            }

            // Corner labels: just inside the feed's bottom corners, slightly toward the user.
            Vector3 toUser = -feed.forward * 0.005f;
            if (m_buildText != null)
            {
                Vector3 corner = feed.position + feed.right * (feedHalfW - 0.01f) + feed.up * (-feedHalfH + 0.01f) + toUser;
                m_buildText.transform.SetPositionAndRotation(corner, feed.rotation);
            }
            if (m_databaseText != null)
            {
                Vector3 corner = feed.position - feed.right * (feedHalfW - 0.01f) + feed.up * (-feedHalfH + 0.01f) + toUser;
                m_databaseText.transform.SetPositionAndRotation(corner, feed.rotation);
            }
        }

        private TextMesh CreateLabel(string name, TextAnchor anchor, TextAlignment alignment, Color color)
        {
            var go = new GameObject(name);
            var text = go.AddComponent<TextMesh>();
            text.characterSize = 0.01f;
            text.fontSize = 100;
            text.anchor = anchor;
            text.alignment = alignment;
            text.color = color;
            return text;
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

        // Starts a fit on the next frame (after TextMesh regenerates its mesh), replacing any pending fit.
        private void FitWhenReady(TextMesh text, float lineHeight, int lines, float maxWidth)
        {
            if (text == null || !isActiveAndEnabled)
            {
                return;
            }
            if (m_fitRoutines.TryGetValue(text, out Coroutine running) && running != null)
            {
                StopCoroutine(running);
            }
            m_fitRoutines[text] = StartCoroutine(FitRoutine(text, lineHeight, lines, maxWidth));
        }

        private IEnumerator FitRoutine(TextMesh text, float lineHeight, int lines, float maxWidth)
        {
            // Reset to the unit scale and wait a frame, so the measured mesh is the real text mesh.
            text.transform.localScale = Vector3.one;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                yield return null;

                if (TryMeasureUnscaled(text, out Vector2 size))
                {
                    float targetHeight = lineHeight * lines;
                    float scale = targetHeight / size.y;
                    if (maxWidth > 0f && size.x * scale > maxWidth)
                    {
                        scale = maxWidth / size.x; // shrink to fit the panel width, never overflow
                    }
                    text.transform.localScale = Vector3.one * scale;

                    Debug.Log("[BoxTag] text '" + Preview(text.text) + "' rendered size: height=" +
                              (size.y * scale).ToString("F4") + " m, width=" + (size.x * scale).ToString("F4") +
                              " m (target height " + targetHeight.ToString("F4") + " m, max width " +
                              maxWidth.ToString("F3") + " m, scale " + scale.ToString("F5") + ")");
                    yield break;
                }
            }
            Debug.LogWarning("[BoxTag] text '" + Preview(text.text) + "' could not be measured; left at unit scale.");
        }

        // Unscaled rendered size of the text mesh (local units; the text is scaled by its transform).
        private static bool TryMeasureUnscaled(TextMesh text, out Vector2 size)
        {
            size = Vector2.zero;
            MeshFilter filter = text.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null)
            {
                return false;
            }
            Bounds b = filter.sharedMesh.bounds;
            if (b.size.x <= 0f || b.size.y <= 0f)
            {
                return false;
            }
            size = new Vector2(b.size.x, b.size.y);
            return true;
        }

        private static string Preview(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "";
            }
            string oneLine = s.Replace("\n", " | ");
            return oneLine.Length > 24 ? oneLine.Substring(0, 24) + "..." : oneLine;
        }

        private static float Valid(float aspect, float fallback)
        {
            return aspect > 0f ? aspect : fallback;
        }
    }
}
