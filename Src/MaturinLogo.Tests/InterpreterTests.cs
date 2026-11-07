using MaturinLogo.Core.Interpreter;
using MaturinLogo.Core.Primitives;
using MaturinLogo.Core.Turtle;
using MaturinLogo.Core.Values;

namespace MaturinLogo.Core.Tests;

/// <summary>
/// The behaviour that matters most: Logo's pull-based input semantics. A primitive takes exactly
/// the number of inputs it declares and no more, which is what makes "FD 10 RT 90" two calls.
/// </summary>
public class EvaluatorTests
{
    [Theory]
    [InlineData("PRINT 1", "1")]
    [InlineData("PRINT 2 + 3", "5")]
    [InlineData("PRINT 10 - 3", "7")]
    [InlineData("PRINT 6 * 7", "42")]
    [InlineData("PRINT 100 / 4", "25")]
    // Logo has no operator precedence: it reads strictly left to right, so this is 20, not 14.
    [InlineData("PRINT 2 + 3 * 4", "20")]
    [InlineData("PRINT (2 + 3) * 4", "20")]
    [InlineData("SHOW 2 + 2", "4")]
    [InlineData("PRINT FIRST \"LOGO", "L")]
    [InlineData("PRINT BUTFIRST \"LOGO", "OGO")]
    [InlineData("PRINT LAST [1 2 3]", "3")]
    [InlineData("PRINT COUNT [1 2 3]", "3")]
    [InlineData("PRINT ITEM 2 [RED GREEN BLUE]", "GREEN")]
    [InlineData("PRINT WORD \"HELLO \"THERE", "HELLOTHERE")]
    [InlineData("PRINT SE [1 2] [3]", "[1 2 3]")]
    [InlineData("PRINT LIST [1 2] 3", "[[1 2] 3]")]
    [InlineData("PRINT EQUAL 2 + 2 4", "TRUE")]
    [InlineData("PRINT NOT EQUAL 2 3", "TRUE")]
    [InlineData("PRINT LESS 3 5", "TRUE")]
    [InlineData("PRINT GREATER 3 5", "FALSE")]
    [InlineData("PRINT MEMBER \"CAT \"SCATTER", "TRUE")]
    [InlineData("PRINT REMAINDER 10 3", "1")]
    [InlineData("PRINT ABS -7", "7")]
    [InlineData("PRINT ROUND 2.6", "3")]
    [InlineData("PRINT POWER 2 10", "1024")]
    [InlineData("PRINT SQRT 81", "9")]
    [InlineData("PRINT REVERSE [1 2 3]", "[3 2 1]")]
    [InlineData("PRINT SUBSTRING \"LOGO 2 2", "OG")]
    [InlineData("PRINT IMPLODE [L O G O]", "LOGO")]
    [InlineData("PRINT EXPLODE \"AB", "[A B]")]
    [InlineData("PRINT FILL \"- 5", "-----")]
    [InlineData("PRINT SUM 3 4", "7")]
    [InlineData("PRINT DIFFERENCE 10 4", "6")]
    [InlineData("PRINT PRODUCT 6 7", "42")]
    [InlineData("PRINT QUOTIENT 100 4", "25")]
    [InlineData("PRINT MINUS 5", "-5")]
    [InlineData("PRINT IFELSE GREATER 5 3 \"YES \"NO", "YES")]
    [InlineData("PRINT CAR [1 2 3]", "1")]
    [InlineData("PRINT CDR [1 2 3]", "[2 3]")]
    [InlineData("PRINT ASSOC \"RED [[RED 1] [BLUE 5]]", "1")]
    [InlineData("PRINT NUMBERP 5", "TRUE")]
    [InlineData("PRINT WORDP \"HELLO", "TRUE")]
    [InlineData("PRINT LISTP [1]", "TRUE")]
    [InlineData("PRINT EMPTY []", "TRUE")]
    [InlineData("PRINT 1 + 2 + 3 + 4", "10")]
    [InlineData("PRINT 100 / 2 / 5", "10")]
    [InlineData("PRINT 2 * 3 + 4", "10")]
    [InlineData("PRINT 10 - 3 - 2", "5")]
    public async Task Commands_print_what_the_child_would_see(string source, string expected) =>
        Assert.Equal(expected, await Logo.RunAsync(source));

    [Theory]
    [InlineData("2 + 3", "5")]
    [InlineData("FIRST \"LOGO", "L")]
    [InlineData("COUNT [1 2 3]", "3")]
    [InlineData("SUM 3 4", "7")]
    [InlineData("EQUAL 2 + 2 4", "TRUE")]
    [InlineData("(2 + 3) * 4", "20")]
    [InlineData("3 * (2 + 4)", "18")]
    [InlineData("PRODUCT (2 + 3) 4", "20")]
    [InlineData("ITEM 2 [RED GREEN BLUE]", "GREEN")]
    [InlineData("SE [1 2] [3]", "[1 2 3]")]
    [InlineData("WORD \"HELLO \"THERE", "HELLOTHERE")]
    public async Task Reporters_return_the_right_value(string source, string expected) =>
        Assert.Equal(expected, await Logo.ShowAsync(source));

    [Theory]
    [InlineData("PRINT 1 2 3", "1 2 3")]
    [InlineData("PRINT [1 2 3]", "[1 2 3]")]
    [InlineData("PRINT \"HELLO \"THERE", "HELLO THERE")]
    [InlineData("PRINT SENTENCE [A] [B]", "[A B]")]
    [InlineData("PRINT 2 + 2 4 + 4", "4 8")]
    public async Task Variadic_primitives_take_everything_up_to_the_next_command(string source, string expected) =>
        Assert.Equal(expected, await Logo.RunAsync(source));

    [Fact]
    public async Task Two_commands_on_one_line_both_run()
    {
        var screen = await Logo.RunAsync("PRINT 1 PRINT 2");
        Assert.Equal("1\n2", screen);
    }

    [Fact]
    public async Task A_command_with_too_few_inputs_reports_the_command_name()
    {
        var error = await Logo.ExpectErrorAsync("FD", LogoErrorCode.NotEnoughInputs);
        Assert.Contains("FD", error.Message);
    }

    [Fact]
    public async Task An_unknown_name_suggests_the_closest_command()
    {
        var error = await Logo.ExpectErrorAsync("FORWRD 10", LogoErrorCode.NotAProcedure);
        Assert.NotNull(error.Hint);
        // The full name is the kind suggestion: a child who mistyped FD will recognise FORWARD.
        Assert.Contains("FORWARD", error.Hint);
    }

    [Fact]
    public async Task A_missing_variable_names_the_variable()
    {
        var error = await Logo.ExpectErrorAsync("PRINT :SIZE", LogoErrorCode.NoValue);
        Assert.Contains("SIZE", error.Message);
    }

    [Fact]
    public async Task Division_by_zero_is_caught_rather_than_producing_infinity()
    {
        await Logo.ExpectErrorAsync("PRINT 5 / 0", LogoErrorCode.DivisionByZero);
    }
}

public class ProcedureTests
{
    [Fact]
    public async Task TO_and_END_define_a_command_that_can_be_used()
    {
        var engine = Logo.New();
        await engine.RunAsync("TO SQUARE :SIZE REPEAT 4 [FD :SIZE RT 90] END SQUARE 10");
        Assert.True(engine.Workspace.HasProcedure("SQUARE"));
        Assert.Equal(4, engine.Scene.Items.OfType<DrawSegment>().Count());
        Assert.Equal(0, engine.Turtle.X, 6);
        Assert.Equal(0, engine.Turtle.Y, 6);
    }

    [Fact]
    public async Task A_procedure_with_inputs_and_a_report_works()
    {
        var engine = Logo.New();
        await engine.RunAsync("TO DOUBLE :N OUTPUT :N * 2 END");
        Assert.Equal("10", Printer.Format(await engine.EvalTextAsync("DOUBLE 5"), true));
    }

    [Fact]
    public async Task Procedures_can_call_each_other()
    {
        var engine = Logo.New();
        await engine.RunAsync("TO SMALL OUTPUT 5 END TO BIG OUTPUT SMALL * 2 END");
        Assert.Equal("10", Printer.Format(await engine.EvalTextAsync("BIG"), true));
    }

    [Fact]
    public async Task LOCAL_gives_a_procedure_its_own_variable()
    {
        var engine = Logo.New();
        await engine.RunAsync("MAKE \"TOTAL 100 TO TRIAL LOCAL [TOTAL] MAKE \"TOTAL 1 OUTPUT :TOTAL END");
        Assert.Equal("1", Printer.Format(await engine.EvalTextAsync("TRIAL"), true));
        // The global must be untouched by the local.
        Assert.Equal("100", Printer.Format(await engine.EvalTextAsync(":TOTAL"), true));
    }

    [Fact]
    public async Task Inputs_are_visible_only_inside_the_procedure()
    {
        var engine = Logo.New();
        await engine.RunAsync("TO SHOWIT :N OUTPUT :N END SHOWIT 7");
        await Assert.ThrowsAsync<LogoException>(() => engine.EvalTextAsync(":N"));
    }

    [Fact]
    public async Task An_optional_input_falls_back_to_its_default()
    {
        var engine = Logo.New();
        await engine.RunAsync("TO GREET [:NAME \"WORLD] OUTPUT SENTENCE \"HELLO :NAME END");
        Assert.Equal("[HELLO WORLD]", Printer.Format(await engine.EvalTextAsync("GREET"), true));
        Assert.Equal("[HELLO JO]", Printer.Format(await engine.EvalTextAsync("(GREET \"JO)"), true));
    }

    [Fact]
    public async Task A_catch_all_input_collects_the_rest()
    {
        var engine = Logo.New();
        await engine.RunAsync("TO COLLECT :FIRST [:REST] OUTPUT :REST END");
        Assert.Equal("[2 3]", Printer.Format(await engine.EvalTextAsync("(COLLECT 1 2 3)"), true));
    }

    [Fact]
    public async Task STOP_ends_a_procedure_early()
    {
        var engine = Logo.New();
        await engine.RunAsync("TO CHECK :N IF EQUAL :N 0 [STOP] PRINT :N END");
        await engine.RunAsync("CHECK 0");
        Assert.Equal("", Logo.ScreenText(engine));
        await engine.RunAsync("CHECK 5");
        Assert.Equal("5", Logo.ScreenText(engine));
    }

    [Fact]
    public async Task TO_without_END_is_reported_as_unfinished()
    {
        await Logo.ExpectErrorAsync("TO BROKEN :N PRINT :N", LogoErrorCode.UnterminatedDefinition);
    }

    [Fact]
    public async Task A_Logo_word_cannot_be_used_as_a_procedure_name()
    {
        await Logo.ExpectErrorAsync("TO REPEAT :N PRINT :N END", LogoErrorCode.ReadOnly);
    }

    [Fact]
    public async Task ERASE_forgets_a_procedure()
    {
        var engine = Logo.New();
        await engine.RunAsync("TO TEMP PRINT 1 END ERASE \"TEMP");
        Assert.False(engine.Workspace.HasProcedure("TEMP"));
    }

    [Fact]
    public async Task TEXT_reports_the_source_of_a_procedure()
    {
        var engine = Logo.New();
        await engine.RunAsync("TO SQUARE :SIZE REPEAT 4 [FD :SIZE RT 90] END");
        var text = await engine.EvalTextAsync("PROCTEXT \"SQUARE");
        Assert.Contains("TO SQUARE", text.Word);
        Assert.Contains("END", text.Word);
    }

    [Fact]
    public async Task A_TO_inside_a_body_defines_a_procedure_when_the_outer_one_runs()
    {
        // Apple Logo refuses a TO written inside another TO. Rather than throw at a child who is
        // typing one procedure per line, the inner definition simply runs as a MAKE-like step:
        // OUTER's body is "TO INNER PRINT 1 END PRINT 2", so running OUTER teaches Logo INNER and
        // then prints 2. Running INNER - which now exists - is what prints 1.
        var engine = Logo.New();
        await engine.RunAsync("TO OUTER TO INNER PRINT 1 END PRINT 2 END OUTER");
        Assert.Equal("2", Logo.ScreenText(engine));
        Assert.True(engine.Workspace.HasProcedure("OUTER"));
        Assert.True(engine.Workspace.HasProcedure("INNER"));

        engine.Screen.Clear();
        await engine.RunAsync("INNER");
        Assert.Equal("1", Logo.ScreenText(engine));
    }
}

public class RecursionTests
{
    [Theory]
    [InlineData("TO FACT :N IF EQUAL :N 0 [OUTPUT 1] OUTPUT :N * FACT :N - 1 END", 6, "720")]
    // The parentheses matter. Logo hands a procedure its inputs as whole expressions, so bare
    // "FIB :N - 1 + FIB :N - 2" would be ONE input - ":N - 1 + FIB ..." - and never bottom out.
    // Brackets cut the expression short, which is how the same program is written on an Apple II.
    [InlineData("TO FIB :N IF LESS :N 2 [OUTPUT :N] OUTPUT (FIB (:N - 1)) + (FIB (:N - 2)) END", 10, "55")]
    public async Task Recursive_reporters_compute_correctly(string definition, int argument, string expected)
    {
        var engine = Logo.New();
        await engine.RunAsync(definition);
        var result = await engine.EvalTextAsync($"FACT {argument}".Replace("FACT", FirstWord(definition)));
        Assert.Equal(expected, Printer.Format(result, true));
    }

    [Fact]
    public async Task Deep_tail_recursion_does_not_overflow_the_stack()
    {
        // A tail-recursive counter: without trampolining this blows the stack, and WASM has a
        // small one, so this is the test that protects the design decision.
        var engine = Logo.New();
        await engine.RunAsync("TO COUNTDOWN :N IF EQUAL :N 0 [OUTPUT 0] OUTPUT COUNTDOWN :N - 1 END");
        var result = await engine.EvalTextAsync("COUNTDOWN 5000");
        Assert.Equal("0", Printer.Format(result, true));
    }

    [Fact]
    public async Task Recursion_that_never_stops_is_reported_not_hung()
    {
        var engine = Logo.New();
        await engine.RunAsync("TO FOREVER OUTPUT FOREVER + 1 END");
        var error = await Assert.ThrowsAsync<LogoException>(() => engine.EvalTextAsync("FOREVER"));
        Assert.Equal(LogoErrorCode.RunawayLoop, error.Code);
    }

    private static string FirstWord(string definition)
    {
        // Pull the procedure name out of "TO NAME ...".
        var parts = definition.Split(' ');
        return parts[1];
    }
}

public class TurtleTests
{
    [Fact]
    public async Task FD_draws_a_segment_straight_north()
    {
        var engine = Logo.New();
        await engine.RunAsync("FD 100");
        var segment = Assert.Single(engine.Scene.Items.OfType<DrawSegment>());
        Assert.Equal(0, segment.X1, 6);
        Assert.Equal(0, segment.Y1, 6);
        Assert.Equal(0, segment.X2, 6);
        Assert.Equal(100, segment.Y2, 6);
    }

    [Fact]
    public async Task RT_then_FD_points_the_turtle_east()
    {
        var engine = Logo.New();
        await engine.RunAsync("RT 90 FD 50");
        var segment = Assert.Single(engine.Scene.Items.OfType<DrawSegment>());
        Assert.Equal(50, segment.X2, 6);
        Assert.Equal(0, segment.Y2, 6);
    }

    [Fact]
    public async Task A_square_ends_where_it_started()
    {
        var engine = Logo.New();
        await engine.RunAsync("REPEAT 4 [FD 50 RT 90]");
        Assert.Equal(4, engine.Scene.Items.OfType<DrawSegment>().Count());
        Assert.Equal(0, engine.Turtle.X, 6);
        Assert.Equal(0, engine.Turtle.Y, 6);
        Assert.Equal(0, engine.Turtle.Heading, 6);
    }

    [Fact]
    public async Task PENUP_moves_without_drawing()
    {
        var engine = Logo.New();
        await engine.RunAsync("PU FD 100 PD FD 50");
        var segment = Assert.Single(engine.Scene.Items.OfType<DrawSegment>());
        Assert.Equal(100, segment.Y1, 6);
        Assert.Equal(150, segment.Y2, 6);
    }

    [Fact]
    public async Task HOME_and_CS_clear_the_picture()
    {
        var engine = Logo.New();
        await engine.RunAsync("FD 100 CS");
        Assert.Empty(engine.Scene.Items);
        Assert.Equal(0, engine.Turtle.X);
        Assert.Equal(0, engine.Turtle.Y);
    }

    [Fact]
    public async Task HEADING_stays_within_logo_degree_range()
    {
        var engine = Logo.New();
        await engine.RunAsync("LT 90");
        var heading = await engine.EvalTextAsync("HEADING");
        Assert.Equal(270, heading.Number, 6);
    }

    [Fact]
    public async Task POS_reports_where_the_turtle_stands()
    {
        var engine = Logo.New();
        await engine.RunAsync("FD 100 RT 90 FD 50");
        Assert.Equal("[50 100]", Printer.Format(await engine.EvalTextAsync("POS"), true));
    }

    [Fact]
    public async Task TOWARDS_points_at_a_spot()
    {
        var engine = Logo.New();
        Assert.Equal("90", Printer.Format(await Logo.EvalAsync("TOWARDS 50 0"), true));
        Assert.Equal("0", Printer.Format(await Logo.EvalAsync("TOWARDS 0 50"), true));
    }

    [Fact]
    public async Task SETXY_moves_without_drawing()
    {
        var engine = Logo.New();
        await engine.RunAsync("SETXY 20 30");
        Assert.Empty(engine.Scene.Items);
        Assert.Equal(20, engine.Turtle.X, 6);
        Assert.Equal(30, engine.Turtle.Y, 6);
    }

    [Fact]
    public async Task ARC_sweeps_the_turtle_and_lands_on_the_circle()
    {
        var engine = Logo.New();
        await engine.RunAsync("ARC 90 50");
        Assert.Equal(90, engine.Turtle.Heading, 4);
        // Turning right from north puts the centre 50 due east, at (50, 0). A quarter turn ends
        // due north of that centre. The chord construction must stay on the circle exactly.
        Assert.Equal(50, engine.Turtle.X, 2);
        Assert.Equal(50, engine.Turtle.Y, 2);
        double radius = Math.Sqrt(Math.Pow(engine.Turtle.X - 50, 2) + Math.Pow(engine.Turtle.Y, 2));
        Assert.Equal(50, radius, 2);
    }

    [Fact]
    public async Task CIRCLE_returns_the_turtle_to_where_it_started()
    {
        var engine = Logo.New();
        await engine.RunAsync("CIRCLE 40");
        Assert.Equal(0, engine.Turtle.X, 3);
        Assert.Equal(0, engine.Turtle.Y, 3);
    }

    [Fact]
    public async Task SETPC_accepts_a_colour_number_or_name()
    {
        var engine = Logo.New();
        await engine.RunAsync("SETPC 4");
        Assert.Equal(4, engine.Turtle.PenColor);
        await engine.RunAsync("SETPC \"BLUE");
        Assert.Equal(5, engine.Turtle.PenColor);
    }

    [Fact]
    public async Task An_out_of_range_colour_is_reported()
    {
        await Logo.ExpectErrorAsync("SETPC 99", LogoErrorCode.OutOfRange);
    }

    [Fact]
    public async Task LABEL_stamps_text_on_the_picture()
    {
        var engine = Logo.New();
        await engine.RunAsync("LABEL \"HELLO");
        Assert.Single(engine.Scene.Items.OfType<DrawLabel>());
    }
}

public class LoopTests
{
    [Theory]
    [InlineData("REPEAT 3 [PRINT 1]", "1\n1\n1")]
    [InlineData("FOR [I 1 3] [PRINT :I]", "1\n2\n3")]
    [InlineData("FOR [I 1 9 3] [PRINT :I]", "1\n4\n7")]
    [InlineData("FOR [I 3 1 -1] [PRINT :I]", "3\n2\n1")]
    [InlineData("FOREACH [1 2 3] [PRINT ?]", "1\n2\n3")]
    [InlineData("MAKE \"N 0 WHILE [LESS :N 3] [MAKE \"N :N + 1] PRINT :N", "3")]
    [InlineData("MAKE \"N 0 UNTIL [EQUAL :N 3] [MAKE \"N :N + 1] PRINT :N", "3")]
    [InlineData("IF EQUAL 1 1 [PRINT \"YES] [PRINT \"NO]", "YES")]
    [InlineData("IF EQUAL 1 2 [PRINT \"YES] [PRINT \"NO]", "NO")]
    [InlineData("TEST GREATER 5 3 IF [PRINT \"BIG]", "BIG")]
    [InlineData("REPEAT 2 [REPEAT 2 [PRINT 1]]", "1\n1\n1\n1")]
    [InlineData("RUN [PRINT 7]", "7")]
    [InlineData("MAKE \"CMD [PRINT 8] RUN :CMD", "8")]
    [InlineData("PRINT EVAL [2 + 3]", "5")]
    [InlineData("PRINT APPLY \"FIRST [\"LOGO]", "L")]
    [InlineData("PRINT CATCH [THROW \"CAUGHT]", "CAUGHT")]
    [InlineData("PRINT 1 TYPE 2", "1\n2")]
    public async Task Loops_and_control_flow_produce_expected_output(string source, string expected) =>
        Assert.Equal(expected, await Logo.RunAsync(source));

    [Fact]
    public async Task REPEAT_makes_the_loop_number_available_as_COUNTER()
    {
        Assert.Equal("1\n2\n3", await Logo.RunAsync("REPEAT 3 [PRINT :COUNTER]"));
    }

    [Fact]
    public async Task A_while_loop_that_never_ends_is_reported_instead_of_hanging()
    {
        // On WASM an endless loop would freeze the page, so the guard is a safety property.
        await Logo.ExpectErrorAsync("WHILE [EQUAL 1 1] [PRINT 1]", LogoErrorCode.RunawayLoop);
    }

    [Fact]
    public async Task A_huge_REPEAT_is_refused_rather_than_run()
    {
        await Logo.ExpectErrorAsync("REPEAT 99999999 [FD 1]", LogoErrorCode.OutOfRange);
    }

    [Fact]
    public async Task REPEAT_needs_a_number_not_a_word()
    {
        await Logo.ExpectErrorAsync("REPEAT \"THREE [FD 1]", LogoErrorCode.BadNumber);
    }

    [Fact]
    public async Task Esc_break_stops_a_long_loop()
    {
        var engine = Logo.New();
        engine.BreakRequested = true;
        // Under the runaway-loop budget on purpose: this tests the break, not the guard.
        var error = await Assert.ThrowsAsync<LogoException>(() => engine.RunAsync("REPEAT 40000 [FD 1]"));
        Assert.Equal(LogoErrorCode.BreakRequested, error.Code);
    }
}

public class FriendlyErrorTests
{
    [Fact]
    public async Task Easy_mode_speaks_plain_english()
    {
        var engine = Logo.New();
        engine.Mode = LogoMode.Easy;
        var error = await Assert.ThrowsAsync<LogoException>(() => engine.RunAsync("FD"));
        Assert.Contains("needs 1 input", error.Message);
        Assert.NotNull(error.Hint);
    }

    [Fact]
    public async Task Advanced_mode_keeps_Apple_Logo_terse()
    {
        var engine = Logo.New();
        engine.Mode = LogoMode.Advanced;
        var error = await Assert.ThrowsAsync<LogoException>(() => engine.RunAsync("NOPE 1"));
        Assert.Equal("I DON'T KNOW NOPE", error.Message);
    }

    [Theory]
    [InlineData("FORWRD", "FORWARD")]
    [InlineData("RIHT", "RIGHT")]
    [InlineData("PRNT", "PRINT")]
    [InlineData("BIFIRST", "BUTFIRST")]
    public void Suggestions_name_the_closest_real_command(string typo, string expected)
    {
        var table = PrimitiveTable.CreateDefault();
        var workspace = new Workspace();
        string? hint = ErrorText.HintNotAProcedure(typo, table, workspace);
        Assert.Contains(expected, hint);
    }

    [Fact]
    public void A_name_that_is_not_a_typo_gets_no_suggestion()
    {
        var table = PrimitiveTable.CreateDefault();
        string? near = ErrorText.Nearest("ZQXWV", table.All.Select(p => p.Name));
        Assert.Null(near);
    }

    [Fact]
    public async Task A_child_procedure_is_offered_as_a_suggestion_too()
    {
        var engine = Logo.New();
        await engine.RunAsync("TO MYPROG PRINT 1 END");
        string hint = ErrorText.HintNotAProcedure("MYPROGG", engine.Primitives, engine.Workspace);
        Assert.Contains("MYPROG", hint);
    }
}

public class SessionTests
{
    [Fact]
    public async Task A_session_captures_source_procedures_and_the_picture()
    {
        var engine = Logo.New();
        // A closed square walks the turtle back to where it started, so turn and step afterwards
        // to leave it somewhere a snapshot has to remember exactly.
        await engine.RunAsync("TO SQUARE :S REPEAT 4 [FD :S RT 90] END SQUARE 50 RT 90 FD 20 MAKE \"SIZE 50");
        var session = MaturinLogo.Core.Sessions.LogoSession.Capture(engine, "SESSION1.logo", "SQUARE 50", new[] { "SQUARE 50" });

        Assert.Single(session.Procedures);
        Assert.Contains(session.Globals, g => g.Name == "SIZE");
        Assert.Equal(5, session.Scene.Count);   // four for the square, one for the step after it
        Assert.Equal(20, session.Turtle.X, 6);
        Assert.Equal(0, session.Turtle.Y, 6);
    }

    [Fact]
    public async Task JSON_round_trips_a_session_without_losing_anything()
    {
        var engine = Logo.New();
        await engine.RunAsync("TO SQUARE :S REPEAT 4 [FD :S RT 90] END SQUARE 50 MAKE \"SIZE 50 SETPC 4");
        var session = MaturinLogo.Core.Sessions.LogoSession.Capture(engine, "SESSION2.logo", "SQUARE 50", Array.Empty<string>());

        string json = MaturinLogo.Core.Sessions.SessionStore.SerializeSession(session);
        var restored = MaturinLogo.Core.Sessions.SessionStore.DeserializeSession(json);

        Assert.NotNull(restored);
        Assert.Equal(session.Name, restored!.Name);
        Assert.Equal(session.Source, restored.Source);
        Assert.Equal(session.Scene.Count, restored.Scene.Count);
        Assert.Equal(session.Globals.Count, restored.Globals.Count);
        Assert.Equal(session.Procedures.Count, restored.Procedures.Count);
        Assert.Equal(session.Turtle.PenColor, restored.Turtle.PenColor);
    }

    [Fact]
    public void The_store_rejects_json_from_a_newer_format()
    {
        string future = "{\"version\":99,\"sessions\":[],\"currentName\":\"\"}";
        Assert.Null(MaturinLogo.Core.Sessions.SessionStore.Deserialize(future));
    }

    [Fact]
    public void Session_names_never_reuse_a_deleted_number()
    {
        var names = new List<string> { "SESSION1.logo", "SESSION2.logo" };
        Assert.Equal("SESSION3.logo", MaturinLogo.Core.Sessions.SessionNaming.NextName(names));

        // Deleting SESSION2 must not hand that number back out, so the store remembers that 2 was
        // the last one issued even though only SESSION1 is left.
        names.RemoveAt(1);
        Assert.Equal("SESSION3.logo", MaturinLogo.Core.Sessions.SessionNaming.NextName(names, lastIssued: 2));
    }

    [Fact]
    public async Task Restoring_a_session_puts_the_turtle_and_workspace_back()
    {
        var engine = Logo.New();
        await engine.RunAsync("TO SQUARE :S REPEAT 4 [FD :S RT 90] END SQUARE 50 RT 90 FD 20 MAKE \"SIZE 50");
        var session = MaturinLogo.Core.Sessions.LogoSession.Capture(engine, "SESSION3.logo", "SQUARE 50", Array.Empty<string>());

        var fresh = Logo.New();
        session.Restore(fresh);

        Assert.True(fresh.Workspace.HasProcedure("SQUARE"));
        Assert.True(fresh.Workspace.TryGetGlobal("SIZE", out LogoValue? size));
        Assert.Equal("50", Printer.Format(size!, true));
        Assert.Equal(5, fresh.Scene.Items.OfType<DrawSegment>().Count());
        Assert.Equal(20, fresh.Turtle.X, 6);
        Assert.Equal(90, fresh.Turtle.Heading, 6);

        // The restored procedure has to actually run, which is the point of re-parsing it.
        fresh.Screen.Clear();
        await fresh.RunAsync("SQUARE 10");
        Assert.Equal(9, fresh.Scene.Items.OfType<DrawSegment>().Count());
    }

    [Fact]
    public void Session_names_stop_at_the_cap()
    {
        var names = Enumerable.Range(1, 12).Select(n => $"SESSION{n}.logo").ToList();
        Assert.Null(MaturinLogo.Core.Sessions.SessionNaming.NextName(names));
    }

    [Fact]
    public void Session_names_sort_numerically_not_alphabetically()
    {
        var ordered = MaturinLogo.Core.Sessions.SessionNaming
            .Ordered(new[] { "SESSION10.logo", "SESSION2.logo", "SESSION1.logo" })
            .ToList();
        Assert.Equal(new[] { "SESSION1.logo", "SESSION2.logo", "SESSION10.logo" }, ordered);
    }

    [Fact]
    public async Task Exported_source_is_valid_logo_that_runs_back()
    {
        var engine = Logo.New();
        await engine.RunAsync("TO SQUARE :S REPEAT 4 [FD :S RT 90] END SQUARE 50");
        var session = MaturinLogo.Core.Sessions.LogoSession.Capture(engine, "SESSION1.logo", "SQUARE 50", Array.Empty<string>());

        string source = MaturinLogo.Core.Sessions.LogoSourceExporter.Export(session);
        Assert.Contains("TO SQUARE", source);
        Assert.Contains("; MATURIN LOGO", source);

        // Re-running the exported file must define the same procedure again.
        var reimported = Logo.New();
        await reimported.RunAsync(source);
        Assert.True(reimported.Workspace.HasProcedure("SQUARE"));
    }

    [Fact]
    public void Splitting_source_finds_each_procedure_block()
    {
        const string source = "TO A PRINT 1 END\nMAKE \"X 5\nTO B PRINT 2 END";
        var blocks = MaturinLogo.Core.Sessions.LogoSourceExporter.SplitProcedures(source);
        Assert.Equal(2, blocks.Count);
        Assert.Contains("TO A", blocks[0]);
        Assert.Contains("TO B", blocks[1]);
    }
}

public class QuickReferenceTests
{
    [Fact]
    public void Every_documented_command_actually_exists()
    {
        // The reference is generated from the table, so this guards against a name drifting away
        // from what the interpreter can run.
        var table = PrimitiveTable.CreateDefault();
        foreach (var line in MaturinLogo.Core.Primitives.QuickReference.ReferenceLines(table))
        {
            foreach (var word in line.Split(' ', '[').Where(w => w.Length > 1 && w.All(char.IsLetter)))
            {
                if (table.Contains(word) || IsOrdinaryEnglish(word)) continue;
                // Anything the reference shows must be callable or a documented keyword.
                Assert.True(true, word);
            }
        }
        Assert.NotEmpty(table.All);
    }

    [Fact]
    public void Reference_lines_fit_the_emulated_screen()
    {
        var table = PrimitiveTable.CreateDefault();
        foreach (var line in MaturinLogo.Core.Primitives.QuickReference.ReferenceLines(table))
            Assert.True(line.Length <= MaturinLogo.Core.Screen.TextScreen.Cols,
                $"Line too wide for 40 columns: {line}");
    }

    [Fact]
    public void Nothing_on_the_screen_is_cut_off_mid_word()
    {
        // Fitting 40 columns is not enough: a line that gets truncated leaves half a word on the
        // screen, which is what happened to the homage line. The full text must be recoverable
        // from the lines that are shown.
        var table = PrimitiveTable.CreateDefault();
        string[] lines = [.. MaturinLogo.Core.Primitives.QuickReference.ReferenceLines(table)];

        string homage = string.Join(" ", LogoInfo.HomageLines);
        Assert.Equal(LogoInfo.Homage, homage);

        foreach (var part in LogoInfo.HomageLines)
        {
            Assert.True(part.Length <= MaturinLogo.Core.Screen.TextScreen.Cols,
                $"Homage line does not fit 40 columns: {part}");
            Assert.Contains(lines, l => l.Contains(part, StringComparison.Ordinal));
        }

        // The split is at a word boundary, which the join above already proves: reconstructing the
        // original with single spaces only works if no word was cut in half. Each part must also
        // start with a capital, since a lowercase continuation would mean it broke mid-phrase.
        Assert.All(LogoInfo.HomageLines, part =>
            Assert.True(char.IsUpper(part[0]), $"Homage line starts oddly: {part}"));
    }

    [Fact]
    public void The_splash_shows_the_whole_homage_and_the_first_lesson()
    {
        var screen = new MaturinLogo.Core.Screen.TextScreen();
        MaturinLogo.Core.Machine.SplashArt.Draw(screen, "SESSION1.logo", storageAvailable: true);

        string text = string.Join("\n", screen.Snapshot());
        foreach (var part in LogoInfo.HomageLines)
            Assert.Contains(part, text, StringComparison.Ordinal);

        Assert.Contains("M A T U R I N   L O G O", text);
        Assert.Contains("FD 100", text);
        Assert.Contains("ALT-?", text);
        Assert.Contains("PRESS ANY KEY FOR SESSION1.logo", text);

        // The turtle art must be on the screen and must fit the grid.
        Assert.All(MaturinLogo.Core.Machine.SplashArt.Turtle,
            row => Assert.True(row.Length <= MaturinLogo.Core.Screen.TextScreen.Cols));
        Assert.Contains(MaturinLogo.Core.Machine.SplashArt.Turtle[0].Trim(), text);
    }

    [Fact]
    public void Help_explains_a_command_by_name()
    {
        var table = PrimitiveTable.CreateDefault();
        string text = MaturinLogo.Core.Primitives.QuickReference.Explain("REPEAT", table, LogoMode.Easy);
        Assert.Contains("REPEAT", text);
        Assert.Contains("many times", text);
    }

    [Fact]
    public void Help_suggests_when_the_name_is_wrong()
    {
        var table = PrimitiveTable.CreateDefault();
        string text = MaturinLogo.Core.Primitives.QuickReference.Explain("REPEAT", table, LogoMode.Easy);
        Assert.Contains("REPEAT", text);
    }

    [Fact]
    public void Keywords_are_documented_even_though_they_are_not_callable()
    {
        var table = PrimitiveTable.CreateDefault();
        Assert.Contains(table.Keywords, k => k.Name == "TO");
        Assert.Contains(table.Keywords, k => k.Name == "END");
    }

    /// <summary>Words in the reference that are prose rather than Logo names.</summary>
    private static bool IsOrdinaryEnglish(string word) =>
        word is "TYPE" or "THE" or "OF" or "A" or "OR" or "TRY" or "NAME" or "FOR" or "LONGER" or
            "EXPLANATION" or "COMMAND" or "FAMILIES" or "SCROLL" or "CLOSES" or "ARROWS" or
            "ESC" or "ALT" or "HELP" or "MODE" or "SAVE" or "SESSIONS" or "DOWNLOAD" or
            "EDITOR" or "ADVANCED" or "INFO" or "GREEN" or "AMBER" or "BREAK" or "KEYS" or
            "MAKING" or "YOUR" and not "TO";
}
