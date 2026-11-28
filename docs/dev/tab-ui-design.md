# Tab UI design

The `tab-ui-design` branch splits the Takes workflow into **Record**, **View**, **Edit**, and **Export**. Switching tabs keeps the same take open. **Scene** remains a separate workspace behind a disabled feature flag. This page describes the branch direction and the major work still open. Smaller additions, fixes, and verification tasks are in the [development checklist](TODO.md).

- `d95baff` brings avatar linking, source diagnostics, and live UDP fallback controls into the Devices panel.
- `87d2289` adds the four tabs, a devices-only live preview in Record, and a saved calibration list. `e5a584a` updates the guides for that layout.
- `556500a` and `main`’s `1f7786b` both implement adjustable head and hand offsets with different calibration behaviour. Hardware-specific default profiles are still open. [Capture and motion quality](TODO.md#capture-and-motion-quality)

## Record

Record shows live SteamVR devices, calibration controls, and source diagnostics. If VRChat has supplied no avatar ID, the base-model picker shows an OSC warning and discards the selection. Let users choose the mannequin or a local avatar, confirm tracker assignments, and calibrate with VRChat closed. Save the scale, bindings, and other calibration inputs needed to reproduce the solve when the take is reopened. [Devices view model](../../src/Vrmr.App/ViewModels/DevicesViewModel.cs) · [Standalone capture checks](TODO.md#standalone-capture)

## View

View shows the solved take, its timeline, and controls for the take and its calibration source. Add optional reference-video playback beside the take using `yt-dlp`, with an adjustable sync offset. Use VRChat video-player log events to detect song changes and mark clip boundaries within a continuous recording. [Capture and motion quality](TODO.md#capture-and-motion-quality)

## Edit

Edit shows the open take and its timeline. Trim and cut are already available. The branch has no dedicated Edit inspector, and the take controls still live in View. Define the role and layout of the Edit inspector as the take-editing workflow develops. Scene strip editing and corrective posing are tracked under Scene workspace. [UI style](ui-style.md)

## Export

The branch gives take export its own panel. The app-written Unity Humanoid clip exporter is experimental and defaults off. Validate it on the recorded avatar and a second avatar against Generic FBX, Humanoid FBX, and Unity’s own clip before enabling it by default and updating the Unity integration. The Blender add-on can import exported manifests. The desktop handoff still needs to connect the manifest launcher to **Export + open in Blender**. [Export and import checks](TODO.md#export-and-import) · [Code cleanup](TODO.md#code-cleanup)

The planned default is one app-written Unity Humanoid `.anim` per take, with body and face in the same clip. The Unity bridge currently writes `<name>_humanoid.anim` from the imported FBX. Keep that workflow documented until the app-written clip is verified and shipped. [Current export workflow](../export-and-pipeline.md) · [Documentation checklist](TODO.md#export-documentation)

## Scene workspace

The disabled feature flag hides the workspace switch. With the flag enabled, the shell exposes a placeholder view and empty transport, timeline, and inspector hosts. [Feature flag](../../src/Vrmr.App/Composition/AppFeatures.cs) · [Placeholder view](../../src/Vrmr.App/Views/Scene/SceneWorkspaceView.axaml)

The desktop backlog comes from the WinForms parity audit before removal commit `a8fd40d`. The scene engine already supports non-linear editing, posing, input layers and export. Each scene references recorded takes without changing them. It solves each take in the background on the actor’s avatar using the take’s calibration settings. A strip playing a whole take gives the same solve when the avatar and settings match. [Solver architecture](architecture.md#the-body-solve)

### Scene management and playback

Scenes are JSON files stored by default in the recordings folder’s `Scenes` subfolder. Take references store their paths and creation times. Provide a scene library with create, open, save, rename and delete. Handle unsaved changes and relink moved takes using their creation times. Expose playback, scrubbing, actor selection, frame-range controls and one undo step per edit. [Scene format](file-formats.md#scene-vrmscene) · [Scene model](../../src/Vrmr.Scene/SceneModel.cs)

### Tracks and strips

Add takes from the library and manage actors, tracks and strips. Each strip plays a range of one take. Drag a strip to move it or change tracks. Snap to edges, the playhead, markers and frames, with Ctrl to move freely. Drag an edge to trim and Alt-drag to slip the source range. Expose split, duplicate, delete, speed and source timing.

Tracks evaluate from bottom to top. A strip replaces or adds to the result below it according to its influence, blend ramps and body mask. Masks cover the whole body or parts such as upper body, limbs, fingers and face. Expose track order, mute, solo, lock and root placement in the timeline and inspectors. [Scene operations](../../src/Vrmr.Scene/SceneOps.cs)

### Transitions

Crossfades span the cut and use frames from both strips. Align the incoming strip by centre of mass, hips or feet, preserving height, and optionally match facing or travel direction. Expose crossfade length and curve, with handles for moving the cut and changing the length.

The 0–100 match score uses joint distances and velocities, with more weight on hips and feet. Show the suggested length, score map, pose and velocity differences, foot slide and centre-of-mass discontinuity. **Find best cut** searches 2 s of the first take and 3 s of the second and applies the best match as one undo step. **Best entry** searches the whole second take. Both should expose the score map for selecting a candidate. [Transition engine](../../src/Vrmr.Scene/Transitions.cs) · [Scene measurements](accuracy.md#scene-measurements)

### Keyframes and corrective posing

In bone mode, edit the pose with limb and spine IK, hand and foot pins, hips moves and bone rotation. Support corrective and additive pose keys, keyframe-only animation, channel selection and interpolation. Expose key insertion, deletion and retiming. Drag key diamonds to retime them, with Alt to move the selected key and later keys together.

In device mode, animate tracker offsets and influence before the solve. The move and rotate gizmo edits the device keys for headsets, controllers and trackers. A weight or ramp can disable a device over time. A device-only take starts in a standing pose and can be animated entirely with keys. A device needs an existing calibration binding before it can be keyed into a recorded take. During a drag, solve around the playhead for feedback. [Keyframe operations](../../src/Vrmr.Scene/KeyframeOps.cs) · [Pose IK](../../src/Vrmr.Scene/PoseIK.cs) · [Scene measurements](accuracy.md#scene-measurements)

### Punch-in recording

Choose the actor and body or input mask, including tracked limbs, face, fingers or selected devices. Arm at the playhead or record immediately. During an armed punch-in, start from pre-roll with count-in beeps, allow the recording hotkey in VR, and land the take at the matching scene time on a new input track. Match headset placement and replace only the chosen inputs. Expose timing measurement by trigger taps, per-device weights, input alignment and repeated attempts. [Punch-in engine](../../src/Vrmr.Scene/PunchIn.cs)

### Cleanup modifiers

Expose enabling, reordering and removing modifiers on actors, pose strips and device layers. Include body masks and repair ranges. Foot lock finds contacts, pins the foot and solves the leg. Repair interpolates or holds marked ranges. Smoothing and gap filling can operate on individual trackers before the solve. Expose glitch detection in the same workflow. [Pose cleanup](../../src/Vrmr.Scene/Cleanup.cs) · [Device cleanup](../../src/Vrmr.Scene/CleanupDevice.cs) · [Foot-lock measurements](accuracy.md#scene-measurements)

### Scene export and cameras

The engine exports one set of files per actor and a shared manifest with the frame rate, range, markers, tracks and cameras. Blender imports the actors into collections and shows strips as muted NLA tracks for reference. The Unity bridge imports the manifest into a Timeline. Connect the exporter to the desktop for baking, format selection, folder and Unity project destinations, progress, results and opening the files in the target application. [Scene exporter](../../src/Vrmr.Export/SceneExport.cs) · [Round-trip measurements](../export-and-pipeline.md#scenes)

The scene model preserves authored camera data, and the exporter defines a camera type. Recorded camera motion already has an import and export path. Add editing and evaluation for keyed camera paths, with optional in-world station movement, handheld capture relative to the path, and corrective keys for steering and smoothing. [Scene model](../../src/Vrmr.Scene/SceneModel.cs)

Collision and physics remain exploratory. The pose rig already estimates centre of mass. Revisit collision shapes and joint limits if real takes show a need for them. [Collision and physics backlog](TODO.md#open-question-for-later-collision-and-physics)

### Desktop integration

- Port the editor into `Views/Scene`, `ViewModels/Scene`, `Controls/Nla` and `Services/Scene`. Enable `AppFeatures.SceneWorkspace` locally to test the workspace switch. With the flag off, `ShellViewModel.SceneAvailable` hides the switch and `WorkspaceIndex` stays at 0.
- Use `ShellViewModel.WorkspaceIndex`, `Workspace` and `WorkspaceChanged` to coordinate switching. `TakesWorkspaceViewModel.SetActive(false)` releases the viewport tool, animation and playback. `MainWindow` provides the viewport and the `SceneWorkspaceView`, `SceneTransportHost`, `SceneTimelineHost` and `SceneInspectorHost` slots. The timeline host occupies the resizable bottom area. [Main window](../../src/Vrmr.App/Views/MainWindow.axaml)
- Reuse the take library, settings, UI state, status, avatars, recorder, live sources, recording controller and room services from `AppHost`. Share `EditHistory`, `PlaybackClock`, `ThemeBrushes` and the reusable controls. [App host](../../src/Vrmr.App/Composition/AppHost.cs)
- Use `LibraryViewModel.Items` as the drag source for adding takes to the Scene timeline. Set `NumberField.GestureSource` on the Scene inspector root so each numeric drag forms one undo step.
- Use the recording controller’s `Started` and `Stopped` events for punch-in recording. Takes opens finished recordings from `Stopped` while it is active. Route completed punch-ins to the active Scene workspace.

After the Scene controls settle, document pose modes, punch-in and re-record steps, best cut and best entry commands, modifiers and shortcuts in the [usage guide](../usage.md). Check shortcut descriptions against the implemented bindings.

### Planned Scene shortcuts

- **Scene timeline.** Space plays or pauses. I and O set the scene range. K cuts at the playhead, Del removes a strip, T creates a transition, M mutes, Shift+D duplicates, F frames all, and Shift+M adds a marker. Ctrl+Z undoes. Ctrl+Shift+Z and Ctrl+Y redo.
- **Pose tool.** I keys the selection, Shift+I keys everything, and Alt+I deletes the key. M mirrors the pose. Ctrl+C and Ctrl+V copy and paste the pose. Comma and period jump to the previous and next keys. W moves and E rotates. H toggles a device.

## Work across tabs

- **Optional integrations.** Make SteamVR the only required live runtime. Give VRChat, HUMR, Blender, and Unity their own enablement, lifecycle, settings, and availability. Keep OSC and OSCQuery, logs, avatar identity, settings and calibration events, face replay, and video events in the VRChat integration. Give HUMR explicit timing and coordinate mapping, with VRChat log input enabled only when selected. Keep Blender handoff and Unity project discovery, avatar metadata, and clip and Timeline handoff in their respective modules. Define the supported workflows and data exchange for Basis, Resonite, and Chillout. Setup and status indicators should show SteamVR capture readiness separately from integration state. Recording, review, and generic export should continue when integrations are absent, disabled, or fail. [Integration checklist](TODO.md#steamvr-core-and-optional-integrations)
- **Recording privacy.** Exclude avatar IDs and names, world names, VRChat display names, and device serial numbers from saved takes. Keep those associations in local AppData. Use take-local device identifiers for streams, bindings, and calibration, and resolve physical serial numbers through the local mapping. Playback, solving, and generic export must work when those local associations are unavailable. [Privacy checks](TODO.md#privacy-improvements)

The checklist tracks accessibility, view settings, code cleanup, and runtime checks on existing behaviour. [Development checklist](TODO.md)
