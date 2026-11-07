using MaturinLogo.Core.Interpreter;
using MaturinLogo.Core.Primitives;
using MaturinLogo.Core.Values;

namespace MaturinLogo.Core.Tests;

/// <summary>Shared harness: a fresh engine with a headless host and a screen to read back.</summary>
public static class Logo
{
    public static Engine New() => new();

    public static async Task<string> RunAsync(string source)
    {
        var engine = New();
        await engine.RunAsync(source);
        return ScreenText(engine);
    }

    /// <summary>Everything PRINT/TYPE put on the screen, trimmed the way a child would read it.</summary>
    public static string ScreenText(Engine engine) =>
        string.Join("\n", engine.Screen.Snapshot().Select(l => l.TrimEnd())).TrimStart('\n').Trim();

    public static async Task<LogoValue> EvalAsync(string source)
    {
        var engine = New();
        return await engine.EvalTextAsync(source);
    }

    /// <summary>Evaluate and compare against the printed form, which is how Logo results are checked.</summary>
    public static async Task<string> ShowAsync(string source)
    {
        var value = await EvalAsync(source);
        return Printer.Format(value, readable: true);
    }

    /// <summary>Run source expecting a Logo error with this code; returns it for further checks.</summary>
    public static async Task<LogoException> ExpectErrorAsync(string source, LogoErrorCode expected)
    {
        var engine = New();
        try
        {
            await engine.RunAsync(source);
        }
        catch (LogoException thrown)
        {
            Assert.Equal(expected, thrown.Code);
            return thrown;
        }
        Assert.Fail($"Expected {expected} but no error was thrown");
        throw new InvalidOperationException();
    }
}

public class ReaderTests
{
    [Theory]
    [InlineData("FD 10", 2)]
    [InlineData("REPEAT 4 [FD 50 RT 90]", 3)]
    [InlineData("[1 2 [3 4]]", 1)]
    [InlineData("PRINT \"HELLO", 2)]
    public void Reads_expected_number_of_top_level_items(string source, int expected)
    {
        var items = MaturinLogo.Core.Reader.LogoReader.ReadProgram(source);
        Assert.Equal(expected, items.Count);
    }

    [Fact]
    public void Paren_group_is_kept_distinct_from_a_list()
    {
        var items = MaturinLogo.Core.Reader.LogoReader.ReadProgram("(PRINT 1)");
        Assert.Single(items);
        Assert.Equal(MaturinLogo.Core.Reader.ItemKind.Paren, items[0].Kind);
    }

    [Theory]
    [InlineData("#HFF", 255)]
    [InlineData("#B101", 5)]
    [InlineData("#O17", 15)]
    [InlineData("-3.5", -3.5)]
    public void Numbers_include_the_radix_forms(string source, double expected)
    {
        var items = MaturinLogo.Core.Reader.LogoReader.ReadProgram(source);
        Assert.Equal(expected, items[0].Number, 6);
    }

    [Fact]
    public void Case_preserving_bars_survive_folding()
    {
        var items = MaturinLogo.Core.Reader.LogoReader.ReadProgram("\"|Hello World|");
        Assert.Equal("Hello World", items[0].Text);
    }

    [Fact]
    public void Comments_run_to_the_end_of_the_line()
    {
        var items = MaturinLogo.Core.Reader.LogoReader.ReadProgram("FD 10 ; go forward\nRT 90");
        Assert.Equal(4, items.Count);
    }

    [Theory]
    [InlineData("[1 2")]
    [InlineData("(1 2")]
    [InlineData("]")]
    public void Unbalanced_brackets_are_reported(string source)
    {
        Assert.Throws<MaturinLogo.Core.Reader.ReadException>(() => MaturinLogo.Core.Reader.LogoReader.ReadProgram(source));
    }
}

public class PrinterTests
{
    [Theory]
    [InlineData(4.0, "4")]
    [InlineData(2.5, "2.5")]
    [InlineData(-0.0, "0")]
    public void Numbers_print_without_a_spurious_decimal(double value, string expected) =>
        Assert.Equal(expected, Printer.FormatNumber(value));

    [Fact]
    public void A_word_with_a_space_is_bracketed_when_readable()
    {
        var word = LogoValue.FromWord("HELLO THERE");
        Assert.Equal("[HELLO THERE]", Printer.Format(word, readable: true));
        Assert.Equal("HELLO THERE", Printer.Format(word, readable: false));
    }

    [Fact]
    public void Lists_print_nested_with_their_original_brackets()
    {
        var inner = LogoValue.FromList(new[] { LogoValue.FromNumber(1), LogoValue.FromNumber(2) });
        var outer = LogoValue.FromList(new[] { inner, LogoValue.FromWord("X") });
        Assert.Equal("[[1 2] X]", Printer.Format(outer, readable: true));
    }

    [Fact]
    public void Paren_groups_print_with_parentheses()
    {
        var group = LogoValue.FromParenGroup(new[] { LogoValue.FromWord("FD"), LogoValue.FromNumber(10) });
        Assert.Equal("(FD 10)", Printer.Format(group, readable: true));
    }
}
