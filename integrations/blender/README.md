# VR Motion Recorder Bridge for Blender

A Blender add-on for [VR Motion Recorder](https://github.com/miya-sans/vr-motion-recorder). It imports exported
takes and scenes with everything in step (your avatar's meshes, armature, face shape keys, the recorded face and
avatar parameters, cameras), helps you move the motion onto your own rig, and sends your edits on to Unity.

Works in Blender 4.2 LTS up to 5.3. It uses slotted actions where Blender has them and the older action API on 4.2.

## Install

Pick one. Both give the same add-on.

- **As an extension (recommended).** Download `vrmr_bridge-<version>.zip` from the
  [releases page](https://github.com/miya-sans/vr-motion-recorder/releases) and drag it into the Blender window, or
  use *Edit → Preferences → Get Extensions → ⌄ → Install from Disk…*.
- **As a single file.** Take `vrmr_bridge.py` from this folder (or from a release), then
  *Edit → Preferences → Add-ons → ⌄ → Install from Disk…*, pick the file and tick the add-on.

The tools appear in the 3D viewport's sidebar (press **N**), tab **VRMR**.

## What you can do

| Tool | What it does |
|---|---|
| **Import VRMR Scene** | Opens a `.vrmrscene.json` (a take export or a scene export): one collection per actor with the avatar and its action, the frame range, frame rate and markers, cameras with keyed position, rotation and focal length, and the scene's tracks and strips as muted NLA tracks for reference. |
| **Import VRMR FBX** | Opens one exported FBX and sets the frame range. Use it when you only have the FBX. |
| **Send to Unity** | Writes the edited armature (body and fingers), the face and parameter curves and the cameras into `Assets/VRMotionRecorder/<clip>` of the Unity project you picked. The [Unity package](../unity/com.vrmr.motion-bridge/README.md) turns them into clips and a Timeline. |
| **Retarget to Active Armature** | Bakes the imported motion onto another armature. Bones are matched by name and the rest poses are compared in world space, so different bone rolls are fine. |
| **Copy Face From Carrier Mesh** | Copies shape key animation from the imported face mesh to the active mesh, matching names and the usual Unified Expressions and ARKit aliases. |
| **Load Face .json** | Keys the active mesh's shape keys from an exported `.face.json`. |

Import options: *Face/avatar parameters as armature curves* (on by default), *Scene strips as NLA tracks*, *Cameras*.

### Face and avatar parameters travel with the body

Every recorded face-tracking and avatar parameter becomes an animated custom property on the armature, in the same
action as the bones. Cutting, retiming or layering the body in the NLA keeps the face in step, and you can edit face
curves in the Graph Editor. Parameter names longer than Blender's 63-byte limit are shortened with a hash and go
back to Unity under their full name.

### The camera

VRChat sends only the pose of its camera, so the imported camera has a placeholder lens. Animate the lens in
Blender: **Send to Unity** carries position, rotation and field of view per frame.

## How exact is it

Checked on test takes in Blender 4.2 and 5.3: imported bones are within 0.01 mm of the app's solve and the face
shape keys are exact. More numbers are in [docs/export-and-pipeline.md](../../docs/export-and-pipeline.md).

## Files in this folder

| File | What |
|---|---|
| `vrmr_bridge.py` | The add-on. One file, no dependencies outside Blender. |
| `blender_manifest.toml` | The extension manifest. Its `version` must match `bl_info["version"]` in the script. |

`python integrations/package.py <outdir>` builds the extension zip (the script becomes `__init__.py`, with the
manifest and the licence next to it). The release workflow runs it.

## Privacy

The add-on reads the files you pick and writes into the Unity project folder you choose. It makes no network
connections.

## Licence

MIT, see [LICENSE](../../LICENSE).
