using System.Text.RegularExpressions;
using MaturinLogo.Core.Primitives;

namespace MaturinLogo.Core.Tests;

/// <summary>
/// The architecture doc's command reference is written out of <see cref="PrimitiveTable"/>, which
/// means it can go stale the moment someone adds, renames or removes a primitive. These tests make
/// that a build failure instead of a documentation bug nobody notices: the doc must list exactly the
/// commands that exist, with the counts it claims, for the version it claims.
/// </summary>
public class CommandReferenceTests
{
    private static readonly PrimitiveTable Table = PrimitiveTable.CreateDefault();

    /// <summary>Family titles as they appear as "### Title — N commands" headings in the doc.</summary>
    private static readonly (CommandFamily Family, string Title)[] FamilyTitles =
    {
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
    };

    /// <summary>
    /// The reference region of the doc, between its own heading and the next section. Scoped so a
    /// backticked cell in the keyboard map or the glossary is never mistaken for a command row.
    /// </summary>
    /// <remarks>
    /// The sections are found by title rather than by number, because inserting a section in the
    /// middle of this document used to mean editing the test as well - and a doc test that has to be
    /// updated to match the document it is checking is a doc test that quietly passes while the
    /// region it reads is somewhere else entirely.
    /// </remarks>
    private static string ReferenceRegion()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Architecture.md");
        Assert.True(File.Exists(path),
            $"{path} is missing. The test project links ../../Docs/Architecture.md; if it was moved " +
            "or renamed, fix the Content Include in MaturinLogo.Tests.csproj.");

        string text = File.ReadAllText(path);
        return Region(text, "Command reference", "Testing");
    }

    /// <summary>The numbered section named <paramref name="title"/>, up to the section named <paramref name="next"/>.</summary>
    private static string Region(string text, string title, string next)
    {
        Match start = Regex.Match(text, @"(?m)^## \d+\. " + Regex.Escape(title));
        Assert.True(start.Success, $"the doc has no '{title}' section");

        Match after = Regex.Match(text.Substring(start.Index + start.Length), @"(?m)^## \d+\. " + Regex.Escape(next));
        Assert.True(after.Success, $"the '{title}' section is not terminated before '{next}'");

        return text.Substring(start.Index, start.Length + after.Index);
    }

    /// <summary>First backticked token of a table row, or null when the line is not a command row.</summary>
    private static string? FirstCellName(string line)
    {
        var match = Regex.Match(line.TrimStart(), @"^\|\s*`([^`]+)`");
        return match.Success ? match.Groups[1].Value : null;
    }

    [Fact]
    public void EveryCommandIsDocumented()
    {
        string region = ReferenceRegion();
        var missing = Table.All
            .Where(p => !p.Hidden)
            .Where(p => !region.Contains("| `" + p.Name + "`", StringComparison.Ordinal))
            .Select(p => p.Name)
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void NoDocumentedCommandIsUnknown()
    {
        // Catches the other direction: a row left behind after a rename or removal. Only the command
        // tables count — the syntactic-word rows below them (`+`, `TO`, …) are keywords, not members
        // of the primitive table, so they would all look "unknown" here.
        string region = ReferenceRegion();
        int words = region.IndexOf("**Syntactic words", StringComparison.Ordinal);
        Assert.True(words > 0, "the command reference has no 'Syntactic words' block");

        var rows = region.Substring(0, words)
            .Split('\n')
            .Select(FirstCellName)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToList();

        Assert.NotEmpty(rows);

        var unknown = rows.Where(name => !Table.Contains(name)).ToList();
        Assert.Empty(unknown);
    }

    [Fact]
    public void EveryAliasIsPrintedNextToItsCommand()
    {
        string region = ReferenceRegion();
        var missing = Table.All
            .Where(p => !p.Hidden)
            .SelectMany(p => p.Aliases.Select(a => (p.Name, Alias: a)))
            .Where(pair => !region.Contains(pair.Alias, StringComparison.Ordinal))
            .Select(pair => $"{pair.Alias} (alias of {pair.Name})")
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void DeclaredTotalsMatchTheTable()
    {
        string region = ReferenceRegion();
        int commands = Table.All.Count(p => !p.Hidden);

        // The claim sits on one line in the doc, so no normalising is needed; if it ever wraps,
        // this test is telling the truth that the sentence got hard to scan.
        Assert.Contains($"**{commands} commands and {Table.Keywords.Count} syntactic words.**", region);
    }

    [Fact]
    public void EachFamilyHeadingCountsItsOwnCommands()
    {
        string region = ReferenceRegion();
        foreach (var (family, title) in FamilyTitles)
        {
            int count = Table.InFamily(family).Count(p => !p.Hidden);
            Assert.Contains($"### {title} — {count} commands", region);
        }
    }

    [Fact]
    public void EverySyntacticWordIsDocumented()
    {
        string region = ReferenceRegion();
        var missing = Table.Keywords
            .Where(k => !region.Contains("| `" + k.Name + "`", StringComparison.Ordinal))
            .Select(k => k.Name)
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void DocVersionMatchesTheRunningVersion()
    {
        string text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Architecture.md"));

        Assert.Contains($"**Current version: {LogoInfo.Version}**", text);

        // The revision history must carry a row for the version the app reports, so a release that
        // bumps LogoInfo cannot leave the history behind.
        int history = text.IndexOf("## Revision history", StringComparison.Ordinal);
        Assert.True(history >= 0, "the doc has no '## Revision history' section");
        Assert.Contains($"| **{LogoInfo.Version}** |", text.Substring(history));
    }

    [Fact]
    public void TableOfContentsCoversEverySection()
    {
        string text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Architecture.md"));

        var headings = text
            .Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => Regex.IsMatch(l, @"^## \d+\. "))
            .Select(l => l.Substring(3))
            .ToList();

        Assert.NotEmpty(headings);
        foreach (string heading in headings)
        {
            // "## 5. The evaluator" is linked as [The evaluator](#5-the-evaluator): GitHub drops the
            // period and the slash but keeps the space they leave behind as a second dash, so
            // "Known gaps / future" becomes "known-gaps--future".
            string anchor = "#" + new string(heading
                .Where(c => c != '.' && c != '/' && c != '(' && c != ')')
                .ToArray())
                .ToLowerInvariant()
                .Replace(" ", "-");
            Assert.Contains(anchor, text);
        }
    }
}
