// BoxTagDetector.cs
// L2: AprilTag detection (tag36h11) on a WORKER thread, so the headset frame never stalls.
//
// Threading rules used here:
//   - Native objects (detector, family, images, detections, poses) are only touched on the worker
//     thread, and only one job runs at a time (IsBusy guards this).
//   - The worker never touches Unity objects. It returns plain data (TagResult).
//   - Results are handed back to the main thread through a queue and run in Update().
//
// Library used: the AprilTag Unity library in Assets/AprilTag (keijiro's jp.keijiro.apriltag,
// BSD-2). It is only referenced here, not edited.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using AprilTag.Interop;
using UnityEngine;

namespace BoxTagApp
{
    /// <summary>One detected tag, in Unity camera space (see BoxTagPoseMath.ToUnityCameraSpace).</summary>
    public sealed class TagResult
    {
        public int Id;
        public int Hamming;
        public float DecisionMargin;
        public Vector3 Position;   // tag origin in camera space, metres
        public Quaternion Rotation; // tag orientation in camera space

        // Image pixels of the captured image, OpenCV convention (origin top-left, y down).
        public Vector2 Center;
        public Vector2[] Corners;            // detected corners
        public Vector2[] ReprojectedCorners; // tag corners projected with Position/Rotation and the intrinsics
        public float ReprojErrPx;            // mean corner error, pixels
    }

    public class BoxTagDetector : MonoBehaviour
    {
        [Tooltip("Edge length in metres of the tag's OUTER BLACK square (the AprilTag library's tag size). Print 10 cm tags -> 0.10.")]
        [SerializeField] private float m_tagSizeMeters = 0.10f;

        [Tooltip("Native quad decimation. 2 is faster; 1 finds smaller tags but is slower.")]
        [SerializeField] private float m_quadDecimate = 2f;

        [Tooltip("Mirror the detection input horizontally. Use if the feed is mirrored, because mirrored tags do not decode.")]
        [SerializeField] private bool m_mirrorInputX = false;

        [Tooltip("Flip the detection input vertically (after the library's own bottom-to-top flip).")]
        [SerializeField] private bool m_flipInputY = false;

        private Detector m_detector;
        private Family m_family;
        private readonly ConcurrentQueue<Action> m_mainThreadQueue = new ConcurrentQueue<Action>();

        /// <summary>True while a detection job is running on the worker thread.</summary>
        public bool IsBusy { get; private set; }

        /// <summary>Tag size in metres used for the next job (read by the main thread for display).</summary>
        public float TagSizeMeters => m_tagSizeMeters;

        private void Awake()
        {
            // Native setup happens on the main thread once. Only the worker uses it afterwards.
            m_detector = Detector.Create();
            m_family = Family.CreateTag36h11(); // tag36h11 is the family we support in L2
            m_detector.ThreadCount = 2;          // native worker threads for the quad search
            m_detector.QuadDecimate = m_quadDecimate;
            m_detector.AddFamily(m_family);
        }

        private void Update()
        {
            // Run completion callbacks on the main thread.
            while (m_mainThreadQueue.TryDequeue(out Action action))
            {
                action();
            }
        }

        /// <summary>
        /// Starts detection on the worker thread. Returns false if a job is already running.
        /// onDone is called on the MAIN thread with the results (null if the job failed).
        /// </summary>
        public bool Submit(CapturedFrame frame, Action<List<TagResult>> onDone)
        {
            if (IsBusy || frame == null)
            {
                return false;
            }
            IsBusy = true;

            float tagSize = m_tagSizeMeters; // copy before leaving the main thread
            Task.Run(() =>
            {
                List<TagResult> results = null;
                string error = null;
                try
                {
                    results = RunDetection(frame, tagSize);
                }
                catch (Exception e)
                {
                    error = e.ToString();
                }

                m_mainThreadQueue.Enqueue(() =>
                {
                    IsBusy = false;
                    if (error != null)
                    {
                        Debug.LogError("[BoxTag] Detection failed: " + error);
                    }
                    onDone(results);
                });
            });
            return true;
        }

        // Runs on the WORKER thread. Pure native + math; no Unity API calls.
        private List<TagResult> RunDetection(CapturedFrame frame, float tagSize)
        {
            var results = new List<TagResult>();

            // Intrinsics for the image we detect on (scaled from sensor resolution in capture).
            var k = frame.Intrinsics;

            using (ImageU8 image = ImageU8.Create(frame.Width, frame.Height))
            {
                FillGrayImage(frame, image);

                using (DetectionArray detections = m_detector.Detect(image))
                {
                    for (int i = 0; i < detections.Length; i++)
                    {
                        // Keep the ID and quality numbers before the native struct goes away.
                        int id = detections[i].ID;
                        int hamming = detections[i].Hamming;
                        float margin = detections[i].DecisionMargin;

                        var info = new DetectionInfo(ref detections[i], tagSize, k.Fx, k.Fy, k.Cx, k.Cy);
                        // Fully qualified: UnityEngine also has a type called Pose.
                        using (var pose = new AprilTag.Interop.Pose(ref info))
                        {
                            // R and t: tag -> camera, OpenCV convention. Row-major, as in AsFloat3x3.
                            var r = new double[3, 3]
                            {
                                { pose.R.e00, pose.R.e01, pose.R.e02 },
                                { pose.R.e10, pose.R.e11, pose.R.e12 },
                                { pose.R.e20, pose.R.e21, pose.R.e22 },
                            };
                            var t = new double[] { pose.t.e0, pose.t.e1, pose.t.e2 };

                            BoxTagPoseMath.ToUnityCameraSpace(r, t, out Vector3 position, out Quaternion rotation);

                            // Reprojection check in image pixels (same convention as the intrinsics k).
                            var detected = new Vector2[]
                            {
                                new Vector2((float)detections[i].Corner1.x, (float)detections[i].Corner1.y),
                                new Vector2((float)detections[i].Corner2.x, (float)detections[i].Corner2.y),
                                new Vector2((float)detections[i].Corner3.x, (float)detections[i].Corner3.y),
                                new Vector2((float)detections[i].Corner4.x, (float)detections[i].Corner4.y),
                            };
                            float reprojError = BoxTagPoseMath.ReprojectionError(r, t, k, tagSize, detected, out Vector2[] reprojected);
                            (double cx, double cy) = detections[i].Center;

                            results.Add(new TagResult
                            {
                                Id = id,
                                Hamming = hamming,
                                DecisionMargin = margin,
                                Position = position,
                                Rotation = rotation,
                                Center = new Vector2((float)cx, (float)cy),
                                Corners = detected,
                                ReprojectedCorners = reprojected,
                                ReprojErrPx = reprojError,
                            });
                        }
                    }
                }
            }

            return results;
        }

        // Converts the captured frame into the library's grayscale image.
        // Same mapping as the library's own ImageConverter (green channel, rows reversed so the
        // image is top-down), plus optional horizontal/vertical mirror for testing.
        private void FillGrayImage(CapturedFrame frame, ImageU8 image)
        {
            Span<byte> buffer = image.Buffer;
            int stride = image.Stride;
            int w = frame.Width;
            int h = frame.Height;
            Color32[] px = frame.Pixels;

            for (int y = 0; y < h; y++)
            {
                // y = 0 is the bottom row in Unity texture order -> last row of the detection image.
                int dstRow = h - 1 - y;
                if (m_flipInputY)
                {
                    dstRow = y;
                }

                int rowOffset = dstRow * stride;
                for (int x = 0; x < w; x++)
                {
                    int dstX = m_mirrorInputX ? (w - 1 - x) : x;
                    buffer[rowOffset + dstX] = px[y * w + x].g;
                }
            }
        }

        private void OnDestroy()
        {
            // If a job is still running, leave the native objects alive rather than free them under it.
            if (IsBusy)
            {
                return;
            }
            m_detector?.RemoveFamily(m_family);
            m_detector?.Dispose();
            m_family?.Dispose();
            m_detector = null;
            m_family = null;
        }
    }
}
