using MaturinLogo.Core.Interpreter;
using MaturinLogo.Core.Primitives;
using MaturinLogo.Core.Screen;

namespace MaturinLogo.Core.Machine;

/// <summary>
/// The start-up screen: banner, homage line, ASCII-art turtle, and the first three things to type.
/// Everything is drawn into the real 40x24 text screen, so the splash is the same grid the listener
/// uses rather than a separate HTML page.
/// </summary>
/// <remarks>
/// The splash is deliberately where a child learns what to do, because it is the only screen with
/// all 24 rows to itself. Once Logo starts, EASY mode spends the top 20 rows on the turtle, and
/// four rows cannot hold an explanation.
/// </remarks>
public static class SplashArt
{
    /// <summary>
    /// The turtle, seen from above, which is how Logo's turtle actually lives. Produced by
    /// rasterising an ellipse-based shell, flippers, head and tail into a 40x12 cell grid; the
    /// generator is described in Docs/Architecture.md.
    /// </summary>
    public static readonly string[] Turtle =
    [
        "                    #                   ",
        "                  #####                 ",
        "        ###         #         ###       ",
        "       #####                 #####      ",
        "        ###    ###########    ###       ",
        "             ##   .   .   ##            ",
        "             #  .   .   .  #            ",
        "             #.   .   .   .#            ",
        "             ## .   .   . ##            ",
        "        ###    ###########    ###       ",
        "       #####                 #####      ",
        "        ###        ###        ###       ",
    ];

    public const int TurtleHeight = 12;

    /// <summary>Draw the splash into an empty screen.</summary>
    public static void Draw(TextScreen screen, string sessionName, bool storageAvailable) =>
        Draw(screen, sessionName, storageAvailable, offerWhatsNew: false, about: false);

    /// <summary>
    /// Draw the splash. <paramref name="offerWhatsNew"/> puts WHAT'S NEW in bright text, which is
    /// how a child is told there is a page they have not read for this version.
    /// </summary>
    public static void Draw(TextScreen screen, string sessionName, bool storageAvailable, bool offerWhatsNew) =>
        Draw(screen, sessionName, storageAvailable, offerWhatsNew, about: false);

    /// <summary>Draw the ABOUT page: the splash again, minus the invitation to press a key.</summary>
    public static void DrawAbout(TextScreen screen, string sessionName, bool storageAvailable) =>
        Draw(screen, sessionName, storageAvailable, offerWhatsNew: false, about: true);

    private static void Draw(TextScreen screen, string sessionName, bool storageAvailable, bool offerWhatsNew, bool about)
    {
        screen.Clear();

        screen.PutCentered(0, "M A T U R I N   L O G O", CellAttr.Bold);
        screen.PutCentered(1, LogoInfo.Version, CellAttr.Dim);

        int top = 3;
        for (int i = 0; i < Turtle.Length; i++)
            screen.Put(top + i, 0, Turtle[i]);

        int row = top + Turtle.Length + 1;   // 16

        // The homage is longer than one row, so LogoInfo keeps it pre-split at word boundaries
        // rather than letting it be cut off mid-word.
        for (int i = 0; i < LogoInfo.HomageLines.Length; i++)
            screen.PutCentered(row + i, LogoInfo.HomageLines[i], CellAttr.Dim);

        int tip = row + LogoInfo.HomageLines.Length;   // first free row under the homage

        if (about)
        {
            // ABOUT is the same drawing with the invitation swapped for what the program is,
            // because the request was for About to show the same text as the start screen.
            screen.PutLine(tip + 1, " A TURTLE GRAPHICS PROGRAM FOR KIDS.");
            screen.PutLine(tip + 2, " BUILT IN BLAZOR WEBASSEMBLY - NO SERVER,");
            screen.PutLine(tip + 3, " AND IT WORKS WITH NO NETWORK AT ALL.");
            screen.PutCentered(tip + 5, "PRESS ESC TO GO BACK", CellAttr.Reverse);
        }
        else
        {
            screen.PutLine(tip + 1, " TYPE  FD 100   THEN ENTER  - GO FORWARD");
            screen.PutLine(tip + 2, " TYPE  RT 90    THEN ENTER  - TURN RIGHT");
            screen.PutLine(tip + 3, " ALT-? EVERY COMMAND.  ALT-L SAVE FILES.");

            // The prompt sits on the very last row the grid has, so the What's New offer goes on
            // the free row above it - tip + 6 would be row 24, which the screen refuses to draw.
            if (offerWhatsNew)
                screen.PutCentered(tip + 4, "W  WHAT'S NEW IN " + LogoInfo.Version, CellAttr.Reverse);

            string prompt = sessionName.Length > 0
                ? $"PRESS ANY KEY FOR {sessionName}"
                : "PRESS ANY KEY TO START";
            screen.PutCentered(tip + 5, prompt, CellAttr.Reverse);
        }

        if (!storageAvailable)
            screen.PutCentered(0, "NO SAVE SPACE - WORK IS NOT KEPT", CellAttr.Reverse);
    }

    /// <summary>
    /// The transcript a session opens with. Kept short on purpose: in EASY mode only the bottom
    /// four rows are text, so anything longer would be hidden behind the turtle page anyway.
    /// </summary>
    public static void DrawGreeting(TextScreen screen, string sessionName, LogoMode mode)
    {
        screen.Clear();
        screen.PutCentered(screen.WindowTop, $"{sessionName}  {LogoInfo.Product}", CellAttr.Dim);
        screen.PutCentered(screen.WindowTop + 1, mode == LogoMode.Easy
            ? "TYPE LOGO - ENTER RUNS IT - ALT-? HELPS"
            : "ADVANCED MODE", CellAttr.Dim);
    }
}
