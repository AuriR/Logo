using System.Text;
using MaturinLogo.Core.Interpreter;
using MaturinLogo.Core.Values;

namespace MaturinLogo.Core.Primitives;

/// <summary>
/// Printing to the text screen. PRINT puts each input on one line and then moves down; TYPE puts
/// text without moving, which is what lets a child build up a line a piece at a time.
/// </summary>
public static class OutputPrimitives
{
    public static void Register(PrimitiveTable t)
    {
        t.Add("PRINT", -1, "Print anything, then start a new line.", "PRINT \"HELLO",
            CommandFamily.Output, (e, a, _) =>
            {
                if (a.Count == 0)
                {
                    e.Screen.NewLine();
                    return Args.Done();
                }
                var sb = new StringBuilder();
                foreach (var value in a) sb.Append(Printer.Format(value, readable: true)).Append(' ');
                e.Screen.WriteLine(sb.ToString().TrimEnd());
                return Args.Done();
            }, variadic: true, aliases: new[] { "PR" });

        t.Add("SHOW", 1, "Print one value on its own line.", "SHOW 2 + 2",
            CommandFamily.Output, (e, a, _) =>
            {
                e.Screen.WriteLine(Printer.Format(a[0], readable: true));
                return Args.Done();
            }, aliases: new[] { "SH" });

        t.Add("TYPE", -1, "Print without moving to a new line.", "TYPE \"HELLO TYPE \" THERE",
            CommandFamily.Output, (e, a, _) =>
            {
                var sb = new StringBuilder();
                foreach (var value in a) sb.Append(Printer.Format(value, readable: false));
                e.Screen.Write(sb.ToString());
                return Args.Done();
            }, variadic: true);

        t.Add("FORM", 3, "Print a value right-aligned in that many spaces, with a number of decimal places.", "PRINT FORM 3.14159 8 2",
            CommandFamily.Output, (e, a, _) =>
            {
                int width = (int)Math.Round(Args.Number(a, 1, "FORM", e.Mode));
                int decimals = (int)Math.Round(Args.Number(a, 2, "FORM", e.Mode));
                return Args.Word(Format(e, a[0], width, decimals));
            }, isReporter: true);

        t.Add("FILL", 2, "Repeat a word a number of times, for neat tables and borders.", "PRINT FILL \"- 20",
            CommandFamily.Output, (e, a, _) =>
            {
                string text = Args.Word(a, 0, "FILL", e.Mode);
                int times = (int)Math.Round(Args.Number(a, 1, "FILL", e.Mode));
                if (times < 0 || times > 4000)
                    throw new LogoException(LogoErrorCode.OutOfRange, ErrorText.OutOfRange("FILL", e.Mode),
                        "FILL repeats a word up to 4000 times.", subject: "FILL");
                var sb = new StringBuilder(text.Length * times);
                for (int i = 0; i < times; i++) sb.Append(text);
                return Args.Word(sb.ToString());
            }, isReporter: true, aliases: new[] { "REPEATW" });

        t.Add("READ", 0, "Wait for a number to be typed and report it.", "MAKE \"SIZE READ",
            CommandFamily.Output, async (e, a, _) =>
            {
                string? line = await e.Host.ReadLineAsync();
                if (line is null)
                    throw new LogoException(LogoErrorCode.NoValue, "READ found nothing typed",
                        "READ waits for a number - type one and press RETURN.", subject: "READ");
                var items = Reader.LogoReader.ReadProgram(line, e.FoldCase);
                if (items.Count == 0)
                    throw new LogoException(LogoErrorCode.BadNumber, "READ needs a number",
                        $"I saw \"{line}\" which is not a number.", subject: "READ");
                return await e.EvalOneValueAsync(Reader.LogoReader.ToLiteral(items[0]), e.CurrentFrame);
            }, isReporter: true);

        t.Add("READLINE", 0, "Wait for a whole line to be typed and report it as a word.", "MAKE \"NAME READLINE",
            CommandFamily.Output, async (e, a, _) =>
            {
                string? line = await e.Host.ReadLineAsync();
                return LogoValue.FromWord(e.FoldCase ? (line ?? "").ToUpperInvariant() : (line ?? ""));
            }, isReporter: true);

        t.Add("READLIST", 0, "Wait for a list to be typed and report it.", "MAKE \"ITEMS READLIST",
            CommandFamily.Output, async (e, a, _) =>
            {
                string? line = await e.Host.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) return LogoValue.FromList(Array.Empty<LogoValue>());
                var items = Reader.LogoReader.ReadProgram(line.Trim(), e.FoldCase);
                if (items.Count == 1 && items[0].Kind == Reader.ItemKind.List)
                    return Reader.LogoReader.ToLiteral(items[0]);
                var list = new List<LogoValue>(items.Count);
                foreach (var item in items) list.Add(Reader.LogoReader.ToLiteral(item));
                return LogoValue.FromList(list);
            }, isReporter: true);
    }

    /// <summary>Logo's FORM: numbers get decimals, words get truncated or padded to the width.</summary>
    private static string Format(Engine e, LogoValue value, int width, int decimals)
    {
        string text;
        if (value.Kind == LogoKind.Number)
        {
            string format = decimals == 0 ? "0" : "0." + new string('#', decimals);
            text = value.Number.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            text = Printer.Format(value, readable: false);
            if (text.Length > width && width > 0) text = text.Substring(0, width);
        }

        if (width <= 0) return text;
        return text.Length >= width ? text : new string(' ', width - text.Length) + text;
    }
}
