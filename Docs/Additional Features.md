Architecture document
Localizing - Spanish, Japanese, French
Demo mode that runs through some timed typing of 10 of the Examples used, looping, with an option to "Press ESC to exit demo mode" (flashing)
Help system? Menu in the front?
List of commands / command reference with parameter/help details
Pre-loaded examples they can insert and instantly love - perhaps via Alt-L ("Learn") or Alt-X ("Examples")
Add fun 8-bit sounds, togglable in settings
About screen
Additional Resources screen
Exception log that can be downloaded and tell me if exceptions are logged to browser/web tools log
The Old Way is the Best Way quote?
Add list of supported commands to Architecture doc
Support for the sound command TOOT? It exiss in Coleco ADAM SmartLOGO.
Support for SETSPEED command? We want to see the drawing/animation as part of a "fluid" experience.
Support for SETSH "set shape" command?

A few other changes:

- Use 80 column so it's mostly taking up the width of the screen. 40 column seems too small. Is this a massive change? Stop if so and ask me about it. This may fix a bug with Alt-I that I saw where text was being cut off.
- Add a menu screen with options: Code (back to editor), Settings, and About. Settings should enable toggling of the CRT, Mode, Language, etc. If it would help, use the Appleworks-like UI we used in the RetroWorks repo.
- Add speed control slider in Settings (using arrows, like `<------*--->` like a slider), for those who can't stand animation?
- Commands in each section in HELP should be alphabetized. They shouldn't cut off at the end - word wrap accordingly.
- Add support for TOOT, SETSPEED, and SETSH (set shape) commands to play sound, control animation speed, and change the turtle shape. These exist on the Coleco ADAM SmartLOGO, but I think were also part of other LOGO implementations.

- Text should come from resource files so we can support multiple languages. English first (default), then add Spanish. We'll ignore the Apple II didn't support some of those special characters, since that shouldn't affect the interpreter (it will be limited like the Apple II). Let me know if that's an issue - stop if so.

Also, the letterboxing is bothering me. 40 column seems too small. Is there a better approach? Perhaps 80 columns? Ask me about it. This may fix a bug with Alt-I that I saw where text was being cut off.

I was allowed to create a function named "SQUARE:" and I think that should only allow alphanumeric characters.

Shape bug
