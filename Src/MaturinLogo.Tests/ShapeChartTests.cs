using System.Text.Json;
using MaturinLogo.Core.Interpreter;
using MaturinLogo.Core.Machine;
using MaturinLogo.Core.Primitives;
using MaturinLogo.Core.Screen;
using MaturinLogo.Core.Turtle;

namespace MaturinLogo.Core.Tests;

/// <summary>
/// VIEWSHAPES: the shape cards, what they send to the canvas, and the two charts' shared behaviour.
/// The design point is that the cards are drawn by the turtle's own painter, so the chart cannot show
/// a shape the turtle is not capable of wearing - which is the one thing a chart that exists to settle
/// "what does SHAPE 4 look like" must not get wrong.
/// </summary>
public class ShapeChartTests
{
    // ---- the cards themselves ---------------------------------------------------

    [Fact]
    public static void The_chart_shows_every_shape_the_turtle_can_wear()
    {
        List<DrawShapeCard> cards = ShapeChart.Cards().ToList();

        Assert.Equal(TurtleShapes.Count, cards.Count);
        Assert.Equal(Enumerable.Range(0, TurtleShapes.Count), cards.Select(c => c.Shape));
    }

    [Fact]
    public static void Every_card_is_labelled_with_the_number_a_child_types()
    {
        foreach (DrawShapeCard c in ShapeChart.Cards())
        {
            Assert.StartsWith($"{c.Shape} ", c.Text, StringComparison.Ordinal);
            Assert.Contains(TurtleShapes.Names[c.Shape], c.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public static void The_cards_are_the_shapes_the_reference_names()
    {
        // SHAPE's help, SHAPE's error, the quick reference and this chart all read
        // TurtleShapes.Names. If the chart spelled a shape differently, a child who read "ball" on the
        // card and typed SHAPE BALL would be told Logo does not know it - so the card text and the
        // name the interpreter accepts have to be the same string, not merely the same shape.
        string described = TurtleShapes.Describe();

        foreach (DrawShapeCard c in ShapeChart.Cards())
        {
            string name = TurtleShapes.Names[c.Shape];
            Assert.Contains(name, described, StringComparison.OrdinalIgnoreCase);
            Assert.Equal($"{c.Shape} {name}", c.Text);
        }
    }

    [Fact]
    public static void The_whole_chart_fits_inside_the_picture()
    {
        // 280x192 is the page the cards are laid out on. Anything past the edge is not clipped by the
        // canvas - it is dropped by the aspect-ratio fit, which is worse, because it looks like the
        // chart simply has fewer shapes in it.
        foreach (DrawShapeCard c in ShapeChart.Cards())
        {
            Assert.True(c.X >= 0 && c.Y >= 0, $"card {c.Shape} starts off the page");
            Assert.True(c.X + c.Width <= GraphicsScene.SpaceWidth, $"card {c.Shape} runs off the right");
            Assert.True(c.Y + c.Height <= GraphicsScene.SpaceHeight, $"card {c.Shape} runs off the bottom");

            // The caption is under the box, so it is the part most likely to fall off the bottom.
            Assert.True(c.TextY <= GraphicsScene.SpaceHeight, $"card {c.Shape} has its caption off the page");
        }

        Assert.True(ShapeChart.Bottom() <= GraphicsScene.SpaceHeight,
            $"the chart ends at {ShapeChart.Bottom()}, past the {GraphicsScene.SpaceHeight}-unit page");
    }

    [Fact]
    public static void The_grid_is_evenly_spaced_and_centred()
    {
        List<DrawShapeCard> c = ShapeChart.Cards().ToList();

        Assert.Equal(c[0].Y, c[1].Y);
        Assert.Equal(c[0].X + c[0].Width + ShapeChart.GapX, c[1].X, 3);
        Assert.Equal(c[0].Y + c[0].Height + ShapeChart.Caption + ShapeChart.GapY, c[3].Y, 3);

        double right = GraphicsScene.SpaceWidth - (c[2].X + c[2].Width);
        Assert.Equal(c[0].X, right, 3);
    }

    [Fact]
    public static void A_shape_is_drawn_big_enough_to_read_and_small_enough_to_fit()
    {
        // The turtle's own length is right beside the line it is drawing; inside a card it would be a
        // speck. But a shape longer than the box it sits in would be cut off by the card's own border,
        // which is the one thing a shape chart must not do to a shape.
        Assert.True(ShapeChart.ShapeLength > ShapeChart.TurtleLength,
            "the chart does not enlarge the turtle at all");

        foreach (DrawShapeCard c in ShapeChart.Cards())
        {
            Assert.True(ShapeChart.ShapeLength <= c.Height,
                $"a {ShapeChart.ShapeLength}-unit shape will not fit in a {c.Height}-unit card");
            Assert.True(ShapeChart.ShapeLength <= c.Width,
                $"a {ShapeChart.ShapeLength}-unit shape will not fit in a {c.Width}-unit card");
        }
    }

    [Fact]
    public static void The_caption_goes_under_the_box_not_inside_it()
    {
        // Inside is where the shape is. A caption centred in the box would be drawn through the
        // turtle, which is precisely how the colour chart's labels work and precisely wrong here.
        foreach (DrawShapeCard c in ShapeChart.Cards())
        {
            Assert.True(c.TextY > c.Y + c.Height, $"card {c.Shape} puts its caption inside the shape");
            Assert.Equal(c.X + c.Width / 2, c.TextX, 3);
        }
    }

    [Fact]
    public static void The_text_fallback_never_runs_off_the_grid()
    {
        foreach (string line in ShapeChart.TextLines())
            Assert.True(line.Length <= TextScreen.Cols, $"the shape list overruns: {line}");

        string all = string.Join("\n", ShapeChart.TextLines());
        for (int i = 0; i < TurtleShapes.Count; i++)
            Assert.Contains($"{i} {TurtleShapes.Names[i]}", all, StringComparison.Ordinal);
    }

    [Fact]
    public static void The_text_fallback_fits_a_four_row_window()
    {
        // The whole point of packing the list rather than giving it a heading and one shape per row:
        // EASY mode has four text rows, and a list that scrolls loses the first shapes it was there to
        // name. Two rows of shapes plus the caller's headline leaves a row to spare.
        Assert.Equal(2, ShapeChart.TextLines().Count);
        Assert.True(ShapeChart.TextLines().Count + 1 <= 4, "the fallback outgrows the EASY text window");
    }

    [Fact]
    public static async Task Without_a_canvas_the_shapes_that_scroll_are_still_the_ones_you_can_type()
    {
        // Say() writes at the cursor and the window scrolls, so what a child can read is the *tail* of
        // the answer. Packing keeps the tail the useful part: with the headline last, the visible rows
        // are the last shapes and the instruction, and nothing on screen is a number they cannot use.
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.ReplayGraphicsFunc = null;

        machine.HandleKey("A-W");
        string text = MachineHarness.ScreenText(machine);

        foreach (string row in text.Split('\n'))
        {
            // Every number visible on the grid is a shape index, so a child who copies one gets a
            // shape rather than half a sentence.
            foreach (string token in row.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(token, out int n)) continue;
                Assert.True(n is >= 0 and <= TurtleShapes.MaxIndex, $"row {row} shows a number {n} that is not a shape");
            }
        }
    }

    // ---- opening, closing and switching ------------------------------------------

    [Fact]
    public static async Task Alt_W_opens_the_shape_chart()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-W");
        Assert.Equal(AppScreen.Shapes, machine.ScreenKind);
    }

    [Fact]
    public static async Task The_shape_chart_closes_back_onto_the_listener()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-W");
        machine.HandleKey("ESC");
        Assert.Equal(AppScreen.Listener, machine.ScreenKind);
    }

    [Fact]
    public static async Task Alt_V_and_Alt_W_switch_between_the_two_charts()
    {
        // Flipping between "what colour is 9" and "what is shape 4" is one task, so the keys move
        // between the charts rather than closing and reopening one at a time.
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();

        machine.HandleKey("A-V");
        Assert.Equal(AppScreen.Colors, machine.ScreenKind);

        machine.HandleKey("A-W");
        Assert.Equal(AppScreen.Shapes, machine.ScreenKind);

        machine.HandleKey("A-V");
        Assert.Equal(AppScreen.Colors, machine.ScreenKind);

        // And the key of the chart already shown closes it, which is what makes a toggle findable.
        machine.HandleKey("A-V");
        Assert.Equal(AppScreen.Listener, machine.ScreenKind);
    }

    [Fact]
    public static async Task Switching_charts_still_returns_to_where_the_first_one_opened()
    {
        // A chart reached from settings switches in place and still hands the screen back to settings,
        // because the stash underneath was taken before the first chart opened and is left alone.
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.ShowSettings();
        GoToSetting(machine, "COLOR CHART");
        machine.HandleKey("ENTER");
        Assert.Equal(AppScreen.Colors, machine.ScreenKind);

        machine.HandleKey("A-W");
        Assert.Equal(AppScreen.Shapes, machine.ScreenKind);

        machine.HandleKey("ESC");
        Assert.Equal(AppScreen.Settings, machine.ScreenKind);
    }

    [Fact]
    public static async Task The_shape_chart_is_a_row_in_settings()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.ShowSettings();

        GoToSetting(machine, "SHAPE CHART");
        machine.HandleKey("ENTER");
        Assert.Equal(AppScreen.Shapes, machine.ScreenKind);

        machine.HandleKey("ESC");
        Assert.Equal(AppScreen.Settings, machine.ScreenKind);
    }

    // ---- what it sends to the canvas ---------------------------------------------

    [Fact]
    public static async Task The_chart_paints_a_card_per_shape_over_the_whole_grid()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        int before = browser.ReplayCalls;

        machine.HandleKey("A-W");
        await MachineHarness.SettleAsync(machine);

        Assert.True(browser.ReplayCalls > before, "opening the chart never reached the canvas");

        JsonElement[] items = JsonDocument.Parse(browser.LastScene).RootElement.EnumerateArray().ToArray();
        Assert.Equal(TurtleShapes.Count, items.Length);
        Assert.All(items, item => Assert.Equal("shape", item.GetProperty("kind").GetString()));

        // The shape index and the enlarged length both travel, or the browser would draw six turtles
        // of one size or paint them all as arrows.
        Assert.Equal(0, items[0].GetProperty("shape").GetInt32());
        Assert.Equal(TurtleShapes.MaxIndex, items[^1].GetProperty("shape").GetInt32());
        Assert.Equal(ShapeChart.ShapeLength, items[0].GetProperty("shapelength").GetDouble(), 3);

        Assert.Equal(0, browser.LastTextRows);
        Assert.Equal(0, browser.LastBackground);
    }

    [Fact]
    public static async Task The_shape_chart_is_never_part_of_the_childs_picture()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "FD 100");
        int drawn = machine.Engine.Scene.Items.Count;

        machine.HandleKey("A-W");
        await MachineHarness.SettleAsync(machine);
        Assert.Equal(drawn, machine.Engine.Scene.Items.Count);

        machine.HandleKey("ESC");
        await MachineHarness.SettleAsync(machine);

        await machine.DownloadAsync();
        string source = Assert.Single(browser.Downloads).Content;
        Assert.DoesNotContain("shape", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VIEWSHAPES", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public static async Task Closing_the_shape_chart_puts_the_childs_drawing_back()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "FD 100 SETBG 3");

        machine.HandleKey("A-W");
        await MachineHarness.SettleAsync(machine);
        Assert.Equal(0, browser.LastBackground);

        machine.HandleKey("ESC");
        await MachineHarness.SettleAsync(machine);
        Assert.Equal(3, browser.LastBackground);

        JsonElement[] items = JsonDocument.Parse(browser.LastScene).RootElement.EnumerateArray().ToArray();
        Assert.DoesNotContain(items, item => item.GetProperty("kind").GetString() == "shape");
    }

    [Fact]
    public static async Task A_program_run_behind_the_shape_chart_cannot_paint_over_it()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-W");
        await MachineHarness.SettleAsync(machine);

        int replays = browser.ReplayCalls, appends = browser.AppendCalls;
        int before = machine.Engine.Scene.Items.OfType<DrawSegment>().Count();

        await machine.RunAsync("REPEAT 4 [FD 50 RT 90]");

        Assert.Equal(replays, browser.ReplayCalls);
        Assert.Equal(appends, browser.AppendCalls);
        Assert.Equal(AppScreen.Shapes, machine.ScreenKind);
        Assert.Equal(before + 4, machine.Engine.Scene.Items.OfType<DrawSegment>().Count());

        machine.HandleKey("ESC");
        await MachineHarness.SettleAsync(machine);
        Assert.True(browser.ReplayCalls > replays, "closing the chart did not repaint the picture");
    }

    [Fact]
    public static async Task Leaving_the_shape_chart_behind_unfreezes_the_canvas()
    {
        // The same trap the colour chart had: `_viewerOpen` is now shared, so BYE from behind either
        // chart has to clear it or the canvas stays frozen forever.
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-W");
        await MachineHarness.SettleAsync(machine);

        int replays = browser.ReplayCalls;
        await machine.RunAsync("BYE");
        await MachineHarness.SettleAsync(machine);

        Assert.Equal(AppScreen.Splash, machine.ScreenKind);
        Assert.True(browser.ReplayCalls > replays, "the canvas was still frozen after BYE");
    }

    // ---- the command -------------------------------------------------------------

    [Theory]
    [InlineData("VIEWSHAPES")]
    [InlineData("VIEWSHAPE")]
    [InlineData("SHAPES")]
    public static async Task The_command_opens_after_the_line_finishes(string command)
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, $"FD 100 {command}");

        Assert.Equal(AppScreen.Shapes, machine.ScreenKind);
        Assert.IsType<DrawSegment>(Assert.Single(machine.Engine.Scene.Items));
    }

    [Fact]
    public static async Task Without_a_canvas_the_command_gives_the_list_as_text()
    {
        // A chart that is blank is the one thing it must never be, and a bare refusal would leave the
        // child with nothing. The numbers are the part they type, and those survive the grid.
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.ReplayGraphicsFunc = null;

        machine.HandleKey("A-W");
        Assert.Equal(AppScreen.Listener, machine.ScreenKind);

        string text = MachineHarness.ScreenText(machine);
        // The headline must name a key that actually opens this chart. Alt-T is TEXT/GRAPH, which a
        // viewer deliberately ignores, so pointing at it would print an instruction that does nothing.
        Assert.Contains("ALT-W", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ALT-T", text, StringComparison.Ordinal);
        Assert.Contains("SHAPE n", text, StringComparison.Ordinal);

        // Every shape named with its number, so the answer is actually usable without the picture.
        // With the headline last, the list is scrolled off the top of a four-row window - so the
        // machine is asked for the whole thing, which is what a child on the graphics page would see.
        string all = string.Join("\n", ShapeChart.TextLines());
        foreach (int i in Enumerable.Range(0, TurtleShapes.Count))
            Assert.Contains($"{i} {TurtleShapes.Names[i]}", all, StringComparison.Ordinal);
    }

    [Fact]
    public static async Task SHAPE_points_at_the_chart_it_describes()
    {
        // The child who just typed SHAPE 9 is the exact person who needs VIEWSHAPES, and an error
        // message that only says "out of range" sends them to HELP instead.
        LogoException error = await Logo.ExpectErrorAsync("SHAPE 9", LogoErrorCode.OutOfRange);
        Assert.Contains("VIEWSHAPES", error.Hint, StringComparison.Ordinal);

        Primitive shape = PrimitiveTable.CreateDefault().All.First(p => p.Name == "SHAPE");
        Assert.Contains("VIEWSHAPES", shape.Doc, StringComparison.Ordinal);
    }

    [Fact]
    public static void The_chart_is_in_the_quick_reference_and_the_key_list()
    {
        string reference = string.Join("\n", QuickReference.ReferenceLines(PrimitiveTable.CreateDefault()));
        Assert.Contains("VIEWSHAPES", reference, StringComparison.Ordinal);

        string keys = string.Join("\n", QuickReference.KeyLines());
        Assert.Contains("ALT-W SHAPES", keys, StringComparison.Ordinal);
    }

    [Fact]
    public static void Help_explains_the_shape_chart()
    {
        // Called through QuickReference rather than by running HELP: HELP takes its topic as a
        // *name*, and the listener reads a bare VIEWSHAPES as the command itself, which would open
        // the chart instead of explaining it.
        string text = QuickReference.Explain("VIEWSHAPES", PrimitiveTable.CreateDefault(), LogoMode.Easy);
        Assert.Contains("VIEWSHAPES", text, StringComparison.Ordinal);
        Assert.Contains("shape", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public static void Both_charts_are_screens_of_their_own_and_both_show_the_canvas()
    {
        // The screen kind decides whether the canvas is visible and which key handler runs, so a new
        // screen that was not added to GraphicsClass would open as a blank grid.
        Assert.True(Enum.IsDefined(AppScreen.Shapes));

        string razor = File.ReadAllText(Path.Combine(RepoRoot(), "Src", "MaturinLogo", "Components", "LogoScreen.razor"));
        Assert.Contains("AppScreen.Colors or AppScreen.Shapes", razor, StringComparison.Ordinal);
    }

    [Fact]
    public static void A_viewer_cannot_be_added_to_the_scene()
    {
        // The guard that keeps both charts out of the child's picture is the *absence* of these
        // overloads, so the absence is worth asserting: one `Add(DrawShapeCard)` and a chart would
        // start being saved into sessions and exported into .logo files.
        var addOverloads = typeof(GraphicsScene)
            .GetMethods()
            .Where(m => m.Name == nameof(GraphicsScene.Add) && m.GetParameters().Length == 1)
            .Select(m => m.GetParameters()[0].ParameterType.Name)
            .ToList();

        Assert.DoesNotContain("DrawSwatch", addOverloads, StringComparer.Ordinal);
        Assert.DoesNotContain("DrawShapeCard", addOverloads, StringComparer.Ordinal);
        Assert.Contains("DrawSegment", addOverloads, StringComparer.Ordinal);
    }

    // ---- helpers ----------------------------------------------------------------

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Docs", "Architecture.md"))) return dir.FullName;
        throw new InvalidOperationException("could not find the repository root");
    }

    private static void GoToSetting(LogoMachine machine, string label)
    {
        for (int presses = 0; presses <= 30; presses++)
        {
            for (int r = 0; r < TextScreen.Rows; r++)
            {
                for (int c = 1; c < TextScreen.Cols - 1; c++)
                {
                    if (machine.Screen.Attrs[r, c].HasFlag(CellAttr.Reverse)
                        && machine.Screen.GetRowText(r).Contains(label, StringComparison.Ordinal))
                        return;
                }
            }

            machine.HandleKey("DOWN");
        }

        throw new Xunit.Sdk.XunitException($"the settings screen has no row naming {label}");
    }
}
