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

        [Tooltip("Quad renderer that displays the camera image.")]
        [SerializeField] private Renderer m_screen;

        [Tooltip("Layout that sizes and places the panel (set by Setup Scene).")]
        [SerializeField] private BoxTagLayout m_layout;

        [Tooltip("Mirror the image horizontally. Turn on if the feed looks mirrored on the headset.")]
        [SerializeField] private bool m_flipX = false;

        [Tooltip("Mirror the image vertically. Turn on if the feed looks upside down.")]
        [SerializeField] private bool m_flipY = false;

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
