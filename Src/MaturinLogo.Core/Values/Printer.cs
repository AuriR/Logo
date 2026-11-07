using System.Globalization;
using System.Text;

namespace MaturinLogo.Core.Values;

/// <summary>
/// Turns Logo values back into text the way Logo printed them on the Apple II: words are bare,
/// numbers never carry a spurious ".0", and lists are bracketed.
/// </summary>
public static class Printer
{
    /// <summary>Apple Logo prints integers as <c>4</c>, not <c>4.0</c>, with up to 15 significant digits.</summary>
    public static string FormatNumber(double value)
    {
        if (double.IsNaN(value)) return "NAN";
        if (double.IsPositiveInfinity(value)) return "INF";
        if (double.IsNegativeInfinity(value)) return "-INF";

        // Normalise -0 so results like "0 - 0" do not print "-0".
        if (value == 0.0) value = 0.0;

        if (Math.Floor(value) == value && Math.Abs(value) < 1e15)
            return ((long)value).ToString(CultureInfo.InvariantCulture);

        return value.ToString("G15", CultureInfo.InvariantCulture);
    }

    /// <param name="readable">True for PRINT/SHOW (a word containing a space is bracketed so the
    /// result can be read back by Logo); false for TYPE-style output.</param>
    public static string Format(LogoValue value, bool readable)
    {
        switch (value.Kind)
        {
            case LogoKind.None:
                return "";
            case LogoKind.Number:
                return FormatNumber(value.Number);
            case LogoKind.Boolean:
                return value.Bool ? "TRUE" : "FALSE";
            case LogoKind.Procedure:
                return value.Word;
            case LogoKind.Word:
                return readable && NeedsBrackets(value.Word) ? "[" + value.Word + "]" : value.Word;
            case LogoKind.List:
                char open = value.IsParenGroup ? '(' : '[';
                char close = value.IsParenGroup ? ')' : ']';
                var sb = new StringBuilder();
                sb.Append(open);
                for (int i = 0; i < value.Items.Count; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append(Format(value.Items[i], readable));
                }
                sb.Append(close);
                return sb.ToString();
            default:
                return "";
        }
    }

    /// <summary>An empty word, or one containing a space, must be bracketed to stay one list item.</summary>
    private static bool NeedsBrackets(string word) => word.Length == 0 || word.Contains(' ');

    /// <summary>
    /// WRITE-style output: words are quoted with <c>"</c> so a whole line can be read back with
    /// READLIST. Used by the session exporter to keep generated source valid.
    /// </summary>
    public static string FormatQuoted(LogoValue value) =>
        value.Kind == LogoKind.Word ? "\"" + value.Word : Format(value, readable: true);
}
