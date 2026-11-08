using MaturinLogo.Core.Primitives;
using System.Text;

namespace MaturinLogo.Tools.DocGen;

/// <summary>
/// Regenerates the command-reference tables in <c>Docs/Architecture.md</c> straight from
/// <see cref="PrimitiveTable.CreateDefault"/> — the table that runs the language — so the doc can
/// never claim a command that does not exist, omit one that does, or get its family counts wrong.
///
/// <code>
/// dotnet run --project Tools/DocGen            rewrite the tables in place
/// dotnet run --project Tools/DocGen -- --check report drift, change nothing, exit 1 if stale
/// </code>
///
/// Only the block between the two HTML sentinels is generated; the prose around it stays
/// hand-written. <c>CommandReferenceTests</c> asserts the same facts in CI, so forgetting to re-run
/// this after adding a primitive fails the build rather than shipping a stale table.
/// </summary>
public static class Program
{
    private const string StartSentinel = "<!-- generated:command-tables:start -->";
    private const string EndSentinel = "<!-- generated:command-tables:end -->";

    /// <summary>Family titles exactly as they appear as "### Title — N commands" in the doc.</summary>
    private static readonly (CommandFamily Family, string Title)[] Families =
    [
        (CommandFamily.Turtle, "Turtle"),
        (CommandFamily.Math, "Math"),
        (CommandFamily.Words, "Words"),
        (CommandFamily.Lists, "Lists"),
        (CommandFamily.Logic, "Tests"),
        (CommandFamily.Data, "Variables"),
        (CommandFamily.Control, "Repeating"),
        (CommandFamily.Output, "Printing"),
        (CommandFamily.Screen, "Screen"),
        (CommandFamily.System, "Logo itself"),
    ];

    public static int Main(string[] args)
    {
        bool check = args.Contains("--check");
        string? docPath = FindRepositoryDoc();
        if (docPath is null)
        {
            Console.Error.WriteLine("Could not find Docs/Architecture.md above " + Environment.CurrentDirectory);
            return 2;
        }

        var table = PrimitiveTable.CreateDefault();
        string generated = Render(table);
        string text = File.ReadAllText(docPath);

        int start = text.IndexOf(StartSentinel, StringComparison.Ordinal);
        int end = text.IndexOf(EndSentinel, StringComparison.Ordinal);
        if (start < 0 || end < start)
        {
            Console.Error.WriteLine(
                $"{docPath} is missing the generated-block sentinels.\n" +
                "Wrap the '### <Family>' tables in section 14 with:\n" +
                $"  {StartSentinel}\n  {EndSentinel}");
            return 2;
        }

        int cutFrom = start + StartSentinel.Length;
        string current = text.Substring(cutFrom, end - cutFrom);

        if (Normalize(current) == Normalize(generated))
        {
            Console.WriteLine($"{docPath} is up to date " +
                $"({table.All.Count} commands, {table.Keywords.Count} syntactic words).");
            return 0;
        }

        if (check)
        {
            Console.Error.WriteLine($"{docPath} is STALE: its command tables no longer match PrimitiveTable.");
            Console.Error.WriteLine("Run: dotnet run --project Tools/DocGen");
            return 1;
        }

        File.WriteAllText(docPath,
            text.Substring(0, cutFrom) + Environment.NewLine + generated + text.Substring(end));
        Console.WriteLine($"Rewrote the command tables in {docPath}.");
        Console.WriteLine($"  {LogoInfo.Product} {LogoInfo.Version}: " +
            $"{table.All.Count(p => !p.Hidden)} commands, {table.Keywords.Count} syntactic words.");
        Console.WriteLine("Check the totals stated in the section 14 prose still match, then run the tests.");
        return 0;
    }

    /// <summary>Builds the generated block: one table per family, then the syntactic words.</summary>
    private static string Render(PrimitiveTable table)
    {
        var sb = new StringBuilder();
        sb.AppendLine();

        foreach (var (family, title) in Families)
        {
            var visible = table.InFamily(family).Where(p => !p.Hidden).OrderBy(p => p.Name).ToList();
            var hidden = table.InFamily(family).Where(p => p.Hidden).Select(p => p.Name).OrderBy(n => n).ToList();
            if (visible.Count == 0) continue;

            sb.AppendLine($"### {title} — {visible.Count} commands");
            sb.AppendLine();
            sb.AppendLine("| Command | Inputs | Kind | Does what | Try |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var p in visible)
            {
                string name = "`" + Esc(p.Name) + "`";
                if (p.Aliases.Count > 0)
                    name += "<br/>*or " + string.Join(", ", p.Aliases.Select(Esc)) + "*";
                sb.AppendLine($"| {name} | {Arity(p)} | {Kind(p)} | {Esc(p.Doc)} | {Code(Esc(p.Example))} |");
            }
            sb.AppendLine();
            if (hidden.Count > 0)
            {
                sb.AppendLine($"Callable but left out of the on-screen reference: {string.Join(", ", hidden.Select(Esc))}.");
                sb.AppendLine();
            }
        }

        sb.AppendLine($"**Syntactic words — {table.Keywords.Count}.** The engine intercepts these before");
        sb.AppendLine("primitive dispatch, so they are not callable procedures.");
        sb.AppendLine();
        sb.AppendLine("| Word | Does what | Try |");
        sb.AppendLine("|---|---|---|");
        foreach (var k in table.Keywords)
            sb.AppendLine($"| `{Esc(k.Name)}` | {Esc(k.Doc)} | {Code(Esc(k.Example))} |");

        sb.AppendLine();
        return sb.ToString();
    }

    private static string FindRepositoryDoc()
    {
        // Walk up from wherever the tool was launched so it works from the repo root, Src/ or Tools/.
        for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "Docs", "Architecture.md");
            if (File.Exists(candidate)) return candidate;
        }

        return null!;
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim();

    private static string Esc(string s) =>
        s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ").Trim();

    private static string Code(string s) => s.Length == 0 ? "" : "`" + s + "`";

    private static string Arity(Primitive p) =>
        p.Variadic ? "many" : p.DeclaredInputs < 0 ? "-" : p.DeclaredInputs.ToString();

    private static string Kind(Primitive p) =>
        p.Syntactic ? "syntax" : p.IsReporter ? "reporter" : "command";
}
