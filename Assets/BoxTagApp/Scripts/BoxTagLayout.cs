// BoxTagLayout.cs
// Owns every panel size, position and text. Each panel is ONE world-space Canvas, built at
// runtime, with TextMeshPro text and Vertical/Horizontal layout groups. Rows stack, so they
// cannot overlap. Every value is an Inspector field on the BoxTagLayout object in BoxTagApp.unity.
//
// Units:
//   - Placement (feed width, distance, offsets, gap) is in metres.
//   - Panel look and text are in millimetres. The canvas scale is 0.001, so 1 canvas unit = 1 mm.
//     Font sizes are in the same units: title 10, results 18, status 7.
//
// Feed panel (top -> bottom):
//   "Live camera" title -> camera image (4:3) -> status row ("DB: ..." left, "Build ..." right).
// Captured panel (top -> bottom):
//   "Captured frame" title -> captured image (4:3) -> results list, one line per tag
//   ("ID 1 -> Box no 1"), at most MaxResultLines lines. Its top edge stays level with the feed's
//   top edge, so the list grows downward.
//
// Text never leaves its row: each row is one line and is cut with an ellipsis when too long.
// Panels have a semi-transparent dark background with padding behind the content.
//
// Placement: the feed is placed once a head pose exists, straight ahead of the head at
// FeedDistanceMeters. The captured panel is placed to its right, with GapMeters between them.
//
// Controls: right grip re-places the panels in front of the head.
//
// Fonts: TextMeshPro needs its "TMP Essentials" import once per project
// (Window > TextMeshPro > Import TMP Essentials). Without it, no text is drawn and an error is logged.

using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BoxTagApp
{
    public class BoxTagLayout : MonoBehaviour
    {
        private const float MmToMetres = 0.001f;
        private const float DefaultAspect = 4f / 3f;

        [Header("Placement (metres)")]
        [Tooltip("Width of the live feed panel in metres. Its height follows the content.")]
        [SerializeField] private float m_feedWidthMeters = 0.45f;

        [Tooltip("Distance in metres from the head to the feed panel, measured straight ahead.")]
        [SerializeField] private float m_feedDistanceMeters = 1.0f;

        [Tooltip("Vertical offset from eye level in metres. Negative = lower than eye level.")]
        [SerializeField] private float m_feedHeightOffsetMeters = -0.15f;

        [Tooltip("Tilt in degrees. Positive values lean the top of the panel toward the user.")]
        [SerializeField] private float m_feedTiltDegrees = 10f;

        [Tooltip("Width of the captured-frame panel in metres. Its height follows the content.")]
        [SerializeField] private float m_capturedWidthMeters = 0.30f;

        [Tooltip("Gap in metres between the feed panel's right edge and the captured panel's left edge.")]
        [SerializeField] private float m_gapMeters = 0.05f;

        [Header("Panel look (mm)")]
        [Tooltip("Padding in whole mm between each panel's edge and its content. The background covers it.")]
        [SerializeField] private int m_panelPaddingMm = 8;

        [Tooltip("Vertical space in mm between rows (title, image, status or results).")]
        [SerializeField] private float m_rowSpacingMm = 4f;

        [Tooltip("Panel background colour. Alpha 0.6 = 60% opaque black.")]
        [SerializeField] private Color m_backgroundColor = new Color(0f, 0f, 0f, 0.6f);

        [Tooltip("Text colour for all panels.")]
        [SerializeField] private Color m_textColor = Color.white;

        [Header("Text (font size in mm)")]
        [Tooltip("Title size: \"Live camera\" and \"Captured frame\".")]
        [SerializeField] private float m_titleFontSize = 10f;

        [Tooltip("Results size: one line per detected tag.")]
        [SerializeField] private float m_resultFontSize = 18f;

        [Tooltip("Status size: \"DB: ...\" and \"Build ...\".")]
        [SerializeField] private float m_statusFontSize = 7f;

        [Tooltip("Most result lines shown. If there are more tags, the last line says \"+N more\".")]
        [SerializeField] private int m_maxResultLines = 5;

        [Tooltip("Row height = font size x this factor. Leaves room for descenders so they are not clipped.")]
        [SerializeField] private float m_rowHeightFactor = 1.3f;

        [Tooltip("Vertical space in mm between result lines.")]
        [SerializeField] private float m_resultSpacingMm = 2f;

        [Header("Legacy scene objects (replaced by the canvases; hidden at start)")]
        [Tooltip("Old feed quad. Hidden, not deleted, so the scene reference stays valid.")]
        [SerializeField] private Transform m_feedPanel;

        [Tooltip("Old captured-frame quad. Hidden at start.")]
        [SerializeField] private Transform m_capturedPanel;

        [Tooltip("Old title TextMesh. Hidden at start.")]
        [SerializeField] private TextMesh m_titleText;

        [Tooltip("Old result TextMesh. Hidden at start.")]
        [SerializeField] private TextMesh m_idText;

        private RectTransform m_feedRoot;
        private RectTransform m_capturedRoot;
        private RawImage m_feedImage;
        private RawImage m_capturedImage;
        private LayoutElement m_feedImageSlot;
        private LayoutElement m_capturedImageSlot;
        private Image m_flashOverlay;
        private TMP_Text m_databaseText;
        private TMP_Text m_buildText;
        private TMP_Text[] m_resultRows;

        private float m_feedAspect = DefaultAspect;
        private float m_capturedAspect = DefaultAspect;
        private bool m_placed;
        private bool m_sizeLogged;
        private Coroutine m_flashRoutine;

        private void Awake()
        {
            HideLegacyObjects();

            if (TMP_Settings.defaultFontAsset == null)
            {
                Debug.LogError("[BoxTag] No TMP default font. In the Editor: Window > TextMeshPro > " +
                               "Import TMP Essentials, then rebuild. Text will not show until then.");
            }

            // Feed panel: title, camera image, status row.
            m_feedRoot = CreateRoot("BoxTagFeedPanel", m_feedWidthMeters * 1000f);
            AddText(m_feedRoot, "FeedTitle", m_titleFontSize, TextAlignmentOptions.Left).text = "Live camera";
            m_feedImage = AddImageRow(m_feedRoot, "FeedImage", out m_feedImageSlot, out _);
            AddStatusRow(m_feedRoot);

            // Captured panel: title, captured image (with flash overlay), results list.
            m_capturedRoot = CreateRoot("BoxTagCapturedPanel", m_capturedWidthMeters * 1000f);
            AddText(m_capturedRoot, "CapturedTitle", m_titleFontSize, TextAlignmentOptions.Left).text = "Captured frame";
            m_capturedImage = AddImageRow(m_capturedRoot, "CapturedImage", out m_capturedImageSlot, out m_flashOverlay);
            AddResultRows(m_capturedRoot);

            ApplyImageHeight(m_feedImageSlot, m_feedRoot, m_feedAspect);
            ApplyImageHeight(m_capturedImageSlot, m_capturedRoot, m_capturedAspect);

            m_buildText.text = "Build " + Application.version;
            SetDatabaseStatus("DB: starting");
            SetResults("Press A or right trigger to capture");
            LayoutRest();
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

        /// <summary>Sets the live feed's image, aspect and flips. The height follows the aspect.</summary>
        public void SetFeedAspect(float aspect, bool flipX, bool flipY)
        {
            m_feedAspect = Valid(aspect, m_feedAspect);
            if (m_feedImage != null)
            {
                // Flips are UV flips on the RawImage. They look the same as the old negative scale.
                m_feedImage.uvRect = new Rect(flipX ? 1f : 0f, flipY ? 1f : 0f, flipX ? -1f : 1f, flipY ? -1f : 1f);
            }
            ApplyImageHeight(m_feedImageSlot, m_feedRoot, m_feedAspect);
            LayoutRest();
        }

        /// <summary>Sets the captured frame's aspect. The height follows the aspect.</summary>
        public void SetCapturedAspect(float aspect)
        {
            m_capturedAspect = Valid(aspect, m_capturedAspect);
            ApplyImageHeight(m_capturedImageSlot, m_capturedRoot, m_capturedAspect);
            LayoutRest();
        }

        /// <summary>Sets the live camera image.</summary>
        public void SetFeedTexture(Texture texture)
        {
            if (m_feedImage != null)
            {
                m_feedImage.texture = texture;
            }
        }

        /// <summary>Sets the captured frame image.</summary>
        public void SetCapturedTexture(Texture texture)
        {
            if (m_capturedImage != null)
            {
                m_capturedImage.texture = texture;
            }
        }

        /// <summary>
        /// Sets the results list. Each line is one row, cut with an ellipsis if it is too long.
        /// More than MaxResultLines lines: the last row says "+N more".
        /// </summary>
        public void SetResults(string text)
        {
            if (m_resultRows == null)
            {
                return;
            }

            string[] lines = (text ?? string.Empty).Split('\n');
            int max = m_resultRows.Length;
            bool overflow = lines.Length > max;

            for (int i = 0; i < max; i++)
            {
                if (i >= lines.Length)
                {
                    m_resultRows[i].gameObject.SetActive(false);
                    continue;
                }

                m_resultRows[i].gameObject.SetActive(true);
                m_resultRows[i].text = overflow && i == max - 1
                    ? "+" + (lines.Length - (max - 1)) + " more"
                    : lines[i];
            }

            LayoutRest();
        }

        /// <summary>Sets the database status line (bottom-left of the feed).</summary>
        public void SetDatabaseStatus(string text)
        {
            if (m_databaseText != null)
            {
                m_databaseText.text = text;
            }
        }

        /// <summary>Short white flash over the captured image, so a press is clearly acknowledged.</summary>
        public void FlashCapturedFrame()
        {
            if (m_flashOverlay == null || !isActiveAndEnabled)
            {
                return;
            }
            if (m_flashRoutine != null)
            {
                StopCoroutine(m_flashRoutine);
            }
            m_flashRoutine = StartCoroutine(FlashRoutine());
        }

        /// <summary>Places the feed in front of the head, then lays out the captured panel.</summary>
        public void PlaceAll()
        {
            Transform head = Camera.main != null ? Camera.main.transform : null;
            if (head == null || m_feedRoot == null)
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

            // A canvas faces its local -Z, so point -Z at the head. Then tilt the top toward the user.
            Quaternion facing = Quaternion.LookRotation(position - head.position);
            Quaternion tilt = Quaternion.Euler(-m_feedTiltDegrees, 0f, 0f);
            m_feedRoot.SetPositionAndRotation(position, facing * tilt);

            // World-space canvases need a camera for sorting and culling.
            m_feedRoot.GetComponent<Canvas>().worldCamera = Camera.main;
            if (m_capturedRoot != null)
            {
                m_capturedRoot.GetComponent<Canvas>().worldCamera = Camera.main;
            }

            m_placed = true;
            LayoutRest();
        }

        // Positions the captured panel to the right of the feed, top edges level. Forces the layout
        // first, so the panel sizes are the real ones.
        private void LayoutRest()
        {
            if (m_feedRoot == null)
            {
                return;
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(m_feedRoot);
            Transform feed = m_feedRoot;
            float feedHalfW = m_feedRoot.rect.width * MmToMetres * 0.5f;
            float feedHalfH = m_feedRoot.rect.height * MmToMetres * 0.5f;

            if (m_capturedRoot != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(m_capturedRoot);
                float capHalfW = m_capturedRoot.rect.width * MmToMetres * 0.5f;
                float capHalfH = m_capturedRoot.rect.height * MmToMetres * 0.5f;

                // Right of the feed, top edges level, so the results list grows downward.
                Vector3 capPos = feed.position
                                 + feed.right * (feedHalfW + m_gapMeters + capHalfW)
                                 + feed.up * (feedHalfH - capHalfH);
                m_capturedRoot.SetPositionAndRotation(capPos, feed.rotation);
            }

            if (!m_sizeLogged && m_placed)
            {
                m_sizeLogged = true;
                Debug.Log("[BoxTag] panel size mm: feed " + m_feedRoot.rect.width.ToString("F0") + "x" +
                          m_feedRoot.rect.height.ToString("F0") + ", captured " +
                          (m_capturedRoot != null ? m_capturedRoot.rect.width.ToString("F0") + "x" +
                                                    m_capturedRoot.rect.height.ToString("F0") : "none") +
                          " (1 unit = 1 mm)");
            }
        }

        // ---- Canvas construction -------------------------------------------------------------

        // One world-space canvas: background, padding, vertical rows, height fits its content.
        private RectTransform CreateRoot(string name, float widthMm)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(transform, false);

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            var rect = (RectTransform)go.transform;
            rect.sizeDelta = new Vector2(widthMm, 0f);
            rect.localScale = Vector3.one * MmToMetres; // 1 canvas unit = 1 mm

            var background = go.AddComponent<Image>();
            background.color = m_backgroundColor;
            background.raycastTarget = false;

            var group = go.AddComponent<VerticalLayoutGroup>();
            int pad = m_panelPaddingMm;
            group.padding = new RectOffset(pad, pad, pad, pad);
            group.spacing = m_rowSpacingMm;
            group.childAlignment = TextAnchor.UpperLeft;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = true;
            group.childForceExpandHeight = false;

            var fitter = go.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            return rect;
        }

        // One line of TMP text in a fixed-height row. It takes the row's full width and never
        // wraps; too-long text is cut with an ellipsis.
        private TMP_Text AddText(Transform parent, string name, float fontSize, TextAlignmentOptions alignment)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var text = go.AddComponent<TextMeshProUGUI>();
            text.fontSize = fontSize;
            text.color = m_textColor;
            text.alignment = alignment;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.raycastTarget = false;
            text.text = string.Empty;

            float height = fontSize * m_rowHeightFactor;
            var element = go.AddComponent<LayoutElement>();
            element.minWidth = 0f;
            element.preferredWidth = 0f;
            element.flexibleWidth = 1f;
            element.minHeight = height;
            element.preferredHeight = height;
            return text;
        }

        // Camera image row (full width). The overlay is the capture flash, drawn over the image.
        private RawImage AddImageRow(RectTransform root, string name, out LayoutElement slot, out Image flash)
        {
            var row = new GameObject(name, typeof(RectTransform));
            row.transform.SetParent(root, false);
            slot = row.AddComponent<LayoutElement>();
            slot.minWidth = 0f;
            slot.preferredWidth = 0f;
            slot.flexibleWidth = 1f;

            var imageGo = new GameObject("Image", typeof(RectTransform));
            imageGo.transform.SetParent(row.transform, false);
            StretchToParent(imageGo);
            var raw = imageGo.AddComponent<RawImage>();
            raw.raycastTarget = false;

            var flashGo = new GameObject("Flash", typeof(RectTransform));
            flashGo.transform.SetParent(row.transform, false);
            StretchToParent(flashGo);
            flashGo.AddComponent<LayoutElement>().ignoreLayout = true;
            flash = flashGo.AddComponent<Image>();
            flash.color = new Color(1f, 1f, 1f, 0f);
            flash.raycastTarget = false;

            return raw;
        }

        // Feed status row: "DB: ..." on the left, "Build ..." on the right. Each half is cut with an ellipsis.
        private void AddStatusRow(RectTransform root)
        {
            var row = new GameObject("StatusRow", typeof(RectTransform));
            row.transform.SetParent(root, false);

            var group = row.AddComponent<HorizontalLayoutGroup>();
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = false;

            float height = m_statusFontSize * m_rowHeightFactor;
            var element = row.AddComponent<LayoutElement>();
            element.flexibleWidth = 1f;
            element.minHeight = height;
            element.preferredHeight = height;

            m_databaseText = AddText(row.transform, "DatabaseStatus", m_statusFontSize, TextAlignmentOptions.Left);
            m_buildText = AddText(row.transform, "BuildLabel", m_statusFontSize, TextAlignmentOptions.Right);
        }

        // Results list: a vertical group of one-line rows. Unused rows are inactive, so they take no space.
        private void AddResultRows(RectTransform root)
        {
            var container = new GameObject("Results", typeof(RectTransform));
            container.transform.SetParent(root, false);

            var group = container.AddComponent<VerticalLayoutGroup>();
            group.spacing = m_resultSpacingMm;
            group.childAlignment = TextAnchor.UpperLeft;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = true;
            group.childForceExpandHeight = false;

            var element = container.AddComponent<LayoutElement>();
            element.minWidth = 0f;
            element.preferredWidth = 0f;
            element.flexibleWidth = 1f;

            int count = Mathf.Max(1, m_maxResultLines);
            m_resultRows = new TMP_Text[count];
            for (int i = 0; i < count; i++)
            {
                m_resultRows[i] = AddText(container.transform, "Result" + i, m_resultFontSize, TextAlignmentOptions.Left);
            }
        }

        // Image height = the panel's inner width / aspect.
        private void ApplyImageHeight(LayoutElement slot, RectTransform root, float aspect)
        {
            if (slot == null || root == null)
            {
                return;
            }
            float innerWidth = root.sizeDelta.x - 2f * m_panelPaddingMm;
            slot.minHeight = innerWidth / aspect;
            slot.preferredHeight = innerWidth / aspect;
        }

        private IEnumerator FlashRoutine()
        {
            m_flashOverlay.color = new Color(1f, 1f, 1f, 0.8f);
            yield return new WaitForSeconds(0.15f);
            m_flashOverlay.color = new Color(1f, 1f, 1f, 0f);
            m_flashRoutine = null;
        }

        private void HideLegacyObjects()
        {
            // The old quads and TextMesh text are replaced by the canvases. They are hidden, not
            // deleted, so the scene references stay valid.
            if (m_feedPanel != null) m_feedPanel.gameObject.SetActive(false);
            if (m_capturedPanel != null) m_capturedPanel.gameObject.SetActive(false);
            if (m_titleText != null) m_titleText.gameObject.SetActive(false);
            if (m_idText != null) m_idText.gameObject.SetActive(false);
        }

        private static void StretchToParent(GameObject go)
        {
            var rect = (RectTransform)go.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static float Valid(float aspect, float fallback)
        {
            return aspect > 0f ? aspect : fallback;
        }
    }
}
