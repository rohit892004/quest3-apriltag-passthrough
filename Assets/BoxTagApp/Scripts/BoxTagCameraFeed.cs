// BoxTagCameraFeed.cs
// L1: shows the live passthrough camera feed on a world-space panel.
//
// Camera ownership: the ONLY camera owner in BoxTagApp is the MRUK
// PassthroughCameraAccess component (Meta.XR namespace) referenced below.
// Do not add WebCamTextureManager or AprilTag scripts to this scene.

using Meta.XR;
using UnityEngine;

namespace BoxTagApp
{
    public class BoxTagCameraFeed : MonoBehaviour
    {
        [Tooltip("MRUK camera component. Must be in this scene (Left eye).")]
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;

        [Tooltip("Quad renderer that displays the camera image.")]
        [SerializeField] private Renderer m_screen;

        [Tooltip("Distance in metres from the head to the panel, set once when the panel is placed.")]
        [SerializeField] private float m_distanceMeters = 1.2f;

        [Tooltip("Height of the panel in metres. Width follows the camera aspect ratio.")]
        [SerializeField] private float m_heightMeters = 0.8f;

        [Tooltip("Mirror the image horizontally. Turn on if the feed looks mirrored on the headset.")]
        [SerializeField] private bool m_flipX = false;

        [Tooltip("Mirror the image vertically. Turn on if the feed looks upside down.")]
        [SerializeField] private bool m_flipY = false;

        private bool m_placed;
        private Vector2Int m_appliedResolution = Vector2Int.zero;

        private void Awake()
        {
            // Create a per-object material instance so the shared asset is not modified.
            if (m_screen != null)
            {
                m_screen.material = new Material(m_screen.sharedMaterial);
            }
        }

        private void Update()
        {
            // Wait until the camera has permission and is streaming frames.
            if (m_cameraAccess == null || m_screen == null || !m_cameraAccess.IsPlaying)
            {
                return;
            }

            // GetTexture() returns the GPU texture that the camera updates every frame.
            // It is not a CPU copy, so no stall happens here.
            Texture cameraTexture = m_cameraAccess.GetTexture();
            if (cameraTexture == null)
            {
                return;
            }

            if (m_screen.material.mainTexture != cameraTexture)
            {
                m_screen.material.mainTexture = cameraTexture;
            }

            // Resize the panel when the camera resolution changes (it can differ from the requested one).
            Vector2Int resolution = m_cameraAccess.CurrentResolution;
            if (resolution != m_appliedResolution && resolution.x > 0 && resolution.y > 0)
            {
                m_appliedResolution = resolution;
                ApplyAspectRatio(resolution);
            }

            if (!m_placed)
            {
                PlaceInFrontOfHead();
            }
        }

        /// <summary>Transform of the live feed panel (used to place the captured-frame panel next to it).</summary>
        public Transform ScreenTransform => m_screen != null ? m_screen.transform : null;

        /// <summary>Panel width in metres (the panel's local X scale).</summary>
        public float ScreenWidthMeters => m_screen != null ? m_screen.transform.localScale.x : 0f;

        /// <summary>Moves the panel in front of the user's head again (useful while testing).</summary>
        public void Recenter()
        {
            m_placed = false;
        }

        // Sets the quad scale so the image is not stretched.
        // A Unity quad is 1 x 1 units, so scale = (width, height).
        private void ApplyAspectRatio(Vector2Int resolution)
        {
            float aspect = (float)resolution.x / resolution.y;
            float width = m_heightMeters * aspect;
            float signX = m_flipX ? -1f : 1f;
            float signY = m_flipY ? -1f : 1f;
            m_screen.transform.localScale = new Vector3(width * signX, m_heightMeters * signY, 1f);
        }

        // Places the panel once, straight ahead of the head, facing the user.
        // After that the panel stays where it is in world space.
        private void PlaceInFrontOfHead()
        {
            Transform head = Camera.main != null ? Camera.main.transform : null;
            if (head == null)
            {
                return; // No head pose yet; try again next frame.
            }

            Vector3 forward = head.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
            {
                forward = Vector3.forward;
            }
            forward.Normalize();

            Vector3 position = head.position + forward * m_distanceMeters;
            m_screen.transform.position = position;

            // A Unity quad's visible side faces its local -Z, so point -Z at the head.
            m_screen.transform.rotation = Quaternion.LookRotation(position - head.position);

            m_placed = true;
        }
    }
}
