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
//   - Intrinsics are scaled from SensorResolution to the detected image size.
//   - LensOffset is NOT applied again: MRUK GetCameraPose() already includes it.
//   - Only the first Width*Height entries of GetColors() are valid pixels. MRUK allocates the
//     buffer as Width*Height*4 Color32 entries.
//
// Camera ownership: this script only reads from the MRUK PassthroughCameraAccess component.

using System.Collections.Generic;
using Meta.XR;
using Unity.Collections;
using UnityEngine;

namespace BoxTagApp
{
    public class BoxTagCapture : MonoBehaviour
    {
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;
        [SerializeField] private BoxTagDetector m_detector;
        [SerializeField] private BoxTagCameraFeed m_feed;

        [Tooltip("Quad that shows the captured frame.")]
        [SerializeField] private Renderer m_capturedScreen;

        [Tooltip("Text that lists the detected tag IDs.")]
        [SerializeField] private TextMesh m_resultText;

        [Tooltip("Height of the captured-frame panel in metres. Width follows the image aspect ratio.")]
        [SerializeField] private float m_capturedHeightMeters = 0.6f;

        private Texture2D m_capturedTexture;
        private bool m_capturedPanelPlaced;

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
                res);

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
                " fx=" + scaled.Fx.ToString("F1") + " fy=" + scaled.Fy.ToString("F1") +
                " cx=" + scaled.Cx.ToString("F1") + " cy=" + scaled.Cy.ToString("F1") +
                " camPos=" + frame.CameraPose.position.ToString("F3") +
                " camRot=" + frame.CameraPose.rotation.eulerAngles.ToString("F1"));

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
                lines.Add("ID " + tag.Id + "  (dist " + tag.Position.magnitude.ToString("F2") + " m)");
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

            float aspect = (float)frame.Width / frame.Height;
            float width = m_capturedHeightMeters * aspect;
            m_capturedScreen.transform.localScale = new Vector3(width, m_capturedHeightMeters, 1f);

            PlaceCapturedPanelNextToFeed(width);
        }

        // Places the captured-frame panel once, to the right of the live feed, facing the same way.
        private void PlaceCapturedPanelNextToFeed(float capturedWidth)
        {
            if (m_capturedPanelPlaced || m_feed == null || m_feed.ScreenTransform == null)
            {
                return;
            }

            Transform feed = m_feed.ScreenTransform;
            float gap = 0.1f;
            Vector3 offset = feed.right * (m_feed.ScreenWidthMeters * 0.5f + gap + capturedWidth * 0.5f);

            m_capturedScreen.transform.position = feed.position + offset;
            m_capturedScreen.transform.rotation = feed.rotation;

            // Result text sits above the captured frame.
            if (m_resultText != null)
            {
                m_resultText.transform.position = m_capturedScreen.transform.position + Vector3.up * (m_capturedHeightMeters * 0.5f + 0.12f);
                m_resultText.transform.rotation = feed.rotation;
            }

            m_capturedPanelPlaced = true;
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
