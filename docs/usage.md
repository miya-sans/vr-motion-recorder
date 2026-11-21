# Usage

## Record, review and export

1. **Record.** Start SteamVR, open the app and check that your devices appear. Press the record button or **F9**, perform a short movement, then press again to stop. The recorder writes the take to disk as you move.
2. **Review.** Open the take: the body is solved from the raw data and shown on your avatar. Scrub, trim with **I** and **O**, cut with **Del**. Change the calibration, the avatar or a setting, and the take solves again.
3. **Export.** Choose a folder and the formats: an FBX with your avatar, an armature-only FBX, Unity `.anim` clips.
4. **Open it in Blender or Unity.**
   - Blender (4.2 to 5.3): install the [add-on](../integrations/blender/README.md) and use **Import VRMR Scene**, or import the FBX directly.
   - Unity (2022.3 or later): install the [package](../integrations/unity/com.vrmr.motion-bridge/README.md) by Git URL, export into your project, and run **Tools → VR Motion Bridge → Create Timeline From Scene**.

     ```
     https://github.com/miya-sans/vr-motion-recorder.git?path=integrations/unity/com.vrmr.motion-bridge
     ```

## Launch options

Two options are useful for trying things out; with `dotnet run`, put them after `--` (`dotnet run --project src/Vrmr.App -c Release -- --no-capture`):

- `--data <folder>`: keeps takes and settings in that folder instead of your real ones.
- `--no-capture`: opens takes only, without connecting to SteamVR or VRChat.

## Recording

- Start recording with the record button, a global hotkey (F9 by default), or an avatar bool parameter (`VRMR/Record` by default) that you toggle from your avatar's menu. Recording can also follow the button of an in-world HUMR recorder.
- For VRChat OSC capture, the app can be started before or after VRChat. Enable OSC (Action Menu → Options → OSC → Enabled). If another program already uses port 9001 and VRChat was running first, switch OSC off and on once so VRChat finds the recorder through OSCQuery.

## The live view

With SteamVR running, the 3D view can show what the devices do right now instead of a take: the headset, the controllers and every tracker with a drop line to the floor, each base station with its field of view, and the play area. A device that loses tracking stays at its last position, faded. The device list shows each device's role, model, battery and tracking state. Open **Record** for this SteamVR preview and the saved calibrations. Switch to **View** to review the open take, **Edit** for its timeline or **Export** for output options. Switching tabs keeps the take open.

## Adjust a recorded take

Because a take keeps the raw data, all of this can be changed after recording, and the take solves again in a fraction of a second:

- the calibration and every calibration setting (see [calibration.md](calibration.md));
- the avatar the take is solved onto (see [avatars.md](avatars.md));
- tracker roles, which trackers are used, and a hand rotation correction;
- the body source (the IK solve, or VRChat's own IK where the take has it), the finger source, locomotion, VRChat's jumps, tracking-loss repairs and smoothing;
- the face sync offset, and where the take is placed in the world.

Each loss is listed with its reason and shown on the timeline. A repair can be switched off (the stretch is then kept as recorded), and you can mark a stretch as lost yourself. The marks are saved in the take.

For a VRChat take, enable *Include VRChat jumps* to keep in-game jumps.

- **Follow VRChat's avatar scaling** is off by default: the avatar keeps its own size through the whole take. Switched on, the body follows VRChat's size; exports then carry the scale as keys on the armature's top node (FBX) or a scale curve on the avatar root (`.anim`).
- **Avatar scale** forces a fixed size. Zero means the avatar's own size.

## See also

- [calibration.md](calibration.md): VRChat-style calibration, its settings and the calibration library.
- [avatars.md](avatars.md): the avatar library and solving a take onto another avatar.
- [scenes.md](scenes.md): arranging takes in the scene editor.
- [export-and-pipeline.md](export-and-pipeline.md): export formats, Blender and Unity.
- [file-formats.md](file-formats.md): what is inside a take and a scene file.
- [accuracy.md](accuracy.md): how the solve was measured against VRChat, and the results.
