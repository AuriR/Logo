using MaturinLogo.Core.Machine;
using MaturinLogo.Core.Screen;
using MaturinLogo.Core.Sessions;

namespace MaturinLogo.Core.Tests;

/// <summary>
/// Repros for the second adversarial review. Every test here was reported by the reviewer with a
/// concrete key sequence and reproduced against the real machine; each asserts on the exact grid
/// geometry the reviewer measured, not on a message.
/// </summary>
public class PainterReviewTests
{
    /// <summary>Every cell on the grid that is not blank, as "row:col:char".</summary>
    private static List<string> FilledCells(LogoMachine machine)
    {
        var found = new List<string>();
        for (int r = 0; r < TextScreen.Rows; r++)
            for (int c = 0; c < TextScreen.Cols; c++)
                if (machine.Screen.Chars[r, c] != ' ')
                    found.Add($"{r}:{c}:{machine.Screen.Chars[r, c]}");
        return found;
    }

    /// <summary>Rows the draft still occupies: any row holding text that is not the prompt.</summary>
    private static int RowsHolding(LogoMachine machine, string text)
    {
        int hits = 0;
        foreach (string row in machine.Screen.Snapshot())
            if (row.Contains(text, StringComparison.Ordinal)) hits++;
        return hits;
    }

    // ---- Issue 1: an Alt key duplicates the draft --------------------------------

    [Fact]
    public async Task An_Alt_key_that_prints_does_not_leave_a_second_copy_of_the_draft()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();

        MachineHarness.Type(machine, "PRINT 5");
        machine.HandleKey("A-C");                   // CRT on, which prints a line

        // The draft must exist in exactly one place, not on the prompt row and again below.
        Assert.Equal(1, RowsHolding(machine, "PRINT 5"));

        // And it must still be editable: deleting everything leaves no copy anywhere.
        for (int i = 0; i < 20; i++) machine.HandleKey("BKSP");
        Assert.Equal("", machine.InputText);
        Assert.Equal(0, RowsHolding(machine, "PRINT 5"));
    }

    [Fact]
    public async Task Editing_the_draft_after_an_Alt_key_updates_the_only_copy()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();

        MachineHarness.Type(machine, "PRINT 5");
        machine.HandleKey("A-C");
        MachineHarness.Type(machine, "99");

        Assert.Equal("PRINT 599", machine.InputText);
        Assert.Equal(1, RowsHolding(machine, "PRINT 599"));
        Assert.Equal(0, RowsHolding(machine, "PRINT 5\n"));   // the stale copy is gone
        Assert.Equal(0, machine.Screen.Snapshot()
            .Count(r => r.Contains("PRINT 5 ", StringComparison.Ordinal)
                     && !r.Contains("PRINT 599", StringComparison.Ordinal)));
    }

    // ---- Issue 2: ghosts when a long line is deleted -------------------------------

    [Theory]
        [InlineData(158)]
        [InlineData(200)]
        [InlineData(256)]
    public async Task Deleting_a_line_longer_than_the_window_leaves_no_ghosts(int length)
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();

        MachineHarness.Type(machine, new string('S', length));
        for (int i = 0; i < length; i++) machine.HandleKey("BKSP");

        Assert.Equal("", machine.InputText);

        // Only the prompt survives: nothing of the drafted line may remain anywhere.
        List<string> extra = FilledCells(machine)
            .Where(cell => !cell.EndsWith(":>", StringComparison.Ordinal) && !cell.Contains(":>", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(extra);
    }

    [Fact]
    public async Task Recalling_history_over_a_long_draft_leaves_no_ghosts()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();

        MachineHarness.Type(machine, new string('S', 200));
        machine.HandleKey("ENTER");
        await MachineHarness.SettleAsync(machine);

        MachineHarness.Type(machine, "FD 10");
        machine.HandleKey("ENTER");
        await MachineHarness.SettleAsync(machine);

        machine.HandleKey("UP");                    // the 200-character command
        machine.HandleKey("DOWN");                  // back to a blank line

        Assert.Equal("", machine.InputText);

        // The harm the reviewer measured was not cosmetic: leftover cells glued onto the next
        // command. Recall a long line, drop it again, then type - and the typing must be clean.
        MachineHarness.Type(machine, "PRINT 1");
        machine.HandleKey("ENTER");
        await MachineHarness.SettleAsync(machine);

        string text = MachineHarness.ScreenText(machine);
        Assert.Contains("> PRINT 1", text);
        Assert.DoesNotContain("SPRINT", text);
        Assert.DoesNotContain("SSSPRINT", text);
    }

    [Fact]
    public async Task A_ghost_free_transcript_survives_a_run()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();

        MachineHarness.Type(machine, new string('R', 200));
        for (int i = 0; i < 200; i++) machine.HandleKey("BKSP");

        await MachineHarness.RunAsync(machine, "PRINT 1");

        // Before the fix the leftover cells glued onto the command: "RRPRINT 1".
        Assert.DoesNotContain("RRPRINT", MachineHarness.ScreenText(machine));
        Assert.Contains("PRINT 1", MachineHarness.ScreenText(machine));
    }

    // ---- Issue 3: ALT-T strands the previous copy ----------------------------------

    [Theory]
        [InlineData(4)]
        [InlineData(38)]
        [InlineData(60)]
        [InlineData(100)]
    public async Task Changing_the_layout_does_not_strand_a_copy_of_the_draft(int length)
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();

        MachineHarness.Type(machine, new string('L', length));
        machine.HandleKey("A-T");                   // split -> full text
        for (int i = 0; i < length; i++) machine.HandleKey("BKSP");

        Assert.Equal("", machine.InputText);
        Assert.Equal(0, RowsHolding(machine, "LLL"));
    }

    [Fact]
    public async Task Two_layout_flips_do_not_strand_copies()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();

        MachineHarness.Type(machine, new string('L', 200));
        machine.HandleKey("A-T");
        machine.HandleKey("A-T");
        for (int i = 0; i < 200; i++) machine.HandleKey("BKSP");

        Assert.Equal("", machine.InputText);
        Assert.Equal(0, RowsHolding(machine, "LLL"));
    }

    // ---- Issue 4: closing an overlay eats a transcript row ------------------------

    [Fact]
    public async Task Closing_an_overlay_does_not_scroll_the_transcript()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();

        await MachineHarness.RunAsync(machine, "PRINT 101");
        await MachineHarness.RunAsync(machine, "PRINT 202");
        await MachineHarness.RunAsync(machine, "PRINT 303");
        await MachineHarness.RunAsync(machine, "PRINT 404");
        await MachineHarness.RunAsync(machine, "PRINT 505");

        string before = MachineHarness.ScreenText(machine);
        Assert.Contains("505", before);

        MachineHarness.Type(machine, new string('S', 45));
        machine.HandleKey("A-?");                   // quick reference over the top
        machine.HandleKey("ESC");                   // and back down again

        string after = MachineHarness.ScreenText(machine);
        Assert.Contains("> PRINT 505", after);      // the prompt line must still be there
        Assert.Contains("505", after);
        Assert.Equal(1, RowsHolding(machine, "PRINT 505"));
    }

    [Fact]
    public async Task Closing_the_session_manager_does_not_scroll_the_transcript()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();

        for (int i = 1; i <= 5; i++) await MachineHarness.RunAsync(machine, $"PRINT {i}00");
        MachineHarness.Type(machine, new string('S', 45));

        machine.HandleKey("A-L");
        machine.HandleKey("ESC");

        Assert.Contains("> PRINT 500", MachineHarness.ScreenText(machine));
    }

    // ---- Issue 5: a stale draft answers a program's READ ---------------------------

    [Fact]
    public async Task An_unsubmitted_draft_never_becomes_a_programs_answer()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        browser.PickResult = "MAKE \"ANS READLINE PRINT :ANS";

        MachineHarness.Type(machine, "PRINT 9");    // typed but NOT submitted

        // Fire and forget, exactly as the key handler does: the loaded program parks on READ, so
        // awaiting it here would wait for an answer that has not been typed yet.
        _ = machine.ImportAsync();
        for (int i = 0; i < 200 && !machine.AwaitingRead; i++) await Task.Delay(5);
        Assert.True(machine.AwaitingRead);

        // The buffer must be empty: the program's answer is what is typed now, not the draft.
        Assert.Equal("", machine.InputText);

        MachineHarness.Type(machine, "42");
        machine.HandleKey("ENTER");
        await MachineHarness.SettleAsync(machine);

        string text = MachineHarness.ScreenText(machine);
        Assert.DoesNotContain("PRINT 942", text);
        Assert.Contains("42", text);
    }

    // ---- Issue 6: the editor is not bound to a session ----------------------------

    [Fact]
    public async Task Making_a_new_session_from_the_editor_saves_the_program_to_the_old_file()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-M");                   // ADVANCED
        machine.HandleKey("A-E");                   // editor

        MachineHarness.Type(machine, "TO MYPROG OUTPUT 1 END");

        machine.HandleKey("A-L");                   // session manager, from inside the editor
        machine.HandleKey("N");                     // new session
        await MachineHarness.SettleAsync(machine);

        SessionStore.StoreFile? file = SessionStore.Deserialize(browser.StoreJson);
        LogoSession first = file!.Sessions.Single(s => s.Name == "SESSION1.logo");
        Assert.Contains("MYPROG", first.Source);    // the work belongs to the file it was typed in
    }

    [Fact]
    public async Task Alt_S_inside_the_editor_actually_saves_the_program()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-M");
        machine.HandleKey("A-E");
        MachineHarness.Type(machine, "MAKE \"Q 5");

        machine.HandleKey("A-S");
        await MachineHarness.SettleAsync(machine);

        SessionStore.StoreFile? file = SessionStore.Deserialize(browser.StoreJson);
        Assert.Contains("MAKE \"Q 5", file!.Sessions.Single().Source);
    }

    [Fact]
    public async Task Alt_D_inside_the_editor_downloads_the_program()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-M");
        machine.HandleKey("A-E");
        MachineHarness.Type(machine, "TO TEST OUTPUT 1 END");

        await machine.DownloadAsync();
        await MachineHarness.SettleAsync(machine);

        (string, string) download = Assert.Single(browser.Downloads);
        Assert.Contains("TEST", download.Item2);
    }

    // ---- Issue 8 (found while fixing the above): output printed into the block ------

    [Fact]
    public async Task A_program_run_from_the_editor_prints_and_stays_printed()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-M");
        machine.HandleKey("A-E");
        MachineHarness.Type(machine, "PRINT 99");
        machine.HandleKey("A-R");
        await MachineHarness.SettleAsync(machine);

        // The listener still held its prompt block when the program ran. Its output landed on those
        // rows, and the repaint after the run erased the block - and the answer with it.
        string text = MachineHarness.ScreenText(machine);
        Assert.Contains("99", text);
    }

    [Fact]
    public async Task A_loaded_program_prints_and_stays_printed()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        browser.PickResult = "PRINT 555";

        machine.HandleKey("A-O");
        await MachineHarness.SettleAsync(machine);

        Assert.Contains("555", MachineHarness.ScreenText(machine));
    }

    [Fact]
    public async Task A_draft_does_not_swallow_the_answer_to_a_program_from_the_editor()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();

        // Same shape as issue 5, but reached from the listener with a program that prints while a
        // half-typed line sits at the prompt: the draft must not be painted over the output.
        MachineHarness.Type(machine, "PRINT 7");
        await machine.RunAsync("PRINT 111");
        await MachineHarness.SettleAsync(machine);

        string text = MachineHarness.ScreenText(machine);
        Assert.Contains("111", text);
        Assert.Contains("PRINT 7", text);      // the draft is still there, unsubmitted and editable
        Assert.DoesNotContain("PRINT 7111", text);
    }

    // ---- Issue 6b: the route the browser probe took -------------------------------

    [Fact]
    public async Task Alt_l_still_opens_the_session_manager_after_a_loaded_program()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        browser.PickResult = "TYPE \"NAME? MAKE \"A READLINE PRINT :A";

        MachineHarness.Type(machine, "PRINT 9");
        _ = machine.ImportAsync();
        for (int i = 0; i < 200 && !machine.AwaitingRead; i++) await Task.Delay(5);
        MachineHarness.Type(machine, "42");
        machine.HandleKey("ENTER");
        await MachineHarness.SettleAsync(machine);

        machine.HandleKey("A-M");                     // ADVANCED
        machine.HandleKey("A-E");                     // editor
        Assert.Equal(AppScreen.Editor, machine.ScreenKind);

        MachineHarness.Type(machine, "TO MYPROG");
        machine.HandleKey("A-L");
        Assert.Equal(AppScreen.Sessions, machine.ScreenKind);

        machine.HandleKey("N");
        await MachineHarness.SettleAsync(machine);
        Assert.Equal(AppScreen.Editor, machine.ScreenKind);
        Assert.Equal("SESSION2.logo", machine.CurrentName);

        SessionStore.StoreFile? file = SessionStore.Deserialize(browser.StoreJson);
        Assert.Contains("MYPROG", file!.Sessions.Single(s => s.Name == "SESSION1.logo").Source);
    }

    // ---- Issue 9 (browser only): keys arrive lowercase -----------------------------

    [Fact]
    public async Task A_lowercase_letter_token_still_works()
    {
        // A browser reports an unshifted letter as typed, so the session manager receives "n" and
        // "d", not "N" and "D". The unit harness has always pressed the uppercase token, which is
        // exactly why this only showed up in the real browser.
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();

        machine.HandleKey("A-L");
        Assert.Equal(AppScreen.Sessions, machine.ScreenKind);

        machine.HandleKey("n");                    // lowercase, as the keyboard sends it
        await MachineHarness.SettleAsync(machine);

        Assert.Equal(AppScreen.Listener, machine.ScreenKind);
        Assert.Equal(2, machine.Sessions.Count);
    }

    [Fact]
    public async Task Lowercase_d_deletes_the_highlighted_session()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-L");
        machine.HandleKey("N");
        await MachineHarness.SettleAsync(machine);
        Assert.Equal(2, machine.Sessions.Count);

        machine.HandleKey("A-L");
        machine.HandleKey("d");                    // lowercase, as the keyboard sends it
        await MachineHarness.SettleAsync(machine);

        Assert.Single(machine.Sessions);
    }

    [Fact]
    public async Task Opening_the_session_manager_from_the_editor_does_not_paint_over_it()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        machine.HandleKey("A-M");
        machine.HandleKey("A-E");
        MachineHarness.Type(machine, "TO MYPROG");

        machine.HandleKey("A-L");

        // The editor used to redraw itself on top of the file list, so the child saw the program
        // while the machine was handling session-manager keys.
        Assert.Equal(AppScreen.Sessions, machine.ScreenKind);
        Assert.Contains("SESSIONS", MachineHarness.ScreenText(machine));
        Assert.DoesNotContain("MYPROG", MachineHarness.ScreenText(machine));
    }

    [Theory]
        [InlineData(45)]       // taller than the 4-row split window
        [InlineData(200)]      // taller than the whole screen
    public async Task A_message_and_the_draft_never_share_a_row(int length)
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();

        MachineHarness.Type(machine, new string('S', length));
        machine.HandleKey("A-T");                   // TEXT, which prints a line

        string[] rows = machine.Screen.Snapshot();
        Assert.Contains(rows, r => r.Trim() == "TEXT - THE WHOLE SCREEN IS TEXT");

        // The message owns its own row; the draft starts on the row below it and is not spliced
        // into anything Logo printed.
        int messageRow = Array.FindIndex(rows, r => r.Trim() == "TEXT - THE WHOLE SCREEN IS TEXT");
        Assert.DoesNotContain("SSSS", rows[messageRow]);
        Assert.StartsWith("> SSSS", rows[messageRow + 1].TrimEnd());
    }

    [Fact]
    public async Task Committing_a_draft_writes_from_the_start_of_the_row()
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();

        // The browser sequence that spliced a command into the middle of a message: a long draft,
        // ALT-T (which prints on the row the draft leaves), delete the draft, then type and run a
        // command. Committing wrote at the column the painter had parked the cursor at rather than
        // at the start of the row, giving "TEXT - THE > PRINT 101N IS TEXT".
        MachineHarness.Type(machine, new string('S', 45));
        machine.HandleKey("A-T");
        for (int i = 0; i < 45; i++) machine.HandleKey("BKSP");

        MachineHarness.Type(machine, "PRINT 101");
        machine.HandleKey("ENTER");
        await MachineHarness.SettleAsync(machine);

        string[] rows = machine.Screen.Snapshot();

        // A transcript line always starts at column 0. Leading spaces here mean the command was
        // written at the column the painter had parked the cursor at, not at the start of the row.
        Assert.Contains(rows, r => r.Trim() == "TEXT - THE WHOLE SCREEN IS TEXT");
        Assert.Contains(rows, r => r.StartsWith("> PRINT 101"));
        Assert.DoesNotContain(rows, r => r.Trim().StartsWith("TEXT") && r.Contains("> PRINT"));
    }

    // ---- Issue 7: "SAVED" when the browser refused the write -----------------------

    [Fact]
    public async Task A_refused_save_says_so_instead_of_claiming_success()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        browser.WriteResult = false;   // quota exceeded

        await MachineHarness.RunAsync(machine, "MAKE \"SECRET 1234");
        machine.HandleKey("A-S");
        await MachineHarness.SettleAsync(machine);

        string text = MachineHarness.ScreenText(machine);
        Assert.DoesNotContain("SAVED SESSION1.logo", text);
        Assert.Contains("COULD NOT SAVE", text);
    }
}
