# BoxTag App

A Quest 3 / 3S app that reads AprilTags (tag36h11) with the passthrough camera, shows the camera
feed and the detected IDs, looks up each ID's label in an online database, and places the label
floating above the real tag in the room.

- App name: **BoxTag App**. Android package: `com.asas.boxtagapp` (change `AndroidPackageName` in `Editor/BoxTagAppBuild.cs`).
- Unity: **6000.0.66f2**, Built-in render pipeline, Android, OpenXR / Meta Quest.
- Release APK: `Builds/BoxTagApp_Final.apk`.

## Status

| Part | Status |
|---|---|
| L1 – live camera feed on a world-space panel | Works on headset. |
| L2 – single capture (A), detect tag36h11, show IDs | Works on headset. **This is the reliable detection path.** |
| L3 – tag ID → label from an online database (Gist) | Works on headset. Left X reloads. |
| L4 – labels floating above the real tags | Works on headset in DepthRaycast mode. |
| Bonus – continuous detection (B) | **Beta / experimental.** Not reliable yet. Use single capture (A). |
| tagStandard52h13 | **Not done.** Needs a native library rebuild. Only tag36h11 is detected. |

## What each level does and how it works

**L1 – Live feed.** The MRUK `PassthroughCameraAccess` component opens the left camera. Each frame,
`GetTexture()` (a GPU texture) is shown on the feed panel. The panel is a world-space canvas placed
in front of the head when the app starts. The right grip short tap re-places it.

**L2 – Capture and detect.** Pressing A (or the right trigger) calls `GetColors()`, which blocks
briefly. The pixels, the camera pose (`GetCameraPose()`), the timestamp, and the intrinsics are read
right after it, so they describe the same frame. The intrinsics are mapped from sensor pixels to
image pixels exactly like MRUK's sensor crop (`BoxTagPoseMath.ScaleIntrinsics`). The pixels are copied,
and the AprilTag library detects tag36h11 on a worker thread. The main thread gets the results and
shows the captured frame with the IDs. A green/red reprojection overlay is available with Left Y.

**L3 – Tag database.** `BoxTagDatabase` loads the tag labels in this order:
1. The bundled default (`Resources/BoxTagDefaultTags.json`) at start. Status `DB: offline default`.
2. The cache (`persistentDataPath/boxtag_tags.json`), if present. Status `DB: cached`.
3. The remote URL (`RemoteUrl` in `Data/BoxTagDatabaseSettings.asset`). On success the data is replaced
   and saved to the cache. Status `DB: online, N tags`.

Each download adds `?t=<unix ms>` to the URL and sends `Cache-Control: no-cache`, so the latest file is
always fetched. A tag ID with no entry shows `(not in database)` in the panel and `ID n (not in DB)` in orange
in the world.

**L4 – Floating labels.** For each detected tag, `BoxTagLabels` places one label about 5 cm above the
tag centre (world up). The label has a dark rounded background, about 3 cm text, a leader bar to a dot at
the tag centre, and always faces the user.

Tag position has two modes (Left thumbstick click toggles; the status row shows the current one):
- **DepthRaycast (default).** The tag-centre pixel is turned into a ray with
  `PassthroughCameraAccess.ViewportPointToRay` (using the capture-time pose), and the ray is cast into the
  environment depth (`EnvironmentRaycastManager`). The hit point is the tag position. This does not depend on
  the tag size setting. If the raycast fails, the AprilTag pose is used and the dot is yellow.
- **AprilTagPose.** The AprilTag pose only: `world = CapturedFrame.CameraPose × tagPose`. This depends on the
  tag size setting.

In both modes the AprilTag pose gives the orientation.

Stability: a label is placed at its first detection. A re-detection moves it only if the position differs by
more than 3 cm, and then it eases there. A label whose tag is not detected stays where it was, unless the tag
position is inside the captured image (with a margin) and in front of the camera. Then the label fades out
over 0.3 s, and the log says `[BoxTag] tag N not seen in view -> removed`. A tag outside the captured image
keeps its label.

## Architecture

| File | Role |
|---|---|
| `Scripts/BoxTagLayout.cs` | Owns all panels and text. Each panel is a world-space canvas (TextMeshPro, layout groups), built at runtime. Sizes are Inspector fields. |
| `Scripts/BoxTagCameraFeed.cs` | L1. Gives the live camera texture to the layout. |
| `Scripts/BoxTagCapture.cs` | L2. A (single capture) and B (continuous, beta). Reads the pixels and the pose, submits to the detector, and runs the debug view (Left Y). |
| `Scripts/BoxTagDetector.cs` | L2. AprilTag detection on a worker thread. Returns the ID, the pose (Unity convention), the corners, and the reprojection error. |
| `Scripts/BoxTagPoseMath.cs` | L2. Intrinsics mapping (`ScaleIntrinsics`), the one OpenCV → Unity conversion (`ToUnityCameraSpace`), the reprojection check, and `CapturedFrame`. |
| `Scripts/BoxTagDatabase.cs` | L3. Loads tag labels: bundled → cache → remote Gist. Left X reloads. |
| `Scripts/BoxTagDatabaseSettings.cs` | L3. ScriptableObject: the remote URL, timeout, and cache file name. |
| `Scripts/BoxTagLabels.cs` | L4. World labels, DepthRaycast / AprilTagPose, stability rules, removal, right grip (tap re-places, hold clears). |
| `Editor/BoxTagAppSetup.cs` | `Tools > BoxTagApp > Setup Scene`. Builds the scene from scratch. See the warning below. |
| `Editor/BoxTagAppBuild.cs` | `Tools > BoxTagApp > Build APK`. Creates the debug materials, includes their shader, and builds the release APK. |
| `Resources/BoxTagDebug*.mat` | Unlit debug materials (red, green, blue, white, yellow), created by the build. |
| `Resources/BoxTagDefaultTags.json` | Bundled fallback tags (inside the APK). |
| `Data/BoxTagDatabaseSettings.asset` | The remote URL and settings. |

The scene (`Scene/BoxTagApp.unity`) is the only enabled scene. `BoxTagLabels` and the database and
capture connections are created or wired at runtime, so you do not need to edit the scene for L4.

**Warning:** `Setup Scene` creates a new scene and overwrites `Scene/BoxTagApp.unity`. It also resets
manual scene edits. Run it only to rebuild the scene from nothing.

**Camera ownership:** only the MRUK `PassthroughCameraAccess` opens the camera. No `WebCamTexture` or
other camera owner is used.

## Controls

| Control | Action |
|---|---|
| **A** (right) or right trigger | Single capture: detect the tags now. Ignored while Continuous is ON. |
| **X** (left) | Reload the database from the Gist. |
| **Y** (left) | Debug view on or off: tag axes (red x, green y, blue z, 5 cm × 3 mm) and the reprojection corners (green detected, red reprojected). **OFF by default.** |
| **B** (right) | Continuous (beta) on or off. Shown as `Continuous (beta)` on the status row. |
| Right grip, short tap | Re-place the panels in front of the head. |
| Right grip, hold 1 s | Clear all labels. |
| Left thumbstick click | Toggle the pose mode: `DepthRaycast` (default) / `AprilTagPose`. Applies to the next capture. |

The feed panel shows these controls at its bottom:
`A: Capture  X: Reload DB  Y: Debug  B: Continuous (beta)  Grip: Move panels`

## Add a new tag (no rebuild)

1. Print a tag36h11 tag (10 cm outer black square; set the same size in `BoxTagDetector` → Tag Size Meters).
2. Open the Gist and add an entry, for example:
   `{ "id": 3, "family": "tag36h11", "label": "Box no 3" }`
   inside the `tags` array of `tags.json`. Save the Gist.
3. On the headset, press **X**. The status shows `DB reloaded: N tags` for 3 seconds.
4. Capture the tag with **A**. Its label shows `Box no 3`. Existing labels update their text on the reload too.

No code change and no rebuild are needed.

## Build steps

**Requirements:** Unity 6000.0.66f2 with the Android build module, the project's packages (MRUK, Meta XR SDK,
AprilTag library), and TMP Essentials.

1. Open the project in Unity 6000.0.66f2.
2. **Window > TextMeshPro > Import TMP Essentials** (once per project; it is already in `Assets/TextMesh Pro`).
3. Set the remote URL in `Assets/BoxTagApp/Data/BoxTagDatabaseSettings.asset` (or leave the placeholder to use only the bundled tags).
4. Close the Unity editor if you use batch builds on the same project folder.
5. Build the release APK, either from the menu (`Tools > BoxTagApp > Build APK`) or headless:
   ```
   Unity.exe -batchmode -nographics -quit -projectPath <project> -executeMethod BoxTagAppBuild.BuildApkHeadless -logFile <log>
   ```
   Output: `Builds/BoxTagApp_Final.apk`. A successful build prints `[BoxTagAppBuild] SUCCESS`.
   The build also creates the debug materials and adds `Unlit/Color` to Always Included Shaders.
6. Install and launch on the headset with adb (from the project folder):
   ```
   adb install -r "Builds\BoxTagApp_Final.apk"
   adb shell monkey -p com.asas.boxtagapp -c android.intent.category.LAUNCHER 1
   ```

**Permissions:** the manifest declares `horizonos.permission.HEADSET_CAMERA` (camera) and
`com.oculus.permission.USE_SCENE` (environment depth). The app asks for USE_SCENE on first launch. Accept
it, or DepthRaycast will fall back to the AprilTag pose (yellow dots).

**Signing:** the APK is built with the project's Android keystore settings. Check `Player Settings >
Publishing Settings` before you distribute it.

## Quick test checklist

1. The feed shows the room. The controls line is at the bottom of the feed panel.
2. Hold a tag 40–80 cm away and press **A**. The captured panel shows the tag with its ID and label.
3. Press **Y**. The corners show: green on the tag, red on the reprojection, about 1 px apart.
4. Left thumbstick click to switch to AprilTagPose and back. The status row shows the mode.
5. Move your head: the label stays on the tag.
6. Cover the tag and press **A**: the label fades out in about 0.3 s.

## Known limitations

- **tagStandard52h13 is not done.** It needs a native rebuild of the AprilTag library. Only tag36h11 is detected.
- **Continuous (B) is beta and not reliable.** Single capture (A) is the working detection path. Continuous mode
  takes a one-time GetColors reading on each B press to check the image row order.
- **Depth labels depend on the environment depth.** Glass, very dark or featureless surfaces, and tags far away can
  give no depth hit. Those labels fall back to the AprilTag pose.
- **AprilTagPose depends on the tag size setting.** If the printed tag is a different size, the distance is wrong.
- **One tag per ID.** Two physical tags with the same ID share one label.
- **Labels are not saved.** Clearing (grip hold) or restarting the app removes them.
- **Lighting.** AprilTag detection needs a sharp, well-lit image of the tag. Motion blur or glare stops detection.

## Licenses and attribution

- **Own code (BoxTag App):** everything in `Assets/BoxTagApp/` (scripts, editor tools, data, materials, this README).
  The scene `Scene/BoxTagApp.unity` is also part of this app. License: **MIT**, see `Assets/BoxTagApp/LICENSE`.
- **AprilTag library:** `Assets/AprilTag/` (Unity wrapper by keijiro, built on the AprilTag C library). BSD-2-Clause
  (copyright The Regents of The University of Michigan, see `Assets/AprilTag/LICENSE`). Used as-is; not edited.
- **Meta Passthrough Camera API sample:** `Assets/PassthroughCameraApiSamples/`. MIT License (Meta Platforms, Inc.,
  see `Assets/PassthroughCameraApiSamples/LICENSE.txt`). This app uses its camera prefab through `Setup Scene`, and
  the `PassthroughCameraAccess` component from the MRUK package.
- **Meta XR SDK and MRUK packages:** under the Oculus SDK License Agreement (`LICENSE.txt` at the repository root).
  The environment depth and passthrough features come from these packages.
- **TextMeshPro:** the TMP Essentials resources imported into `Assets/TextMesh Pro` from Unity's TextMeshPro package. See the package's own license terms.
