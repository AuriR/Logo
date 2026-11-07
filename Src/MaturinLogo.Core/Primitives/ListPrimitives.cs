using MaturinLogo.Core.Interpreter;
using MaturinLogo.Core.Values;

namespace MaturinLogo.Core.Primitives;

/// <summary>
/// List commands that work on whole lists. Logo's list operations never change the original -
/// they report a new list - which is why the pattern is MAKE :X LPUT 1 :X.
/// </summary>
public static class ListPrimitives
{
    public static void Register(PrimitiveTable t)
    {
        t.Add("LPUT", 2, "Add an item to the front of a list.", "PRINT LPUT 1 [2 3]",
            CommandFamily.Lists, (e, a, _) =>
            {
                var items = new List<LogoValue> { a[0] };
                items.AddRange(Args.List(a, 1, "LPUT", e.Mode));
                return Args.Result(LogoValue.FromList(items));
            }, isReporter: true);

        t.Add("FPUT", 2, "Add an item to the end of a list.", "PRINT FPUT 4 [1 2 3]",
            CommandFamily.Lists, (e, a, _) =>
            {
                var items = new List<LogoValue>(Args.List(a, 1, "FPUT", e.Mode));
                items.Add(a[0]);
                return Args.Result(LogoValue.FromList(items));
            }, isReporter: true);

        t.Add("DELETE", 2, "Remove an item from a list at a position counting from 1.", "PRINT DELETE 2 [1 2 3]",
            CommandFamily.Lists, (e, a, _) =>
            {
                var items = new List<LogoValue>(Args.List(a, 1, "DELETE", e.Mode));
                int index = Args.Index(new[] { a[0] }, 0, items.Count, "DELETE", e.Mode);
                items.RemoveAt(index);
                return Args.Result(LogoValue.FromList(items));
            }, isReporter: true);

        t.Add("SUBSTITUTE", 3, "Replace every copy of an item in a list with another.", "PRINT SUBSTITUTE 2 \"X [1 2 2 3]",
            CommandFamily.Lists, (e, a, _) =>
            {
                var items = new List<LogoValue>(Args.List(a, 2, "SUBSTITUTE", e.Mode));
                for (int i = 0; i < items.Count; i++)
                    if (items[i].Equals(a[0])) items[i] = a[1];
                return Args.Result(LogoValue.FromList(items));
            }, isReporter: true);

        t.Add("CAR", 1, "The first item of a list (the classic Lisp name Logo kept).", "PRINT CAR [1 2 3]",
            CommandFamily.Lists, (e, a, _) =>
            {
                var items = Args.List(a, 0, "CAR", e.Mode);
                if (items.Count == 0)
                    throw new LogoException(LogoErrorCode.BadItemIndex, "CAR of an empty list",
                        "CAR needs a list with at least one item.", subject: "CAR");
                return Args.Result(items[0]);
            }, isReporter: true);

        t.Add("CDR", 1, "A list without its first item.", "PRINT CDR [1 2 3]",
            CommandFamily.Lists, (e, a, _) =>
            {
                var items = Args.List(a, 0, "CDR", e.Mode);
                if (items.Count == 0)
                    throw new LogoException(LogoErrorCode.BadItemIndex, "CDR of an empty list",
                        "CDR needs a list with at least one item.", subject: "CDR");
                return Args.Result(LogoValue.FromList(items.Skip(1).ToList()));
            }, isReporter: true);

        t.Add("ASSOC", 2, "Look a word up in a list of pairs, like a tiny dictionary.", "PRINT ASSOC \"RED [[RED 1] [BLUE 5]]",
            CommandFamily.Lists, (e, a, _) =>
            {
                var pairs = Args.List(a, 1, "ASSOC", e.Mode);
                foreach (var pair in pairs)
                {
                    if (pair.Kind == LogoKind.List && pair.Items.Count >= 2 && pair.Items[0].AsWordText() == a[0].AsWordText())
                        return Args.Result(pair.Items[1]);
                }
                return Args.Result(LogoValue.None);
            }, isReporter: true);

        t.Add("EVAL", 1, "Work out a list as if it were typed, then run it.", "PRINT EVAL [2 + 3]",
            CommandFamily.Control, (e, a, _) => EvalListAsync(e, a[0]), isReporter: true);

        t.Add("APPLY", 2, "Run a procedure with a list of inputs.", "PRINT APPLY \"MAXIMUM [3 9]",
            CommandFamily.Control, async (e, a, env) =>
            {
                string name = Args.Name(a, 0, "APPLY", e.Mode);
                var inputs = Args.List(a, 1, "APPLY", e.Mode);
                if (e.Workspace.TryGetProcedure(name, out UserProcedure? proc) && proc is not null)
                    return await e.InvokeUserAsync(proc, inputs, env);
                if (e.Primitives.TryGet(name, out Primitive? primitive) && primitive is not null)
                    return await e.InvokePrimitiveDirectAsync(primitive, inputs, env);
                throw new LogoException(LogoErrorCode.NotAProcedure, ErrorText.NotAProcedure(name, e.Mode),
                    $"APPLY needs a procedure that exists, like APPLY \"FD [10].", subject: name);
            }, isReporter: true);

        t.Add("IDENTITY", 1, "Report a procedure's name as a word, for use with APPLY.", "PRINT IDENTITY \"SQUARE",
            CommandFamily.Control, (e, a, _) => Args.Word(a[0].AsWordText()), isReporter: true);
    }

    private static async Task<LogoValue> EvalListAsync(Engine e, LogoValue value)
    {
        if (value.Kind != LogoKind.List)
            throw new LogoException(LogoErrorCode.BadList, "EVAL needs a list",
                "Try EVAL [2 + 3] - the expression goes in brackets.", subject: "EVAL");

        // EVAL reads the list as an expression, so [2 + 3] reports 5 rather than running commands.
        var items = new List<Reader.ProgramItem>(value.Items.Count);
        foreach (var element in value.Items) items.Add(Engine.ValueToItem(element));
        var stream = new SourceStream(items);
        LogoValue result = LogoValue.None;
        while (!stream.AtEnd) result = await e.EvalAsync(stream, e.CurrentFrame);
        return result;
    }
}
