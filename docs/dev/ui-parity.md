# Feature parity with the previous UI

Every feature of the previous (WinForms) UI's Takes mode, settings and export, where it lives in the Avalonia app,
and its state. The Scene (NLA) workspace has its own table once it is ported.

The previous UI has been removed. This checklist remains a historical feature reference and a backlog for the
Avalonia app; unfinished features have no fallback desktop implementation.

**States.** *Done*: built and exercised on a copy of a real take (or, where no take is involved, in the running
window). *Built*: in the app and covered by tests on a made-up take, not yet exercised on a real one. *Simulated*:
built and exercised with generated data through the recorder's real sources (SteamVR devices fed with
`OpenVRSource.Feed`, VRChat's OSC as UDP packets on a private port, VRChat's log as a file in a sandbox folder:
`tests/Vrmr.App.Tests/LiveSourceTests.cs`), but never with SteamVR or VRChat themselves, which test runs must not
start. `docs/STATUS.md` lists what to check on real hardware. *Waiting*:
depends on something that is not in the tree yet. *Open*: not built.

Renamed or moved things are marked **→**.

## Window and shell

| Previous UI | Now | State |
|---|---|---|
| Record button with the hotkey shown under it | Top bar, left: Record / "Stop 00:12"; the hotkey is in the tooltip | Simulated |
| Takes / Scene switch | Hidden until the scene editor is ported (`AppFeatures.SceneWorkspace` in `Composition/AppFeatures.cs`, one flag); the app stays in the Takes workspace | Off |
| Status chips: VRChat, Face, SteamVR, Body, Avatar | **→** Source pills SteamVR, VRChat, Face, Avatar; each opens a popover with the details and the fix. "Body: HUMR live" moved into the VRChat pill's details | Built; live states need devices |
| Tabs Clip / Export / Live / Settings in one strip | **→** Inspector tabs Take / Export / Devices; Settings is a page of its own (gear, Ctrl+,) | Done |
| Fixed panels | Library and inspector fold (Ctrl+1, Ctrl+2), timeline folds (Ctrl+3); all three resize and remember their size. Under 1280 px the inspector lies over the view | Done |
| Window position and size remembered (settings.json) | Remembered in `ui-state.json`; restored only onto a screen that still exists | Done |
| One instance; a second start shows a message box | A second start brings the running window to the front | Built |
| Errors as message boxes | Status bar line and toasts; errors say what to do next. The only dialogs: file pickers and "move to the Recycle Bin?" | Done |
| Tooltips | Every control; icon-only buttons take their accessible name from the tooltip | Done |
| – | Help sheet with the whole keyboard and mouse map (F1, ?) | Done |
| – | Light theme and "follow system" (Settings → View) | Done (dark and light in the window; "follow system" Built) |
| – | The take that was open is opened again at the next start | Done |

## Library

| Previous UI | Now | State |
|---|---|---|
| Recordings list, newest first: name, length, avatar · world | Same, in the Library panel | Done |
| Badges FACE, HANDS, HUMR, NO VR | Badges FACE, HANDS, **→** "VRC IK" (was HUMR), NO VR, each with a tooltip | Done |
| Tooltip with date, avatar, world, data | Same | Done |
| Open take dot for unsaved edits | Same ("name •") | Done |
| Folder / Open… / Delete buttons | Icon buttons: open folder, open a file (Ctrl+O), show in folder (new), delete; also a right-click menu | Built (delete and the pickers need a real window) |
| Delete asks, then Recycle Bin | Same; the result is a status line | Built |
| – | Search over name, avatar, world, file name (Ctrl+F); "no match" and "no takes yet" states with a button | Done |
| List refreshed after save / record | The folder is watched: takes copied in or recorded appear by themselves | Built |
| Rename: the name box on the Clip tab | Name box on the Take tab; saved with Ctrl+S, shown in the library at once | Done |

## Transport and playback

| Previous UI | Now | State |
|---|---|---|
| ⏮ ▶ ⏭, Space, Home / End | Same, plus single-frame buttons (the ← → keys had no buttons) | Done |
| Loop, speed 0.25×–2× | Same | Done |
| In, Out (I, O), Cut (Del), Keep, Reset | Same, as icon buttons with tooltips; Cut and Keep are disabled without a selection | Done |
| Undo / redo (Ctrl+Z, Ctrl+Shift+Z, Ctrl+Y); a slider drag is one step | Same; a drag on the timeline, the gizmo or a number field is exactly one step, however long it takes | Done |
| Origin, ↺, ↻ | Same | Done |
| Calibrate (at the playhead) | Same | Done |
| Live toggle; playing, scrubbing or picking a take switches it off; on with no take | Same | Done (the live picture itself needs devices) |
| Time read-out "playhead / length", hidden when narrow; edited length as tooltip | Same; in a narrow window the bar drops the time, then the button labels, then the step buttons | Done |
| Playback on a 16 ms timer (about 37 fps) | Paced by the display's frames, capped at 60; nothing runs while paused | Done |
| Playback skips cuts, loops over the kept parts | Same | Done |

## Timeline

| Previous UI | Now | State |
|---|---|---|
| Ruler, playhead, scrub by dragging on the ruler | Same | Done |
| Trim handles (drag), trimmed parts darkened | Same; the solve waits for the end of the drag | Done |
| Cuts hatched red; right-click restores one | Same | Done |
| Selection by dragging in the lanes (or Shift) | Same | Done |
| Click a shaded span (tracker loss, VRChat calibration) to select it | Same | Done |
| Lanes: VRChat IK, SteamVR, VRC poses, Tracker loss, Face activity | Same ("VRChat poses", "Tracking loss") | Done |
| VRChat not-tracking spans (dashed, labelled) | Same | Done |
| Flags: old origin, calibration, avatar / world change, VRChat calibration, VRChat setting, playspace jump, VRChat jump | Same, and each flag now says what it is in a tooltip | Done |
| Wheel zooms about the pointer, middle / right drag pans | Same | Done |

## 3D view

| Previous UI | Now | State |
|---|---|---|
| The solved avatar at the playhead | The avatar's textured mesh, skinned on the GPU (`Vrmr.Render` through `GpuView`); the built-in mannequin has a mesh of its own. Same silhouette as the previous software renderer on a real take (9 frame and camera pairs) | Done |
| Face shapes on the mesh | Same, applied on the GPU | Done |
| Take's devices at tracking scale, VRChat camera | Same | Done |
| Take's own play area and base stations; else SteamVR's room as last seen when it fits, with a note | Same (`RoomService`, `TakePresenter`) | Built |
| Live: devices with labels, battery, faded when not tracked, play area, base stations with field of view, VRChat head / wrists / camera, VRChat's IK avatar mapped onto the devices | Same (`LivePresenter`) | Simulated |
| Chips Mesh, Skeleton, Devices, Headset, Playspace, Base stations; faded when there is none | Same, centred under the view | Done |
| Orbit, pan, zoom, double-click and F frame all | Same, plus a frame button | Built |
| Skeleton only and a lower frame rate while recording | Same (15 fps, 4 fps with preview off) | Simulated |
| Overlay text: source, frame, room note; take name on the right | Same | Done |
| Empty text "Waiting for data…" | Empty states that say what to do: pick a take, start VRChat / SteamVR, add an avatar; a failed solve says why | Done |
| Placement gizmo (W, E, Shift, Esc) | Same; arrows and rings on the hips | Built (drag by mouse needs a real window) |

## Take inspector (was the Clip tab)

| Previous UI | Now | State |
|---|---|---|
| Name box, info line, solve line | Same, on top | Done |
| Save (Ctrl+S), Revert | Same; Save is highlighted while there are unsaved edits | Done |
| Details: recorded data, solver notes | "Details" (folded), with the number of solver notes | Done |
| Body: Avatar list (Auto + library), note on what it was recorded as | Same | Done |
| Link VRChat avatar; Unity model… | **→** Body → "Avatar links" (folded) | Built |
| Body from: Auto / VRChat IK (HUMR) / IK solve | "Automatic / VRChat's own IK / IK solve" | Done |
| Thumbstick locomotion and turning; VRChat jumps (count) | Same | Done |
| Smoothing slider (all takes) | Number field with reset | Done |
| Tracker roles per tracker, "Auto: role" | Same, with plain role names | Done |
| Tracking loss: list, select on timeline, keep / repair / remove mark, mark selection, clear marks | Same | Done |
| Placement: Move cm, Turn °, Gizmo, Move / Turn, Reset, summary | Same, as number fields you can drag | Done |
| Calibration "From" list: best T-pose, moment, VRChat's calibrations, one VRChat calibration, library (✓ match, warnings), linked copy | Same; warnings in words instead of symbols | Done |
| Note under the list, full story in the tooltip | Same | Done |
| At playhead, Find T-pose | Same | Done |
| Height mode + value; Spine; Measure; Follow VRChat; trackers used | Same; trackers used are chips | Done |
| Advanced: keep feet, ignore head tilt, hang from headset, VRChat hand offsets, shoulders, procedural steps, follow avatar scaling, avatar scale, hand rotation X / Y / Z | Same, folded, with the number of changed options | Done |
| VRChat settings in this take: text, add a change at the playhead, clear | Same | Done |
| Library and presets: make default, use defaults, preset load / save as… / delete | **→** "Defaults and presets"; the preset name is typed in place (no input dialog) | Built |
| Capture now (5 s countdown), Save to library, Delete | **→** "Calibration library" | Capture: Simulated; others Built |
| Details: scale, drift, calibration watch status | "Result" and the watch line in "Calibration library" | Done |
| No take selected: the controls edit the defaults | **→** Settings → Calibration defaults (the same editor) | Done for a toggle (changed, restarted, kept) |
| Fingers: Auto / SteamVR / gestures / none | Same | Done |
| Face sync offset slider | "Face timing" number field in seconds, with reset | Done |
| Replay face into VRChat | Same; disabled without live sources | Simulated |
| Notes | Same, folded, first line as summary | Done |

## Devices (was the Live tab)

| Previous UI | Now | State |
|---|---|---|
| Avatar in VRChat, base model picker (links the avatar id) | Same | Simulated |
| SteamVR device list: role, model, serial, battery, charging, tracking state, base station height, play area | Same, as cards | Simulated |
| Face tracking bars | Same | Simulated |
| Connections text (OSCQuery, ports, avatar, parameters, SteamVR, log) | Same, folded | Simulated |

## Export

| Previous UI | Now | State |
|---|---|---|
| Folder: type, Browse, Unity project, Open; a folder per take | Export tab → Folder | Done for the default folder and a folder per take; typing, Browse and Unity project: Built |
| Formats: FBX with model (+ face shapes), FBX armature, camera / trackers in the FBX, Unity .anim; more: parameters .anim, OSC_Recorder .json, HUMR log, face .json | Export tab → Formats, generated from a catalogue so formats can change without touching the view | Done: all nine choices written from a take copy through the window (11 files plus textures); the body clip equals an engine bake |
| Options: frame rate, face shape names, face mesh, split at avatar changes | Export tab → Options | Built (the defaults were used) |
| Export waits for the solve of the last edit | `TakeSession.WhenSolved()` | Built |
| Progress and result under the button | Same, plus the status bar | Done |

## Settings

| Previous UI | Now | State |
|---|---|---|
| Recording: folder, hotkey, avatar toggle, follow HUMR, preview while recording, save every VRChat calibration, new takes use the latest calibration | Settings → Recording and Folders | Done for the check boxes (changed, restarted, kept); hotkey and parameter fields: Built and Simulated |
| Sources: SteamVR devices, finger tracking, UDP 9001, Open log | Settings → Sources; the log is under About | Done (changed, restarted, kept) |
| Avatar library: list, Add…, Remove, Default, VRChat names, details, open folder, reload | Settings → Avatars | Built |
| Avatar preview: hidden meshes; Export FBX with face shapes… | Settings → View (hidden meshes), Avatars (FBX export) | Done for hidden meshes (seen in the 3D view, kept over a restart); FBX export: Built |
| Unity projects: add, remove, rescan, link current avatar to FBX… | Settings → Folders / Avatars | Built |
| – | First-run guide: what was found, where recordings go, the avatar | Done on an empty data folder (three steps, Finish, stays closed); Add avatar: Built |

## Keyboard

The previous map is kept: Space, ← →, Home, End, I, O, Del, Ctrl+S, Ctrl+Z, Ctrl+Shift+Z, Ctrl+Y, F, and W / E / G /
R / Esc for the placement gizmo. New: Ctrl+F, Ctrl+O, Ctrl+1 / 2 / 3, Ctrl+, and F1. Shortcuts never fire while a
text box has the focus; Space and the arrows stay with a focused list or button.
