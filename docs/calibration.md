# Calibration

Body calibration works like VRChat's full-body calibration: you calibrate in VRChat as usual and the app follows it. A take keeps the raw device data, so the calibration, your size and every setting on this page can be changed after recording, and the take is solved again in a fraction of a second. The constants below were measured against VRChat's own IK; [accuracy.md](accuracy.md) says how, and how close the result is.

## What calibration means here

**The headset and controllers are never calibrated.** The head and hands sit on them through fixed offsets, the ones VRChat uses:

- **Hands (Index controllers).** The hand bone sits where VRChat puts it: (∓2.1, 2.2, −10.3) avatar cm in the controller's frame, with a fixed rotation that is stored relative to the hand's T-pose, so it does not depend on the avatar. The offset follows the **avatar's** size, not your tracking scale, and is the same in every spine mode and measurement mode. Other controllers, or Index controllers with the setting switched off, use SteamVR's skeletal wrist plus the base solver's controller offsets.
- **Head.** The head bone sits 4.1 cm below and 4.0 cm behind the headset (avatar cm), rigidly with *Lock head*. The eye point VRChat's IK really uses lies between the avatar's eye bones and its view position; the app uses that measured point.

**Body trackers** (hips, chest, feet, knees, elbows) are bound to their bones at one **calibration moment**: VRChat's own calibrate-and-confirm, a manual capture, or a T-pose in the take. At that moment the avatar is in its T-pose **hanging from your headset**, facing where you face, as in VRChat. If your scaled headset is lower than the avatar's eyes, the avatar's feet end up below the floor with straight legs. Each tracker's offset to its bone (position and rotation) is stored, and from then on the bone follows its tracker. A tracker that is missing at the calibration moment is not used.

**Your size** comes from VRChat's own **User Real Height** by default, read from VRChat's settings in the Windows registry (read-only):

- **By height:** eye height = height × **0.94**, the ratio VRChat was measured to use.
- **By arm span:** VRChat's formula. The avatar's head bone to wrist distance, arm straight out in the T-pose, divided by **0.4537**, is mapped onto your eye height.

The scale, the floor and the tracker offsets come from the same calibration, so they agree with each other. With only a headset and controllers there are no trackers to bind, and the calibration only sets your size.

## Quick guide

1. **Calibrate in VRChat as usual** (Calibrate FBT, then both triggers), before or during the take. The app saves every VRChat calibration to its library, and a take uses the calibrations that were in effect while it was recorded.
2. **For a live calibration,** choose **Sync with VRChat** in the Live transport to use detected VRChat calibrations automatically. To capture manually, choose a 1-, 3- or 5-second countdown and press **Calibrate** while standing in a T-pose (stand straight, look ahead, arms out). In a take's inspector, **Capture now** uses a fixed 5-second countdown to save the current SteamVR poses.
3. **Change it later if you like.** In the take's inspector (Calibration section) you can pick another calibration from the library, the take's best T-pose or any moment of the take, and change every setting in the table below. Nothing is baked into the recording.

## How VRChat's calibration is detected

VRChat's calibration is detected automatically, whether the app is recording or not, and saved as a **calibration snapshot**. Everything used for this is read-only: VRChat's log, and SteamVR's trigger input, which the app reads as a background utility app (SteamVR delivers it while VRChat has focus). VRChat's sequence is:

1. **"Calibrate FBT"** (a menu click with one trigger) reloads the avatar. The log shows the avatar switch, the limb and IK set-up and a re-measure of the avatar, all within about a second. The avatar is now in calibration mode.
2. **The confirm** (both triggers, or one with VRChat's one-handed option) logs a lone re-measure line some seconds later: 3.4–15 s after the reload on the test takes.

The snapshot holds the device poses at the **full press**: the moment both triggers rise above 0.7, minus 0.03 s. On a take where the headset moved 8 cm in the 0.3 s before the confirm, the full press fitted VRChat's binding about 1 cm per bone better than the start of the pull (see [accuracy.md](accuracy.md)). When there is no trigger data, these are used instead, in order:

- the moment both index fingers curl and release together (Index finger tracking);
- 0.3 s before VRChat's log line.

**Not counted** as a calibration:

- a change of the avatar measurement (the same reload, but its re-measure follows in the same second);
- avatar loads after a world change.

VRChat's OSC `TrackingType` is not used: it did not change through whole calibrations.

How reliable this is: the detected confirm lay within 0.07 s of an independent trigger probe on the reference takes, and replaying a whole day's VRChat log through the live detector found exactly the 5 confirms in it and none of the 5 other avatar loads.

Takes recorded by early versions have no trigger or re-measure events. For those the app reads VRChat's log for the take's time (if the log still exists) and refines the moment with the index fingers.

## The calibration library

Snapshots are JSON files in `%APPDATA%\VRMotionRecorder\calibrations\snapshots\`, one per calibration, named by their creation time. A snapshot stores:

- the raw pose, serial number, role and model of every device;
- SteamVR's skeletal wrist for each hand, when finger tracking was live;
- VRChat's User Real Height and IK settings;
- the avatar's id, name and eye height;
- the SteamVR session (the start time of SteamVR's server), which tells whether two calibrations share a playspace;
- when it was taken, where it came from (VRChat, a manual capture or a take) and how the moment was found.

> **Privacy.** A snapshot contains your devices' serial numbers, your avatar's id and name and your User Real Height. Takes embed a copy of the snapshot they use. Don't share either without thinking about it.

**New takes are linked automatically** to VRChat's calibrations as they happened:

- the calibration in effect when recording started: the latest snapshot from before the take whose trackers cover the take's, preferring one from the same SteamVR session;
- then every VRChat calibration inside the take, from its "Calibrate FBT" click on.

So calibrating without recording counts, and so does recalibrating in the middle of a take. If neither exists, the take is linked to the latest matching snapshot from before the take ended. A take with no calibration at all is calibrated at its best T-pose. The copy of the snapshot saved inside the take keeps it self-contained even when the snapshot is deleted from the library.

**Per take** you can choose the calibration it uses:

- the take's best T-pose (found automatically);
- any moment of the take (calibrate at the playhead);
- VRChat's calibrations as they happened, when VRChat calibrated before or inside the take;
- one of VRChat's calibrations inside the take, for the whole take;
- any snapshot in the library.

The list marks the snapshots whose trackers match the take, and warns when:

- a tracker of the take is missing from a snapshot (that tracker is then not used);
- a snapshot comes from another SteamVR session;
- a snapshot was taken hours apart from the take: the straps have probably moved, so recalibrate.

Library commands:

- **Capture now** saves the current SteamVR poses after a 5-second countdown (stand straight, look ahead, arms out), for when you don't calibrate in VRChat.
- **Save to library** stores the calibration a take uses (an old take's T-pose, for example), so other takes can use it.
- **Delete** sends a snapshot to the Recycle Bin. Takes keep their own copy.

Two switches in Settings turn off the watching (saving every VRChat calibration) and the automatic linking of new takes.

## Several calibrations and setting changes in one take

A take can contain anything VRChat lets you do while recording, and the solve follows VRChat through it. This was checked on the multi-calibration take (see [accuracy.md](accuracy.md)): two calibrations, a switch from arm span to height, a switch of the spine mode, thumbstick moves, a playspace mover and a playspace reset.

- **Calibrations as they happened.** The take starts with the calibration that was in effect when recording started, and each VRChat calibrate-and-confirm takes over from its "Calibrate FBT" click on.
- **Settings as they happened.** VRChat's spine mode and avatar measurement apply from the moment they changed (*Follow VRChat's spine mode and avatar measurement*, on by default). While recording, the app logs VRChat's IK settings every 0.5 s (registry, read-only) as `vrcSettings` events in the take. Takes without these events only have VRChat's log: there a measurement change can be recognised (an avatar reload whose re-measure follows in the same second), but a spine mode change leaves no trace. For those takes you can add a setting change by hand at the playhead (Lock hip / head / both, or by height / by arm span).
- **After a measurement change** VRChat re-applies the same calibration at the new scale, and so does the solve: with the calibration rescaled, the feet stay within 0.9–1.0 cm of VRChat's.
- **Stretches.** Each stretch is solved with its own calibration, scale and spine mode, and neighbouring stretches crossfade over 0.3 s. The take's inspector lists the stretches under the calibration result, and the timeline marks VRChat's calibrations, setting changes and playspace jumps and shades where VRChat was not tracking.
- **Playspace movers and resets** (OVR Advanced Settings, for example) need no setting; see [Architecture](architecture.md).

**While VRChat is in calibration mode** (from the "Calibrate FBT" click to the confirm) VRChat hides the avatar, and its own IK hangs 30–60 cm off the controllers.

- *IK solve:* the new calibration already applies from the click. You stand in the calibration pose with the trackers strapped as at the confirm, so the body is solved normally there. Joint speeds in those stretches stay like the rest of the take (p95 0.75–1.25 m/s on the multi-calibration take).
- *VRChat's own IK (HUMR):* the stretch stays in the take as VRChat recorded it, so you can see it. It is shaded on the timeline; select the shaded span and cut it to remove it.

## Settings

Every setting can be set per take, as a global default and as a named preset (see "Saving and loading" below).

| Setting                                                       | What it does                                                                                                                                                                                                                                                                                                                                                                                                                                                                                           |
| ------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| **Height** (VRChat's "User Real Height")                      | *VRChat's User Real Height* (default): read from VRChat's settings, and stored with each calibration and take so it solves the same way later. Or *Measure from the headset at calibration* (eye height at the calibration moment, head tilt removed). Or *My real height* (eye height = height × 0.94). Or *My eye height*. Or *Fixed tracking scale* (real metres → avatar metres).                                                                                                                  |
| **Follow VRChat's spine mode and avatar measurement**         | On by default: the take uses the spine mode and measurement VRChat had while recording, switching when they changed. The *Avatar measurement* and *Spine* rows then only apply to takes without that information.                                                                                                                                                                                                                                                                                      |
| **Avatar measurement**                                        | VRChat IK 2.0's choice: *by height* (default; best for full-body tracking) or *by arm span*. Arm span uses VRChat's formula: the avatar's head-to-wrist reach / 0.4537 against your eye height. With *Measure from the headset* it uses the controller span in the T-pose minus 7 cm per hand instead.                                                                                                                                                                                                 |
| **Follow VRChat's avatar scaling**                            | Off by default: the avatar keeps its own size when you resize it in VRChat. On: the body follows VRChat's height slider and resets (see [Architecture](architecture.md)).                                                                                                                                                                                                                                                                                                                              |
| **Avatar scale**                                              | 0 (default) = the avatar's own size, as uploaded. Any other value forces that size.                                                                                                                                                                                                                                                                                                                                                                                                                    |
| **Spine (with a hip tracker)**                                | VRChat's full-body spine modes, as measured. *Lock head* (default): the head stays on the headset; the hips come down (up to 3 cm) when a leg can't reach its tracker. *Lock hips*: the hips stay on the tracker; the head may leave the headset (p95 9 cm in VRChat; the spine doesn't fold shorter than its length) and a foot that can't reach lifts. *Lock both*: the spine bends to keep both; a gap it can't close goes mostly to the head (the hips follow 25 %), and a foot may lift a little. |
| **Trackers used**                                             | Hips, chest, feet, knees and elbows, each on by default. Switching one off solves without it and leaves the tracker roles alone.                                                                                                                                                                                                                                                                                                                                                                       |
| **Keep feet on their trackers (Lock head)**                   | On by default. With *Lock head*, a leg that can't reach its foot tracker (a slipped tracker, standing taller than at calibration) gets the hips lowered just enough, as VRChat does. *Lock hips* and *Lock both* keep the hips and let the foot lift, also as VRChat does. With *Lock both* the head target also stops at the calibration standing height.                                                                                                                                             |
| **Ignore head tilt at the calibration moment**                | On by default. Turns the head upright about the avatar's head pivot before binding. A confirm is often made while looking down at the menu.                                                                                                                                                                                                                                                                                                                                                            |
| **Hang the avatar from the headset at calibration (VRChat)**  | On by default. Off stands the avatar on the floor at calibration, as the base solver does; the knees then bend whenever the scaled headset is lower than the avatar's eyes.                                                                                                                                                                                                                                                                                                                            |
| **Head and hands where VRChat puts them (Index controllers)** | On by default: VRChat's measured hand pose on Index controllers and VRChat's head offset. Off: SteamVR's skeletal wrist plus the base solver's offsets, and the avatar's eye bones at the headset.                                                                                                                                                                                                                                                                                                     |
| **Shoulder movement**                                         | On by default. Off keeps the shoulders still, like VRChat's `--disable-shoulder-tracking` launch option.                                                                                                                                                                                                                                                                                                                                                                                               |
| **Procedural steps for feet without trackers**                | On by default: with head and hands only, the feet take procedural steps (the base solver's foot IK). Tracked feet always follow their trackers.                                                                                                                                                                                                                                                                                                                                                        |
| **Hand rotation fix**                                         | Zero by default. A per-take correction of the hand angle in degrees, mirrored for the right hand.                                                                                                                                                                                                                                                                                                                                                                                                      |

After every solve the Calibration section shows the result:

- the calibration used and the scale, with how the scale was measured;
- where the hand offsets came from;
- how far the head, hips and feet drift from their devices (cm);
- how high the standing feet are above the floor;
- how often and how far the hips were lowered to keep the feet planted.

In the 3D view the controllers and trackers are drawn at the tracking scale, so you can see the hands holding the controllers.

**Show** (next to *Find T-pose*) shows the calibration of the stretch at the playhead instead of the solved pose: the avatar's T-pose at the center of the view, facing ahead, and every device where it was against the avatar at that moment. Unused trackers are dimmed. Playing turns it off.

While it shows, each body tracker can be moved against the avatar, as an edit of that calibration: click a tracker in the view (or its gizmo button in the list) and drag the arrows, or type the offset (right, up, forward, in avatar cm). The bone is then bound that much the other way. Each calibration of a take keeps its own offsets; they are saved with the take. Use them when a calibration was made while not standing straight, or when a strap slipped; a better calibration moment (*At playhead*, *Find T-pose*) fixes every tracker at once and is usually the better choice. Things to know:

- Feet are pinned to their trackers, so moving a foot tracker up lowers the foot against the floor. A foot only goes as low as its leg reaches: with straight legs at calibration, most of a downward move is lost.
- Moving the hip tracker changes how far the legs reach.
- With offsets, the solve no longer matches VRChat's.

With **Mirror** on (the default), moving a left or right tracker puts its partner at the mirror image of where the moved tracker ends up, across the avatar's middle: the positions are mirrored, not just the offsets. The view's **Front**, **Side** and **Top** buttons (Numpad 1, 3, 7) look at the pose without perspective, which makes lining trackers up easier; dragging to orbit, **3D** or Numpad 5 goes back to perspective.

## Saving and loading

Calibration settings live in three places:

- **Per take.** Any change gives the take its own settings, saved in the take's edit list next to the raw data (`calibration` in the edits; see [file-formats.md](file-formats.md)), so the take always solves the same way.
- **Global defaults** (Settings → Calibration defaults). They apply to every take that has no settings of its own (`calibrationDefaults` in `settings.json`). A take's settings can be made the default, and a take can drop its own settings and go back to the defaults.
- **Named presets.** Save the current settings under a name, load them into another take, or delete them (the file goes to the Recycle Bin). Presets are JSON files in `%APPDATA%\VRMotionRecorder\calibrations\`.

## See also

- [Architecture](architecture.md): the data sources, the IK solve, tracker roles, tracking-loss repair.
- [accuracy.md](accuracy.md): how the solve was measured against VRChat's own IK, and the results.
- [avatars.md](avatars.md): the avatar library, and solving a take onto another avatar.
