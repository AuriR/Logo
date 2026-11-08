using System.Text.RegularExpressions;
using MaturinLogo.Core.Interpreter;
using MaturinLogo.Core.Machine;
using MaturinLogo.Core.Primitives;

namespace MaturinLogo.Core.Tests;

/// <summary>
/// TOOT, the pitch table behind it, and the switch that turns sound off. The interpreter decides
/// all of it; the browser only ever receives a frequency and a length, which is why the whole
/// command can be tested without one.
/// </summary>
public class SoundTests
{
    [Theory]
    [InlineData("TOOT 440", 440, SoundPrimitives.DefaultDuration)]
    [InlineData("TOOT 440 0.5", 440, 0.5)]
    [InlineData("TOOT 1000 1", 1000, 1)]
    [InlineData("TOOT 100 0.02", 100, 0.02)]
    [InlineData("TOOT \"A4", 440, SoundPrimitives.DefaultDuration)]
    [InlineData("TOOT \"A4 2", 440, 2)]
    [InlineData("TOOT \"C4", 261.63, SoundPrimitives.DefaultDuration)]
    [InlineData("TOOT \"C#4", 277.18, SoundPrimitives.DefaultDuration)]
    [InlineData("TOOT \"Bb3", 233.08, SoundPrimitives.DefaultDuration)]
    [InlineData("TOOT \"C", 261.63, SoundPrimitives.DefaultDuration)]      // octave omitted
    [InlineData("TOOT \"A0", 27.5, SoundPrimitives.DefaultDuration)]
    public static async Task Toot_hands_the_browser_a_frequency_and_a_length(
        string command, double hertz, double seconds)
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, command);

        (double played, double length) = Assert.Single(browser.Tones);
        Assert.Equal(hertz, played, 2);
        Assert.Equal(seconds, length, 3);
    }

    [Fact]
    public static async Task Toot_accepts_a_word_that_holds_a_number()
    {
        // TOOT :P is how a program plays a pitch it computed, and P may hold "440" as easily as 440.
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "MAKE \"P \"440 TOOT :P");

        Assert.Equal(440, browser.Tones.Single().Hertz, 2);
    }

    [Theory]
    [InlineData("TOOT")]                       // no pitch at all
    [InlineData("TOOT 440 0.5 2")]             // a third number meant something
    [InlineData("TOOT 8")]                     // below the lowest note
    [InlineData("TOOT 20000")]                 // above the highest
    [InlineData("TOOT 440 0.001")]             // too short to hear
    [InlineData("TOOT 440 60")]                // longer than anyone will wait
    [InlineData("TOOT \"BANANA")]              // not a note we know
    public static async Task Toot_refuses_what_it_cannot_play(string command)
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, command);

        Assert.Empty(browser.Tones);

        // And it says so rather than going silent: a command that does nothing and says nothing is
        // indistinguishable from a computer with its speaker turned down.
        Assert.True(MachineHarness.ScreenText(machine).Length > 0, $"{command} said nothing");
    }

    [Theory]
    [InlineData("TOOT 5", LogoErrorCode.OutOfRange)]
    [InlineData("TOOT 440 30", LogoErrorCode.OutOfRange)]
    [InlineData("TOOT", LogoErrorCode.NotEnoughInputs)]
    [InlineData("TOOT 1 2 3", LogoErrorCode.TooManyInputs)]
    [InlineData("TOOT \"BANANA", LogoErrorCode.BadNumber)]
    [InlineData("TOOT \"H4", LogoErrorCode.BadNumber)]
    [InlineData("TOOT [440]", LogoErrorCode.BadInputType)]
    public static async Task Toot_reports_the_right_error(string command, LogoErrorCode code) =>
        await Logo.ExpectErrorAsync(command, code);

    [Fact]
    public static async Task A_sequence_of_toots_is_a_tune()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "REPEAT 3 [TOOT 440 0.1 TOOT 550 0.1]");

        Assert.Equal(6, browser.Tones.Count);
        Assert.Equal(440, browser.Tones[0].Hertz, 2);
        Assert.Equal(550, browser.Tones[1].Hertz, 2);
    }

    [Fact]
    public static async Task Setsound_off_silences_the_language()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();

        await MachineHarness.RunAsync(machine, "SETSOUND FALSE");
        Assert.False(machine.Engine.SoundEnabled);
        await MachineHarness.RunAsync(machine, "TOOT 440");
        Assert.Empty(browser.Tones);

        await MachineHarness.RunAsync(machine, "SETSOUND TRUE");
        await MachineHarness.RunAsync(machine, "TOOT 440");
        Assert.Single(browser.Tones);
    }

    [Fact]
    public static async Task Turning_sound_off_also_stops_what_is_queued()
    {
        // "Off" that lets the rest of a tune finish is not off.
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "SETSOUND FALSE");
        Assert.Equal(1, browser.ToneStops);
    }

    [Fact]
    public static async Task A_tone_does_not_hold_up_the_interpreter()
    {
        // The browser queues notes on its own audio clock, so Logo must not wait for one. A two
        // second note at speed 25 still lets the next command run at once - which is also what
        // makes ESC work in the middle of a tune.
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        machine.SetSpeed(25);

        await MachineHarness.RunAsync(machine, "REPEAT 5 [TOOT 440 2]");
        Assert.Equal(5, browser.Tones.Count);
        Assert.All(browser.Tones, tone => Assert.Equal(2, tone.Seconds, 3));
    }

    [Theory]
    [InlineData("SETSOUND \"FALSE", false)]
    [InlineData("SETSOUND FALSE", false)]              // the manual's spelling, now a real value
    [InlineData("SETSOUND \"TRUE", true)]
    [InlineData("SETSOUND TRUE", true)]
    public static async Task Setsound_accepts_the_usual_spellings(string command, bool expected)
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, command);
        Assert.Equal(expected, machine.Engine.SoundEnabled);
    }

    [Theory]
    [InlineData("PRINT SOUND", "TRUE")]
    [InlineData("SETSOUND \"FALSE PRINT SOUND", "FALSE")]
    public static async Task Sound_reports_the_switch(string command, string expected)
    {
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, command);

        Assert.Contains(expected, MachineHarness.ScreenText(machine), StringComparison.Ordinal);
    }

    [Fact]
    public static async Task A_bare_true_and_false_are_values()
    {
        // The fix SETSOUND needed is the one SETCASE and the manual's examples always assumed: a
        // boolean printed as TRUE or FALSE can be typed back in as itself.
        Assert.Equal("TRUE", await Logo.ShowAsync("IF EQUAL 1 1 [OUTPUT \"TRUE] [OUTPUT \"FALSE]"));
        Assert.Equal("TRUE", await Logo.ShowAsync("IF EQUAL 1 1 [OUTPUT TRUE] [OUTPUT FALSE]"));
        await Logo.RunAsync("SETCASE FALSE");    // the architecture document's own example
    }

    [Fact]
    public static async Task A_child_who_took_the_word_true_keeps_theirs()
    {
        // TRUE and FALSE are the *last* thing a bare word resolves to, not the first, so nothing that
        // used to work stops working. A procedure or a variable named TRUE still belongs to the child
        // who defined it - the same rule that lets MAKE 1 "X through untouched.
        Assert.Contains("HELLO", await Logo.RunAsync("TO TRUE PRINT \"HELLO END TRUE"), StringComparison.Ordinal);
        Assert.Equal("5", await Logo.ShowAsync("MAKE \"TRUE 5 :TRUE"));

        // And with nothing defined, the word is the value - usable as a test and as an input.
        Assert.Equal("TRUE", await Logo.ShowAsync("IF TRUE [OUTPUT \"TRUE] [OUTPUT \"FALSE]"));
        Assert.Equal("FALSE", await Logo.ShowAsync("IF FALSE [OUTPUT \"TRUE] [OUTPUT \"FALSE]"));
        Assert.Equal("TRUE", await Logo.ShowAsync("NOT FALSE"));
    }

    [Fact]
    public static async Task Breaking_a_program_silences_it()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();

        // A long note is exactly the case ESC has to survive: the interpreter never waits for the
        // sound, so it can break immediately - but the notes already scheduled still have to stop.
        MachineHarness.Submit(machine, "REPEAT 200 [TOOT 440 2]");
        machine.HandleKey("A-B");
        await MachineHarness.SettleAsync(machine);

        Assert.True(browser.ToneStops >= 1, "breaking the program did not silence its notes");
    }

    [Fact]
    public static async Task Sound_is_remembered_with_the_other_settings()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        await MachineHarness.RunAsync(machine, "SETSOUND FALSE");
        await machine.SaveAsync();

        (LogoMachine again, _) = await MachineHarness.AtListenerAsync(browser.StoreJson);
        Assert.False(again.Engine.SoundEnabled);
    }

    [Fact]
    public static async Task Sound_defaults_to_on()
    {
        // A child who never touches the settings expects the new command to make a noise.
        (LogoMachine machine, _) = await MachineHarness.AtListenerAsync();
        Assert.True(machine.Engine.SoundEnabled);
    }

    [Fact]
    public static async Task The_settings_screen_toggles_sound()
    {
        (LogoMachine machine, FakeBrowser browser) = await MachineHarness.AtListenerAsync();
        machine.ShowSettings();

        GoToSetting(machine, "SOUND");
        Assert.Contains("ON", HighlightedRow(machine), StringComparison.Ordinal);

        machine.HandleKey("ENTER");
        Assert.False(machine.Engine.SoundEnabled);
        Assert.Equal(AppScreen.Settings, machine.ScreenKind);

        machine.HandleKey("ENTER");
        Assert.True(machine.Engine.SoundEnabled);

        // The row tells and toggles in the same press, and the screen is repainted from live state.
        Assert.Contains("ON", HighlightedRow(machine), StringComparison.Ordinal);
        Assert.Empty(browser.Tones);
    }

    /// <summary>Walk the settings cursor down to the row naming <paramref name="label"/>.</summary>
    private static void GoToSetting(LogoMachine machine, string label)
    {
        for (int presses = 0; presses <= 24; presses++)
        {
            if (HighlightedRow(machine).Contains(label, StringComparison.Ordinal)) return;
            machine.HandleKey("DOWN");
        }

        throw new Xunit.Sdk.XunitException($"the settings screen has no row naming {label}");
    }

    private static string HighlightedRow(LogoMachine machine)
    {
        for (int r = 0; r < MaturinLogo.Core.Screen.TextScreen.Rows; r++)
        {
            for (int c = 1; c < MaturinLogo.Core.Screen.TextScreen.Cols - 1; c++)
            {
                if (machine.Screen.Attrs[r, c].HasFlag(MaturinLogo.Core.Screen.CellAttr.Reverse))
                    return machine.Screen.GetRowText(r).Trim();
            }
        }

        throw new Xunit.Sdk.XunitException("no settings row is highlighted");
    }

    // ---- the pitch table, note by note ------------------------------------------------

    [Theory]
    [InlineData("A4", 440)]
    [InlineData("A3", 220)]
    [InlineData("A5", 880)]
    [InlineData("C4", 261.63)]
    [InlineData("E4", 329.63)]
    [InlineData("G4", 392)]
    [InlineData("C5", 523.25)]
    [InlineData("B4", 493.88)]
    [InlineData("Cb4", 246.94)]
    [InlineData("B#3", 261.63)]                        // enharmonic: B#3 is middle C
    [InlineData("Db5", 554.37)]
    [InlineData("F##4", 392)]                          // two sharps, and it is G4
    [InlineData("c4", 261.63)]                         // case is Logo's business, not ours
    [InlineData("C♯4", 277.18)]
    [InlineData("B♭3", 233.08)]
    public static void Note_names_become_hertz(string name, double hertz)
    {
        Assert.True(LogoPitch.TryToHertz(name, out double actual), $"{name} did not parse");
        Assert.Equal(hertz, actual, 2);
    }

    [Theory]
    [InlineData("H4")]
    [InlineData("A9")]                                 // off the top of any piano
    [InlineData("A-1")]
    [InlineData("4")]
    [InlineData("A44")]
    [InlineData("AA4")]
    [InlineData("")]
    [InlineData("BEBOP")]
    public static void Names_that_are_not_notes_are_refused(string name) =>
        Assert.False(LogoPitch.TryToHertz(name, out _));

    [Fact]
    public static void The_octave_may_be_left_off()
    {
        // A child who types TOOT "C means middle C, which is the fourth octave on a piano.
        Assert.True(LogoPitch.TryToHertz("C", out double omitted));
        Assert.True(LogoPitch.TryToHertz("C4", out double written));
        Assert.Equal(written, omitted, 2);
    }

    [Fact]
    public static void A_note_and_its_hertz_agree()
    {
        // The two halves of the pitch table - equal temperament and the note names - are only
        // trustworthy while they say the same thing about the same note.
        Assert.Equal(440, LogoPitch.ToHertz(LogoPitch.ToMidi(440)), 2);
        Assert.Equal(440, LogoPitch.ToHertz(69), 2);
        Assert.Equal(220, LogoPitch.ToHertz(57), 2);   // an octave down is half the frequency
    }

    [Fact]
    public static void Every_note_name_can_be_spelled_back()
    {
        // The error hint quotes a real note name, so whatever it prints has to be readable again.
        for (int midi = 12; midi <= 108; midi++)
        {
            double hertz = LogoPitch.ToHertz(midi);
            string name = LogoPitch.NoteName(hertz);
            Assert.True(LogoPitch.TryToHertz(name, out double back), $"{name} could not be read back");
            Assert.Equal(hertz, back, 1);
        }
    }

    [Fact]
    public static async Task The_note_the_error_hint_quotes_is_one_the_command_accepts()
    {
        // The hint names a real note at the end of the range so a child who typed 5 knows what
        // "too low" means. It is built from hertz, and a MIDI number passed by mistake once spelled
        // the note "G-1" - a name the pitch table refuses, so the hint quoted a note Logo could not
        // play. Checking the text of the hint is the only way to catch that.
        LogoException low = await Logo.ExpectErrorAsync("TOOT 5", LogoErrorCode.OutOfRange);
        LogoException high = await Logo.ExpectErrorAsync("TOOT 20000", LogoErrorCode.OutOfRange);

        foreach (LogoException error in new[] { low, high })
        {
            string hint = error.Hint ?? "";
            Match match = Regex.Match(hint, @"[A-G][#B♯♭]?-?[0-8]\b");
            Assert.True(match.Success, $"the hint names no note: {hint}");

            Assert.True(LogoPitch.TryToHertz(match.Value, out double hertz),
                $"the hint names {match.Value}, which is not a note Logo can play");
            Assert.True(hertz >= SoundPrimitives.MinHertz && hertz <= SoundPrimitives.MaxHertz,
                $"the hint names {match.Value} at {hertz:F0} Hz, outside the accepted range");
        }
    }

    [Fact]
    public static void A_flat_survives_the_reader_folding_it_to_capitals()
    {
        // Logo folds every word to capitals, so "Bb3" reaches a primitive as "BB3". A flat written
        // with a letter that also names the note B has to be read as a flat anyway, or the child
        // who typed the correct name of the note hears the wrong one.
        Assert.True(LogoPitch.TryToHertz("BB3", out double folded));
        Assert.True(LogoPitch.TryToHertz("Bb3", out double written));
        Assert.Equal(written, folded, 2);

        Assert.True(LogoPitch.TryToHertz("CB4", out double cFlat));
        Assert.Equal(246.94, cFlat, 2);
    }
}
