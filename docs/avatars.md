# Avatars

VRChat drives any humanoid avatar from the same headset, controllers and trackers, and every take keeps that raw data. So the app can solve a take onto **any humanoid skeleton**: the base model of the avatar you wore, or a different one, picked at any time. This page covers the avatar library, how to add a base model you own, how the app works out which base model you are wearing, and the face.

The repository and the download contain **no avatars**. You add the ones you own, and their files never leave your computer.

## The built-in mannequin

So that the app works before you add anything, it comes with a plain mannequin: a neutral humanoid skeleton of adult size (1.70 m) with a simple body, made for this project and built in code, with no model file. Takes are solved onto it when no base model matches, and you can choose it for any take. It has no face shapes, so recorded face tracking is kept in the take and exported as curves, but not shown on it.

## The avatar library

The library holds your base models. It lives in `Documents\VR Motion Recorder\Avatars` (the folder can be changed in Settings), one folder per entry:

| File                    | What                                                                                                                                                                          |
| ----------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `<Name>.vrmavatar.json` | The humanoid skeleton: rest pose and bone mapping exactly as Unity imports the model, the name patterns that identify the avatar, and the VRChat avatar ids you linked to it. |
| `<Name>_FT.fbx`         | The model with its face-tracking shapes merged in. The 3D view and the FBX export use it.                                                                                     |
| `textures\`             | The textures its materials use.                                                                                                                                               |

### Adding a base model you own

No Unity project is needed.

1. Choose **Add avatar…** in the library.
2. Pick the avatar's `.unitypackage`, or the `.zip` you downloaded with the package inside. If the avatar has a face-tracking add-on that comes as a BlendShare asset, pick that package too.
3. The app reads the packages in memory. Nothing in them is run. It takes the humanoid model and its import settings, picks the add-on's BlendShare asset made for exactly that version of the model (add-ons list a SHA-256 for each model version they support), merges the face shapes into a copy of the FBX, copies the textures and adds the entry.

A `.rar` archive has to be extracted first. A model that is already in a Unity project (an `.fbx` with its `.meta`) can be added as well.

If a shop download contains loose FBX files next to the package, use the package: the loose files are often a different export, and face-tracking add-ons are made for the packaged one.

This was checked on four commercial base models: in Blender every original mesh came out unchanged (vertices, polygons, vertex groups, the original shape keys in their order), with 110 to 126 face-tracking shapes added to the face mesh and 2 to a second mesh, and left and right shapes on the correct side.

### Linking a Unity project instead

If your avatar lives in a Unity project, you can link the project in Settings. The app then finds the model through the avatar's blueprint id, and reads face-tracking shapes from a BlendShare asset in the project that targets the model. The [Unity package](../integrations/unity/com.vrmr.motion-bridge/README.md) can also write the avatar's rest skeleton and blendshape names for the app (**Export Avatar Skeleton**), which makes face shapes map exactly. Both are optional.

## Which base model am I wearing?

The app decides in this order:

1. an avatar id you linked to a library entry;
2. a linked Unity project (the avatar's blueprint id leads to its model);
3. the avatar's name containing one of an entry's name patterns. The name comes from VRChat's OSCQuery, its log, or VRChat's own OSC configuration files (read-only). Patterns can be edited per entry and may be in any script;
4. the avatar's eye height within 3 % of an entry's. This is a guess and is shown as one: it separates base models of clearly different size, not two of nearly the same height;
5. otherwise your default entry (the one you chose, or else the one you use most), and failing that the mannequin.

For public avatars, renamed edits and anything else the app cannot place, pick the base model while you wear the avatar. The choice is remembered for that avatar id.

New takes store the base model of the avatar you wore and how it was matched.

## Solving a take onto another avatar

Per take, the avatar is *Auto* (the one the take was recorded with) or any library entry. Picking another entry **transfers the take**: the IK solve runs again on that skeleton, at that avatar's own size, from the same raw data. Where a take holds VRChat's own pose and you choose it as the body source, it is retargeted bone by bone from the recorded avatar's T-pose.

**Changing avatar during a take** splits it into stretches by avatar (reloading the same avatar, as a calibration does, is not a change). The solve keeps one skeleton for the take; the export can write one clip per avatar, each solved onto that avatar's base model.

**Size.** An avatar keeps its own size: the base model's skeleton, scaled when the avatar you wore was uploaded at another size than its base model (more than 2 % apart). Resizing the avatar inside VRChat is a separate matter, described in [Architecture](architecture.md).

## The face

Recorded face tracking drives the avatar's own face-tracking shapes. They come from:

1. the library's `<Name>_FT.fbx`, which already has them;
2. a BlendShare asset in a linked Unity project that targets the model;
3. failing both, the model's own blink, mouth and smile shapes, as a rough stand-in.

On the test models, 42 to 44 recorded face shapes drive real shapes of the model.

### The add-on's own face animator

Face-tracking add-ons do more in VRChat than set one shape per tracked value: their animator blends correction shapes (a tongue that comes out in steps, the lips shaping around it, a mouth that opens differently while smiling). When an avatar has its add-on's animator kept with it, the app plays that animator on the recorded values, so the face moves as it does in VRChat, in the 3D view and in every export (FBX, Unity `.anim`, face `.json`).

- **Adding an avatar** keeps the animator of the face-tracking add-on picked with it.
- **For an avatar already in the library**: Settings → Avatars, select it, **Face-tracking animator…**, and pick the add-on's package. Add-ons built on a separate face-tracking framework need that too: pick its package as well, or link the Unity project it is installed in (the app finds it there by itself).

The files are kept in a `face-animator` folder next to the model. The app reads the controllers, clips and parameter settings and evaluates their blend trees (1D, 2D, Direct) and state transitions itself; nothing in the files is run. Only face shapes are taken from it; bone animation (a physics-driven tongue, ears) is not.

**Export avatar FBX with its face shapes** writes a copy of a model's FBX with the BlendShare shapes added as ordinary blendshapes, everything else untouched, and its textures next to it. Use it when another tool should see the face-tracking face. It imports in Blender with identical meshes, skinning and armature.

## The avatar in the 3D view

With a base model, the 3D view shows the avatar's own meshes with their skin weights and textures, and the face driven by the recorded face tracking. The mesh is skinned with the FBX's bind poses, converted the way Unity's importer does it, so the preview deforms like the avatar in Unity (on a test avatar the bind skeleton is within 1.4 mm RMS of the rest skeleton).

- Single meshes can be hidden (an outfit piece, say), and the choice is remembered.
- Outfit changes made in Unity by build-time tools or prefab overrides are not part of the FBX, so the view shows the model's stock outfit.
- While recording, the view shows the skeleton only, to stay light.

## Your avatar files stay yours

- The library contains copies of files you bought. Do not share the library folder, and do not attach avatar files to bug reports.
- Exports that contain the avatar's meshes (the FBX with the model) are for your own use under the avatar's licence. The armature-only FBX and the Unity clips contain no meshes.
- A take stores the avatar's id and name, never its meshes.
- The repository's `.gitignore` blocks model, texture and package files so they cannot be committed by accident.

## See also

- [Architecture](architecture.md): the solve, and avatar resizing in VRChat.
- [export-and-pipeline.md](export-and-pipeline.md): what the exported FBX contains.
- [file-formats.md](file-formats.md): the avatar library entry in detail.
