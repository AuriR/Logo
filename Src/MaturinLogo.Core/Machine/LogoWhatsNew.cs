using MaturinLogo.Core.Primitives;
using MaturinLogo.Core.Screen;

namespace MaturinLogo.Core.Machine;

/// <summary>
/// The release notes the splash offers as WHAT'S NEW and the settings screen repeats. Newest
/// version first, so a child who has not read since 1.0 still sees what changed most recently.
/// </summary>
/// <remarks>
/// Kept in code rather than a file because it is part of the build: a release that bumps
/// <see cref="LogoInfo"/> without adding an entry here is a release that cannot be announced, and
/// <c>MachineTests</c> asserts that every version the notes claim exists in <see cref="LogoInfo"/>
/// history. Every line is wrapped to the 40-column grid before it reaches the screen.
/// </remarks>
public static class LogoWhatsNew
{
    /// <summary>One release: its version, its date and the lines of prose under it.</summary>
    public sealed record Entry(string Version, string Date, string[] Lines);

    /// <summary>The whole history, newest first.</summary>
    public static readonly Entry[] Entries =
    [
        new("1.2", "2026-11-07",
        [
            "TOOT MAKES A NOISE. TOOT 440 plays a note, TOOT \"A4 0.5 plays a named one. SETSOUND FALSE turns it off.",
            "VIEWCOLORS SHOWS THE PALETTE. All sixteen colours with their numbers, on the graphics page. ALT-V does the same.",
            "VIEWSHAPES SHOWS THE TURTLE. All six shapes with their numbers, drawn by the turtle itself. ALT-W does the same.",
            "SOUND IN THE SETTINGS. ALT-, now has a SOUND row, a COLOR CHART row and a SHAPE CHART row.",
            "TRUE AND FALSE ARE WORDS YOU CAN TYPE. SETSOUND FALSE works, and so does the SETCASE FALSE the manual always promised.",
        ]),
        new("1.1", "2026-11-07",
        [
            "SHAPES AT LAST. SHAPE 0-5 now really changes the turtle: arrow, triangle, turtle, dot, car, ball.",
            "SIXTEEN COLORS. SETPC and SETBG use the full 0-15 range, and the color names RED, YELLOW, CYAN and friends all work.",
            "SETSPEED 25-100. Watch the turtle draw, or set 100 for no pause at all. PRINT SPEED reports where you are.",
            "THE TURTLE MOVES NOW. It turns and glides to its next spot instead of jumping, so a spin is something you see.",
            "SETTINGS. Press ALT-, for one screen with monitor, CRT, mode and speed. Type SETTINGS to do the same.",
            "WHAT'S NEW AND ABOUT. What's New is offered on the splash when you have not read it; both are under ALT-, as well.",
            "HELP IS IN ORDER. Commands are alphabetised inside each section, and nothing is cut off any more - long explanations wrap.",
            "NAMES ARE CHECKED. TO SQUARE: is refused instead of quietly defining a procedure you can never call.",
            "ACCESSIBILITY. If your system asks for reduced motion, Logo draws instantly and ignores SETSPEED.",
        ]),
        new("1.0", "2026-11-07",
        [
            "First release. The whole Apple Logo II language: 136 commands across turtle, math, words, lists, tests, variables, repeating, printing, screen and system.",
            "A 24 by 40 Apple IIe screen with green or amber phosphor, a CRT overlay, and turtle graphics on a canvas.",
            "Sessions saved in the browser, downloadable as .logo files, and everything works with no network at all.",
        ]),
    ];

    /// <summary>The version a child should be shown as new: the newest entry, which is the running version.</summary>
    public static string LatestVersion => Entries.Length > 0 ? Entries[0].Version : LogoInfo.Version;

    /// <summary>True when the stored version is not the running one, so the splash should offer the page.</summary>
    public static bool IsUnread(string? seenVersion) =>
        !string.Equals(seenVersion, LogoInfo.Version, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The whole history as screen rows: each entry is a heading, then its lines wrapped to the
    /// grid with a hanging indent, then a blank row - so paging with the arrows behaves exactly as
    /// it does for the quick reference.
    /// </summary>
    public static List<string> Lines()
    {
        var lines = new List<string>();

        lines.Add(TextWrap.Center($"{LogoInfo.Product} WHAT'S NEW"));
        lines.Add(TextWrap.Center($"VERSION {LogoInfo.Version}"));
        lines.Add(new string('-', TextScreen.Cols));
        lines.Add("ARROWS SCROLL - ESC CLOSES.");
        lines.Add("");

        foreach (Entry entry in Entries)
        {
            lines.Add($"VERSION {entry.Version}  {entry.Date}");
            lines.Add(new string('-', TextScreen.Cols));

            foreach (string paragraph in entry.Lines)
                foreach (string row in TextWrap.Wrap(paragraph, TextScreen.Cols - 2))
                    lines.Add(" " + row);

            lines.Add("");
        }

        return lines;
    }

    /// <summary>
    /// Every version the notes claim, oldest last. A release that bumps <see cref="LogoInfo"/> and
    /// adds a line to the history table without adding notes here is a release whose What's New
    /// page says nothing about it, which is what <c>WhatsNewTests</c> checks.
    /// </summary>
    public static IEnumerable<string> Versions => Entries.Select(e => e.Version);
}
