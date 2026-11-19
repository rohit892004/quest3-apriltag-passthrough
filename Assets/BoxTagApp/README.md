# BoxTagApp

Quest 3 app that reads AprilTags from the passthrough camera and shows labels from a database.
Own code lives only in `Assets/BoxTagApp/`. The AprilTag library (`Assets/AprilTag/`) and the
sample code (`Assets/PassthroughCameraApiSamples/`) are referenced or left unchanged.

App identity: product name `BoxTag App`, Android package `com.assignment.boxtagapp`
(a **placeholder**). Change `AndroidPackageName` in `Editor/BoxTagAppBuild.cs` to your own name.

## Status

| Level | Status |
|---|---|
| L1 – camera feed on a world-space panel | Works on headset (feed visible). |
| L2 – capture, detect tag36h11, show IDs | Code built and installed. Needs a headset test with a printed tag. |
| L3 – tag ID → label from database | Not started |
| L4 – labels above real tags | Not started (L2 already computes camera-space tag poses) |
| Bonus – continuous mode, tagStandard52h13 | Not started |

## Architecture

| File | Role |
|---|---|
| `Scripts/BoxTagCameraFeed.cs` | L1. Shows the live camera (MRUK `PassthroughCameraAccess`) on a world-space panel. Placed once in front of the head, then world-locked. |
| `Scripts/BoxTagCapture.cs` | L2. Button press → copies one frame, reads pose/timestamp/intrinsics for the same frame, shows the frame and the detected IDs. |
| `Scripts/BoxTagDetector.cs` | L2. tag36h11 detection on a **worker thread** (one job at a time). Results return to the main thread through a queue. |
| `Scripts/BoxTagPoseMath.cs` | L2. Pure math: intrinsics scaling, the single OpenCV → Unity camera-space pose conversion, and the `CapturedFrame` data. |
| `Editor/BoxTagAppSetup.cs` | `Tools > BoxTagApp > Setup Scene`. Builds `Scene/BoxTagApp.unity`, passthrough setup, Build Settings. |
| `Editor/BoxTagAppBuild.cs` | `Tools > BoxTagApp > Build APK`. Headless APK build, sets the app identity. |

**Camera ownership:** only the MRUK `PassthroughCameraAccess` opens the camera. No
`WebCamTextureManager` or AprilTag camera scripts are in this scene.

### Capture details (L2)
- Input: right controller **A** or right **index trigger**.
- `GetColors()` blocks briefly; acceptable for a single press. Only the first `Width*Height`
  entries are valid pixels.
- Pose, timestamp and intrinsics are read right after `GetColors()`, so they describe the same frame.
- Intrinsics: MRUK gives them for `SensorResolution`. Focal length and principal point are scaled
  to the detected image size.
- `GetCameraPose()` already includes `LensOffset`, so it is not applied again.
- Detection image: green channel, rows reversed to top-down (same as the AprilTag library's converter).

### Coordinates (L2)
`BoxTagPoseMath.ToUnityCameraSpace` is the only place the conversion happens:
- AprilTag/OpenCV camera: x right, y down, z forward. `R, t` map tag → camera.
- Unity camera: x right, y up, z forward.
- `position = F·t`, `rotation = F·R·F` with `F = diag(1,-1,1)`. This matches the library's own sign flips.

### Passthrough (full room)
Set by the Setup Scene script, copied from the sample `CameraViewer.unity`:
- `OVRManager.isInsightPassthroughEnabled = true`, and `requestPassthroughCameraAccessPermissionOnStartup = true`.
- `OVRProjectConfig.insightPassthroughSupport = Supported`.
- `OVRPassthroughLayer`: `projectionSurfaceType = Reconstructed`, `overlayType = Underlay`.
- All cameras under the rig: Solid Color, background `(0,0,0,0)`.
- `RenderSettings.skybox = null`, ambient Flat. The default skybox was hiding the real room.

## Build setup

1. Open the project in Unity 6000.0.66f2. Close the editor first if you run batch builds (Unity
   refuses to open a project that's already open).
2. Setup Scene (menu, or headless):
   `Unity.exe -batchmode -nographics -quit -projectPath <project> -executeMethod BoxTagAppSetup.SetupScene -logFile <log>`
3. Build APK (menu, or headless):
   `Unity.exe -batchmode -nographics -quit -projectPath <project> -executeMethod BoxTagAppBuild.BuildApkHeadless -logFile <log>`
   Output: `Builds/BoxTagApp_L2.apk`. Success line: `[BoxTagAppBuild] SUCCESS`.
   The build refuses unless exactly `BoxTagApp` is enabled in Build Settings.
4. Install: `npx -y @meta-quest/hzdb app install -r Builds/BoxTagApp_L2.apk`
5. Launch: `npx -y @meta-quest/hzdb app launch com.assignment.boxtagapp`

## Headset test (L2)

1. Print a tag36h11 tag (10 cm outer black square) and hold it 40–80 cm from the headset.
2. Open the app, accept the camera prompt, and wait for the live panel.
3. Press **A** (or the right index trigger). A captured-frame panel appears to the right of the feed.
4. Expected: text shows `ID <n>` with distance. Logcat shows `[BoxTag] Tag ID=…`.
5. Check the captured frame: is it mirrored or upside down? Set `Mirror Input X` or `Flip Input Y`
   on `BoxTagDetector` in the scene and retry. A mirrored image does not decode.

## Planned: how to add a tag to the database (L3)

Not implemented yet. The plan is a `tags.json` file with entries `{ "id": 1, "family": "tag36h11", "label": "Box no 1" }`, loaded from a URL with a local cache and an offline fallback. A new tag will need only a database change.
