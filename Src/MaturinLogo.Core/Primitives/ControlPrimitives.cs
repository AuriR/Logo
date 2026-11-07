using MaturinLogo.Core.Interpreter;
using MaturinLogo.Core.Reader;
using MaturinLogo.Core.Values;

namespace MaturinLogo.Core.Primitives;

/// <summary>
/// Control flow: REPEAT, IF, FOR, WHILE, FOREACH, RUN, LOCAL and friends. Loops are syntactic -
/// they read their inputs as raw program items rather than having them evaluated first, because a
/// loop body has to be run again and again rather than worked out once.
/// </summary>
public static class ControlPrimitives
{
    public static void Register(PrimitiveTable t)
    {
        t.AddSyntactic("REPEAT", 2, "Run a list of commands that many times.", "REPEAT 4 [FD 50 RT 90]",
            CommandFamily.Control, async (e, stream, env) =>
            {
                var count = await e.EvalAsync(stream, env);
                var body = await TakeOneAsync(e, stream, env, "REPEAT");
                int times = CheckedCount(e, count, "REPEAT");
                RequireList(e, body, "REPEAT", "the commands to repeat");

                for (int i = 0; i < times; i++)
                {
                    e.CurrentLoopIndex = i + 1;
                    await e.RunListAsync(body, env, stream.InBody);
                    await e.StepForBreakAsync();
                }
                return LogoValue.None;
            });

        t.AddSyntactic("IF", 2, "Run the first list when the test is true, the second when it is not.", "IF GREATER :N 5 [PRINT \"BIG] [PRINT \"SMALL]",
            CommandFamily.Control, async (e, stream, env) =>
            {
                // IF has two shapes. "IF [PRINT 1] [PRINT 2]" reads the answer remembered by TEST,
                // so its first item is already a branch. "IF GREATER :N 5 [PRINT \"BIG] [...]"
                // reads a test expression first, then the two branches. The first item's shape tells
                // us which one we are in, which is exactly what Apple Logo did.
                var first = await TakeOneAsync(e, stream, env, "IF");

                bool condition;
                LogoValue trueBranch;
                if (first.Kind == LogoKind.List)
                {
                    condition = e.LastTest;
                    trueBranch = first;
                }
                else
                {
                    condition = first.IsTrue;
                    trueBranch = await TakeOneAsync(e, stream, env, "IF");
                }

                // The ELSE half is only taken when a list actually follows, so that
                // "IF :TEST [FD 10] RT 90" leaves the RT alone.
                LogoValue falseBranch = LogoValue.None;
                if (!stream.AtEnd && stream.Peek().Kind is ItemKind.List or ItemKind.Paren)
                    falseBranch = await TakeOneAsync(e, stream, env, "IF");

                var chosen = condition ? trueBranch : falseBranch;
                if (chosen.Kind == LogoKind.None) return LogoValue.None;
                if (chosen.Kind == LogoKind.List) return await e.RunListAsync(chosen, env, stream.InBody);
                return chosen;
            }, isReporter: true);

        t.Add("TEST", 1, "Remember whether something is true, for IF to act on later.", "TEST GREATER :N 10 IF [PRINT \"BIG]",
            CommandFamily.Control, (e, a, env) =>
            {
                e.LastTest = a[0].IsTrue;
                return Args.Done();
            });

        t.Add("IFELSE", 3, "Report the second input when the test is true, otherwise the third.", "PRINT IFELSE GREATER 5 3 \"YES \"NO",
            CommandFamily.Control, async (e, a, env) =>
            {
                var chosen = Args.Bool(a, 0, "IFELSE", e.Mode) ? a[1] : a[2];
                if (chosen.Kind == LogoKind.List) return await e.RunListAsync(chosen, env);
                return chosen;
            }, isReporter: true);

        t.AddSyntactic("FOR", 4, "Count a variable from a start to a stop, stepping by a number.", "FOR [I 1 10 1] [PRINT :I]",
            CommandFamily.Control, async (e, stream, env) =>
            {
                var spec = await TakeOneAsync(e, stream, env, "FOR");
                var body = await TakeOneAsync(e, stream, env, "FOR");

                if (spec.Kind != LogoKind.List || spec.Items.Count is < 3 or > 4)
                    throw new LogoException(LogoErrorCode.BadList, "FOR needs a list like [I 1 10 1]",
                        "FOR [name start stop step] - the step is optional, so try FOR [I 1 5] [PRINT :I].",
                        subject: "FOR");
                RequireList(e, body, "FOR", "the commands to repeat");

                string varName = spec.Items[0].AsWordText();
                double start = Args.Number(spec.Items, 1, "FOR", e.Mode);
                double stop = Args.Number(spec.Items, 2, "FOR", e.Mode);
                double step = spec.Items.Count == 4 ? Args.Number(spec.Items, 3, "FOR", e.Mode) : 1;

                if (step == 0)
                    throw new LogoException(LogoErrorCode.DivisionByZero, "FOR's step cannot be zero",
                        "The last number in FOR is how much to count by, like 1 or 2.", subject: "FOR");

                int passes = 0;
                for (double v = start; step > 0 ? v <= stop : v >= stop; v += step)
                {
                    e.MakeVariable(varName, LogoValue.FromNumber(v), env);
                    await e.RunListAsync(body, env, stream.InBody);
                    if (++passes > Engine.MaxLoopPasses) throw Runaway(e, "FOR");
                    await e.StepForBreakAsync();
                }
                return LogoValue.None;
            });

        t.AddSyntactic("WHILE", 2, "Keep running a list while the test stays true.", "WHILE [LESS :N 5] [MAKE \"N :N + 1]",
            CommandFamily.Control, (e, stream, env) => LoopAsync(e, stream, env, "WHILE", stopWhenFalse: true));

        t.AddSyntactic("UNTIL", 2, "Keep running a list until the test becomes true.", "UNTIL [EQUAL :N 5] [MAKE \"N :N + 1]",
            CommandFamily.Control, (e, stream, env) => LoopAsync(e, stream, env, "UNTIL", stopWhenFalse: false));

        t.AddSyntactic("FOREACH", 2, "Run a list once for each item, with ? holding the current item.", "FOREACH [1 2 3] [PRINT ?]",
            CommandFamily.Control, async (e, stream, env) =>
            {
                var collection = await e.EvalAsync(stream, env);
                var body = await TakeOneAsync(e, stream, env, "FOREACH");
                RequireList(e, body, "FOREACH", "the commands to repeat");

                var items = collection.Kind == LogoKind.List
                    ? collection.Items
                    : Args.List(new[] { collection }, 0, "FOREACH", e.Mode);

                LogoValue saved = e.LoopItem;
                foreach (var item in items)
                {
                    e.LoopItem = item;
                    await e.RunListAsync(body, env, stream.InBody);
                }
                e.LoopItem = saved;
                return LogoValue.None;
            });

        t.AddSyntactic("RUN", 1, "Run a list of commands, or a procedure name someone gave you.", "RUN [FD 50 RT 90]",
            CommandFamily.Control, async (e, stream, env) =>
            {
                var value = await e.EvalAsync(stream, env);
                return await e.RunListAsync(value, env, stream.InBody);
            }, isReporter: true);

        t.AddSyntactic("LOCAL", 1, "Give a procedure its own private variables.", "TO TRIAL :N LOCAL [TOTAL] MAKE \"TOTAL 0",
            CommandFamily.Control, async (e, stream, env) =>
            {
                var names = await e.EvalAsync(stream, env);
                if (names.Kind != LogoKind.List)
                    throw new LogoException(LogoErrorCode.BadList, "LOCAL needs a list of names",
                        "Try LOCAL [TOTAL COUNT] - the names go in brackets.", subject: "LOCAL");
                e.DeclareLocals(names.Items.Select(i => i.AsWordText()), env);
                return LogoValue.None;
            });

        t.Add("CATCH", 1, "Run a list, but let a THROW jump back out of it with a value.", "PRINT CATCH [IF EQUAL 1 1 [THROW \"CAUGHT]]",
            CommandFamily.Control, async (e, a, env) =>
            {
                e.ThrowDepth++;
                try
                {
                    return await e.RunListAsync(a[0], env);
                }
                catch (ThrowSignal throw_)
                {
                    return throw_.Value;
                }
                finally
                {
                    e.ThrowDepth--;
                }
            }, isReporter: true);

        t.Add("THROW", 1, "Jump back to the nearest CATCH, carrying a value with it.", "THROW \"DONE",
            CommandFamily.Control, (e, a, env) =>
            {
                if (e.ThrowDepth <= 0)
                    throw new LogoException(LogoErrorCode.Internal, "THROW found no CATCH to jump back to",
                        "THROW only works inside something started by CATCH.", subject: "THROW");
                throw new ThrowSignal(a[0]);
            });

        // The syntactic words the engine handles itself, documented so the quick reference is honest.
        t.AddKeyword("TO", "Start a new procedure: give it a name, its inputs, then the commands.", "TO SQUARE :SIZE REPEAT 4 [FD :SIZE RT 90] END", CommandFamily.Control);
        t.AddKeyword("END", "Finish the procedure that TO started.", "END", CommandFamily.Control);
        t.AddKeyword("STOP", "Finish a procedure right away without giving back a value.", "IF EQUAL :N 0 STOP", CommandFamily.Control);
        t.AddKeyword("OUTPUT", "Finish a procedure and give back a value.", "OUTPUT :SIZE * 2", CommandFamily.Control);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>WHILE and UNTIL share everything but the sense of the test.</summary>
    private static async Task<LogoValue> LoopAsync(Engine e, SourceStream stream, Frame env, string name, bool stopWhenFalse)
    {
        var condition = await TakeOneAsync(e, stream, env, name);
        var body = await TakeOneAsync(e, stream, env, name);
        RequireList(e, condition, name, "the test");
        RequireList(e, body, name, "the commands to repeat");

        int passes = 0;
        while (true)
        {
            // The test is a list of program items like [LESS :N 5], so it is run as one expression.
            // Evaluating its words one at a time would call LESS with no inputs at all.
            LogoValue test = await e.EvalOneValueAsync(condition, env);

            if (!(stopWhenFalse ? test.IsTrue : !test.IsTrue)) break;

            await e.RunListAsync(body, env, stream.InBody);

            // A loop that never ends is the classic kid mistake, and on WASM it would hang the
            // page; this turns it into a message instead. StepForBreakAsync also watches for Esc.
            if (++passes > Engine.MaxLoopPasses)
                throw new LogoException(LogoErrorCode.RunawayLoop, ErrorText.Runaway(e.Mode),
                    stopWhenFalse
                        ? "WHILE kept going forever because its test never became false. Something inside the loop has to change what the test checks."
                        : "UNTIL kept going forever because its test never became true. Something inside the loop has to make the test come true.",
                    subject: name);
            await e.StepForBreakAsync();
        }
        return LogoValue.None;
    }

    private static LogoException Runaway(Engine e, string name) =>
        new(LogoErrorCode.RunawayLoop, ErrorText.Runaway(e.Mode),
            $"{name} counted far more times than anyone can watch. Try a smaller stop number.", subject: name);

    private static void RequireList(Engine e, LogoValue value, string name, string what)
    {
        if (value.Kind == LogoKind.List) return;
        throw new LogoException(LogoErrorCode.BadList,
            ErrorText.TypeMismatch(name, what, e.Mode),
            $"{name} needs {what} in brackets, like {ExampleFor(name)}.", subject: name);
    }

    /// <summary>
    /// Read one item from the stream without evaluating it first, which is what loops need: a
    /// bracketed list stays data so it can be run again, while anything else is worked out.
    /// </summary>
    private static async Task<LogoValue> TakeOneAsync(Engine e, SourceStream stream, Frame env, string name)
    {
        if (stream.AtEnd)
            throw new LogoException(LogoErrorCode.NotEnoughInputs,
                ErrorText.NotEnoughInputs(name, 2, 1, e.Mode), ExampleHint(name), subject: name);

        var item = stream.Items[stream.Index++];
        if (item.Kind is ItemKind.List or ItemKind.Paren) return LogoReader.ToLiteral(item);
        return await e.EvalAsync(stream, env, item);
    }

    private static string ExampleHint(string name) => name switch
    {
        "REPEAT" => "Try REPEAT 4 [FD 50 RT 90] - a number, then the commands in brackets.",
        "IF" => "Try IF GREATER 5 3 [PRINT \"BIG] [PRINT \"SMALL] - a test, then two lists.",
        "FOR" => "Try FOR [I 1 5] [PRINT :I].",
        "WHILE" => "Try WHILE [LESS :N 5] [MAKE \"N :N + 1].",
        "UNTIL" => "Try UNTIL [EQUAL :N 5] [MAKE \"N :N + 1].",
        "FOREACH" => "Try FOREACH [1 2 3] [PRINT ?].",
        _ => $"Type HELP {name} to see how {name} is used.",
    };

    private static string ExampleFor(string name) => name switch
    {
        "REPEAT" => "REPEAT 4 [FD 50 RT 90]",
        "IF" => "IF EQUAL 1 1 [PRINT \"YES] [PRINT \"NO]",
        "FOR" => "FOR [I 1 5] [PRINT :I]",
        "WHILE" => "WHILE [LESS :N 5] [PRINT :N]",
        "UNTIL" => "UNTIL [EQUAL :N 5] [PRINT :N]",
        "FOREACH" => "FOREACH [1 2] [PRINT ?]",
        _ => name,
    };

    private static int CheckedCount(Engine e, LogoValue count, string name)
    {
        if (count.Kind != LogoKind.Number)
            throw new LogoException(LogoErrorCode.BadNumber, ErrorText.TypeMismatch(name, "a number", e.Mode),
                $"{name} needs a number of times, like {name} 4 [...].", subject: name);
        int times = (int)Math.Round(count.Number);
        if (times < 0)
            throw new LogoException(LogoErrorCode.OutOfRange, ErrorText.OutOfRange(name, e.Mode),
                $"{name} cannot repeat a negative number of times.", subject: name);
        if (times > Engine.MaxLoopPasses)
            throw new LogoException(LogoErrorCode.OutOfRange, ErrorText.OutOfRange(name, e.Mode),
                $"{name} can repeat up to {Engine.MaxLoopPasses:N0} times, and {times} is more than that.", subject: name);
        return times;
    }
}

/// <summary>Internal control-flow signal: THROW jumped back to its CATCH.</summary>
public sealed class ThrowSignal : Exception
{
    public LogoValue Value { get; }
    public ThrowSignal(LogoValue value) : base("throw") => Value = value;
}
