using System.Text.Json;
using System.Text.RegularExpressions;
using MaturinLogo.Core.Interpreter;
using MaturinLogo.Core.Machine;
using MaturinLogo.Core.Primitives;
using MaturinLogo.Core.Reader;
using MaturinLogo.Core.Screen;
using MaturinLogo.Core.Turtle;

namespace MaturinLogo.Core.Tests;

/// <summary>
/// The bugs and features listed under "A few bugs I found" in <c>Docs/Additional Features.md</c>:
/// names that should never have been allowed, SHAPE doing nothing, text running off the grid, the
/// quick reference in random order, the missing speed control, the settings / about / what's-new
/// screens, the turtle's animation, and the sixteen colours.
/// </summary>
public class ReleaseOneOneTests
{
    // ---- names ------------------------------------------------------------------

    [Theory]
    [InlineData("SQUARE", true)]
    [InlineData("MY_FUNC", true)]
    [InlineData("SIDE2", true)]
    [InlineData("_PRIVATE", true)]
    [InlineData("SQUARE:", false)]
    [InlineData("MY-FUNC", false)]
    [InlineData("PLUS+", false)]
    [InlineData("A B", false)]
    [InlineData("", false)]
    public static void Names_are_letters_digits_and_underscore(string name, bool expected) =>
        Assert.Equal(expected, LogoReader.IsValidName(name));

    [Fact]
    public static async Task TO_refuses_a_name_with_punctuation()
    {
        // The reported bug: this defined a procedure literally named SQUARE:, which could then
        // never be called, because the listener reads SQUARE: as a name and a colon.
        LogoException ex = await Logo.ExpectErrorAsync("TO SQUARE: FD 50 END", LogoErrorCode.BadName);
        Assert.Contains("SQUARE:", ex.Message, StringComparison.Ordinal);
        Assert.Contains("underscore", ex.Hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public static async Task TO_still_accepts_an_ordinary_and_an_underscore_name()
    {
        Assert.Contains("42", await Logo.RunAsync("TO MY_FUNC OUTPUT 42 END PRINT MY_FUNC"),
            StringComparison.Ordinal);

        string screen = await Logo.RunAsync("TO SQUARE :S REPEAT 4 [FD :S RT 90] END SQUARE 10 PRINT XCOR");
        Assert.Contains("0", screen, StringComparison.Ordinal);
    }

    [Fact]
    public static async Task TO_refuses_an_input_name_with_punctuation()
    {
        await Logo.ExpectErrorAsync("TO SQUARE :S! FD :S! END", LogoErrorCode.BadName);
    }

    [Fact]
    public static async Task MAKE_refuses_an_unspellable_variable_name()
    {
        await Logo.ExpectErrorAsync("MAKE \"MY-VAR 5", LogoErrorCode.BadName);
        await Logo.ExpectErrorAsync("MAKE \"A.B 5", LogoErrorCode.BadName);
    }

    [Fact]
    public static async Task MAKE_still_accepts_a_number_as_a_name()
    {
        // Logo itself allows this, so the new rule must not quietly take it away.
        var engine = Logo.New();
        await engine.RunAsync("MAKE 1 5");
        Assert.True(engine.Workspace.Globals.ContainsKey("1"));
    }

    [Fact]
    public static async Task PUT_refuses_an_unspellable_property_name()
    {
        await Logo.ExpectErrorAsync("PUT \"PLAYER \"S.CORE 10", LogoErrorCode.BadName);
    }

    [Fact]
    public static async Task LOCAL_refuses_an_unspellable_name()
    {
        await Logo.ExpectErrorAsync("TO TRIAL LOCAL [A-B] MAKE \"A-B 1 END TRIAL", LogoErrorCode.BadName);
    }

    [Fact]
    public static void A_bad_name_in_a_saved_session_does_not_lose_the_rest()
    {
        // Sessions written before the rule existed hold names the new validator refuses. Restore
        // skips the one bad procedure rather than failing the whole session.
        var engine = Logo.New();
        Assert.False(engine.DefineProcedureFromSource("TO BAD-NAME FD 10 END"));
        Assert.True(engine.DefineProcedureFromSource("TO GOOD_NAME FD 10 END"));
        Assert.True(engine.Workspace.HasProcedure("GOOD_NAME"));
        Assert.False(engine.Workspace.HasProcedure("BAD-NAME"));
    }

    [Fact]
    public static async Task An_exported_session_still_loads_after_names_are_checked()
    {
        // A session saved before the rule can hold a name this Logo refuses. The export must not
        // write a line that would stop the rest of the file running, so it comments it out.
        var session = new MaturinLogo.Core.Sessions.LogoSession();
        session.Procedures.Add("TO GOOD_ONE FD 10 END");
        session.Procedures.Add("TO BAD-NAME FD 20 END");
        session.Globals.Add(new MaturinLogo.Core.Sessions.StoredValue { Name = "GOOD_VAR", Text = "1" });
        session.Globals.Add(new MaturinLogo.Core.Sessions.StoredValue { Name = "BAD-VAR", Text = "2" });

        string source = MaturinLogo.Core.Sessions.LogoSourceExporter.Export(session);
        Assert.Contains("TO GOOD_ONE", source, StringComparison.Ordinal);
        Assert.Contains("MAKE \"GOOD_VAR", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TO BAD-NAME", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MAKE \"BAD-VAR", source, StringComparison.Ordinal);
        Assert.Contains("; SKIPPED PROCEDURE BAD-NAME", source, StringComparison.Ordinal);
        Assert.Contains("; SKIPPED VARIABLE BAD-VAR", source, StringComparison.Ordinal);

        // The whole file must still run, which is the promise the child made when they downloaded it.
        var engine = Logo.New();
        await engine.RunAsync(source);
        Assert.True(engine.Workspace.HasProcedure("GOOD_ONE"));
        Assert.True(engine.Workspace.Globals.ContainsKey("GOOD_VAR"));
    }

    // ---- SHAPE -------------------------------------------------------------------

    [Fact]
    public static async Task SHAPE_reaches_the_browser_with_the_turtle()
    {
        // The reported bug: the engine set the shape and nothing on screen ever moved, because the
        // turtle payload did not carry it.
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();

        await machine.RunAsync("SHAPE 3");
        using var json = JsonDocument.Parse(browser.LastTurtle);
        Assert.Equal(3, json.RootElement.GetProperty("shape").GetInt32());
    }

    [Fact]
    public static async Task SHAPE_refuses_a_shape_that_does_not_exist()
    {
        await Logo.ExpectErrorAsync("SHAPE 6", LogoErrorCode.OutOfRange);
        await Logo.ExpectErrorAsync("SHAPE -1", LogoErrorCode.OutOfRange);

        var engine = Logo.New();
        await engine.RunAsync("SETSHAPE 5");
        Assert.Equal(5, engine.Turtle.Shape);
    }

    [Fact]
    public static void The_shapes_the_interpreter_offers_are_the_ones_the_canvas_draws()
    {
        string js = ReadInterop();
        string[] drawn = Between(js, "const SHAPES = [", "];")
            .Split(',')
            .Select(s => s.Trim().Trim('\'', ' ', '\r', '\n'))
            .Where(s => s.Length > 0)
            .ToArray();

        Assert.Equal(TurtleShapes.Names, drawn);

        // HELP must describe the same shapes, or it promises something SHAPE refuses.
        Primitive shape = PrimitiveTable.CreateDefault().All.First(p => p.Name == "SHAPE");
        foreach (string name in TurtleShapes.Names)
            Assert.Contains(name.ToLowerInvariant(), shape.Doc, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the palette --------------------------------------------------------------

    [Fact]
    public static void The_browser_palette_is_the_interpreters_palette()
    {
        string js = ReadInterop();
        string[] hex = Between(js, "const PALETTE = [", "];")
            .Split(',')
            .Select(s => s.Trim().Trim('\'', ' ', '\r', '\n'))
            .Where(s => s.Length > 0)
            .ToArray();

        Assert.Equal(ApplePalette.Count, hex.Length);
        Assert.Equal(ApplePalette.Hex, hex);
    }

    [Fact]
    public static async Task The_extended_colours_are_reachable_and_reach_the_scene()
    {
        var engine = Logo.New();
        await engine.RunAsync("SETPC 8");
        Assert.Equal(8, engine.Turtle.PenColor);

        await engine.RunAsync("SETPC \"RED");
        Assert.Equal(8, engine.Turtle.PenColor);

        await engine.RunAsync("SETPC 15");
        Assert.Equal(15, engine.Turtle.PenColor);

        // The old bug: 6-15 folded onto 0-5, so a program that picked RED drew green.
        await engine.RunAsync("SETPC 6 FD 10");
        var segment = (DrawSegment)engine.Scene.Items[^1];
        Assert.Equal(6, segment.Color);
    }

    [Fact]
    public static async Task Colours_above_fifteen_are_still_refused()
    {
        await Logo.ExpectErrorAsync("SETPC 16", LogoErrorCode.OutOfRange);
    }

    // ---- SETSPEED -----------------------------------------------------------------

    [Fact]
    public static async Task SETSPEED_sets_and_reports_the_speed()
    {
        var engine = Logo.New();
        await engine.RunAsync("SETSPEED 50");
        Assert.Equal(50, engine.Speed);

        await engine.RunAsync("PRINT SPEED");
        Assert.Contains("50", Logo.ScreenText(engine), StringComparison.Ordinal);

        await engine.RunAsync("PRINT GETSPEED");
        int reported = Logo.ScreenText(engine).Split('\n').Count(l => l.Trim() == "50");
        Assert.Equal(2, reported);   // both SPEED and GETSPEED must answer

        await engine.RunAsync("SETSPEED 100");
        Assert.Equal(100, engine.Speed);
    }

    [Theory]
    [InlineData("SETSPEED 24")]
    [InlineData("SETSPEED 101")]
    [InlineData("SETSPEED 0")]
    [InlineData("SETSPEED 200")]
    public static async Task SETSPEED_refuses_a_number_outside_25_to_100(string command)
    {
        LogoException ex = await Logo.ExpectErrorAsync(command, LogoErrorCode.OutOfRange);
        Assert.Contains("25", ex.Hint, StringComparison.Ordinal);
        Assert.Contains("100", ex.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public static void Speed_turns_into_a_pause_that_ends_at_no_pause()
    {
        Assert.Equal(0, Engine.FrameDelayMs(Engine.MaxSpeed));
        Assert.Equal(450, Engine.FrameDelayMs(Engine.MinSpeed));
        Assert.True(Engine.FrameDelayMs(25) > Engine.FrameDelayMs(50));
        Assert.True(Engine.FrameDelayMs(50) > Engine.FrameDelayMs(75));
    }

    [Fact]
    public static async Task Reduced_motion_draws_instantly_however_speed_was_left()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.SetSpeed(25);
        machine.ReducedMotion = true;

        Assert.True(machine.Engine.InstantDraw);

        // The settings screen says so rather than showing a speed the machine is ignoring.
        machine.ShowSettings();
        Assert.Contains("INSTANT", MachineHarness.ScreenText(machine), StringComparison.Ordinal);

        machine.ReducedMotion = false;
        Assert.False(machine.Engine.InstantDraw);
        Assert.Equal(25, machine.Engine.Speed);
    }

    [Fact]
    public static async Task Speed_is_remembered_between_visits()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        await machine.RunAsync("SETSPEED 40");
        await machine.SaveAsync();

        (LogoMachine again, _) = await MachineHarness.AtListenerAsync(browser.StoreJson);
        Assert.Equal(40, again.Engine.Speed);
    }

    [Fact]
    public static async Task An_old_store_without_a_speed_gets_the_fastest_one()
    {
        // A store written by 1.0 has no speed field, so it deserialises to 0 - which must not
        // become "slower than the slowest setting".
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        string json = Regex.Replace(browser.StoreJson!, "\"speed\":[0-9]+,?", "");
        Assert.False(json.Contains("speed", StringComparison.Ordinal));

        (LogoMachine again, _) = await MachineHarness.AtListenerAsync(json);
        Assert.Equal(Engine.MaxSpeed, again.Engine.Speed);
        _ = machine;
    }

    [Fact]
    public static async Task The_turtle_payload_carries_everything_the_canvas_needs_to_animate()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        await machine.RunAsync("FD 10 RT 45 SETPC 9 PENSIZE 3 SHAPE 4 SETSPEED 50");

        using var json = JsonDocument.Parse(browser.LastTurtle);
        JsonElement turtle = json.RootElement;
        Assert.Equal(4, turtle.GetProperty("shape").GetInt32());
        Assert.Equal(9, turtle.GetProperty("color").GetInt32());
        Assert.Equal(3, turtle.GetProperty("size").GetDouble());
        Assert.True(turtle.GetProperty("ms").GetInt32() > 0,
            "a slow speed must give the canvas time to tween the move");

        await machine.RunAsync("SETSPEED 100");
        using var fast = JsonDocument.Parse(browser.LastTurtle);
        Assert.Equal(0, fast.RootElement.GetProperty("ms").GetInt32());
    }

    // ---- the quick reference -------------------------------------------------------

    [Fact]
    public static void Every_reference_row_fits_the_screen()
    {
        foreach (string line in QuickReference.ReferenceLines(PrimitiveTable.CreateDefault()))
            Assert.True(line.Length <= TextScreen.Cols, $"too wide ({line.Length}): {line}");
    }

    [Fact]
    public static void Reference_rows_are_never_cut_off_with_an_ellipsis()
    {
        // The bug was Shorten(): a description longer than its column ended in an ellipsis and the
        // child never learned what the rest of the sentence said.
        string all = string.Join("\n", QuickReference.ReferenceLines(PrimitiveTable.CreateDefault()));
        Assert.DoesNotContain("…", all, StringComparison.Ordinal);
    }

    [Fact]
    public static void Every_command_name_is_printed_in_full()
    {
        // The name field is a fixed width, so a command named longer than it would be cut rather
        // than wrapped. Asserting that cannot happen is cheaper than carrying a special case.
        var table = PrimitiveTable.CreateDefault();
        var lines = QuickReference.ReferenceLines(table);

        foreach (Primitive primitive in table.All.Where(p => !p.Hidden))
        {
            Assert.True(primitive.Name.Length <= QuickReference.NameColumn,
                $"{primitive.Name} no longer fits the reference's name column");
            Assert.Contains(lines, l => EntryName(l) == primitive.Name);
        }
    }

    [Fact]
    public static void The_reference_page_is_not_in_registration_order()
    {
        // The table hands out primitives in the order they were registered, which is exactly why
        // the page used to look random. Sorting happens at render, so what a child sees must not
        // match the table's own order.
        var table = PrimitiveTable.CreateDefault();
        var registration = table.InFamily(CommandFamily.Turtle).Where(p => !p.Hidden).Select(p => p.Name).ToList();
        Assert.NotEqual(registration.OrderBy(n => n, StringComparer.Ordinal).ToList(), registration);

        var rendered = QuickReference.ReferenceLines(table)
            .Select(line => EntryName(line))
            .Where(n => n is not null)
            .Select(n => n!)
            .ToList();

        Assert.Contains("FD", rendered, StringComparer.Ordinal);
        Assert.True(rendered.IndexOf("ARC") < rendered.IndexOf("HOME"), "the turtle block is not alphabetical");
    }

    [Fact]
    public static void The_reference_entries_are_alphabetical_within_every_block()
    {
        // Registration order used to leak into the page, which is what made the commands look
        // random. Read the entries back off the rendered page, block by block.
        var blocks = new List<List<string>>();
        var current = new List<string>();

        foreach (string line in QuickReference.ReferenceLines(PrimitiveTable.CreateDefault()))
        {
            string? name = EntryName(line);
            if (name is not null) { current.Add(name); continue; }

            // A section heading starts a new block; anything else is prose inside the current one.
            if (line.Contains("====", StringComparison.Ordinal) && current.Count > 0)
            {
                blocks.Add(current);
                current = new List<string>();
            }
        }
        if (current.Count > 0) blocks.Add(current);

        Assert.True(blocks.Count >= 3, $"expected several blocks, found {blocks.Count}");
        foreach (var block in blocks)
            Assert.Equal(block.OrderBy(n => n, StringComparer.Ordinal).ToList(), block);
    }

    [Fact]
    public static void A_long_description_wraps_and_nothing_is_lost()
    {
        const string doc = "This explanation is far too long to fit inside the column it was given to.";
        var table = new PrimitiveTable();
        table.Add(new PrimitiveSpec
        {
            Name = "LONGONE",
            Inputs = 0,
            Family = CommandFamily.Turtle,
            Doc = doc,
            Example = "LONGONE",
        });

        var lines = QuickReference.ReferenceLines(table);
        int entry = lines.FindIndex(l => EntryName(l, table) == "LONGONE");
        Assert.True(entry >= 0, "the entry was not printed at all");

        // The rows that follow it, up to the next entry, are its wrapped continuation.
        var wrapped = new List<string> { lines[entry] };
        for (int i = entry + 1; i < lines.Count && lines[i].StartsWith("          ", StringComparison.Ordinal); i++)
            wrapped.Add(lines[i]);

        Assert.True(wrapped.Count >= 2, "a description this long must wrap onto more than one row");
        Assert.All(wrapped, l => Assert.True(l.Length <= TextScreen.Cols, $"too wide: {l}"));

        string recovered = string.Join(" ", wrapped.Select(l => l.Trim()));
        foreach (string word in doc.Split(' '))
            Assert.Contains(word, recovered, StringComparison.Ordinal);
    }

    [Fact]
    public static void The_key_list_fits_and_mentions_the_newest_keys()
    {
        foreach (string line in QuickReference.KeyLines())
            Assert.True(line.Length <= TextScreen.Cols - 4, $"key line too wide: {line}");

        string all = string.Join("\n", QuickReference.KeyLines());
        Assert.Contains("ALT-, SETTINGS", all, StringComparison.Ordinal);
        Assert.Contains("ALT-I INFO", all, StringComparison.Ordinal);
    }

    /// <summary>
    /// The command a row describes, or null when it is prose. An entry is "  NAME" padded to the
    /// name column followed by its description, so a row whose leading token is a command and that
    /// has no double space inside it is an entry. The name index at the top of each family and the
    /// key list both contain runs of spaces, and neither is an entry.
    /// <para>
    /// <paramref name="table"/> decides which names count, because one test renders a table of its
    /// own rather than the real one.
    /// </para>
    /// </summary>
    private static string? EntryName(string line, PrimitiveTable? table = null)
    {
        table ??= Cached;
        if (!line.StartsWith("  ", StringComparison.Ordinal)) return null;
        string after = line.Substring(2);
        if (after.Length == 0 || after[0] == ' ') return null;       // indented continuation

        int space = after.IndexOf(' ');
        string name = space < 0 ? after : after.Substring(0, space);
        return table.Contains(name) ? name : null;
    }

    private static readonly PrimitiveTable Cached = PrimitiveTable.CreateDefault();

    // ---- the info dashboard ---------------------------------------------------------

    [Fact]
    public static async Task Info_shows_every_key_and_nothing_runs_off_the_grid()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        await machine.RunAsync("TO A_LONG_PROCEDURE_NAME FD 10 END");

        machine.ShowInfo();
        string text = MachineHarness.ScreenText(machine);

        // The old dashboard hand-wrote four key lines of 38 characters at column 3, which is how
        // the last character fell off the right-hand edge.
        foreach (string line in QuickReference.KeyLines())
            Assert.Contains(line.Trim(), text, StringComparison.Ordinal);

        Assert.Contains("SPEED", text, StringComparison.Ordinal);
        Assert.Contains("PROCEDURES", text, StringComparison.Ordinal);
        foreach (string row in machine.Screen.Snapshot())
            Assert.True(row.TrimEnd().Length <= TextScreen.Cols);
    }

    [Fact]
    public static async Task The_session_list_does_not_overrun_its_row()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        for (int i = 0; i < 11; i++) machine.NewSession();

        machine.ShowSessions();
        string text = MachineHarness.ScreenText(machine);

        // The old format ran to 41 columns for a two-digit session number, which is how the last
        // digit of a date vanished off the right edge.
        Assert.Contains("SESSION11.logo", text, StringComparison.Ordinal);
        foreach (string row in machine.Screen.Snapshot())
            Assert.True(row.TrimEnd().Length <= TextScreen.Cols);
    }

    // ---- settings --------------------------------------------------------------------

    [Fact]
    public static async Task Alt_comma_opens_settings_and_closes_it_again()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        MachineHarness.Type(machine, "FD 10");

        machine.HandleKey("A-COMMA");
        Assert.Equal(AppScreen.Settings, machine.ScreenKind);

        machine.HandleKey("ESC");
        Assert.Equal(AppScreen.Listener, machine.ScreenKind);

        // The half-typed line has to survive the trip, which is what the overlay stash is for.
        Assert.Equal("FD 10", machine.InputText);
    }

    [Fact]
    public static async Task The_settings_screen_shows_every_setting_it_promises()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.ShowSettings();

        string text = MachineHarness.ScreenText(machine);
        Assert.Contains("MONITOR", text, StringComparison.Ordinal);
        Assert.Contains("CRT", text, StringComparison.Ordinal);
        Assert.Contains("MODE", text, StringComparison.Ordinal);
        Assert.Contains("SPEED", text, StringComparison.Ordinal);
        Assert.Contains("REDUCED MOTION", text, StringComparison.Ordinal);
        Assert.Contains("WHAT'S NEW", text, StringComparison.Ordinal);
        Assert.Contains("ABOUT", text, StringComparison.Ordinal);
        Assert.Contains("ESC BACK", text, StringComparison.Ordinal);

        // The box's borders must survive: a full-width row would have painted straight over them.
        Assert.Equal('╔', machine.Screen.Chars[1, 0]);
        Assert.Equal('╗', machine.Screen.Chars[1, TextScreen.Cols - 1]);
        Assert.Equal('║', machine.Screen.Chars[8, 0]);
        Assert.Equal('║', machine.Screen.Chars[8, TextScreen.Cols - 1]);

        // Past the box's title, the top rule is still there - a full-width PutLine would have
        // eaten it, which is how a framed page used to lose its right edge.
        Assert.Equal('═', machine.Screen.Chars[1, TextScreen.Cols - 2]);
    }

    [Fact]
    public static async Task Settings_arrows_change_the_highlighted_value()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        machine.ShowSettings();

        Assert.Equal("green", machine.Theme);
        machine.HandleKey("RIGHT");
        Assert.Equal("amber", machine.Theme);
        Assert.Equal("amber", browser.Theme);

        GoToSetting(machine, "CRT");
        machine.HandleKey("ENTER");
        Assert.True(machine.CrtEmulation);

        GoToSetting(machine, "MODE");
        machine.HandleKey("LEFT");
        Assert.Equal(LogoMode.Advanced, machine.Engine.Mode);

        machine.HandleKey("ESC");
        Assert.Equal(AppScreen.Listener, machine.ScreenKind);
    }

    [Fact]
    public static async Task Settings_moves_the_drawing_speed_and_wraps_at_the_ends()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.SetSpeed(100);
        machine.ShowSettings();

        GoToSetting(machine, "SPEED");
        Assert.Contains("100", MachineHarness.ScreenText(machine), StringComparison.Ordinal);

        machine.HandleKey("LEFT");
        Assert.Equal(95, machine.Engine.Speed);

        machine.HandleKey("RIGHT");
        Assert.Equal(100, machine.Engine.Speed);

        machine.HandleKey("RIGHT");
        Assert.Equal(Engine.MinSpeed, machine.Engine.Speed);   // wrapped, not stuck at the wall
    }

    [Fact]
    public static async Task The_reduced_motion_row_tells_and_does_not_toggle()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.ShowSettings();

        GoToSetting(machine, "REDUCED MOTION");

        machine.HandleKey("RIGHT");
        machine.HandleKey("ENTER");
        Assert.False(machine.ReducedMotion);
        Assert.Equal(AppScreen.Settings, machine.ScreenKind);
    }

    [Fact]
    public static async Task Settings_opens_the_pages_and_returns_to_settings()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.ShowSettings();

        GoToSetting(machine, "COLOUR CHART");
        machine.HandleKey("ENTER");
        Assert.Equal(AppScreen.Colors, machine.ScreenKind);

        machine.HandleKey("ESC");
        Assert.Equal(AppScreen.Settings, machine.ScreenKind);

        GoToSetting(machine, "WHAT'S NEW");
        machine.HandleKey("ENTER");
        Assert.Equal(AppScreen.WhatsNew, machine.ScreenKind);

        machine.HandleKey("ESC");
        Assert.Equal(AppScreen.Settings, machine.ScreenKind);

        GoToSetting(machine, "ABOUT");
        machine.HandleKey("ENTER");
        Assert.Equal(AppScreen.About, machine.ScreenKind);

        machine.HandleKey("ESC");
        Assert.Equal(AppScreen.Settings, machine.ScreenKind);

        machine.HandleKey("ESC");
        Assert.Equal(AppScreen.Listener, machine.ScreenKind);
    }

    [Fact]
    public static async Task The_settings_highlight_never_lands_on_the_separator()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.ShowSettings();

        for (int i = 0; i < 24; i++)
        {
            machine.HandleKey("DOWN");
            int row = RowOfHighlight(machine);
            Assert.True(row >= 0, "no row of the settings screen is highlighted");
            Assert.True(machine.Screen.GetRowText(row).Trim().Length > 0,
                $"the highlight is sitting on the blank separator (row {row})");
        }
    }

    [Fact]
    public static async Task The_SETTINGS_command_opens_the_screen_after_the_line()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        await machine.RunAsync("SETTINGS");
        Assert.Equal(AppScreen.Settings, machine.ScreenKind);

        // And it waits for the drawing to finish rather than stealing the screen mid-program.
        (LogoMachine other, _) = await MachineHarness.AtListenerAsync();
        await other.RunAsync("FD 10 SETTINGS FD 20");
        Assert.Equal(AppScreen.Settings, other.ScreenKind);
        Assert.Equal(2, other.Engine.Scene.Items.Count);
    }

    [Fact]
    public static async Task Settings_survive_a_reload()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        machine.ShowSettings();
        machine.HandleKey("RIGHT");       // amber
        machine.HandleKey("DOWN");
        machine.HandleKey("ENTER");       // CRT on
        await machine.SaveAsync();

        (LogoMachine again, _) = await MachineHarness.AtListenerAsync(browser.StoreJson);
        Assert.Equal("amber", again.Theme);
        Assert.True(again.CrtEmulation);
    }

    [Fact]
    public static async Task Settings_can_be_opened_from_the_splash()
    {
        (LogoMachine machine, _) = await MachineHarness.StartAsync();
        machine.HandleKey("A-COMMA");
        Assert.Equal(AppScreen.Settings, machine.ScreenKind);

        machine.HandleKey("ESC");
        Assert.Equal(AppScreen.Splash, machine.ScreenKind);

        // Opening settings must not have started Logo on the way in or out.
        Assert.NotEqual(AppScreen.Listener, machine.ScreenKind);
    }

    // ---- about ------------------------------------------------------------------------

    [Fact]
    public static async Task About_shows_the_same_text_as_the_start_screen()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();

        machine.ShowSplash();
        string splash = MachineHarness.ScreenText(machine);

        machine.ShowAbout();
        string about = MachineHarness.ScreenText(machine);

        foreach (string line in LogoInfo.HomageLines)
        {
            Assert.Contains(line, splash, StringComparison.Ordinal);
            Assert.Contains(line, about, StringComparison.Ordinal);
        }

        foreach (string art in SplashArt.Turtle.Where(a => a.Trim().Length > 0))
        {
            Assert.Contains(art.Trim(), splash, StringComparison.Ordinal);
            Assert.Contains(art.Trim(), about, StringComparison.Ordinal);
        }

        Assert.Contains("M A T U R I N   L O G O", about, StringComparison.Ordinal);
        Assert.Contains(LogoInfo.Version, about, StringComparison.Ordinal);

        // The invitation to start belongs to the splash only; About is a page you read and leave.
        Assert.Contains("PRESS ANY KEY", splash, StringComparison.Ordinal);
        Assert.DoesNotContain("PRESS ANY KEY", about, StringComparison.Ordinal);
        Assert.Contains("PRESS ESC TO GO BACK", about, StringComparison.Ordinal);
    }

    [Fact]
    public static async Task About_closes_back_onto_the_splash()
    {
        (LogoMachine machine, _) = await MachineHarness.StartAsync();

        // ABOUT is the last row of the list, and the arrows jump the blank separator rather than
        // parking on it - which is why the test names the row instead of counting down to it.
        machine.ShowSettings();
        GoToSetting(machine, "ABOUT");
        Assert.Contains("ABOUT", HighlightedRow(machine), StringComparison.Ordinal);

        machine.HandleKey("ENTER");
        Assert.Equal(AppScreen.About, machine.ScreenKind);

        machine.HandleKey("ESC");
        Assert.Equal(AppScreen.Settings, machine.ScreenKind);

        machine.HandleKey("ESC");
        Assert.Equal(AppScreen.Splash, machine.ScreenKind);
        Assert.NotEqual(AppScreen.Listener, machine.ScreenKind);
    }

    /// <summary>The text of the highlighted settings row, so a test can name the row it means.</summary>
    private static string HighlightedRow(LogoMachine machine)
    {
        int row = RowOfHighlight(machine);
        Assert.True(row >= 0, "no settings row is highlighted");
        return machine.Screen.GetRowText(row).Trim();
    }

    /// <summary>
    /// Walk the settings cursor down to the row naming <paramref name="label"/>, and return how many
    /// presses it took. Settings rows are named rather than counted: a screen that gains a row should
    /// not have to renegotiate every test that reaches past it.
    /// </summary>
    private static int GoToSetting(LogoMachine machine, string label)
    {
        for (int presses = 0; presses <= 24; presses++)
        {
            if (HighlightedRow(machine).Contains(label, StringComparison.Ordinal)) return presses;
            machine.HandleKey("DOWN");
        }

        throw new Xunit.Sdk.XunitException($"the settings screen has no row naming {label}");
    }

    [Fact]
    public static async Task The_splash_only_honours_the_keys_it_advertises()
    {
        // "PRESS ANY KEY" has to mean any key starts Logo, so a bare letter is not a shortcut here -
        // and W is only a shortcut while the splash is actually showing the offer.
        (LogoMachine machine, _) = await MachineHarness.StartAsync();
        machine.HandleKey("W");                       // offer is showing, so W reads it
        Assert.Equal(AppScreen.WhatsNew, machine.ScreenKind);

        machine.HandleKey("ESC");
        Assert.Equal(AppScreen.Splash, machine.ScreenKind);

        machine.HandleKey("W");                       // read, so the offer is gone and W starts
        Assert.Equal(AppScreen.Listener, machine.ScreenKind);
    }

    [Fact]
    public static async Task Alt_comma_opens_settings_from_the_splash()
    {
        (LogoMachine machine, _) = await MachineHarness.StartAsync();
        machine.HandleKey("A-COMMA");
        Assert.Equal(AppScreen.Settings, machine.ScreenKind);
    }

    // ---- what's new ---------------------------------------------------------------------

    [Fact]
    public static void Every_what_s_new_row_fits_the_screen()
    {
        foreach (string line in LogoWhatsNew.Lines())
            Assert.True(line.Length <= TextScreen.Cols, $"too wide ({line.Length}): {line}");
    }

    [Fact]
    public static void What_s_new_covers_every_released_version()
    {
        string doc = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Architecture.md"));
        int history = doc.IndexOf("## Revision history", StringComparison.Ordinal);
        Assert.True(history >= 0);

        var released = Regex.Matches(doc.Substring(history), @"^\|\s*\*\*(\d+\.\d+)\*\*\s*\|", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.NotEmpty(released);
        Assert.Contains(LogoInfo.Version, released, StringComparer.Ordinal);

        // Newest first, and one entry per released version: a release with no notes is a release
        // the splash cannot announce.
        Assert.Equal(released, LogoWhatsNew.Versions.ToList());
    }

    [Fact]
    public static void What_s_new_mentions_the_things_that_actually_changed()
    {
        // Pinned to the 1.1 entry rather than "the newest one", because what 1.1 changed is what
        // this release's tests know about; the current release's own topics are checked by the
        // tests that added them.
        LogoWhatsNew.Entry entry = LogoWhatsNew.Entries.First(e => e.Version == "1.1");
        string all = string.Join(" ", entry.Lines).ToUpperInvariant();
        foreach (string topic in new[] { "SHAPE", "COLOUR", "SETSPEED", "SETTINGS", "HELP", "NAME" })
            Assert.Contains(topic, all, StringComparison.Ordinal);
    }

    [Fact]
    public static async Task The_splash_offers_what_s_new_once_and_then_stops()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.StartAsync();

        string offer = "WHAT'S NEW IN " + LogoInfo.Version;
        Assert.Contains(offer, MachineHarness.ScreenText(machine), StringComparison.Ordinal);

        machine.HandleKey("W");
        Assert.Equal(AppScreen.WhatsNew, machine.ScreenKind);

        machine.HandleKey("ESC");
        Assert.Equal(AppScreen.Splash, machine.ScreenKind);

        // Read is read: the offer goes away, and it stays away after a reload.
        Assert.DoesNotContain(offer, MachineHarness.ScreenText(machine), StringComparison.Ordinal);

        (LogoMachine again, _) = await MachineHarness.StartAsync(browser.StoreJson);
        Assert.DoesNotContain(offer, MachineHarness.ScreenText(again), StringComparison.Ordinal);
    }

    [Fact]
    public static async Task What_s_new_pages_like_the_reference()
    {
        (LogoMachine machine, _) = await MachineHarness.StartAsync();
        machine.HandleKey("W");

        string text = MachineHarness.ScreenText(machine);
        Assert.Contains("MATURIN LOGO WHAT'S NEW", text, StringComparison.Ordinal);
        Assert.Contains("ARROWS SCROLL", text, StringComparison.Ordinal);

        machine.HandleKey("END");
        Assert.Contains("OF ", MachineHarness.ScreenText(machine), StringComparison.Ordinal);

        machine.HandleKey("HOME");
        Assert.Contains("MATURIN LOGO WHAT'S NEW", MachineHarness.ScreenText(machine), StringComparison.Ordinal);
    }

    // ---- the speed bar ---------------------------------------------------------------------

    [Fact]
    public static void Every_key_token_the_machine_reads_is_one_the_browser_can_send()
    {
        // The machine switches on tokens the browser produces, and the two halves live in different
        // languages with nothing between them but a string. `Alt-,` opened nothing at all until
        // mapKey was made to return 'A-COMMA' rather than the bare 'A-,' that a punctuation key
        // delivers - a renamed or unmapped token is a dead shortcut that no C# test can see.
        string js = ReadInterop();
        string machine = File.ReadAllText(Path.Combine(RepoRoot(), "Src", "MaturinLogo.Core", "Machine", "LogoMachine.cs"));

        var tokens = Regex.Matches(machine, "case \"([A-Z0-9?_/+.-]+)\":")
            .Select(m => m.Groups[1].Value)
            .Where(t => t.Length > 1 && !t.All(char.IsDigit))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(tokens);

        // Alt plus a letter is built from the physical key, so those tokens are never literals.
        Assert.Contains("'A-' + k.toUpperCase()", js, StringComparison.Ordinal);

        foreach (string token in tokens)
        {
            if (Regex.IsMatch(token, "^A-[A-Z]$")) continue;
            Assert.True(js.Contains("'" + token + "'", StringComparison.Ordinal),
                $"the machine reads {token} but interop.js never produces it");
        }

        // The punctuation Alt keys each need their own case, because a browser reports them by the
        // character rather than a name.
        foreach (string token in new[] { "A-?", "A-SLASH", "A-COMMA", "C-SLASH" })
            Assert.Contains("'" + token + "'", js, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Docs", "Architecture.md"))) return dir.FullName;
        throw new InvalidOperationException("could not find the repository root above " + AppContext.BaseDirectory);
    }

    [Fact]
    public static void The_speed_bar_spans_the_range()
    {
        string slow = LogoMachine.SpeedBar(Engine.MinSpeed);
        string mid = LogoMachine.SpeedBar(Engine.MinSpeed + 25);
        string fast = LogoMachine.SpeedBar(Engine.MaxSpeed);

        // Same width at every speed, or the row would jitter as the arrows are held down.
        Assert.Equal(slow.Length, fast.Length);
        Assert.Equal(slow.Length, mid.Length);
        Assert.StartsWith("<", slow, StringComparison.Ordinal);
        Assert.EndsWith(">", slow, StringComparison.Ordinal);

        Assert.DoesNotContain('=', slow);
        Assert.DoesNotContain('-', fast);
        Assert.Contains('-', mid);
        Assert.Contains('=', mid);
    }

    [Fact]
    public static void The_speed_bar_uses_only_the_bitmap_font_can_draw()
    {
        // A geometric or block character the self-hosted font does not carry shows as an empty box,
        // which on a terminal is indistinguishable from a bug.
        string bar = LogoMachine.SpeedBar(60);
        Assert.All(bar, c => Assert.True(c < 128, $"non-ASCII in the slider: {c}"));
    }

    [Fact]
    public static async Task The_settings_screen_keeps_every_row_inside_the_frame()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();

        // Widest value at every speed, plus the longest theme and mode names, must still fit inside
        // the frame. The box runs from row 1 to the row above the key list; only its own rows carry
        // the left and right borders.
        foreach (int speed in new[] { Engine.MinSpeed, 50, Engine.MaxSpeed })
        {
            machine.SetSpeed(speed);
            machine.ShowSettings();

            for (int r = 0; r < TextScreen.Rows; r++)
                Assert.True(machine.Screen.GetRowText(r).Length <= TextScreen.Cols,
                    $"row {r} overruns at speed {speed}");

            // The real promise: the value is printed whole. A row that overran would have lost the
            // end of the slider, and the child would not be able to tell.
            string text = MachineHarness.ScreenText(machine);
            Assert.Contains($"{speed} {LogoMachine.SpeedBar(speed)}", text, StringComparison.Ordinal);

            int side = 0;
            for (int r = 0; r < TextScreen.Rows; r++)
            {
                if (machine.Screen.Chars[r, 0] != '║') continue;
                side++;
                Assert.Equal('║', machine.Screen.Chars[r, TextScreen.Cols - 1]);
            }
            Assert.True(side >= 6, $"the frame has only {side} bordered rows");

            machine.HandleKey("ESC");
        }
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static int RowOfHighlight(LogoMachine machine)
    {
        for (int r = 0; r < TextScreen.Rows; r++)
            for (int c = 1; c < TextScreen.Cols - 1; c++)
                if (machine.Screen.Attrs[r, c].HasFlag(CellAttr.Reverse)) return r;
        return -1;
    }

    private static string ReadInterop() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "interop.js"));

    /// <summary>The text between the first <paramref name="open"/> and the next <paramref name="close"/>.</summary>
    private static string Between(string text, string open, string close)
    {
        int start = text.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{open} is missing from interop.js");
        int end = text.IndexOf(close, start, StringComparison.Ordinal);
        Assert.True(end > start, $"{close} is missing after {open}");
        return text.Substring(start + open.Length, end - start - open.Length);
    }
}
