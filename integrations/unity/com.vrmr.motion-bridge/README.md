# VR Motion Bridge for Unity

The Unity side of [VR Motion Recorder](https://github.com/miya-sans/vrc-motion-recorder). It takes what the app (or
its Blender add-on) exports and makes it play on your avatar: FBX clips become Humanoid, face and parameter curves
become clips, and a take or a whole scene becomes a Timeline you can render with physics on top.

It is an editor-only package. Nothing from it ends up in a build or in an uploaded avatar.

## Requirements

- Unity 2022.3 or later (the version VRChat avatar projects use works).
- The Timeline package (`com.unity.timeline`). Unity installs it with this package if the project does not have it.

## Install

In Unity: **Window → Package Manager → + → Add package from git URL…** and paste

```
https://github.com/miya-sans/vrc-motion-recorder.git?path=integrations/unity/com.vrmr.motion-bridge
```

To stay on one release, add its tag to the end, for example `#v1.0.0`.

Without git: download `com.vrmr.motion-bridge-<version>.tgz` from the
[releases page](https://github.com/miya-sans/vrc-motion-recorder/releases) and use **Add package from tarball…**.

If you used the bridge as loose scripts before (a folder `Assets/VRMotionBridge`), delete that folder first. The
package contains the same scripts with the same ids, and Unity cannot have both.

## What it does on its own

Whenever one of these files arrives in the project, the bridge sets it up. Nothing has to be clicked.

| File | What the bridge does |
|---|---|
| `<clip>.fbx` with `<clip>.vrmr.json` next to it | Imports the FBX as **Humanoid**. If the avatar's own model is in the project, its rig definition is copied, so bones map one to one. If it is not, the rig is rebuilt from the bone mapping and T-pose stored in the `.vrmr.json`. |
| the same FBX, once imported | Writes `<clip>_humanoid.anim`: the humanoid clip (muscles, body position, face blendshapes) as a stand-alone clip that plays on any Humanoid avatar. |
| `<clip>.vrmr.json` sent from Blender | Writes `<clip>_face.anim` (your edited face curves on the avatar's real blendshape names), `<clip>_params.anim` (raw avatar parameters as Animator parameter curves) and `<clip>_<camera>_camera.anim` (position, rotation and field of view). |
| `<scene>.vrmrscene.json` | Tells you in the Console that the scene is ready for **Create Timeline From Scene**. |

Export from the app straight into the project (the app's export folder set to your Unity project puts files under
`Assets/VRMotionRecorder`), or use **Send to Unity** in the Blender add-on.

## Menu: Tools → VR Motion Bridge

| Command | Select first | Result |
|---|---|---|
| **Create Timeline From Scene** | the exported folder, or its `.vrmrscene.json` | One Timeline with a Body and a Face track per actor, bound to the avatars in the open scene (matched by actor name, then by base model), markers as signals, and the exported camera. The bound Animators are set to *Always Animate*, so an avatar off camera keeps moving. Works for a single take too: a take export is a scene with one actor. |
| **Create Timeline From Scene (exact pose, Generic)** | as above | The same Timeline playing the transform clips (`_body.anim`). The bound Animators are switched to Generic (their Avatar field is cleared; Undo brings it back). Use it to render on the same avatar the scene was solved on. |
| **Create Timeline From Bundle** | a clip folder sent from Blender, its `.fbx` or `.vrmr.json` | A Timeline with Body and Face tracks on your avatar and the animated cameras. |
| **Rebuild Clips From Bundle** | as above | Writes the face, parameter and camera clips again, for example after you changed the avatar in the scene. |
| **Make Humanoid Clip (select avatar + clip)** | a generic clip (`_body.anim`) and your avatar in the scene | Bakes the clip into a humanoid `.anim` on that avatar. |
| **Export Avatar Skeleton for VR Motion Recorder** | your avatar in the scene | Writes the avatar's rest skeleton and blendshape names to `%APPDATA%\VRMotionRecorder\profiles`, where the app finds it. Optional: the app can also import a base model on its own. |

Enter Play mode to see physics components (hair, skirts, tails) react while a Timeline plays: only humanoid bones
are animated, so they simulate on top. Unity Recorder can render the result.

## Humanoid or exact pose?

- **Humanoid clips** (`_humanoid.anim`, the normal Timeline) retarget onto any Humanoid avatar at any scale. They go
  through Unity's muscle space, which is close but not exact: on the same avatar the body is typically within a few
  millimetres, and Unity's muscle limits can move the arms by more in some reaching poses.
- **Generic clips** (`_body.anim`, the "exact pose" Timeline) store transforms and reproduce the solved pose on the
  avatar they were made for. A Humanoid Animator ignores or garbles transform curves, which is why the exact-pose
  command switches the Animator to Generic.

Measured numbers are in the main repository's
[export documentation](https://github.com/miya-sans/vrc-motion-recorder/blob/main/docs/export-and-pipeline.md).

## Good to know

- An avatar emulator or gesture manager in the scene takes over the avatar's Animator in Play mode. Switch it off
  for Timeline renders.
- Outfit changes made by build-time tools are not part of an exported FBX. Play the clips on the avatar in your
  scene to get the outfit you see there.
- The bridge only writes inside the folder of the clip or scene it is working on, and (for **Export Avatar
  Skeleton**) into `%APPDATA%\VRMotionRecorder\profiles`. It sends nothing over the network.

## Licence

MIT, see [LICENSE.md](LICENSE.md).
