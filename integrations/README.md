# Integrations

The app exports files that Blender and Unity can open on their own. These two add-ons make the round trip
comfortable. Both are optional, live outside the .NET solution, and are written in their host's language.

- The Blender add-on imports takes and scenes, retargets motion and sends edits to Unity. [Blender requirements and installation](blender/README.md#install)
- The Unity bridge sets up Humanoid rigs and creates clips and Timelines from exports. [Unity requirements and installation](unity/com.vrmr.motion-bridge/README.md#install)

The two share one version number (the Blender add-on's `bl_info` and manifest, the Unity package's `package.json`),
because they exchange files with each other. The file formats they read are described in
[File formats](../docs/dev/file-formats.md), the whole pipeline in
[docs/export-and-pipeline.md](../docs/export-and-pipeline.md).

## Packaging

```
python integrations/package.py dist
```

writes the Blender extension zip, the single-file add-on and the Unity package tarball into `dist/`. It needs only
Python; CI runs it on every push and the release workflow attaches the results to the release.

## Rules for changes here

- Unity: every file and folder in the package needs its `.meta`, and a `.meta`'s `guid` never changes once it has
  shipped. Projects reference scripts by that id.
- Blender: keep `vrmr_bridge.py` a single file that works from Blender 4.2 on, and keep its version equal to the
  manifest's.
- `VRMotionBridgeFace.cs` mirrors `src/Vrmr.Avatars/FaceMapping.cs`. Change them together.
- No avatar names, paths or ids from your own machine in code, comments or messages.
