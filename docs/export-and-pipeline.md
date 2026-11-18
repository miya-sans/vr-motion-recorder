# Export and pipeline

The app exports takes and scenes as files that Blender and Unity open on their own. The Blender add-on and the Unity package (see [integrations](../integrations/README.md)) make the round trip comfortable, and are optional.

The pipeline the app is built for: record in VRChat, edit animation and cameras in Blender, render in Unity with your avatar's physics on top. Every step can be skipped.

```
            ┌─────────────── FBX, manifest ──────────────▶ Blender (edit body, face, cameras)
VR Motion   │                                                   │ Send to Unity
Recorder ───┤                                                   ▼
            └── FBX, .anim, manifest into the project ────▶ Unity (clips, Timeline, physics, render)
```

> **This page is being updated.** Export is changing: the app will write one Unity humanoid clip per take (body and face in one `.anim`) by itself, so Unity will no longer be needed to produce it. The table below marks the rows this affects. Until that lands, the humanoid clip is made by the Unity package as described here.

## Where exports go

Any folder you choose. Without a choice it is `Documents\VR Motion Recorder\Exports`, or the folder `Assets\VRMotionRecorder` of a linked Unity project, where the Unity package takes over. By default each take gets its own folder inside it, and every file name starts with the take's name.

Exports are written from the take **as placed** (see "Placing a take" below) and at the export frame rate (60 by default). Exporting opens nothing: you import the files yourself.

## Files

`<name>` is the take's name, or `<scene>_<actor>` for a scene. Each kind of file can be switched on or off.

| File                    | What it is                                                                                                                                                                                                                                                    | Written by                                                     | Notes                                                                                                                                        |
| ----------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------- |
| `<name>.fbx`            | **Your avatar with the take on it**: the avatar's own model (every mesh, skin weights, materials, face-tracking shapes), the body animation on its real bones, the face on its own shape keys. Optionally the VRChat camera path and the trackers as empties. | app                                                            | Needs a base model in the avatar library or a linked Unity project. Without one: the armature and a small mesh that carries the face shapes. |
| `<name>_armature.fbx`   | **Armature only**: exact bone names and rest pose with the take, no meshes and no face. Camera and trackers as above.                                                                                                                                         | app                                                            | Small, and safe to pass on: it contains nothing of the avatar's meshes.                                                                      |
| `textures\<avatar>\`    | The model's colour textures, copied once per avatar and referenced from the FBX by relative path.                                                                                                                                                             | app                                                            | See "Textures".                                                                                                                              |
| `<name>_body.anim`      | Unity clip on the avatar's real transform paths (body and face).                                                                                                                                                                                              | app                                                            | Exact on a **Generic** Animator. Not for Humanoid Animators (see "Humanoid or Generic").                                                     |
| `<name>_face.anim`      | Unity clip with the face blendshape curves only.                                                                                                                                                                                                              | app                                                            |                                                                                                                                              |
| `<name>_params.anim`    | The raw avatar parameters as Animator parameter curves, to replay face tracking through the avatar's own animator layers.                                                                                                                                     | app                                                            | Optional.                                                                                                                                    |
| `<name>_humanoid.anim`  | Unity **Humanoid** clip: muscles, body position and face blendshapes in one clip. Plays on any Humanoid avatar.                                                                                                                                               | Unity package today; **the app, once the export change lands** | The row that is changing.                                                                                                                    |
| `<name>.face.json`      | Face shapes, raw parameters and the avatar link.                                                                                                                                                                                                              | app                                                            | Read by the Blender add-on.                                                                                                                  |
| `<name>.vrmr.json`      | Sidecar for Unity: the avatar's humanoid bone mapping and Unity rest pose (T-pose).                                                                                                                                                                           | app, and the Blender add-on on **Send to Unity**               | The Unity package sets the FBX up as Humanoid with it. `<name>_armature.vrmr.json` does the same for the armature FBX.                       |
| `<name>.vrmrscene.json` | Manifest: frame rate, frame range, files, actors, markers, cameras. A take export is a scene with one actor.                                                                                                                                                  | app                                                            | **Import VRMR Scene** in Blender and **Create Timeline From Scene** in Unity read it.                                                        |
| `<name>_osc.json`       | Parameter replay as OSC messages.                                                                                                                                                                                                                             | app                                                            | Optional, for OSC replay tools.                                                                                                              |
| `output_log_<name>.txt` | A HUMR-compatible log of the solved body.                                                                                                                                                                                                                     | app                                                            | Optional, for tools that load HUMR logs.                                                                                                     |

The JSON files are described in [file-formats.md](file-formats.md).

**Split at avatar changes** writes one set of files per avatar when you changed avatar during the take, each solved onto that avatar's base model.

### Textures

Every material of the model FBX gets its colour texture as the FBX's diffuse texture: its own, else the texture the avatar library keeps under the material's name, else its emission texture. The images are copied once per avatar into `textures\<avatar>` and referenced by relative path, so Blender and Unity both show them wherever the folder is moved. (Unity ignores textures embedded in an FBX until you extract them, which is why they are files.) Textures that are already in the Unity project you export into are used where they are. Links to textures that do not exist, and an emission map that only repeats the colour texture, are dropped from the copy.

Checked on two test models in Blender and in Unity 2022.3 with the folder moved: every material that has a texture shows it.

### Humanoid or Generic in Unity

- **Humanoid** (`_humanoid.anim`) retargets onto any Humanoid avatar at any scale. It goes through Unity's muscle space. On the same avatar the result is within 0.9 mm on average, 95 % within 3.2 mm, worst 14 mm at a thumb tip (Unity's thumb muscle limits); on a second test avatar 0.8 mm / 2.9 mm / 16 mm, with the face exact. Keys are reduced by a cubic fit (muscles within 0.001, body 0.1 mm, face 0.2 %). About 35 MB for a 39 s take.
- **Generic** (`_body.anim`) stores transforms. On a Generic Animator it is within 0.3 mm on average and 1.3 mm at worst. On a **Humanoid** Animator Unity ignores or garbles transform curves (0.6–1.9 m off, measured in Unity 2022.3), so use the humanoid clip there. A Generic clip stores local positions, so a differently scaled root scales the motion with it; humanoid clips are scale-free.

### Size

For a 51 s take, the armature-only FBX is 3.9 MB against 22 MB with a test avatar's model, with the same motion within 0.1 mm.

## Placing a take

One placement moves and turns the whole solved take (body, VRChat camera, devices, the recorded play area) after the solve. The solve itself does not change.

- **Origin** puts the avatar on the origin at the playhead: hips above the origin, feet at standing height on the floor, facing forward (+Z in Unity).
- **Turn** by 90° left or right about the hips, type exact values (Unity axes, centimetres and degrees), or drag a move and turn gizmo on the hips in the 3D view. **Reset** goes back to the take as solved.

Every take export carries the placement. Checked on a copy (Origin, a turn, then export): the FBX in Blender matches the placed take to 0.000 mm (bones, camera, trackers), the Generic clip to 0.3 mm (its key reduction), and the manifest's camera to 0.01 mm. Scenes place their strips themselves and ignore a take's placement.

## Workflows

### App → Blender

1. Export with an FBX switched on.
2. In Blender (4.2 to 5.3), either use *File → Import → FBX*, or install the [add-on](../integrations/blender/README.md) and use **Import VRMR Scene** on `<name>.vrmrscene.json`.

With the add-on you get:

- your avatar: every mesh skinned to its armature (fingers included), its textures, its face shape keys keyed by the recorded face tracking;
- the VRChat camera as a camera (VRChat sends only its pose, so the lens is a placeholder), and the take's frame range and frame rate;
- every face-tracking and avatar parameter as an animated custom property on the armature, in the same action as the bones, so cutting, retiming or layering the body keeps the face in step.

Measured on copies of two test takes (39 s and 51 s) in Blender 5.3: all meshes skinned and moving (13 meshes with 36,415 vertices, and 21 meshes with 65,734), bones within 0.001 mm of the app, face shape keys exact, camera within 0.007 mm. Opening takes about 12 s.

### Edit in Blender, then Blender → Unity

Edit the body, fingers, face parameter curves and cameras (lens animation included). Then set the Unity project folder in the add-on's panel and press **Send to Unity**. It writes `Assets/VRMotionRecorder/<clip>/<clip>.fbx` (the armature) and `<clip>.vrmr.json` (parameters, cameras, and the avatar's bone mapping and T-pose kept from the import). In Unity the [package](../integrations/unity/com.vrmr.motion-bridge/README.md) then builds:

- a **Humanoid** clip and `<clip>_humanoid.anim`. The rig is built from the FBX's own rest pose with your avatar's bone mapping and T-pose, so Blender's axis conventions and a bind pose that differs from the T-pose do not matter;
- `<clip>_face.anim`: your edited face curves on the face mesh's real blendshape names, read from the avatar in the open scene;
- `<clip>_params.anim`: the raw parameters as Animator parameter curves;
- `<clip>_<camera>_camera.anim`: position, rotation and field of view per frame;
- on request (**Create Timeline From Bundle**), a Timeline with body and face tracks on your avatar and an animated camera.

Measured end to end on a test avatar (app → Blender edit → Unity humanoid), identical in Blender 4.2 and 5.3: the body is within 1.9 mm of the recording, and parameter edits and the camera (position, rotation, field of view) arrive exactly. Blender's own import of the app's FBX is exact (0.01 mm or better, shape keys exact), and retargeting onto the avatar's original model is 0.0 mm off.

### App → Unity

Export into the Unity project (`Assets\VRMotionRecorder`). The package sets the FBX up as Humanoid: when the avatar's base model is in the project its rig definition is copied; otherwise the rig is rebuilt from the sidecar's bone mapping and T-pose, with default muscle limits. Then select the take's folder and run **Tools → VR Motion Bridge → Create Timeline From Scene**: body and face bound to the avatar in the open scene, plus the VRChat camera.

Enter Play mode and physics components simulate on top, because only humanoid bones are animated. Unity Recorder can render it.

### Scenes

A scene exports the same files once per actor (`<scene>_<actor>…`) and one `<scene>.vrmrscene.json` with the frame rate, range, markers, actors, the track layout, and a camera list that is reserved for later.

- **Blender:** **Import VRMR Scene** gives a collection per actor, the frame range, frame rate and markers, and the strips as muted NLA tracks for reference. Bones are within 0.008 mm of the app.
- **Unity:** **Create Timeline From Scene** gives one Timeline with body and face per actor, bound to the avatars in the open scene by actor name and then by base model, with markers as signals. The humanoid clips retarget onto any avatar, but Unity's muscle space can move the arms in some poses (up to 25 cm for a few frames of a reaching layer in one test). **Create Timeline From Scene (exact pose, Generic)** plays the transform clips instead: 1.5 mm on the same avatar.

## Good to know

- An avatar emulator or gesture manager in the Unity scene takes over the avatar's Animator in Play mode. Switch it off for Timeline renders.
- An avatar that is off camera stops animating unless its Animator is set to *Always Animate*. The Timeline commands set this for the avatars they bind.
- Outfit changes made in Unity are not part of the FBX. Play the clips on the avatar in your scene to get the outfit you see there.
- The face can also be replayed into VRChat while a take plays: the app then sends the recorded face parameters to VRChat's OSC input on your own computer, so you can watch the replay on your avatar in game. This is off unless you switch it on, and it is the only thing the app ever sends to VRChat.

## See also

- [file-formats.md](file-formats.md): the manifest, the sidecar and the face file in detail.
- [scenes.md](scenes.md): the scene editor.
- [avatars.md](avatars.md): where the avatar's model comes from.
- [accuracy.md](accuracy.md): how close the solve itself is to VRChat.
