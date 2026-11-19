// BoxTagCapture.cs
// L2: capture one frame on a button press, detect AprilTags, and show the frame and the IDs.
//
// Input: right controller A button (OVRInput.Button.One) or right index trigger
//        (OVRInput.Button.SecondaryIndexTrigger). Press = capture, one at a time.
//
// Capture rules (from the L2 plan):
//   - GetColors() blocks briefly. That is acceptable for a single press.
//   - Pose, timestamp and intrinsics are read IMMEDIATELY after GetColors(), so they describe the
//     same frame. They are stored with the frame (needed for L4 world placement).
//   - Intrinsics are mapped exactly like MRUK: centred sensor crop, then one uniform scale
//     (see BoxTagPoseMath.ScaleIntrinsics).
//   - LensOffset is NOT applied again: MRUK GetCameraPose() already includes it.
//   - Only the first Width*Height entries of GetColors() are valid pixels. MRUK allocates the
//     buffer as Width*Height*4 Color32 entries.
//
// Feedback on capture: a short beep and a brief white flash of the captured-frame panel.
//
// Camera ownership: this script only reads from the MRUK PassthroughCameraAccess component.

using System.Collections;
using System.Collections.Generic;
using Meta.XR;
using Unity.Collections;
using UnityEngine;

namespace BoxTagApp
{
    [RequireComponent(typeof(AudioSource))]
    public class BoxTagCapture : MonoBehaviour
    {
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;
        [SerializeField] private BoxTagDetector m_detector;
        [SerializeField] private BoxTagCameraFeed m_feed;

        [Tooltip("Quad that shows the captured frame.")]
        [SerializeField] private Renderer m_capturedScreen;

        [Tooltip("Title above the captured frame (\"Captured frame\").")]
        [SerializeField] private TextMesh m_titleText;

        [Tooltip("Detected tag IDs, shown in large text under the captured frame.")]
        [SerializeField] private TextMesh m_resultText;

        [Tooltip("Gap in metres between the live feed panel and the captured-frame panel.")]
        [SerializeField] private float m_gapMeters = 0.3f;

        [Tooltip("Used only if the live feed panel is missing.")]
        [SerializeField] private float m_fallbackHeightMeters = 0.8f;

        private Texture2D m_capturedTexture;
        private bool m_capturedPanelPlaced;
        private AudioSource m_audio;
        private AudioClip m_beep;
        private Coroutine m_flash;

        private void Awake()
        {
            m_audio = GetComponent<AudioSource>();
            m_audio.playOnAwake = false;
            m_audio.spatialBlend = 0f; // plain 2D beep, not positioned in the room
            m_beep = MakeBeep(880f, 0.12f);
        }

        private void Update()
        {
            bool pressed = OVRInput.GetDown(OVRInput.Button.One) ||
                           OVRInput.GetDown(OVRInput.Button.SecondaryIndexTrigger);
            if (pressed)
            {
                TryCapture();
            }
        }

        private void TryCapture()
        {
            if (m_cameraAccess == null || !m_cameraAccess.IsPlaying)
            {
                SetText("Camera not ready");
                return;
            }
            if (m_detector.IsBusy)
            {
                SetText("Still detecting the last capture...");
                return;
            }

            Vector2Int res = m_cameraAccess.CurrentResolution;
            int pixelCount = res.x * res.y;

            // Blocking GPU readback. Fine for a single button press.
            NativeArray<Color32> colors = m_cameraAccess.GetColors();
            if (!colors.IsCreated || colors.Length < pixelCount)
            {
                SetText("No camera pixels yet");
                return;
            }

            // Copy to managed memory now, so the worker thread never touches MRUK's buffer.
            Color32[] pixels = colors.GetSubArray(0, pixelCount).ToArray();

            // Read these right after GetColors() so they describe the same frame.
            var intrinsics = m_cameraAccess.Intrinsics;
            BoxTagPoseMath.PixelIntrinsics scaled = BoxTagPoseMath.ScaleIntrinsics(
                intrinsics.FocalLength,
                intrinsics.PrincipalPoint,
                intrinsics.SensorResolution,
                res,
                out BoxTagPoseMath.SensorCrop crop,
                out float uniformScale);

            var frame = new CapturedFrame
            {
                Pixels = pixels,
                Width = res.x,
                Height = res.y,
                Timestamp = m_cameraAccess.Timestamp,
                CameraPose = m_cameraAccess.GetCameraPose(),
                Intrinsics = scaled,
            };

            Debug.Log(
                "[BoxTag] Capture " + res.x + "x" + res.y +
                " sensor=" + intrinsics.SensorResolution.x + "x" + intrinsics.SensorResolution.y +
                " crop=(" + crop.X.ToString("F1") + "," + crop.Y.ToString("F1") + " " +
                crop.Width.ToString("F1") + "x" + crop.Height.ToString("F1") + ")" +
                " scale=" + uniformScale.ToString("F4") +
                " fx=" + scaled.Fx.ToString("F1") + " fy=" + scaled.Fy.ToString("F1") +
                " cx=" + scaled.Cx.ToString("F1") + " cy=" + scaled.Cy.ToString("F1") +
                " camPos=" + frame.CameraPose.position.ToString("F3") +
                " camRot=" + frame.CameraPose.rotation.eulerAngles.ToString("F1"));

            PlayCaptureFeedback();
            ShowCapturedFrame(frame);
            SetText("Detecting...");

            m_detector.Submit(frame, OnDetectionDone);
        }

        // Runs on the MAIN thread when the worker finishes.
        private void OnDetectionDone(List<TagResult> results)
        {
            if (results == null)
            {
                SetText("Detection failed (see log)");
                return;
            }

            if (results.Count == 0)
            {
                SetText("No tags found");
                Debug.Log("[BoxTag] No tags found.");
                return;
            }

            var lines = new List<string>();
            foreach (TagResult tag in results)
            {
                lines.Add("ID " + tag.Id + "  (" + tag.Position.magnitude.ToString("F2") + " m)");
                Debug.Log("[BoxTag] Tag ID=" + tag.Id +
                          " hamming=" + tag.Hamming +
                          " margin=" + tag.DecisionMargin.ToString("F1") +
                          " posCam=" + tag.Position.ToString("F3") +
                          " rotCam=" + tag.Rotation.eulerAngles.ToString("F1"));
            }
            SetText(string.Join("\n", lines));
        }

        // Shows the captured frame on its own panel, next to the live feed.
        private void ShowCapturedFrame(CapturedFrame frame)
        {
            if (m_capturedTexture == null || m_capturedTexture.width != frame.Width || m_capturedTexture.height != frame.Height)
            {
                if (m_capturedTexture != null)
                {
                    Destroy(m_capturedTexture);
                }
                m_capturedTexture = new Texture2D(frame.Width, frame.Height, TextureFormat.RGBA32, false);
            }

            // Pixels are in Unity texture order, so they display upright as the camera image does.
            m_capturedTexture.SetPixels32(frame.Pixels);
            m_capturedTexture.Apply();

            if (m_capturedScreen == null)
            {
                return;
            }
            m_capturedScreen.material.mainTexture = m_capturedTexture;

            // Same height as the live feed, width from the image's aspect ratio.
            float height = m_feed != null && m_feed.ScreenHeightMeters > 0f
                ? m_feed.ScreenHeightMeters
                : m_fallbackHeightMeters;
            float aspect = (float)frame.Width / frame.Height;
            float width = height * aspect;
            m_capturedScreen.transform.localScale = new Vector3(width, height, 1f);

            PlaceCapturedPanelNextToFeed(width, height);
        }

        // Places the captured-frame panel once, to the right of the live feed, facing the same way.
        // Title sits above it, and the detected IDs sit below it.
        private void PlaceCapturedPanelNextToFeed(float capturedWidth, float capturedHeight)
        {
            if (m_capturedPanelPlaced || m_feed == null || m_feed.ScreenTransform == null)
            {
                return;
            }

            Transform feed = m_feed.ScreenTransform;
            Vector3 offset = feed.right * (m_feed.ScreenWidthMeters * 0.5f + m_gapMeters + capturedWidth * 0.5f);

            Transform panel = m_capturedScreen.transform;
            panel.position = feed.position + offset;
            panel.rotation = feed.rotation;

            if (m_titleText != null)
            {
                m_titleText.transform.position = panel.position + panel.up * (capturedHeight * 0.5f + 0.05f);
                m_titleText.transform.rotation = feed.rotation;
            }

            if (m_resultText != null)
            {
                m_resultText.transform.position = panel.position - panel.up * (capturedHeight * 0.5f + 0.05f);
                m_resultText.transform.rotation = feed.rotation;
            }

            m_capturedPanelPlaced = true;
        }

        // Beep plus a short white flash of the captured panel, so the press is clearly acknowledged.
        private void PlayCaptureFeedback()
        {
            if (m_beep != null)
            {
                m_audio.PlayOneShot(m_beep);
            }

            if (m_capturedScreen != null)
            {
                if (m_flash != null)
                {
                    StopCoroutine(m_flash);
                }
                m_flash = StartCoroutine(FlashCapturedPanel());
            }
        }

        private IEnumerator FlashCapturedPanel()
        {
            Material mat = m_capturedScreen.material;
            // Unlit/Texture multiplies the texture by _Color, so values above 1 brighten it.
            mat.color = new Color(2.5f, 2.5f, 2.5f, 1f);
            yield return new WaitForSeconds(0.15f);
            mat.color = Color.white;
            m_flash = null;
        }

        // Builds a short sine beep at runtime, so no audio asset is needed in the project.
        private static AudioClip MakeBeep(float frequencyHz, float seconds)
        {
            const int sampleRate = 44100;
            int samples = (int)(seconds * sampleRate);
            var data = new float[samples];
            for (int i = 0; i < samples; i++)
            {
                float envelope = 1f - (float)i / samples; // fade out to avoid a click
                data[i] = 0.3f * envelope * Mathf.Sin(2f * Mathf.PI * frequencyHz * i / sampleRate);
            }
            var clip = AudioClip.Create("BoxTagBeep", samples, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        private void SetText(string text)
        {
            if (m_resultText != null)
            {
                m_resultText.text = text;
            }
        }
    }
}
