# How it works

VR Motion Recorder listens to what VRChat and SteamVR report while you play, writes it down unchanged, and works
out the body afterwards. A take holds **raw inputs only**. The body you see is solved from them on your avatar's
own skeleton, and is solved again whenever you change a setting, a calibration or the avatar. Nothing you do in the
app alters a recording.

This page explains where the data comes from, how the body is solved, and what the app does about the things that
go wrong in real sessions: trackers that drop out, playspace resets, jumps, avatar resizing. The code layout is in
[architecture.md](architecture.md).

## Where the motion comes from

| Source | Works in | What it gives |
|---|---|---|
| **VRChat OSC** (found through OSCQuery, or UDP port 9001) | any world | Every avatar parameter: face tracking (`FT/v2/*`), gestures, locomotion, eye height, avatar scaling. In VR also VRChat's own head and wrist poses (`/tracking/vrsystem/*`) and the pose of VRChat's camera. |
| **SteamVR** (the app connects to a running SteamVR and never starts it) | any world | Headset, controllers and every tracker at full rate, and finger tracking through SteamVR Skeletal Input. Base stations, battery levels and the play area for the live view. |
| **VRChat's log, in worlds with a [HUMR](https://github.com/DrSakuu/HyakuashiUdonMotionRecorder) recorder** (VRChat's logging set to Full) | those worlds only | VRChat's **own** IK result for every humanoid bone, 30 times a second. |

No program outside VRChat can read VRChat's body pose in an ordinary world: VRChat only shows it to scripts that
run inside a world, which is what a HUMR recorder is. Everywhere else the body has to be solved again from the
devices. Where VRChat's own pose is available it is kept as a **reference**: it is how the solve was measured (see
[accuracy.md](accuracy.md)), and it can be chosen as the body source of a take.

Everything is read-only. The app joins SteamVR as a background utility, which lets it read controller and finger
input without taking it away from VRChat. It reads VRChat's log and VRChat's settings in the registry, and writes
neither.

### Face tracking

Face tracking is recorded from VRChat's OSC echo of the `FT/v2/*` avatar parameters that VRCFaceTracking sends:
full-precision floats for every face parameter the avatar has, plus the binary sync bits.

- Shapes the avatar does not have are not echoed, so they are not recorded.
- If VRChat treats the avatar as hardware full-face tracking, the face is sent as one binary blob instead of
  parameters, and no face parameters reach the recorder.
- The face-tracking program sends to one target only (VRChat). The recorder does not divert that stream; it only
  hears what VRChat echoes.

### Fingers

Fingers come from SteamVR Skeletal Input: real curl and splay on controllers that track fingers, SteamVR's estimate
on others. The full 31-bone hand skeleton is stored for each hand. Without it, VRChat's hand gestures are used. The
finger source can be changed per take.

## Recording

- Start recording with the record button, a global hotkey (F9 by default), or an avatar bool parameter
  (`VRMR/Record` by default) that you toggle from your avatar's menu. Recording can also follow the button of an
  in-world HUMR recorder.
- The app can be started before or after VRChat. VRChat's OSC has to be on (Action Menu → Options → OSC → Enabled).
  If another program already uses port 9001 and VRChat was running first, switch OSC off and on once so VRChat
  finds the recorder through OSCQuery.
- The take file is written while you record, every 200 ms, so a crash or a power cut keeps everything up to the
  last fraction of a second.
- Recording is cheap: OSC is parsed without allocations on its own thread, SteamVR is polled at 90 Hz in the
  background, and the 3D view drops to a skeleton while recording.

Every stream shares one high-resolution clock, so face, body and camera stay in step.

## The body solve

The body comes from a full-body IK solver that runs on the avatar's own skeleton. It is fed what VRChat is fed:
the head and hand targets, every SteamVR tracker, finger tracking, and eye tracking for the eye bones. The arm,
spine and knee behaviour is matched to VRChat's IK 2.0 by measurement on recorded takes; no third-party IK source
code is included.

Because a take keeps the raw data, all of this can be changed after recording, and the take solves again in a
fraction of a second:

- the calibration and every calibration setting (see [calibration.md](calibration.md));
- the avatar the take is solved onto (see [avatars.md](avatars.md));
- tracker roles, which trackers are used, and a hand rotation correction;
- the body source (the IK solve, or VRChat's own IK where the take has it), the finger source, locomotion,
  VRChat's jumps, tracking-loss repairs and smoothing;
- the face sync offset, and where the take is placed in the world.

### Tracker setups

Any subset of the 11 points works: head, both hands, hips, chest, both elbows, both knees, both feet. The table
shows the mean distance of all bones to VRChat's own pose on an 11-point test take, with trackers left out of the
solve only (so fewer trackers are compared with VRChat's 11-point pose).

| Setup | Standing | Lying on the floor |
|---|---|---|
| 11-point (head, hands, hips, chest, elbows, knees, feet) | 1.66 cm | 5.17 cm |
| 10 (no chest) | 1.71 cm | 5.06 cm |
| 10 (no hips) | 3.70 cm | 7.77 cm |
| 9 (no elbows) | 1.87 cm | 5.20 cm |
| 8 (no chest, no elbows) | 1.90 cm | 5.13 cm |
| 6 (head, hands, hips, feet) | 2.06 cm | 5.44 cm |
| 3 (head, hands) | 6.15 cm | 32.3 cm (the legs are procedural) |

The 3-point and no-hips rows mostly measure how far a guessed body is from a tracked one. The full table, the
method and its limits are in [accuracy.md](accuracy.md).

None of the extra trackers is bound rigidly, in VRChat or here: an elbow tracker steers the bend of the arm, a
knee tracker the bend of the leg, and a chest tracker turns the chest and pulls it gently towards the tracker.

### Tracker roles

Which tracker is on which body part is found automatically. Roles you set by hand, and the roles set in SteamVR's
"Manage Trackers", win.

1. **With VRChat's own pose in the take: VRChat's own assignment.** A tracker on a bone keeps its distances to
   the headset and the controllers changing exactly like that bone's distances to VRChat's head and hands. Each
   tracker is matched to the bone whose distance curves it follows best. On the 11-point test take all 8 trackers
   came out right, each 1.3–2.9 cm clearer than its next-best bone.
2. **Otherwise from how each tracker moves while you stand**, over the whole take: how far below the eyes it stays
   (feet, knees, hips and chest by your avatar's proportions; of two at hip height the higher is the chest),
   whether it keeps a steady distance to one controller (an elbow: the forearm is rigid, so the spread of that
   distance stays under 7 % of the eye height), and which side of the head it is on.
3. Only when neither is conclusive: the heights at the calibration moment.

None of these depends on the calibration pose or on where the playspace floor is.

## When tracking goes wrong

### A tracker loses tracking

A tracker that is lain on, hidden or out of battery leaves a hole, a frozen pose or a jump in the take. The app
finds these per tracker from:

- SteamVR's own tracking state, which is recorded as an event whenever it changes;
- no pose for more than 0.1 s;
- the same pose repeated for 0.08 s or more (SteamVR holding the last pose);
- a move faster than 12 m/s in one sample. A snap of more than 20 cm that does not come back means the pose before
  it had drifted, so the repair starts 0.3 s earlier.

A playspace reset, where all devices jump together, is not a loss.

The repair carries the lost tracker along with a neighbour on the same body segment (a knee with its foot, a foot
with its knee, an elbow with its hand, the hips with the chest and the other way round; otherwise the hips or the
headset). Its offset from the neighbour blends from the last good one before the loss to the first good one after
it, so the tracker moves with the body and lands exactly on the recovered pose. A tracker that never comes back
keeps its last offset to the end of the take, so the rest keeps solving. A tracker that is missing at the
calibration moment is not used at all.

On the 11-point test take this found 19 losses, 10.3 s in all: the hips for 2.5 s and the chest for 2.0 s while
lying on them, a knee frozen for 1.1 s and then jumping 55 cm, and single-sample foot glitches of up to 78 cm.

Each loss is listed with its reason and shown on the timeline. A repair can be switched off (the stretch is then
kept as recorded), and you can mark a stretch as lost yourself. The marks are saved in the take.

### The playspace moves

Tools that move or reset the playspace change the raw SteamVR coordinates, and VRChat follows them.

- **A slow drag** needs nothing. The solve does not assume the floor is at height zero: feet follow their
  trackers, the hip and head limits are relative to the feet, and tracker roles use heights below the headset.
- **A reset or re-centre** is an instant jump of every device by the same rigid transform. It is recognised when
  every device moves at least 12 cm or turns by a common 8° within one poll, and the devices stay rigid to each
  other within 3 cm. The solver is fed the poses with the jump undone, so its filters see continuous motion, and
  the jump is put back on the solved body: it teleports in the same frame as the devices, as the avatar does in
  VRChat. On the multi-calibration test take: one jump of 83 cm, all devices rigid within 1.8 cm, and no false
  jumps in the other takes.

Moving and turning with the thumbsticks moves VRChat's world, not SteamVR's. The solve works in SteamVR's space, so
they do not affect it.

### Jumping in VRChat

VRChat's jump lifts the whole avatar in VRChat's world while you stand still. Jumps and drops are found from the
avatar parameter `Grounded` (or from `VelocityY` when it is missing). By default a jump is left out of the result;
*Include VRChat jumps* keeps it. Jumps never touch the calibration, the tracker binding or the tracker roles,
which come from raw SteamVR data. On the 11-point test take: one jump, 0.9 s long and 43 cm high.

### Resizing the avatar in VRChat

VRChat reports avatar scaling over OSC (`ScaleFactor`, `ScaleFactorInverse`, `EyeHeightAsMeters`,
`EyeHeightAsPercent`, `ScaleModified`), but not every time. On a test take with four size changes:

| What VRChat did | What OSC reported |
|---|---|
| × 1.36 | `ScaleFactor` only; the eye height parameter did not change |
| back to × 1 | nothing |
| × 0.69 | all five parameters |
| reset to × 1 | all five parameters |

Every change does leave a line in VRChat's log, so the app starts a new stretch there and takes its size from OSC
around it. A stretch with no OSC report is marked uncertain; when the take holds VRChat's own pose, its size is
measured from that instead. The stretches are listed with the take.

- **Follow VRChat's avatar scaling** is off by default: the avatar keeps its own size through the whole take.
  Switched on, the body follows VRChat's size; exports then carry the scale as keys on the armature's top node
  (FBX) or a scale curve on the avatar root (`.anim`).
- **Avatar scale** forces a fixed size. Zero means the avatar's own size.

### VRChat's own pose arriving late

A HUMR recorder stamps VRChat's bones with VRChat's game clock, which falls behind real time on long frames
(avatar reloads, calibrations, size changes). On one test take the frames were in step for the first half minute
and 0.6–0.8 s late later on. New takes are timed against a sliding minimum (± 4 s) of the frames' arrival. Older
takes are re-aligned when they are opened, by comparing the height of VRChat's head with the headset's in 16 s
windows; the file is not changed.

## The live view

With SteamVR running, the 3D view can show what the devices do right now instead of a take: the headset, the
controllers and every tracker with a drop line to the floor, each base station with its field of view, and the
play area. A device that loses tracking stays at its last position, faded. The device list shows each device's
role, model, battery and tracking state.

Takes store the play area and the base stations as they were when recording started (about 0.8 KB), so a replay
shows them where they were around the body. Base stations are for orientation only: they do not feed the solve.

While recording in a world with a HUMR recorder, VRChat's own skeleton is drawn over the devices. VRChat's world is
not SteamVR's playspace (it differs by the spawn point, every thumbstick move and the tracking scale), so the view
estimates the mapping from the last 3 s. Replaying recorded takes as if live, VRChat's head lands 4.3 cm (median)
from where the headset says it should be and the hands 3.3 cm; 6–10 cm on takes with thumbstick moves, where the
mapping catches up within 3 s.

## Staying out of the way

The app runs next to VRChat and SteamVR, which need the processor and the graphics card more than a preview does.
The 3D view draws on the GPU and only when something changed, a paused app uses no processor time, and solves run
on background threads at low priority. The rules are in [architecture.md](architecture.md).

## See also

- [calibration.md](calibration.md): VRChat-style calibration, its settings and the calibration library.
- [avatars.md](avatars.md): the avatar library and solving a take onto another avatar.
- [scenes.md](scenes.md): arranging takes in the scene editor.
- [export-and-pipeline.md](export-and-pipeline.md): export formats, Blender and Unity.
- [file-formats.md](file-formats.md): what is inside a take and a scene file.
- [accuracy.md](accuracy.md): how the solve was measured against VRChat, and the results.
