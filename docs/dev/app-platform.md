# Application structure and platform services

## Desktop application

`src/Vrmr.App` is the Avalonia app (assembly `VRMotionRecorder`, namespace `VRMR.App`). See the [system architecture](architecture.md) for the capture-to-export pipeline and the [UI style](ui-style.md), [tab UI design](tab-ui-design.md) and [development checklist](TODO.md) for desktop design and validation.

### Layers

Dependencies point downwards only.

#### Views

`Views/` contains XAML and code-behind for layout, keyboard shortcuts, file pickers and confirmation. It uses view-models and controls.

#### Controls

`Controls/` contains reusable controls such as `Section`, `FieldRow`, `NumberField`, `SegmentedControl`, `ToggleChip`, `StatusPill`, `IconButton`, `IconToggle`, `EmptyState`, `Icon` and `TimelineControl`. It uses Avalonia and plain types from services.

#### Themes

`Themes/` defines dark and light tokens in `Tokens.axaml`, icons in `Icons.axaml`, control themes in `Controls.axaml` and stock control styles and text classes in `Styles.axaml`.

#### View-models

`ViewModels/` owns view state and CommunityToolkit.Mvvm commands. It calls services and stays independent of Avalonia.

#### Viewport

`Viewport/` contains `IViewport`, `ViewScene`, the presenters and the placement tool. It uses services.

#### Service layer

`Services/` implements app operations on top of the engine libraries and is covered by unit tests.

#### Platform

`Platform/` provides the UI dispatcher and the global hotkey behind service interfaces.

#### Composition and startup

`Composition/AppHost.cs` creates the engine objects and services once and can use the other layers.

`Program.cs` parses the command line, enforces a single instance per data folder, sets up logging and starts Avalonia. `App.axaml.cs` builds the `AppHost`, the `ShellViewModel` and the `MainWindow`.

### Services

All in `Services/`, created by `AppHost`, none of them touches a control. Work that takes time runs on a worker and comes back through `IUiDispatcher.Post`; tests replace the dispatcher.

#### Settings and status

- `SettingsStore` wraps the engine settings, notifies listeners and saves on a worker after changes settle.
- `UiStateStore` remembers open sections, panel sizes, window position and theme in `ui-state.json`.
- `StatusService` shows progress and routine results through the status line and toasts.

#### Take library and editing

- `TakeLibrary` lists the recordings in the background, watches the folder, supports search, and can reveal takes or send them to the Recycle Bin.
- `TakeSession` loads, saves and reverts the open take, manages edits and undo/redo, and runs a cancellable background solve with progress after a 350 ms edit delay.
- `EditHistory` merges bursts of changes into snapshots and groups a drag into one undo step through `Gesture()`.

#### Playback and placement

- `PlaybackClock` moves the playhead in response to the viewport frame callback and user input.
- `PlacementService` handles the origin, 90° turns and exact placement values.
- `RoomService` keeps the last observed SteamVR room for takes with no recorded room.

#### Live sources and recording

- `LiveService` and `SourceStatus` expose live devices, the worn avatar and its base model, face values and source status pills.
- `RecordingController` starts and stops recording from the UI, global hotkey, avatar parameter or in-world HUMR recorder.
- `FaceReplayService` sends a playing take’s face parameters to VRChat when live sources are enabled.

#### Rules the services keep

- **Edits are copy-on-write.** `TakeSession.Edit(what, change)` applies the change to a copy of the `EditList` and swaps it in. A solve that is running never sees a half-made edit, and the UI thread never waits for a lock.
- **One undo step per drag.** A drag (timeline handle, gizmo, number field) opens `TakeSession.Gesture()` and disposes it at the end. During the drag edits pass `solve: false`; the drag's end calls `RequestSolve()`.
- **Nothing slow on the UI thread.** Opening and saving takes, listing the library, solving, exporting and importing avatars are `Task.Run` or a dedicated thread, reported through `StatusService`.
- **Timers.** The record-time, source-status and HUMR-end checks run while their sources are active. The viewport has its own [frame pacing](#frames-and-pacing).
- **Errors say what to do.** Every failure message names what failed, why in plain words, and the next step.

### The 3D view

`Viewport/IViewport` is what the app needs from a 3D view: a `ViewScene` to fill (the avatar's node poses, markers, play area, overlay text), `ViewOptions` (the chips), `Animating` / `FrameCap` / `FrameTick` for display-paced frames, camera framing, projection, and an `IViewportTool` that owns a gizmo. `TakePresenter` fills it from the solved clip at the playhead (`BakedClip.EvaluateNodes`, the take's devices, camera and room), `LivePresenter` from the live devices.

`Viewport/GpuView` is the view in the window: the GPU control of `Vrmr.Render` (`ViewportControl`, see [Renderer API](#renderer-api)) with `IViewport` on top. On every `SceneChanged()` it copies the `ViewScene` into a rented `ViewFrame` and submits it. It loads the avatar's mesh in the background (`MeshModel.Create`, the last two avatars stay loaded; the bones show until the mesh is there), applies the hidden-meshes list, fills the view's colours from the theme tokens, and lends the placement tool the renderer's `Gizmo`. `Animating`, `FrameCap` and `FrameTick` are the control's own. While recording the mesh is off and the bones are on. Nothing above `IViewport` knows which control draws.

## Platform APIs

What the app (and the CLI and the tests) call to find folders, look at the machine, load settings and get a body to solve onto. Everything here is UI-free and lives in the libraries; all types are in the `VRMR` namespace.

- `AppPaths` in `Vrmr.Core` (Where the app's own files are).
- `IMachine`, `Machine.Real`, `SandboxMachine` in `Vrmr.Core` (The machine, read-only (known folders, registry, processes)).
- `VrcFolders`, `SteamVRLocator`, `SteamLocator`, `BlenderInstalls`, `UnityProjects` in `Vrmr.Core` (VRChat's folders, SteamVR, Steam, Blender, Unity projects).
- `EnvironmentProbe` in `Vrmr.Core` (First-run guide and Settings status rows).
- `RecycleBin` in `Vrmr.Core` (Delete to the Recycle Bin).
- `EfficiencyMode` in `Vrmr.Core` (Windows efficiency mode).
- `Settings` in `Vrmr.Model` (User settings).
- `Mannequin`, `MannequinMesh` in `Vrmr.Model`, `Vrmr.Avatars` (The built-in avatar).
- `AvatarLibrary`, `AvatarEntry` in `Vrmr.Avatars` (Avatar library (built-in entry, aliases, face mesh)).
- `OpenVRSource.CopyDevices` in `Vrmr.Capture` (Live devices without allocations).

### Start-up

The desktop entry point parses the launch options, sets the active paths and opens the log in the selected log directory. The application host loads settings from those paths and constructs the avatar library. [Program](../../src/Vrmr.App/Program.cs) · [AppHost](../../src/Vrmr.App/Composition/AppHost.cs)

`AppPaths.Use` is the only global switch. Code that still uses the older statics (`Settings.AppDataDir`, `VrcPaths.LogDir`, `CalibrationPresets.Dir`, `PunchIn.FilePath`) reads the same `AppPaths.Current`, so one call moves everything.

### AppPaths

One object answers "where does this file go". Nothing else builds a path from the user profile.

```csharp
public sealed class AppPaths
{
    public static AppPaths Current { get; }                    // ForUser() until Use() is called
    public static void Use(AppPaths paths);
    public static AppPaths ForUser();                          // normal runs
    public static AppPaths ForMachine(IMachine machine);       // the per-user folders of another machine view
    public static AppPaths ForDataRoot(string root, bool isolated = true);
    public static AppPaths ForFolders(string appDataDir, string documentsDir, IMachine machine = null);  // a layout of your own
    public static AppPaths FromCommandLine(string[] args);     // understands --data <dir>

    public string DataRoot { get; }       // null on normal runs
    public bool Isolated { get; }         // true: nothing outside DataRoot is read
    public IMachine Machine { get; }      // what look-ups go through (see below)

    // settings, calibration library, logs
    public string AppDataDir { get; }               // %APPDATA%\VRMotionRecorder
    public string SettingsFile { get; }             // settings.json
    public string UiStateFile { get; }              // ui-state.json: the app's own window and panel state
    public string LogDir { get; }                   // AppDataDir\logs (one timestamped log per run)
    public string LogFile { get; }
    public string CalibrationPresetsDir { get; }    // calibrations\
    public string CalibrationSnapshotsDir { get; }  // calibrations\snapshots\
    public string UnityProfilesDir { get; }         // profiles\ (skeletons the Unity bridge exports)
    public string PunchInFile { get; }              // punchin.json
    public string RoomFile { get; }                 // steamvr_room.json
    public string WritableSteamVRManifestDir();     // where the app writes its SteamVR input manifest (used by capture only)

    // the user's work
    public string DocumentsDir { get; }             // Documents\VR Motion Recorder
    public string RecordingsDir { get; }            // default for Settings.RecordingsDir (= DocumentsDir)
    public string AvatarLibraryDir { get; }         // default for Settings.AvatarLibraryDir (Avatars\)
    public string ScenesDir { get; }                // Scenes\
    public string ExportsDir { get; }               // Exports\
}
```

Normal runs keep existing settings and user data locations. Logs are stored in the `AppDataDir\logs` subfolder.

`--data <dir>` (`ForDataRoot`) puts everything under one folder:

```
<dir>\AppData\      settings.json, calibrations\, profiles\, logs\, ...
<dir>\Documents\    takes, Avatars\, Scenes\, Exports\
<dir>\Machine\      what the sandboxed machine sees: LocalLow\VRChat\VRChat\, Local\openvr\, ...
```

With `isolated: true` (the default, and what `--data` uses) `Machine` is a `SandboxMachine` rooted at `<dir>\Machine`: VRChat's folder, SteamVR's path file, the registry and the process list are all empty unless a test puts something there. Nothing in the real profile is read or written, so a test launch cannot touch a user's takes, settings, VRChat or SteamVR. `isolated: false` keeps the app's files in the folder but looks at the real machine (a portable install).

`Settings.RecordingsDir` and `Settings.AvatarLibraryDir` remain the folders actually used (the user can change them); `AppPaths` only supplies their defaults.

### IMachine

The read-only view of the computer that every look-up goes through. The app never needs to implement it.

```csharp
public enum KnownFolder { AppData, LocalAppData, LocalAppDataLow, Documents, ProgramFiles, ProgramFilesX86, ProgramData, Temp }
public enum RegistryRoot { CurrentUser, LocalMachine, ClassesRoot }

public interface IMachine
{
    string Folder(KnownFolder folder);                                   // null when there is none
    object RegistryValue(RegistryRoot root, string key, string name);    // null when missing
    IReadOnlyList<string> RegistryValueNames(RegistryRoot root, string key);
    IReadOnlyList<string> RegistrySubKeys(RegistryRoot root, string key);
    bool IsProcessRunning(string name);                                  // "vrserver", without .exe
}

public static class Machine { public static IMachine Real { get; } }

public sealed class SandboxMachine : IMachine          // tests, --data, UI previews
{
    public SandboxMachine(string root);
    public string Root { get; }
    public SandboxMachine SetRegistry(RegistryRoot root, string key, string name, object value);
    public SandboxMachine SetRunning(string process, bool running = true);
}
```

Files are read with `System.IO` as usual: a sandbox works because every path starts from one of its known folders or registry values. To fake an installed program in a test, create its files under the sandbox and (if the look-up uses the registry) call `SetRegistry`.

### Looking things up

All of these are static, take an `IMachine`, never throw and never write.

```csharp
new VrcFolders(machine)            // .DataDir .LogDir .OscDir .Exists .NewestLog()
VrcPaths.DataDir / LogDir / OscDir // the same for AppPaths.Current.Machine

SteamLocator.InstallDir(machine)          // Steam itself, from its registry keys
SteamLocator.Libraries(machine)           // every library folder (libraryfolders.vdf)
SteamLocator.AppDir(machine, appId)       // an installed app's folder, in whichever library it is

SteamVRLocator.RuntimeDir(machine)        // openvrpaths.vrpath, then Steam's libraries
SteamVRLocator.SettingsFile(machine)      // steamvr.vrsettings (read-only)
SteamVRLocator.IsRunning(machine)         // is vrserver up

BlenderInstalls.Find(machine, userPath)   // List<BlenderInstall>, best first
    // BlenderInstall: Exe, Version, Source (User, Registry, InstallFolder, Launcher, Steam), Channel (Release, Daily, Experimental)

UnityProjects.Find(machine, extraPaths)   // List<UnityProjectInfo>, only explicitly linked paths
    // UnityProjectInfo: Path, Name, EditorVersion, Exists, HasBridge, FromHub
UnityProjects.HasBridge(projectDir)
UnityProjectSources.Discover(machine, source, folder) // explicit import from Unity Hub, ALCOM or Creator Companion
```

`userPath` is `Settings.BlenderPath`: a `blender.exe` or the folder that holds it; it wins over everything found. `BlenderLocator` (`Vrmr.Export`) still opens exports in Blender and uses the same list.

Unity project counts and rows use `Settings.UnityProjects`, including missing links. Startup and environment checks do not read manager project lists or automatically link projects. In Settings → Folders, choose **Folder**, **Unity Hub**, **ALCOM** or **VRChat Creator Companion** under **Add project from**. The manager picker reads its saved list only when opened and adds only selected projects. **Locate data folder…** supports custom manager locations. Imports do not synchronize subsequent manager changes.

Manager lookup reads Hub JSON, legacy `userProjects`, VCC LiteDB and ALCOM SQLite stores without updating them. Bridge indicators detect package dependencies or copied assembly definitions; they do not verify Unity compilation.

Adding projects indexes only the newly linked paths. Additions during indexing are queued and repeated pending requests are combined. Each linked project's row shows its own progress, unique avatar count and linked-avatar count; no combined scan result is shown. Existing projects are not reindexed when another project is added.

### EnvironmentProbe

For the first-run guide and the status rows in Settings. It reports and never changes anything. Run it on a worker (it reads the registry and lists a few folders: tens of milliseconds, more with many Unity projects).

```csharp
public enum ProbeState { Ok, Attention, Missing, Unknown }

public sealed class ProbeItem
{
    public string Id { get; }        // stable, see the table
    public string Title { get; }     // "SteamVR"
    public ProbeState State { get; }
    public string Summary { get; }   // one sentence, always set
    public string Action { get; }    // what the user should do, or null when nothing is needed
    public string Path { get; }      // the folder or file it is about, or null
    public bool Optional { get; }    // true: the app records without it
}

public sealed class ProbeOptions      // what the user has configured; Settings.ProbeOptions() fills it
{
    public string BlenderPath, RecordingsDir, AvatarLibraryDir;
    public List<string> UnityProjects;
}
// ids as constants: EnvironmentProbe.SteamVR, .VrChat, .VrChatOsc, .VrChatLog, .Blender, .Unity,
// .RecordingsFolder, .AvatarsFolder, .AppDataFolder

public sealed class EnvironmentReport
{
    public IReadOnlyList<ProbeItem> Items { get; }
    public ProbeItem this[string id] { get; }                 // null for an unknown id
    public bool CanRecord { get; }                            // VRChat's folder is there, OSC is not known to be off, the takes folder is writable
    public SteamVRStatus SteamVR { get; }                     // Installed, Running, RuntimeDir, SettingsFile
    public VrChatStatus VRChat { get; }                       // DataDir, Exists, NewestLog, NewestLogTimeUtc, OscEnabled (bool?), SendsHeadAndWrists (bool?), UserHeight
    public IReadOnlyList<BlenderInstall> Blender { get; }
    public IReadOnlyList<UnityProjectInfo> UnityProjects { get; }
}

public static class EnvironmentProbe
{
    public static EnvironmentReport Run(AppPaths paths, ProbeOptions options = null);
    public static EnvironmentReport Run(IMachine machine, AppPaths paths, ProbeOptions options = null);
}
```

- `steamvr` (optional). SteamVR is installed (the summary says whether it is running).
- `vrchat` (required). VRChat's data folder exists.
- `vrchat.osc` (required). VRChat's own setting says OSC is on (`Unknown` when the setting cannot be read).
- `vrchat.log` (optional). VRChat has written a log the app can follow.
- `blender` (optional). at least one Blender was found.
- `unity` (optional). at least one Unity project was found (`Attention` when none has the bridge).
- `folder.recordings` (required). the takes folder exists or can be created, and is writable.
- `folder.avatars` (required). the same for the avatar library folder.
- `folder.appdata` (required). the same for the settings folder.

Writable is tested with a temporary file that Windows deletes when the handle closes; a folder that does not exist yet is judged by its nearest existing parent and is not created.

OSC: VRChat keeps the switch in its own settings (registry). The probe reads it and never writes it; the guide can only tell the user where to turn it on (Action Menu → Options → OSC → Enabled).

### Settings

```csharp
public sealed class Settings
{
    public const int SchemaVersion = 2;
    public static Settings Load();                  // AppPaths.Current
    public static Settings Load(AppPaths paths);
    public void Save();                             // to the file it was loaded from
    public ProbeOptions ProbeOptions();
    public string BlenderPath;                      // "" = find it
    public string FaceMeshPath;                     // "" = the avatar's own face mesh (AvatarLibrary.FaceMeshFor)
    // ... the existing fields are unchanged
}
```

- Older files load as before; `SettingsMigrations` upgrades them step by step and is idempotent.
- Keys the running version does not know are kept and written back, so two versions can share one file.
- Folders that are still at their default are not written to the file: the file carries no machine paths until the user picks a folder.
- **Owned by the app:** window position, size and panel state live in `AppPaths.UiStateFile`. The retired WinForms keys (`wx`, `wy`, `ww`, `wh`, `wmax`, `nlaHeight`) have no application-facing fields; loaded values are preserved through the unknown-key round trip, and new settings files do not create them. `LastScene` remains a legacy field. Do not add UI state to `Settings`.

### Avatars

```csharp
public static class Mannequin                       // Vrmr.Model
{
    public const string Id = "mannequin";
    public const string Name = "Mannequin";
    public static AvatarProfile Profile();          // 1.70 m adult, T-pose, 54 humanoid bones (no jaw)
    public static bool Is(AvatarProfile profile);
}

public sealed class MannequinMesh                   // Vrmr.Avatars: built in code, no asset file
{
    public static MannequinMesh For(AvatarProfile mannequin);   // cached
    public Vec3[] Positions, Normals;               // rest pose, metres, Unity space
    public int[] Node;                              // one profile node per vertex (rigid: ball joints and segments)
    public int[] Triangles;                         // 3 per triangle, clockwise front faces (Unity)
    public uint[] Color;                            // 0xAARRGGBB per vertex: BodyColor, JointColor, MarkColor (eyes, nose)
    public AvatarProfile Profile;
    public void Skin(Vec3[] nodePos, Quat[] nodeRot, Vec3[] outPositions, Vec3[] outNormals, float scale = 1f);
}
```

The mannequin has no FBX, so `AvatarMeshLoader.ForProfile` returns null for it; a view that wants a mesh checks `Mannequin.Is(profile)` first and draws `MannequinMesh.For(profile)` (about 5,000 vertices, 10,000 triangles). Skinning is one node per vertex: `world = nodePos + nodeRot * restRot⁻¹ * (position − restPos) * scale`, with the profile's `RestWorldPos/Rot` and the node transforms of `BakedClip.EvaluateNodes`. `Skin` does exactly that on the CPU without allocating; a GPU view uploads `Positions`, `Normals`, `Node` and `Color` once and does the same in the vertex shader. `AvatarProfile.Generic(eyeHeight)`, the fallback the solver uses for a take with no avatar at all, is the same mannequin sized to that eye height.

```csharp
library.BuiltIn                         // the mannequin's AvatarEntry (BuiltIn == true; cannot be removed or saved)
library.All()                           // the user's entries, then the built-in one
library.Stored()                        // only the entries that are files in the library folder
library.Default                         // the user's choice, else their most used entry, else the mannequin
library.Match(...) / ForAvatar(...)     // never without an entry any more: the last fallback is the mannequin
library.FaceMeshFor(profile, settings.FaceMeshPath)   // the mesh path to key face shapes on
entry.Aliases                           // names that mean this base model (was the hard-coded table); editable
entry.FaceMesh                          // the mesh with the face-tracking shapes, found at import
```

Aliases replace the table of names that used to be in the code. At import they are taken from the package's own names (`AvatarAliases`): the model file, the prefabs that use the model, the package file; a name counts only when one meaningful word is left after dropping version numbers and generic words. An avatar worn in VRChat whose name contains an alias is matched to that entry. Entry files written before this keep working: their `match` list is read as aliases (and still written, so a build from before the change keeps its names too).

`FaceMeshFor` is what `Settings.FaceMeshPath == ""` means: the entry's `FaceMesh`, found by `FaceMeshFinder` at import (for entries imported earlier, on first use, by reading the FBX once: call it from a worker), else the mesh of the skeleton with the most face shapes, else `FaceMeshFinder.Fallback`. The exporters still take a plain mesh path; whoever fills their options calls `library.FaceMeshFor(profile, settings.FaceMeshPath)`.

`BlenderLocator.OpenExport(manifest, inUnityProject, settings.BlenderPath)` opens an export in the user's Blender, or the best one found.

### Capture helpers

```csharp
int n = openVr.CopyDevices(buffer, DeviceFilter.Connected);   // fills caller-owned Device objects, no allocation
int n = openVr.CopyDevices(buffer, DeviceFilter.TrackedBody); // HMD, controllers, trackers with a valid pose
```

`buffer` is a `OpenVRSource.Device[]` the caller keeps (64 is always enough: `OpenVRSource.MaxDevices`); null slots are filled on first use. `Devices()` and `Snapshot()` still exist for one-off use; a view that refreshes on a timer or per frame should use `CopyDevices`.

`OpenVRSource.OnHand` now hands out arrays that are reused for the next sample: a handler that keeps them copies them (the recorder does). `VrcLogTailer` can be pointed at any folder and driven by hand for tests: `new VrcLogTailer(folder, startThread: false)` and `Poll()`.

```csharp
EfficiencyMode.Update(windowActive, minimised, recording);    // call when any of the three changes
```

It asks Windows for efficiency mode (EcoQoS) while the window is in the background and nothing is being recorded, and leaves it otherwise. The capture threads opt out on their own, so live data keeps its timing while the UI and the workers are throttled. On systems without the feature the call does nothing.

`--no-capture` needs no API: the app simply does not call `Recorder.Start()`.

## Renderer API

`Vrmr.Render` (namespace `VRMR.Render`) holds the GPU renderer and `ViewportControl`, the Avalonia control the app puts in its window. This section describes the contract between the renderer and the app: what the app fills in, what the control offers, and the rules that keep it fast.

### How it works

- The control draws with OpenGL ES 3.0 (ANGLE on Windows) inside Avalonia's compositor. It is an ordinary control: other Avalonia controls can sit on top of it (view chips, camera buttons).
- The control redraws on a new frame, camera move, toggle, tool request or resize. [Frame pacing](#frames-and-pacing)
- The app never hands the renderer live objects. It fills a `ViewFrame` (plain data: poses, markers, text) and submits it. The renderer reads that frame only; it never calls into takes, clips or the recorder, and takes no locks on them.
- Everything is on the UI thread: `Submit`, the properties, tool callbacks and the draw itself (Avalonia runs `OnOpenGlRender` there). A frame costs well under a millisecond of CPU, because skinning, face shapes and rasterising happen on the GPU.
- Units: world space is Unity's (metres, +Y up, +Z forward, left-handed). Screen positions and sizes are in device-independent pixels, origin top-left, as Avalonia reports pointer positions. The control scales by the window's render scaling itself, so text and lines stay crisp at any DPI.

### Start-up: texture decoder

`SkinnedAvatar` (in `Vrmr.Avatars`) loads the avatar mesh and asks a host-provided decoder for its textures. Each host owns an `AvatarMeshLoader` with an immutable decoder and its own mesh cache:

```csharp
var meshes = new AvatarMeshLoader(VRMR.Render.SkiaTextureDecoder.Decode);
```

Share that loader between the host's avatar service and view. Without a decoder the mesh still loads and is drawn untextured; constructing another loader does not change the first loader's decoder or cache.

### Showing a frame

```csharp
// once per avatar (worker thread is fine): the mesh packed for the GPU
MeshModel model = MeshModel.Create(meshes.ForProfile(profile));

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

`RentFrame` / `Submit` recycle frame objects and their arrays, so a steady playback allocates nothing. A frame that was submitted stays on screen until the next one: camera moves and toggles redraw it without the app.

#### `ViewFrame`

- `ActorView AddActor(AvatarProfile profile, MeshModel model)`. Adds an actor and returns it with `NodePos` / `NodeRot` sized for the profile.
- `IReadOnlyList<ActorView> Actors`. The actors added so far.
- `int MainActor`. Index of the actor the camera follows and `FrameAll` frames (default 0).
- `List<Marker> Markers`. Devices, the VRChat camera, points and axes.
- `List<Vec3> PlayArea`. Corners of the play area on the floor; empty for none.
- `StringBuilder Overlay`, `OverlayRight`. Text in the top-left / top-right corner (`\n` starts a new line).
- `string EmptyText`. Shown in the centre when the frame has no actor and no device.
- `bool Recording`. Draws the recording frame around the view.

#### `ActorView`

- `AvatarProfile Profile`. The skeleton.
- `MeshModel Model`. The mesh, or null. Its profile must have the same node count.
- `Vec3[] NodePos`, `Quat[] NodeRot`. World pose per profile node (what `BakedClip.EvaluateNodes` writes).
- `float Scale`. Avatar scale of this pose (`BakedClip.ScaleAt`), default 1.
- `FaceWeights Face`. Face shape weights: `Set(name, weight)` with the recording's shape names (Unified Expressions or ARKit), or `Set(model.FaceChannel(name), weight)` with an index looked up once. Shapes under 0.01 cost nothing.
- `ActorStyle Style`. `Full` (default): mesh and bones as the view's Mesh / Skeleton toggles say. `Figure`: a plain skeleton in `FigureColor` (other actors of a scene), not affected by the toggles.
- `Rgba FigureColor`. Colour of a `Figure`.
- `bool[] HiddenParts`. Per `Model.Parts` index, true hides that mesh part; null shows all. Build it with `model.HiddenParts(names)`.

#### `Marker` (struct)

`Kind` (`MarkerKind`: `Axes`, `Camera`, `Point`, `Headset`, `Controller`, `Tracker`, `BaseStation`), `Pose`, `Color`, `Label` (may have two lines), `Faded` (last known pose of a device that is not tracked now), `Size` (model scale, 0 = 1), `NoDropLine`, and for base stations `FovLeft`, `FovRight`, `FovTop`, `FovBottom` (half angles in degrees, 0 = default).

#### `MeshModel`

`MeshModel.Create(SkinnedAvatar)` packs vertices, skin weights, face shapes and textures for the GPU; it can run on a worker thread. The GPU copy is made on first draw and freed when the model is disposed or garbage collected. `MeshModel.Create(MannequinMesh)` does the same for the built-in mannequin (one part, its vertex colours as a palette). `Parts` (name, triangles), `TriangleCount`, `VertexCount`, `FaceChannel(name)`, `HiddenParts(names)`, `Dispose()`. One model can be used by several actors and several views.

### `ViewportControl`

#### Frames and pacing

- `ViewFrame RentFrame()`. A cleared frame to fill.
- `void Submit(ViewFrame frame)`. Shows the frame and requests a draw.
- `ViewFrame Frame`. The frame on screen (read-only; tools use it for picking).
- `void RequestFrame()`. Redraw once (a tool changed its overlay, a setting changed).
- `bool Animating`. While true the control raises `FrameTick` once per display frame, limited by `FrameCap`. Set it while playing or showing live devices; clear it when paused.
- `double FrameCap`. Frames per second while animating (default 60; the app lowers it while recording).
- `event Action<TimeSpan> FrameTick`. Raised on the UI thread before a paced frame. Advance the playhead and call `Submit` here. With `Animating` off, draws come from changes and explicit requests.
- `ViewportStats Stats`. `FramesDrawn`, `LastFrameCpuMs`, `LastFrameAllocatedBytes`, `LastFrameGapMs`, `Triangles`.

#### View state

Styled properties, so they bind in XAML: `ShowMesh`, `ShowSkeleton`, `ShowDevices`, `ShowHeadset` (off by default), `ShowPlayArea`, `ShowBaseStations`, `ShowHint`, `Follow`, `Msaa` (0 = off, the default; 2, 4 or 8), `FrameCap`, `Animating`. `Theme` (`ViewportTheme`) carries every colour and the fonts: the app passes its theme tokens.

#### Camera

`Camera` (`ViewCamera`): `Yaw`, `Pitch`, `Distance`, `Fov`, `Target`; read-only `Eye`, `Right`, `Up`, `Forward`, `Focal`; `Project(world, out screen)`, `Ray(screen, out origin, out dir)`. After changing camera fields from code, call `RequestFrame()`. Built in: left drag orbits, right or middle drag pans (and ends following), the wheel zooms, a double click frames everything. `FrameAll()`, `bool FrameDevices()`, `event Action CameraChanged`.

#### Tools

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

`view.Tool` gets pointer and key events before the camera. `ViewPointer` has `Position` (`Vector2`), `Button`, `Modifiers` and `ClickCount`. `event Action<ViewportControl, OverlayList> DrawOverlay` draws without taking input.

`OverlayList` batches everything into the same GPU pass, in the order it was added:

- `Line(a, b, color, width, dash)`. A line with round caps; `a`, `b` as `Vec3` (world) or `Vector2` (screen). `LineDash.Solid`, `Dash`, `Dot`.
- `Polyline(points, color, width, closed)`. Connected screen-space lines.
- `Circle(center, radius, color, width, dash)`. A ring; centre as `Vec3` or `Vector2`, radius in pixels.
- `Disc(center, radius, fill)`. A filled circle.
- `Polygon(points, fill)`. A filled convex polygon (screen points).
- `Rect(rect, radius, fill, stroke)`. A rounded rectangle.
- `Text(at, text, color, size, align)`. Text at a screen point; `MeasureText(text, size)` returns its size.
- `Arrow(a, b, color, width)`. A line with an arrow head at `b`.

Hit-test helpers are in `HitTest`: `DistanceToSegment`, `DistanceToPolyline`, `InsidePolygon`, `NearestPoint`. `view.PickMarker(screen, radius)` returns the index of the marker under a point.

#### Gizmo

`Gizmo` is the move / turn handle the pose tool and the placement tool share: axis arrows, plane squares and a view-plane handle (move); axis rings and a view ring (turn); constant size on screen.

```csharp
gizmo.Center = hips; gizmo.Axes = Quat.Identity; gizmo.Rotate = turning;
GizmoPart hot = gizmo.Hit(view.Camera, e.Position);            // hover and press
gizmo.Begin(view.Camera, hot, e.Position);                     // press
gizmo.Delta(view.Camera, e.Position, fine, out Vec3 move, out Quat rot);   // drag: change since Begin
gizmo.End();
gizmo.Draw(view.Camera, overlay, hot, view.Theme);             // in the tool's Draw
```

What the drag edits (a placement, device keys, bone keys) stays in the app.

### Renderer rules

- Do not keep or change a frame after `Submit`. Rent a new one for the next change.
- Do not draw from a timer. Set `Animating` and use `FrameTick`, or call `Submit` / `RequestFrame` when data changes.
- Keep per-frame work in `FrameTick` allocation-free: reuse strings for labels, append numbers to `Overlay`.
- Screenshots of a user's avatar are personal data: keep them out of the repository.
