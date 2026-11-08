using MaturinLogo.Core.Interpreter;
using MaturinLogo.Core.Primitives;

namespace MaturinLogo.Core.Primitives;

/// <summary>
/// Builds the two voices of Logo's error messages. EASY mode speaks plain English with a hint and
/// a nearest-match suggestion; ADVANCED mode keeps Apple Logo's terse style. Suggestions come from
/// the same primitive table that drives execution, so they can never name a command that is missing.
/// </summary>
public static class ErrorText
{
    public static string NotAProcedure(string name, LogoMode mode) => mode == LogoMode.Easy
        ? $"I don't know the name {name}"
        : $"I DON'T KNOW {name}";

    public static string HintNotAProcedure(string name, PrimitiveTable table, Workspace workspace)
    {
        string? suggestion = Nearest(name, Candidates(table, workspace));
        if (suggestion is not null)
            return $"Did you mean {suggestion}? Type HELP {suggestion} to see what it does.";

        var family = FamilyFor(name, table);
        return family is not null
            ? $"{name} is close to the {family} commands. Type ALT-? to see the quick reference."
            : "Check the spelling, or type ALT-? for the quick reference.";
    }

    public static string NoValue(string name, LogoMode mode) => mode == LogoMode.Easy
        ? $":{name} has no value yet"
        : $"NO VALUE NAMED {name}";

    public static string HintNoValue(string name, PrimitiveTable table, Workspace workspace)
    {
        if (table.Contains(name))
            return $"{name} is a command, not a variable. Drop the colon to use it as a command.";
        string? suggestion = Nearest(name, workspace.GlobalNames());
        if (suggestion is not null)
            return $"Did you mean :{suggestion}?";
        return "Give it a value first, for example: MAKE \"" + name + " 5";
    }

    public static string NotEnoughInputs(string name, int wanted, int got, LogoMode mode) => mode == LogoMode.Easy
        ? $"{name} needs {wanted} input{(wanted == 1 ? "" : "s")} but only got {got}"
        : $"{name} DOESN'T ENOUGH INPUTS";

    public static string HintInputs(Primitive primitive)
    {
        if (primitive.Example.Length == 0)
            return $"Type HELP {primitive.Name} to see how {primitive.Name} is used.";
        return $"For example: {primitive.Example}";
    }

    public static string MissingEnd(string name, LogoMode mode) => mode == LogoMode.Easy
        ? $"{name} is missing its END"
        : $"TO {name} NOT FINISHED";

    public static string HintReserved(string name, LogoMode mode) => mode == LogoMode.Easy
        ? $"{name} is part of Logo itself, so it cannot be a procedure name. Try a different name."
        : $"{name} IS A LOGO WORD";

    /// <summary>
    /// Refusal for a name with punctuation or a space in it. Says what is allowed rather than only
    /// what was wrong, because a child who typed <c>TO SQUARE:</c> genuinely believes they named a
    /// procedure SQUARE.
    /// </summary>
    public static string BadName(string name, LogoMode mode) => mode == LogoMode.Easy
        ? $"{name} is not a name I can use"
        : $"BAD NAME {name}";

    public static string HintBadName(string what, LogoMode mode) => mode == LogoMode.Easy
        ? $"A {what} uses letters, numbers and underscore only - no spaces or punctuation."
        : $"A {what} IS LETTERS NUMBERS _ ONLY";

    public static string Runaway(LogoMode mode) => mode == LogoMode.Easy
        ? "That program keeps calling itself over and over"
        : "RECURSION TOO DEEP";

    public static string RunawayHint(LogoMode mode) => mode == LogoMode.Easy
        ? "A procedure that calls itself needs a STOP or an IF that eventually becomes false."
        : "Add a STOP condition.";

    public static string DivisionByZero(LogoMode mode) => mode == LogoMode.Easy
        ? "I can't divide by zero"
        : "DIVISION BY ZERO";

    public static string OutOfRange(string what, LogoMode mode) => mode == LogoMode.Easy
        ? $"{what} is out of range"
        : $"BAD {what}";

    public static string TypeMismatch(string name, string expected, LogoMode mode) => mode == LogoMode.Easy
        ? $"{name} wanted {expected}, but got something else"
        : $"{name} ISN'T {expected}";

    // ------------------------------------------------------------------ suggestions

    /// <summary>
    /// Every name a child could have meant: primitives, their aliases and the child's own
    /// procedures. Aliases are included so a child who typed FORWRD is pointed at FORWARD, which
    /// is the spelling they were reaching for.
    /// </summary>
    private static IEnumerable<string> Candidates(PrimitiveTable table, Workspace workspace)
    {
        foreach (var primitive in table.All)
        {
            yield return primitive.Name;
            foreach (var alias in primitive.Aliases) yield return alias;
        }
        foreach (var name in workspace.ProcedureNames()) yield return name;
    }

    /// <summary>
    /// The closest candidate within a distance that still looks like a typo, or null when nothing
    /// is near enough to be worth suggesting. Equal distances are broken by how close the lengths
    /// are: a child who typed BIFIRST is 2 edits from both FIRST and BUTFIRST, and means BUTFIRST.
    /// </summary>
    public static string? Nearest(string name, IEnumerable<string> candidates)
    {
        int limit = Math.Max(2, name.Length / 3 + 1);
        string best = "";
        int bestDistance = int.MaxValue;
        int bestLengthGap = int.MaxValue;

        foreach (var candidate in candidates)
        {
            int distance = Distance(name, candidate);
            if (distance > limit) continue;

            int lengthGap = Math.Abs(name.Length - candidate.Length);
            if (distance < bestDistance || (distance == bestDistance && lengthGap < bestLengthGap))
            {
                bestDistance = distance;
                bestLengthGap = lengthGap;
                best = candidate;
            }
        }

        return bestDistance > limit ? null : best;
    }

    /// <summary>Levenshtein distance, case-insensitive.</summary>
    public static int Distance(string a, string b)
    {
        a = a.ToUpperInvariant();
        b = b.ToUpperInvariant();
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        int[] previous = new int[b.Length + 1];
        int[] current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    /// <summary>Which command family a mistyped name most resembles, for a topical hint.</summary>
    public static string? FamilyFor(string name, PrimitiveTable table)
    {
        string? best = null;
        int bestScore = int.MaxValue;
        foreach (var primitive in table.All)
        {
            int score = Distance(name, primitive.Name);
            if (score < bestScore)
            {
                bestScore = score;
                best = primitive.Family.ToString().ToUpperInvariant();
            }
        }
        return bestScore <= 3 ? best : null;
    }
}
