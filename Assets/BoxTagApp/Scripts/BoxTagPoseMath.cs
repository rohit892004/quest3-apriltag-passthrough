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
        /// Converts MRUK CameraIntrinsics (given for SensorResolution) to the resolution of the
        /// image we detect on. Focal length and principal point scale with the resolution ratio.
        /// </summary>
        public static PixelIntrinsics ScaleIntrinsics(
            Vector2 focalLength,
            Vector2 principalPoint,
            Vector2Int sensorResolution,
            Vector2Int imageResolution)
        {
            double sx = (double)imageResolution.x / sensorResolution.x;
            double sy = (double)imageResolution.y / sensorResolution.y;
            return new PixelIntrinsics
            {
                Fx = focalLength.x * sx,
                Fy = focalLength.y * sy,
                Cx = principalPoint.x * sx,
                Cy = principalPoint.y * sy,
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
