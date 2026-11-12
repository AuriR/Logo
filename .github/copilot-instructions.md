# Copilot Instructions for MaturinLogo

## Critical Rules (Non-Negotiable)

1. **NEVER tell the user you're done if the code doesn't compile.** Always run `dotnet build` and verify 0 errors before reporting completion. If you can't build, you're not done.
2. **Perform an adversarial review after any non-trivial change.** Try to break your own changes by considering edge cases, error paths, race conditions, null references, off-by-one errors, and assumptions that might break. Challenge your own solution before declaring work complete.
3. **Run the full test suite** (`dotnet test`) after any non-trivial change to ensure nothing broke.
4. **Increase the build number after any non-trivial change.** The build number is `git rev-list --count HEAD` (see Build Number below), so committing a non-trivial change bumps it automatically — commit such changes rather than leaving them uncommitted, and verify the new number shows on the splash before reporting completion.

## Architecture Overview

MaturinLogo is a .NET 10 Blazor WASM clone of Terrapin Logo / Apple Logo II for the Apple II series, designed to help kids learn programming through an authentic Apple IIe-style UI (24×40 text screen, green/amber phosphor toggle, CRT emulation).

### Solution Structure
- `Src/MaturinLogo` — Blazor WASM UI project (single deployable)
- `Src/MaturinLogo.Core` — Interpreter engine library (pure C#, no browser dependencies)
- `Src/MaturinLogo.Tests` — Test suite (xUnit)
- `Tools/DocGen` — Command reference generator (excluded from solution, never published)

### Key Files
| File | Purpose |
|------|---------|
| `Src/MaturinLogo.Core/Interpreter/Engine.cs` | Core interpreter: evaluation loop, speed system, primitive invocation |
| `Src/MaturinLogo.Core/Machine/LogoMachine.cs` | Application state machine: overlays, turtle state, key routing, demo mode |
| `Src/MaturinLogo.Core/Machine/LogoExamples.cs` | The 10 pre-loaded examples (Alt-X / demo mode) |
| `Src/MaturinLogo.Core/Sessions/LogoSession.cs` | Session capture/restore; `RestoreWorkspaceOnly` preserves graphics between demo examples |
| `Src/MaturinLogo.Core/Primitives/*.cs` | All Logo command implementations (one file per category) |
| `Src/MaturinLogo/Components/LogoApp.razor` | Main app component, keyboard capture, sound callback |
| `Src/MaturinLogo/Components/LogoScreen.razor` | 24×40 text grid rendering with cursor visibility logic |
| `Src/MaturinLogo/wwwroot/js/interop.js` | Browser interop: canvas drawing, tween animation, localStorage, Web Audio |

## Known Gotchas & Lessons Learned

### Build System
- **Blazor namespace directives**: ALL `.razor` files need explicit `@namespace` directive to be discoverable by the build system. Without it, components are not found (CS0246 "The type or namespace name 'App' could not be found" or CS0234). Duplicate `@namespace` directives cause RZ2001 error ("may only occur once per document"). This applies to root-level files like App.razor as well as subfolder components.
- **DocGen tool**: `Tools/DocGen` is intentionally excluded from the solution file so it never appears in published WASM output. Run it manually: `dotnet run --project Tools/DocGen`.

### Browser Testing
- **WASM server restart required**: After rebuilding, you must restart `dotnet run` or the browser silently gets the old wasm hash. Verify by checking which `MaturinLogo.Core.<hash>.wasm` is requested in network tab. The dev server port comes from `Properties/launchSettings.json` (currently **5171**, not 5270) — check the "Now listening on" line, don't assume a port.
- **Browser check tools are broken**: Node.js scripts in `Tools/BrowserCheck/*.cjs` have ESM/CommonJS issues (`require()` not defined). Use `dotnet run` and manual browser testing instead.

### Build Number
- `MaturinLogo.Core.csproj` has a `ComputeBuildNumber` MSBuild target that writes `obj/.../LogoInfo.BuildNumber.g.cs` with `const int BuildNumber = <git rev-list --count HEAD>` (fallback: UTC timestamp for non-git source zips). The splash and quick reference show `"1.4 BUILD 21"` via `LogoInfo.VersionWithBuild`. **MSBuild gotcha:** the `Lines` attribute of `WriteLinesToFile` treats `;` as an item separator, so C# semicolons in generated code must be escaped as `%3B`.
- Tests assert `Assert.Contains(LogoInfo.Version, ...)` — "1.4" must remain a substring of whatever the splash prints. The "WHAT'S NEW IN 1.4" offer intentionally uses plain `LogoInfo.Version`, not `VersionWithBuild`.

### Demo Mode & Examples (v1.4)
- **Web Audio one-shot rule**: `osc.start()` and `osc.stop()` may each be called only ONCE per oscillator node; a second call throws `InvalidStateError`. The sound path swallows exceptions, so the symptom is *silence*, not an error. Never schedule start/stop twice in interop.js.
- **Canvas sync invariant**: `_sentItems` tracks how many scene items were sent to the canvas. When the engine's item count drops below `_sentItems` (e.g., after `CLEAN`), `AppendGraphicsAsync` automatically triggers a full replay that wipes the old picture. This is why demo mode does NOT need an explicit clear call — and why calling one is redundant.
- **Demo exit must close synchronously**: `StopDemo` closes the overlay immediately (ScreenKind flips to Listener on ESC) even when an example is mid-run, then awaits the in-flight run before restoring the workspace. `_demoActive` stays true during that window so `SaveAsync`'s guard prevents demo procedures (TRI:, FLOWER:) from persisting into the child's session. A second ESC is blocked by `_demoStopping`. The regression test `Picking_an_example_right_after_exiting_the_demo_still_runs_it` asserts ScreenKind==Listener *synchronously* after ESC — do not make overlay close async.
- **Demo uses `RestoreWorkspaceOnly`, not full `Restore`**: a full restore clears the scene and erases what was just drawn, so the 3-second pause would show nothing. Workspace-only restores procedures/globals/layout but leaves turtle+scene intact.
- **Examples run as if typed**: `RunExample` puts the source into `_line`, calls `PaintInput()` + `SubmitLine()`, so the code is visible in the transcript and runs through the normal path — never fire-and-forget `RunAsync` from an overlay.

### Logo Language Semantics
- **Speed system**: `Engine.Speed` ranges 25–100 (default 50). Formula: `FrameDelayMs(speed) = speed >= 100 ? 0 : (100 - speed) * 6`. Tween duration = `clamp(FrameDelayMs / 2, 0, 240)`. At speed 50 → ~150ms tween per frame.
- **Animation timing**: The engine only yields to the browser every 3000 steps (`StepSlice`). Short programs (e.g., `REPEAT 4 [FD 50 RT 90]`) never reach that boundary, so graphics must be flushed before the first yield for them to animate. However, flushing on EVERY turtle command breaks long programs because each flush adds the speed delay. Solution: separate "flush graphics" (`FlushGraphicsAsync`, no delay) from "yield with speed delay" (`YieldAsync`). Flush on every graphics change until the first yield boundary is reached; after that, only flush at slice boundaries. `RunAsync` wraps execution in try/finally to ensure a final flush so short programs animate completely.
- **Sound crash handling**: JSInterop `InvokeVoidAsync` rejections propagate up through the entire call stack and abort the whole program (crash at LogoMachine.cs:863). Always wrap sound invokes in try/catch so one bad sound can't kill a child's whole program.
- **Shape rendering data path**: `SHAPE n` validates against `TurtleShapes.MaxIndex=5` → sets `Engine.Turtle.Shape` → `SetTurtleAsync` sends shape index to interop.js → `drawTurtle` switch on `SHAPES[t.shape|0]`. Shape 4 = 'CAR', handled by `drawCar()`.
- **Corner formula for shapes**: Must keep length/width scaling: `cx + fx * l * a + rx * w * b`. Dropping the scale factors makes all shapes render at unit size.

### UI Conventions
- **Cursor visibility**: Only shown when `ScreenKind == Listener` or `Editor`. All overlays (Info, Help, Settings, About, WhatsNew, Sessions, Colors, Shapes) hide the cursor.
- **Spelling**: Use American English ("color" not "colour") throughout all UI strings and documentation.

## Development Workflow

### Adding a New Logo Command
1. Implement the primitive in appropriate file under `Src/MaturinLogo.Core/Primitives/`
2. Register it in `PrimitiveTable.CreateDefault()`
3. Add tests to `Src/MaturinLogo.Tests/`
4. Run DocGen: `dotnet run --project Tools/DocGen`
5. Verify drift guard tests pass: `dotnet test Src/MaturinLogo.Tests`

### Fixing a Bug
1. Reproduce the bug (write a failing test if possible)
2. Implement the fix
3. Run full test suite
4. Perform adversarial review of the fix
5. Update documentation if behavior changed

### Before Reporting Completion
- [ ] `dotnet build` succeeds with 0 errors, 0 warnings
- [ ] `dotnet test` passes all tests
- [ ] Adversarial review performed on non-trivial changes
- [ ] Documentation updated (Architecture.md, command reference)
- [ ] Browser-tested if UI/graphics/sound change

## Documentation Workflow

### Command Reference (§14 in Architecture.md)
The command reference table is **auto-generated** from `PrimitiveTable.CreateDefault()` by `Tools/DocGen`. It must never be hand-edited — it will drift from the actual language.

```bash
# Regenerate after adding/removing primitives:
dotnet run --project Tools/DocGen

# Check for drift (CI mode):
dotnet run --project Tools/DocGen -- --check
```

### Drift Guards
`Src/MaturinLogo.Tests/CommandReferenceTests.cs` contains 8 tests that assert the doc lists exactly match the primitives. These fail if:
- A primitive is added but not documented
- Documentation lists a command that doesn't exist in code
- Command signatures don't match

### Architecture.md Structure
1. Table of Contents (auto-updated)
2. Overview & Design Philosophy
3. System Architecture
4. Interpreter Engine
5. Turtle Graphics
6. UI Layer
7. Session Management
8. Sound System
9. Keyboard Handling
10. CRT Emulation
11. Testing Strategy
12. Build & Deployment
13. Known Limitations
14. Command Reference (auto-generated — do not edit manually)
15. Revision History
16. Glossary

## Environment Notes

- **WASM server**: Runs at `http://localhost:5270` (or whatever port is available). Start with `dotnet run --project Src/MaturinLogo`.
- **.NET version**: 10.0 (check global.json or csproj TargetFramework)
- **Test framework**: xUnit with Microsoft.NET.Test.Sdk

