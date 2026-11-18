# Project status

## Next actions

The first milestone is to record with SteamVR alone, reopen the take, and import the resulting animation into Blender and Unity. Avalonia already supports recording, take editing and export. Longer-term work is tracked in the [roadmap](TODO.md).

1. **Choose a capture avatar with VRChat closed.** Let the user select the mannequin or a local profile and confirm tracker assignments. The live [device panel](../../src/Vrmr.App/ViewModels/DevicesViewModel.cs) currently requires a VRChat avatar ID to select a base model.
2. **Make standalone calibration reproducible.** Manual pose capture exists. Record the scale, bindings and other calibration inputs needed to reopen a take. Check the current VRChat settings reads in the [recorder](../../src/Vrmr.Capture/Recorder.cs), [calibration watcher](../../src/Vrmr.Capture/CalibrationWatcher.cs) and [tracking rig](../../src/Vrmr.Solve/TrackingRig.cs) with integrations disabled.
3. **Exercise the complete workflow on hardware.** With VRChat closed, record a short performance, include a tracker dropout, then reopen and export it. Check controller and finger input, roles, reconnects and lost samples. Repeat with optional HUMR enabled to verify that both motion sources stay in sync. The detailed hardware checklist below covers the other VRChat controls.
4. **Verify imports from the current build.** Take the same exported performance through Blender and Unity and compare it with the preview. Include a scene export from the engine. Earlier import measurements are documented in the [export guide](../export-and-pipeline.md). For the experimental app-written Humanoid clip, compare the recorded and a second avatar against Generic FBX and Unity's own Humanoid import before enabling it by default for takes.

## Implementation and validation record

Reviewed on 2026-11-18 through commit `8edcb8e`. The interim release (`2c0c8d7`) included the capture and scene engines. Commit `a8fd40d` removed the WinForms desktop app. The following measurements and test counts are from earlier validation runs.

## What works today

| Area                         | State                                                                                                                                                                                                                                                                          |
| ---------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Build                        | `dotnet build VRMotionRecorder.slnx -c Release` builds every project on .NET 10.                                                                                                                                                                                               |
| Tests                        | `tests/Vrmr.Tests`: 97 pass. `tests/Vrmr.Render.Tests`: 16 pass. `tests/Vrmr.App.Tests`: 179 pass (7 of them need copies of real takes through `VRMR_PRIVATE_DATA` and are skipped without).                                                                                   |
| Desktop app                  | `src/Vrmr.App` (Avalonia). Run it with `dotnet run --project src/Vrmr.App -c Release`. Take recording, review, editing and export are implemented; the Scene workspace is unavailable.                                                                                         |
| Solve                        | Unchanged by the refactor: bit-identical results before and after on ten takes and a test scene (`vrmr dump` + `tools/compare_dumps.py`). About 1.7 times faster on .NET 10 than on .NET Framework.                                                                            |
| Exports                      | FBX, Unity `.anim` (transform, face, parameters) and the manifests are identical in content to those of the previous build.                                                                                                                                                    |
| Historical renderer baseline | The removed desktop UI drew only when something changed, with timer-paced playback at about 35 frames per second. On a 1100 x 640 view it used 1.4 CPU cores for take playback and 0.7 for scene playback after optimisation. Avalonia renderer measurements are listed below. |
| Engine structure             | Libraries with one-way dependencies: Core, Fbik, Model, Avatars, Solve, Capture, Scene, Export. See `docs/architecture.md`.                                                                                                                                                    |
| Generalisation               | One paths service, no fixed install folders, detection of SteamVR, VRChat, Blender and Unity projects, settings with a schema version, avatar name aliases stored with each library entry, a built-in mannequin for users without an avatar. See `docs/dev/platform-api.md`.   |
| Capture                      | The VRChat log is followed with one open handle instead of being reopened many times a second; cheaper SteamVR waiting.                                                                                                                                                        |
| Repository                   | MIT licence, third-party notices, README and docs, CI and release workflows, issue forms, original icon, the Unity editor scripts as a package, the Blender add-on with an extension manifest.                                                                                 |
| Command line                 | `vrmr info`, `vrmr dump` (regression check), `vrmr humanoid` (experimental, see below).                                                                                                                                                                                        |

## In progress (in the tree, not finished)

### Desktop app (`src/Vrmr.App`, Avalonia 12)

**What a user can do in it now:** open, play, scrub, trim, cut and undo takes; see the avatar's textured mesh with its face in the GPU 3D view (or the built-in mannequin); move and turn a take; change body and calibration settings and see the re-solve; export a take in every format; change settings; get through the first-run guide; and, with SteamVR and VRChat running, see the live devices, record (button, global hotkey, avatar parameter, in-world recorder), capture a calibration and replay the face. `docs/dev/ui-parity.md` has the state of every feature of the previous UI.

How it was checked:
- **3D view** (`Vrmr.Render` through `Viewport/GpuView`): same silhouette as the previous software renderer on a real take in nine frame and camera pairs (intersection over union 0.9999 or better, colours within 3/255 on average). With a take open: paused draws nothing (0.001 to 0.003 CPU cores); playing holds 60 frames per second at about 0.1 (render demo) to 0.26 (whole app) CPU cores.
- **Export, Settings, first-run guide**: driven in the running window on a copy of a take with a scratch data folder. All nine export choices write their files; the body clip equals an engine bake of the same take. Settings changed in each category were kept over a restart.
- **SteamVR and VRChat** were never running: the recorder's real sources were fed with generated data (devices through `OpenVRSource.Feed`, OSC as UDP packets on a private port, VRChat's log as a file in a sandbox folder), see `tests/Vrmr.App.Tests/LiveSourceTests.cs`.

**Unavailable in the desktop app:** scenes (the Scene workspace is switched off by `AppFeatures.SceneWorkspace` until the scene editor is implemented), the pose tool and the layer recorder. The scene engine remains in the shared libraries.

**Needs checking on real hardware** (could not be done on the development machine):
1. Connecting to SteamVR itself: devices, tracker roles, play area, finger tracking, reconnecting after a restart.
2. The global record hotkey while VRChat has the focus.
3. OSCQuery both ways (VRChat finds the app; the app reads the avatar's name and parameters), and the VRChat pill's "no OSC" state.
4. The live 3D view with real devices, base stations and VRChat's own IK avatar in a world with a HUMR recorder.
5. Recording next to VRChat and SteamVR: the view at 15 frames per second with bones only, its CPU cost, no lost device samples.
6. A VRChat calibration being caught by the calibration watcher; "Capture now" with real trackers.
7. Face replay arriving in VRChat (the tests only use a stand-in sender).
8. The record trigger parameter from a real avatar menu.

Known gaps: memory use with an avatar mesh is about 700 MB (230 MB with the old skeleton view); multisampling is not offered in the settings; dragging the placement gizmo with the mouse was not tried in a real window; many drop-downs and text boxes have no accessible name; once in five window runs the whole window repainted for a while when idle (timeline included, so not the 3D view's pacing; not reproduced, cause unknown).

### GPU renderer (`src/Vrmr.Render`, `tools/Vrmr.RenderDemo`)
- In the solution and in the new app (above). `vrmr-render-demo <folder> --bench` measures the four states of the old view; `--shot` draws frames off screen (OpenGL ES through ANGLE).
- Not done: gizmos other than the placement handle and the pose tool (Scene workspace).

### One Humanoid clip written by the app (`src/Vrmr.Export/Humanoid*.cs`)
Goal: a single Unity Humanoid `.anim` per take (body, IK goals and face in one clip) that needs no Unity to make.
- Established by experiment in Unity (batch mode, scratch project): the muscle-to-rotation model reproduces Unity to 0.00007 degrees over 871 poses on 24 skeletons; how the per-bone axes follow from the T-pose; twist sharing; missing optional bones; the centre-of-mass weights and human scale; the IK goal curves.
- Known gap: the body rotation is exact on single-muscle poses but only approximate on compound poses (0.014 degrees on average at half range, 0.12 at full range).
- A converter, a key reducer and a writer exist (about 1,000 lines) and compile. **They have not been verified in Unity**, so the option is off by default (`ClipExportOptions.HumanoidAnim`). Today's way to get a Humanoid clip is still the Unity package, which copies the clip Unity makes from the exported FBX.

## Known limits of this version

- Avalonia is the only desktop UI. Scene editing, the pose tool and layer recording remain unavailable until they are implemented there; removal of the previous UI does not imply feature parity or hardware validation.
- Unity's Humanoid muscle space cannot represent every pose (for example a twist of the shoulder bone), so a Humanoid clip can differ from the solved pose; the exact pose in Unity is the FBX imported as Generic, or the transform clip on a Generic Animator.
- The release workflow publishes `src/Vrmr.App`, which has no Scene workspace yet.
- CI and release workflow runs still need verification.
