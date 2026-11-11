namespace MaturinLogo.Core.Machine;

/// <summary>
/// The pre-loaded programs the Examples page (ALT-X) offers and Demo Mode types out one at a time.
/// Each is a single line of Logo so it can be typed into the listener character by character, and
/// each runs to completion on its own - no READ prompt, no endless loop - because a demo that hangs
/// waiting for input is a child who has stopped watching.
/// </summary>
/// <remarks>
/// Kept in code rather than a file for the same reason as <see cref="LogoWhatsNew"/>: it is part of
/// the build, and <c>ExamplesTests</c> runs every source through the engine so an example that stops
/// parsing or faulting cannot ship. The order here is the teaching order - drawing first, then sound,
/// a function, animation, loops, logic, parameters, variables and a FOR loop - which is also the order
/// Demo Mode walks through them.
/// </remarks>
public static class LogoExamples
{
    /// <summary>One example: its position on the page (0-9), its title, and the line to run.</summary>
    public sealed record Example(int Index, string Title, string Source);

    /// <summary>The whole set, in teaching order. The index is the key a child presses to pick it.</summary>
    public static readonly Example[] All =
    [
        new(0, "SQUARE",
            "CS REPEAT 4 [FD 50 RT 90]"),

        new(1, "MARY HAD A LITTLE LAMB",
            "TOOT \"E4 0.25 TOOT \"D4 0.25 TOOT \"C4 0.25 TOOT \"D4 0.25 TOOT \"E4 0.25 TOOT \"E4 0.25 TOOT \"E4 0.5"),

        new(2, "A FUNCTION",
            "TO TRI REPEAT 3 [FD 60 RT 120] END CS TRI"),

        new(3, "SPINNING STAR",
            "SETSPEED 75 CS REPEAT 5 [FD 80 RT 144]"),

        new(4, "NESTED LOOPS",
            "CS REPEAT 6 [PU FD 20 PD REPEAT 4 [FD 30 RT 90]]"),

        new(5, "LOGIC (IF)",
            "PRINT \"HELLO IF EQUAL (2 + 2) 4 [PRINT \"YES] [PRINT \"NO]"),

        new(6, "A FLOWER (PARAMETERS)",
            "TO FLOWER :S :C SETPC :C REPEAT 8 [FD :S RT 120 FD :S RT 120 RT 165] END CS HOME FLOWER 30 2"),

        new(7, "VARIABLES (MAKE)",
            "MAKE \"SIDE 30 CS REPEAT 4 [FD :SIDE RT 90]"),

        new(8, "RAINBOW (FOR)",
            "CS FOR [I 0 15 1] [SETPC :I FD 40 RT 90]"),

        new(9, "DANCING TURTLE",
            "SETSPEED 60 CLEAN REPEAT 20 [FD 30 RT 90 BK 30 LT 90]"),
    ];

    /// <summary>How many examples there are - the page and the demo both count on this.</summary>
    public static int Count => All.Length;

    /// <summary>The example at a key position, or null when the number is out of range.</summary>
    public static Example? ByIndex(int index) =>
        index >= 0 && index < All.Length ? All[index] : null;
}
