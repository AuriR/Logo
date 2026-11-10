# Maturin Logo

```
                    #
                  #####
        ###         #         ###
       #####                 #####
        ###    ###########    ###
             ##   .   .   ##
             #  .   .   .  #
             #.   .   .   .#
             ## .   .   . ##
        ###    ###########    ###
       #####                 #####
        ###        ###        ###
```

*A turtle graphics program for kids, in the spirit of Terrapin Logo and Apple Logo II on the Apple II.*

## What it is

Maturin Logo is a browser-based turtle-graphics programming environment built for children. It runs
entirely in the browser as Blazor WebAssembly — there is no server to talk to, no account to make,
and once loaded it keeps working with the network turned off. A child types `FD 100`, presses Enter,
and a turtle draws on a 40×24 grid that looks and behaves like an Apple IIe screen: one phosphor
colour, a fixed character cell, and a small set of commands they can actually memorise.

The language is deliberately close to the originals — `FD`, `RT`, `REPEAT`, `SETPC`, `TOOT` — so it
feels familiar to anyone who has seen Logo before, while staying simple enough for a first-time
programmer. EASY mode hides the advanced commands and gives the turtle most of the screen; ADVANCED
mode opens up procedures, variables and the full command set.

## Where to try it

**<https://logo.auri.net>** — open it in any modern browser and start drawing. No install, no login.

## How to run it locally

The app is a standard Blazor WebAssembly project. From this folder:

```powershell
dotnet run --project Src/MaturinLogo
```

That starts the local dev server (the URL is printed in the console) and serves the app with hot
reloading for the C# side. To produce an optimised, offline-capable bundle for deployment:

```powershell
dotnet publish Src\MaturinLogo -c Release
```

The published output includes a service worker that caches every asset, so the deployed site works
with no network at all after the first load.

## Tech breakdown

| Layer | Technology | Notes |
|---|---|---|
| Runtime | .NET 10 / Blazor WebAssembly | Compiles to WASM; runs fully client-side. |
| UI | Razor components + a `<canvas>` | The grid is drawn on canvas through a thin JS interop layer (`wwwroot/js/interop.js`). |
| Core logic | `MaturinLogo.Core` (plain class library) | Interpreter, turtle, screen, sessions and the command table — no browser dependency, so it is unit-testable. |
| Audio | Web Audio API via interop | `TOOT` plays a square wave on the audio clock; notes queue rather than block the interpreter. |
| Persistence | Browser storage + `.logo` files | Sessions are saved locally and can be downloaded/uploaded as plain-text source. |
| Offline | Service worker | Caches all assets at publish time for fully offline use. |
| Tests | xUnit (`MaturinLogo.Tests`) + Node harnesses | 498 unit tests plus three `Tools/BrowserCheck` scripts that drive the real `interop.js`. |

The split between **Core** (pure, testable C#) and the **Blazor host** (canvas, audio, storage) is
the load-bearing design decision: everything a child can type is decided in Core, so it can be tested
without a browser. The browser only paints what Core tells it to.

## Documentation

For the full picture — architecture, the command reference, testing strategy and design rationale —
see **[Docs/Architecture.md](Docs/Architecture.md)**. It is generated from the same command table that
runs the language, so it cannot drift out of date with the code.

## License

Maturin Logo is licensed under the [MIT License](LICENSE.md). Free to use, modify and distribute —
perfect for classrooms, homeschooling and anyone who wants to teach kids how to program.
