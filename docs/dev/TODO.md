# Development checklist

The goal is a standalone SteamVR recorder and editor. The [current action](STATUS.md) is selected from this checklist. Early take formats may change as the project develops.

## SteamVR core and optional integrations

Make SteamVR the only required external runtime. The core owns device capture, avatar selection, tracker assignment, calibration, recording, review and generic export. Optional modules provide workflows for VRChat, HUMR, Blender, Unity, Basis, Resonite and Chillout.

- [ ] Audit dependencies on VRChat, HUMR, Blender and Unity across startup, settings, capture, calibration, avatar loading, solving and export. Identify the platform-specific behaviour to move into optional modules.
The SteamVR-only workflow and integration lifecycle are described in the [tab UI design](tab-ui-design.md#work-across-tabs).

- [ ] Check the available integration mechanisms for Basis, Resonite and Chillout before choosing an implementation.
- [ ] Verify the core workflow with every module disabled and the integration applications absent. Check that each enabled module reports its own availability and that core recording continues when a module is unavailable, disconnects or fails.
- [ ] Verify setup, source indicators and documentation show integration availability and enablement separately from SteamVR capture readiness.

## Privacy improvements

The take data and local associations are described in the [recording privacy design](tab-ui-design.md#work-across-tabs).

- [ ] Audit take headers, embedded profiles, stream metadata, events and calibration snapshots for copies of identifying information, including data supplied by optional integration modules.
- [ ] Verify that newly recorded and saved takes contain none of the excluded information, while local avatar and device associations remain available through AppData.

## Standalone capture

The open avatar-selection and calibration work is described in the [Record design](tab-ui-design.md#record).

- [ ] Add a button to recenter calibration before recording.
- [ ] Let users name a take before recording, with optional placeholders for the date and avatar name.
- [ ] Check VRChat settings reads in the recorder, calibration watcher and tracking rig when integrations are disabled.
- [ ] Verify SteamVR connection, device roles, play area, base stations, controller/finger input and reconnection after a restart.
- [ ] With VRChat closed, record a performance including a tracker dropout, reopen it and export it. Measure preview CPU cost and check for lost samples.

## Scene editing

The [Scene workspace design](tab-ui-design.md#scene-workspace) tracks the desktop features identified in the WinForms parity audit and links to the existing engine code.

## Export and import

- [ ] Add an export folder setting to the project settings.
- [ ] Verify imports from the current build in Blender and Unity against the preview, including scene exports from the engine. Earlier measurements are in the [export guide](../export-and-pipeline.md).
- [ ] Verify the experimental app-written Humanoid clip on the recorded avatar and a second avatar against Generic FBX, Humanoid FBX and Unity's own clip. [Export design](tab-ui-design.md#export)
- [ ] Measure compound-pose body rotation error in the Humanoid converter. Earlier experiments found 0.014° mean error at half muscle range and 0.12° at full range. Unity muscle space cannot express every solved pose, so use Generic playback as the exact-pose reference.

## Desktop verification and rendering

- [ ] Add folders to the recording library so users can organise takes.
- [ ] Exercise library pickers/deletion, export folder/options controls, avatar import/link/export and calibration presets/library actions in a real window.
- [ ] Check second-instance activation and following the system theme.
- [ ] Check placement-gizmo dragging with the mouse in a real window.
- [ ] Add accessible names to remaining drop-downs and text boxes.
- [ ] Investigate avatar-mesh memory use, previously measured at about 700 MB compared with 230 MB for the old skeleton view.
- [ ] Investigate intermittent whole-window repainting while idle, previously seen in one of five runs.
- [ ] Offer multisampling in view settings.
- [ ] Implement the remaining scene/pose gizmos alongside the Scene workspace.

## Documentation

### Export documentation

Update these in order when the app-written Humanoid clip ships. The [Export design](tab-ui-design.md#export) describes the planned workflow.

- [ ] Update the export guide’s opening note, file table and App → Unity steps. Identify who writes the Humanoid clip and whether `_body.anim`, `_face.anim` and `.vrmr.json` are still written or optional. Measure the generated clip’s accuracy, key reduction and size before replacing the older Unity-package figures (0.9 mm mean and 35 MB for 39 s). [Export guide](../export-and-pipeline.md)
- [ ] Document the new clip’s manifest keys and the sidecar’s remaining role. [File formats](file-formats.md#export-manifests-and-sidecars)
- [ ] Update the Unity bridge import behaviour and menu reference, add a changelog entry and bump its package version together with the Blender add-on’s. Update the Blender and integration guides if **Send to Unity** changes. [Unity bridge](../../integrations/unity/com.vrmr.motion-bridge/README.md) · [Blender add-on](../../integrations/blender/README.md) · [Integrations](../../integrations/README.md)
- [ ] Update the README export feature summary and the usage guide’s export steps. [README](../../README.md#features) · [Usage](../usage.md#record-review-and-export)

### Desktop documentation

Check labels, defaults and shortcuts against the final UI. Add locations when they are stable. Scene-specific documentation work is in the [Scene design](tab-ui-design.md#desktop-integration).

- [ ] Add a main-window screenshot to the README using non-personal content. Check first-run steps, the release ZIP name, the Windows warning if releases are signed, and the recording, trimming and export quick start. [README](../../README.md)
- [ ] Check recording controls and defaults, live view toggles and the device list, **Include VRChat jumps**, tracking-loss actions and where avatar resize ranges appear. [Usage](../usage.md)
- [ ] Check calibration inspector and defaults paths, **Capture now**, **Save to library**, **Delete**, watching and automatic linking switches, setting labels, timeline events and modes, list match marks and warnings, and the solve result summary. [Calibration guide](../calibration.md)
- [ ] Verify package and BlendShare imports, **Add avatar…**, library and default selection, the worn avatar’s base-model picker, face-shape FBX export, mesh hiding and the shipped mannequin description. [Avatar guide](../avatars.md)
- [ ] Check export option labels, **Origin**, **Turn**, **Reset**, the placement gizmo and face replay. [Export guide](../export-and-pipeline.md)
- [ ] Check source settings, the height setting and **Hang the avatar from the headset at calibration**. Add troubleshooting entries for issues surfaced by first-run setup. [Troubleshooting](../troubleshooting.md)
- [ ] Check how to switch body sources and follow the in-world HUMR recording button. If the measurement harness is added to the repo, replace its availability note with run instructions. [Accuracy guide](accuracy.md#reproduce-it-on-your-own-takes)
- [ ] Keep the live connection guidance aligned with the OSC and OSCQuery implementations when source or port behaviour changes. [Security](../../SECURITY.md#live-connections)

## Optional VRChat hardware verification

- [ ] Verify the record hotkey while VRChat has focus and the record parameter from a real avatar menu.
- [ ] Verify OSCQuery discovery in both directions, avatar/parameter reads and the VRChat pill's no-OSC state.
- [ ] Check the live view with actual devices, base stations and HUMR avatar motion, then verify synchronization of HUMR and SteamVR recordings.
- [ ] Measure recording alongside VRChat and SteamVR with the skeleton preview at 15 fps, including CPU cost and sample loss.
- [ ] Verify automatic VRChat calibration capture and manual Capture now with real trackers.
- [ ] Investigate intermittent failures with **Sync with VRChat** calibration. Test pressing both controller triggers together and with a delay between presses.
- [ ] Verify face replay arriving in VRChat.

## Capture and motion quality

- [ ] Develop controller-specific hand and head offset profiles as measurements become available for more hardware.
- [ ] Use optional HUMR recordings to measure per-bone error and evaluate changes to the arm, spine and knee solve. Measure planted-foot travel in HUMR, the solve, FBX and Unity Humanoid playback with foot IK on and off, on both the recorded and a second avatar, to locate the source of sliding.

Reference-video playback and sync are described in the [View design](tab-ui-design.md#view).

## Virtual camera crane

The authored camera workflow is described in the [Scene workspace design](tab-ui-design.md#scene-workspace).

## Code cleanup

- [ ] Share the identical `Num`, `Check` and `Choice` modifier factories between pose and device cleanup. [Pose cleanup](../../src/Vrmr.Scene/Cleanup.cs) · [Device cleanup](../../src/Vrmr.Scene/CleanupDevice.cs)
- [ ] Remove the private `FbxExporter.NonZero` helper. Its declaration is its only occurrence in tracked source. [FBX avatar exporter](../../src/Vrmr.Export/FbxExporter.Avatar.cs)
- [ ] Review the duplicated add-on loading logic in the direct-FBX `OpenScript` and manifest-based `OpenSceneScript`. The audit found no in-repository callers for either `OpenScript` or `OpenExport`, which uses `OpenSceneScript`. The manifest launcher corresponds to the planned **Export + open in Blender** action. [Blender launcher](../../src/Vrmr.Export/BlenderLocator.cs) · [Export design](tab-ui-design.md#export)
- [ ] Verify whether the public `ExportCsv` method has external callers or a planned use before deciding whether to keep it. The audit found no in-repository caller or desktop export catalog entry. [CSV exporter](../../src/Vrmr.Export/MiscExporters.cs) · [Export catalog](../../src/Vrmr.App/Services/ExportCatalog.cs)

## Before a public release

- [ ] Verify CI runs, including the Linux build and tests. Exercise the tagged release workflow with a throwaway tag in a fork or private copy before the first public release. [CI](../../.github/workflows/ci.yml) · [Release workflow](../../.github/workflows/release.yml)
- [ ] Recheck third-party notices when dependency versions change. Ensure the release ZIP includes the Inter SIL Open Font License 1.1 text at `licenses/Inter/OFL.txt`. [Third-party notices](../../THIRD_PARTY_NOTICES.md)
- [ ] Decide whether the Unity and Blender integrations, currently both at `1.4.0`, should follow the app’s version from 1.0 onward.
- [ ] Verify that GitHub Discussions is available, or remove its link from the issue template chooser. [Issue template configuration](../../.github/ISSUE_TEMPLATE/config.yml)
- [ ] Review the provenance and structure of the VRChat-matched arm, spine and knee solver code before making the repository public.

## Open question for later: collision and physics

- [ ] Revisit physics if editing real takes reveals a need. The [pose rig](../../src/Vrmr.Scene/ScenePose.cs) already estimates centre of mass using human segment masses. Possible experiments include generating collision shapes for intersection cleanup or adding rigid bodies with soft joint limits for posing selected limbs and the spine. [Ragdoll reference](https://youtu.be/yY6vck8SuCY)
