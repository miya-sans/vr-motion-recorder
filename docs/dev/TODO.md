# Long-term roadmap

The goal is a standalone SteamVR recorder and editor. The immediate milestone, implementation gaps and hardware checks are in [Project status](STATUS.md). Early take formats may change as the project develops.

## Scene editing

- Bring non-destructive cuts, crossfades and keyframe-only animation into the Avalonia Scene workspace. The [scene engine](../../src/Vrmr.Scene/SceneOps.cs) already edits strips that reference takes. Transition alignment and scoring already use centre of mass, facing and joint motion. The desktop work is to let a user preview candidate cuts, choose one and export the result.
- Expose additive pose keys, animated tracker offsets and IK targets. The engine already supports limb and spine posing, input layers and [partial-body recording](../../src/Vrmr.Scene/PunchIn.cs). The pose tool and layer recorder still need desktop controls.
- Let users preview and adjust foot locks and tracking repairs. Measure planted-foot travel in HUMR, the solve, FBX and Unity Humanoid playback with foot IK on and off, on both the recorded and a second avatar. Use those comparisons to locate the source of sliding before changing the solver.

## Capture and motion quality

- Develop controller-specific hand and head offset profiles as measurements become available for more hardware.
- Use optional HUMR recordings to measure per-bone error and evaluate changes to the arm, spine and knee solve.
- Add optional VRChat video player support. Detect song changes in the log, turn timestamps into clip boundaries within a continuous recording, and play the reference video alongside a take using yt-dlp with an adjustable sync offset.

## Virtual camera crane

Camera export and import already carry recorded motion. The [scene model](../../src/Vrmr.Scene/SceneModel.cs) reserves space for authored cameras. The proposed workflow is to key a camera path, use an optional in-world station to move the performer along it, capture handheld motion relative to the path, then smooth and steer that motion with corrective keys.

## Before a public release

Review the provenance and structure of the VRChat-matched arm, spine and knee solver code before making the repository public.

## Open question for later: collision and physics

Revisit physics if editing real takes reveals a need. The [pose rig](../../src/Vrmr.Scene/ScenePose.cs) already estimates centre of mass using human segment masses. Possible experiments include generating collision shapes for intersection cleanup or adding rigid bodies with soft joint limits for posing selected limbs and the spine. [Ragdoll reference](https://youtu.be/yY6vck8SuCY)
