using System.Text;
using MaturinLogo.Core.Interpreter;
using MaturinLogo.Core.Reader;
using MaturinLogo.Core.Values;

namespace MaturinLogo.Core.Primitives;

/// <summary>
/// Word (string) commands. Logo glues words together rather than having a string type, so FIRST
/// and LAST work on both words and lists - which is why children pick it up so quickly.
/// </summary>
public static class WordPrimitives
{
    public static void Register(PrimitiveTable t)
    {
        t.Add("WORD", -1, "Join words together into one word.", "PRINT WORD \"HELLO \"THERE",
            CommandFamily.Words, (e, a, _) =>
            {
                var sb = new StringBuilder();
                foreach (var v in a) sb.Append(v.AsWordText());
                return Args.Word(sb.ToString());
            }, isReporter: true, variadic: true);

        t.Add("FIRST", 1, "The first letter of a word, or the first item of a list.", "PRINT FIRST \"LOGO",
            CommandFamily.Words, (e, a, _) => First(a[0], "FIRST", e.Mode), isReporter: true,
            aliases: new[] { "F" });

        t.Add("BUTFIRST", 1, "Everything except the first letter of a word, or the first item of a list.", "PRINT BUTFIRST \"LOGO",
            CommandFamily.Words, (e, a, _) => ButFirst(a[0], "BUTFIRST", e.Mode), isReporter: true,
            aliases: new[] { "BF" });

        t.Add("LAST", 1, "The last letter of a word, or the last item of a list.", "PRINT LAST \"LOGO",
            CommandFamily.Words, (e, a, _) =>
            {
                var value = a[0];
                if (value.Kind == LogoKind.List)
                    return value.Items.Count == 0
                        ? EmptyError("LAST", e.Mode)
                        : Args.Result(value.Items[^1]);
                string word = value.AsWordText();
                return word.Length == 0
                    ? EmptyError("LAST", e.Mode)
                    : Args.Word(word[^1].ToString());
            }, isReporter: true, aliases: new[] { "L" });

        t.Add("BUTLAST", 1, "Everything except the last letter of a word, or the last item of a list.", "PRINT BUTLAST \"LOGO",
            CommandFamily.Words, (e, a, _) =>
            {
                var value = a[0];
                if (value.Kind == LogoKind.List)
                    return value.Items.Count == 0
                        ? EmptyError("BUTLAST", e.Mode)
                        : Args.Result(LogoValue.FromList(value.Items.Take(value.Items.Count - 1).ToList()));
                string word = value.AsWordText();
                return word.Length == 0
                    ? EmptyError("BUTLAST", e.Mode)
                    : Args.Word(word.Substring(0, word.Length - 1));
            }, isReporter: true, aliases: new[] { "BL" });

        t.Add("SENTENCE", -1, "Join lists (or words) into one list.", "PRINT SE [1 2] [3]",
            CommandFamily.Words, (e, a, _) =>
            {
                var joined = new List<LogoValue>();
                foreach (var v in a)
                {
                    if (v.Kind == LogoKind.List) joined.AddRange(v.Items);
                    else joined.Add(v);
                }
                return Args.Result(LogoValue.FromList(joined));
            }, isReporter: true, variadic: true, aliases: new[] { "SE" });

        t.Add("LIST", -1, "Make a list out of its inputs, keeping lists as single items.", "PRINT LIST [1 2] 3",
            CommandFamily.Lists, (e, a, _) => Args.Result(LogoValue.FromList(a.ToList())),
            isReporter: true, variadic: true);

        // Logo's signature is ITEM index list, so the number comes first.
        t.Add("ITEM", 2, "The item at a position in a list, counting from 1.", "PRINT ITEM 2 [RED GREEN BLUE]",
            CommandFamily.Lists, (e, a, _) =>
            {
                var items = Args.List(a, 1, "ITEM", e.Mode);
                int index = Args.Index(a, 0, items.Count, "ITEM", e.Mode);
                return Args.Result(items[index]);
            }, isReporter: true);

        t.Add("SETITEM", 3, "Replace one item of a list and report the new list.", "MAKE \"COLORS SETITEM 2 :COLORS \"TEAL",
            CommandFamily.Lists, (e, a, _) =>
            {
                var items = new List<LogoValue>(Args.List(a, 1, "SETITEM", e.Mode));
                int index = Args.Index(a, 0, items.Count, "SETITEM", e.Mode);
                items[index] = a[2];
                return Args.Result(LogoValue.FromList(items));
            }, isReporter: true);

        t.Add("MEMBER", 2, "Report TRUE when the first input appears inside the second.", "PRINT MEMBER \"CAT \"SCATTER",
            CommandFamily.Words, (e, a, _) =>
            {
                string needle = a[0].AsWordText();
                var haystack = a[1];
                if (haystack.Kind == LogoKind.List)
                    return Args.Flag(haystack.Items.Any(i => i.AsWordText() == needle));
                return Args.Flag(haystack.AsWordText().Contains(needle, StringComparison.Ordinal));
            }, isReporter: true, aliases: new[] { "MEMBERP" });

        t.Add("EQUAL", 2, "Report TRUE when two things are exactly the same.", "PRINT EQUAL 2 + 2 4",
            CommandFamily.Logic, (e, a, _) => Args.Flag(a[0].Equals(a[1])), isReporter: true,
            aliases: new[] { "EQUALP", "EQ" });

        t.Add("NOT", 1, "Flip TRUE into FALSE and FALSE into TRUE.", "PRINT NOT EQUAL 3 4",
            CommandFamily.Logic, (e, a, _) => Args.Flag(!Args.Bool(a, 0, "NOT", e.Mode)), isReporter: true);

        t.Add("AND", 2, "TRUE only when both inputs are TRUE.", "PRINT AND GREATER 5 3 LESS 5 9",
            CommandFamily.Logic, (e, a, _) => Args.Flag(Args.Bool(a, 0, "AND", e.Mode) && Args.Bool(a, 1, "AND", e.Mode)), isReporter: true);

        t.Add("OR", 2, "TRUE when either input is TRUE.", "PRINT OR EQUAL 1 1 EQUAL 2 3",
            CommandFamily.Logic, (e, a, _) => Args.Flag(Args.Bool(a, 0, "OR", e.Mode) || Args.Bool(a, 1, "OR", e.Mode)), isReporter: true);

        // LESSP/GREATERP only. "LT" and "GT" belong to the turtle: they are turn-left and turn-right,
        // and giving them to a comparison would silently break every drawing program.
        t.Add("LESS", 2, "Report TRUE when the first number is smaller.", "PRINT LESS 3 5",
            CommandFamily.Logic, (e, a, _) => Args.Flag(Args.Number(a, 0, "LESS", e.Mode) < Args.Number(a, 1, "LESS", e.Mode)),
            isReporter: true, aliases: new[] { "LESSP" });

        t.Add("GREATER", 2, "Report TRUE when the first number is bigger.", "PRINT GREATER 3 5",
            CommandFamily.Logic, (e, a, _) => Args.Flag(Args.Number(a, 0, "GREATER", e.Mode) > Args.Number(a, 1, "GREATER", e.Mode)),
            isReporter: true, aliases: new[] { "GREATERP" });

        t.Add("EQUIV", 2, "Report TRUE when two numbers are equal.", "PRINT EQUIV 4 2 + 2",
            CommandFamily.Logic, (e, a, _) => Args.Flag(Args.Number(a, 0, "EQUIV", e.Mode).Equals(Args.Number(a, 1, "EQUIV", e.Mode))),
            isReporter: true, aliases: new[] { "EQUIVP" });

        t.Add("WORDP", 1, "Report TRUE when the input is a word.", "PRINT WORDP \"HELLO",
            CommandFamily.Words, (e, a, _) => Args.Flag(a[0].Kind == LogoKind.Word), isReporter: true,
            aliases: new[] { "ISWORD" });

        t.Add("LISTP", 1, "Report TRUE when the input is a list.", "PRINT LISTP [1 2]",
            CommandFamily.Lists, (e, a, _) => Args.Flag(a[0].Kind == LogoKind.List), isReporter: true);

        t.Add("EMPTY", 1, "Report TRUE when a word or list has nothing in it.", "PRINT EMPTY []",
            CommandFamily.Logic, (e, a, _) => Args.Flag(a[0].IsEmpty), isReporter: true, aliases: new[] { "EMPTYP" });

        t.Add("COUNT", 1, "How many letters a word has, or how many items a list has.", "PRINT COUNT [1 2 3]",
            CommandFamily.Lists, (e, a, _) =>
            {
                var value = a[0];
                return Args.Number(value.Kind switch
                {
                    LogoKind.List => value.Items.Count,
                    LogoKind.Word => value.Word.Length,
                    _ => 0,
                });
            }, isReporter: true);

        t.Add("REVERSE", 1, "Flip a list or word end-to-front.", "PRINT REVERSE [1 2 3]",
            CommandFamily.Lists, (e, a, _) =>
            {
                var value = a[0];
                if (value.Kind == LogoKind.List)
                {
                    var reversed = value.Items.ToList();
                    reversed.Reverse();
                    return Args.Result(LogoValue.FromList(reversed));
                }
                char[] chars = value.AsWordText().ToCharArray();
                Array.Reverse(chars);
                return Args.Word(new string(chars));
            }, isReporter: true, aliases: new[] { "REV" });

        t.Add("SUBSTRING", 3, "Part of a word: the word, where to start counting from 1, and how many letters.", "PRINT SUBSTRING \"LOGO 2 2",
            CommandFamily.Words, (e, a, _) =>
            {
                string word = Args.Word(a, 0, "SUBSTRING", e.Mode);
                int start = (int)Math.Round(Args.Number(a, 1, "SUBSTRING", e.Mode));
                int length = (int)Math.Round(Args.Number(a, 2, "SUBSTRING", e.Mode));
                if (start < 1 || length < 0 || start + length - 1 > word.Length)
                    throw new LogoException(LogoErrorCode.BadItemIndex, ErrorText.OutOfRange("SUBSTRING", e.Mode),
                        $"\"{word}\" has {word.Length} letters, so try something like SUBSTRING \"{word}\" 1 2.",
                        subject: "SUBSTRING");
                return Args.Word(word.Substring(start - 1, length));
            }, isReporter: true, aliases: new[] { "SUBSTR" });

        t.Add("EXPLODE", 1, "Break a word into a list of its letters.", "PRINT EXPLODE \"LOGO",
            CommandFamily.Words, (e, a, _) =>
            {
                string word = a[0].AsWordText();
                var letters = new List<LogoValue>(word.Length);
                foreach (char c in word) letters.Add(LogoValue.FromWord(c.ToString()));
                return Args.Result(LogoValue.FromList(letters));
            }, isReporter: true);

        t.Add("IMPLODE", 1, "Join a list of letters back into one word.", "PRINT IMPLODE [L O G O]",
            CommandFamily.Words, (e, a, _) =>
            {
                var items = Args.List(a, 0, "IMPLODE", e.Mode);
                var sb = new StringBuilder();
                foreach (var item in items) sb.Append(item.AsWordText());
                return Args.Word(sb.ToString());
            }, isReporter: true);

        t.Add("UPPERCASE", 1, "Turn a word into big letters.", "PRINT UPPERCASE \"logo",
            CommandFamily.Words, (e, a, _) => Args.Word(a[0].AsWordText().ToUpperInvariant()),
            isReporter: true, aliases: new[] { "UC" });

        t.Add("LOWERCASE", 1, "Turn a word into small letters.", "PRINT LOWERCASE \"LOGO",
            CommandFamily.Words, (e, a, _) => Args.Word(a[0].AsWordText().ToLowerInvariant()),
            isReporter: true, aliases: new[] { "LC" });

        // Apple Logo uses TEXT for the screen, so the procedure-source reporter goes by its
        // Terrapin name; TEXT is registered in ScreenPrimitives.
        t.Add("PROCTEXT", 1, "Report the source of a procedure so it can be printed or saved.", "PRINT PROCTEXT \"SQUARE",
            CommandFamily.System, (e, a, _) =>
            {
                string name = Args.Name(a, 0, "TEXT", e.Mode);
                if (!e.Workspace.TryGetProcedure(name, out UserProcedure? proc))
                    throw new LogoException(LogoErrorCode.NotDefined, ErrorText.NotAProcedure(name, e.Mode),
                        $"TO {name} ... END first, then TEXT :{name} will show it.", subject: name);
                return Args.Word(ProcedureText.Format(proc!));
            }, isReporter: true);
    }

    private static Task<LogoValue> First(LogoValue value, string name, LogoMode mode)
    {
        if (value.Kind == LogoKind.List)
            return value.Items.Count == 0 ? EmptyError(name, mode) : Args.Result(value.Items[0]);
        string word = value.AsWordText();
        return word.Length == 0 ? EmptyError(name, mode) : Args.Word(word.Substring(0, 1));
    }

    private static Task<LogoValue> ButFirst(LogoValue value, string name, LogoMode mode)
    {
        if (value.Kind == LogoKind.List)
        {
            if (value.Items.Count == 0) return EmptyError(name, mode);
            return Args.Result(LogoValue.FromList(value.Items.Skip(1).ToList()));
        }
        string word = value.AsWordText();
        if (word.Length == 0) return EmptyError(name, mode);
        return Args.Word(word.Substring(1));
    }

    private static Task<LogoValue> EmptyError(string name, LogoMode mode) =>
        throw new LogoException(LogoErrorCode.BadItemIndex,
            ErrorText.OutOfRange(name, mode),
            $"{name} needs something with at least one letter or item in it.", subject: name);
}
