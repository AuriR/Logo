using MaturinLogo.Core.Interpreter;
using MaturinLogo.Core.Values;

namespace MaturinLogo.Core.Primitives;

/// <summary>
/// Arithmetic. Logo's + - * / are variadic (they keep taking numbers), while SUM, DIFFERENCE,
/// PRODUCT and QUOTIENT take exactly two - which is why PRINT 1 2 3 prints 6 but SUM 1 2 3 errors.
/// </summary>
public static class MathPrimitives
{
    public static void Register(PrimitiveTable t)
    {
        // + - * / are not ordinary primitives: Logo reads them as maths, between two values.
        // The engine handles them in Engine.ApplyInfixAsync, and Arithmetic does the maths.
        t.AddKeyword("+", "Add the numbers on either side.", "PRINT 2 + 3", CommandFamily.Math);
        t.AddKeyword("-", "Take the second number away from the first.", "PRINT 10 - 3", CommandFamily.Math);
        t.AddKeyword("*", "Multiply the numbers on either side.", "PRINT 6 * 7", CommandFamily.Math);
        t.AddKeyword("/", "Divide the first number by the second.", "PRINT 100 / 4", CommandFamily.Math);

        t.Add("SUM", 2, "Add two numbers.", "PRINT SUM 3 4",
            CommandFamily.Math, (e, a, _) => Args.Number(Args.Number(a, 0, "SUM", e.Mode) + Args.Number(a, 1, "SUM", e.Mode)), isReporter: true);

        t.Add("DIFFERENCE", 2, "Subtract the second number from the first.", "PRINT DIFFERENCE 10 4",
            CommandFamily.Math, (e, a, _) => Args.Number(Args.Number(a, 0, "DIFFERENCE", e.Mode) - Args.Number(a, 1, "DIFFERENCE", e.Mode)), isReporter: true);

        t.Add("PRODUCT", 2, "Multiply two numbers.", "PRINT PRODUCT 6 7",
            CommandFamily.Math, (e, a, _) => Args.Number(Args.Number(a, 0, "PRODUCT", e.Mode) * Args.Number(a, 1, "PRODUCT", e.Mode)), isReporter: true);

        t.Add("QUOTIENT", 2, "Divide the first number by the second.", "PRINT QUOTIENT 100 4",
            CommandFamily.Math, (e, a, _) =>
            {
                double divisor = Args.Number(a, 1, "QUOTIENT", e.Mode);
                if (divisor == 0)
                    throw new LogoException(LogoErrorCode.DivisionByZero, ErrorText.DivisionByZero(e.Mode),
                        "Nothing can be divided by zero.", subject: "QUOTIENT");
                return Args.Number(Args.Number(a, 0, "QUOTIENT", e.Mode) / divisor);
            }, isReporter: true);

        t.Add("REMAINDER", 2, "What is left over after dividing.", "PRINT REMAINDER 10 3",
            CommandFamily.Math, (e, a, _) =>
            {
                double divisor = Args.Number(a, 1, "REMAINDER", e.Mode);
                if (divisor == 0)
                    throw new LogoException(LogoErrorCode.DivisionByZero, ErrorText.DivisionByZero(e.Mode),
                        "REMAINDER needs a divisor that is not zero.", subject: "REMAINDER");
                return Args.Number(Args.Number(a, 0, "REMAINDER", e.Mode) % divisor);
            }, isReporter: true, aliases: new[] { "MOD", "MODULO" });

        t.Add("MINUS", 1, "Flip a number's sign.", "PRINT MINUS 5",
            CommandFamily.Math, (e, a, _) => Args.Number(-Args.Number(a, 0, "MINUS", e.Mode)), isReporter: true);

        t.Add("ABS", 1, "A number's size, ignoring whether it is negative.", "PRINT ABS -7",
            CommandFamily.Math, (e, a, _) => Args.Number(Math.Abs(Args.Number(a, 0, "ABS", e.Mode))), isReporter: true,
            aliases: new[] { "ABSOLUTE" });

        t.Add("SIGN", 1, "Report -1, 0 or 1 depending on whether the number is below, equal to, or above zero.", "PRINT SIGN -8",
            CommandFamily.Math, (e, a, _) => Args.Number(Math.Sign(Args.Number(a, 0, "SIGN", e.Mode))), isReporter: true);

        t.Add("ROUND", 1, "Round a number to the nearest whole number.", "PRINT ROUND 2.6",
            CommandFamily.Math, (e, a, _) => Args.Number(Math.Round(Args.Number(a, 0, "ROUND", e.Mode), MidpointRounding.AwayFromZero)), isReporter: true);

        t.Add("TRUNC", 1, "Cut off everything after the decimal point.", "PRINT TRUNC 2.9",
            CommandFamily.Math, (e, a, _) => Args.Number(Math.Truncate(Args.Number(a, 0, "TRUNC", e.Mode))), isReporter: true,
            aliases: new[] { "TRUNCATE" });

        t.Add("POWER", 2, "Raise the first number to the power of the second.", "PRINT POWER 2 10",
            CommandFamily.Math, (e, a, _) => Args.Number(Math.Pow(Args.Number(a, 0, "POWER", e.Mode), Args.Number(a, 1, "POWER", e.Mode))), isReporter: true);

        t.Add("SQRT", 1, "The square root of a number.", "PRINT SQRT 81",
            CommandFamily.Math, (e, a, _) =>
            {
                double value = Args.Number(a, 0, "SQRT", e.Mode);
                if (value < 0)
                    throw new LogoException(LogoErrorCode.OutOfRange, ErrorText.OutOfRange("SQRT", e.Mode),
                        "SQRT needs a number that is not negative.", subject: "SQRT");
                return Args.Number(Math.Sqrt(value));
            }, isReporter: true, aliases: new[] { "ROOT" });

        t.Add("RANDOM", 1, "Report a whole number from 0 up to (but not including) that number.", "PRINT RANDOM 6",
            CommandFamily.Math, (e, a, _) =>
            {
                double limit = Args.Number(a, 0, "RANDOM", e.Mode);
                if (limit <= 0)
                    throw new LogoException(LogoErrorCode.OutOfRange, ErrorText.OutOfRange("RANDOM", e.Mode),
                        "RANDOM needs a positive number, like RANDOM 6 for a dice roll.", subject: "RANDOM");
                return Args.Number(Math.Floor(e.Random.NextDouble() * limit));
            }, isReporter: true);

        t.Add("PI", 0, "The ratio of a circle's round to its straight across.", "PRINT PI",
            CommandFamily.Math, (e, a, _) => Args.Number(Math.PI), isReporter: true);

        // Trig in Logo degrees rather than radians, because that is what a child is thinking in.
        t.Add("SIN", 1, "Sine of an angle in degrees.", "PRINT SIN 30",
            CommandFamily.Math, (e, a, _) => Args.Number(Math.Sin(Radians(Args.Number(a, 0, "SIN", e.Mode)))), isReporter: true);

        t.Add("COS", 1, "Cosine of an angle in degrees.", "PRINT COS 60",
            CommandFamily.Math, (e, a, _) => Args.Number(Math.Cos(Radians(Args.Number(a, 0, "COS", e.Mode)))), isReporter: true);

        t.Add("TAN", 1, "Tangent of an angle in degrees.", "PRINT TAN 45",
            CommandFamily.Math, (e, a, _) => Args.Number(Math.Tan(Radians(Args.Number(a, 0, "TAN", e.Mode)))), isReporter: true);

        t.Add("ARCTAN", 1, "The angle whose tangent is that number, in degrees.", "PRINT ARCTAN 1",
            CommandFamily.Math, (e, a, _) => Args.Number(Math.Atan(Args.Number(a, 0, "ARCTAN", e.Mode)) * 180.0 / Math.PI), isReporter: true);

        t.Add("NUMBERP", 1, "Report TRUE when the input is a number.", "PRINT NUMBERP 5",
            CommandFamily.Math, (e, a, _) => Args.Flag(a[0].Kind == LogoKind.Number), isReporter: true,
            aliases: new[] { "ISNUMBER", "NUMBER?" });
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180.0;

    private static double Sum(IReadOnlyList<LogoValue> values)
    {
        double total = 0;
        foreach (var v in values) total += Number(v, "+", LogoMode.Easy);
        return total;
    }

    private static double Number(LogoValue value, string name, LogoMode mode)
    {
        if (value.Kind == LogoKind.Number) return value.Number;
        throw new LogoException(LogoErrorCode.BadInputType, ErrorText.TypeMismatch(name, "a number", mode),
            $"{name} only works with numbers, and {Printer.Format(value, true)} is not one.", subject: name);
    }
}
