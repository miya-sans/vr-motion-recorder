# The desktop app: `Vrmr.App`

`src/Vrmr.App` is the Avalonia app (assembly `VRMotionRecorder`, namespace `VRMR.App`). This page says how it is put together, the rules its code follows, and where the Scene workspace plugs in. The look and behaviour it implements are in [`docs/dev/ui-design.md`](ui-design.md); missing desktop features are tracked in the [roadmap](TODO.md#scene-editing), with validation records and remaining checks in the [development checklist](TODO.md#previous-validation).

## Layers

Dependencies point downwards only.

| Folder                   | What                                                                                                                                                                          | May use                         |
| ------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------- |
| `Views/`                 | XAML and code-behind: layout, the keyboard map, file pickers, the one confirm dialog                                                                                          | view-models, controls           |
| `Controls/`              | Reusable controls (`Section`, `FieldRow`, `NumberField`, `SegmentedControl`, `ToggleChip`, `StatusPill`, `IconButton`, `IconToggle`, `EmptyState`, `Icon`, `TimelineControl`) | Avalonia, services' plain types |
| `Themes/`                | `Tokens.axaml` (colours, type, spacing; dark and light), `Icons.axaml`, `Controls.axaml` (control themes), `Styles.axaml` (stock controls and text classes)                   | –                               |
| `ViewModels/`            | State for views, commands (CommunityToolkit.Mvvm). No Avalonia types                                                                                                          | services                        |
| `Viewport/`              | The seam to the 3D view (`IViewport`, `ViewScene`), presenters that fill it, the placement tool                                                                               | services                        |
| `Services/`              | Everything the app *does*, UI-free and unit-tested                                                                                                                            | the engine libraries            |
| `Platform/`              | The few OS-specific pieces behind service interfaces (UI dispatcher, global hotkey)                                                                                           | –                               |
| `Composition/AppHost.cs` | The composition root: creates the engine objects and every service once                                                                                                       | all of the above                |

`Program.cs` parses the command line, enforces a single instance per data folder, sets up logging and starts Avalonia. `App.axaml.cs` builds the `AppHost`, the `ShellViewModel` and the `MainWindow`.

### Command line

- `--data <dir>` (or `--data=<dir>`): portable mode. Settings, calibrations, logs, takes, the avatar library and exports all live under that folder (`AppPaths.ForDataRoot`: `AppData\`, `Documents\`, `Machine\`), and look-ups of VRChat, SteamVR and the registry see an empty sandbox. Tests and screenshots always use this.
- `--no-capture`: the recorder's sources are never started (no OSC ports, no OSCQuery, no SteamVR, no log following, no global hotkey). Takes can be opened, edited and exported.
- Anything else is an error: a typo must never open the real data by accident.

## Services

All in `Services/`, created by `AppHost`, none of them touches a control. Work that takes time runs on a worker and comes back through `IUiDispatcher.Post`; tests replace the dispatcher.

| Service                       | What it does                                                                                                                                                        |
| ----------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `SettingsStore`               | The engine's `Settings` plus change notification; saves a moment after the last change, off the UI thread                                                           |
| `UiStateStore`                | What the window remembers (`ui-state.json`): open sections, panel sizes, window place, theme                                                                        |
| `StatusService`               | The status line, running work with progress, toasts. Routine results go here, never into a dialog                                                                   |
| `TakeLibrary`                 | The takes in the recordings folder: background listing, a folder watcher, search, Recycle Bin, reveal                                                               |
| `TakeSession`                 | The open take: load, the edit list (copy-on-write), undo / redo, save, revert, and the background solve with progress and cancellation (350 ms after the last edit) |
| `EditHistory`                 | Undo and redo over snapshots: bursts merge, a `Gesture()` makes a whole drag one step                                                                               |
| `PlaybackClock`               | The playhead; it only moves when the view's frame callback or the user moves it                                                                                     |
| `PlacementService`            | Origin, the 90° turns and exact placement values                                                                                                                    |
| `RoomService`                 | The SteamVR room as last seen, for takes that did not record their own                                                                                              |
| `LiveService`, `SourceStatus` | What the sources report now: devices, the avatar worn and its base model, face values, the pill states                                                              |
| `RecordingController`         | Start / stop from the button, the global hotkey, an avatar parameter, or the in-world HUMR recorder                                                                 |
| `FaceReplayService`           | Sends a playing take's face parameters to VRChat (only with live sources on)                                                                                        |

### Rules the services keep

- **Edits are copy-on-write.** `TakeSession.Edit(what, change)` applies the change to a copy of the `EditList` and swaps it in. A solve that is running never sees a half-made edit, and the UI thread never waits for a lock.
- **One undo step per drag.** A drag (timeline handle, gizmo, number field) opens `TakeSession.Gesture()` and disposes it at the end. During the drag edits pass `solve: false`; the drag's end calls `RequestSolve()`.
- **Nothing slow on the UI thread.** Opening and saving takes, listing the library, solving, exporting and importing avatars are `Task.Run` or a dedicated thread, reported through `StatusService`.
- **No timer that only redraws.** While playing or live, the view's `FrameTick` drives everything. The few timers that exist (the record time, the source pills, the HUMR end check) run only while their source is on and change text, not frames.
- **Errors say what to do.** Every failure message names what failed, why in plain words, and the next step.

## The 3D view

`Viewport/IViewport` is what the app needs from a 3D view: a `ViewScene` to fill (the avatar's node poses, markers, play area, overlay text), `ViewOptions` (the chips), `Animating` / `FrameCap` / `FrameTick` for display-paced frames, camera framing, projection, and an `IViewportTool` that owns a gizmo. `TakePresenter` fills it from the solved clip at the playhead (`BakedClip.EvaluateNodes`, the take's devices, camera and room), `LivePresenter` from the live devices.

`Viewport/GpuView` is the view in the window: the GPU control of `Vrmr.Render` (`ViewportControl`, see [`render-api.md`](render-api.md)) with `IViewport` on top. On every `SceneChanged()` it copies the `ViewScene` into a rented `ViewFrame` and submits it. It loads the avatar's mesh in the background (`MeshModel.Create`, the last two avatars stay loaded; the bones show until the mesh is there), applies the hidden-meshes list, fills the view's colours from the theme tokens, and lends the placement tool the renderer's `Gizmo`. `Animating`, `FrameCap` and `FrameTick` are the control's own. While recording the mesh is off and the bones are on. Nothing above `IViewport` knows which control draws.

## The Scene seam

The scene (NLA) editor is ported separately into `Views/Scene`, `ViewModels/Scene`, `Controls/Nla` and `Services/Scene`. What it gets from the shell:

- **The flag.** `Composition/AppFeatures.SceneWorkspace` is false until the editor is ported: the switch in the top bar is hidden (`ShellViewModel.SceneAvailable`) and `WorkspaceIndex` cannot leave 0. Set it to true to work on the scene workspace.
- **The workspace switch.** `ShellViewModel.WorkspaceIndex` / `Workspace` and the `WorkspaceChanged` event. The Takes workspace releases the view (tool, animation, playback) in `TakesWorkspaceViewModel.SetActive(false)`.
- **Its places in the window** (`MainWindow.axaml`): `SceneWorkspaceView` over the 3D view (a placeholder with an empty state today), and three empty hosts shown only in the Scene workspace: `SceneTransportHost`, `SceneTimelineHost` (the resizable bottom area) and `SceneInspectorHost`.
- **Shared services from `AppHost`:** `Library` (takes; drag sources come from `LibraryViewModel.Items`), `Settings` / `SettingsStore`, `UiState`, `Status` (status line, progress, toasts), `Avatars`, `Recorder`, `Live`, `Recording` (its `Started` / `Stopped` events), `Rooms`, and the view through `MainWindow.Viewport`.
- **Shared building blocks:** `EditHistory` (the same undo model), `PlaybackClock`, `NumberField.GestureSource` (set it on the scene inspector's root so number drags become one undo step), `ThemeBrushes` for custom-drawn controls, and every control in `Controls/`.

The Takes workspace only opens a finished recording while it is active (`RecordingController.Stopped`); the scene workspace subscribes to the same event for punch-in recording.

## Conventions

- **Views contain no colour, size or font literals**: only token keys (`{DynamicResource …}`) and the text classes of `Styles.axaml` (`title`, `secondary`, `caption`, `note`, `warning`, `error`, `tabular`).
- **Sections and rows.** A panel is a stack of `Section`s (each with a `StateKey`, so it remembers being folded); rarely used settings go into a level-2 `Section` named "Advanced", closed by default. One setting is one `FieldRow`. Numbers are `NumberField`s with their unit.
- **Writing.** Sentence case; verbs on buttons; plain words ("IK solve", "tracker"); units shown; tooltips say what a control does to the result. An icon-only button always has a tooltip (it also becomes its accessible name).
- **Compiled bindings** everywhere (`x:DataType` on every view and template).
- **View-models** use `[ObservableProperty]` and `[RelayCommand]`; they never reference a control. What needs a window (file pickers, the confirm dialog) is a `Func<…>` property the view sets.

## Building and testing

```
dotnet build src/Vrmr.App -c Release
dotnet run --project src/Vrmr.App -c Release -- --data <scratch folder> --no-capture
dotnet test --project tests/Vrmr.App.Tests -c Release
```

`tests/Vrmr.App.Tests` holds the service tests and headless UI tests (Avalonia.Headless with Skia: the real window, drawn off screen).

- Tests that need real takes are skipped unless `VRMR_PRIVATE_DATA` points to a folder with copies of your own takes (`.vrmr`) and an `Avatars` folder. Takes are personal data and never part of the repository.
- With `VRMR_SHOTS` set to a folder, the screenshot tests save each state as a PNG there. Screenshots of your own avatars and take names stay on your machine.
