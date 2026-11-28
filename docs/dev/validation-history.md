# Validation history

## Codebase audit

- The Release solution rebuild passed with one existing warning, `AVA2003`, for an unchecked application-lifetime cast in the former RenderDemo project. It emitted no obsolete API warnings.
- All 409 tests passed, with none skipped.
- The Blender and Unity integrations received a source review only. No motion changes were made during the audit, so no dump comparison was needed.

## Earlier project validation

The following records were carried over from STATUS and describe earlier runs through commit `8edcb8e`, not checks repeated during this documentation update. The interim release was `2c0c8d7`, and `a8fd40d` removed the WinForms app. The parity audit compared source only.

- [x] Build. `dotnet build VRMotionRecorder.slnx -c Release` builds every project on .NET 10.
- [x] Tests. `tests/Vrmr.Tests`: 97 pass. `tests/Vrmr.Render.Tests`: 16 pass. `tests/Vrmr.App.Tests`: 179 pass (7 of them need copies of real takes through `VRMR_PRIVATE_DATA` and are skipped without).
- [x] Desktop app. `src/Vrmr.App` (Avalonia). Run it with `dotnet run --project src/Vrmr.App -c Release`. Take recording, review, editing and export are implemented; the Scene workspace is unavailable.
- [x] Solve. Unchanged by the refactor: bit-identical results before and after on ten takes and a test scene (`vrmr dump` + `tools/compare_dumps.py`). About 1.7 times faster on .NET 10 than on .NET Framework.
- [x] Exports. FBX, Unity `.anim` (transform, face, parameters) and the manifests are identical in content to those of the previous build.
- [x] Historical renderer baseline. The removed desktop UI drew only when something changed, with timer-paced playback at about 35 frames per second. On a 1100 x 640 view it used 1.4 CPU cores for take playback and 0.7 for scene playback after optimisation.
- [x] Engine structure. Libraries with one-way dependencies: Core, Fbik, Model, Avatars, Solve, Capture, Scene, Export. [Project map](architecture.md#projects)
- [x] Generalisation. One paths service, no fixed install folders, detection of SteamVR, VRChat, Blender and Unity projects, settings with a schema version, avatar name aliases stored with each library entry, a built-in mannequin for users without an avatar. [Platform APIs](app-platform.md#platform-apis)
- [x] Capture. The VRChat log is followed with one open handle instead of being reopened many times a second; cheaper SteamVR waiting.
- [x] Repository. MIT licence, third-party notices, README and docs, CI and release workflows, issue forms, original icon, the Unity editor scripts as a package, the Blender add-on with an extension manifest.
- [x] Command line. `vrmr info`, `vrmr dump` (regression check), `vrmr humanoid` (experimental).
- [x] Compare GPU silhouettes with the previous renderer on a real take in nine frame/camera pairs. Recorded intersection over union was at least 0.9999, with average colour differences within 3/255. Paused usage was 0.001–0.003 CPU cores, and playback held 60 fps at about 0.1 cores in the demo and 0.26 in the app.
- [x] Exercise exports, settings and first-run setup in the window using a take copy and scratch data folder. All nine export choices wrote files, the body clip matched an engine bake, and changed settings survived restart.
- [x] Exercise live-source wiring with synthetic devices through OpenVRSource.Feed, private-port OSC packets and a scratch VRChat log in LiveSourceTests. These runs used neither SteamVR nor VRChat.
- [x] Implement the GPU renderer, render demo benchmark and offscreen screenshots through ANGLE.
- [x] Implement the experimental Humanoid converter, key reducer and writer. Earlier Unity experiments measured the muscle-to-rotation model within 0.00007° over 871 poses on 24 skeletons, including rest axes, twist sharing, optional bones, human scale, mass weights and IK goal curves. See the [export checklist](TODO.md#export-and-import) for playback verification. ClipExportOptions.HumanoidAnim defaults to false, and the existing Unity package obtains Humanoid clips from imported FBX.
