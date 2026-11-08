using MaturinLogo.Core.Interpreter;
using MaturinLogo.Core.Screen;
using MaturinLogo.Core.Values;

namespace MaturinLogo.Core.Primitives;

/// <summary>
/// Screen layout commands. Apple Logo swapped the whole screen between text and hi-res graphics;
/// EASY mode instead keeps a live strip of text at the bottom (Logo's LOWER) so a child can type
/// and watch the turtle at the same time.
/// </summary>
public static class ScreenPrimitives
{
    public static void Register(PrimitiveTable t)
    {
        t.Add("TEXT", 0, "Show the whole screen as text again.", "TEXT",
            CommandFamily.Screen, (e, a, _) => { e.ShowFullText(); e.Host.Invalidate(); return Args.Done(); });

        t.Add("GRAPHICS", 0, "Show the whole screen as the turtle's graphics page.", "GRAPHICS",
            CommandFamily.Screen, (e, a, _) => { e.ShowFullGraphics(); e.Host.Invalidate(); return Args.Done(); },
            aliases: new[] { "GFX", "HIRES" });

        t.Add("LOWER", 1, "Keep that many lines of text at the bottom, with graphics above.", "LOWER 4",
            CommandFamily.Screen, (e, a, _) =>
            {
                int lines = (int)Math.Round(Args.Number(a, 0, "LOWER", e.Mode));
                if (lines < 1 || lines > TextScreen.Rows - 2)
                    throw new LogoException(LogoErrorCode.OutOfRange, ErrorText.OutOfRange("LOWER", e.Mode),
                        $"LOWER takes a number from 1 to {TextScreen.Rows - 2}.", subject: "LOWER");
                e.ShowLower(lines);
                e.Host.Invalidate();
                return Args.Done();
            });

        t.Add("CLS", 0, "Clear the text window, leaving the turtle's picture alone.", "CLS",
            CommandFamily.Screen, (e, a, _) => { e.Screen.ClearText(); e.Host.Invalidate(); return Args.Done(); },
            aliases: new[] { "CLR" });

        t.Add("SETBG", 1, "Choose the background colour of the graphics page.", "SETBG 0",
            CommandFamily.Screen, (e, a, _) =>
            {
                int color = Args.Color(a, 0, "SETBG", e.Mode);
                e.Scene.Background = color;
                e.Host.Invalidate();
                return Args.Done();
            });

        // VIEWCOLORS is a viewer rather than a drawing: it opens over the listener and closes back
        // onto it, exactly like the quick reference. Putting the chart in the child's own picture
        // instead would have saved it into the session and exported it into their .logo file, so
        // their spiral would come back with sixteen boxes painted over it.
        t.Add("VIEWCOLORS", 0, "Show every pen colour with its number and name.", "VIEWCOLORS",
            CommandFamily.Screen, (e, a, _) =>
            {
                e.RequestColors();
                return Args.Done();
            }, aliases: new[] { "VIEWCOLOR", "COLORS", "COLOURS" });

        t.Add("SETSPEED", 1, "Choose how fast the turtle draws, from 25 (slow) to 100 (fast).", "SETSPEED 60",
            CommandFamily.Screen, (e, a, _) =>
            {
                e.Speed = Args.Speed(a, 0, "SETSPEED", e.Mode);
                return Args.Done();
            });

        // SPEED is the reporter, as it is in Apple Logo, so `PRINT SPEED` answers rather than
        // trying to set something. GETSPEED exists because a child who learned SETSPEED guesses it.
        t.Add("SPEED", 0, "Report how fast the turtle draws, from 25 to 100.", "PRINT SPEED",
            CommandFamily.Screen, (e, a, _) => Args.Number(e.Speed), isReporter: true,
            aliases: new[] { "GETSPEED" });

        // SETTINGS opens the app's own settings screen. The machine notices the request after the
        // line finishes, exactly as it does for BYE, because opening an overlay in the middle of a
        // program would steal the screen from the drawing the child is watching.
        t.Add("SETTINGS", 0, "Open the settings screen: monitor, CRT, mode and drawing speed.", "SETTINGS",
            CommandFamily.System, (e, a, _) =>
            {
                e.RequestSettings();
                return Args.Done();
            });

        t.Add("BG", 0, "Report the graphics background colour.", "PRINT BG",
            CommandFamily.Screen, (e, a, _) => Args.Number(e.Scene.Background), isReporter: true);

        t.Add("INVERSE", 0, "Print in reverse video (bright block letters).", "INVERSE PRINT \"BIG",
            CommandFamily.Screen, (e, a, _) => { e.Screen.PenAttr = CellAttr.Reverse; return Args.Done(); },
            aliases: new[] { "INV" });

        t.Add("NORMAL", 0, "Go back to ordinary text.", "NORMAL",
            CommandFamily.Screen, (e, a, _) => { e.Screen.PenAttr = CellAttr.None; return Args.Done(); });

        t.Add("SCROLL", 0, "Slide the text up one line.", "SCROLL",
            CommandFamily.Screen, (e, a, _) => { e.Screen.ScrollUp(); return Args.Done(); });

        t.Add("CURSOR", 0, "Report where the text cursor is, as a list [row column].", "PRINT CURSOR",
            CommandFamily.Screen, (e, a, _) =>
                Args.Result(LogoValue.FromList(new[]
                {
                    LogoValue.FromNumber(e.Screen.CursorRow + 1),
                    LogoValue.FromNumber(e.Screen.CursorCol + 1),
                })),
            isReporter: true);

        t.Add("SETCURSOR", 2, "Move the text cursor to a row and column, counting from 1.", "SETCURSOR 5 1",            CommandFamily.Screen, (e, a, _) =>
            {
                int row = (int)Math.Round(Args.Number(a, 0, "SETCURSOR", e.Mode)) - 1;
                int col = (int)Math.Round(Args.Number(a, 1, "SETCURSOR", e.Mode)) - 1;
                if (row < e.Screen.WindowTop || row > e.Screen.WindowBottom || col < 0 || col >= TextScreen.Cols)
                    throw new LogoException(LogoErrorCode.OutOfRange, ErrorText.OutOfRange("SETCURSOR", e.Mode),
                        $"The text window runs from row {e.Screen.WindowTop + 1} to {e.Screen.WindowBottom + 1}, and columns 1 to {TextScreen.Cols}.",
                        subject: "SETCURSOR");
                e.Screen.CursorRow = row;
                e.Screen.CursorCol = col;
                e.Host.Invalidate();
                return Args.Done();
            });
    }
}
