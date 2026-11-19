# Credits and third-party licenses

## BoxTag App (`Assets/BoxTagApp/`)

Own code: scripts, editor tools, data files, materials, and documentation in `Assets/BoxTagApp/`,
plus the scene `Assets/BoxTagApp/Scene/BoxTagApp.unity`. License: **MIT**, see
[Assets/BoxTagApp/LICENSE](Assets/BoxTagApp/LICENSE).

## AprilTag library (`Assets/AprilTag/`)

- Unity wrapper and runtime, by keijiro (`jp.keijiro.apriltag`), built on the AprilTag C library.
- License: BSD 2-Clause (copyright The Regents of The University of Michigan), see `Assets/AprilTag/LICENSE`.
- Used unchanged. The BoxTag code only references it.

## Meta Passthrough Camera API sample (`Assets/PassthroughCameraApiSamples/`)

- Copyright (c) Meta Platforms, Inc. and affiliates.
- License: MIT, see `Assets/PassthroughCameraApiSamples/LICENSE.txt`.
- BoxTag uses the sample's `PassthroughCameraAccessPrefab` through the Setup Scene tool.

## Meta XR SDK and Mixed Reality Utility Kit (packages)

- Used via `Packages/manifest.json`: Meta XR SDK, MRUK (`PassthroughCameraAccess`, `EnvironmentRaycastManager`),
  and the Meta XR building blocks.
- License: Oculus SDK License Agreement, see `LICENSE.txt` at the repository root.

## Unity packages

- TextMeshPro (TMP Essentials resources in `Assets/TextMesh Pro`), Unity Inference Engine and other
  packages listed in `Packages/manifest.json`. Each keeps its own license terms.

## Note on the repository license

The repository is a mix of licenses. Each part keeps its own:
- BoxTag code: MIT (`Assets/BoxTagApp/LICENSE`).
- AprilTag library: BSD-2-Clause (`Assets/AprilTag/LICENSE`).
- Meta sample: MIT (`Assets/PassthroughCameraApiSamples/LICENSE.txt`).
- Meta XR SDK / MRUK: Oculus SDK License Agreement (`LICENSE.txt` at the root).

The root `LICENSE.txt` is the Oculus SDK License from the Meta sample. It is not an MIT file for the
whole repository.
