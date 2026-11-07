using System.Text;
using MaturinLogo.Core.Interpreter;

namespace MaturinLogo.Core.Primitives;

/// <summary>
/// The quick reference, built from the very table that runs the language, so the help text can
/// never name a command that is not implemented. Everything is plain uppercase text in 40 columns
/// because it is shown on the emulated screen and paged with the keyboard.
/// </summary>
public static class QuickReference
{
    /// <summary>Width of the emulated screen; every line is padded or wrapped to this.</summary>
    public const int Width = Screen.TextScreen.Cols;

    /// <summary>Order the families appear in, matching how a child meets them.</summary>
    private static readonly (CommandFamily Family, string Title)[] FamilyOrder =
    {
        (CommandFamily.Turtle, "TURTLE"),
        (CommandFamily.Math, "MATH"),
        (CommandFamily.Words, "WORDS"),
        (CommandFamily.Lists, "LISTS"),
        (CommandFamily.Logic, "TESTS"),
        (CommandFamily.Data, "VARIABLES"),
        (CommandFamily.Control, "REPEATING"),
        (CommandFamily.Output, "PRINTING"),
        (CommandFamily.Screen, "SCREEN"),
        (CommandFamily.System, "LOGO ITSELF"),
    };

    /// <summary>One line per screen row, ready to page through with the arrow keys.</summary>
    public static List<string> ReferenceLines(PrimitiveTable table)
    {
        var lines = new List<string>();

        lines.Add(Center($"{LogoInfo.Product} QUICK REFERENCE"));
        lines.Add(Center($"VERSION {LogoInfo.Version} - {table.All.Count} COMMANDS"));
        lines.Add(new string('-', Width));
        lines.Add("TYPE THE NAME OF A COMMAND TO USE IT.");
        lines.Add("TYPE HELP NAME FOR A LONGER EXPLANATION.");
        lines.Add("ARROWS SCROLL - ESC CLOSES.");
        lines.Add("");

        foreach (var (family, title) in FamilyOrder)
        {
            var primitives = table.InFamily(family).Where(p => !p.Hidden).ToList();
            if (primitives.Count == 0) continue;

            lines.Add("");
            lines.Add(title + " " + new string('=', Math.Max(0, Width - title.Length - 1)));

            // A name index first: a child scanning for FD finds it faster than in prose.
            foreach (var line in Wrap(string.Join("  ", primitives.Select(p => p.Name)), Width, "  "))
                lines.Add(line);

            foreach (var primitive in primitives)
            {
                lines.Add("  " + Trim(primitive.Name, 12) + Shorten(primitive.Doc, Width - 14));
                if (primitive.Example.Length > 0)
                    lines.Add("  " + new string(' ', 12) + Shorten("e.g. " + primitive.Example, Width - 14));
            }
        }

        var keywords = table.Keywords;
        if (keywords.Count > 0)
        {
            lines.Add("");
            lines.Add("MAKING YOUR OWN COMMANDS " + new string('=', Math.Max(0, Width - 25)));
            foreach (var keyword in keywords)
            {
                lines.Add("  " + Trim(keyword.Name, 12) + Shorten(keyword.Doc, Width - 14));
                if (keyword.Example.Length > 0)
                    lines.Add("  " + new string(' ', 12) + Shorten("e.g. " + keyword.Example, Width - 14));
            }
        }

        lines.Add("");
        lines.Add(new string('-', Width));
        // Every key line is written to fit 40 columns exactly; the reference is paged on the
        // emulated screen, so a line wider than the grid would look broken.
        lines.Add("KEYS:");
        lines.Add("  ALT-? HELP    ALT-M MODE");
        lines.Add("  ALT-S SAVE    ALT-L SESSIONS");
        lines.Add("  ALT-D SAVE TO FILE");
        lines.Add("  ALT-E EDITOR  ALT-I INFO");
        lines.Add("  ALT-G GREEN/AMBER  ALT-C CRT");
        lines.Add("  ALT-T TEXT    ALT-O OPEN FILE");
        lines.Add("  ESC CLOSES OR STOPS");
        lines.Add("");
        lines.Add(Center(LogoInfo.HomageLines[0]));
        lines.Add(Center(LogoInfo.HomageLines[1]));

        return lines;
    }

    /// <summary>The text HELP writes for one topic: a command, a family, or ALL.</summary>
    public static string Explain(string topic, PrimitiveTable table, LogoMode mode)
    {
        var sb = new StringBuilder();

        if (string.Equals(topic, "ALL", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("FAMILIES: ");
            sb.Append(string.Join(", ", FamilyOrder.Select(f => f.Title)));
            sb.Append(". TRY HELP REPEAT");
            return sb.ToString();
        }

        Primitive? primitive = table.All.FirstOrDefault(p =>
            string.Equals(p.Name, topic, StringComparison.OrdinalIgnoreCase) ||
            p.Aliases.Any(a => string.Equals(a, topic, StringComparison.OrdinalIgnoreCase)));

        if (primitive is not null)
        {
            sb.Append(primitive.Name.ToUpperInvariant());
            if (primitive.Aliases.Count > 0)
                sb.Append(" (").Append(string.Join(", ", primitive.Aliases)).Append(')');
            sb.Append(" - ").Append(primitive.Doc);
            if (primitive.Example.Length > 0)
                sb.Append(". TRY: ").Append(primitive.Example);
            return sb.ToString();
        }

        foreach (var (family, title) in FamilyOrder)
        {
            if (!string.Equals(title, topic, StringComparison.OrdinalIgnoreCase)) continue;
            var names = table.InFamily(family).Select(p => p.Name);
            sb.Append(title).Append(": ").Append(string.Join(", ", names));
            return sb.ToString();
        }

        // Not a command or a family: suggest the closest thing that is.
        string? near = ErrorText.Nearest(topic, table.All.SelectMany(p => new[] { p.Name }.Concat(p.Aliases)));
        if (near is not null)
        {
            sb.Append("I don't know HELP ").Append(topic.ToUpperInvariant());
            sb.Append(". Did you mean ").Append(near).Append("?");
            return sb.ToString();
        }

        sb.Append("I don't know HELP ").Append(topic.ToUpperInvariant());
        sb.Append(". Type ALT-? to see everything Logo can do.");
        return sb.ToString();
    }

    /// <summary>The lines WHO prints: everything the child has defined.</summary>
    public static List<string> Who(Workspace workspace)
    {
        var lines = new List<string>();
        var variables = workspace.GlobalNames();
        var procedures = workspace.ProcedureNames();

        if (variables.Count == 0) lines.Add("NO VARIABLES YET");
        else lines.AddRange(Wrap("VARIABLES: " + string.Join(" ", variables), Width, ", "));

        if (procedures.Count == 0) lines.Add("NO PROCEDURES YET");
        else lines.AddRange(Wrap("PROCEDURES: " + string.Join(" ", procedures), Width, ", "));
        return lines;
    }

    private static string Center(string text) => text.Length >= Width
        ? text.Substring(0, Width)
        : new string(' ', (Width - text.Length) / 2) + text;

    private static string Trim(string text, int width) =>
        text.Length >= width ? text.Substring(0, width) : text + new string(' ', width - text.Length);

    private static string Shorten(string text, int width) =>
        text.Length <= width ? text : text.Substring(0, Math.Max(0, width - 1)) + "\u2026";

    /// <summary>Break a long run of words onto several lines no wider than the screen.</summary>
    private static List<string> Wrap(string text, int width, string separator)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        foreach (var word in text.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            string piece = word + separator;
            if (current.Length + piece.Length > width && current.Length > 0)
            {
                result.Add(current.ToString().TrimEnd());
                current.Clear();
            }
            current.Append(piece);
        }
        if (current.Length > 0) result.Add(current.ToString().TrimEnd());
        return result;
    }
}
