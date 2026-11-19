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
// Panel sizes and positions are owned by BoxTagLayout. This script only supplies the image,
// its aspect, and the ID text.
//
// Feedback on capture: a short beep and a brief white flash of the captured-frame panel.
//
// Camera ownership: this script only reads from the MRUK PassthroughCameraAccess component.

using System.Collections;
using System.Collections.Generic;
using Meta.XR;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace BoxTagApp
{
    [RequireComponent(typeof(AudioSource))]
    public class BoxTagCapture : MonoBehaviour
    {
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;
        [SerializeField] private BoxTagDetector m_detector;

        [Tooltip("Tag ID -> label database (set by Setup Scene).")]
        [SerializeField] private BoxTagDatabase m_database;

        [Tooltip("Layout that owns the panels, the captured image and the results text (set by Setup Scene).")]
        [SerializeField] private BoxTagLayout m_layout;

        private Texture2D m_capturedTexture;
        private AudioSource m_audio;
        private AudioClip m_beep;
        private BoxTagLabels m_labels;

        private void Awake()
        {
            m_audio = GetComponent<AudioSource>();
            m_audio.playOnAwake = false;
            m_audio.spatialBlend = 0f; // plain 2D beep, not positioned in the room
            m_beep = MakeBeep(880f, 0.12f);

            // L4: floating labels. Added at runtime, so the scene needs no change.
            m_labels = GetComponent<BoxTagLabels>();
            if (m_labels == null)
            {
                m_labels = gameObject.AddComponent<BoxTagLabels>();
            }
            m_labels.Initialize(m_layout, m_database, m_cameraAccess);
        }

        private void Update()
        {
            // Right B: toggle continuous mode.
            if (OVRInput.GetDown(OVRInput.Button.Two))
            {
                ToggleContinuous();
            }

            // In continuous mode the readbacks drive detection; A and the trigger do nothing.
            if (m_continuous)
            {
                UpdateContinuous();
                return;
            }

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

            m_detector.Submit(frame, results => OnDetectionDone(frame, results, false));
        }

        // Runs on the MAIN thread when the worker finishes.
        private void OnDetectionDone(CapturedFrame frame, List<TagResult> results, bool continuous)
        {
            if (results == null)
            {
                SetText("Detection failed (see log)");
                return;
            }

            if (continuous)
            {
                Debug.Log("[BoxTag] continuous: detect=" + m_detector.LastDetectionMs.ToString("F1") +
                          " ms copy=" + frame.CopyMs.ToString("F1") + " ms tags=" + results.Count);
            }

            // L4: one label per tag, placed with the capture-time camera pose.
            if (m_labels != null)
            {
                m_labels.ApplyDetections(frame, results, continuous);
            }
            DrawReprojectionOverlay(results);

            if (results.Count == 0)
            {
                SetText("No tags found");
                Debug.Log("[BoxTag] No tags found.");
                return;
            }

            var lines = new List<string>();
            foreach (TagResult tag in results)
            {
                // The label comes from the database only (tags.json); unknown IDs are shown as such.
                string label = m_database != null && m_database.TryGetLabel(tag.Id, out string found)
                    ? found
                    : "(not in database)";
                lines.Add("ID " + tag.Id + " -> " + label);
                Debug.Log("[BoxTag] Tag ID=" + tag.Id + " label=" + label +
                          " hamming=" + tag.Hamming +
                          " margin=" + tag.DecisionMargin.ToString("F1") +
                          " posCam=" + tag.Position.ToString("F3") +
                          " rotCam=" + tag.Rotation.eulerAngles.ToString("F1"));
            }
            SetText(string.Join("\n", lines));
        }

        // Shows the captured frame on its panel. The layout sizes and places the panel.
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

            if (m_layout != null)
            {
                m_layout.SetCapturedTexture(m_capturedTexture);
                m_layout.SetCapturedAspect((float)frame.Width / frame.Height);
            }
        }

        // ---- Continuous mode (right B) -------------------------------------------------------
        // Non-blocking: AsyncGPUReadback fetches the camera texture while the frame keeps running.
        // One readback and one detection are in flight at a time, about 5 per second. The pose and
        // intrinsics are read when the readback is requested, so they match the pixels.

        private const float ContinuousIntervalSeconds = 0.2f; // about 5 per second
        private const int MaxReadbackErrors = 3;

        private bool m_continuous;
        private bool m_readbackInFlight;
        private float m_nextRequestTime;
        private int m_readbackErrors;

        private void ToggleContinuous()
        {
            m_continuous = !m_continuous;
            m_readbackErrors = 0;
            m_nextRequestTime = 0f;
            if (m_layout != null)
            {
                m_layout.SetContinuous(m_continuous);
            }
            Debug.Log(m_continuous ? "[BoxTag] Continuous: ON" : "[BoxTag] Continuous: OFF");
        }

        private void UpdateContinuous()
        {
            if (m_readbackInFlight || m_detector.IsBusy || Time.realtimeSinceStartup < m_nextRequestTime)
            {
                return;
            }
            if (m_cameraAccess == null || !m_cameraAccess.IsPlaying)
            {
                return;
            }

            Texture source = m_cameraAccess.GetTexture();
            if (source == null)
            {
                return;
            }

            // Capture-time values for the frame that is about to be read back.
            var intrinsics = m_cameraAccess.Intrinsics;
            BoxTagPoseMath.PixelIntrinsics scaled = BoxTagPoseMath.ScaleIntrinsics(
                intrinsics.FocalLength,
                intrinsics.PrincipalPoint,
                intrinsics.SensorResolution,
                new Vector2Int(source.width, source.height),
                out _,
                out _);

            var frame = new CapturedFrame
            {
                Width = source.width,
                Height = source.height,
                Timestamp = m_cameraAccess.Timestamp,
                CameraPose = m_cameraAccess.GetCameraPose(),
                Intrinsics = scaled,
            };

            m_readbackInFlight = true;
            m_nextRequestTime = Time.realtimeSinceStartup + ContinuousIntervalSeconds;
            AsyncGPUReadback.Request(source, 0, TextureFormat.RGBA32, request => OnReadbackDone(request, frame));
        }

        // Runs on the main thread when the GPU has the pixels. Copies them, then starts detection.
        private void OnReadbackDone(AsyncGPUReadbackRequest request, CapturedFrame frame)
        {
            m_readbackInFlight = false;

            if (request.hasError)
            {
                m_readbackErrors++;
                Debug.LogError("[BoxTag] continuous readback failed (" + m_readbackErrors + "/" + MaxReadbackErrors + ")");
                if (m_readbackErrors >= MaxReadbackErrors)
                {
                    m_continuous = false;
                    if (m_layout != null)
                    {
                        m_layout.SetContinuous(false);
                    }
                    SetText("Continuous stopped: readback failed");
                    Debug.LogError("[BoxTag] Continuous: OFF (readback not available on this camera texture)");
                }
                return;
            }

            // Turned off while this readback was running.
            if (!m_continuous)
            {
                return;
            }
            m_readbackErrors = 0;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            NativeArray<Color32> data = request.GetData<Color32>();
            // Copy now: the readback buffer is released when this callback returns.
            Color32[] pixels = data.ToArray();
            frame.CopyMs = (float)stopwatch.Elapsed.TotalMilliseconds;
            if (pixels.Length < frame.Width * frame.Height)
            {
                return;
            }
            frame.Pixels = pixels;

            ShowCapturedFrame(frame);
            if (!m_detector.Submit(frame, results => OnDetectionDone(frame, results, true)))
            {
                Debug.Log("[BoxTag] continuous: detector busy, frame skipped");
            }
        }

        // Draws each tag's detected corners (green) and reprojected corners (red) on the captured image,
        // so the fit can be seen on the panel. Image pixels are top-down; texture rows are bottom-up.
        private void DrawReprojectionOverlay(List<TagResult> results)
        {
            if (m_capturedTexture == null || results == null || results.Count == 0)
            {
                return;
            }

            int w = m_capturedTexture.width;
            int h = m_capturedTexture.height;
            Color32[] px = m_capturedTexture.GetPixels32();
            var green = new Color32(0, 255, 0, 255);
            var red = new Color32(255, 0, 0, 255);
            foreach (TagResult tag in results)
            {
                if (tag.Corners == null || tag.ReprojectedCorners == null)
                {
                    continue;
                }
                for (int i = 0; i < 4; i++)
                {
                    MarkPixel(px, w, h, tag.Corners[i], green);
                    MarkPixel(px, w, h, tag.ReprojectedCorners[i], red);
                }
            }
            m_capturedTexture.SetPixels32(px);
            m_capturedTexture.Apply();
        }

        private static void MarkPixel(Color32[] px, int w, int h, Vector2 imagePixel, Color32 color)
        {
            int cx = Mathf.RoundToInt(imagePixel.x);
            int cy = h - 1 - Mathf.RoundToInt(imagePixel.y);
            for (int dy = -2; dy <= 2; dy++)
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x >= 0 && x < w && y >= 0 && y < h)
                    {
                        px[y * w + x] = color;
                    }
                }
            }
        }

        // Beep plus a short white flash over the captured image, so the press is clearly acknowledged.
        private void PlayCaptureFeedback()
        {
            if (m_beep != null)
            {
                m_audio.PlayOneShot(m_beep);
            }

            if (m_layout != null)
            {
                m_layout.FlashCapturedFrame();
            }
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
            if (m_layout != null)
            {
                m_layout.SetResults(text);
            }
        }
    }
}
