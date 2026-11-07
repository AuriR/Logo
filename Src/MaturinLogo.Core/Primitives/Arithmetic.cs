using MaturinLogo.Core.Interpreter;
using MaturinLogo.Core.Values;

namespace MaturinLogo.Core.Primitives;

/// <summary>
/// The arithmetic behind Logo's infix operators, kept here so the engine and the primitive table
/// share one definition of what <c>+</c> means. Logo has no operator precedence: everything runs
/// left to right, which is why <c>2 + 3 * 4</c> is 20 and not 14.
/// </summary>
public static class Arithmetic
{
    public static LogoValue Apply(string op, LogoValue left, LogoValue right, LogoMode mode)
    {
        double a = AsNumber(left, op, mode);
        double b = AsNumber(right, op, mode);

        switch (op)
        {
            case "+":
                return LogoValue.FromNumber(a + b);
            case "-":
                return LogoValue.FromNumber(a - b);
            case "*":
                return LogoValue.FromNumber(a * b);
            case "/":
                if (b == 0)
                    throw new LogoException(LogoErrorCode.DivisionByZero, ErrorText.DivisionByZero(mode),
                        "Nothing can be divided by zero. Try a number like 2 or 10.", subject: "/");
                return LogoValue.FromNumber(a / b);
            default:
                throw new LogoException(LogoErrorCode.Internal, $"I don't know the operator {op}");
        }
    }

    /// <summary>Prefix MINUS, which Logo also allows as a negative sign in front of a value.</summary>
    public static LogoValue Negate(LogoValue value, LogoMode mode) =>
        LogoValue.FromNumber(-AsNumber(value, "MINUS", mode));

    private static double AsNumber(LogoValue value, string op, LogoMode mode)
    {
        if (value.Kind == LogoKind.Number) return value.Number;
        // Kids type "10" as a word constantly, and Logo was forgiving about numeric words.
        if (value.Kind == LogoKind.Word && TryNumericWord(value.Word, out double parsed)) return parsed;
        throw new LogoException(LogoErrorCode.BadInputType,
            ErrorText.TypeMismatch(op, "a number", mode),
            $"{op} only works with numbers, and {Printer.Format(value, true)} is not one.", subject: op);
    }

    private static bool TryNumericWord(string word, out double value)
    {
        value = 0;
        if (word.Length == 0) return false;
        if (word[0] == '#') return Reader.LogoReader.TryNumber(word.ToUpperInvariant(), out value);
        return double.TryParse(word, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}
