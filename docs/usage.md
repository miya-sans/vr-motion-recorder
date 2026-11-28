# Usage

Record a performance with SteamVR, review and edit the take, then export it for animation in Blender or rendering in Unity. You can start with a headset and two controllers and add body trackers as needed.

## Record, review and export

1. **Record.** Start SteamVR, open the app and check that your devices appear. In Takes, click **Live** above the library to show the current device poses. Start recording with **Record** in the transport or **F9**, perform a short movement, then press again to stop. The recorder writes the take to disk as you move.
2. **Review.** Open the take from the library: the body is solved from the raw data and shown on your avatar. Scrub, trim with **I** and **O**, cut with **Del**. Change the calibration, the avatar or a setting, and the take solves again.
3. **Export.** Choose a folder and the formats: an FBX with your avatar, an armature-only FBX, Unity `.anim` clips.
4. **Open it in Blender or Unity.**

   - In Blender, open the exported FBX directly or use **Import VRMR Scene** with the [Blender add-on](../integrations/blender/README.md#install).
   - Export the take into a Unity project with the bridge installed. In Unity, run **Tools → VR Motion Bridge → Create Timeline From Scene**. [Unity bridge installation](../integrations/unity/com.vrmr.motion-bridge/README.md#install)

## Launch options

When starting the app with `dotnet run`, pass app options after `--`. For example, `dotnet run --project src/Vrmr.App -c Release -- --no-capture` starts the app with capture disabled.

- **`--data <folder>` or `--data=<folder>`** enables portable mode. Settings, calibrations, logs, takes, avatars and exports live under that folder. The app uses an isolated `Machine` subfolder for SteamVR, VRChat and registry lookups.
- **`--no-capture`** disables SteamVR, OSC, OSCQuery, log following and the global recording hotkey. You can still open, edit and export takes.
- **`--background`** opens the window without activating it.

The app rejects unrecognised arguments before opening its data.

### Viewport benchmarks

- Run `dotnet run --project src/Vrmr.App -c Release -- --bench <take.vrmr>` to measure a paused take with the mesh, playback with the mesh, playback with the skeleton only, and playback with the mesh and 14 simulated devices. Capture sources stay off. Use a take whose avatar mesh the app can load.
- Use `--bench-live` in place of `--bench <take.vrmr>` to measure the real Live preview with SteamVR or VRChat input. With `--data`, the app uses that folder for its files and the real machine for source discovery.
- Use `--bench-seconds <seconds>` to change the sample duration per scenario from the default of 10 seconds. The app writes FPS, frame times, CPU use and allocations to its [log](troubleshooting.md) and exits.

Close any running instance that uses the same data folder before starting the benchmark.

## Recording

- The app records SteamVR device poses and tracking-state changes, plus finger motion through SteamVR Skeletal Input on supported controllers. It writes samples during recording and timestamps them on the same clock as any VRChat inputs.
- Start or stop recording with **Record** in the Live transport or the global hotkey (**F9** by default).
- The recording countdown selector beside **Record** offers 0, 1, 3 or 5 seconds; the same delay applies to the Record button and **F9**.

### Optional VRChat inputs

- Enable OSC in **Action Menu → Options → OSC → Enabled** to capture face tracking, avatar parameters and camera poses. The app can start before or after VRChat. If VRChat was running first and another program uses port 9001, toggle OSC off and on so VRChat discovers the app through OSCQuery.
- For VRChat full-body tracking, calibrate in VRChat as usual. Use the avatar bool parameter `VRMR/Record` to start or stop a take from an avatar menu. [Calibration guide](calibration.md)
- In a world with Hyakuashi Udon Motion Recorder (HUMR), the app can record VRChat’s solved avatar motion from the log alongside the SteamVR poses. It can also follow the in-world recorder button.

## The live view

With SteamVR running, click **Live** above the take library to show what the devices do right now instead of a take: the headset, controllers and trackers with drop lines to the floor, each base station with its field of view, and the play area. A device that loses tracking stays at its last position, faded. The device list shows each device's role, model, battery and tracking state. Click a take in the library to leave Live and review it; click **Live** again to return to the live view. Recording also switches to Live.

In Live, the transport contains **Record**, its countdown selector, **Calibrate**, a calibration selector and the age in minutes of the last successful calibration. Choose **Sync with VRChat** to use detected VRChat calibrations, or choose a 1-, 3- or 5-second countdown and press **Calibrate** to capture the current SteamVR poses manually. See [calibration.md](calibration.md) for details.

During take review, the timecode and frame number appear above the timeline. Use the timeline button in the transport area (**Ctrl+3**) to hide or show it, and drag its divider to resize it; reopening it restores its previous height. The library and inspector can also be hidden or shown (**Ctrl+1** and **Ctrl+2**).

## Adjust a recorded take

The full-body IK solver uses the selected avatar’s skeleton and supports 3-point tracking (head and hands) through 11-point tracking. It can infer tracker roles and follow recorded VRChat calibrations, spine and measurement changes, and playspace resets. [Accuracy measurements](dev/accuracy.md#fewer-trackers-on-the-same-take)

Use the built-in mannequin or import a base model you own from its avatar package without opening Unity. The avatar library can merge face shapes from BlendShare add-ons. You can solve a recorded take again on another humanoid avatar. [Avatar guide](avatars.md)

Trim, cut and place a take with undo. These edits preserve the raw recording.

Because a take keeps the raw data, all of this can be changed after recording, and the take solves again in a fraction of a second:

- the calibration and every calibration setting (see [calibration.md](calibration.md));
- the avatar the take is solved onto (see [avatars.md](avatars.md));
- tracker roles, which trackers are used, and a hand rotation correction;
- the body source (the IK solve, or VRChat's own IK where the take has it), the finger source, locomotion, VRChat's jumps, tracking-loss repairs and smoothing;
- the face sync offset, and where the take is placed in the world.

Each loss is listed with its reason and shown on the timeline. A repaired tracker follows a nearby tracked body part through the gap and blends back to its recovered pose if tracking returns. Switch off a repair to keep the stretch as recorded, or mark a stretch as lost yourself. The marks are saved in the take. [Tracking loss and repair](dev/architecture.md#a-tracker-loses-tracking)

For a VRChat take, enable *Include VRChat jumps* to keep in-game jumps.

- **Follow VRChat's avatar scaling** is off by default: the avatar keeps its own size through the whole take. Switched on, the body follows VRChat's size; exports then carry the scale as keys on the armature's top node (FBX) or a scale curve on the avatar root (`.anim`).
- **Avatar scale** forces a fixed size. Zero means the avatar's own size.

## Export options

- **FBX.** Export with the avatar’s meshes, textures and face shape keys, or export the armature on its own.
- **Blender.** Import avatar motion, face shapes, parameters and cameras with the add-on. It also supports retargeting and **Send to Unity**.
- **Unity.** Use transform `.anim` clips for Generic rigs or the Unity bridge for Humanoid setup, clip generation and Timeline import. Play the animation on your avatar to render with components such as hair and cloth physics.

The desktop exports takes. Multi-actor scene export is available through the scene engine only. [Scene exports](export-and-pipeline.md#scenes)

See the [export guide](export-and-pipeline.md) for format details and the Blender and Unity workflows.

## See also

- [calibration.md](calibration.md): VRChat-style calibration, its settings and the calibration library.
- [avatars.md](avatars.md): the avatar library and solving a take onto another avatar.
- [file-formats.md](dev/file-formats.md): what is inside a take and a scene file.
- [accuracy.md](dev/accuracy.md): how the solve was measured against VRChat, and the results.
