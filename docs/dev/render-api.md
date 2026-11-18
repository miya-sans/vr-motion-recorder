# The 3D view: `Vrmr.Render`

`Vrmr.Render` (namespace `VRMR.Render`) holds the GPU renderer and `ViewportControl`, the Avalonia control the app puts
in its window. This page is the contract between the renderer and the app: what the app fills in, what the control
offers, and the rules that keep it fast.

## How it works

- The control draws with OpenGL ES 3.0 (ANGLE on Windows) inside Avalonia's compositor. It is an ordinary control:
  other Avalonia controls can sit on top of it (view chips, camera buttons).
- **It draws only when something changed.** A new frame, a camera move, a toggle, a tool that asks for it, a resize.
  There is no timer. Nothing changed means no frame and no CPU.
- The app never hands the renderer live objects. It fills a `ViewFrame` (plain data: poses, markers, text) and
  submits it. The renderer reads that frame only; it never calls into takes, clips or the recorder, and takes no
  locks on them.
- Everything is on the UI thread: `Submit`, the properties, tool callbacks and the draw itself (Avalonia runs
  `OnOpenGlRender` there). A frame costs well under a millisecond of CPU, because skinning, face shapes and
  rasterising happen on the GPU.
- Units: world space is Unity's (metres, +Y up, +Z forward, left-handed). Screen positions and sizes are in
  device-independent pixels, origin top-left, as Avalonia reports pointer positions. The control scales by the
  window's render scaling itself, so text and lines stay crisp at any DPI.

## Start-up: texture decoder

`SkinnedAvatar` (in `Vrmr.Avatars`) loads the avatar mesh and asks a host-provided decoder for its textures. Call
this once before the first avatar loads:

```csharp
VRMR.Render.SkiaTextureDecoder.Install();   // sets SkinnedAvatar.TextureDecoder
```

Without a decoder the mesh still loads and is drawn untextured.

## Showing a frame

```csharp
// once per avatar (worker thread is fine): the mesh packed for the GPU
MeshModel model = MeshModel.Create(SkinnedAvatar.ForProfile(profile));

// whenever the pose changes (UI thread)
ViewFrame f = view.RentFrame();                    // recycled, already cleared
ActorView a = f.AddActor(clip.Profile, model);     // model may be null: skeleton only
clip.EvaluateNodes(frame, a.NodePos, a.NodeRot);   // world position / rotation per profile node
a.Scale = clip.ScaleAt(frame);
foreach (var kv in clip.Face) a.Face.Set(kv.Key, kv.Value[frame]);
f.Markers.Add(new Marker { Kind = MarkerKind.Tracker, Pose = pose, Color = color, NoDropLine = true });
f.Overlay.Append("IK solve   frame ").Append(frame).Append('/').Append(clip.Frames - 1);
view.Submit(f);                                    // the frame now belongs to the view: do not touch it again
```

`RentFrame` / `Submit` recycle frame objects and their arrays, so a steady playback allocates nothing. A frame that
was submitted stays on screen until the next one: camera moves and toggles redraw it without the app.

### `ViewFrame`

| Member | Meaning |
|---|---|
| `ActorView AddActor(AvatarProfile profile, MeshModel model)` | Adds an actor and returns it with `NodePos` / `NodeRot` sized for the profile. |
| `IReadOnlyList<ActorView> Actors` | The actors added so far. |
| `int MainActor` | Index of the actor the camera follows and `FrameAll` frames (default 0). |
| `List<Marker> Markers` | Devices, the VRChat camera, points and axes. |
| `List<Vec3> PlayArea` | Corners of the play area on the floor; empty for none. |
| `StringBuilder Overlay`, `OverlayRight` | Text in the top-left / top-right corner (`\n` starts a new line). |
| `string EmptyText` | Shown in the centre when the frame has no actor and no device. |
| `bool Recording` | Draws the recording frame around the view. |

### `ActorView`

| Member | Meaning |
|---|---|
| `AvatarProfile Profile` | The skeleton. |
| `MeshModel Model` | The mesh, or null. Its profile must have the same node count. |
| `Vec3[] NodePos`, `Quat[] NodeRot` | World pose per profile node (what `BakedClip.EvaluateNodes` writes). |
| `float Scale` | Avatar scale of this pose (`BakedClip.ScaleAt`), default 1. |
| `FaceWeights Face` | Face shape weights: `Set(name, weight)` with the recording's shape names (Unified Expressions or ARKit), or `Set(model.FaceChannel(name), weight)` with an index looked up once. Shapes under 0.01 cost nothing. |
| `ActorStyle Style` | `Full` (default): mesh and bones as the view's Mesh / Skeleton toggles say. `Figure`: a plain skeleton in `FigureColor` (other actors of a scene), not affected by the toggles. |
| `Rgba FigureColor` | Colour of a `Figure`. |
| `bool[] HiddenParts` | Per `Model.Parts` index, true hides that mesh part; null shows all. Build it with `model.HiddenParts(names)`. |

### `Marker` (struct)

`Kind` (`MarkerKind`: `Axes`, `Camera`, `Point`, `Headset`, `Controller`, `Tracker`, `BaseStation`), `Pose`, `Color`,
`Label` (may have two lines), `Faded` (last known pose of a device that is not tracked now), `Size` (model scale, 0 = 1),
`NoDropLine`, and for base stations `FovLeft`, `FovRight`, `FovTop`, `FovBottom` (half angles in degrees, 0 = default).

### `MeshModel`

`MeshModel.Create(SkinnedAvatar)` packs vertices, skin weights, face shapes and textures for the GPU; it can run on a
worker thread. The GPU copy is made on first draw and freed when the model is disposed or garbage collected.
`MeshModel.Create(MannequinMesh)` does the same for the built-in mannequin (one part, its vertex colours as a palette).
`Parts` (name, triangles), `TriangleCount`, `VertexCount`, `FaceChannel(name)`, `HiddenParts(names)`, `Dispose()`.
One model can be used by several actors and several views.

## `ViewportControl`

### Frames and pacing

| Member | Meaning |
|---|---|
| `ViewFrame RentFrame()` | A cleared frame to fill. |
| `void Submit(ViewFrame frame)` | Shows the frame and requests a draw. |
| `ViewFrame Frame` | The frame on screen (read-only; tools use it for picking). |
| `void RequestFrame()` | Redraw once (a tool changed its overlay, a setting changed). |
| `bool Animating` | While true the control raises `FrameTick` once per display frame, limited by `FrameCap`. Set it while playing or showing live devices; clear it when paused. |
| `double FrameCap` | Frames per second while animating (default 60; the app lowers it while recording). |
| `event Action<TimeSpan> FrameTick` | Raised on the UI thread before a paced frame: advance the playhead and `Submit` here. No tick and no draw happen while `Animating` is false. |
| `ViewportStats Stats` | `FramesDrawn`, `LastFrameCpuMs`, `LastFrameAllocatedBytes`, `LastFrameGapMs`, `Triangles`. |

### View state

Styled properties, so they bind in XAML: `ShowMesh`, `ShowSkeleton`, `ShowDevices`, `ShowHeadset` (off by default),
`ShowPlayArea`, `ShowBaseStations`, `ShowHint`, `Follow`, `Msaa` (0 = off, the default; 2, 4 or 8), `FrameCap`,
`Animating`. `Theme` (`ViewportTheme`) carries every colour and the fonts: the app passes its theme tokens.

### Camera

`Camera` (`ViewCamera`): `Yaw`, `Pitch`, `Distance`, `Fov`, `Target`; read-only `Eye`, `Right`, `Up`, `Forward`,
`Focal`; `Project(world, out screen)`, `Ray(screen, out origin, out dir)`. After changing camera fields from code,
call `RequestFrame()`. Built in: left drag orbits, right or middle drag pans (and ends following), the wheel zooms,
a double click frames everything. `FrameAll()`, `bool FrameDevices()`, `event Action CameraChanged`.

### Tools

```csharp
public interface IViewportTool
{
    bool PointerPressed(ViewportControl view, in ViewPointer e);   // true: the tool takes this drag
    void PointerMoved(ViewportControl view, in ViewPointer e);     // hover, and the drag it took
    void PointerReleased(ViewportControl view, in ViewPointer e);
    bool KeyDown(ViewportControl view, Key key, KeyModifiers modifiers);   // true: handled
    void Draw(ViewportControl view, OverlayList overlay);          // called for every drawn frame
}
```

`view.Tool` gets pointer and key events before the camera. `ViewPointer` has `Position` (`Vector2`), `Button`,
`Modifiers` and `ClickCount`. `event Action<ViewportControl, OverlayList> DrawOverlay` draws without taking input.

`OverlayList` batches everything into the same GPU pass, in the order it was added:

| Call | Draws |
|---|---|
| `Line(a, b, color, width, dash)` | A line with round caps; `a`, `b` as `Vec3` (world) or `Vector2` (screen). `LineDash.Solid`, `Dash`, `Dot`. |
| `Polyline(points, color, width, closed)` | Connected screen-space lines. |
| `Circle(center, radius, color, width, dash)` | A ring; centre as `Vec3` or `Vector2`, radius in pixels. |
| `Disc(center, radius, fill)` | A filled circle. |
| `Polygon(points, fill)` | A filled convex polygon (screen points). |
| `Rect(rect, radius, fill, stroke)` | A rounded rectangle. |
| `Text(at, text, color, size, align)` | Text at a screen point; `MeasureText(text, size)` returns its size. |
| `Arrow(a, b, color, width)` | A line with an arrow head at `b`. |

Hit-test helpers are in `HitTest`: `DistanceToSegment`, `DistanceToPolyline`, `InsidePolygon`, `NearestPoint`.
`view.PickMarker(screen, radius)` returns the index of the marker under a point.

### Gizmo

`Gizmo` is the move / turn handle the pose tool and the placement tool share: axis arrows, plane squares and a
view-plane handle (move); axis rings and a view ring (turn); constant size on screen.

```csharp
gizmo.Center = hips; gizmo.Axes = Quat.Identity; gizmo.Rotate = turning;
GizmoPart hot = gizmo.Hit(view.Camera, e.Position);            // hover and press
gizmo.Begin(view.Camera, hot, e.Position);                     // press
gizmo.Delta(view.Camera, e.Position, fine, out Vec3 move, out Quat rot);   // drag: change since Begin
gizmo.End();
gizmo.Draw(view.Camera, overlay, hot, view.Theme);             // in the tool's Draw
```

What the drag edits (a placement, device keys, bone keys) stays in the app.

## Rules

- Do not keep or change a frame after `Submit`. Rent a new one for the next change.
- Do not draw from a timer. Set `Animating` and use `FrameTick`, or call `Submit` / `RequestFrame` when data changes.
- Keep per-frame work in `FrameTick` allocation-free: reuse strings for labels, append numbers to `Overlay`.
- Screenshots of a user's avatar are personal data: keep them out of the repository.
