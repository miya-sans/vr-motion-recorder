# Code style

## Design and tests

- Give each method a clear responsibility. Reduce deep nesting and extract helpers when they improve clarity.
- Use static methods when behavior does not depend on instance state.
- Avoid speculative abstractions; solve the current requirements with code that is easy to navigate and modify.
- Keep dependencies explicit and respect project boundaries. Avoid adding dependencies without a concrete need.
- Make failures easy to diagnose and behavior easy to test. Add or update meaningful tests for behavior changes and bug fixes.
- Name each test for the behaviour it checks, for example `SaveThenLoadKeepsParametersAndMetadata`. Document what public types and members are for in their XML summaries. Use theme tokens for UI colours and sizes.

## Shared code conventions

The standard rules in [`.editorconfig`](../../.editorconfig) and the SDK checks below define automated C# style verification. Review the conventions below for details Roslyn cannot check, including field order, ternary wrapping and Avalonia markup. Rider cleanup is optional. The `resharper_*` settings and `VRMotionRecorder.sln.DotSettings` provide JetBrains preferences.

- **Layout:** use four spaces for C#, block-scoped namespaces, and opening braces on their own line for expanded blocks. Keep `using` directives outside namespaces, with `System` first; remove unused imports and unnecessary qualification when the name remains unambiguous.
- **Method bodies:** use block bodies for methods, operators, and overrides, even when they only return an expression. Keep expression-bodied properties and simple accessors, such as `public bool CanUndo => _undo.Count > 0;`. Short constructors and simple accessor blocks may remain on one line where the cleaned code does so.
- **Statements and blocks:** put separate statements on separate lines. Expand blocks containing several statements, including setters and control flow; do not compress assignments, calls, and returns into one line. Short single-statement guards such as `if (_editing) return;` may remain inline without braces. Wrap longer embedded statements onto an indented line, following nearby code.
- **Spacing and wrapping:** separate methods and logical blocks with blank lines. Wrap long signatures, calls, conditions, and initializers with the continuation indentation used nearby. Do not collapse wrapped code to fit more onto one line or impose a new hard line limit. For multiline conditional expressions, keep `?` and `:` at the start of their branch lines together with the following expression; never put either operator alone on a line. Indent nested conditionals one level further.
- **Local types:** use explicit built-in types (`bool`, `int`, `float`, `double`, `string`, `object`) and `var` for other inferred local types, including loop variables and `out var`. For example, use `double t = Clock.Now;`, `var pose = BuildPose();`, and `foreach (double[] segment in segments)`. Keep explicit types where inference is unavailable or would change behavior.
- **Object creation:** use target-typed `new` when the declared target provides the type, such as `readonly List<string> _undo = new();`. With `var`, keep the constructor type: `var items = new List<string>();`.
- **Names and modifiers:** use PascalCase for types and members, `_camelCase` for private fields, and camelCase for locals and parameters. Omit redundant `private` and unnecessary `this.`; retain `this.` when it disambiguates a field from a parameter. Preserve existing maths, serialized, interop, and derived solver names.
- **Member layout:** follow the cleaned class's grouping: constants and fields before constructors, properties before ordinary methods, and nested helper types near the end. Within field groups with the same access and static/readonly modifiers, sort alphabetically by declaration name (the first name in a declaration containing several fields). Keep multiline field declarations together; for example, the declaration starting with `leftLegLen` belongs before `nativeFeet`. Preserve initialization order wherever it affects behavior; do not alphabetize all members across groups.
- **Expressions and arguments:** omit parentheses that C# precedence and associativity make redundant. Write `v < a ? a : v > b ? b : v`, `position + rotation * vector`, and `off + ((i - off + 3) & ~3)`. Keep parentheses that affect grouping; do not reassociate floating-point calculations, change numeric types, or change overload resolution or evaluation order. Remove redundant argument names where the cleaned code does.
- **String conversion:** omit parameterless `.ToString()` when string concatenation already performs the same conversion, such as `s + " uncertain"`. Keep explicit conversions when formatting, culture, null behavior, or overload resolution requires them.
- **Extension methods:** use receiver syntax when an extension is available and resolves to the same method: `window.FindControl<Control>("WorkspaceSwitch")`, rather than `ControlExtensions.FindControl<Control>(window, "WorkspaceSwitch")`.
- **Documentation:** keep concise XML summaries inline. Wrap longer summaries into a multiline `<summary>` block with the text indented four spaces after `/// `. Use a space before `/>` in XML references, such as `<see cref="Advance" />`.
- **XML and Avalonia:** use two-space base indentation in project files and `.axaml`; wrap long attribute lists and align continuation lines with the surrounding cleaned markup. Keep a space before `/>`.

Apply these conventions to changed code. Preserve the existing solver layout and naming exceptions in `Vrmr.Fbik`.

## Verify from the command line

Run the following from the repository root with the SDK selected by [`global.json`](../../global.json). The Release build runs the compiler, the SDK's .NET analyzers and applicable project analyzers, including xUnit analyzers in the test projects.

```powershell
dotnet build VRMotionRecorder.slnx -c Release
```

For changed C# files in the solution, check whitespace and Roslyn style after the build. Replace the example path with the files being reviewed. Both commands report proposed fixes without editing files.

```powershell
dotnet format whitespace VRMotionRecorder.slnx --no-restore --verify-no-changes --include src/Vrmr.App/Services/PlaybackClock.cs
dotnet format style VRMotionRecorder.slnx --no-restore --verify-no-changes --severity info --include src/Vrmr.App/Services/PlaybackClock.cs
```

The style check includes suggestion-level diagnostics such as the local type rules. Preferences marked `silent` remain available to editor cleanup. Inspect the final diff and rerun the relevant checks after applying fixes.

The [Unity integration](../../integrations/unity/com.vrmr.motion-bridge/README.md) is a separate editor assembly. For changes there, verify compilation in a compatible Unity project (the package declares Unity 2022.3). The solution commands cover SDK projects only.

## Build warnings and analyzers

- **Warning policy:** do not introduce new warnings. Fix warnings caused by the current change and report pre-existing warnings separately. Avoid unrelated warning cleanup; an existing warning count is not a permanent allowed baseline.
- **Obsolete APIs (`SYSLIB0014`, `CS0618`):** use supported APIs when writing or modifying affected code: `HttpClient` for HTTP requests, `SKFont` for glyph lookup, and bitmap saving overloads accepting `BitmapEncoderOptions`. Preserve existing timeout, cancellation, resource ownership, and output behavior during migrations.
- **Async tests (`xUnit1031`):** use `async Task` tests and await asynchronous operations. Avoid `.Result`, `.Wait()`, and `.GetAwaiter().GetResult()` in tests. Preserve UI-thread dispatch requirements; do not copy synchronous infrastructure bridges into new tests.
- **Collection assertions (`xUnit2029`):** use `Assert.DoesNotContain(collection, predicate)` to assert that matching elements are absent. Keep `Assert.Empty` for assertions that an entire collection is empty.
- **Compatibility names (`CS8981`):** preserve deliberate Unity.Mathematics-compatible shim names such as `math` and `quaternion`. Do not rename them solely to silence warnings or extend this naming convention to ordinary application types.
- **Suppressions:** prefer fixing the cause. Any necessary new suppression must cover the smallest practical scope and explain the compatibility requirement; do not broadly disable analyzers or add solution-wide suppressions.
