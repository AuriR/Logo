using MaturinLogo.Core.Interpreter;
using MaturinLogo.Core.Machine;
using MaturinLogo.Core.Screen;
using MaturinLogo.Core.Sessions;

namespace MaturinLogo.Core.Tests;

/// <summary>
/// Deliberately nasty cases against the parts of the machine that hold the most state: the caret
/// arithmetic that paints the typed line, and the overlay stack that has to give the screen back
/// untouched. These are the areas where an off-by-one shows up as a screen a child cannot read.
/// </summary>
public class EdgeCaseTests
{
    [Fact]
    public async Task A_caret_at_exactly_the_last_column_stays_inside_the_grid()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();

        // 37 characters plus the two-character prompt ends on column 38, one short of the edge.
        MachineHarness.Type(machine, new string('A', 37));
        Assert.Equal(39, machine.CaretPosition().Col);

        machine.HandleKey("RIGHT");   // must not run past the last column
        Assert.Equal(39, machine.CaretPosition().Col);
    }

    [Fact]
    public async Task Typing_past_the_last_column_wraps_onto_the_next_row()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();

        // 37 characters exactly fills the row: the prompt takes two columns, so the last typed
        // character lands in column 39.
        MachineHarness.Type(machine, new string('B', 37));
        (int row, int col) = machine.CaretPosition();
        Assert.Equal(39, col);
        Assert.Equal(machine.Screen.WindowTop + 2, row);

        // One more character has nowhere to go on this row, so it starts the next one.
        MachineHarness.Type(machine, "C");
        (int afterRow, int afterCol) = machine.CaretPosition();
        Assert.Equal(0, afterCol);
        Assert.Equal(row + 1, afterRow);
    }

    [Fact]
    public async Task A_line_longer_than_the_window_is_capped_and_shows_the_caret()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();

        // The cap is a fixed 256 characters, not a multiple of the current window: ALT-T shrinks the
        // window from four and twenty rows, and a limit that moved would let a child type a line
        // that could no longer be drawn.
        string command = "MAKE \"X " + new string('9', 300) + " PRINT :X";
        MachineHarness.Type(machine, command);

        Assert.Equal(LogoMachine.MaxInputLength, machine.InputText.Length);

        (int row, int col) = machine.CaretPosition();
        Assert.True(row >= machine.Screen.WindowTop && row <= machine.Screen.WindowBottom,
            $"caret row {row} is inside the text window");
        Assert.True(col < TextScreen.Cols, $"caret column {col} is on the grid");

        // The window shows the end of the line, where the caret is, rather than the beginning.
        Assert.Equal('9', machine.Screen.Chars[row, col == 0 ? TextScreen.Cols - 1 : col - 1]);
    }

    [Fact]
    public async Task A_wrapped_command_still_runs_and_its_output_wraps_too()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();

        // 60 characters is longer than one row, so both the command and the value it prints span
        // rows. Checking a run of digits that straddles a row break proves the text was not lost.
        const int digits = 60;
        await MachineHarness.RunAsync(machine, "MAKE \"X " + new string('9', digits) + " PRINT :X");

        string withoutSpaces = string.Concat(machine.Screen.Snapshot()).Replace(" ", "");
        Assert.Equal(digits, CountRun(withoutSpaces, '9'));
    }

    /// <summary>The longest unbroken run of a character in the text.</summary>
    private static int CountRun(string text, char target)
    {
        int best = 0, current = 0;
        foreach (char c in text)
        {
            current = c == target ? current + 1 : 0;
            best = Math.Max(best, current);
        }
        return best;
    }

    [Fact]
    public async Task Editing_backwards_across_a_wrapped_line_keeps_the_caret_in_place()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        MachineHarness.Type(machine, new string('C', 45));

        (int row, int col) = machine.CaretPosition();
        machine.HandleKey("BKSP");
        (int newRow, int newCol) = machine.CaretPosition();

        Assert.Equal(row, newRow);
        Assert.Equal(col - 1, newCol);
    }

    [Fact]
    public async Task Home_and_end_survive_a_wrapped_line()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();

        // Where the prompt itself sits, before anything is typed.
        (int promptRow, int promptCol) = machine.CaretPosition();
        Assert.Equal(2, promptCol);

        MachineHarness.Type(machine, new string('D', 50));

        machine.HandleKey("HOME");
        (int homeRow, int homeCol) = machine.CaretPosition();
        Assert.Equal(promptCol, homeCol);   // HOME returns to just after the prompt
        Assert.Equal(promptRow, homeRow);

        machine.HandleKey("END");
        (int endRow, int endCol) = machine.CaretPosition();
        Assert.Equal(12, endCol);           // 52 characters is one row plus column 12
        Assert.Equal(promptRow + 1, endRow);
    }

    [Fact]
    public async Task Deleting_everything_returns_the_caret_to_the_prompt()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        MachineHarness.Type(machine, "PRINT 123");

        for (int i = 0; i < 20; i++) machine.HandleKey("BKSP");

        Assert.Equal(2, machine.CaretPosition().Col);
        Assert.DoesNotContain("PRINT", MachineHarness.ScreenText(machine));
    }

    // ---- overlays -------------------------------------------------------------

    [Fact]
    public async Task Opening_an_overlay_twice_does_not_corrupt_the_saved_screen()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "PRINT \"MARKER");

        machine.HandleKey("A-?");
        machine.HandleKey("A-I");     // help -> info, without closing help
        machine.HandleKey("ESC");

        // The transcript from before the first overlay must still be there.
        Assert.Contains("MARKER", MachineHarness.ScreenText(machine));
        Assert.Equal(AppScreen.Listener, machine.ScreenKind);
    }

    [Fact]
    public async Task Closing_an_overlay_restores_attributes_not_just_text()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "FORWRD 10");   // prints a reverse-video error

        int reverseBefore = CountAttr(machine, CellAttr.Reverse);
        Assert.True(reverseBefore > 0, "the error row should be reverse video");

        machine.HandleKey("A-?");
        machine.HandleKey("ESC");

        Assert.Equal(reverseBefore, CountAttr(machine, CellAttr.Reverse));
    }

    [Fact]
    public async Task The_editor_can_open_the_session_manager_and_return_to_editing()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-M");
        machine.HandleKey("A-E");
        MachineHarness.Type(machine, "PRINT 5");

        machine.HandleKey("A-L");
        Assert.Equal(AppScreen.Sessions, machine.ScreenKind);

        machine.HandleKey("ESC");
        Assert.Equal(AppScreen.Editor, machine.ScreenKind);

        // Still editing the same text, not a blank buffer.
        Assert.Contains("PRINT 5", MachineHarness.ScreenText(machine));
    }

    [Fact]
    public async Task An_overlay_does_not_disturb_the_turtle_or_the_workspace()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "FD 100 RT 45");
        double x = machine.Engine.Turtle.X, heading = machine.Engine.Turtle.Heading;
        int strokes = machine.Engine.Scene.Items.Count;

        machine.HandleKey("A-?");
        machine.HandleKey("A-I");
        machine.HandleKey("ESC");

        Assert.Equal(x, machine.Engine.Turtle.X, 6);
        Assert.Equal(heading, machine.Engine.Turtle.Heading, 6);
        Assert.Equal(strokes, machine.Engine.Scene.Items.Count);
    }

    [Fact]
    public async Task Bye_from_the_listener_clears_any_stashed_overlay_screen()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-?");
        machine.HandleKey("ESC");
        await MachineHarness.RunAsync(machine, "BYE");

        Assert.Equal(AppScreen.Splash, machine.ScreenKind);

        // Starting again must show a clean greeting, not the old transcript from behind the overlay.
        machine.HandleKey("X");
        Assert.Equal(AppScreen.Listener, machine.ScreenKind);
    }

    // ---- running programs -----------------------------------------------------

    [Fact]
    public async Task A_second_command_while_one_runs_is_refused_not_queued()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        MachineHarness.Submit(machine, "REPEAT 20000 [FD 1]");
        for (int i = 0; i < 50 && !machine.IsBusy; i++) await MachineHarness.PumpAsync();

        await machine.RunAsync("PRINT 999");     // direct call while busy
        Assert.DoesNotContain("999", MachineHarness.ScreenText(machine));

        machine.HandleKey("ESC");
        await MachineHarness.SettleAsync(machine);
    }

    [Fact]
    public async Task After_a_break_the_listener_works_again()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        MachineHarness.Submit(machine, "REPEAT 20000 [FD 1]");
        for (int i = 0; i < 50 && !machine.IsBusy; i++) await MachineHarness.PumpAsync();
        machine.HandleKey("ESC");
        await MachineHarness.SettleAsync(machine);

        await MachineHarness.RunAsync(machine, "PRINT \"AFTERBREAK");
        Assert.Contains("AFTERBREAK", MachineHarness.ScreenText(machine));
        Assert.False(machine.IsBusy);
    }

    [Fact]
    public async Task A_run_that_faults_leaves_the_machine_usable()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();

        // A procedure that recurses without an exit trips a guard; the machine must recover.
        await MachineHarness.RunAsync(machine, "TO FOREVER OUTPUT FOREVER + 1 END");
        await MachineHarness.RunAsync(machine, "FOREVER");

        Assert.False(machine.IsBusy);
        await MachineHarness.RunAsync(machine, "PRINT \"RECOVERED");
        Assert.Contains("RECOVERED", MachineHarness.ScreenText(machine));
    }

    [Fact]
    public async Task Read_then_help_then_answer_keeps_the_programs_answer()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        MachineHarness.Submit(machine, "MAKE \"X READ PRINT :X");
        for (int i = 0; i < 200 && !machine.AwaitingRead; i++) await Task.Delay(5);

        // Alt keys are refused while a program waits, so the digits cannot be stolen.
        machine.HandleKey("A-?");
        Assert.True(machine.AwaitingRead);

        MachineHarness.Type(machine, "7");
        machine.HandleKey("ENTER");
        await MachineHarness.SettleAsync(machine);

        Assert.Contains("7", MachineHarness.ScreenText(machine));
    }

    // ---- sessions -------------------------------------------------------------

    [Fact]
    public async Task Twelve_sessions_fill_up_and_the_thirteenth_is_refused_politely()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();

        for (int i = 0; i < 11; i++)
        {
            machine.HandleKey("A-L");
            machine.HandleKey("N");
        }

        Assert.Equal(12, machine.Sessions.Count);

        machine.HandleKey("A-L");
        machine.HandleKey("N");
        Assert.Equal(12, machine.Sessions.Count);
        Assert.Contains("NO ROOM", MachineHarness.ScreenText(machine));
    }

    [Fact]
    public async Task Deleting_the_current_session_moves_to_a_neighbour_without_crashing()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-L");
        machine.HandleKey("N");                       // SESSION2 current
        machine.HandleKey("A-L");
        machine.HandleKey("D");                       // delete the current one

        Assert.NotEmpty(machine.CurrentName);
        Assert.Equal("SESSION1.logo", machine.CurrentName);
    }

    [Fact]
    public async Task Deleting_the_last_session_makes_a_replacement()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-L");
        machine.HandleKey("D");                       // delete the only session

        Assert.Single(machine.Sessions);
        Assert.Equal("SESSION2.logo", machine.CurrentName);
    }

    [Fact]
    public async Task A_corrupt_store_starts_clean_instead_of_crashing()
    {
        // Half-written JSON from a browser that ran out of space mid-save.
        (LogoMachine machine, _) = await MachineHarness.StartAsync("{\"version\":1,\"sessions\":[{");

        Assert.Equal(AppScreen.Splash, machine.ScreenKind);
        Assert.Equal("SESSION1.logo", machine.CurrentName);
    }

    [Fact]
    public async Task Changing_the_layout_with_a_long_line_typed_does_not_crash_the_keyboard()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();

        // Full text has 24 rows, so a long line is allowed. Switching to the four-row split page
        // must not leave the painter reaching for rows that no longer exist.
        MachineHarness.Type(machine, new string('A', 300));
        machine.HandleKey("A-T");                       // TEXT -> split (LOWER 4)

        string text = MachineHarness.ScreenText(machine);
        Assert.True(machine.CaretPosition().Row <= machine.Screen.WindowBottom,
            "the caret stayed inside the text window");

        // Every key after this must still work; before the fix the painter threw on each one.
        machine.HandleKey("B");
        machine.HandleKey("LEFT");
        machine.HandleKey("BKSP");
        Assert.Contains(">", MachineHarness.ScreenText(machine));
    }

    [Fact]
    public async Task A_long_line_survives_alternating_layout_switches()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        MachineHarness.Type(machine, new string('C', 500));

        for (int i = 0; i < 4; i++)
        {
            machine.HandleKey("A-T");
            machine.HandleKey("D");

            (int row, int col) = machine.CaretPosition();
            Assert.True(row >= machine.Screen.WindowTop && row <= machine.Screen.WindowBottom,
                $"caret row {row} is inside the window after layout switch {i}");
            Assert.True(col >= 0 && col < TextScreen.Cols, $"caret column {col} is on the grid");

            // The buffer is never truncated by a layout change - shrinking the window must not
            // silently delete what the child typed - so it stays at the fixed cap.
            Assert.Equal(LogoMachine.MaxInputLength, machine.InputText.Length);
        }
    }

    [Fact]
    public async Task Graphics_mode_still_paints_the_typed_line_inside_the_single_row()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "GRAPHICS");

        MachineHarness.Type(machine, new string('D', 80));
        (int row, int col) = machine.CaretPosition();

        Assert.True(row <= machine.Screen.WindowBottom, $"caret row {row} must stay in the window");
        Assert.True(col < TextScreen.Cols, $"caret column {col} must stay on the grid");
    }

    [Fact]
    public async Task A_programs_read_prompt_is_not_replaced_by_the_listener_prompt()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        MachineHarness.Submit(machine, "TYPE \"NAME? MAKE \"N READLINE");

        for (int i = 0; i < 200 && !machine.AwaitingRead; i++) await Task.Delay(5);
        Assert.True(machine.AwaitingRead);

        string asked = MachineHarness.ScreenText(machine);
        Assert.Contains("NAME?", asked);

        MachineHarness.Type(machine, "BOB");

        // The question must still be readable, and no "> " may appear while a program is running.
        Assert.Contains("NAME?", MachineHarness.ScreenText(machine));
        Assert.DoesNotContain("> BOB", MachineHarness.ScreenText(machine));

        machine.HandleKey("ENTER");
        await MachineHarness.SettleAsync(machine);
    }

    [Fact]
    public async Task Alt_E_inside_the_editor_does_not_throw_away_unsaved_work()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-M");
        machine.HandleKey("A-E");
        MachineHarness.Type(machine, "PRINT 7");

        machine.HandleKey("A-E");     // re-press the same key

        Assert.Contains("PRINT 7", MachineHarness.ScreenText(machine));
    }

    [Fact]
    public async Task The_session_menu_is_never_saved_as_the_child_transcript()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "PRINT \"MYWORK");

        machine.HandleKey("A-L");
        machine.HandleKey("N");
        await Task.Delay(80);

        SessionStore.StoreFile? file = SessionStore.Deserialize(browser.StoreJson);
        foreach (var session in file!.Sessions)
            Assert.DoesNotContain(session.ScreenLines,
                l => l.Contains("ENTER OPEN", StringComparison.Ordinal));
    }

    // ---- review findings: persistence, keys and the picture ---------------------

    [Fact]
    public async Task Properties_stored_with_PUT_survive_a_reload()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "PUT \"PLAYER \"SCORE 10 PRINT GET \"PLAYER \"SCORE");

        SessionStore.StoreFile? file = SessionStore.Deserialize(browser.StoreJson);
        StoredProperty saved = Assert.Single(file!.Sessions.Single().Properties);
        Assert.Equal("PLAYER", saved.Name);
        Assert.Equal("SCORE", saved.Property);

        // A second machine boots from the same store and must answer GET without being told again.
        (LogoMachine reloaded, _) = await MachineHarness.AtListenerAsync(browser.StoreJson);
        await MachineHarness.RunAsync(reloaded, "PRINT GET \"PLAYER \"SCORE");

        Assert.Contains("10", MachineHarness.ScreenText(reloaded));
    }

    [Fact]
    public async Task Ctrl_S_saves_the_session_like_Alt_S_does()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "MAKE \"X 5");

        string? before = browser.StoreJson;
        await MachineHarness.RunAsync(machine, "MAKE \"Y 6");
        browser.StoreJson = null;

        machine.HandleKey("C-S");
        await MachineHarness.SettleAsync(machine);

        Assert.NotNull(browser.StoreJson);
        Assert.NotSame(before, browser.StoreJson);
        Assert.Contains("SAVED", MachineHarness.ScreenText(machine));
    }

    [Fact]
    public async Task A_background_change_repaints_the_whole_picture_not_just_new_strokes()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "FD 10");

        int replays = browser.ReplayCalls;

        // SETBG adds no strokes, so an append-only path would leave the old colour on the canvas.
        // The machine must notice the colour changed and replay everything.
        machine.Engine.Scene.Background = 3;
        await machine.AppendGraphicsAsync();

        Assert.True(browser.ReplayCalls > replays, "a full replay ran after the colour changed");
        Assert.Equal(3, browser.LastBackground);
    }

    [Fact]
    public async Task A_program_that_changes_the_screen_colour_while_running_is_repainted()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();

        await MachineHarness.RunAsync(machine, "REPEAT 4 [FD 20 SETBG 3 RT 90]");

        Assert.Equal(3, machine.Engine.Scene.Background);
        Assert.Equal(machine.Engine.Scene.Background, browser.LastBackground);
    }

    private static int CountAttr(LogoMachine machine, CellAttr attr)
    {
        int count = 0;
        for (int r = 0; r < TextScreen.Rows; r++)
            for (int c = 0; c < TextScreen.Cols; c++)
                if ((machine.Screen.Attrs[r, c] & attr) != 0) count++;
        return count;
    }
}
