# Development checklist

The goal is a standalone SteamVR recorder and editor. The [current action](STATUS.md) is selected from this checklist. Early take formats may change as the project develops.

## SteamVR core and optional integrations

- [ ] Audit dependencies on VRChat, HUMR, Blender and Unity across startup, settings, capture, calibration, avatar loading, solving and export. Identify the platform-specific behaviour to move into optional modules.
- [ ] Make SteamVR the only required external runtime for live recording. Keep local avatar selection, tracker roles, calibration, raw take storage, review and generic exports available with integrations disabled.
- [ ] Define module enablement, settings and lifecycle. Start only enabled modules, report unavailable applications or failed connections per module, and keep core recording operational when a module fails.
- [ ] Move VRChat-specific OSC/OSCQuery discovery, logs, avatar identity, settings and calibration events, face replay and video-player events behind the VRChat module.
- [ ] Make HUMR an optional motion source with explicit timing and coordinate mapping. Enable its VRChat log input only when that integration is selected.
- [ ] Keep Blender import/export handoff and Unity project discovery, avatar metadata and clip/Timeline handoff in their optional modules. Generic recording and file export must work with neither application installed.
- [ ] Define the supported workflows and data exchange for separate Basis, Resonite and Chillout modules. Check each platform's available integration mechanisms before selecting an implementation.
- [ ] Verify the core with every module disabled and the integration applications absent. Then verify individual modules and failure cases, including unavailable applications, disconnection and recording through a module failure.
- [ ] Update setup, source indicators and documentation to expose integration availability and enablement separately from SteamVR capture readiness.

## Privacy improvements

- [ ] Exclude avatar IDs and names, world names, VRChat display names and device serial numbers from recorded takes.
- [ ] Store this identifying information and its local associations in the application's AppData directory.
- [ ] Use take-local device identifiers for recorded streams, tracker bindings and calibration data. Resolve physical device serial numbers through the AppData mapping.
- [ ] Keep playback, solving and generic export functional when a take is opened without its AppData associations.
- [ ] Audit take headers, embedded profiles, stream metadata, events and calibration snapshots for copies of identifying information, including data supplied by optional integration modules.
- [ ] Verify that newly recorded and saved takes contain none of the excluded information, while local avatar and device associations remain available through AppData.

## Standalone capture

- [ ] Select the mannequin or a local capture avatar and confirm tracker assignments with VRChat closed. The live [device panel](../../src/Vrmr.App/ViewModels/DevicesViewModel.cs) currently requires a VRChat avatar ID to select a base model.
- [ ] Make standalone calibration reproducible. Store scale, bindings and calibration inputs with the take. Check VRChat settings reads in the recorder, calibration watcher and tracking rig when integrations are disabled.
- [ ] Verify SteamVR connection, device roles, play area, base stations, controller/finger input and reconnection after a restart.
- [ ] With VRChat closed, record a performance including a tracker dropout, reopen it and export it. Measure preview CPU cost and check for lost samples.

## Scene editing

A source audit against the WinForms UI immediately before removal commit `a8fd40d` found the following desktop functionality still missing in Avalonia. The [Scene workspace flag](../../src/Vrmr.App/Composition/AppFeatures.cs) is off and its [view](../../src/Vrmr.App/Views/Scene/SceneWorkspaceView.axaml) is a placeholder. The shared scene engine already implements much of the underlying behaviour.

- [ ] Restore scene management and preview. Provide a scene library with new, open, save, rename and delete, unsaved-change handling, scene playback and scrubbing, undo/redo, actor selection and scene frame-range controls.
- [ ] Restore the NLA timeline and inspectors. Support adding takes from the library, managing actors and tracks, selecting and moving strips, trimming, splitting, duplicating and deleting strips, speed and source timing, snapping and markers. Expose track ordering, mute/solo/lock, strip influence and blend ramps, root placement, body masks and Replace/Add blending. The [scene operations](../../src/Vrmr.Scene/SceneOps.cs) already edit strips that reference takes without changing the recordings.
- [ ] Restore transition editing and search. Expose crossfade length and curve, centre-of-mass/hips/feet alignment, facing and travel-direction matching, suggested lengths, Find best cut, Best entry into B and the transition match map. Show pose/velocity differences and output foot-slide and centre-of-mass discontinuity measurements alongside the preview. These calculations remain in the scene engine.
- [ ] Restore keyframe-only animation and corrective posing. Expose bone and device modes, limb and spine IK, hand/foot pins, hips moves, key insertion/removal, channel selection and interpolation. Support device-only takes, additive pose keys, animated tracker offsets and keyed device influence through the existing pose and input-layer engine.
- [ ] Restore scene layer controls and partial-body recording. Let the user choose an actor and body/input mask, arm at the playhead or record immediately, set pre-roll and count-in, measure timing by trigger taps, and manage repeated attempts. Expose per-device weights and input alignment. The [punch-in engine](../../src/Vrmr.Scene/PunchIn.cs) already builds the recorded layers.
- [ ] Restore modifier controls for actors, pose strips and device layers, including enable/reorder/remove, body masks and repair ranges. Expose foot locking, smoothing, tracker gap filling and glitch detection through the existing cleanup implementations.
- [ ] Restore scene export from the desktop. Expose multi-actor scene baking, format options, export to a folder or linked Unity project, progress/results, opening the export folder and Export + open in Blender. The take export panel already works, and the [scene exporter](../../src/Vrmr.Export/SceneExport.cs) remains in the library.

## Export and import

- [ ] Verify imports from the current build in Blender and Unity against the preview, including scene exports from the engine. Earlier measurements are in the [export guide](../export-and-pipeline.md).
- [ ] Verify the experimental app-written Humanoid clip on the recorded avatar and a second avatar against Generic FBX, Humanoid FBX and Unity's own clip. Only then enable it by default for takes and update the Unity integration.
- [ ] Measure compound-pose body rotation error in the Humanoid converter. Earlier experiments found 0.014° mean error at half muscle range and 0.12° at full range. Unity muscle space cannot express every solved pose, so use Generic playback as the exact-pose reference.

## Desktop verification and rendering

- [ ] Exercise library pickers/deletion, export folder/options controls, avatar import/link/export and calibration presets/library actions in a real window.
- [ ] Check second-instance activation and following the system theme.
- [ ] Check placement-gizmo dragging with the mouse in a real window.
- [ ] Add accessible names to remaining drop-downs and text boxes.
- [ ] Investigate avatar-mesh memory use, previously measured at about 700 MB compared with 230 MB for the old skeleton view.
- [ ] Investigate intermittent whole-window repainting while idle, previously seen in one of five runs.
- [ ] Offer multisampling in view settings.
- [ ] Implement the remaining scene/pose gizmos alongside the Scene workspace.

## Optional VRChat hardware verification

- [ ] Verify the record hotkey while VRChat has focus and the record parameter from a real avatar menu.
- [ ] Verify OSCQuery discovery in both directions, avatar/parameter reads and the VRChat pill's no-OSC state.
- [ ] Check the live view with actual SteamVR devices and base stations, then verify synchronization of HUMR and SteamVR recordings.
- [ ] Measure recording alongside VRChat and SteamVR with the Record device preview at 15 fps, including CPU cost and sample loss.
- [ ] Verify automatic VRChat calibration capture and manual Capture now with real trackers.
- [ ] Verify face replay arriving in VRChat.

## Capture and motion quality

- [ ] Develop controller-specific hand and head offset profiles as measurements become available for more hardware.
- [ ] Use optional HUMR recordings to measure per-bone error and evaluate changes to the arm, spine and knee solve. Measure planted-foot travel in HUMR, the solve, FBX and Unity Humanoid playback with foot IK on and off, on both the recorded and a second avatar, to locate the source of sliding.
- [ ] Add optional VRChat video player support. Detect song changes in the log, turn timestamps into clip boundaries within a continuous recording, and play the reference video alongside a take using yt-dlp with an adjustable sync offset.

## Virtual camera crane

- [ ] Build the authored camera workflow. Camera export and import already carry recorded motion. The [scene model](../../src/Vrmr.Scene/SceneModel.cs) reserves space for authored cameras. The proposed workflow is to key a camera path, use an optional in-world station to move the performer along it, capture handheld motion relative to the path, then smooth and steer that motion with corrective keys.

## Before a public release

- [ ] Verify CI and release workflow runs.
- [ ] Review the provenance and structure of the VRChat-matched arm, spine and knee solver code before making the repository public.

## Open question for later: collision and physics

- [ ] Revisit physics if editing real takes reveals a need. The [pose rig](../../src/Vrmr.Scene/ScenePose.cs) already estimates centre of mass using human segment masses. Possible experiments include generating collision shapes for intersection cleanup or adding rigid bodies with soft joint limits for posing selected limbs and the spine. [Ragdoll reference](https://youtu.be/yY6vck8SuCY)

## Previous validation

The following records were carried over from STATUS and describe earlier runs through commit `8edcb8e`, not checks repeated during this documentation update. The interim release was `2c0c8d7`, and `a8fd40d` removed the WinForms app. The parity audit compared source only.

- [x] Build. `dotnet build VRMotionRecorder.slnx -c Release` builds every project on .NET 10.
- [x] Tests. `tests/Vrmr.Tests`: 97 pass. `tests/Vrmr.Render.Tests`: 16 pass. `tests/Vrmr.App.Tests`: 179 pass (7 of them need copies of real takes through `VRMR_PRIVATE_DATA` and are skipped without).
- [x] Desktop app. `src/Vrmr.App` (Avalonia). Run it with `dotnet run --project src/Vrmr.App -c Release`. Take recording, review, editing and export are implemented; the Scene workspace is unavailable.
- [x] Solve. Unchanged by the refactor: bit-identical results before and after on ten takes and a test scene (`vrmr dump` + `tools/compare_dumps.py`). About 1.7 times faster on .NET 10 than on .NET Framework.
- [x] Exports. FBX, Unity `.anim` (transform, face, parameters) and the manifests are identical in content to those of the previous build.
- [x] Historical renderer baseline. The removed desktop UI drew only when something changed, with timer-paced playback at about 35 frames per second. On a 1100 x 640 view it used 1.4 CPU cores for take playback and 0.7 for scene playback after optimisation.
- [x] Engine structure. Libraries with one-way dependencies: Core, Fbik, Model, Avatars, Solve, Capture, Scene, Export. See `docs/architecture.md`.
- [x] Generalisation. One paths service, no fixed install folders, detection of SteamVR, VRChat, Blender and Unity projects, settings with a schema version, avatar name aliases stored with each library entry, a built-in mannequin for users without an avatar. See `docs/dev/platform-api.md`.
- [x] Capture. The VRChat log is followed with one open handle instead of being reopened many times a second; cheaper SteamVR waiting.
- [x] Repository. MIT licence, third-party notices, README and docs, CI and release workflows, issue forms, original icon, the Unity editor scripts as a package, the Blender add-on with an extension manifest.
- [x] Command line. `vrmr info`, `vrmr dump` (regression check), `vrmr humanoid` (experimental).
- [x] Compare GPU silhouettes with the previous renderer on a real take in nine frame/camera pairs. Recorded intersection over union was at least 0.9999, with average colour differences within 3/255. Paused usage was 0.001–0.003 CPU cores, and playback held 60 fps at about 0.1 cores in the demo and 0.26 in the app.
- [x] Exercise exports, settings and first-run setup in the window using a take copy and scratch data folder. All nine export choices wrote files, the body clip matched an engine bake, and changed settings survived restart.
- [x] Exercise live-source wiring with synthetic devices through OpenVRSource.Feed, private-port OSC packets and a scratch VRChat log in LiveSourceTests. These runs used neither SteamVR nor VRChat.
- [x] Implement the GPU renderer, render demo benchmark and offscreen screenshots through ANGLE.
- [x] Implement the experimental Humanoid converter, key reducer and writer. Earlier Unity experiments measured the muscle-to-rotation model within 0.00007° over 871 poses on 24 skeletons, including rest axes, twist sharing, optional bones, human scale, mass weights and IK goal curves. Playback verification of the generated clip remains open above. ClipExportOptions.HumanoidAnim defaults to false, and the existing Unity package obtains Humanoid clips from imported FBX.
