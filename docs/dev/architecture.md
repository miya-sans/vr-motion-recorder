# Architecture

VR Motion Recorder records SteamVR device motion and optional VRChat data, solves motion on the avatar’s skeleton, and exports animation. For Avalonia services, runtime discovery and viewport APIs, see the [app and platform guide](app-platform.md). For recording and editing steps, see the [usage guide](../usage.md).

## Projects

Dependencies point downwards only. Nothing depends on the app.

| Project            | What it holds                                                                                                        | Depends on                          |
| ------------------ | -------------------------------------------------------------------------------------------------------------------- | ----------------------------------- |
| `Vrmr.Core`        | Maths (`Vec3`, `Quat`, `Pose`), JSON, clock, logging, platform paths                                                 | –                                   |
| `Vrmr.Fbik`        | The full-body IK solver, stand-alone (`VRMR.Fbik`)                                                                   | –                                   |
| `Vrmr.Model`       | Takes (`.vrmr` reading and writing), avatar skeleton (`AvatarProfile`, `HumanBones`), calibration data, app settings | Core                                |
| `Vrmr.Avatars`     | FBX and Unity file readers, BlendShare, the avatar library, skinned mesh data, face shape mapping                    | Core, Model                         |
| `Vrmr.Solve`       | Tracking rig, calibration, tracker roles and repairs, the IK adapter, baked clips                                    | Core, Model, Avatars, Fbik          |
| `Vrmr.Capture`     | OpenVR, OSC, OSCQuery, VRChat log, the recorder, the live calibration watcher                                        | Core, Model, Avatars, Solve         |
| `Vrmr.Scene`       | The scene (NLA) model, evaluation, keyframes, transitions, input layers, cleanup                                     | Core, Model, Avatars, Solve, Fbik   |
| `Vrmr.Export`      | FBX, Unity `.anim`, manifests for Blender and Unity                                                                  | Core, Model, Avatars, Solve, Scene  |
| `Vrmr.Render`      | GPU renderer and the 3D view control                                                                                 | Core, Model, Avatars                |
| `Vrmr.App`         | The desktop app (Avalonia): views, view-models, services                                                             | everything above                    |
| `Vrmr.Cli`         | `vrmr`: command-line access for tests, batch work and benchmarks                                                     | everything but Capture, Render, App |
| `tests/Vrmr.Tests` | Unit and synthetic end-to-end tests                                                                                  | the libraries                       |

The integrations live outside the solution, in their own languages: `integrations/blender` (Python add-on) and `integrations/unity` (C# editor package).

Namespaces: library types are in `VRMR` (the solver in `VRMR.Fbik`). New projects use `VRMR.Render`, `VRMR.App`, `VRMR.Cli`, `VRMR.Tests`.

## Data flow

```
SteamVR ──┐                       ┌─ live view
VRChat OSC ├─ Capture ─ take (.vrmr, raw data only) ─ Solve ─ baked clip ─┬─ 3D view
VRChat log ┘                                                              ├─ Scene (NLA) ─ scene bake
                                                                          └─ Export (FBX, .anim, manifests)
```

- A take stores **raw inputs only** (device poses, parameters, events). It is never rewritten by an edit: edits are a small list inside the file's metadata, and the solve is repeated from the raw data whenever settings change.
- A solve is a pure function of (take, avatar skeleton, calibration settings). It has no hidden state and gives the same result on every machine and runtime, which the regression check relies on.
- Scenes only reference takes. Cutting, layering and keyframing never change a recording.

## Where the motion comes from

| Source                                                                                                                                    | Works in          | What it gives                                                                                                                                                                                               |
| ----------------------------------------------------------------------------------------------------------------------------------------- | ----------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **VRChat OSC** (found through OSCQuery, or UDP port 9001)                                                                                 | any world         | Every avatar parameter: face tracking (`FT/v2/*`), gestures, locomotion, eye height, avatar scaling. In VR also VRChat's own head and wrist poses (`/tracking/vrsystem/*`) and the pose of VRChat's camera. |
| **SteamVR** (the app connects to a running SteamVR and never starts it)                                                                   | any world         | Headset, controllers and every tracker at full rate, and finger tracking through SteamVR Skeletal Input. Base stations, battery levels and the play area for the live view.                                 |
| **VRChat's log, in worlds with a [HUMR](https://github.com/DrSakuu/HyakuashiUdonMotionRecorder) recorder** (VRChat's logging set to Full) | those worlds only | VRChat's **own** IK result for every humanoid bone, 30 times a second.                                                                                                                                      |

VRChat exposes its body pose to in-world scripts. A take that contains a HUMR reference can use that pose as its body source or compare it with the solve from SteamVR devices. [Measurement method](accuracy.md#what-is-compared)

Everything is read-only. The app joins SteamVR as a background utility, which lets it read controller and finger input without taking it away from VRChat. It reads VRChat's log and VRChat's settings in the registry, and writes neither.

### Face tracking

Face tracking is recorded from VRChat's OSC echo of the `FT/v2/*` avatar parameters that VRCFaceTracking sends: full-precision floats for every face parameter the avatar has, plus the binary sync bits.

- Shapes the avatar does not have are not echoed, so they are not recorded.
- If VRChat treats the avatar as hardware full-face tracking, the face is sent as one binary blob instead of parameters, and no face parameters reach the recorder.
- The face-tracking program sends to one target only (VRChat). The recorder does not divert that stream; it only hears what VRChat echoes.

### Fingers

Fingers come from SteamVR Skeletal Input: real curl and splay on controllers that track fingers, SteamVR's estimate on others. The full 31-bone hand skeleton is stored for each hand. Without it, VRChat's hand gestures are used. The finger source can be changed per take.

## Recording timing

- The take file is written while you record, every 200 ms, so a crash or a power cut keeps everything up to the last fraction of a second.

Every stream shares one high-resolution clock, so face, body and camera stay in step.

## The body solve

The body comes from a full-body IK solver that runs on the avatar's own skeleton. It is fed what VRChat is fed: the head and hand targets, every SteamVR tracker, finger tracking, and eye tracking for the eye bones. The arm, spine and knee behaviour is matched to VRChat's IK 2.0 by measurement on recorded takes; no third-party IK source code is included.

See the [usage guide](../usage.md#adjust-a-recorded-take) for settings you can change after recording.

### Tracker setups

The solver supports up to 11 points. The head and hands can drive a 3-point solve with procedural legs. Hips, chest, elbows, knees and feet can add tracked targets. Elbow and knee trackers steer the bend of their limbs, and a chest tracker adds direction and position goals. [Tracker-subset measurements](accuracy.md#fewer-trackers-on-the-same-take)

### Tracker roles

Which tracker is on which body part is found automatically. Roles you set by hand, and the roles set in SteamVR's "Manage Trackers", win.

1. **With VRChat’s pose in the take.** Match each tracker’s distance curves to the headset and controllers against each bone’s distances to VRChat’s head and hands. [Tracker-role measurements](accuracy.md#other-measurements)
2. **Otherwise from how each tracker moves while you stand.** Compare its height below the eyes with the avatar’s body proportions and use which side of the head it is on. Of two trackers at hip height, the higher one is the chest. An elbow tracker stays at a steady distance from its controller, with a 10th-to-90th-percentile spread under 7 % of eye height.
3. Only when neither is conclusive: the heights at the calibration moment.

None of these depends on the calibration pose or on where the playspace floor is.

## When tracking goes wrong

### A tracker loses tracking

A tracker that is lain on, hidden or out of battery leaves a hole, a frozen pose or a jump in the take. The app finds these per tracker from:

- SteamVR's own tracking state, which is recorded as an event whenever it changes;
- no pose for more than 0.1 s;
- the same pose repeated for 0.08 s or more (SteamVR holding the last pose);
- a move faster than 12 m/s in one sample. A snap of more than 20 cm that does not come back means the pose before it had drifted, so the repair starts 0.3 s earlier.

A playspace reset, where all devices jump together, is not a loss.

The repair carries the lost tracker along with a neighbour on the same body segment (a knee with its foot, a foot with its knee, an elbow with its hand, the hips with the chest and the other way round; otherwise the hips or the headset). Its offset from the neighbour blends from the last good one before the loss to the first good one after it, so the tracker moves with the body and lands exactly on the recovered pose. A tracker that never comes back keeps its last offset to the end of the take, so the rest keeps solving. A tracker that is missing at the calibration moment is not used at all.

See [Adjust a recorded take](../usage.md#adjust-a-recorded-take) for manual repairs and [Other measurements](accuracy.md#other-measurements) for the losses detected on the 11-point take.

### The playspace moves

Tools that move or reset the playspace change the raw SteamVR coordinates, and VRChat follows them.

- **A slow drag** needs nothing. The solve does not assume the floor is at height zero: feet follow their trackers, the hip and head limits are relative to the feet, and tracker roles use heights below the headset.
- **A reset or re-centre** is an instant jump of every device by the same rigid transform. It is recognised when every device moves at least 12 cm or turns by a common 8° within one poll, and the devices stay rigid to each other within 3 cm. The solver removes the jump before filtering and reapplies it to the solved body in the same frame. [Measured reset](accuracy.md#several-calibrations-in-one-take)

Moving and turning with the thumbsticks moves VRChat's world, not SteamVR's. The solve works in SteamVR's space, so they do not affect it.

### Jumping in VRChat

VRChat’s jump lifts the whole avatar in VRChat’s world while you stand still. The solver detects jumps and drops from `Grounded`, falling back to `VelocityY`. Applying a jump changes the solved body’s trajectory. Calibration, binding and tracker roles use the raw SteamVR data. [Take controls](../usage.md#adjust-a-recorded-take)

### Resizing the avatar in VRChat

The app starts a new stretch from the VRChat log event and looks for `ScaleFactor`, `ScaleFactorInverse`, `EyeHeightAsMeters`, `EyeHeightAsPercent` and `ScaleModified` around that event. It marks a stretch without an OSC report uncertain and can estimate size from the reference pose when available. [Take controls](../usage.md#adjust-a-recorded-take) · [Scaling measurements](accuracy.md#avatar-scaling-the-scaling-take)

### VRChat's own pose arriving late

A HUMR recorder stamps VRChat’s bones with the game clock, which falls behind real time on long frames such as avatar reloads. New takes use a sliding minimum of arrival times (±4 s). On load, older takes are aligned in memory by comparing VRChat’s head height with the headset in 16 s windows, every 4 s with interpolation. [Timing measurements](accuracy.md#other-measurements)

## Live view coordinates

Takes store the play area and the base stations as they were when recording started (about 0.8 KB), so a replay shows them where they were around the body. Base stations are for orientation only: they do not feed the solve.

The live view can overlay the HUMR skeleton by estimating the mapping from VRChat world space to the SteamVR playspace using the last 3 s of data. [Mapping measurements](accuracy.md#other-measurements)

## Threads

| Thread        | Work                                                              | Priority     |
| ------------- | ----------------------------------------------------------------- | ------------ |
| OpenVR poll   | Device poses at 90 Hz by default, finger input                    | above normal |
| OSC receive   | Parameter and pose messages, parsed without allocations           | above normal |
| VRChat log    | Follows the log for calibration, avatar and HUMR lines            | normal       |
| Take writer   | Flushes the take to disk while recording                          | below normal |
| Solve workers | Take and scene solves, cancellable                                | below normal |
| UI            | View-model updates, input and rendering                           | normal       |

Capture threads write into recorder-owned buffers. The UI reads immutable snapshots. [Desktop dispatch and worker rules](app-platform.md#rules-the-services-keep)

## Rendering

The desktop app passes viewport snapshots to `Vrmr.Render`. The renderer uses the GPU and redraws on demand, with display-paced updates during live preview or playback. [Renderer API and pacing](app-platform.md#renderer-api)

## Performance rules

- Measure before and after. `vrmr dump` gives solve times; the app's own frame statistics give draw times.
- Reuse buffers in capture and solve inner loops, and limit solve concurrency to leave CPU time for SteamVR and VRChat.
- Use events or file watchers where available. Keep viewport frame pacing and application work on their respective paths. [Renderer rules](app-platform.md#renderer-rules) · [Desktop service rules](app-platform.md#rules-the-services-keep)

## Regression check

`vrmr dump` writes hashes and raw curves for comparing solved output across builds. [Motion regression procedure](../../CONTRIBUTING.md#checking-solved-motion)

## User data

User data lives outside the application and repository. `AppPaths` resolves the data directories, and settings select the recording and avatar folders. [Platform paths](app-platform.md#apppaths) · [Stored files and personal data](file-formats.md#overview)
