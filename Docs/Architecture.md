# Maturin Logo — Architecture

A Blazor WebAssembly clone of Terrapin Logo / Apple Logo II, built to teach kids programming on
the look and feel of an Apple IIe. .NET 10, no server, works fully offline.

**Current version: 1.0** — the version in `LogoInfo`, printed by `PRINT VERSION`, on the splash, and
on the `Alt-I` dashboard. The revision history at the bottom of this document tracks every release.

### Contents

| § | Section |
|---|---|
| 1 | [Shape of the solution](#1-shape-of-the-solution) |
| 2 | [The screen](#2-the-screen) |
| 3 | [The keyboard](#3-the-keyboard) |
| 4 | [The listener](#4-the-listener) |
| 5 | [The evaluator](#5-the-evaluator) — pull semantics, tail calls, limits, primitive table |
| 6 | [Turtle graphics](#6-turtle-graphics) |
| 7 | [Screen layouts](#7-screen-layouts) |
| 8 | [Friendly errors](#8-friendly-errors) |
| 9 | [Sessions](#9-sessions) |
| 10 | [The splash turtle](#10-the-splash-turtle) |
| 11 | [Offline](#11-offline) |
| 12 | [EASY vs ADVANCED](#12-easy-vs-advanced) |
| 13 | [Keyboard map](#13-keyboard-map) |
| 14 | [Command reference](#14-command-reference) — every supported command |
| 15 | [Testing](#15-testing) |
| 16 | [Known gaps / future](#16-known-gaps--future) |
| 17 | [Glossary](#17-glossary) |
| — | [Revision history](#revision-history) |

---

## 1. Shape of the solution

`Src/` holds the solution (`MaturinLogo.slnx`) with **one folder per project**:

| Project | Kind | Purpose |
|---|---|---|
| `MaturinLogo.Core` | class library | The Logo language and the whole application machine. No Blazor, no JavaScript, no DOM. |
| `MaturinLogo` | Blazor WASM | The screen: renders the grid, hosts the canvas, owns the keyboard and browser services. |
| `MaturinLogo.Tests` | xUnit | Tests over Core, over the machine, and over this document (§15). |

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

A single character arrives **lowercase** — `mapKey` returns `k` when `k.length === 1`, because
unshifted letters are lowercase and the real character has to reach Logo. So `HandleKey` uppercases
one-character tokens before routing them, which is what makes the key commands (`N` new session,
`D` delete, `R` run, `E` editor …) actually fire in a browser. Text being typed is untouched and
Logo's own case rules live in the reader (`|LOGO|` preserves case), so nothing else changes. The
unit harness always pressed `"N"`, which is exactly why two browser-only bugs hid here: a test that
simulates a keyboard must simulate *this* keyboard.

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
the line runs out of window.

**The line being typed owns a contiguous block of grid rows, and the block is described by one
record.** `PaintedBlock` is the geometry of the draft as it was actually drawn: `Anchor` (the first
row it took, in absolute grid coordinates), `Base` (the prompt column), `Length`, `FirstRow` (which
character the window starts on once part of the line has scrolled out), `VisibleRows`, and
`Prompted` (whether the `> ` was part of it). Every repaint erases the **old** block *and* the new
one before drawing, which is the whole lesson of this section:

- Erasing only the new geometry leaves the tail of the old one behind as a **ghost** — a duplicated
  draft that then gets committed into the transcript and saved to disk.
- The block's `Anchor` moves under the painter: Logo printing pushes it down, `Alt-T` changes the
  window, an overlay closing restores a different screen, and `TextScreen.ScrollUp` shifts every row.
  `ScrollUp` therefore raises the `Scrolled` event and the machine subtracts that count from the
  anchor, so the block keeps pointing at its own rows.
- `TextScreen.DeleteRows(start, count)` removes rows outright (used when a multi-row draft is
  retired into the transcript, so the line does not have to be re-typed as one long wrap).

After painting, the engine's cursor is **parked just past the draft** (`CursorRow`/`CursorCol`).
That is what guarantees output can never land on top of the line being typed, and it has to happen
as part of painting, not only when the listener submits: a program started from *outside* the
listener (`Alt-R` in the editor, `Alt-O` import) prints into the same grid, and without parking, its
output overwrote the draft rows and the next repaint deleted the evidence. The commit path
consequently resets `CursorCol = 0` before writing, because the parked cursor is deliberately past
the rows it is deleting.

`ReleaseBlock` (the draft retiring into the transcript) refuses to do anything while an overlay owns
the grid, otherwise closing `Alt-L` would cut a hole in the quick reference it had painted over.

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

**The editor is bound to the session it was opened for** (`_editorSessionName`), not to "whatever
is current". `ApplyEditor()` copies the buffer into that session in memory on every keystroke, so
save, download and the file list all see the text as typed; `CommitEditor()` is `ApplyEditor()` plus
`SaveAsync()`. Serialising the whole store per keystroke was the first attempt and is far too slow —
a session's picture can be megabytes. `Alt-L` while editing opens the manager over the editor and
`Alt-S`/`Alt-D` inside it act on the bound session, so a child cannot finish editing `SESSION3`,
have the current session change underneath, and write their work into `SESSION1`.

**A save that failed is reported as a failure.** `writeStore` returns false when localStorage throws
(`QuotaExceededError`), `SaveAsync` returns that result, and `Say`/`Announce` print either `SAVED` or
an explicit refusal. Chrome grows a persistent origin's quota on demand, so the honest failure path
matters: a child who is told `SAVED` and then loses their work in a crash is worse off than one told
the machine could not write it.

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

## 14. Command reference

**136 commands and 8 syntactic words.** Everything from the first `###` heading below to the last row
of the syntactic-word table is generated from `PrimitiveTable.CreateDefault()` — the very table that
runs the language — so it cannot list a command that does not exist or omit one that does. After
adding a primitive, re-run the generator:

```
dotnet run --project Tools/DocGen          # rewrite the tables in place
dotnet run --project Tools/DocGen -- --check  # report drift, change nothing, exit 1 if stale
```

`Tools/DocGen` replaces only the block between the two `<!-- generated:command-tables -->` sentinels,
so the prose above and below stays hand-written. It is deliberately **not** in the solution, so
`dotnet build` and `dotnet publish` never touch it. `CommandReferenceTests` (§15) checks the same
facts in CI, which is what makes forgetting the generator a build failure instead of a stale table
nobody notices.

Columns: **Inputs** is how many expressions a bare call pulls off the source stream (`many` = it
keeps pulling to the end of its group, `syntax` words read raw items themselves). **Kind** is
`reporter` when it gives back a value, `command` when it does not, `syntax` when the engine
intercepts the word before dispatch.

### Calling conventions

Logo is **prefix** and **input-consuming**, which is the single biggest adjustment for a child
coming from BASIC or Python:

- `FD 100` — the command and its input are separate words; there are no call parentheses.
- `REPEAT 4 [FD 50 RT 90]` — a **list in brackets** is a bundle of commands to run.
- `"HELLO` — a quote makes the following word literal text. `[` and `(` also open a list, so
  `"([` is how a list literal is quoted; one quote is enough for a word.
- `:SIZE` — a colon reads a variable. `MAKE "SIZE 50` writes one, `PRINT :SIZE` reads it.
- `PRINT 2 + 3` — the four operators are **infix**, the only ones Logo has.
- Everything folds to **capitals** by default (`SETCASE FALSE` stops it), so `fd`, `Fd` and `FD` are
  the same command — and `TO F` collides with `FIRST`'s alias `F` and is refused.
- A fixed-arity reporter takes its inputs as **whole expressions**, so
  `FIB :N - 1 + FIB :N - 2` is one input. Write `(FIB (:N - 1)) + (FIB (:N - 2))`. See §5.
- `TO NAME :INPUTS … END` defines a procedure; `STOP` leaves it, `OUTPUT` leaves it with a value.

At the listener a reporter prints its answer and a command does not, so `PRINT 2 + 2` answers `4`
while `FD 100` answers nothing. In **EASY** mode a bare reporter also prints its value.

The keyboard shortcuts (`Alt-?`, `Alt-S`, …) are browser commands, not Logo ones — they are in §13.

### On-screen help

`Alt-?` pages through the same families on the emulated screen. `HELP REPEAT` explains one command,
`HELP LIST` explains a whole family, `HELP ALL` lists the families, and a misspelling gets a
"did you mean" suggestion from the closest real name. `WHO` reports what *the child* has defined.

<!-- generated:command-tables:start -->

### Turtle — 27 commands

| Command | Inputs | Kind | Does what | Try |
|---|---|---|---|---|
| `ARC` | 2 | command | Draw an arc: the number of degrees to sweep, then the radius. | `ARC 180 40` |
| `BK`<br/>*or BACK* | 1 | command | Move the turtle backward without turning around. | `BK 20` |
| `CIRCLE` | 1 | command | Draw a circle with its centre that many steps ahead of the turtle. | `CIRCLE 40` |
| `CLEAN` | 0 | command | Erase the picture but leave the turtle where it is. | `CLEAN` |
| `CS`<br/>*or CLEARSCREEN* | 0 | command | Erase the picture and send the turtle home. | `CS` |
| `FD`<br/>*or FORWARD* | 1 | command | Move the turtle forward by a number of steps. | `FD 50` |
| `HEADING` | 0 | reporter | Report the direction the turtle is facing, in degrees. | `PRINT HEADING` |
| `HOME` | 0 | command | Send the turtle to the middle of the screen, pointing north. | `HOME` |
| `HT`<br/>*or HIDETURTLE* | 0 | command | Hide the turtle so only the picture shows. | `HT` |
| `LABEL` | 1 | command | Print text on the graphics page where the turtle stands. | `LABEL "HELLO` |
| `LT`<br/>*or LEFT* | 1 | command | Turn the turtle left (counter-clockwise) by that many degrees. | `LT 90` |
| `PC` | 0 | reporter | Report the pen's current colour number. | `PRINT PC` |
| `PD`<br/>*or PENDOWN* | 0 | command | Put the pen down so the turtle draws again. | `PD FD 100` |
| `PENSIZE`<br/>*or SETPS* | 1 | command | Make the pen thicker, in pixels. | `PENSIZE 3` |
| `POS` | 0 | reporter | Report the turtle's position as a list [x y]. | `PRINT POS` |
| `PU`<br/>*or PENUP* | 0 | command | Lift the pen so the turtle moves without drawing. | `PU FD 100` |
| `RT`<br/>*or RIGHT* | 1 | command | Turn the turtle right (clockwise) by that many degrees. | `RT 90` |
| `SETH`<br/>*or SETHEADING* | 1 | command | Point the turtle at an absolute heading: 0 north, 90 east, 180 south, 270 west. | `SETH 180` |
| `SETPC`<br/>*or SETPENCOLOR, PENCOLOR* | 1 | command | Choose the pen colour: 0 black, 1 green, 2 white, 3 purple, 4 orange, 5 blue. | `SETPC 4` |
| `SETX` | 1 | command | Move the turtle to an x position without drawing. | `SETX 50` |
| `SETXY` | 2 | command | Move the turtle to a position given as two numbers or as a list [x y]. | `SETXY 20 30` |
| `SETY` | 1 | command | Move the turtle to a y position without drawing. | `SETY 40` |
| `SHAPE`<br/>*or SETSHAPE* | 1 | command | Choose the turtle's shape: 0 arrow, 1 triangle, 2 turtle, 3 dot, 4 car, 5 ball. | `SHAPE 2` |
| `ST`<br/>*or SHOWTURTLE* | 0 | command | Show the turtle. | `ST` |
| `TOWARDS` | 2 | reporter | Report which way the turtle would have to point to head straight at that spot. | `PRINT TOWARDS 30 40` |
| `XCOR` | 0 | reporter | Report the turtle's x position. | `PRINT XCOR` |
| `YCOR` | 0 | reporter | Report the turtle's y position. | `PRINT YCOR` |

### Math — 19 commands

| Command | Inputs | Kind | Does what | Try |
|---|---|---|---|---|
| `ABS`<br/>*or ABSOLUTE* | 1 | reporter | A number's size, ignoring whether it is negative. | `PRINT ABS -7` |
| `ARCTAN` | 1 | reporter | The angle whose tangent is that number, in degrees. | `PRINT ARCTAN 1` |
| `COS` | 1 | reporter | Cosine of an angle in degrees. | `PRINT COS 60` |
| `DIFFERENCE` | 2 | reporter | Subtract the second number from the first. | `PRINT DIFFERENCE 10 4` |
| `MINUS` | 1 | reporter | Flip a number's sign. | `PRINT MINUS 5` |
| `NUMBERP`<br/>*or ISNUMBER, NUMBER?* | 1 | reporter | Report TRUE when the input is a number. | `PRINT NUMBERP 5` |
| `PI` | 0 | reporter | The ratio of a circle's round to its straight across. | `PRINT PI` |
| `POWER` | 2 | reporter | Raise the first number to the power of the second. | `PRINT POWER 2 10` |
| `PRODUCT` | 2 | reporter | Multiply two numbers. | `PRINT PRODUCT 6 7` |
| `QUOTIENT` | 2 | reporter | Divide the first number by the second. | `PRINT QUOTIENT 100 4` |
| `RANDOM` | 1 | reporter | Report a whole number from 0 up to (but not including) that number. | `PRINT RANDOM 6` |
| `REMAINDER`<br/>*or MOD, MODULO* | 2 | reporter | What is left over after dividing. | `PRINT REMAINDER 10 3` |
| `ROUND` | 1 | reporter | Round a number to the nearest whole number. | `PRINT ROUND 2.6` |
| `SIGN` | 1 | reporter | Report -1, 0 or 1 depending on whether the number is below, equal to, or above zero. | `PRINT SIGN -8` |
| `SIN` | 1 | reporter | Sine of an angle in degrees. | `PRINT SIN 30` |
| `SQRT`<br/>*or ROOT* | 1 | reporter | The square root of a number. | `PRINT SQRT 81` |
| `SUM` | 2 | reporter | Add two numbers. | `PRINT SUM 3 4` |
| `TAN` | 1 | reporter | Tangent of an angle in degrees. | `PRINT TAN 45` |
| `TRUNC`<br/>*or TRUNCATE* | 1 | reporter | Cut off everything after the decimal point. | `PRINT TRUNC 2.9` |

### Words — 13 commands

| Command | Inputs | Kind | Does what | Try |
|---|---|---|---|---|
| `BUTFIRST`<br/>*or BF* | 1 | reporter | Everything except the first letter of a word, or the first item of a list. | `PRINT BUTFIRST "LOGO` |
| `BUTLAST`<br/>*or BL* | 1 | reporter | Everything except the last letter of a word, or the last item of a list. | `PRINT BUTLAST "LOGO` |
| `EXPLODE` | 1 | reporter | Break a word into a list of its letters. | `PRINT EXPLODE "LOGO` |
| `FIRST`<br/>*or F* | 1 | reporter | The first letter of a word, or the first item of a list. | `PRINT FIRST "LOGO` |
| `IMPLODE` | 1 | reporter | Join a list of letters back into one word. | `PRINT IMPLODE [L O G O]` |
| `LAST`<br/>*or L* | 1 | reporter | The last letter of a word, or the last item of a list. | `PRINT LAST "LOGO` |
| `LOWERCASE`<br/>*or LC* | 1 | reporter | Turn a word into small letters. | `PRINT LOWERCASE "LOGO` |
| `MEMBER`<br/>*or MEMBERP* | 2 | reporter | Report TRUE when the first input appears inside the second. | `PRINT MEMBER "CAT "SCATTER` |
| `SENTENCE`<br/>*or SE* | many | reporter | Join lists (or words) into one list. | `PRINT SE [1 2] [3]` |
| `SUBSTRING`<br/>*or SUBSTR* | 3 | reporter | Part of a word: the word, where to start counting from 1, and how many letters. | `PRINT SUBSTRING "LOGO 2 2` |
| `UPPERCASE`<br/>*or UC* | 1 | reporter | Turn a word into big letters. | `PRINT UPPERCASE "logo` |
| `WORD` | many | reporter | Join words together into one word. | `PRINT WORD "HELLO "THERE` |
| `WORDP`<br/>*or ISWORD* | 1 | reporter | Report TRUE when the input is a word. | `PRINT WORDP "HELLO` |

### Lists — 13 commands

| Command | Inputs | Kind | Does what | Try |
|---|---|---|---|---|
| `ASSOC` | 2 | reporter | Look a word up in a list of pairs, like a tiny dictionary. | `PRINT ASSOC "RED [[RED 1] [BLUE 5]]` |
| `CAR` | 1 | reporter | The first item of a list (the classic Lisp name Logo kept). | `PRINT CAR [1 2 3]` |
| `CDR` | 1 | reporter | A list without its first item. | `PRINT CDR [1 2 3]` |
| `COUNT` | 1 | reporter | How many letters a word has, or how many items a list has. | `PRINT COUNT [1 2 3]` |
| `DELETE` | 2 | reporter | Remove an item from a list at a position counting from 1. | `PRINT DELETE 2 [1 2 3]` |
| `FPUT` | 2 | reporter | Add an item to the end of a list. | `PRINT FPUT 4 [1 2 3]` |
| `ITEM` | 2 | reporter | The item at a position in a list, counting from 1. | `PRINT ITEM 2 [RED GREEN BLUE]` |
| `LIST` | many | reporter | Make a list out of its inputs, keeping lists as single items. | `PRINT LIST [1 2] 3` |
| `LISTP` | 1 | reporter | Report TRUE when the input is a list. | `PRINT LISTP [1 2]` |
| `LPUT` | 2 | reporter | Add an item to the front of a list. | `PRINT LPUT 1 [2 3]` |
| `REVERSE`<br/>*or REV* | 1 | reporter | Flip a list or word end-to-front. | `PRINT REVERSE [1 2 3]` |
| `SETITEM` | 3 | reporter | Replace one item of a list and report the new list. | `MAKE "COLORS SETITEM 2 :COLORS "TEAL` |
| `SUBSTITUTE` | 3 | reporter | Replace every copy of an item in a list with another. | `PRINT SUBSTITUTE 2 "X [1 2 2 3]` |

### Tests — 8 commands

| Command | Inputs | Kind | Does what | Try |
|---|---|---|---|---|
| `AND` | 2 | reporter | TRUE only when both inputs are TRUE. | `PRINT AND GREATER 5 3 LESS 5 9` |
| `EMPTY`<br/>*or EMPTYP* | 1 | reporter | Report TRUE when a word or list has nothing in it. | `PRINT EMPTY []` |
| `EQUAL`<br/>*or EQUALP, EQ* | 2 | reporter | Report TRUE when two things are exactly the same. | `PRINT EQUAL 2 + 2 4` |
| `EQUIV`<br/>*or EQUIVP* | 2 | reporter | Report TRUE when two numbers are equal. | `PRINT EQUIV 4 2 + 2` |
| `GREATER`<br/>*or GREATERP* | 2 | reporter | Report TRUE when the first number is bigger. | `PRINT GREATER 3 5` |
| `LESS`<br/>*or LESSP* | 2 | reporter | Report TRUE when the first number is smaller. | `PRINT LESS 3 5` |
| `NOT` | 1 | reporter | Flip TRUE into FALSE and FALSE into TRUE. | `PRINT NOT EQUAL 3 4` |
| `OR` | 2 | reporter | TRUE when either input is TRUE. | `PRINT OR EQUAL 1 1 EQUAL 2 3` |

### Variables — 10 commands

| Command | Inputs | Kind | Does what | Try |
|---|---|---|---|---|
| `ERALL` | 0 | command | Forget every procedure and variable written this session. | `ERALL` |
| `ERASE`<br/>*or ER* | 1 | command | Forget one variable or procedure. | `ERASE "SIZE` |
| `GET` | 2 | reporter | Read back a property stored with PUT. | `PRINT GET "PLAYER "SCORE` |
| `LISTPROC` | 0 | reporter | Report the names of every procedure the child wrote. | `PRINT LISTPROC` |
| `MAKE`<br/>*or SET* | 2 | command | Give a variable a value. | `MAKE "SIZE 50` |
| `NAME` | 1 | reporter | Report where a value is kept, or nothing when the input is not a name. | `PRINT NAME "SIZE` |
| `NAMES`<br/>*or LISTVARS* | 0 | reporter | Report everything defined, as [variables] [procedures]. | `PRINT NAMES` |
| `PUT` | 3 | command | Store a value under a property of a name, like a tiny notebook. | `PUT "PLAYER "SCORE 10` |
| `REMPROP` | 2 | command | Forget one property stored with PUT. | `REMPROP "PLAYER "SCORE` |
| `THING` | 1 | reporter | Report what a variable holds. | `PRINT THING "SIZE` |

### Repeating — 15 commands

| Command | Inputs | Kind | Does what | Try |
|---|---|---|---|---|
| `APPLY` | 2 | reporter | Run a procedure with a list of inputs. | `PRINT APPLY "MAXIMUM [3 9]` |
| `CATCH` | 1 | reporter | Run a list, but let a THROW jump back out of it with a value. | `PRINT CATCH [IF EQUAL 1 1 [THROW "CAUGHT]]` |
| `EVAL` | 1 | reporter | Work out a list as if it were typed, then run it. | `PRINT EVAL [2 + 3]` |
| `FOR` | 4 | syntax | Count a variable from a start to a stop, stepping by a number. | `FOR [I 1 10 1] [PRINT :I]` |
| `FOREACH` | 2 | syntax | Run a list once for each item, with ? holding the current item. | `FOREACH [1 2 3] [PRINT ?]` |
| `IDENTITY` | 1 | reporter | Report a procedure's name as a word, for use with APPLY. | `PRINT IDENTITY "SQUARE` |
| `IF` | 2 | syntax | Run the first list when the test is true, the second when it is not. | `IF GREATER :N 5 [PRINT "BIG] [PRINT "SMALL]` |
| `IFELSE` | 3 | reporter | Report the second input when the test is true, otherwise the third. | `PRINT IFELSE GREATER 5 3 "YES "NO` |
| `LOCAL` | 1 | syntax | Give a procedure its own private variables. | `TO TRIAL :N LOCAL [TOTAL] MAKE "TOTAL 0` |
| `REPEAT` | 2 | syntax | Run a list of commands that many times. | `REPEAT 4 [FD 50 RT 90]` |
| `RUN` | 1 | syntax | Run a list of commands, or a procedure name someone gave you. | `RUN [FD 50 RT 90]` |
| `TEST` | 1 | command | Remember whether something is true, for IF to act on later. | `TEST GREATER :N 10 IF [PRINT "BIG]` |
| `THROW` | 1 | command | Jump back to the nearest CATCH, carrying a value with it. | `THROW "DONE` |
| `UNTIL` | 2 | syntax | Keep running a list until the test becomes true. | `UNTIL [EQUAL :N 5] [MAKE "N :N + 1]` |
| `WHILE` | 2 | syntax | Keep running a list while the test stays true. | `WHILE [LESS :N 5] [MAKE "N :N + 1]` |

### Printing — 8 commands

| Command | Inputs | Kind | Does what | Try |
|---|---|---|---|---|
| `FILL`<br/>*or REPEATW* | 2 | reporter | Repeat a word a number of times, for neat tables and borders. | `PRINT FILL "- 20` |
| `FORM` | 3 | reporter | Print a value right-aligned in that many spaces, with a number of decimal places. | `PRINT FORM 3.14159 8 2` |
| `PRINT`<br/>*or PR* | many | command | Print anything, then start a new line. | `PRINT "HELLO` |
| `READ` | 0 | reporter | Wait for a number to be typed and report it. | `MAKE "SIZE READ` |
| `READLINE` | 0 | reporter | Wait for a whole line to be typed and report it as a word. | `MAKE "NAME READLINE` |
| `READLIST` | 0 | reporter | Wait for a list to be typed and report it. | `MAKE "ITEMS READLIST` |
| `SHOW`<br/>*or SH* | 1 | command | Print one value on its own line. | `SHOW 2 + 2` |
| `TYPE` | many | command | Print without moving to a new line. | `TYPE "HELLO TYPE " THERE` |

### Screen — 11 commands

| Command | Inputs | Kind | Does what | Try |
|---|---|---|---|---|
| `BG` | 0 | reporter | Report the graphics background colour. | `PRINT BG` |
| `CLS`<br/>*or CLR* | 0 | command | Clear the text window, leaving the turtle's picture alone. | `CLS` |
| `CURSOR` | 0 | reporter | Report where the text cursor is, as a list [row column]. | `PRINT CURSOR` |
| `GRAPHICS`<br/>*or GFX, HIRES* | 0 | command | Show the whole screen as the turtle's graphics page. | `GRAPHICS` |
| `INVERSE`<br/>*or INV* | 0 | command | Print in reverse video (bright block letters). | `INVERSE PRINT "BIG` |
| `LOWER` | 1 | command | Keep that many lines of text at the bottom, with graphics above. | `LOWER 4` |
| `NORMAL` | 0 | command | Go back to ordinary text. | `NORMAL` |
| `SCROLL` | 0 | command | Slide the text up one line. | `SCROLL` |
| `SETBG` | 1 | command | Choose the background colour of the graphics page. | `SETBG 0` |
| `SETCURSOR` | 2 | command | Move the text cursor to a row and column, counting from 1. | `SETCURSOR 5 1` |
| `TEXT` | 0 | command | Show the whole screen as text again. | `TEXT` |

### Logo itself — 12 commands

| Command | Inputs | Kind | Does what | Try |
|---|---|---|---|---|
| `ADVANCED` | 0 | command | Strict Apple Logo errors and the full-screen editor keys. | `ADVANCED` |
| `BYE` | 0 | command | Save the session and go back to the splash screen. | `BYE` |
| `CASE` | 0 | reporter | Report whether Logo is folding words to capitals. | `PRINT CASE` |
| `EASY` | 0 | command | Friendly errors, hints, and the split graphics/text screen. | `EASY` |
| `HELP` | 1 | command | Explain one Logo command, or list the command families with HELP ALL. | `HELP REPEAT` |
| `MODE` | 1 | command | Switch between EASY and ADVANCED. | `MODE ADVANCED` |
| `PROCTEXT` | 1 | reporter | Report the source of a procedure so it can be printed or saved. | `PRINT PROCTEXT "SQUARE` |
| `RANDSEED` | 1 | command | Choose the seed for RANDOM so a game can be repeated exactly. | `RANDSEED 5` |
| `SETCASE` | 1 | command | Tell Logo whether to turn words into capital letters. | `SETCASE FALSE` |
| `SHOWSEED` | 0 | reporter | Report the seed RANDOM is using. | `PRINT SHOWSEED` |
| `VERSION` | 0 | reporter | Report which Logo this is. | `PRINT VERSION` |
| `WHO` | 0 | command | Report the names of everything defined this session. | `WHO` |

**Syntactic words — 8.** The engine intercepts these before
primitive dispatch, so they are not callable procedures.

| Word | Does what | Try |
|---|---|---|
| `+` | Add the numbers on either side. | `PRINT 2 + 3` |
| `-` | Take the second number away from the first. | `PRINT 10 - 3` |
| `*` | Multiply the numbers on either side. | `PRINT 6 * 7` |
| `/` | Divide the first number by the second. | `PRINT 100 / 4` |
| `TO` | Start a new procedure: give it a name, its inputs, then the commands. | `TO SQUARE :SIZE REPEAT 4 [FD :SIZE RT 90] END` |
| `END` | Finish the procedure that TO started. | `END` |
| `STOP` | Finish a procedure right away without giving back a value. | `IF EQUAL :N 0 STOP` |
| `OUTPUT` | Finish a procedure and give back a value. | `OUTPUT :SIZE * 2` |

<!-- generated:command-tables:end -->

---

## 15. Testing

303 tests, all green. `dotnet build` and `dotnet test` are clean; `dotnet publish` produces a
working offline bundle. **This is the only place the total is stated** — a test count cannot verify
itself, so repeating it elsewhere just multiplies the staleness.

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
- **Painter** (`PainterReviewTests.cs`) — one test per finding from the second review pass, all
  grouped under `// ---- Issue N:` headers: block ownership, ghosts, cursor parking, scroll tracking,
  overlay stashing, `READ` semantics, editor↔session binding, and the lowercase-key bugs.
- **This document** (`CommandReferenceTests.cs`) — the command reference must list exactly the
  primitives that exist (both directions: nothing missing, nothing left behind after a rename), with
  the aliases, the family counts, the totals and the version it claims, and every section heading must
  be in the contents. The doc is linked into the test output directory by a `Content Include`, so
  these run without depending on a working directory.

Two rules the tests encode, because they are the things that broke during development:

1. A test that interrupts a program must **pump the queue** until `IsBusy` is true before pressing
   `ESC`. `Task.Delay` in a synchronous test body runs nothing.
2. The fake `YieldFrameFunc` must **actually await**, or a running program can never be
   interrupted and the break tests are meaningless.

### In a real browser

Unit tests cannot see a canvas, a font, or a stretched bitmap, so four Playwright scripts run
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
- `paint.mjs` — the listener painter: `Alt-L` → `N` really creates a session (the lowercase-key
  bug), a long typed line is painted exactly once, `Alt-T` mid-line neither duplicates nor loses it,
  `Alt-E` in the editor is a no-op, and an overlay opening from the editor is not painted over.

**These scripts found three bugs no unit test could see**, all three caused by the real browser
delivering lowercase single characters or by asynchronous overlay timing. Treat the browser pass as
part of the test suite, not a smoke test. Two operational traps: a `dotnet run --no-build` server
caches the boot manifest, so after a rebuild the browser silently gets the **old** wasm until the
server restarts — verify by logging `request` events and checking the requested
`MaturinLogo.Core.<hash>.wasm` name; and a deterministic `QuotaExceededError` needs
`setItem` stubbed to throw, because Chromium grows a persistent origin's quota on demand.

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

### Second review pass

The next pass found seven more. All were reproduced as failing tests first, and all are covered by
`PainterReviewTests.cs` (23 tests, grouped by `// ---- Issue N:`).

| # | Severity | Finding | Fix |
|---|---|---|---|
| 1 | Critical | The crash repro was still live in a new form: a repaint measured against a stale layout indexed outside the grid | `PaintedBlock` — the draft's geometry is recorded as drawn and erased as drawn |
| 2 | High | A draft that grew, then shrank, left its old tail behind as a ghost that was then committed and saved | erase the old block *and* the new one before painting |
| 3 | High | Logo output printed onto the draft rows and the next repaint deleted it (`PRINT 99` from the editor vanished) | park the engine cursor past the draft in `PaintInput`, not only at submit |
| 4 | Medium | Scrolling moved the grid but not the block, so the anchor pointed at the wrong rows | `TextScreen.Scrolled` event adjusts `PaintedBlock.Anchor` |
| 5 | Medium | Closing an overlay re-derived painting state from the cursor, losing the draft | the stash carries the block |
| 6 | Low | `Alt-L` → `N` silently did nothing in a browser | `HandleKey` uppercases single-character tokens |
| 7 | Low | A committed command was spliced into a message (`TEXT - THE > PRINT 101N IS TEXT`) | `CommitDraftToTranscript` forces `CursorCol = 0` |

Two more came out of the browser pass rather than the review: `Alt-E` inside the editor repainted
the program straight over the `SESSIONS` overlay that had just opened, and a stale draft could
answer a `READ` the program never got to ask (`ReadLineAsync` clears the draft first now).

The honest summary of this round: the first fix for #1 was wrong twice. What made it wrong was
patching the *drawing* while the real invariant lives in the *ownership* — the draft has to own its
rows, nothing else may write into them, and the rows must be erased using the block's own geometry.
Once that was a single record, the duplication, the ghosts, the stranded `Alt-T` caret and the
spliced message were all the same bug.

---

## 16. Known gaps / future

`Docs/Additional Features.md` holds the wish list: localisation, an examples library, 8-bit sound
(see `Docs/8-bit Sound Code Example.htm`), an animation-speed control, about/resources screens, and
a downloadable exception log. Terrapin-only surfaces (bitmaps, controls, ports, arrays, property
lists, multi-turtle `TELL`/`ASK`) are out of scope; the primitive table leaves room for them.

---

## 17. Glossary

Terms in alphabetical order. Anything a section explains in more depth is linked.

| Term | Meaning here |
|---|---|
| **Apple IIe** | The 1983 home computer this UI imitates: 40 columns × 24 rows of text, a green or amber phosphor monitor, and a hi-res graphics page. |
| **arity** | How many inputs a command takes. Declared per primitive (`FD` is 1, `SETITEM` is 3); `many` means variadic. See [§14](#14-command-reference). |
| **block** (painted block) | The run of grid rows currently occupied by the line being typed. `PaintedBlock` records its geometry so it can be erased exactly as it was drawn. See [§4](#4-the-listener). |
| **bracketed list** `[a b c]` | Logo's way of bundling commands or values together. In `REPEAT 4 [FD 50]` the brackets hold the commands to repeat. |
| **CAR / CDR** | Lisp's names for "first item" and "everything but the first", which Logo kept alongside `FIRST` and `BUTFIRST`. |
| **command vs reporter** | A *command* does something and gives back nothing (`FD`, `MAKE`). A *reporter* computes and returns a value (`SUM`, `FIRST`). At the listener a reporter's value prints, a command's does not. |
| **cooperative yield** | The evaluator pauses every `StepSlice` steps so the browser can paint and stay responsive. Without it a long program freezes the tab and Chrome offers to kill the page. See [§5](#5-the-evaluator). |
| **CRT emulation** | The optional scanline/glow/bloom overlay on the screen. Purely cosmetic; `Alt-C` toggles it. |
| **draft** | The line the child is currently typing, before ENTER. Synonym of *line being typed*; it is what the block belongs to. |
| **fold / case folding** | Logo's rule for turning words to capitals so `fd` matches `FD`. `SETCASE FALSE` stops it, which is how `|MixedCase|` survives. |
| **ghost** | The leftover tail of an old draft that was painted but not erased, which then got committed into the transcript and saved to disk. See [§4](#4-the-listener). |
| **grid** | The 24×40 array of character cells (`TextScreen`) that is the whole text screen. Rendered to HTML by `ScreenGrid`. |
| **hi-res** | The Apple II's high-resolution graphics mode (280×192, 6 colours). Here: the canvas the turtle draws on. `GRAPHICS`/`GFX`/`HIRES` switches to it. |
| **infix** | Written *between* its operands (`2 + 3`). Logo has exactly four infix operators, `+ - * /`; everything else is prefix. |
| **listener** | Logo's name for the prompt you type commands at — the REPL. Everything in [§4](#4-the-listener) is about it. |
| **localStorage** | The browser's per-origin key/value store. Where sessions live, under one namespaced key. Nothing leaves the machine. |
| **MarkupString** | Blazor's "trust this string as HTML". Used to inject the pre-built grid markup in one node instead of diffing 960 spans, so everything Logo prints is escaped first. |
| **monotonic** | Only ever increasing. Session numbers use a persisted high-water mark, so a deleted `SESSION2` is never handed out again. |
| **Open Apple key** | The Apple II keyboard's modifier that this project maps to `Alt`. |
| **overlay** | A screen that takes the grid away from the listener: the quick reference, the session manager, the editor, the info page. A stack; the grid is stashed and restored byte-for-byte. |
| **phosphor** | The glowing coating that made a real CRT's text green or amber. `Alt-G` switches the two. |
| **primitive** | A command built into Logo, as opposed to a *procedure* the child defines. All 136 live in `PrimitiveTable`. |
| **procedure** | A user-defined command: `TO SQUARE :SIZE … END`. Stored as source text so reloading re-parses it. |
| **prompt** `> ` | The characters Logo waits for input behind. Suppressed while a program is asking a question. |
| **pull semantics** (input-consuming) | Logo's evaluation rule: a command *pulls* its inputs off the source stream after its name, which is why `FD 10 RT 90` is two calls and why `PRINT` can be variadic. The most important thing to understand about the evaluator. See [§5](#5-the-evaluator). |
| **quote** `"WORD` | Makes the following word literal text instead of a name to look up. |
| **reader** | The tokenizer: turns characters into `ProgramItem`s for the evaluator. Not "the child". |
| **reverse video** | Flipped foreground/background — bright block letters. Apple II text mode's only emphasis. `INVERSE`. |
| **scute** | One of the plates on a turtle's shell; here, one filled cell of the ASCII splash art. |
| **session** | A saved workspace (source, procedures, variables, turtle, picture, transcript, history) under a name like `SESSION1`. |
| **source stream** | The queue of items the evaluator reads from, which pull semantics consume from. |
| **stash** | The saved copy of the grid (chars, attributes, cursor, painted block) that an overlay is restored from when it closes. |
| **syntactic word** | A word the engine intercepts before primitive dispatch (`TO`, `END`, `STOP`, `OUTPUT`, `IF`, `REPEAT`…), so it reads *unevaluated* items itself. |
| **tail call / tail position** | A call that is the last thing a procedure does, so its stack frame can be thrown away and reused. Detected at runtime, not statically. See [§5](#5-the-evaluator). |
| **trampolining** | Replacing nested calls with a loop so deep recursion does not overflow the stack — what makes `COUNTDOWN 5000` work in WASM, where the CLR stack is small. |
| **transcript** | The scrollback of everything printed and typed, as opposed to the line still being edited. |
| **tree-walking** | Evaluating the parsed structure directly instead of compiling it to something else first. |
| **variadic** | Takes as many inputs as follow it (`PRINT`, `WORD`, `SE`). |
| **WASM** (WebAssembly) | What Blazor WebAssembly compiles C# to so it runs in the browser with no server. |
| **word** | Logo's type for a single token — a number, a name, or literal text. Not necessarily a "string". |
| **workspace** | Everything defined right now: procedures, variables, properties, plus the turtle and picture. What a session saves. |

---

## Revision history

| Version | Date | Changes |
|---|---|---|
| **1.0** | 2026-11-07 | First release. Complete Apple Logo II language: 136 commands and 8 syntactic words across turtle, math, words, lists, tests, variables, control, printing, screen and system. 24×40 Apple IIe screen with blinking cursor, green/amber phosphor and CRT overlay; color-fringed turtle graphics on a canvas. Splash with generated ASCII turtle; EASY/ADVANCED modes; keyboard-only `SESSIONn` sessions in localStorage with `.logo` download and import; scrollable `Alt-?` quick reference; ADVANCED full-screen editor. Reworked the listener painter around `PaintedBlock` row ownership with the engine cursor parked past the draft, which removed a family of duplicated drafts, ghosts and spliced messages. Ships with this document's contents, command reference (§14, generated from the primitive table by `Tools/DocGen`), glossary (§17) and revision history, pinned by `CommandReferenceTests` so they cannot silently drift. |
| 1.0-dev | 2026-11-07 | Pre-release milestones, in order: Logo reader, evaluator and primitive set with a cooperative step budget and `ESC` break; tail calls trampolined in both command and expression position, fixing stack overflow in classic recursion; the 40×24 grid, keyboard routing and canvas overlay ported from the AppleWorks approach in `C:\Dev\RetroWorks`; sessions, `.logo` export and offline publish; two adversarial review passes — 8 findings, then 7 more plus 3 caught only by the browser suites, all reproduced as failing tests before being fixed. |

*The version number lives in `LogoInfo` (`Major`/`Minor`) so `PRINT VERSION`, the splash, the `Alt-I`
dashboard and the quick reference can never disagree with each other. When a release changes that
number, add a row here.*
