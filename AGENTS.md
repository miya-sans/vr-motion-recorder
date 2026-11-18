# For AI agents

## Working approach

- Keep changes focused on the requested task. Follow nearby conventions and avoid unrelated refactoring or formatting.
- Give each method a clear responsibility. Reduce deep nesting and extract helpers when they improve clarity.
- Use static methods when behavior does not depend on instance state.
- Avoid speculative abstractions; solve the current requirements with code that is easy to navigate and modify.
- Keep dependencies explicit and respect project boundaries. Avoid adding dependencies without a concrete need.
- Make failures easy to diagnose and behavior easy to test. Add or update meaningful tests for behavior changes and bug fixes.
- Follow `.editorconfig` and the Rider cleanup conventions below. Match the cleaned source when older comments describe a more compact style.
- When committing, use [Conventional Commits](https://www.conventionalcommits.org/), with a short imperative subject and a body explaining why when useful.

## Rider code conventions

These conventions reflect the repository's Rider cleanup, rather than assumptions about Rider defaults. `VRMotionRecorder.sln.DotSettings` currently contains a dictionary entry, not a formatting profile. Use the cleaned files in `src/` as examples, including `Vrmr.App/Services/PlaybackClock.cs`, `Vrmr.App/Services/EditHistory.cs`, and `Vrmr.Core/MathUtil.cs`.

- **Layout:** use four spaces for C#, block-scoped namespaces, and opening braces on their own line for expanded blocks. Keep `using` directives outside namespaces, with `System` first; remove unused imports and unnecessary qualification when the name remains unambiguous.
- **Method bodies:** use block bodies for methods, operators, and overrides, even when they only return an expression. Keep expression-bodied properties and simple accessors, such as `public bool CanUndo => _undo.Count > 0;`. Short constructors and simple accessor blocks may remain on one line where the cleaned code does so.
- **Statements and blocks:** put separate statements on separate lines. Expand blocks containing several statements, including setters and control flow; do not compress assignments, calls, and returns into one line. Short single-statement guards such as `if (_editing) return;` may remain inline without braces. Wrap longer embedded statements onto an indented line, following nearby code.
- **Spacing and wrapping:** separate methods and logical blocks with blank lines. Wrap long signatures, calls, conditions, and initializers with the continuation indentation used nearby. Do not collapse wrapped code to fit more onto one line or impose a new hard line limit.
- **Local types:** use explicit built-in types (`bool`, `int`, `float`, `double`, `string`, `object`) and `var` for other inferred local types, including loop variables and `out var`. For example, use `double t = Clock.Now;`, `var pose = BuildPose();`, and `foreach (double[] segment in segments)`. Keep explicit types where inference is unavailable or would change behavior.
- **Object creation:** use target-typed `new` when the declared target provides the type, such as `readonly List<string> _undo = new();`. With `var`, keep the constructor type: `var items = new List<string>();`.
- **Names and modifiers:** use PascalCase for types and members, `_camelCase` for private fields, and camelCase for locals and parameters. Omit redundant `private` and unnecessary `this.`; retain `this.` when it disambiguates a field from a parameter. Preserve existing maths, serialized, interop, and derived solver names.
- **Member layout:** follow the cleaned class's grouping: constants and fields before constructors, properties before ordinary methods, and nested helper types near the end. Keep static/readonly fields and related members grouped as Rider has arranged them. Preserve initialization order wherever it affects behavior; do not alphabetize or reorder members mechanically.
- **Expressions and arguments:** remove redundant parentheses and argument names where the cleaned code does, while preserving grouping, overload resolution, and readability. Do not change numeric types or evaluation order to satisfy a style preference.
- **Documentation:** keep concise XML summaries inline. Wrap longer summaries into a multiline `<summary>` block with the text indented four spaces after `/// `. Use a space before `/>` in XML references, such as `<see cref="Advance" />`.
- **XML and Avalonia:** use two-space base indentation in project files and `.axaml`; wrap long attribute lists and align continuation lines with the surrounding cleaned markup. Keep a space before `/>`.

Apply these conventions to new or changed code without running a repository-wide cleanup for an unrelated task. In `Vrmr.Fbik`, retain the current Rider-cleaned layout and the existing naming exceptions; do not restore the older compressed formatting.

## Repository layout and boundaries

Read [CONTRIBUTING.md](CONTRIBUTING.md) for contribution rules and [docs/architecture.md](docs/architecture.md) for dependencies and data flow.

- `src/Vrmr.Core` and `src/Vrmr.Model`: maths, shared utilities, file formats, skeletons, and settings.
- `src/Vrmr.Fbik` and `src/Vrmr.Solve`: IK and the conversion of raw takes into poses. Preserve the derived solver's current Rider-cleaned formatting, naming exceptions, and licence notices.
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
