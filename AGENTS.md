# For AI agents

## Working approach

- Keep changes focused on the requested task. Follow nearby conventions and avoid unrelated refactoring or formatting.
- Give each method a clear responsibility. Reduce deep nesting and extract helpers when they improve clarity.
- Use static methods when behavior does not depend on instance state.
- Avoid speculative abstractions; solve the current requirements with code that is easy to navigate and modify.
- Keep dependencies explicit and respect project boundaries. Avoid adding dependencies without a concrete need.
- Make failures easy to diagnose and behavior easy to test. Add or update meaningful tests for behavior changes and bug fixes.
- Follow `.editorconfig` and existing formatting. Favor readable expressions over rigid rules about line length or one operation per line.
- When committing, use [Conventional Commits](https://www.conventionalcommits.org/), with a short imperative subject and a body explaining why when useful.

## Repository layout and boundaries

Read [CONTRIBUTING.md](CONTRIBUTING.md) for contribution rules and [docs/architecture.md](docs/architecture.md) for dependencies and data flow.

- `src/Vrmr.Core` and `src/Vrmr.Model`: maths, shared utilities, file formats, skeletons, and settings.
- `src/Vrmr.Fbik` and `src/Vrmr.Solve`: IK and the conversion of raw takes into poses. Preserve the derived solver's formatting and licence notices.
- `src/Vrmr.Capture`: OpenVR, OSC, VRChat logs, and recording.
- `src/Vrmr.Avatars`, `src/Vrmr.Scene`, and `src/Vrmr.Export`: avatar loading, scene evaluation, and exporters.
- `src/Vrmr.Render` and `src/Vrmr.App`: GPU rendering and the Avalonia app. Follow [docs/ui-design.md](docs/ui-design.md) for UI changes.
- `src/Vrmr.App.WinForms`: the existing WinForms app, frozen during the Avalonia migration. Consult [docs/STATUS.md](docs/STATUS.md) before UI work.
- `src/Vrmr.Cli`: command-line tools. Keep shared libraries independent of the apps and preserve the CLI's cross-platform build.
- `integrations/blender` and `integrations/unity`: separate integrations outside the .NET solution; read their READMEs before changing them.
- `tests/Vrmr.Tests`, `tests/Vrmr.App.Tests`, and `tests/Vrmr.Render.Tests`: library, app, and renderer tests.

## Project constraints

- Preserve raw recordings. Scene edits must not change recorded inputs, and solving must remain deterministic for the same inputs.
- Follow the existing read-only interaction with VRChat and SteamVR; preserve explicitly enabled face replay behavior.
- Avoid per-frame allocations in capture, solve inner loops, and drawing. Keep solves, file reads, and exports off the UI thread, and redraw only when needed.
- Keep personal recordings, scenes, avatar assets, exports, and machine-specific paths or identifiers out of commits. See `.gitignore` and `CONTRIBUTING.md`.
- Do not hand-edit generated build or package output in `artifacts/`, `bin/`, `obj/`, `dist/`, `publish/`, or `TestResults/`; change the source or generator instead.
- Preserve Unity `.meta` files byte for byte unless the task requires changing asset metadata. Retain third-party headers and update `THIRD_PARTY_NOTICES.md` when adding third-party code.

## Build and validation

Use .NET SDK 10 or later, as configured in `global.json`. Run commands from the repository root.

```powershell
dotnet build VRMotionRecorder.slnx -c Release
dotnet test --solution VRMotionRecorder.slnx -c Release --no-build
```

`build.cmd` runs both commands on Windows. For a focused test run, use the relevant project, for example:

```powershell
dotnet test --project tests/Vrmr.Tests/Vrmr.Tests.csproj -c Release
```

Other useful checks:

- Cross-platform libraries and CLI: `dotnet build src/Vrmr.Cli/Vrmr.Cli.csproj -c Release`.
- Integration packaging: `python integrations/package.py dist`.
- Run the existing app: `dotnet run --project src/Vrmr.App.WinForms -c Release`.

Run validation appropriate to the change; documentation-only edits do not require building or running tests. Report what was checked and any checks that could not run.

For changes that could affect solved motion, follow the regression procedure in `CONTRIBUTING.md`: produce before and after dumps using `vrmr dump <takesDir> <outDir>`, then compare with `python tools/compare_dumps.py <before> <after>`. Use copies of available takes, and report if suitable data is unavailable. Changes not intended to alter motion must produce `IDENTICAL`; intentional changes must describe the measured difference.
