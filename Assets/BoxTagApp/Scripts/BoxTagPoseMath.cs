// BoxTagPoseMath.cs
// Pure math helpers for L2/L4. Safe to call from worker threads (no UnityEngine objects are touched).
//
// Contents:
//   1. ScaleIntrinsics       – camera intrinsics for SensorResolution -> image resolution.
//   2. ToUnityCameraSpace    – THE ONE place where AprilTag (OpenCV) pose becomes Unity camera space.
//   3. CapturedFrame         – a captured image plus the pose/intrinsics taken at capture time.

using System;
using UnityEngine;

namespace BoxTagApp
{
    public static class BoxTagPoseMath
    {
        // Intrinsics of the image we actually detect on, in pixels.
        public struct PixelIntrinsics
        {
            public double Fx, Fy, Cx, Cy;
        }

        /// <summary>
        /// Sensor crop used by MRUK (PassthroughCameraAccess.CalcSensorCropRegion, MRUK 85.0.0):
        /// the output image is a centred crop of the sensor, with one uniform scale.
        /// </summary>
        public struct SensorCrop
        {
            public float X, Y, Width, Height; // in sensor pixels
        }

        /// <summary>
        /// Mirrors MRUK's CalcSensorCropRegion exactly:
        ///   scale = imageRes / sensorRes; scale /= max(scale.x, scale.y)
        ///   crop  = (sensor.x*(1-scale.x)/2, sensor.y*(1-scale.y)/2, sensor.x*scale.x, sensor.y*scale.y)
        /// </summary>
        public static SensorCrop CalcSensorCrop(Vector2Int sensorResolution, Vector2Int imageResolution)
        {
            Vector2 sensor = sensorResolution;
            Vector2 scale = new Vector2((float)imageResolution.x / sensor.x, (float)imageResolution.y / sensor.y);
            scale /= Mathf.Max(scale.x, scale.y);
            return new SensorCrop
            {
                X = sensor.x * (1f - scale.x) * 0.5f,
                Y = sensor.y * (1f - scale.y) * 0.5f,
                Width = sensor.x * scale.x,
                Height = sensor.y * scale.y,
            };
        }

        /// <summary>
        /// Converts MRUK CameraIntrinsics (in SENSOR pixels) to the detected image's pixels.
        /// Steps (same as MRUK's viewport mapping):
        ///   1. crop = CalcSensorCrop(sensor, image)     (centred crop, e.g. 1728x1296 at y=216)
        ///   2. s    = image.x / crop.Width               (one uniform scale; equals image.y / crop.Height)
        ///   3. fx' = fx*s, fy' = fy*s, cx' = (cx - crop.X)*s, cy' = (cy - crop.Y)*s
        /// </summary>
        public static PixelIntrinsics ScaleIntrinsics(
            Vector2 focalLength,
            Vector2 principalPoint,
            Vector2Int sensorResolution,
            Vector2Int imageResolution,
            out SensorCrop crop,
            out float uniformScale)
        {
            crop = CalcSensorCrop(sensorResolution, imageResolution);
            uniformScale = imageResolution.x / crop.Width;
            return new PixelIntrinsics
            {
                Fx = focalLength.x * uniformScale,
                Fy = focalLength.y * uniformScale,
                Cx = (principalPoint.x - crop.X) * uniformScale,
                Cy = (principalPoint.y - crop.Y) * uniformScale,
            };
        }

        /// <summary>
        /// ONE clearly defined conversion: AprilTag pose -> Unity camera space.
        ///
        /// Input (AprilTag / OpenCV convention):
        ///   - Camera axes: x right, y DOWN, z forward.
        ///   - R (3x3, row-major) and t (3) map tag coordinates into camera coordinates:
        ///       p_camOpenCV = R * p_tag + t
        ///   - t is the tag origin position in the camera frame, in metres.
        ///
        /// Output (Unity camera space, same as a camera child transform):
        ///   - x right, y UP, z forward (left-handed).
        ///
        /// Math: Unity is OpenCV with the y axis flipped. Let F = diag(1, -1, 1).
        ///   position = F * t
        ///   rotation = F * R * F   (a change of basis; equal to the sign flips
        ///              (x,y,z,w)->(-x,y,-z,w) that the AprilTag Unity job applies).
        /// The rotation matrix's columns are the tag axes in Unity camera space; they are
        /// turned into a quaternion with LookRotation(forward = column 2, up = column 1).
        /// </summary>
        public static void ToUnityCameraSpace(
            double[,] r,
            double[] t,
            out Vector3 position,
            out Quaternion rotation)
        {
            // Sign of each axis for F = diag(1, -1, 1).
            double[] f = { 1.0, -1.0, 1.0 };

            position = new Vector3((float)(f[0] * t[0]), (float)(f[1] * t[1]), (float)(f[2] * t[2]));

            // m = F * R * F
            var m = new double[3, 3];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    m[i, j] = f[i] * r[i, j] * f[j];
                }
            }

            // Columns of m are the tag's x, y, z axes expressed in Unity camera space.
            // Column 2 (tag z) is the forward direction and column 1 (tag y) is up.
            var forward = new Vector3((float)m[0, 2], (float)m[1, 2], (float)m[2, 2]);
            var up = new Vector3((float)m[0, 1], (float)m[1, 1], (float)m[2, 1]);
            rotation = Quaternion.LookRotation(forward, up);
        }
    }

    /// <summary>
    /// One captured camera frame plus everything needed to place results in the world later (L4):
    /// the camera pose and intrinsics are copied at capture time, so later head movement does not
    /// change where the tag is.
    /// </summary>
    public sealed class CapturedFrame
    {
        // Pixels in Unity texture order (bottom row first), exactly as GetColors() returns them.
        public Color32[] Pixels;
        public int Width;
        public int Height;

        public DateTime Timestamp;          // MRUK PassthroughCameraAccess.Timestamp at capture
        public Pose CameraPose;             // MRUK GetCameraPose(); already includes LensOffset
        public BoxTagPoseMath.PixelIntrinsics Intrinsics; // scaled to Width x Height
    }
}
