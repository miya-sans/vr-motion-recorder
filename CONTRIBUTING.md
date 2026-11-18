# Contributing

Thanks for helping. This page gets you from a clone to a running build, and lists the few rules the project keeps.

## Build and run

You need the [.NET SDK 10](https://dotnet.microsoft.com/download) or later. Nothing else: packages are restored from
nuget.org on the first build.

```
git clone https://github.com/miya-sans/vrc-motion-recorder
cd vrc-motion-recorder
dotnet build VRMotionRecorder.slnx -c Release
dotnet test --solution VRMotionRecorder.slnx -c Release
dotnet run --project src/Vrmr.App -c Release
```

`build.cmd` does the build and the tests in one go on Windows. All build output goes to `artifacts/`.

SteamVR and VRChat are not needed to build, test or open existing takes. To record you need VRChat with OSC on;
SteamVR adds controllers, trackers and finger tracking.

## Where things are

`docs/architecture.md` describes the projects and how data flows through them. In short:

| Folder | What |
|---|---|
| `src/Vrmr.Core`, `Vrmr.Model` | Maths, JSON, file formats, settings |
| `src/Vrmr.Fbik`, `Vrmr.Solve` | The IK solver and everything that turns a take into a pose |
| `src/Vrmr.Capture` | OpenVR, OSC, the VRChat log, the recorder |
| `src/Vrmr.Avatars`, `Vrmr.Scene`, `Vrmr.Export` | Avatar files, the scene editor's model, exporters |
| `src/Vrmr.Render`, `Vrmr.App` | The 3D view and the desktop app |
| `src/Vrmr.Cli` | `vrmr`, the command-line tool |
| `integrations/blender`, `integrations/unity` | The Blender add-on and the Unity editor package |
| `tests` | Unit and end-to-end tests |

## Rules

1. **No personal or licensed data in the repository.** No takes (`.vrmr`), scenes, avatar files (FBX, textures,
   `.unitypackage`, BlendShare assets), exports, screenshots that show a paid avatar, or paths and ids from your own
   machine. `.gitignore` blocks the usual file types; check `git status` before you commit anyway.
2. **Changes that are not meant to alter the motion must not alter it.** Before and after your change, run
   `vrmr dump <folder with copies of your takes> <outDir>` and compare the two dumps with
   `python tools/compare_dumps.py <before> <after>`. It must say `IDENTICAL`. If your change is meant to alter the
   motion, say in the pull request what changed, by how much, and how you measured it.
3. **The app only reads from VRChat and SteamVR.** It never sends input, never takes VRChat's ports, and never
   writes VRChat's or SteamVR's settings.
4. **Keep it light.** The app runs next to VRChat. No per-frame allocations in capture, solving or drawing; nothing
   slow on the UI thread; no timer that only redraws. `docs/architecture.md` has the details.
5. **Third-party code keeps its notice.** If you bring in code under another licence, add it to
   `THIRD_PARTY_NOTICES.md` and keep the file headers.

## Style

- Rider: use **Full Cleanup** on changed code. The shared `VRMotionRecorder.sln.DotSettings` selects that profile;
  `.editorconfig` supplies the repository's syntax and formatting rules and takes precedence over Rider settings.
  Enable EditorConfig support and review the cleanup diff before committing. The baseline is Rider 2026.3;
  member layout and unspecified cleanup options use its built-in defaults. `dotnet format` supports the standard
  EditorConfig rules but does not replace Rider's full cleanup or member rearrangement.
- C#: the code style is in `.editorconfig`. Public types and members get a one-line `///` summary that says what
  they are for, not how they work.
- Tests: one behaviour per test, named as a sentence (`SaveThenLoadKeepsParametersAndMetadata`).
- UI: follow `docs/ui-design.md`. Colours and sizes come from the theme tokens.
- Commits: a short imperative subject, then why the change was needed.

## Reporting a problem

Use the issue templates. For anything about tracking or the solve, the most useful attachment is a short take that
shows the problem, but only share one if you are comfortable with it: a take contains your motion, your avatar's id
and name, and the world name.
