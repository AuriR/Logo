using System.Text.Json;
using MaturinLogo.Core.Interpreter;
using MaturinLogo.Core.Machine;
using MaturinLogo.Core.Primitives;
using MaturinLogo.Core.Screen;
using MaturinLogo.Core.Turtle;

namespace MaturinLogo.Core.Tests;

/// <summary>
/// VIEWCOLORS: the chart's geometry, what it sends to the canvas, and the fact that it is a viewer
/// rather than a drawing. The last one is the whole design - a chart that went into the child's
/// scene would be saved into their session and exported into their .logo file, so a downloaded
/// spiral would come back with sixteen boxes painted over it.
/// </summary>
public class ColorChartTests
{
    // ---- the chart itself ------------------------------------------------------

    [Fact]
    public static void The_chart_shows_every_colour_in_the_palette()
    {
        List<DrawSwatch> swatches = ColorChart.Swatches().ToList();

        Assert.Equal(ApplePalette.Count, swatches.Count);
        Assert.Equal(Enumerable.Range(0, ApplePalette.Count), swatches.Select(s => s.Color));
    }

    [Fact]
    public static void Every_swatch_is_labelled_with_the_number_a_child_types()
    {
        // The number first, because that is what goes into SETPC; the name after it, because that is
        // what tells the child which one they meant.
        foreach (DrawSwatch s in ColorChart.Swatches())
        {
            Assert.StartsWith($"{s.Color} ", s.Text, StringComparison.Ordinal);
            Assert.Contains(ApplePalette.Names[s.Color], s.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public static void The_whole_chart_fits_inside_the_picture()
    {
        // 280x192 is the hi-res page the swatches are laid out on. Anything past its edge is not
        // clipped by the canvas - it is dropped by the aspect-ratio fit, which is worse because it
        // looks like the chart simply has fewer colours in it.
        foreach (DrawSwatch s in ColorChart.Swatches())
        {
            Assert.True(s.X >= 0 && s.Y >= 0, $"swatch {s.Color} starts off the page");
            Assert.True(s.X + s.Width <= GraphicsScene.SpaceWidth, $"swatch {s.Color} runs off the right");
            Assert.True(s.Y + s.Height <= GraphicsScene.SpaceHeight, $"swatch {s.Color} runs off the bottom");
        }

        Assert.True(ColorChart.Bottom() <= GraphicsScene.SpaceHeight,
            $"the chart ends at {ColorChart.Bottom()}, past the {GraphicsScene.SpaceHeight}-unit page");
    }

    [Fact]
    public static void The_grid_is_evenly_spaced_and_centred()
    {
        List<DrawSwatch> s = ColorChart.Swatches().ToList();

        // Four across, four down, and every row starting where the last one ended plus a gap.
        Assert.Equal(s[0].Y, s[1].Y);
        Assert.Equal(s[0].X + s[0].Width + ColorChart.GapX, s[1].X, 3);
        Assert.Equal(s[0].Y + s[0].Height + ColorChart.Caption + ColorChart.GapY, s[4].Y, 3);

        // Centred left to right, with the same margin either side of the four columns.
        double right = GraphicsScene.SpaceWidth - (s[3].X + s[3].Width);
        Assert.Equal(s[0].X, right, 3);
    }

    [Fact]
    public static void A_label_reads_against_its_own_swatch()
    {
        const int white = 2;

        // Colour 0 is black and colour 2 is white. A black label inside the black swatch would be
        // unreadable, so the caption colour is chosen from the box's own luminance.
        foreach (DrawSwatch s in ColorChart.Swatches())
        {
            Assert.True(s.TextColor != s.Color, $"swatch {s.Color} labels itself in its own colour");
            Assert.Equal(ApplePalette.IsDark(s.Color) ? white : ApplePalette.Background, s.TextColor);
        }

        Assert.Equal(white, ApplePalette.ContrastIndex(0));          // black box, white text
        Assert.Equal(ApplePalette.Background, ApplePalette.ContrastIndex(2));   // white box, black text
        Assert.Equal(ApplePalette.Background, ApplePalette.ContrastIndex(13));  // yellow is bright
    }

    [Fact]
    public static void The_text_fallback_never_runs_off_the_grid()
    {
        // For anywhere the canvas is not: every row must fit 40 columns, which is the bug this whole
        // release was supposed to stop producing.
        foreach (string line in ColorChart.TextLines())
            Assert.True(line.Length <= TextScreen.Cols, $"the colour list overruns: {line}");

        // Every colour appears exactly once, which is the point of a reference table. No heading: the
        // list is packed to the grid and the headline the caller prints frames it, so a heading here
        // would only push an entry off the top of EASY mode's four-row window.
        string all = string.Join("\n", ColorChart.TextLines());
        for (int i = 0; i < ApplePalette.Count; i++)
            Assert.Contains($"{i} {ApplePalette.Names[i]}", all, StringComparison.Ordinal);
    }

    // ---- opening and closing it ------------------------------------------------

    [Fact]
    public static async Task Alt_V_opens_the_colour_chart()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-V");
        Assert.Equal(AppScreen.Colors, machine.ScreenKind);
    }

    [Fact]
    public static async Task The_chart_closes_back_onto_whatever_it_came_from()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-V");

        machine.HandleKey("ESC");
        Assert.Equal(AppScreen.Listener, machine.ScreenKind);

        // And from settings, since that is where the row is.
        machine.ShowSettings();
        GoToSetting(machine, "COLOR CHART");
        machine.HandleKey("ENTER");
        Assert.Equal(AppScreen.Colors, machine.ScreenKind);

        machine.HandleKey("ENTER");                       // ENTER closes it too
        Assert.Equal(AppScreen.Settings, machine.ScreenKind);
    }

    [Theory]
    [InlineData("ESC")]
    [InlineData("ENTER")]
    public static async Task The_chart_closes_back_onto_the_listener(string token)
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-V");
        Assert.Equal(AppScreen.Colors, machine.ScreenKind);

        machine.HandleKey(token);
        Assert.Equal(AppScreen.Listener, machine.ScreenKind);
    }

    [Fact]
    public static async Task A_key_the_chart_does_not_answer_leaves_it_open()
    {
        // The chart owns the screen, so an ordinary keystroke must not fall through to the listener
        // and start typing a command behind the swatches.
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-V");

        machine.HandleKey("F");
        machine.HandleKey("A-T");
        Assert.Equal(AppScreen.Colors, machine.ScreenKind);
    }

    // ---- what it sends to the canvas -------------------------------------------

    [Fact]
    public static async Task The_chart_paints_sixteen_swatches_over_the_whole_grid()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        int before = browser.ReplayCalls;

        machine.HandleKey("A-V");
        await MachineHarness.SettleAsync(machine);

        Assert.True(browser.ReplayCalls > before, "opening the chart never reached the canvas");

        JsonElement[] items = JsonDocument.Parse(browser.LastScene).RootElement.EnumerateArray().ToArray();
        Assert.Equal(ApplePalette.Count, items.Length);
        Assert.All(items, item => Assert.Equal("swatch", item.GetProperty("kind").GetString()));

        // Zero text rows: the canvas covers every row while the chart is up, which is what makes all
        // sixteen swatches readable at once instead of six of them being off the bottom.
        Assert.Equal(0, browser.LastTextRows);

        // And black, whatever the child's own background - a chart of colours laid over their own
        // background would compare every swatch against it.
        Assert.Equal(0, browser.LastBackground);
    }

    [Fact]
    public static async Task The_chart_is_never_part_of_the_childs_picture()
    {
        // The reason VIEWCOLORS is a viewer. A swatch in Engine.Scene would be saved into the
        // session and exported into the .logo source, so a downloaded spiral would come back with
        // sixteen boxes painted over it.
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "FD 100");
        int drawn = machine.Engine.Scene.Items.Count;

        machine.HandleKey("A-V");
        await MachineHarness.SettleAsync(machine);
        Assert.Equal(drawn, machine.Engine.Scene.Items.Count);

        machine.HandleKey("ESC");
        await MachineHarness.SettleAsync(machine);

        await machine.DownloadAsync();
        string source = Assert.Single(browser.Downloads).Content;
        Assert.DoesNotContain("swatch", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VIEWCOLORS", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public static async Task Closing_the_chart_puts_the_childs_drawing_back()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "FD 100 SETBG 3");

        machine.HandleKey("A-V");
        await MachineHarness.SettleAsync(machine);
        Assert.Equal(0, browser.LastBackground);              // the chart owns the canvas

        machine.HandleKey("ESC");
        await MachineHarness.SettleAsync(machine);
        Assert.Equal(3, browser.LastBackground);              // and gives it back

        // The replayed scene is the drawing again, not the sixteen swatches.
        JsonElement[] items = JsonDocument.Parse(browser.LastScene).RootElement.EnumerateArray().ToArray();
        Assert.DoesNotContain(items, item => item.GetProperty("kind").GetString() == "swatch");
    }

    [Fact]
    public static async Task A_program_run_behind_the_chart_cannot_paint_over_it()
    {
        // The chart is on top of the canvas, so a program that replayed or appended while it was up
        // would draw its spiral straight through the swatches. Run through the engine rather than
        // the keyboard, because the keyboard is the other thing the chart owns.
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-V");
        await MachineHarness.SettleAsync(machine);

        int replays = browser.ReplayCalls, appends = browser.AppendCalls;
        int before = machine.Engine.Scene.Items.OfType<DrawSegment>().Count();

        await machine.RunAsync("REPEAT 4 [FD 50 RT 90]");

        Assert.Equal(replays, browser.ReplayCalls);
        Assert.Equal(appends, browser.AppendCalls);
        Assert.Equal(AppScreen.Colors, machine.ScreenKind);

        // And the drawing is not lost: the strokes were made, they are just not on screen yet.
        Assert.Equal(before + 4, machine.Engine.Scene.Items.OfType<DrawSegment>().Count());

        machine.HandleKey("ESC");
        await MachineHarness.SettleAsync(machine);
        Assert.True(browser.ReplayCalls > replays, "closing the chart did not repaint the picture");
    }

    [Fact]
    public static async Task The_chart_owns_the_keyboard_until_it_closes()
    {
        // Typing must not land in the draft behind the swatches, and the keys that dismiss the chart
        // are the ones it advertises. This is the same ownership the splash and the editor have.
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-V");

        MachineHarness.Type(machine, "FD 100");
        Assert.Equal(AppScreen.Colors, machine.ScreenKind);
        Assert.Empty(machine.Engine.Scene.Items.OfType<DrawSegment>());

        machine.HandleKey("ESC");
        Assert.Equal(AppScreen.Listener, machine.ScreenKind);

        machine.HandleKey("ENTER");       // an empty line at the listener does nothing
        Assert.Equal(AppScreen.Listener, machine.ScreenKind);
    }

    // ---- the command -----------------------------------------------------------

    [Theory]
    [InlineData("VIEWCOLORS")]
    [InlineData("VIEWCOLOR")]
    [InlineData("COLORS")]
    [InlineData("COLOURS")]
    public static async Task The_command_opens_after_the_line_finishes(string command)
    {
        // A program that draws something and then asks to see the palette gets both: the chart opens
        // when the line is done, not over the middle of the drawing.
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, $"FD 100 {command}");

        Assert.Equal(AppScreen.Colors, machine.ScreenKind);

        // The child's stroke is there and nothing else is - the chart did not add itself to the
        // picture on the way past.
        Assert.IsType<DrawSegment>(Assert.Single(machine.Engine.Scene.Items));
    }

    [Fact]
    public static async Task Without_a_canvas_the_command_gives_the_list_as_text()
    {
        // A colour chart that is blank is the one thing it must never be. Rather than opening an
        // empty screen, Logo prints the palette as text - the grid is one phosphor colour, so it
        // names each entry rather than showing it, and the numbers are the part a child types.
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.ReplayGraphicsFunc = null;

        machine.HandleKey("A-V");
        Assert.Equal(AppScreen.Listener, machine.ScreenKind);

        string text = MachineHarness.ScreenText(machine);
        // The headline must name a key that actually opens this chart. Alt-T is TEXT/GRAPH, which a
        // viewer deliberately ignores, so pointing at it would print an instruction that does nothing.
        Assert.Contains("ALT-V", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ALT-T", text, StringComparison.Ordinal);
        Assert.Contains("SETPC n", text, StringComparison.Ordinal);
    }

    [Fact]
    public static void Each_headline_names_its_own_key_and_fits_the_grid()
    {
        // Two charts, two keys, and a headline that wraps is a headline that reads as garbage on a
        // 40-column screen. Pinned here because both are constants a reader would never think to run.
        Assert.EndsWith("ALT-V SHOWS THEM", ColorChart.Headline, StringComparison.Ordinal);
        Assert.EndsWith("ALT-W SHOWS THEM", ShapeChart.Headline, StringComparison.Ordinal);
        Assert.True(ColorChart.Headline.Length <= TextScreen.Cols,
            $"colour headline is {ColorChart.Headline.Length} columns");
        Assert.True(ShapeChart.Headline.Length <= TextScreen.Cols,
            $"shape headline is {ShapeChart.Headline.Length} columns");
    }

    [Fact]
    public static void The_chart_is_in_the_quick_reference_and_the_key_list()
    {
        // A command nobody can find is a command nobody uses. The reference is many pages long, so
        // the generated lines are checked rather than the 24 rows currently on screen - and the same
        // for the key list, which the info dashboard prints in full.
        string reference = string.Join("\n", QuickReference.ReferenceLines(PrimitiveTable.CreateDefault()));
        Assert.Contains("VIEWCOLORS", reference, StringComparison.Ordinal);

        string keys = string.Join("\n", QuickReference.KeyLines());
        Assert.Contains("ALT-V COLORS", keys, StringComparison.Ordinal);
    }

    [Fact]
    public static void Help_explains_the_chart()
    {
        // Called through QuickReference rather than by running HELP: HELP takes its topic as a
        // *name*, and the listener reads a bare VIEWCOLORS as the command itself, which would open
        // the chart instead of explaining it.
        string text = QuickReference.Explain("VIEWCOLORS", PrimitiveTable.CreateDefault(), LogoMode.Easy);
        Assert.Contains("VIEWCOLORS", text, StringComparison.Ordinal);
        Assert.Contains("colour", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public static async Task Leaving_the_view_with_the_chart_open_unfreezes_the_canvas()
    {
        // `_chartOpen` stands the replay, append and turtle paths down. Emptying the overlay stack
        // without clearing it - which is what `DiscardOverlay` used to do - would leave the flag set,
        // so every drawing after that was made and never seen: the child keeps typing FD 100 at a
        // screenful of swatches.
        //
        // Driven through the engine rather than the keyboard on purpose. The chart owns the keys, so
        // typing BYE at it just closes it; this is the path a program that reaches BYE takes, and the
        // path the editor's Run uses.
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-V");
        await MachineHarness.SettleAsync(machine);

        int replays = browser.ReplayCalls;
        await machine.RunAsync("BYE");
        await MachineHarness.SettleAsync(machine);

        Assert.Equal(AppScreen.Splash, machine.ScreenKind);
        Assert.True(browser.ReplayCalls > replays, "the canvas was still frozen after BYE");

        // And Logo is drawable again afterwards, not merely un-frozen once.
        machine.HandleKey("X");
        await MachineHarness.SettleAsync(machine);
        await MachineHarness.RunAsync(machine, "FD 100");
        Assert.True(browser.ReplayCalls > replays + 1, "the canvas never came back after leaving the chart");
    }

    [Fact]
    public static async Task The_chart_does_not_answer_the_keys_that_would_leave_it_behind()
    {
        // The reason the test above had to go through the engine: Alt-L, Alt-E, Alt-T and Alt-S are
        // all swallowed while the chart is up, so no overlay can be stacked under it and no view can
        // change underneath it. That is what keeps `DiscardOverlay` a defensive path rather than an
        // everyday one.
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-V");

        foreach (string token in new[] { "A-L", "A-E", "A-T", "A-S", "A-G", "A-M", "A-B" })
        {
            machine.HandleKey(token);
            Assert.Equal(AppScreen.Colors, machine.ScreenKind);
        }

        // Alt-I, Alt-, and Alt-? are the exceptions: they leave the chart and open what they name.
        machine.HandleKey("A-I");
        Assert.Equal(AppScreen.Info, machine.ScreenKind);
    }

    [Fact]
    public static async Task A_viewer_key_at_the_chart_opens_its_page_as_well_as_leaving()
    {
        // Pressing Alt-, while looking at swatches means settings, not "go back to the listener and
        // press it again". Each of the three is checked from a fresh chart, because the second press
        // would be answering a screen that is no longer the chart.
        foreach ((string token, string expect) in new[] { ("A-COMMA", "Settings"), ("A-?", "Help"), ("A-I", "Info") })
        {
            (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
            machine.HandleKey("A-V");
            machine.HandleKey(token);

            Assert.Equal(expect, machine.ScreenKind.ToString());
        }
    }

    [Fact]
    public static async Task A_viewer_key_from_the_chart_opened_in_settings_only_goes_back()
    {
        // The child came *from* the settings list, so going back to it is the whole answer. Carrying
        // Alt-, forward as well would close settings too and drop them at the listener for a key
        // they pressed once.
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.ShowSettings();
        GoToSetting(machine, "COLOR CHART");
        machine.HandleKey("ENTER");
        Assert.Equal(AppScreen.Colors, machine.ScreenKind);

        machine.HandleKey("A-COMMA");
        Assert.Equal(AppScreen.Settings, machine.ScreenKind);
    }

    [Fact]
    public static void The_chart_is_a_screen_of_its_own()
    {
        // The screen kind decides whether the canvas is visible and which key handler runs, so a
        // new screen that was not added to GraphicsClass would open as a blank grid.
        Assert.True(Enum.IsDefined(AppScreen.Colors));

        string razor = File.ReadAllText(Path.Combine(RepoRoot(), "Src", "MaturinLogo", "Components", "LogoScreen.razor"));
        Assert.Contains("Colors", razor, StringComparison.Ordinal);
    }

    [Fact]
    public static void The_settings_screen_offers_the_chart_and_sound()
    {
        // Both rows were added for this release. A settings screen missing one is how a feature ends
        // up keyboard-only and unknown.
        string machine = File.ReadAllText(Path.Combine(RepoRoot(), "Src", "MaturinLogo.Core", "Machine", "LogoMachine.cs"));
        foreach (string row in new[] { "\"SOUND\"", "\"COLOR CHART\"" })
            Assert.Contains(row, machine, StringComparison.Ordinal);

        Assert.Contains("case \"A-V\"", machine, StringComparison.Ordinal);
    }

    // ---- helpers ----------------------------------------------------------------

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Docs", "Architecture.md"))) return dir.FullName;
        throw new InvalidOperationException("could not find the repository root");
    }

    /// <summary>Walk the settings cursor to the row naming <paramref name="label"/>, returning the presses it took.</summary>
    private static int GoToSetting(LogoMachine machine, string label)
    {
        for (int presses = 0; presses <= 24; presses++)
        {
            for (int r = 0; r < TextScreen.Rows; r++)
            {
                for (int c = 1; c < TextScreen.Cols - 1; c++)
                {
                    if (machine.Screen.Attrs[r, c].HasFlag(CellAttr.Reverse)
                        && machine.Screen.GetRowText(r).Contains(label, StringComparison.Ordinal))
                        return presses;
                }
            }

            machine.HandleKey("DOWN");
        }

        throw new Xunit.Sdk.XunitException($"the settings screen has no row naming {label}");
    }
}
