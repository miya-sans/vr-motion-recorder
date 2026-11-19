# Platform API

What the app (and the CLI and the tests) call to find folders, look at the machine, load settings and get a body to solve onto. Everything here is UI-free and lives in the libraries; all types are in the `VRMR` namespace.

| Need                                                        | Type                                                                               | Project                      |
| ----------------------------------------------------------- | ---------------------------------------------------------------------------------- | ---------------------------- |
| Where the app's own files are                               | `AppPaths`                                                                         | `Vrmr.Core`                  |
| The machine, read-only (known folders, registry, processes) | `IMachine`, `Machine.Real`, `SandboxMachine`                                       | `Vrmr.Core`                  |
| VRChat's folders, SteamVR, Steam, Blender, Unity projects   | `VrcFolders`, `SteamVRLocator`, `SteamLocator`, `BlenderInstalls`, `UnityProjects` | `Vrmr.Core`                  |
| First-run guide and Settings status rows                    | `EnvironmentProbe`                                                                 | `Vrmr.Core`                  |
| Delete to the Recycle Bin                                   | `RecycleBin`                                                                       | `Vrmr.Core`                  |
| Windows efficiency mode                                     | `EfficiencyMode`                                                                   | `Vrmr.Core`                  |
| User settings                                               | `Settings`                                                                         | `Vrmr.Model`                 |
| The built-in avatar                                         | `Mannequin`, `MannequinMesh`                                                       | `Vrmr.Model`, `Vrmr.Avatars` |
| Avatar library (built-in entry, aliases, face mesh)         | `AvatarLibrary`, `AvatarEntry`                                                     | `Vrmr.Avatars`               |
| Live devices without allocations                            | `OpenVRSource.CopyDevices`                                                         | `Vrmr.Capture`               |

## Start-up

```csharp
var paths = AppPaths.FromCommandLine(args);   // "--data <dir>" or the per-user folders
AppPaths.Use(paths);                          // once, before anything reads settings
AppLog.Init(paths.LogDir);
var settings = Settings.Load();               // migrates older files, keeps keys it does not know
var library = new AvatarLibrary(settings);    // always has the built-in mannequin
```

`AppPaths.Use` is the only global switch. Code that still uses the older statics (`Settings.AppDataDir`, `VrcPaths.LogDir`, `CalibrationPresets.Dir`, `PunchIn.FilePath`) reads the same `AppPaths.Current`, so one call moves everything.

## AppPaths

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
    public string LogDir { get; }                   // = AppDataDir (vrmr.log)
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

Normal runs keep the locations the app has always used, so existing data is found as before.

`--data <dir>` (`ForDataRoot`) puts everything under one folder:

```
<dir>\AppData\      settings.json, calibrations\, profiles\, vrmr.log, ...
<dir>\Documents\    takes, Avatars\, Scenes\, Exports\
<dir>\Machine\      what the sandboxed machine sees: LocalLow\VRChat\VRChat\, Local\openvr\, ...
```

With `isolated: true` (the default, and what `--data` uses) `Machine` is a `SandboxMachine` rooted at `<dir>\Machine`: VRChat's folder, SteamVR's path file, the registry and the process list are all empty unless a test puts something there. Nothing in the real profile is read or written, so a test launch cannot touch a user's takes, settings, VRChat or SteamVR. `isolated: false` keeps the app's files in the folder but looks at the real machine (a portable install).

`Settings.RecordingsDir` and `Settings.AvatarLibraryDir` remain the folders actually used (the user can change them); `AppPaths` only supplies their defaults.

## IMachine

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

## Looking things up

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

## EnvironmentProbe

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

| `Id`                | Ok when                                                                         | Optional |
| ------------------- | ------------------------------------------------------------------------------- | -------- |
| `steamvr`           | SteamVR is installed (the summary says whether it is running)                   | yes      |
| `vrchat`            | VRChat's data folder exists                                                     | no       |
| `vrchat.osc`        | VRChat's own setting says OSC is on (`Unknown` when the setting cannot be read) | no       |
| `vrchat.log`        | VRChat has written a log the app can follow                                     | yes      |
| `blender`           | at least one Blender was found                                                  | yes      |
| `unity`             | at least one Unity project was found (`Attention` when none has the bridge)     | yes      |
| `folder.recordings` | the takes folder exists or can be created, and is writable                      | no       |
| `folder.avatars`    | the same for the avatar library folder                                          | no       |
| `folder.appdata`    | the same for the settings folder                                                | no       |

Writable is tested with a temporary file that Windows deletes when the handle closes; a folder that does not exist yet is judged by its nearest existing parent and is not created.

OSC: VRChat keeps the switch in its own settings (registry). The probe reads it and never writes it; the guide can only tell the user where to turn it on (Action Menu → Options → OSC → Enabled).

## Settings

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

## Avatars

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

## Capture helpers

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
