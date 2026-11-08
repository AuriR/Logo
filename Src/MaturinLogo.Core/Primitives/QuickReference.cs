using System.Text;
using MaturinLogo.Core.Interpreter;
using MaturinLogo.Core.Screen;

namespace MaturinLogo.Core.Primitives;

/// <summary>
/// The quick reference, built from the very table that runs the language, so the help text can
/// never name a command that is not implemented. Everything is plain uppercase text in 40 columns
/// because it is shown on the emulated screen and paged with the keyboard.
/// </summary>
/// <remarks>
/// Two rules the screen enforces and an earlier version broke: commands are listed alphabetically
/// inside each family (a child scanning for <c>FD</c> should not have to read the whole section),
/// and nothing is ever cut off. A description longer than the column it lives in is wrapped onto
/// indented continuation rows rather than truncated with an ellipsis, so every row that reaches
/// the grid is at most <see cref="Width"/> columns and no word is lost.
/// </remarks>
public static class QuickReference
{
    /// <summary>Width of the emulated screen; every line is padded or wrapped to this.</summary>
    public const int Width = TextScreen.Cols;

    /// <summary>
    /// Columns a command name owns before its description starts. Public because a test asserts
    /// every name fits it - a command named longer than this would be cut off on the page. The
    /// longest visible name is nine characters, so there is room to spare.
    /// </summary>
    public const int NameColumn = 10;

    /// <summary>Where a wrapped description hangs: the 2-space margin, the name column and the gap
    /// after it, so a name exactly as wide as its column still cannot run into its own doc.</summary>
    private const int Lead = NameColumn + 3;

    /// <summary>Order the families appear in, matching how a child meets them.</summary>
    private static readonly (CommandFamily Family, string Title)[] FamilyOrder =
    [
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
    ];

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
            var primitives = table.InFamily(family)
                .Where(p => !p.Hidden)
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToList();
            if (primitives.Count == 0) continue;

            lines.Add("");
            lines.Add(Header(title));

            // A name index first: a child scanning for FD finds it faster than in prose.
            foreach (string row in TextWrap.Wrap(string.Join("  ", primitives.Select(p => p.Name)), Width, "  "))
                lines.Add(row);

            lines.Add("");
            foreach (var primitive in primitives)
                Entry(lines, primitive.Name, primitive.Doc, primitive.Example);
        }

        var keywords = table.Keywords;
        if (keywords.Count > 0)
        {
            lines.Add("");
            lines.Add(Header("MAKING YOUR OWN COMMANDS"));
            foreach (var keyword in keywords.OrderBy(k => k.Name, StringComparer.Ordinal))
                Entry(lines, keyword.Name, keyword.Doc, keyword.Example);
        }

        lines.Add("");
        lines.Add(new string('-', Width));
        lines.Add("KEYS:");
        foreach (string row in KeyLines()) lines.Add("  " + row);
        lines.Add("");
        lines.Add(Center(LogoInfo.HomageLines[0]));
        lines.Add(Center(LogoInfo.HomageLines[1]));

        return lines;
    }

    /// <summary>
    /// The Alt keys, two per row, for the quick reference and the INFO dashboard to share. Each
    /// half is padded to a fixed width so the columns line up whatever the key text says, and two
    /// halves plus the margin cannot exceed the screen.
    /// </summary>
    public static IEnumerable<string> KeyLines()
    {
        string[] keys =
        [
            "ALT-? HELP",
            "ALT-, SETTINGS",
            "ALT-M MODE",
            "ALT-G COLOR",
            "ALT-C CRT",
            "ALT-I INFO",
            "ALT-V COLOURS",
            "ALT-W SHAPES",
            "ALT-L FILES",
            "ALT-S SAVE",
            "ALT-D DOWNLOAD",
            "ALT-O OPEN FILE",
            "ALT-E EDITOR",
            "ALT-T TEXT/GRAPH",
            "ALT-B BREAK",
            "ESC CLOSES OR STOPS",
        ];

        const int column = 17;
        for (int i = 0; i < keys.Length; i += 2)
        {
            string left = TextWrap.Column(keys[i], column);
            string right = i + 1 < keys.Length ? keys[i + 1] : "";
            yield return (left + right).TrimEnd();
        }
    }

    /// <summary>A section title with its rule filling the rest of the row.</summary>
    private static string Header(string title) =>
        title + " " + new string('=', Math.Max(0, Width - title.Length - 1));

    /// <summary>
    /// Emit one entry: the name in its own column and the description wrapped into the space beside
    /// it, with the overflow hung under the first row rather than hard against the left edge.
    /// </summary>
    private static void Entry(List<string> lines, string name, string doc, string example)
    {
        Describe(lines, "  " + TextWrap.Column(name, NameColumn + 1), doc);
        if (example.Length > 0)
            Describe(lines, new string(' ', Lead), "e.g. " + example);
    }

    /// <summary>
    /// Emit one entry: the leading text in its own column and the description wrapped into the
    /// space beside it, with the overflow hung under the first row rather than hard against the
    /// left edge.
    /// </summary>
    private static void Describe(List<string> lines, string lead, string text)
    {
        string continuation = new string(' ', Lead);
        foreach (string row in TextWrap.WrapIndented(text, Width, lead, continuation))
            lines.Add(row.TrimEnd());
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
            var names = table.InFamily(family).Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal);
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
        else lines.AddRange(TextWrap.Wrap("VARIABLES: " + string.Join(" ", variables), Width, " "));

        if (procedures.Count == 0) lines.Add("NO PROCEDURES YET");
        else lines.AddRange(TextWrap.Wrap("PROCEDURES: " + string.Join(" ", procedures), Width, " "));
        return lines;
    }

    private static string Center(string text) => TextWrap.Center(text, Width);
}
