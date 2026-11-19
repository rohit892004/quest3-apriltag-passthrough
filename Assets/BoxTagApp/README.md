# BoxTagApp

Quest 3 app that reads AprilTags from the passthrough camera and shows labels from a database.
Own code lives only in `Assets/BoxTagApp/`. The AprilTag library (`Assets/AprilTag/`) and the
sample code (`Assets/PassthroughCameraApiSamples/`) are referenced or left unchanged.

App identity: product name `BoxTag App`, Android package `com.asas.boxtagapp`.
Change `AndroidPackageName` in `Editor/BoxTagAppBuild.cs` to use a different package.

## Status

| Level | Status |
|---|---|
| L1 – camera feed on a world-space panel | Works on headset. |
| L2 – capture, detect tag36h11, show IDs | Works on headset (beep, captured frame, ID shown). Needs a tag test with a printed tag36h11 tag. |
| L3 – tag ID → label from database | Not started |
| L4 – labels above real tags | Not started (L2 already computes camera-space tag poses) |
| Bonus – continuous mode, tagStandard52h13 | Not started |

## Architecture

| File | Role |
|---|---|
| `Scripts/BoxTagLayout.cs` | **All panel and text sizes/positions.** Inspector fields on the `BoxTagLayout` object. Right grip re-places the panels in front of the head. |
| `Scripts/BoxTagCameraFeed.cs` | L1. Shows the live camera (MRUK `PassthroughCameraAccess`) on the feed panel. Reports its aspect and flips to the layout. |
| `Scripts/BoxTagCapture.cs` | L2. Button press → copies one frame, reads pose/timestamp/intrinsics for the same frame, shows the frame and the IDs. Beep and flash on capture. |
| `Scripts/BoxTagDetector.cs` | L2. tag36h11 detection on a **worker thread** (one job at a time). Results return to the main thread through a queue. |
| `Scripts/BoxTagPoseMath.cs` | L2. Pure math: MRUK-exact intrinsics scaling, the single OpenCV → Unity camera-space pose conversion, and the `CapturedFrame` data. |
| `Editor/BoxTagAppSetup.cs` | `Tools > BoxTagApp > Setup Scene`. Builds `Scene/BoxTagApp.unity`, passthrough setup, Build Settings. |
| `Editor/BoxTagAppBuild.cs` | `Tools > BoxTagApp > Build APK`. Headless APK build, sets the app identity. |

**Camera ownership:** only the MRUK `PassthroughCameraAccess` opens the camera. No
`WebCamTextureManager` or AprilTag camera scripts are in this scene.

## Layout settings (tweak in the Inspector, no code change)

Select the `BoxTagLayout` object in the scene. Units are metres.

| Setting | Default | Meaning |
|---|---|---|
| Feed Width Meters | 0.45 | Live feed width. Height follows the 4:3 image. |
| Feed Distance Meters | 1.0 | Distance straight ahead of the head. |
| Feed Height Offset Meters | -0.15 | Negative = lower than eye level. |
| Feed Tilt Degrees | 10 | Positive leans the top toward the user. |
| Captured Width Meters | 0.30 | Captured frame width. Height follows its 4:3 aspect (0.225 m). |
| Gap Meters | 0.05 | Gap between the feed's right edge and the captured frame's left edge. The captured frame is centred vertically on the feed. |
| Title Height Meters | 0.02 | "Captured frame" title height. |
| ID Line Height Meters | 0.04 | Detected-ID text height per line. |
| Build Height Meters | 0.01 | Build label height (bottom-right of the feed). |

Right grip: press to re-place the panels in front of your current head position.

## Capture details (L2)
- Input: right controller **A** or right **index trigger**.
- `GetColors()` blocks briefly; acceptable for a single press. Only the first `Width*Height`
  entries are valid pixels.
- Pose, timestamp and intrinsics are read right after `GetColors()`, so they describe the same frame.
- Intrinsics follow MRUK exactly (`PassthroughCameraAccess.CalcSensorCropRegion`, MRUK 85.0.0):
  a centred sensor crop (1728×1296 at y=216 for a 1280×960 output), then one uniform scale
  `s = 1280/1728`. So `fx' = fx·s`, `fy' = fy·s`, `cx' = (cx − cropX)·s`, `cy' = (cy − cropY)·s`.
  The capture log prints the crop, the scale, and both focal lengths.
- `GetCameraPose()` already includes `LensOffset`, so it is not applied again.
- Detection image: green channel, rows reversed to top-down (same as the AprilTag library's converter).

## Coordinates (L2)
`BoxTagPoseMath.ToUnityCameraSpace` is the only place the conversion happens:
- AprilTag/OpenCV camera: x right, y down, z forward. `R, t` map tag → camera.
- Unity camera: x right, y up, z forward.
- `position = F·t`, `rotation = F·R·F` with `F = diag(1,-1,1)`. This matches the library's own sign flips.

## Passthrough (full room)
Set by the Setup Scene script, copied from the sample `CameraViewer.unity`:
- `OVRManager.isInsightPassthroughEnabled = true`, and `requestPassthroughCameraAccessPermissionOnStartup = true`.
- `OVRProjectConfig.insightPassthroughSupport = Supported`.
- `OVRPassthroughLayer`: `projectionSurfaceType = Reconstructed`, `overlayType = Underlay`.
- All cameras under the rig: Solid Color, background `(0,0,0,0)`.
- `RenderSettings.skybox = null`, ambient Flat. The default skybox was hiding the real room.

## Build setup

1. Setup Scene (menu, or headless):
   `Unity.exe -batchmode -nographics -quit -projectPath <project> -executeMethod BoxTagAppSetup.SetupScene -logFile <log>`
2. Build APK (menu, or headless):
   `Unity.exe -batchmode -nographics -quit -projectPath <project> -executeMethod BoxTagAppBuild.BuildApkHeadless -logFile <log>`
   Output: `Builds/BoxTagApp_L2.apk`. Success line: `[BoxTagAppBuild] SUCCESS`.
   The build refuses unless exactly `BoxTagApp` is enabled in Build Settings.
   Close the Unity editor before batch runs on the same project folder (Unity refuses to open a
   project that is already open).
3. Install and launch from a cmd window with the Unity SDK adb:
   `"D:\UnityEditior\6000.0.66f2\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe" install -r Builds\BoxTagApp_L2.apk`
   `"...\adb.exe" shell am start -n com.asas.boxtagapp/com.unity3d.player.UnityPlayerGameActivity`

## Headset test (L2)

1. Print a tag36h11 tag (10 cm outer black square) and hold it 40–80 cm from the headset.
2. Open the app, accept the camera prompt, and wait for the live panel.
3. Press **A** (or the right index trigger). The captured-frame panel appears to the right of the feed, with a beep and a flash.
4. Expected: `ID <n>` in large text under the captured frame. Logcat shows `[BoxTag] Tag ID=…`.
5. Check the captured frame: mirrored or upside down? Set `Mirror Input X` or `Flip Input Y` on `BoxTagDetector` in the scene and retry.
6. Capture log: `fx` and `fy` should now be equal (they were 866.8 and 650.1 before the crop fix, which came from a non-uniform scale).

## Planned: how to add a tag to the database (L3)

Not implemented yet. The plan is a `tags.json` file with entries `{ "id": 1, "family": "tag36h11", "label": "Box no 1" }`, loaded from a URL with a local cache and an offline fallback. A new tag will need only a database change.
