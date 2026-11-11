using MaturinLogo.Core.Machine;
using MaturinLogo.Core.Values;

namespace MaturinLogo.Core.Tests;

/// <summary>
/// The examples are the first programs a child ever runs, so they must all be valid Logo that
/// finishes on its own. These tests run every source through the engine: one that stops parsing,
/// faults, or hangs waiting for input cannot ship in the Examples page or Demo Mode.
/// </summary>
public class ExamplesTests
{
    [Fact]
    public void There_are_ten_examples() => Assert.Equal(10, LogoExamples.Count);

    [Fact]
    public void Every_example_has_a_unique_index_in_order()
    {
        for (int i = 0; i < LogoExamples.All.Length; i++)
            Assert.Equal(i, LogoExamples.All[i].Index);
    }

    [Fact]
    public void ByIndex_returns_the_right_example_and_null_out_of_range()
    {
        Assert.Same(LogoExamples.All[3], LogoExamples.ByIndex(3));
        Assert.Null(LogoExamples.ByIndex(-1));
        Assert.Null(LogoExamples.ByIndex(LogoExamples.Count));
    }

    [Fact]
    public void Every_example_is_a_single_line() =>
        // A demo types one line at a time; a newline would split it into two commands.
        Assert.All(LogoExamples.All, ex => Assert.DoesNotContain('\n', ex.Source));

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    public async Task Every_example_runs_to_completion_without_an_error(int index)
    {
        LogoExamples.Example ex = LogoExamples.All[index];

        // A throw here is the failure: a parse error, a bad primitive, or a fault in an example.
        var engine = Logo.New();
        await engine.RunAsync(ex.Source);
    }

    [Fact]
    public async Task The_function_example_leaves_a_callable_procedure()
    {
        // A FUNCTION defines TRI: and calls it; the procedure must survive for a child to reuse.
        var engine = Logo.New();
        await engine.RunAsync(LogoExamples.All[2].Source);

        Assert.True(engine.Workspace.HasProcedure("TRI"), "TRI should be defined after running the example.");
    }

    [Fact]
    public async Task The_variable_example_stores_a_value()
    {
        var engine = Logo.New();
        await engine.RunAsync(LogoExamples.All[7].Source);

        Assert.True(engine.Workspace.TryGetGlobal("SIDE", out LogoValue? side), "SIDE should be set.");
        Assert.Equal(30, side!.AsNumberOrDefault(), 6);
    }
}
