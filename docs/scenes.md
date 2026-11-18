# Scenes

> **Availability:** the Avalonia Scene workspace, pose tool and layer recorder are not yet implemented. This page
> describes the scene engine and intended editing workflow; there is no alternative desktop scene editor in this repository.

A scene arranges takes on tracks, in the manner of a non-linear animation (NLA) editor: cut and join them, layer
body parts from different takes, add keyframes, re-record parts, and export the whole thing. **Takes are only
read.** Cutting, moving or deleting strips, layering, keyframing and re-recording never change a recording.

A scene is a readable JSON file (`.vrmscene`, by default in `Documents\VR Motion Recorder\Scenes`). It links its
takes by a path relative to the scene file and finds them again by their creation time if they move. The format is
described in [file-formats.md](file-formats.md).

Every take a scene uses is solved again on the actor's avatar in the background, with the take's own calibration
settings. A strip that plays a whole take gives exactly the take's own solve. A scene can have several actors,
each on its own avatar.

Every edit is one undo step (Ctrl+Z; Ctrl+Shift+Z or Ctrl+Y to redo).

## Tracks and strips

Put a take on a track by dragging it there. A strip plays a stretch of a take:

- **Move** it by dragging; it snaps to strip edges, the playhead, markers and frames (hold Ctrl to move freely).
  Drag up or down to change track.
- **Trim** by dragging an edge. **Slip** (change which part of the take plays) with Alt-drag.
- **K** cuts at the playhead; both halves still have the whole take behind them. **Del** removes a strip,
  **Shift+D** duplicates, **M** mutes. Each track can be muted, soloed and locked.
- **Space** plays, **I** and **O** set the scene range, **Shift+M** adds a marker, **F** frames everything.

Tracks play from bottom to top. A strip **replaces** what the tracks below give, or **adds** to it, with an
influence, a blend in and out, and a **body-part mask**: full body, upper or lower body, head and face, arms, legs,
a single limb, fingers, or face. That is how you combine the legs of one take with the arms of another.

## Transitions

**T** crossfades a strip into the next one on its track. The crossfade is centred on the cut and uses the takes'
data on both sides of it, so nothing is lost.

- **Placement.** The second strip is placed where the first one is: its centre of mass (or hips, or feet) lands on
  the same spot on the floor, facing as the hips and shoulders do, with heights unchanged. Optionally the turn
  also follows the direction the body travels.
- **Match score.** Each transition gets a score from 0 to 100: how far apart the joints are over the crossfade and
  how differently they move, with the hips and feet weighed most. It is shown on the cut, with a suggested length.
- **Find best cut** searches around the cut (2 s of the first take, 3 s of the second) and moves the cut to the
  best match, as one undo step. **Best entry** searches all of the second take. Both show a score map you can
  click.

On test takes, cuts 0.1 s apart inside one take score 83 and random cuts between takes 21. The score follows the
extra joint speed a crossfade adds (rank correlation 0.81). *Find best cut* raised 8 cuts from 31 to 55 on average,
with no jump of the centre of mass in any crossfade.

Drag the middle of a transition to move the cut by hand, and its edges to change its length.

## Input tracks: change what the solve is fed

An input track changes the *inputs* of the takes below it (the headset, controllers and trackers), and the body is
solved again from them. A 12 s strip solves in about 0.1 s in the background; while you drag, only the stretch
around the playhead is solved.

- **Move a device.** With the pose tool in device mode, click the headset, a controller or a tracker in the 3D
  view and drag it, or use the move and rotate gizmo. The keys go to a device-key strip, and the body follows
  through the solve. A key that moves the left foot tracker by 10 cm moves the solved foot 10.0 cm and nothing
  else.
- **Switch a device off and on** over time, with a ramp (**H**, or a weight per device). The body is then solved
  as if that tracker were not there.
- **Re-record parts.** Choose what to record again (arms, legs, head, upper body, whole body, face tracking, finger
  tracking, or any mix of devices), arm at the playhead and press the record hotkey in VR. The scene plays from a
  pre-roll with count-in beeps, and the new take lands on a new input track at exactly the scene time it was
  performed, placed by matching the headset. Only the chosen inputs are replaced: with re-recorded controllers the
  hands follow the new controllers while the legs stay on the first take's trackers. Each device of a re-recorded
  layer has its own weight.
- **Clean up a device** before the solve: smooth one tracker, or fill a gap in it.

Not possible yet: adding a tracker the take did not have, because it has no calibration to bind to.

## Keyframes

The pose tool's bone mode poses the avatar with IK: grab a hand or a foot, point a knee or an elbow, bend the spine
from the head or the chest, move the hips with the feet pinned, turn any bone. Keys go to a pose-key track, added
on top of the motion capture or replacing it.

| Key | Action |
|---|---|
| I | key the selection |
| Shift+I | key everything |
| Alt+I | delete the key |
| M | mirror the pose |
| Ctrl+C / Ctrl+V | copy / paste a pose |
| , / . | previous / next key |
| W / E | move / turn |

Key diamonds on a strip can be dragged to retime them (with Alt, that key and all later ones).

A **device take** is a take animated only by keys: a headset, controllers and trackers keyed in a standing pose and
solved by the same IK as a recording. Use it to animate from nothing.

## Cleanup

Strips and actors take modifiers:

- **Foot lock** finds the foot contacts, pins the foot and solves the leg. On two test takes the average slide in
  contacts went from 0.73 cm and 0.71 cm to 0.00 cm, with the other bones untouched.
- **Repair** interpolates or holds stretches you marked.
- **Smooth.**

## Export

A scene exports like a take, once per actor, plus one manifest that ties the actors together: frame rate, range,
markers, the track layout, and a camera list. Blender imports the manifest with the add-on (a collection per actor,
the strips as muted NLA tracks for reference), and Unity turns it into a Timeline with the Unity package. The
files, and how exact the round trip is, are in [export-and-pipeline.md](export-and-pipeline.md).

Camera work is not part of scenes yet. The scene file and the exporters keep a place for it.

## See also

- [how-it-works.md](how-it-works.md): the solve that scenes rerun.
- [file-formats.md](file-formats.md): the `.vrmscene` file.
- [ui-design.md](ui-design.md): the keyboard map the app keeps.
