# Architecture

VR Motion Recorder records what VRChat and SteamVR report while you play, solves a body from it on your avatar's own
skeleton, and exports the result. This page describes how the code is organised and the rules that keep it fast.

## Projects

Dependencies point downwards only. Nothing depends on the app.

| Project | What it holds | Depends on |
|---|---|---|
| `Vrmr.Core` | Maths (`Vec3`, `Quat`, `Pose`), JSON, clock, logging, platform paths | – |
| `Vrmr.Fbik` | The full-body IK solver, stand-alone (`VRMR.Fbik`) | – |
| `Vrmr.Model` | Takes (`.vrmr` reading and writing), avatar skeleton (`AvatarProfile`, `HumanBones`), calibration data, app settings | Core |
| `Vrmr.Avatars` | FBX and Unity file readers, BlendShare, the avatar library, skinned mesh data, face shape mapping | Core, Model |
| `Vrmr.Solve` | Tracking rig, calibration, tracker roles and repairs, the IK adapter, baked clips | Core, Model, Avatars, Fbik |
| `Vrmr.Capture` | OpenVR, OSC, OSCQuery, VRChat log, the recorder, the live calibration watcher | Core, Model, Avatars, Solve |
| `Vrmr.Scene` | The scene (NLA) model, evaluation, keyframes, transitions, input layers, cleanup | Core, Model, Avatars, Solve, Fbik |
| `Vrmr.Export` | FBX, Unity `.anim`, manifests for Blender and Unity | Core, Model, Avatars, Solve, Scene |
| `Vrmr.Render` | GPU renderer and the 3D view control | Core, Model, Avatars |
| `Vrmr.App` | The desktop app (Avalonia): views, view-models, services | everything above |
| `Vrmr.Cli` | `vrmr`: command-line access for tests, batch work and benchmarks | everything but Capture, Render, App |
| `tests/Vrmr.Tests` | Unit and synthetic end-to-end tests | the libraries |

The integrations live outside the solution, in their own languages: `integrations/blender` (Python add-on) and
`integrations/unity` (C# editor package).

Namespaces: library types are in `VRMR` (the solver in `VRMR.Fbik`). New projects use `VRMR.Render`, `VRMR.App`,
`VRMR.Cli`, `VRMR.Tests`.

## Data flow

```
SteamVR ──┐                       ┌─ live view
VRChat OSC ├─ Capture ─ take (.vrmr, raw data only) ─ Solve ─ baked clip ─┬─ 3D view
VRChat log ┘                                                              ├─ Scene (NLA) ─ scene bake
                                                                          └─ Export (FBX, .anim, manifests)
```

- A take stores **raw inputs only** (device poses, parameters, events). It is never rewritten by an edit: edits are a
  small list inside the file's metadata, and the solve is repeated from the raw data whenever settings change.
- A solve is a pure function of (take, avatar skeleton, calibration settings). It has no hidden state and gives the
  same result on every machine and runtime, which the regression check relies on.
- Scenes only reference takes. Cutting, layering and keyframing never change a recording.

## Threads

| Thread | Work | Priority |
|---|---|---|
| OpenVR poll | Device poses at 90 Hz, finger input | above normal |
| OSC receive | Parameter and pose messages, parsed without allocations | above normal |
| VRChat log | Follows the log for calibration, avatar and HUMR lines | normal |
| Take writer | Flushes the take to disk every 200 ms while recording | below normal |
| Solve workers | Take and scene solves, cancellable | below normal |
| UI | View-models and input only; never a solve, file read or long lock | normal |

Capture threads write into buffers the recorder owns. The UI reads immutable snapshots.

## Rendering

The 3D view draws on the GPU and **only when something changed**: the playhead, the camera, a selection, a toggle, a
new solve, or a live device that moved. A paused app draws nothing and uses no CPU. While playing or showing live
devices it is paced by the display, capped at 60 frames per second (lower while recording).

The app runs next to VRChat and SteamVR, which need the CPU and the GPU more than a preview does. So: no per-frame
allocations in the draw path, skinning on the GPU, no work on all cores at once, and a frame cap.

## Performance rules

- Measure before and after. `vrmr dump` gives solve times; the app's own frame statistics give draw times.
- No allocations per frame in capture, solve inner loops, or drawing. Reuse buffers.
- Nothing slow on the UI thread. A solve, a file read or an export runs on a worker and reports back.
- Never poll what can be awaited or watched. Timers that only redraw are not allowed.

## Regression check

`vrmr dump <takesDir> <outDir>` solves every take in a folder and writes hashes and raw curves of the results;
`tools/compare_dumps.py <refDir> <candDir>` compares two such dumps. A change that is not meant to alter the motion
must give an identical dump. Run it on copies of your own takes: takes are personal data and are never part of the
repository.

## User data

Nothing personal lives in the repository or next to the program. Settings, the calibration library and logs are in
`%APPDATA%\VRMotionRecorder`; takes, scenes, the avatar library and exports are in `Documents\VR Motion Recorder`
(both can be changed in Settings). Avatar files are the user's own purchases: the app reads them where they are and
never redistributes them.
