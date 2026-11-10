using MaturinLogo.Core.Interpreter;
using MaturinLogo.Core.Values;

namespace MaturinLogo.Core.Primitives;

/// <summary>
/// Commands about Logo itself: help, mode, case, the random seed, and version. HELP reads the same
/// table that runs the language, so what a child is told always matches what actually works.
/// </summary>
public static class SystemPrimitives
{
    public static void Register(PrimitiveTable t)
    {
        t.Add("HELP", 1, "Explain one Logo command, or list the command families with HELP ALL.", "HELP REPEAT",
            CommandFamily.System, (e, a, _) =>
            {
                string topic = Args.Name(a, 0, "HELP", e.Mode);
                e.Screen.Write(QuickReference.Explain(topic, e.Primitives, e.Mode));
                return Args.Done();
            });

        t.Add("MODE", 1, "Switch between EASY and ADVANCED.", "MODE ADVANCED",
            CommandFamily.System, (e, a, _) =>
            {
                string wanted = Args.Word(a, 0, "MODE", e.Mode);
                if (string.Equals(wanted, "EASY", StringComparison.OrdinalIgnoreCase))
                    e.Mode = LogoMode.Easy;
                else if (string.Equals(wanted, "ADVANCED", StringComparison.OrdinalIgnoreCase))
                    e.Mode = LogoMode.Advanced;
                else
                    throw new LogoException(LogoErrorCode.OutOfRange, "MODE only knows EASY and ADVANCED",
                        "Try MODE EASY or MODE ADVANCED.", subject: "MODE");
                e.Host.Invalidate();
                return Args.Done();
            });

        t.Add("EASY", 0, "Friendly errors, hints, and the split graphics/text screen.", "EASY",
            CommandFamily.System, (e, a, _) =>
            {
                e.Mode = LogoMode.Easy;
                e.ShowLower(4);
                e.Host.Invalidate();
                return Args.Done();
            });

        t.Add("ADVANCED", 0, "Strict Apple Logo errors and the full-screen editor keys.", "ADVANCED",
            CommandFamily.System, (e, a, _) =>
            {
                e.Mode = LogoMode.Advanced;
                e.Host.Invalidate();
                return Args.Done();
            });

        t.Add("SETCASE", 1, "Tell Logo whether to turn words into capital letters.", "SETCASE FALSE",
            CommandFamily.System, (e, a, _) =>
            {
                bool fold = Args.Bool(a, 0, "SETCASE", e.Mode);
                e.FoldCase = fold;
                e.Workspace.FoldCase = fold;
                return Args.Done();
            });

        t.Add("CASE", 0, "Report whether Logo is folding words to capitals.", "PRINT CASE",
            CommandFamily.System, (e, a, _) => Args.Result(LogoValue.FromBoolean(e.FoldCase)), isReporter: true);

        t.Add("RANDSEED", 1, "Choose the seed for RANDOM so a game can be repeated exactly.", "RANDSEED 5",
            CommandFamily.System, (e, a, _) =>
            {
                e.RandomSeed = (int)Math.Round(Args.Number(a, 0, "RANDSEED", e.Mode));
                e.ResetRandom();
                return Args.Done();
            });

        t.Add("SHOWSEED", 0, "Report the seed RANDOM is using.", "PRINT SHOWSEED",
            CommandFamily.System, (e, a, _) => Args.Number(e.RandomSeed ?? 0), isReporter: true);

        t.Add("WHO", 0, "Report the names of everything defined this session.", "WHO",
            CommandFamily.System, (e, a, _) =>
            {
                foreach (var line in QuickReference.Who(e.Workspace)) e.Screen.WriteLine(line);
                return Args.Done();
            });

        t.Add("BYE", 0, "Save the session and go back to the splash screen.", "BYE",
            CommandFamily.System, (e, a, _) =>
            {
                e.RequestBye();
                return Args.Done();
            });

        t.Add("VERSION", 0, "Report which Logo this is.", "PRINT VERSION",
            CommandFamily.System, (e, a, _) => Args.Word(LogoInfo.VersionText), isReporter: true);
    }
}

/// <summary>Names and version shown on the splash, the Alt-I dashboard and the quick reference.</summary>
public static class LogoInfo
{
    public const int Major = 1;
    public const int Minor = 3;
    public const string Product = "MATURIN LOGO";

    /// <summary>Display version, kept in one place so splash, dashboard and VERSION agree.</summary>
    public static string Version => $"{Major}.{Minor}";

    public static string VersionText => $"{Product} {Version}";

    /// <summary>
    /// The homage line requested on the splash screen. Long, so anything drawn on the 40-column
    /// screen must use HomageLines instead of wrapping this by hand.
    /// </summary>
    public const string Homage = "FOR THE APPLE II FAMILY - INSPIRED BY TERRAPIN LOGO AND APPLE LOGO II";

    /// <summary>
    /// The homage split at word boundaries so each part fits one screen row. Anything that prints
    /// it on the 40-column grid should use this, or the line gets cut off mid-word.
    /// </summary>
    public static readonly string[] HomageLines =
    [
        "FOR THE APPLE II FAMILY - INSPIRED BY",
        "TERRAPIN LOGO AND APPLE LOGO II",
    ];

    /// <summary>How many primitives are implemented, shown on the Alt-I dashboard.</summary>
    public static int CountPrimitives(PrimitiveTable table) => table.All.Count;
}
