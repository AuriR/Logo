# Future Feature Planning List

The items in this file don't need to be implemented - I'm just tracking my ideas here.

## Done

- Architecture document
- Help system? Menu in the front?
- List of commands / command reference with parameter/help details
- About screen
- Add list of supported commands to Architecture doc
- Support for the sound command TOOT? It exiss in Coleco ADAM SmartLOGO.
- Support for SETSPEED command? We want to see the drawing/animation as part of a "fluid" experience.
- Support for SETSH "set shape" command?
- Add a menu screen with options: Code (back to editor), Settings, and About. Settings should enable toggling of the CRT, Mode, Language, etc. If it would help, use the Appleworks-like UI we used in the RetroWorks repo.
- Add speed control slider in Settings (using arrows, like `<------*--->` like a slider), for those who can't stand animation?
- Commands in each section in HELP should be alphabetized. They shouldn't cut off at the end - word wrap accordingly.
- Add support for TOOT, SETSPEED, and SETSH (set shape) commands to play sound, control animation speed, and change the turtle shape. These exist on the Coleco ADAM SmartLOGO, but I think were also part of other LOGO implementations.
- I was allowed to create a function named "SQUARE:" and I think that should only allow alphanumeric characters.
- SHAPE command is listed in HELP but doesn't work
- Text is cut off in ALT-I and HELP but should wrap or be better formatted.
- Commands in HELP should be alphabetized in each section. They're in seemingly random order now.
- Add support for SETSPEED command to set drawing speed 25-100. Ignore speed and draw instantly if accessibility setting is set in the OS. I believe that's simple OS/CSS media query.
- Add a settings screen to toggle settings in one place, such as CRT mode, Monitor Color, Drawing Speed, Easy/Advanced Mode.
- When turtle spins, it should animate to its position.
- Support more than the 6 colors. Support 16 colors. We're in 2026, it's OK...
- Add a WHAT'S NEW option when loading the application. It should also be available from Settings screen.
- Add About option in Settings that shows the same text as the start screen.

## Remaining

Localizing - Spanish, Japanese, French
Demo mode that runs through some timed typing of 10 of the Examples used, looping, with an option to "Press ESC to exit demo mode" (flashing)
Pre-loaded examples they can insert and instantly love - perhaps via Alt-L ("Learn") or Alt-X ("Examples")
Add fun 8-bit sounds, togglable in settings
Additional Resources screen
Exception log that can be downloaded and tell me if exceptions are logged to browser/web tools log
Text should come from resource files so we can support multiple languages. English first (default), then add Spanish. We'll ignore the Apple II didn't support some of those special characters, since that shouldn't affect the interpreter (it will be limited like the Apple II). Let me know if that's an issue - stop if so.
10 Examples with basics + animations + explanation and make it look like code is being entered, also available from help screen to view the code, optionally enter the code to run it, with question whether they want to run CLEAN first
Demo Mode (D at startup, and available from settings), shows off Examples, rotating through them
Demo Mode should run at speed set to 50.
Access examples list from ALT-X, with keys 0-9 to insert the appropriate example.
Should we support saving the way LOGO did, in addition to creating the default .logo file? Perhaps that would be good, so they could save when they want? What are the save commands?
Plan: Add a tutorial mode, accessible from the main screen by pressing T.

## Status of the bug list above — shipped in 1.1

All ten are done. What each one turned out to be:

| Item | What it took |
|---|---|
| `TO SQUARE:` accepted | `LogoReader.IsValidName` (letters, digits, underscore) enforced in `Engine.DefineProcedure` and a new `Args.Variable`. `MAKE`/`THING`/`ERASE`/`PUT`/`GET`/`REMPROP`/`LOCAL` and a procedure's input names all check it. A number is still a legal name, as Logo intends, and an old session with a punctuation name loses that one procedure on load rather than failing to load. |
| `SHAPE` listed but broken | The engine was setting the shape correctly; `SetTurtleAsync` never put it in the JSON. Six shapes are now drawn: arrow, triangle, turtle, dot, car, ball. |
| Text cut off in ALT-I and HELP | One `TextWrap` helper in Core, used by the reference, the dashboard, the session list and both new pages. Nothing is truncated and nothing ends in an ellipsis — a long description wraps onto an indented row. |
| HELP not alphabetised | `QuickReference` sorts each family by name. `Tools/DocGen` already sorted the doc's tables, which is why the two disagreed. |
| `SETSPEED` 25–100, ignore when accessibility is on | `SETSPEED`/`SPEED`/`GETSPEED`. 100 is no pause, 25 is 450 ms a frame. `prefers-reduced-motion` is read in `interop.js`, pushed into the engine, and overrides the range; the CRT scanline animation stops under the same query. |
| Settings screen | `Alt-,` (and the `SETTINGS` command): monitor, CRT, mode, drawing speed as a slider, What's New, About. |
| Turtle should animate | The canvas lerps position, takes the shortest arc for heading (so `RT 350` turns ten degrees rather than unwinding three hundred and fifty), and fades for `HT`/`ST`. |
| 16 colours | `interop.js` had six and folded `SETPC 6-15` onto them with a modulo. All sixteen `ApplePalette.Hex` values are drawn now; only the six real hi-res colours keep the chromatic fringe. |
| WHAT'S NEW on load and in Settings | A paged page, offered as `W` on the splash in bright text only when the stored version is not the running one. Reading it marks it read. |
| About in Settings | `SplashArt` gained a non-interactive variant, so About is the same drawing as the start screen with the invitation replaced — one painter, so they cannot disagree. |

## Shipped in 1.2

Three more items from the list above:

| Item | What it became |
|---|---|
| `TOOT` (Coleco ADAM SmartLOGO) | `TOOT 440` and `TOOT "A4 0.5` — hertz or a note name, length optional. A square wave on the Web Audio clock, following `8-bit Sound Code Example.htm`. Notes queue rather than block, so a tune never stops the interpreter and `ESC` still works mid-note. |
| Fun 8-bit sounds, togglable in settings | `SETSOUND`/`SOUND` in the language and a `SOUND` row in `Alt-,`, remembered with the monitor colour. Off also silences the notes already queued. |
| 16 colours | Went further than the palette itself: `VIEWCOLORS` (`Alt-V`) draws all sixteen with the number a child types into `SETPC`, on the canvas, where a colour can actually be shown. |
| `VIEWSHAPES` | Not on the list — it came from the same idea one turn later. `VIEWSHAPES` (`Alt-W`) draws all six turtle shapes with the number a child types into `SHAPE`, painted by the turtle's own painter so the chart cannot promise a shape the turtle cannot wear. `Alt-V` and `Alt-W` switch between the two pictures. |

`SETSH` was already covered by `SHAPE` in 1.1 — same command, Apple Logo's name for it.

Still open from this list: localisation and resource files, 80 columns, demo mode, an examples
library, additional resources, and a downloadable exception log.

80 columns was left alone because the wrapping above fixes the cut-off text that made 40 look too
small, and the grid is 40x24 everywhere — the reader, the painter, the sessions format and the
canvas split. See §17 of Architecture.md.


(the items in this file don't need to be implemented - I'm just tracking my ideas here)
