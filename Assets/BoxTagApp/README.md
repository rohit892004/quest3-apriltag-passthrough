# BoxTagApp

Quest 3 app that reads AprilTags from the passthrough camera and shows labels from a database.
Own code lives only in `Assets/BoxTagApp/`. The AprilTag library (`Assets/AprilTag/`) and the
sample code (`Assets/PassthroughCameraApiSamples/`) are referenced or left unchanged.

## Status

| Level | Status |
|---|---|
| L1 – camera feed on a world-space panel | Code written, compile-checked. **Not yet built or run on the headset.** |
| L2 – capture, detect tag36h11, show IDs | Not started |
| L3 – tag ID → label from database | Not started |
| L4 – labels above real tags | Not started |
| Bonus – continuous mode, tagStandard52h13 | Not started |

## Architecture (L1)

- `Scripts/BoxTagCameraFeed.cs` – reads frames from MRUK `PassthroughCameraAccess` and shows them on a quad. Places the quad once in front of the head, then leaves it in world space.
- `Editor/BoxTagAppSetup.cs` – `Tools > BoxTagApp > Setup Scene`. Builds `Scene/BoxTagApp.unity` and makes it the only enabled build scene.
- `Materials/BoxTagCameraFeed.mat` – created by the setup menu (Unlit/Texture).

**Camera ownership:** only the MRUK `PassthroughCameraAccess` component opens the camera. The scene does not contain `WebCamTextureManager` or AprilTag scripts. The camera can only be opened by one owner.

**Camera prefab:** the scene instantiates `Assets/PassthroughCameraApiSamples/PassthroughCamera/Prefabs/PassthroughCameraAccessPrefab.prefab` (Left eye, 1280×960 requested). Permissions and the passthrough layer come from the sample's `RequestPermissionsOnce`, which runs for every scene except `StartScene`.

## Build setup (L1)

1. Open the project in the Unity version in `ProjectSettings/ProjectVersion.txt` (6000.0.66f2).
2. Run `Tools > BoxTagApp > Setup Scene`. This creates `Assets/BoxTagApp/Scene/BoxTagApp.unity` and updates Build Settings:
   - `BoxTagApp` is index 0 and enabled.
   - All other scenes stay in the list but are disabled, so the APK does not start the FRC `StartScene`.
3. In Build Profiles, check that the Android profile uses the Build Settings scene list (no per-profile override).

## Headset test (L1)

1. Connect a Quest 3 or 3S (USB or Horizon Link 2.1+). Passthrough must be enabled.
2. Build and install the APK, or press Play with the Quest connected.
3. Accept the camera permission prompt.
4. Expected: a camera image panel about 1.2 m in front of you, stationary when you turn your head.
5. If the image is mirrored or upside down, toggle `Flip X` / `Flip Y` on `BoxTagCameraFeed` in the scene. Use `Recenter()` to move the panel again.

## Planned: how to add a tag to the database (L3)

Not implemented yet. The plan is a `tags.json` file with entries `{ "id": 1, "family": "tag36h11", "label": "Box no 1" }`, loaded from a URL with a local cache and an offline fallback. A new tag will need only a database change.
