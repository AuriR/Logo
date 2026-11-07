using MaturinLogo.Core.Interpreter;
using MaturinLogo.Core.Values;

namespace MaturinLogo.Core.Primitives;

/// <summary>
/// Input checking with a friendly voice. Every primitive asks for the type it needs here, so an
/// error message names the command, says what it wanted, and (in EASY mode) shows an example.
/// </summary>
public static class Args
{
    public static LogoValue Get(IReadOnlyList<LogoValue> args, int index, Primitive primitive)
    {
        if (index < args.Count) return args[index];
        throw new LogoException(LogoErrorCode.NotEnoughInputs,
            ErrorText.NotEnoughInputs(primitive.Name, primitive.Inputs, index, LogoMode.Easy),
            ErrorText.HintInputs(primitive), subject: primitive.Name);
    }

    public static double Number(IReadOnlyList<LogoValue> args, int index, string name, LogoMode mode)
    {
        var value = args[index];
        if (value.Kind == LogoKind.Number) return value.Number;
        // Logo was forgiving about numeric words, and kids type "10" by accident constantly.
        if (value.Kind == LogoKind.Word && TryParseWordNumber(value.Word, out double parsed)) return parsed;
        throw new LogoException(LogoErrorCode.BadInputType,
            ErrorText.TypeMismatch(name, "a number", mode),
            $"{name} needs a number like 5 or 2.5, not {Printer.Format(value, true)}.", subject: name);
    }

    private static bool TryParseWordNumber(string word, out double value)
    {
        value = 0;
        if (word.Length == 0) return false;
        if (word[0] == '#') return Reader.LogoReader.TryNumber(word.ToUpperInvariant(), out value);
        return double.TryParse(word, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    public static string Word(IReadOnlyList<LogoValue> args, int index, string name, LogoMode mode) =>
        args[index].Kind switch
        {
            LogoKind.Word => args[index].Word,
            LogoKind.Number => Printer.FormatNumber(args[index].Number),
            LogoKind.Boolean => args[index].Bool ? "TRUE" : "FALSE",
            LogoKind.Procedure => args[index].Word,
            _ => throw new LogoException(LogoErrorCode.BadInputType,
                ErrorText.TypeMismatch(name, "a word", mode),
                $"{name} needs a word, not {Printer.Format(args[index], true)}.", subject: name),
        };

    /// <summary>Accept a word and use it as a name (MAKE, THING, ERASE). Numbers are legal names in Logo.</summary>
    public static string Name(IReadOnlyList<LogoValue> args, int index, string name, LogoMode mode) =>
        args[index].Kind switch
        {
            LogoKind.Word or LogoKind.Number or LogoKind.Boolean or LogoKind.Procedure => args[index].AsWordText(),
            _ => throw new LogoException(LogoErrorCode.BadInputType,
                ErrorText.TypeMismatch(name, "a name", mode),
                $"{name} needs a name like {name.ToUpperInvariant()}SAMPLE, not a list.", subject: name),
        };

    public static IReadOnlyList<LogoValue> List(IReadOnlyList<LogoValue> args, int index, string name, LogoMode mode)
    {
        var value = args[index];
        if (value.Kind == LogoKind.List) return value.Items;
        // Logo treats a word as a one-item list in many places; being strict here confuses kids.
        if (value.Kind == LogoKind.Word)
            return new[] { LogoValue.FromWord(value.Word) };
        throw new LogoException(LogoErrorCode.BadList,
            ErrorText.TypeMismatch(name, "a list", mode),
            $"{name} needs a list in brackets, like [1 2 3].", subject: name);
    }

    public static LogoValue ListValue(IReadOnlyList<LogoValue> args, int index, string name, LogoMode mode)
    {
        var value = args[index];
        if (value.Kind == LogoKind.List) return value;
        if (value.Kind == LogoKind.Word) return LogoValue.FromList(new[] { LogoValue.FromWord(value.Word) });
        throw new LogoException(LogoErrorCode.BadList,
            ErrorText.TypeMismatch(name, "a list", mode),
            $"{name} needs a list in brackets, like [1 2 3].", subject: name);
    }

    public static bool Bool(IReadOnlyList<LogoValue> args, int index, string name, LogoMode mode)
    {
        var value = args[index];
        if (value.Kind == LogoKind.Boolean) return value.Bool;
        if (value.Kind == LogoKind.Word)
            return string.Equals(value.Word, "TRUE", StringComparison.OrdinalIgnoreCase);
        if (value.Kind == LogoKind.None) return false;
        return true;
    }

    /// <summary>A colour number or colour name resolved to a palette index.</summary>
    public static int Color(IReadOnlyList<LogoValue> args, int index, string name, LogoMode mode)
    {
        if (Turtle.ApplePalette.TryResolve(args[index], out int color)) return color;
        throw new LogoException(LogoErrorCode.OutOfRange,
            $"{name} needs a colour from 0 to 15",
            "0 is black, 1 green, 2 white, 3 purple, 4 orange, 5 blue. Type SETPENCOLOR 4 to try orange.",
            subject: name);
    }

    /// <summary>An index into a list, converted from Logo's 1-based counting.</summary>
    public static int Index(IReadOnlyList<LogoValue> args, int index, int count, string name, LogoMode mode)
    {
        double raw = Number(args, index, name, mode);
        int position = (int)Math.Round(raw);
        if (position < 1 || position > count)
            throw new LogoException(LogoErrorCode.BadItemIndex,
                ErrorText.OutOfRange($"{name}'s item number", mode),
            $"That list has {count} item{(count == 1 ? "" : "s")}, so the number must be between 1 and {count}.",
                subject: name);
        return position - 1;
    }

    public static Task<LogoValue> Done() => Task.FromResult(LogoValue.None);
    public static Task<LogoValue> Result(LogoValue value) => Task.FromResult(value);
    public static Task<LogoValue> Number(double value) => Task.FromResult(LogoValue.FromNumber(value));
    public static Task<LogoValue> Word(string value) => Task.FromResult(LogoValue.FromWord(value));
    public static Task<LogoValue> Flag(bool value) => Task.FromResult(LogoValue.FromBoolean(value));
}
