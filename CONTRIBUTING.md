# Contributing

Thanks for helping. This page gets you from a clone to a running build, and lists the few rules the project keeps.

## Before you start

Check [docs/dev/](docs/dev/) first. Read [STATUS.md](docs/dev/STATUS.md) for the current action and [TODO.md](docs/dev/TODO.md) for pending work. Consult the guides relevant to your task. 

## Build and run

Use the [.NET SDK 10](https://dotnet.microsoft.com/download) or later as configured in `global.json`. Run these commands from the repository root.

```powershell
dotnet build VRMotionRecorder.slnx -c Release
dotnet test --solution VRMotionRecorder.slnx -c Release --no-build
dotnet run --project src/Vrmr.App -c Release
```

On Windows, `build.cmd` builds the app into `dist\VRMotionRecorder.exe`, and `build.cmd test` also builds and tests the solution first. Run `build.cmd avatar-meshes` to check the two Settings tests that load a real avatar's meshes. Other build output goes to `artifacts/`. Building, testing and reviewing saved takes work with SteamVR and VRChat closed.

Choose checks that exercise the change. For library tests, use `dotnet test --project tests/Vrmr.Tests/Vrmr.Tests.csproj -c Release`. Check the cross-platform CLI with `dotnet build src/Vrmr.Cli/Vrmr.Cli.csproj -c Release` and integration packaging with `python integrations/package.py dist`. For documentation-only edits, check the affected links and diff.

### Desktop app checks

Use a scratch data folder to try the desktop app in isolation. The app test project covers services and renders the window off-screen with Avalonia.Headless and Skia. [Launch options](docs/usage.md#launch-options)

```powershell
dotnet build src/Vrmr.App -c Release
dotnet run --project src/Vrmr.App -c Release -- --data artifacts/app-scratch --no-capture
dotnet test --project tests/Vrmr.App.Tests -c Release
```

- Set `VRMR_PRIVATE_DATA` for tests that need real takes. Put copies of `.vrmr` takes at the root of that folder or in its `Recordings` subfolder. Tests that load avatar meshes also need an `Avatars` subfolder. On Windows, the tests check the user-level setting if the process setting yields no takes.
- Set `VRMR_SHOTS` to an output folder to save PNGs from the screenshot tests. Keep personal takes and screenshots containing avatar assets or take names out of commits.

## Project boundaries

See the [architecture guide](docs/dev/architecture.md) for the project map and data flow. Keep shared libraries independent of the apps and keep the CLI buildable across platforms. Desktop UI work belongs in the Avalonia app. Read the [Blender](integrations/blender/README.md) or [Unity](integrations/unity/com.vrmr.motion-bridge/README.md) integration guide before changing its package.

- Design the main capture workflow for SteamVR alone, including avatar selection, tracker assignment, calibration, recording and export. VRChat OSC, HUMR and video player integration are optional. Keep device and settings access read-only and preserve explicitly enabled face replay.
- Preserve raw recordings and deterministic solves. Store the calibration inputs needed to reproduce standalone motion in the take. The take format may change during development without supporting earlier files.
- Keep capture, solving and drawing free of per-frame allocations. Run solves, file reads and exports off the UI thread, and redraw only when needed.
- Keep personal recordings, scenes, avatar assets, exports, screenshots of paid avatars, machine paths and identifiers out of commits. Check the diff even when `.gitignore` covers the file types.
- Change the source or generator for build and package outputs. Preserve Unity `.meta` files byte for byte unless the task requires a metadata change. Retain third-party headers and update [the notices](THIRD_PARTY_NOTICES.md) when adding third-party code.

## Checking solved motion

For changes that could affect motion, run `vrmr dump <takesDir> <outDir>` on copies of the same takes before and after the change. Compare the results with `python tools/compare_dumps.py <before> <after>`. Behaviour-preserving changes must produce `IDENTICAL`. For an intentional motion change, describe what changed, by how much, and how you measured it.

## Submitting changes

Run the applicable CLI checks and review changed code against the [code style guide](docs/dev/code-style.md), including the Avalonia conventions and solver exceptions. Add or update tests for behaviour changes and bug fixes.

Use [Conventional Commits](https://www.conventionalcommits.org/) with a short imperative subject. Explain the reason in the body when useful.

## Reporting a problem

Use the issue templates. For tracking or solve problems, a short take can help reproduce the issue. Review the [recording privacy guidance](SECURITY.md#sharing-a-recording) before attaching one.
