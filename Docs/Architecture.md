# Maturin Logo — Architecture

A Blazor WebAssembly clone of Terrapin Logo / Apple Logo II, built to teach kids programming on
the look and feel of an Apple IIe. .NET 10, no server, works fully offline.

---

## 1. Shape of the solution

`Src/` holds the solution (`MaturinLogo.slnx`) with **one folder per project**:

| Project | Kind | Purpose |
|---|---|---|
| `MaturinLogo.Core` | class library | The Logo language and the whole application machine. No Blazor, no JavaScript, no DOM. |
| `MaturinLogo` | Blazor WASM | The screen: renders the grid, hosts the canvas, owns the keyboard and browser services. |
| `MaturinLogo.Tests` | xUnit | 217 tests over Core and over the machine. |

The split is the single most important decision in the project. Everything below the picture —
reader, evaluator, turtle, errors, sessions, key routing, even the listener's line editor — is
plain C# that runs with no browser at all. That is what makes a Logo interpreter testable: a test
can type a keystroke and assert on the 40 characters of row 22.

```
MaturinLogo (WASM)
  Components/LogoApp.razor      wires browser -> machine, owns the repaint loop
  Components/LogoScreen.razor   the 40x24 grid + canvas overlay
  wwwroot/js/interop.js         keyboard, fit-to-window, canvas, localStorage, download
  wwwroot/css/app.css           phosphor theme, CRT overlay, cursor blink
        |
        | delegates (Func<...> properties) - the only seam between the two halves
        v
MaturinLogo.Core
  Machine/LogoMachine.cs        screens, key routing, listener, overlays, sessions, editor
  Machine/ScreenGrid.cs         TextScreen -> HTML
  Machine/SplashArt.cs          the start-up screen
  Interpreter/Engine.cs         the evaluator
  Reader/                       tokenizer and reader
  Primitives/                   the command table, friendly errors, quick reference
  Turtle/                       geometry, palette, graphics scene
  Screen/TextScreen.cs          the 40x24 cell grid
  Sessions/                     session model, localStorage format, .logo export
```

`MaturinLogo.Core` has **no package reference to Blazor**. If that ever becomes false, the
testability of the whole project goes with it.

---

## 2. The screen

`TextScreen` is a `char[24,40]` plus a `CellAttr[24,40]`, a cursor, and a text window
(`WindowTop`/`WindowBottom`) that Logo's `LOWER n` shrinks to make room for graphics.

It is rendered by `ScreenGrid.Build`, which emits **one HTML string for the whole grid** and hands
it to Blazor as a `MarkupString`. Blazor then replaces that one node rather than diffing 960
spans. Two reasons, both learned from the AppleWorks port in `C:\Dev\RetroWorks`:

- **Speed.** A program printing thousands of characters would otherwise thrash the diff.
- **Correctness.** Per-cell diffing leaves stale characters behind when a row gets shorter.

Every character is escaped on the way out (`ScreenGrid.Escape`), including quotes and `&`, so
nothing Logo prints can inject markup. Blank cells become `&nbsp;` because a span holding a plain
space can collapse and let a row drift out of 40 columns.

**Attribute bits.** Apple II text mode had no bold and no underline — the bitmap font was the only
font — so `.a-b` and `.a-u` exist in the data model and deliberately paint nothing. Reverse
(`.a-r`) and dim (`.a-dim`) are real and are used for errors and hints.

**The cursor** is an inset `box-shadow` with `steps(2)` timing, not a border: it is paint-only, so
a cell never changes size and no row shifts when the cursor moves. The hard on/off blink is what a
real CRT did; a smooth fade is not.

**Layout is one number.** Cells are `0.5em` wide (VT323's aspect is 1:2), so `interop.js` only has
to choose a font size that fits `40 * 0.5` by `24` into the window, and everything else scales.
`fitGrid()` runs on load, on resize, and on `document.fonts.ready`.

---

## 3. The keyboard

There is no `<input>` anywhere. A single global `keydown` listener in `interop.js` (capture phase)
turns every event into a **token string** and hands it to `[JSInvokable] OnKey`:

`UP DOWN LEFT RIGHT HOME END PGUP PGDN BKSP DEL ENTER ESC TAB SPACE`, a single character for
printables, `A-X` for Alt (the Open Apple key), `C-SLASH` / `C-S` for the two Ctrl keys the browser
would otherwise eat.

`preventDefault` + `stopPropagation` on everything mapped, so no page scroll, no find bar, no
browser save dialog. The one exception is deliberate: events whose target is inside
`a, button, input, select, textarea` are ignored, because forwarding Enter to the app while
Blazor's error-banner Reload link has focus **reloads the page mid-program**. That was a real bug
in the reference port and the guard is copied here.

Keys are routed by `LogoMachine.HandleKey` per screen, and the rules are worth spelling out
because they are what makes the machine feel solid:

- **Splash:** any key starts Logo. `Alt-M`/`Alt-G`/`Alt-C` set mode, phosphor and CRT first, so a
  child can choose before starting. Nothing that opens an overlay is accepted — there is no
  transcript to go back to.
- **A program waiting on `READ` owns the keyboard**, and is checked *before* the busy test,
  because a program parked on `READ` is still running.
- **While a program runs**, only `ESC`/`Alt-B` do anything. Everything else is dropped, so a child
  cannot type `CLS` into the middle of a drawing.
- **While an overlay is open**, keys belong to the overlay. Text typed there never reaches the
  hidden listener line.

`Alt-?` is the quick reference. Because browsers eat `Alt+?` on some layouts, `Alt-Q`, `Alt-/` and
`Ctrl-/` all open it too.

---

## 4. The listener

The child types into a `StringBuilder` buffer with its own caret; `LogoMachine` paints it into the
grid after the prompt. This is not what an Apple II did (it had no line editing), but arrow-key
editing was an explicit requirement, and doing it in the buffer rather than in the screen means the
grid stays a pure function of state.

`PaintInput` wraps a long line across rows exactly as the II would and scrolls the transcript when
the line runs out of window. `MoveCursorPastInput` parks the engine's cursor just past the typed
text before anything prints, which is what guarantees **output can never land on top of the line
being typed** — the failure mode a naive implementation hits first.

**The painter never writes outside `[WindowTop..WindowBottom]`.** Every cell it touches goes
through `TryCellOf`, which refuses a row outside the window, and `CaretPosition` clamps to a row
that exists. This matters because `Alt-T` shrinks the window from 24 rows to 4 *while a line is
typed*: an earlier version measured the line against the old layout and indexed past the end of the
grid, and because `OnKey` calls `HandleKey` synchronously the exception came back on **every
 keystroke afterwards** — a permanently deaf keyboard. Now a line taller than the window scrolls
inside it to follow the caret.

The cap is a **fixed `MaxInputLength = 256`**, not a multiple of the current window, for the same
reason: a limit that moved with the layout would let a child type 958 characters in full text and
then be unable to draw them after `Alt-T`.

History recall counts from the newest entry, so `UP UP` walks backwards and `DOWN` comes forward
to a blank line.

**A waiting program owns its own prompt.** When a running program parks on `READ`/`READLINE`,
`_showPrompt` goes false and the answer is painted at the column the program's `TYPE` left the
cursor — the listener's `> ` is *not* drawn, because writing it would erase the question the child
is being asked to answer. `CompleteRead` then parks the cursor past the echoed answer and drops a
line before the program resumes printing.

A reporter typed at the listener prints its value (`FIRST "LOGO` answers `L`), while a command does
not (`FD 100` answers nothing). That is Logo's behaviour and it is `ReportToplevel`.

---

## 5. The evaluator

Tree-walking over `ProgramItem`s read by `LogoReader`, with Logo's **input-consuming ("pull")
semantics**: each primitive declares its arity and the evaluator reads that many expressions off
the source stream following the call.

Two consequences that cost real debugging time and are worth recording:

- **Greedy inputs.** A fixed-arity reporter takes its inputs as whole expressions, so
  `FIB :N - 1 + FIB :N - 2` is *one* input and never terminates. Real Apple Logo programs write
  `(FIB (:N - 1)) + (FIB (:N - 2))`. A bare paren group `(:N - 1)` is **not** self-delimiting;
  `(FIB (:N - 1))` is. The engine is right here and several early test expectations were wrong.
- **Static tail-call analysis is unsound.** Any approach that walks items without evaluating them
  has to re-implement the infix grammar and will silently change answers. The first attempt at
  this parsed `OUTPUT FIB :N - 1 + FIB :N - 2` as a tail call and dropped the `+`. Tail position
  is now a **runtime flag** (`_tailPosition`), armed only by `OUTPUT` and cleared wherever a value
  is consumed instead of returned (operator right-hand side, call inputs, bracketed lists, paren
  groups).

Tail calls are trampolined in **both** command and expression position. The expression path
matters: `OUTPUT COUNTDOWN :N - 1` reaches the procedure through `EvalBareWordAsync`, and without
a trampoline there the classic countdown recursion still overflowed the stack.

**Case folding.** `FoldCase` defaults to true, which makes everything uppercase and is why
`TO F` collides with `FIRST`'s alias `F`.

### Limits

Chosen by measurement, not guessed. All are `const` on `Engine`.

| Constant | Value | Why this number |
|---|---|---|
| `StepSlice` | 3,000 | Evaluation steps between cooperative yields. |
| `MaxDepth` | 200 | Nested non-tail calls. `ADDUP 199` works; 200 trips. WASM has a small stack and each Logo level costs several CLR frames. |
| `MaxLoopPasses` | 50,000 | WHILE/UNTIL/REPEAT. A 3,600-step spiral and a countdown to 5,000 fit with room to spare; a loop that never ends now reports in ~700 ms instead of ~2.7 s. In a classroom, waiting is the failure. |
| `MaxTailJumps` | 100,000 | The only guard a trampoline cannot see: `TO FOREVER OUTPUT FOREVER END` never grows the stack, so depth cannot catch it. Trips in ~2 ms. |

**Important host caveat:** `NullHost.YieldAsync` returns a completed task, so every `await`
completes synchronously and deep Logo recursion nests real CLR frames. This is why `MaxDepth`
must stay modest — 400 overflowed the *test host's* stack. The WASM host yields for real, which
keeps the browser responsive but does not remove the CLR frame cost.

### Primitive table

`PrimitiveTable` maps names and aliases to `Primitive`s. Registration order is Turtle → Math →
Words → …, and aliases are registered with no priority, so a later alias could silently overwrite
an earlier command. `Add` now **throws** on a duplicate name or alias, which turned a whole class
of invisible bugs into a build/startup failure:

- `LESS` carried alias `LT`, clobbering the turtle's turn-left. `LT`/`GT` are gone; `LESSP`/`GREATERP` stay.
- `FIRST`→`F` and `LAST`→`L` are authentic Apple Logo abbreviations and are kept. Real Logo also
  refuses `TO F` for the same reason, so `ReadOnly` is the correct answer, not a bug.

`ITEM`/`SETITEM` take **index then list**, which is Logo's signature and the opposite of what was
first implemented.

---

## 6. Turtle graphics

The scene is a **replayable item list**, not a bitmap: `DrawSegment`, `DrawLabel`, `StampItem` in
Apple II hi-res space (280x192, origin centred, y up). This is what makes a session restorable
exactly, lets the canvas redraw at any size, and lets the `.logo` export rebuild the picture from
source.

`MaturinLogo` converts items to JSON and hands them to `interop.js`, which fits 280x192 into the
graphics region and keeps the aspect ratio (or a circle becomes an ellipse).

**Chromatic fringe:** each stroke is drawn three times — a faint red copy offset left, a faint
blue copy offset right, then the true colour on top. That is the sub-pixel smear a real
television-made Apple II display produced on a bright line.

Only the six true hi-res colours are used (`PALETTE` in `interop.js`); Logo's `SETPC 0-15` is
folded onto them so what appears is what the hardware could show.

`Arc`/`Circle` draw true chords (`2r·sin(δ/2)` with half-turns either side), so the turtle stays
exactly on the circle with no drift to correct.

**Repaint cost.** A running program sends only the strokes added since the last yield
(`AppendGraphicsAsync`), not the whole scene — otherwise a 10,000-line spiral gets slower as it
draws. If items were removed (a `CLS`/`CLEAN`) or the count went backwards, it falls back to a
full replay so the two sides cannot desynchronise. The **background colour is part of that
comparison**: `SETBG` adds no strokes at all, so an append-only path would leave the old colour on
screen. `ReplayGraphicsAsync` records `_sentBackground`, and any change to it forces a replay.

**The turtle is live state, not part of the replayed picture**, so it travels with the replay *and*
after every append (`SetTurtleAsync` → `setTurtle` in `interop.js`), otherwise it lags behind the
line it is drawing. This is what makes `HT`, `ST` and `SETSHAPE` do something visible.

**Resizing the window repaints the canvas.** The CSS box grows but a canvas keeps its old bitmap, so
the resize listener calls `resizeGraphics()` as well as `fitGrid()` — without it the picture is
stretched until the next full replay. The picture is always re-rendered from the item list, never
scaled.

---

## 7. Screen layouts

Apple Logo swapped the whole screen for graphics. This app has three states:

- **Full text** — `TEXT`, all 24 rows.
- **Split** — `LOWER n`: 20 rows of picture, 4 rows of live text, so a child types `FD 100` and
  watches it happen.
- **Full graphics** — `GRAPHICS` (aliases `GFX`, `HIRES`; there is **no `GRAPH`**), keeping exactly
  one text row so a child can always type `TEXT` to get the screen back.

`Engine.ShowLower(n)` clamps `n` to 1..22, so **`ShowLower(0)` is not a way to get full text** —
use `ShowFullText()`. This bit me: `Clear()` used to reset the window, which silently hid it.

**The canvas is only shown when the listener owns the screen.** It is an opaque black rectangle
painted over the grid, so leaving it up during the splash, the quick reference, the editor or the
info page hides the text underneath. `LogoScreen.GraphicsClass` requires both `ScreenKind ==
Listener` and a graphics region (`WindowTop > 0`).

**Overlays are a stack.** `OpenOverlay` stashes the whole grid (chars, attrs, cursor, and which
screen it came from) and `CloseOverlay` puts it back byte-for-byte, which is what lets the editor
open the session manager and return to editing. A single slot was the original design and it lost
the child's place in their program. `OpenEditor` **early-returns when already editing** — reopening
would reload the buffer from the last saved source and silently throw away unsaved typing.

`TextScreen.Clear()` erases the grid but **keeps the text window**; only `Reset()` (and the
constructor) restores a whole-screen window. Logo's `CLT`/`CLS` should wipe what is on screen, not
hand the turtle's page back to text.

**EASY starts on the split page.** The splash is the only screen with all 24 rows, so it carries
the getting-started lesson (`FD 100`, `RT 90`, `Alt-?`). Once Logo starts there are only four text
rows, and a four-row window cannot hold an explanation — so the greeting is two short lines.

---

## 8. Friendly errors

`ErrorText` speaks two voices. EASY is plain English plus a nearest-match hint; ADVANCED is terse,
as Apple Logo was. `ErrorText.Nearest` is Levenshtein over primitive names, aliases, and the
child's own procedures, so `FORWRD` suggests `FORWARD` — the full name, because a child who
mistyped `FD` recognises `FORWARD`.

Messages are **wrapped to 40 columns** by `LogoMachine.WriteWrapped`, breaking on spaces so a word
is never cut in half. And in EASY mode the hint is **trimmed to fit the window**: with only four
text rows, a long hint would scroll the actual error off the top and leave the child reading the
tail of an explanation for a message they never saw.

---

## 9. Sessions

`SESSION1.logo`, `SESSION2.logo`, … maximum 12, all in **one** namespaced localStorage key
(`maturinlogo.sessions.v1`) holding a versioned `StoreFile`. Versioning means a future format can
migrate rather than discard; a store from a *newer* version is refused outright instead of being
half-parsed.

A session captures the **whole workspace**: the child's source text, defined procedures (as source
text, so loading re-parses against the current interpreter rather than trusting stored internals),
global variables, turtle state, the picture as an item list, the screen transcript, and command
history.

`SessionNaming.NextName(names, lastIssued)` takes a **persisted high-water mark**
(`StoreFile.LastIssuedSessionNumber`). Counting only the live sessions would hand a deleted
`SESSION2` back out, and a child told "your work is in SESSION2" would find someone else's code
sitting there.

Property lists from `PUT`/`GET` are stored too (`StoredProperty`, keyed by name *and* property — a
property hangs off a thing, not off the workspace). Without them a child could run `PUT`, reload,
and be told `GET` was never stored.

`Restore` re-parses procedures through `DefineProcedureFromSource`, which returns false rather
than swallowing everything: a procedure that no longer parses is skipped so the rest of the session
survives. (The bug that lived here: it passed a stream still positioned on `TO`, so the procedure
being defined was named `TO` — every restored procedure silently vanished.)

Auto-save fires after every listener command and after every editor commit. `BYE` saves and
returns to the splash.

**An overlay never contributes to the transcript.** `SaveAsync` copies `ScreenLines` only while the
listener owns the grid. The session manager's `N` and `D` keys save immediately (so a new or
deleted session is on disk before anything else happens), and without the guard that save captured
the *menu itself* as the child's work — reload and their typing has been replaced by a file list.
Nothing Logo prints happens while an overlay is up, so the last listener transcript is still
correct.

`Alt-D` downloads real Logo source via `LogoSourceExporter`: header comments, the child's
procedures, then a runnable tail that rebuilds the picture. Feeding that file back in reproduces
the session.

---

## 10. The splash turtle

The ASCII turtle is **generated, not hand-typed**: an ellipse-based shell (rim band plus a scute
lattice), head, four flippers and a tail were rasterised into a 40x12 cell grid and the result
pasted into `SplashArt.Turtle`. Rasterising beats guessing at art in a text editor — the shapes
have to line up at exactly 40 columns.

Seen from above, which is how Logo's turtle actually lives.

---

## 11. Offline

Self-hosted **VT323** (`wwwroot/fonts/vt323.woff2`, 17.9 KB, SIL Open Font License) — no Google
Fonts, no CDN, no remote quick reference. The reference is generated at runtime from the very
primitive table that runs the language, so the help text can never name a command that is not
implemented.

PWA via .NET 10's built-in support: `manifest.webmanifest`, and the template's
`service-worker.js`/`service-worker.published.js` pair (the dev one deliberately does not cache,
so development stays sane). One change from the template: the cache pattern list has `\.woff$` but
not `\.woff2$`, which would have left the font uncached, so it is now `\.woff2?$`.

Verified against a `dotnet publish`: the font, the manifest, the assets manifest, and 37 WASM
runtime files are all in the offline bundle.

---

## 12. EASY vs ADVANCED

| | EASY | ADVANCED |
|---|---|---|
| Errors | Plain English + hint, trimmed to fit | Terse, Apple-style |
| Screen | Split (LOWER 4) so the turtle is visible | Full text |
| Editor (`Alt-E`) | refused, with an explanation | unlocked |
| Intended for | a child typing their first program | a longer program worth editing |

The modes are a **teaching** split, not a difficulty gate: ADVANCED exists so a child who outgrows
the training wheels gets a full-screen editor, and EASY exists so nobody has to read a 4-row
window to find out what went wrong.

---

## 13. Keyboard map

| Key | Action |
|---|---|
| `Alt-?`, `Alt-Q`, `Alt-/`, `Ctrl-/` | Quick reference (scrollable, `ESC` closes) |
| `Alt-M` | EASY / ADVANCED |
| `Alt-G` | green / amber phosphor |
| `Alt-C` | CRT emulation on / off |
| `Alt-L` | sessions: new / open / delete |
| `Alt-S`, `Ctrl-S` | save (`Ctrl-S` is mapped so the browser's own page-save dialog does not open) |
| `Alt-D` | download `.logo` source |
| `Alt-O` | open a `.logo` file |
| `Alt-E` | editor (ADVANCED); `ESC` saves, `Alt-R` runs |
| `Alt-T` | text / split |
| `Alt-B`, `ESC` | break a running program |
| arrows | edit the line, or recall history at an empty prompt |
| `PgUp/PgDn/Home/End` | scroll the reference and the editor |

---

## 14. Testing

266 tests, all green. `dotnet build` and `dotnet test` are clean; `dotnet publish` produces a
working offline bundle.

- **Interpreter** (reader, evaluator, primitives, turtle, errors, recursion, loops) — the
  executable spec of the language.
- **Machine** (`MachineTests.cs`) — the UI logic with a `FakeBrowser` supplying in-memory
  localStorage, canvas, theme and download. Covers splash→listener, line editing, history, wrapping,
  errors, every Alt key, overlay save/restore, session naming and round-trips, the editor, `READ`,
  `ESC` break, and runaway loops.
- **Edge cases** (`EdgeCaseTests.cs`) — the stateful corners: caret arithmetic at the last column and
  across wraps, layout switches under a long typed line, overlay stack corruption, `GRAPHICS`' single
  row, session cap and deletion, corrupt stores, re-entrancy, and one regression test per adversarial
  review finding.

Two rules the tests encode, because they are the things that broke during development:

1. A test that interrupts a program must **pump the queue** until `IsBusy` is true before pressing
   `ESC`. `Task.Delay` in a synchronous test body runs nothing.
2. The fake `YieldFrameFunc` must **actually await**, or a running program can never be
   interrupted and the break tests are meaningless.

### In a real browser

Unit tests cannot see a canvas, a font, or a stretched bitmap, so three Playwright scripts run
against the dev server (kept in the session folder, not the repo):

- `e2e.mjs` — boot, splash art, typing, the quick reference scrolling, arrow history, an error and
  its hint, localStorage, amber, reload, **zero off-origin requests**, a long program keeping the
  page responsive, `ESC` break, `READ`, and a clean console.
- `visual.mjs` — screenshots plus pixel counts: the turtle is drawn at start (236 bright pixels),
  `HT` hides it and `ST` shows it, a spiral animates, amber + CRT render together, and the homage
  line is not cut mid-word.
- `regress.mjs` — every adversarial finding driven through the real keyboard path, including the
  crash repro (`Alt-T` with 300 characters typed, then keep typing), `Ctrl-S`, a viewport resize
  checking `canvas.width` against its CSS box × devicePixelRatio, and `SETBG` inside a running
  program.

A canvas "blank" means zero **bright** pixels, not zero non-transparent ones: `paint()` fills the
whole rectangle with the background colour, so an alpha test would always report a drawn picture.

---

### Adversarial review

Every non-trivial change is reviewed by a separate read-only agent looking for defects, not
validation. The last pass found eight, and each was reproduced by a failing test *before* it was
fixed — a review finding that cannot be turned into a red test is usually a style opinion.

| # | Severity | Finding | Fix |
|---|---|---|---|
| 1 | Critical | `Alt-T` with a long line typed threw `IndexOutOfRangeException` in the painter, which killed the keyboard permanently (the key path is synchronous, so every later keystroke threw too) | Window-clamped painting via `TryCellOf`; fixed 256-char cap |
| 2 | High | A running program's `READ` question was erased and a fake `> ` drawn over it | `_showPrompt` / `_readBaseCol`: echo the answer at the program's cursor |
| 3 | High | `Alt-E` inside the editor reloaded the buffer and discarded unsaved work | `OpenEditor` early-returns when already editing |
| 4 | Medium | The `SESSIONS` menu got saved as the child's transcript | `SaveAsync` captures `ScreenLines` only while the listener owns the grid |
| 5 | Medium | `PUT`/`GET` property lists were never persisted | `StoredProperty` in capture and restore |
| 6 | Medium | Resizing the window left the canvas stretched until the next replay | resize listener also calls `resizeGraphics()` |
| 7 | Low | `C-S` and `A-ENTER` were emitted by JS and handled by nothing | `Ctrl-S` saves; the `A-ENTER` token is gone |
| 8 | Low | `AppendGraphicsAsync` ignored background changes, so `SETBG` in a program did nothing visible | `_sentBackground` compared before appending |

Review found clean: `ScreenGrid` escaping (no injection from Logo text into the `MarkupString`),
the overlay stack's save/restore, `IsBusy` plus `finally` re-entrancy, `TextScreen.Clear()` callers,
and session-number monotonicity.

Note on `Ctrl-S`: the wish list prefers Alt over Ctrl because Ctrl is costly to override in a
browser. Here the key was *already* being swallowed and mapped, so leaving it dead meant swallowing
the browser's save dialog for nothing. It now does the same thing as `Alt-S`. `A-ENTER`, which had
no meaning at all, was deleted instead.

---

## 15. Known gaps / future

`Docs/Additional Features.md` holds the wish list: localisation, an examples library, 8-bit sound
(see `Docs/8-bit Sound Code Example.htm`), an animation-speed control, about/resources screens, and
a downloadable exception log. Terrapin-only surfaces (bitmaps, controls, ports, arrays, property
lists, multi-turtle `TELL`/`ASK`) are out of scope; the primitive table leaves room for them.
