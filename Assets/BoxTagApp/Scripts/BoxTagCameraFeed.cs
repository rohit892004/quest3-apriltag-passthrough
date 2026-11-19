// BoxTagCameraFeed.cs
// L1: shows the live passthrough camera feed on a world-space panel.
//
// Camera ownership: the ONLY camera owner in BoxTagApp is the MRUK
// PassthroughCameraAccess component (Meta.XR namespace) referenced below.
// Do not add WebCamTextureManager or AprilTag scripts to this scene.
//
// Size and placement are owned by BoxTagLayout. This script only shows the image and reports
// its aspect and flips to the layout.

using Meta.XR;
using UnityEngine;

namespace BoxTagApp
{
    public class BoxTagCameraFeed : MonoBehaviour
    {
        [Tooltip("MRUK camera component. Must be in this scene (Left eye).")]
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;

        [Tooltip("Layout that owns the feed panel and shows the camera image on it (set by Setup Scene).")]
        [SerializeField] private BoxTagLayout m_layout;

        [Tooltip("Mirror the image horizontally. Turn on if the feed looks mirrored on the headset.")]
        [SerializeField] private bool m_flipX = false;

        [Tooltip("Mirror the image vertically. Turn on if the feed looks upside down.")]
        [SerializeField] private bool m_flipY = false;

        private Vector2Int m_appliedResolution = Vector2Int.zero;
        private Texture m_shownTexture;

        private void Update()
        {
            // Wait until the camera has permission and is streaming frames.
            if (m_cameraAccess == null || m_layout == null || !m_cameraAccess.IsPlaying)
            {
                return;
            }

            // GetTexture() returns the GPU texture that the camera updates every frame.
            // It is not a CPU copy, so no stall happens here. The layout shows it in the panel's image.
            Texture cameraTexture = m_cameraAccess.GetTexture();
            if (cameraTexture == null)
            {
                return;
            }

            if (m_shownTexture != cameraTexture)
            {
                m_shownTexture = cameraTexture;
                m_layout.SetFeedTexture(cameraTexture);
            }

            // Tell the layout the aspect whenever the camera resolution changes.
            Vector2Int resolution = m_cameraAccess.CurrentResolution;
            if (resolution != m_appliedResolution && resolution.x > 0 && resolution.y > 0)
            {
                m_appliedResolution = resolution;
                if (m_layout != null)
                {
                    m_layout.SetFeedAspect((float)resolution.x / resolution.y, m_flipX, m_flipY);
                }
            }
        }
    }
}
