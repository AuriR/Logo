using MaturinLogo.Core.Interpreter;
using MaturinLogo.Core.Machine;
using MaturinLogo.Core.Screen;
using MaturinLogo.Core.Sessions;

namespace MaturinLogo.Core.Tests;

/// <summary>
/// The machine is browser-free, so it is tested against these in-memory stand-ins for localStorage,
/// the canvas, the theme and the download. Recording what the browser was told is what lets the
/// tests check that a keystroke actually reached the screen.
/// </summary>
public sealed class FakeBrowser
{
    public string? StoreJson;
    public readonly List<(string Name, string Content)> Downloads = new();
    public string Theme = "";
    public bool Crt;
    public string? PickResult;
    public string LastScene = "";
    public int LastTextRows = -1;
    public int LastBackground;
    public string LastTurtle = "";
    public int AppendCalls;
    public int ReplayCalls;
    public int YieldCount;

    /// <summary>What a write to localStorage should report. Set to false to simulate QuotaExceededError.</summary>
    public bool WriteResult = true;

    public void Attach(LogoMachine machine)
    {
        machine.ReadStoreFunc = _ => Task.FromResult(StoreJson);
        machine.WriteStoreFunc = (_, json) =>
        {
            if (WriteResult) StoreJson = json;
            return Task.FromResult(WriteResult);
        };
        machine.DownloadFunc = (name, content) => { Downloads.Add((name, content)); return Task.CompletedTask; };
        machine.SetThemeFunc = (theme, crt) => { Theme = theme; Crt = crt; return Task.CompletedTask; };
        machine.PickFileFunc = _ => Task.FromResult(PickResult);
        machine.ReplayGraphicsFunc = (items, background, textRows) =>
        {
            LastScene = items;
            LastBackground = background;
            LastTextRows = textRows;
            ReplayCalls++;
            return Task.CompletedTask;
        };
        machine.AppendGraphicsFunc = _ => { AppendCalls++; return Task.CompletedTask; };
        machine.SetTurtleFunc = turtle => { LastTurtle = turtle; return Task.CompletedTask; };

        // A real await, not a completed task: the tests that press Esc mid-program need the engine
        // to actually give the thread back between steps, exactly as the browser does.
        machine.YieldFrameFunc = async () =>
        {
            YieldCount++;
            await Task.Delay(1);
        };
    }
}

public static class MachineHarness
{
    /// <summary>A machine with a fake browser attached and the store already read.</summary>
    public static async Task<(LogoMachine Machine, FakeBrowser Browser)> StartAsync(string? storedJson = null)
    {
        var machine = new LogoMachine();
        var browser = new FakeBrowser();
        browser.Attach(machine);
        browser.StoreJson = storedJson;
        await machine.StartAsync();
        return (machine, browser);
    }

    /// <summary>A machine sitting at the listener, past the splash.</summary>
    public static async Task<(LogoMachine Machine, FakeBrowser Browser)> AtListenerAsync(string? storedJson = null)
    {
        (LogoMachine machine, FakeBrowser browser) = await StartAsync(storedJson);
        machine.HandleKey("X");     // any key leaves the splash
        await machine.ReplayGraphicsAsync();
        return (machine, browser);
    }

    public static string ScreenText(LogoMachine machine) =>
        string.Join("\n", machine.Screen.Snapshot().Select(r => r.TrimEnd())).Trim('\n');

    /// <summary>Type one character at a time, which is how the keyboard really delivers a command.</summary>
    public static void Type(LogoMachine machine, string text)
    {
        foreach (char c in text) machine.HandleKey(c.ToString());
    }

    /// <summary>
    /// Type a command and press ENTER. Kept separate from waiting for the run: a program that is
    /// meant to be interrupted must still be running when Esc is pressed.
    /// </summary>
    public static void Submit(LogoMachine machine, string command)
    {
        Type(machine, command);
        machine.HandleKey("ENTER");
    }

    /// <summary>Type a command, press ENTER, and wait for Logo to finish with it.</summary>
    public static async Task RunAsync(LogoMachine machine, string command)
    {
        Submit(machine, command);
        await SettleAsync(machine);
    }

    /// <summary>Wait for a fire-and-forget run to finish, failing rather than hanging forever.</summary>
    public static async Task SettleAsync(LogoMachine machine, int milliseconds = 8000)
    {
        for (int waited = 0; waited < milliseconds; waited += 5)
        {
            if (!machine.IsBusy) return;
            await Task.Delay(5);
        }
        throw new TimeoutException("Logo never finished the command.");
    }

    /// <summary>
    /// Let queued continuations run without waiting for a long program to finish. Awaiting the
    /// scheduler is what a real browser does between keystrokes.
    /// </summary>
    public static async Task PumpAsync() =>
        await Task.Delay(1);
}

public class MachineTests
{
    // ---- start-up -------------------------------------------------------------

    [Fact]
    public async Task A_first_visit_makes_a_session_and_shows_the_splash()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.StartAsync();

        Assert.Equal(AppScreen.Splash, machine.ScreenKind);
        Assert.Equal("SESSION1.logo", machine.CurrentName);
        Assert.NotNull(browser.StoreJson);

        string text = MachineHarness.ScreenText(machine);
        Assert.Contains("M A T U R I N   L O G O", text);
        Assert.Contains(SplashArt.Turtle[5].Trim(), text);
        Assert.Contains("PRESS ANY KEY", text);
        Assert.Contains("FD 100", text);   // the first thing a child is told to type
    }

    [Fact]
    public async Task The_splash_shows_the_name_of_the_session_it_will_open()
    {
        // Make a second session from the listener, then check what a fresh visit promises.
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-L");
        machine.HandleKey("N");
        await machine.SaveAsync();

        string json = SessionStore.Serialize(machine.Store);
        // The splash names the session it is about to open, so a child knows where they are going.
        (LogoMachine second, _) = await MachineHarness.StartAsync(json);
        Assert.Equal(AppScreen.Splash, second.ScreenKind);
        Assert.Contains("SESSION2.logo", MachineHarness.ScreenText(second));
    }

    [Fact]
    public async Task Any_key_at_the_splash_opens_the_listener()
    {
        (LogoMachine machine, _) = await MachineHarness.StartAsync();
        machine.HandleKey("F");

        Assert.Equal(AppScreen.Listener, machine.ScreenKind);

        // The prompt's trailing space is trimmed by ScreenText, so the caret position is the
        // honest check: column 2 means "> " is sitting there waiting.
        Assert.StartsWith(">", MachineHarness.ScreenText(machine).Split('\n')[^1]);
        Assert.Equal(2, machine.CaretPosition().Col);
    }

    [Fact]
    public async Task Easy_mode_opens_on_the_turtle_page_with_four_text_rows()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();

        Assert.Equal(4, machine.Engine.LowerLines);
        Assert.Equal(TextScreen.Rows - 4, machine.Screen.WindowTop);
    }

    [Fact]
    public async Task Advanced_mode_opens_as_a_whole_screen_of_text()
    {
        (LogoMachine machine, _) = await MachineHarness.StartAsync();
        machine.HandleKey("A-M");
        machine.HandleKey("X");

        Assert.Equal(LogoMode.Advanced, machine.Engine.Mode);
        Assert.Equal(0, machine.Screen.WindowTop);
    }

    /// <summary>The shape interop.js receives for the live turtle.</summary>
    private sealed record TurtlePayload(double x, double y, double heading, int color, bool visible);

    private static TurtlePayload ParseTurtle(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<TurtlePayload>(json)!;

    [Fact]
    public async Task GRAPHICS_gives_the_screen_to_the_turtle_without_trapping_the_child()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "GRAPHICS");

        // ShowFullGraphics asks for every row, and the screen keeps one row for text so a child can
        // always type TEXT again. Being left with no way back is the failure this guards.
        Assert.Equal(TextScreen.Rows - 1, machine.Screen.WindowTop);
        Assert.Equal(machine.Screen.WindowBottom, machine.Screen.WindowTop);
        Assert.Equal(1, machine.Screen.WindowHeight);

        await MachineHarness.RunAsync(machine, "FD 50");
        Assert.Equal(50, machine.Engine.Turtle.Y, 6);

        // The typed line is visible even in that single row, which is the point of keeping it.
        MachineHarness.Type(machine, "FD 10");
        Assert.Contains("FD 10", MachineHarness.ScreenText(machine));

        // Clear the buffer before running anything else, or the unsubmitted text would be glued
        // onto the next command.
        machine.HandleKey("ESC");

        await MachineHarness.RunAsync(machine, "TEXT");
        Assert.Equal(0, machine.Screen.WindowTop);
        Assert.Equal(TextScreen.Rows - 1, machine.Screen.WindowBottom);
    }

    [Fact]
    public async Task LOWER_n_reserves_exactly_that_many_text_rows()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "LOWER 8");

        Assert.Equal(TextScreen.Rows - 8, machine.Screen.WindowTop);
        Assert.Equal(8, machine.Screen.WindowHeight);

        MachineHarness.Type(machine, "PRINT 3");
        Assert.Contains("PRINT 3", MachineHarness.ScreenText(machine));
    }

    [Fact]
    public async Task The_turtle_is_sent_to_the_canvas_and_shows_where_it_points()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "FD 100 RT 90");

        Assert.NotEmpty(browser.LastTurtle);
        TurtlePayload turtle = ParseTurtle(browser.LastTurtle);
        Assert.True(turtle.visible);

        // Heading 90 after the turn, and the turtle moved north, which is scene y decreasing.
        Assert.Equal(90, turtle.heading, 3);
        Assert.True(turtle.y < 96, "the turtle moved north from the centre");
    }

    [Fact]
    public async Task HT_hides_the_turtle_and_ST_shows_it_again()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();

        await MachineHarness.RunAsync(machine, "HT");
        Assert.False(ParseTurtle(browser.LastTurtle).visible);

        await MachineHarness.RunAsync(machine, "ST");
        Assert.True(ParseTurtle(browser.LastTurtle).visible);
    }

    [Fact]
    public async Task The_turtle_picture_is_sent_to_the_canvas()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "FD 100");

        Assert.Equal(4, browser.LastTextRows);
        Assert.Contains("segment", browser.LastScene);
    }

    // ---- the listener ---------------------------------------------------------

    [Fact]
    public async Task Typed_text_appears_after_the_prompt()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        MachineHarness.Type(machine, "FD 100");

        Assert.Contains("> FD 100", MachineHarness.ScreenText(machine));
        (int row, int col) = machine.CaretPosition();
        Assert.Equal(8, col);
    }

    [Fact]
    public async Task Enter_runs_a_command_and_shows_its_output()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "PRINT \"HELLO");

        Assert.Contains("HELLO", MachineHarness.ScreenText(machine));
    }

    [Fact]
    public async Task A_reporter_typed_at_the_listener_shows_its_value()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "FIRST \"LOGO");

        Assert.Contains("L", MachineHarness.ScreenText(machine));
    }

    [Fact]
    public async Task A_command_that_draws_leaves_no_stray_value_on_the_screen()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "FD 100");

        Assert.DoesNotContain("NONE", MachineHarness.ScreenText(machine));
    }

    [Fact]
    public async Task Editing_keys_move_and_delete_within_the_line()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        MachineHarness.Type(machine, "FDD");     // a mistyped command
        machine.HandleKey("LEFT");               // back over the bad D
        machine.HandleKey("DEL");                // remove it
        machine.HandleKey("END");                // go to the end
        MachineHarness.Type(machine, "10");      // and finish the line

        Assert.Contains("> FD10", MachineHarness.ScreenText(machine));
    }

    [Fact]
    public async Task Backspace_deletes_the_character_before_the_caret()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        MachineHarness.Type(machine, "PRINTX");
        machine.HandleKey("BKSP");

        Assert.Contains("> PRINT", MachineHarness.ScreenText(machine));
    }

    [Fact]
    public async Task Arrow_up_recalls_previous_commands_in_order()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "PRINT 1");
        await MachineHarness.RunAsync(machine, "PRINT 2");

        machine.HandleKey("UP");
        Assert.Contains("PRINT 2", MachineHarness.ScreenText(machine));

        machine.HandleKey("UP");
        Assert.Contains("PRINT 1", MachineHarness.ScreenText(machine));

        machine.HandleKey("DOWN");
        machine.HandleKey("DOWN");

        // Back at a bare prompt with nothing typed.
        Assert.Equal(2, machine.CaretPosition().Col);
        Assert.StartsWith(">", MachineHarness.ScreenText(machine).Split('\n')[^1]);
    }

    [Fact]
    public async Task A_line_longer_than_the_screen_wraps_rather_than_disappearing()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        MachineHarness.Type(machine, "PRINT [AAAAAAAAAA BBBBBBBBBB CCCCCCCCCC DDDDDDDDDD]");

        string withoutSpaces = string.Concat(machine.Screen.Snapshot()).Replace(" ", "");
        Assert.Contains("AAAAAAAAAABBBBBBBBBBCCCCCCCCCCDDDDDDDDDD", withoutSpaces);
    }

    [Fact]
    public async Task Output_from_a_wrapped_command_never_overwrites_the_command_itself()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();

        // Typed across more than 40 columns, but its result fits on one row, so both the command
        // and its output are on the screen and can be checked against each other.
        await MachineHarness.RunAsync(machine, "MAKE \"L [AAAAAAAAAA BBBBBBBBBB CCCCCCCCCC] PRINT :L");

        string text = MachineHarness.ScreenText(machine);
        string printed = "[AAAAAAAAAA BBBBBBBBBB CCCCCCCCCC]";
        Assert.Contains(printed, text);

        // The row carrying the printed result must not also carry the prompt: that would mean the
        // output was written on top of the line the child was typing.
        string[] outputRows = machine.Screen.Snapshot().Where(r => r.Contains(printed)).ToArray();
        Assert.NotEmpty(outputRows);
        Assert.All(outputRows, r => Assert.DoesNotContain(">", r));

        await MachineHarness.RunAsync(machine, "PRINT 7");
        Assert.Contains("7", text = MachineHarness.ScreenText(machine));
        Assert.Equal(2, machine.CaretPosition().Col);   // a fresh prompt is waiting
    }

    [Fact]
    public async Task Output_wider_than_the_screen_wraps_at_the_edge_rather_than_being_cut_off()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "PRINT [AAAAAAAAAA BBBBBBBBBB CCCCCCCCCC DDDDDDDDDD]");

        string joined = string.Concat(machine.Screen.Snapshot());
        Assert.Contains("AAAAAAAAAABBBBBBBBBB", joined.Replace(" ", ""));
        Assert.Contains("DDDDDDDDDD", joined);   // the tail is still on screen, not lost
    }

    [Fact]
    public async Task An_unknown_command_is_answered_with_a_suggestion_and_the_listener_survives()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "FORWRD 10");

        string text = MachineHarness.ScreenText(machine);
        Assert.Contains("FORWRD", text);
        Assert.Contains("FORWARD", text);

        await MachineHarness.RunAsync(machine, "PRINT \"STILLWORKS");
        Assert.Contains("STILLWORKS", MachineHarness.ScreenText(machine));
    }

    // ---- global keys ----------------------------------------------------------

    [Theory]
    [InlineData("A-M", "MODE")]
    [InlineData("A-G", "amber")]
    [InlineData("A-T", "TEXT")]
    public async Task Global_keys_change_state_without_typing_anything(string token, string expected)
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.HandleKey(token);

        Assert.Contains(expected, MachineHarness.ScreenText(machine), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Alt_g_switches_the_phosphor_and_reaches_the_browser()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-G");

        Assert.Equal("amber", machine.Theme);
        Assert.Equal("amber", browser.Theme);
    }

    [Fact]
    public async Task Alt_c_toggles_crt_emulation()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-C");
        Assert.True(browser.Crt);
    }

    [Fact]
    public async Task Alt_t_toggles_between_the_split_screen_and_full_text()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        Assert.Equal(4, machine.Engine.LowerLines);

        machine.HandleKey("A-T");
        Assert.Equal(0, machine.Screen.WindowTop);

        machine.HandleKey("A-T");
        Assert.Equal(TextScreen.Rows - 4, machine.Screen.WindowTop);
    }

    [Fact]
    public async Task Alt_s_saves_the_workspace_into_the_store()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "MAKE \"SIZE 42");
        machine.HandleKey("A-S");
        await Task.Delay(50);

        SessionStore.StoreFile? file = SessionStore.Deserialize(browser.StoreJson);
        Assert.NotNull(file);
        LogoSession session = Assert.Single(file!.Sessions);
        Assert.Contains(session.Globals, g => g.Name == "SIZE" && g.Text == "42");
    }

    [Fact]
    public async Task Alt_d_downloads_the_session_as_real_logo_source()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "TO SQUARE :S REPEAT 4 [FD :S RT 90] END");
        await MachineHarness.RunAsync(machine, "SQUARE 50");

        machine.HandleKey("A-D");
        await Task.Delay(50);

        (string name, string content) = Assert.Single(browser.Downloads);
        Assert.Equal("SESSION1.logo", name);
        Assert.Contains("TO SQUARE", content);
        Assert.Contains("END", content);
    }

    [Fact]
    public async Task Alt_o_loads_a_picked_file_and_runs_it()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        browser.PickResult = "PRINT 555";

        machine.HandleKey("A-O");
        await MachineHarness.SettleAsync(machine);

        Assert.Contains("555", MachineHarness.ScreenText(machine));
    }

    // ---- overlays must hand the screen back ------------------------------------

    [Fact]
    public async Task The_quick_reference_opens_and_closes_without_losing_the_transcript()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "PRINT \"BEFOREHELP");
        string before = MachineHarness.ScreenText(machine);

        machine.HandleKey("A-?");
        Assert.Equal(AppScreen.Help, machine.ScreenKind);
        Assert.Contains("QUICK REFERENCE", MachineHarness.ScreenText(machine));

        machine.HandleKey("ESC");
        Assert.Equal(AppScreen.Listener, machine.ScreenKind);
        Assert.Equal(before, MachineHarness.ScreenText(machine));
    }

    [Theory]
    [InlineData("A-?")]
    [InlineData("A-Q")]
    [InlineData("C-SLASH")]
    public async Task Every_help_shortcut_opens_the_reference(string token)
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.HandleKey(token);
        Assert.Equal(AppScreen.Help, machine.ScreenKind);
    }

    [Fact]
    public async Task The_reference_pages_with_the_arrows_and_shows_where_it_is()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-?");

        string Footer() => machine.Screen.Snapshot()[TextScreen.Rows - 1].Trim();

        Assert.StartsWith("1-", Footer());
        machine.HandleKey("DOWN");
        Assert.StartsWith("2-", Footer());
        machine.HandleKey("PGDN");
        machine.HandleKey("END");
        Assert.Contains("OF", Footer());
    }

    [Fact]
    public async Task The_reference_is_wide_enough_for_no_line_to_be_cut_off_mid_word()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-?");

        // QuickReference wraps to 40 columns itself; the grid must not be adding its own truncation.
        Assert.All(machine.Screen.Snapshot(), row => Assert.True(row.Length <= TextScreen.Cols));
    }

    [Fact]
    public async Task The_info_dashboard_describes_the_workspace_and_closes()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "TO SQUARE :S REPEAT 4 [FD :S RT 90] END");
        await MachineHarness.RunAsync(machine, "MAKE \"SIZE 50");

        machine.HandleKey("A-I");
        string text = MachineHarness.ScreenText(machine);
        Assert.Contains("FILE: SESSION1.logo", text);
        Assert.Contains("PROCEDURES: 1", text);
        Assert.Contains("VARIABLES: 1", text);

        machine.HandleKey("ESC");
        Assert.Equal(AppScreen.Listener, machine.ScreenKind);
    }

    [Fact]
    public async Task Typing_while_an_overlay_is_open_does_not_leak_into_the_hidden_line()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        MachineHarness.Type(machine, "PRINT ");
        machine.HandleKey("A-?");
        MachineHarness.Type(machine, "XYZ");

        machine.HandleKey("ESC");
        Assert.Contains("> PRINT", MachineHarness.ScreenText(machine));
        Assert.DoesNotContain("XYZ", MachineHarness.ScreenText(machine));
    }

    // ---- sessions ---------------------------------------------------------------

    [Fact]
    public async Task The_session_manager_lists_names_and_N_makes_a_new_one()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-L");
        Assert.Equal(AppScreen.Sessions, machine.ScreenKind);
        Assert.Contains("SESSION1.logo", MachineHarness.ScreenText(machine));

        machine.HandleKey("N");
        Assert.Equal("SESSION2.logo", machine.CurrentName);
    }

    [Fact]
    public async Task Deleting_a_session_never_gives_its_number_back()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();

        machine.HandleKey("A-L");
        machine.HandleKey("N");                       // SESSION2 is made; the manager closes
        Assert.Equal("SESSION2.logo", machine.CurrentName);

        machine.HandleKey("A-L");                     // reopen, with SESSION2 highlighted
        machine.HandleKey("D");                       // delete it
        Assert.Equal("SESSION1.logo", machine.CurrentName);

        machine.HandleKey("N");                       // the next one must be SESSION3
        Assert.Equal("SESSION3.logo", machine.CurrentName);
    }

    [Fact]
    public async Task A_session_number_survives_a_reload_after_its_session_is_deleted()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-L");
        machine.HandleKey("N");
        machine.HandleKey("A-L");
        machine.HandleKey("D");
        await machine.SaveAsync();

        SessionStore.StoreFile? saved = SessionStore.Deserialize(browser.StoreJson);
        Assert.NotNull(saved);
        Assert.True(saved!.LastIssuedSessionNumber >= 2);

        // A fresh visit reads that number back off disk, so SESSION2 stays gone.
        (LogoMachine second, _) = await MachineHarness.AtListenerAsync(browser.StoreJson);
        second.HandleKey("A-L");
        second.HandleKey("N");
        Assert.Equal("SESSION3.logo", second.CurrentName);
    }

    [Fact]
    public async Task Opening_a_session_from_the_manager_loads_its_work()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "TO SQUARE :S REPEAT 4 [FD :S RT 90] END");
        await MachineHarness.RunAsync(machine, "SQUARE 50");
        await machine.SaveAsync();

        (LogoMachine second, _) = await MachineHarness.StartAsync(SessionStore.Serialize(machine.Store));
        second.HandleKey("X");
        Assert.True(second.Engine.Workspace.HasProcedure("SQUARE"));
        Assert.Equal(4, second.Engine.Scene.Items.Count);
    }

    [Fact]
    public async Task A_session_round_trips_through_the_store_with_its_procedures_running()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "TO HI :N PRINT :N END");
        await MachineHarness.RunAsync(machine, "HI 7");
        await machine.SaveAsync();

        (LogoMachine restored, _) = await MachineHarness.StartAsync(browser.StoreJson);
        restored.HandleKey("X");
        await MachineHarness.RunAsync(restored, "HI 99");

        Assert.Contains("99", MachineHarness.ScreenText(restored));
    }

    [Fact]
    public async Task The_store_refuses_a_format_this_build_cannot_understand()
    {
        string future = "{\"version\":99,\"sessions\":[],\"currentName\":\"\"}";
        (LogoMachine machine, _) = await MachineHarness.StartAsync(future);

        // Rather than crash or show half-parsed data, it starts clean.
        Assert.Equal(AppScreen.Splash, machine.ScreenKind);
        Assert.Equal("SESSION1.logo", machine.CurrentName);
    }

    [Fact]
    public async Task Work_is_saved_automatically_after_each_command()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "MAKE \"ANSWER 42");

        SessionStore.StoreFile? file = SessionStore.Deserialize(browser.StoreJson);
        Assert.Contains(file!.Sessions[0].Globals, g => g.Name == "ANSWER");
    }

    // ---- the editor -------------------------------------------------------------

    [Fact]
    public async Task The_editor_is_locked_out_of_easy_mode()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-E");

        Assert.Equal(AppScreen.Listener, machine.ScreenKind);
        Assert.Contains("ADVANCED MODE", MachineHarness.ScreenText(machine));
    }

    [Fact]
    public async Task The_editor_types_edits_and_runs_the_session_source()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-M");
        machine.HandleKey("A-E");
        Assert.Equal(AppScreen.Editor, machine.ScreenKind);

        MachineHarness.Type(machine, "PRINT 99");
        machine.HandleKey("A-R");
        await MachineHarness.SettleAsync(machine);

        Assert.Contains("99", MachineHarness.ScreenText(machine));

        SessionStore.StoreFile? file = SessionStore.Deserialize(browser.StoreJson);
        Assert.Contains("PRINT 99", file!.Sessions[0].Source);
    }

    [Fact]
    public async Task Esc_in_the_editor_saves_without_running()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-M");
        machine.HandleKey("A-E");
        MachineHarness.Type(machine, "PRINT 12345");
        machine.HandleKey("ESC");
        await Task.Delay(50);

        Assert.Equal(AppScreen.Listener, machine.ScreenKind);
        Assert.DoesNotContain("12345", MachineHarness.ScreenText(machine));

        SessionStore.StoreFile? file = SessionStore.Deserialize(browser.StoreJson);
        Assert.Contains("PRINT 12345", file!.Sessions[0].Source);
    }

    [Fact]
    public async Task The_editor_moves_the_caret_and_adds_lines()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-M");
        machine.HandleKey("A-E");

        MachineHarness.Type(machine, "FD 10");
        machine.HandleKey("HOME");
        MachineHarness.Type(machine, "REPEAT 4 [");
        machine.HandleKey("END");
        machine.HandleKey("ENTER");
        MachineHarness.Type(machine, "RT 90");

        (int row, int _) = machine.EditorCaret;
        Assert.Equal(1, row);
    }

    [Fact]
    public async Task The_editor_scrolls_past_the_bottom_of_the_screen()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-M");
        machine.HandleKey("A-E");

        for (int line = 0; line < 30; line++)
        {
            MachineHarness.Type(machine, $"PRINT {line}");
            if (line < 29) machine.HandleKey("ENTER");
        }

        for (int i = 0; i < 40; i++) machine.HandleKey("DOWN");

        string text = MachineHarness.ScreenText(machine);
        Assert.Contains("PRINT 29", text);
        Assert.DoesNotContain("PRINT 0", text);   // scrolled off the top of the screen
    }

    // ---- running programs -------------------------------------------------------

    [Fact]
    public async Task A_program_that_asks_for_a_number_takes_the_next_line_typed()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        MachineHarness.Type(machine, "MAKE \"X READ PRINT :X");
        machine.HandleKey("ENTER");

        for (int i = 0; i < 200 && !machine.AwaitingRead; i++) await Task.Delay(5);
        Assert.True(machine.AwaitingRead);

        MachineHarness.Type(machine, "42");
        machine.HandleKey("ENTER");
        await MachineHarness.SettleAsync(machine);

        Assert.Contains("42", MachineHarness.ScreenText(machine));
    }

    [Fact]
    public async Task Esc_breaks_a_running_loop_instead_of_hanging_the_page()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        MachineHarness.Submit(machine, "REPEAT 20000 [FD 1]");

        // Pump until the program is genuinely mid-flight, then press Esc while it still runs.
        for (int i = 0; i < 50 && !machine.IsBusy; i++) await MachineHarness.PumpAsync();
        Assert.True(machine.IsBusy);

        machine.HandleKey("ESC");
        await MachineHarness.SettleAsync(machine);

        Assert.False(machine.IsBusy);
        Assert.Contains("BREAK", MachineHarness.ScreenText(machine));
    }

    [Fact]
    public async Task A_long_program_yields_to_the_browser_so_the_page_can_paint()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "REPEAT 30000 [FD 1 RT 1]");

        // StepSlice is 3000, so a 30,000-step drawing must hand the browser back several times.
        Assert.True(browser.YieldCount >= 2, $"expected several yields, got {browser.YieldCount}");
    }

    [Fact]
    public async Task A_runaway_loop_reports_rather_than_freezing()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "WHILE [EQUAL 1 1] [PRINT 1]");

        Assert.False(machine.IsBusy);

        // The message is wrapped to 40 columns, so check a phrase that cannot be split across rows.
        Assert.Contains("keeps calling", MachineHarness.ScreenText(machine), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Keys_typed_while_a_program_runs_are_ignored_except_break()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        MachineHarness.Submit(machine, "REPEAT 20000 [FD 1]");

        for (int i = 0; i < 50 && !machine.IsBusy; i++) await MachineHarness.PumpAsync();
        Assert.True(machine.IsBusy);

        MachineHarness.Type(machine, "CLS");
        Assert.True(machine.IsBusy);   // typing did not stop or feed the program

        machine.HandleKey("ESC");
        await MachineHarness.SettleAsync(machine);
        Assert.DoesNotContain("CLS", MachineHarness.ScreenText(machine));
    }

    [Fact]
    public async Task Bye_saves_and_returns_to_the_splash()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "MAKE \"SIZE 50");
        await MachineHarness.RunAsync(machine, "BYE");

        Assert.Equal(AppScreen.Splash, machine.ScreenKind);

        SessionStore.StoreFile? file = SessionStore.Deserialize(browser.StoreJson);
        Assert.Contains(file!.Sessions[0].Globals, g => g.Name == "SIZE");
    }

    [Fact]
    public async Task A_program_defined_at_the_listener_stays_available()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "TO POLY :N :S REPEAT :N [FD :S RT 360 / :N] END");
        await MachineHarness.RunAsync(machine, "POLY 6 40");

        Assert.True(machine.Engine.Workspace.HasProcedure("POLY"));
        Assert.Equal(6, machine.Engine.Scene.Items.Count);
    }
}
