# File formats

What the app reads and writes, where each file lives, and what is inside. The code is the reference: each section names the file that defines the format. All numbers are little-endian, all text is UTF-8, positions are in metres and rotations are quaternions (x, y, z, w) in Unity's coordinate space (left-handed, Y up).

## Overview

| File                            | Where                                  | What                                                  | Personal data inside                                                                                                       |
| ------------------------------- | -------------------------------------- | ----------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------- |
| `*.vrmr`                        | `Documents\VR Motion Recorder`         | A take: raw inputs and a small edit list.             | Your motion and face, avatar id and name, world name and id, VRChat display name, device serial numbers, User Real Height. |
| `*.vrmscene`                    | `Documents\VR Motion Recorder\Scenes`  | A scene: an arrangement of takes.                     | Paths to takes, avatar names.                                                                                              |
| `Avatars\<Name>\`               | `Documents\VR Motion Recorder\Avatars` | An avatar library entry.                              | Files of an avatar you own.                                                                                                |
| `settings.json`                 | `%APPDATA%\VRMotionRecorder`           | App settings.                                         | Folder paths, avatar ids.                                                                                                  |
| `calibrations\snapshots\*.json` | `%APPDATA%\VRMotionRecorder`           | Calibration snapshots.                                | Device serial numbers, avatar id and name, User Real Height.                                                               |
| `calibrations\*.json`           | `%APPDATA%\VRMotionRecorder`           | Named calibration presets.                            | None.                                                                                                                      |
| `profiles\*.vrmskel.json`       | `%APPDATA%\VRMotionRecorder`           | Avatar skeletons written by the Unity package.        | Avatar name and blueprint id.                                                                                              |
| `steamvr_room.json`             | `%APPDATA%\VRMotionRecorder`           | The last SteamVR play area and base stations seen.    | Base station serial numbers.                                                                                               |
| `vrmr.log`                      | `%APPDATA%\VRMotionRecorder`           | The log of the current run.                           | Paths, avatar and world names.                                                                                             |
| export files                    | where you export                       | See [export-and-pipeline.md](export-and-pipeline.md). | Avatar id and name; avatar meshes in the model FBX.                                                                        |

Both folders can be moved in Settings. Started with `--data <folder>`, the app keeps everything under that one folder instead, which is how tests and trial runs stay away from your real files.

None of these files belongs in the repository, in a bug report without a second look, or in a pull request.

## Take: `.vrmr`

Defined in `src/Vrmr.Model/Takes/RecordingIO.cs` and `Recording.cs`.

A take stores **raw inputs only**. Edits are a small list inside its metadata, and the body is solved again from the raw data whenever something changes.

```
"VRMR"  u16 version (1)  u16 flags (0)
then chunks:  u32 tag (four ASCII characters)  i32 length  payload
```

A recording in progress is written chunk by chunk every 200 ms. A file that ends in the middle of a chunk (a crash, a power cut) loads up to the last complete chunk. Unknown tags are skipped.

| Tag    | Payload                                                                                                                                                                                                                                                                          |
| ------ | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `META` | JSON: name, creation time, duration, avatar id and name, world, user, notes, `edits`, `extra` (see below).                                                                                                                                                                       |
| `PROF` | JSON: the skeleton of the base model of the avatar worn, with its library id.                                                                                                                                                                                                    |
| `PDEF` | Avatar parameter definitions: `i32 count`, then per parameter `u16 id`, `u8 type` (0 float, 1 int, 2 bool), `u16 length`, name.                                                                                                                                                  |
| `PSMP` | Parameter samples: `i32 count`, then per sample `f64 time`, `u16 id`, `f32 value`.                                                                                                                                                                                               |
| `SDEF` | Pose stream definitions: `i32 count`, then per stream `u16 id`, `u8 kind`, `u16 length`, name, `u16 length`, info JSON (serial, device class, role, model, controller type). Kinds: 0 SteamVR device, 1 VRChat head, 2 VRChat left wrist, 3 VRChat right wrist, 4 VRChat camera. |
| `SSMP` | Pose samples: `i32 count`, then per sample `f64 time`, `u16 id`, position `3 × f32`, rotation `4 × f32`. SteamVR devices are in raw playspace coordinates.                                                                                                                       |
| `HUMR` | VRChat's own IK frames (worlds with a HUMR recorder): `i32 count`, then per frame `f64 time`, `f64 VRChat's time`, hips position `3 × f32`, 55 bone rotations `4 × f32` each (Unity's humanoid bone order).                                                                      |
| `HAND` | Finger tracking: `i32 count`, then per sample `f64 time`, `u8 side`, 5 curls and 4 splays as `f32`, `u16 count` and that many `i16` values: SteamVR's 31-bone hand skeleton, quantised.                                                                                          |
| `EVNT` | JSON list of events, each `{ "t": seconds, "type": …, "text": … }`.                                                                                                                                                                                                              |
| `END ` | Length 0. Marks a cleanly closed file.                                                                                                                                                                                                                                           |

Sample chunks repeat: a long take has many `PSMP` and `SSMP` chunks. All times are seconds from the start of the take on one high-resolution clock, so face, body and camera stay in step.

### Events

| Type                        | Meaning                                                                                                   |
| --------------------------- | --------------------------------------------------------------------------------------------------------- |
| `avatar`, `avatarReload`    | The avatar changed, or the same avatar was loaded again (a calibration does that).                        |
| `remeasure`, `ikInit`       | VRChat re-measured the avatar, or set up its IK (from VRChat's log). They are how calibrations are found. |
| `world`, `joining`          | The world's name and id when you entered it (from VRChat's log).                                          |
| `triggers`, `triggerValues` | Trigger edges (`both`, `left`, `right`, `none`) and analogue values, for the calibration confirm.         |
| `tracking`                  | SteamVR's tracking state of one device changed (200 tracking, 0 pose invalid, −1 disconnected).           |
| `vrcSettings`               | VRChat's IK settings (spine mode, avatar measurement, User Real Height) when they changed.                |
| `calibration`               | A calibration snapshot was saved during the take.                                                         |

### `META.extra`

`steamvrSession` (which SteamVR run the take belongs to), `vrchatSettings` (User Real Height and IK settings when the take started), `steamvrRoom` (the play area's floor corners and the base stations: serial, position, rotation, field of view, in raw SteamVR space), `avatarBase` (how the base model was matched).

### `META.edits`

Everything you change about a take, and nothing else:

| Key                                             | Meaning                                                                                                                                            |
| ----------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------- |
| `trimIn`, `trimOut`, `cuts`                     | The trimmed range and the cut stretches, in take seconds.                                                                                          |
| `placePos`, `placeRot`                          | Where the solved take is placed: a move in metres and a turn as a quaternion, applied after the solve.                                             |
| `originTime`, `originYaw`, `originX`, `originZ` | An older way of placing a take, still read.                                                                                                        |
| `calibrationTime`                               | Which calibration the take uses: a time in the take, or a code for "best T-pose", "the linked snapshot", "VRChat's calibrations as they happened". |
| `calibration`                                   | The take's own calibration settings (absent: the global defaults). See [calibration.md](calibration.md).                                           |
| `calibrationSnapshot`                           | A copy of the calibration snapshot the take is linked to, so the take stays self-contained.                                                        |
| `vrcChanges`                                    | VRChat setting changes added by hand.                                                                                                              |
| `trackerGaps`                                   | Tracking-loss marks you added or switched off.                                                                                                     |
| `bodySource`                                    | `auto`, `ik` (the solve) or `humr` (VRChat's own IK).                                                                                              |
| `fingerSource`                                  | `auto`, `steamvr`, `gestures` or `none`.                                                                                                           |
| `locomotion`, `jumps`                           | Include VRChat's locomotion, and VRChat's jumps.                                                                                                   |
| `faceOffset`                                    | Seconds added to the face parameters' time.                                                                                                        |
| `targetAvatar`                                  | The library entry the take is solved onto (empty: the avatar it was recorded with).                                                                |

`vrmr info <take.vrmr>` prints what a take contains.

## Scene: `.vrmscene`

Defined in `src/Vrmr.Scene/SceneModel.cs`. Readable JSON, `"kind": "scene"`, `"formatVersion": 1`.

| Key                           | Content                                                                                                                                                                                                                      |
| ----------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `name`, `fps`, `start`, `end` | The scene's name, frame rate and range.                                                                                                                                                                                      |
| `takes`                       | The takes used: id, path relative to the scene file, name, creation time, duration. A take that moved is found again by its creation time.                                                                                   |
| `actors`                      | One per performer: the avatar, modifiers, and `tracks`.                                                                                                                                                                      |
| `actors[].tracks`             | `kind` (`pose` or `input`), mute, solo, lock, `strips`, `transitions`.                                                                                                                                                       |
| `strips`                      | `kind` (`take` or `action`), source, start, `srcIn` / `srcOut`, speed, blend mode, influence, blend in and out, the body-part `mask`, input settings (`inputs`, `inputAlign`, per-device weights), a root offset, modifiers. |
| `transitions`                 | From strip, to strip, duration, curve, alignment, `matchVelocity`, score.                                                                                                                                                    |
| `actions`                     | Keyframe channels: `bone/<HumanBodyBones>` (local rotation), `root/position`, `face/<shape>`, `param/<name>`, `device/<Role>/position` and `/rotation`, `influence/<Role>`. Keys are `[time, interpolation, values…]`.       |
| `markers`                     | Named times.                                                                                                                                                                                                                 |
| `cameras`                     | Reserved.                                                                                                                                                                                                                    |

Keys the app does not know are kept when it saves, so a newer scene survives an older build.

## Avatar library entry

Defined in `src/Vrmr.Avatars/AvatarLibrary.cs` and `src/Vrmr.Model/Avatars/AvatarProfile.cs`. One folder per entry:

- `<Name>.vrmavatar.json`: the entry's id and name, the name patterns and VRChat avatar ids that identify it, its eye height, and the humanoid skeleton: every node with its parent and its local rest transform exactly as Unity imports the model, the mapping from Unity's humanoid bones to nodes, and the blendshape names of each mesh.
- `<Name>_FT.fbx`: the model with its face-tracking shapes merged in.
- `textures\`: the textures its materials use.

`*.vrmskel.json` (written by the Unity package's **Export Avatar Skeleton**) holds the same skeleton for an avatar in a Unity project.

## Calibration snapshot

Defined in `src/Vrmr.Model/Calibration/CalibrationSnapshot.cs`. One JSON file per calibration: when and how it was taken, the raw pose, serial number, role and model of every device, SteamVR's skeletal wrists, VRChat's User Real Height and IK settings, the avatar's id, name and eye height, and the SteamVR session. See [calibration.md](calibration.md).

Calibration presets (`calibrations\*.json`) hold only the settings of the table in that document.

## Export manifests and sidecars

Defined in `src/Vrmr.Export`. Read by the Blender add-on and the Unity package; the classes at the top of `integrations/unity/com.vrmr.motion-bridge/Editor/VRMotionBridgeScene.cs` mirror the manifest.

**`<name>.vrmrscene.json`** (`"kind": "scene-export"`): frame rate, frame range and frame count, `actors` (name, the files written for the actor, avatar id, name and base model, the face mesh, scale, and the track layout for reference), `markers`, `cameras` (per frame position, rotation and vertical field of view in Unity space), and `warnings`. A take export is a manifest with one actor.

**`<name>.vrmr.json`** (the Unity sidecar): which avatar the clip belongs to (`avatarId`, `avatarName`, `avatarBase`, `unityModel`), the face mesh, `human` (pairs of bone name and Unity humanoid bone name) and `rest` (the base model's T-pose: name, parent, local position, rotation and scale per bone). When the Blender add-on writes it (**Send to Unity**), it has `"source": "blender"` and also carries `params` (one value per frame for each parameter) and `cameras`.

**`<name>.face.json`**: the frame rate and frame count, `shapes` (one value per frame for each face shape), `params` and `paramTypes` (the raw avatar parameters), and the avatar link and humanoid rig.

## Versioning

`.vrmr` and `.vrmscene` carry a format version. A change that older builds cannot read raises it; additions that older builds can ignore (a new event type, a new key) do not. Tests in `tests/Vrmr.Tests` write and read both formats.
