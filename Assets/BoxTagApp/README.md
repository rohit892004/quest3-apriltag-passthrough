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
| L2 – capture, detect tag36h11, show IDs | Works on headset (beep, captured frame, ID). |
| L3 – tag ID → label from database | Built (`Builds/BoxTagApp_L3.apk`). Needs a headset test and the real Gist URL. |
| L4 – labels above real tags | Not started (L2 already computes camera-space tag poses) |
| Bonus – continuous mode, tagStandard52h13 | Not started |

## Architecture

| File | Role |
|---|---|
| `Scripts/BoxTagLayout.cs` | **All panel and text sizes/positions.** Inspector fields on the `BoxTagLayout` object. Right grip re-places the panels. Logs the rendered text size. |
| `Scripts/BoxTagCameraFeed.cs` | L1. Shows the live camera (MRUK `PassthroughCameraAccess`) on the feed panel. |
| `Scripts/BoxTagCapture.cs` | L2. Button press → one frame, pose/timestamp/intrinsics for that frame, shows the frame and the IDs. Beep and flash on capture. |
| `Scripts/BoxTagDetector.cs` | L2. tag36h11 detection on a **worker thread**. |
| `Scripts/BoxTagPoseMath.cs` | L2. MRUK-exact intrinsics scaling, the single OpenCV → Unity camera-space pose conversion. |
| `Scripts/BoxTagDatabase.cs` | L3. Loads tag ID → label: bundled default → cache → remote. Left X reloads. |
| `Scripts/BoxTagDatabaseSettings.cs` | L3. ScriptableObject with the remote URL, timeout and cache name. |
| `Data/BoxTagDatabaseSettings.asset` | The settings asset (edit the Remote Url here). |
| `Resources/BoxTagDefaultTags.json` | Bundled fallback database (ships inside the APK). |
| `Editor/BoxTagAppSetup.cs` | `Tools > BoxTagApp > Setup Scene`. Builds the scene, passthrough setup, Build Settings. |
| `Editor/BoxTagAppBuild.cs` | `Tools > BoxTagApp > Build APK`. Headless APK build, sets the app identity. |

**Camera ownership:** only the MRUK `PassthroughCameraAccess` opens the camera.

## Tag database (L3)

The database is a JSON file, served from a GitHub Gist raw link or any HTTPS URL:

```json
{ "tags": [ {"id":1,"family":"tag36h11","label":"Box no 1"},
            {"id":2,"family":"tag36h11","label":"Box no 2"} ] }
```

To add a tag, edit the Gist and press **left X** on the headset. **No code change and no rebuild**
are needed. The code has no ID → label mapping. Only `tag36h11` entries with a label are used.

**Setting the URL:** open `Assets/BoxTagApp/Data/BoxTagDatabaseSettings.asset` in the Inspector and
set **Remote Url** to the raw Gist link. The placeholder `REPLACE_WITH_GIST_RAW_URL` skips the download.
Setup Scene keeps this asset on later runs, so your URL is not overwritten.

**Load order:**
1. Bundled default (`BoxTagDefaultTags.json`), loaded immediately. Status `DB: offline default`.
2. Cache in `persistentDataPath/boxtag_tags.json`, if present. Status `DB: cached`.
3. Remote URL. On success it replaces the data and is saved to the cache. Status `DB: online`.

**Results on the panel:** `ID 1 -> Box no 1`, or `ID 5 -> (not in database)`.
**Status line:** bottom-left of the feed, for example `DB: online, 2 tags`.
**Left X:** reloads the remote file. The result shows as `DB reloaded: N tags` for 3 seconds.

## Layout settings (tweak in the Inspector, no code change)

Select the `BoxTagLayout` object in the scene. Units are metres. Text heights are **rendered** heights.

| Setting | Default | Meaning |
|---|---|---|
| Feed Width Meters | 0.45 | Live feed width. Height follows the 4:3 image. |
| Feed Distance Meters | 1.0 | Distance straight ahead of the head. |
| Feed Height Offset Meters | -0.15 | Negative = lower than eye level. |
| Feed Tilt Degrees | 10 | Positive leans the top toward the user. |
| Captured Width Meters | 0.30 | Captured frame width. Centred vertically on the feed. |
| Gap Meters | 0.05 | Gap between the feed and the captured frame. |
| Title Height Meters | 0.01 | "Captured frame" title. |
| ID Line Height Meters | 0.02 | Detected-ID text, per line. Never wider than the captured panel. |
| Build Height Meters | 0.006 | Build label (bottom-right of the feed). |
| Database Height Meters | 0.008 | Database status line (bottom-left of the feed). |

**Checking the text size:** each text logs `[BoxTag] text '...' rendered size: height=… m, width=… m`.
If the height in the log is not the target, the scaling is wrong. Tell me the log line.

Right grip: press to re-place the panels in front of your current head position.

## Capture details (L2)
- Input: right controller **A** or right **index trigger**. Left **X** reloads the database.
- Intrinsics follow MRUK exactly (`PassthroughCameraAccess.CalcSensorCropRegion`, MRUK 85.0.0):
  a centred sensor crop (1728×1296 at y=216 for a 1280×960 output), then one uniform scale
  `s = 1280/1728`. So `fx' = fx·s`, `fy' = fy·s`, `cx' = (cx − cropX)·s`, `cy' = (cy − cropY)·s`.
  The capture log prints the crop, the scale, and both focal lengths.
- `GetCameraPose()` already includes `LensOffset`, so it is not applied again.

## Coordinates (L2)
`BoxTagPoseMath.ToUnityCameraSpace` is the only place the conversion happens:
- `position = F·t`, `rotation = F·R·F` with `F = diag(1,-1,1)` (OpenCV camera → Unity camera).

## Passthrough (full room)
- `OVRManager.isInsightPassthroughEnabled = true`, `OVRProjectConfig.insightPassthroughSupport = Supported`.
- `OVRPassthroughLayer`: `Reconstructed`, `Underlay`. Rig cameras: Solid Color, background alpha 0.
- No skybox, flat ambient.

## Build and install

1. Setup Scene (menu, or headless):
   `Unity.exe -batchmode -nographics -quit -projectPath <project> -executeMethod BoxTagAppSetup.SetupScene -logFile <log>`
2. Build APK (menu, or headless):
   `Unity.exe -batchmode -nographics -quit -projectPath <project> -executeMethod BoxTagAppBuild.BuildApkHeadless -logFile <log>`
   Output: `Builds/BoxTagApp_L3.apk`. Success line: `[BoxTagAppBuild] SUCCESS`.
   Close the Unity editor first if you run batch builds on the same project folder.
3. Install and launch from a cmd window with the Unity SDK adb:
   `"D:\UnityEditior\6000.0.66f2\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe" install -r Builds\BoxTagApp_L3.apk`
   `"D:\UnityEditior\6000.0.66f2\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe" shell am start -n com.asas.boxtagapp/com.unity3d.player.UnityPlayerGameActivity`

## Headset test (L3)

1. Set the Remote Url in the asset (or leave the placeholder to test the bundled/cached data).
2. Print a tag36h11 tag with ID 1 or 2 (10 cm outer black square). Hold it 40–80 cm away.
3. Press **A**. Expected: `ID 1 -> Box no 1` in the text under the captured frame.
4. Check the status line (`DB: …`). Check the log for `[BoxTag] text '…' rendered size`.
5. Add an entry to the Gist, press left **X**, and confirm `DB reloaded: N tags`.

## Planned: L4
Labels above the tags, placed in world space with the stored capture pose and smoothing.
