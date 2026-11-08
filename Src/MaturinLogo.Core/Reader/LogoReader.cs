using System.Globalization;
using MaturinLogo.Core.Values;

namespace MaturinLogo.Core.Reader;

/// <summary>Source text could not be read as Logo. Carries the line number when known.</summary>
public sealed class ReadException : Exception
{
    public int Line { get; }

    public ReadException(string message, int line = 0) : base(message) => Line = line;
}

/// <summary>
/// Turns Logo source text into a tree of <see cref="ProgramItem"/>s: the language's reader.
/// Handles quoted words, variable references, case-preserving |bars|, radix numbers, list
/// literals, parenthesized groups and ';' comments.
/// </summary>
public static class LogoReader
{
    /// <summary>Read a whole program (any number of lines and commands).</summary>
    public static List<ProgramItem> ReadProgram(string source, bool foldCase = true)
    {
        var tokens = Tokenize(source, foldCase);
        int pos = 0;
        var items = new List<ProgramItem>();
        while (pos < tokens.Count)
        {
            var item = ReadItem(tokens, ref pos, foldCase);
            if (item is not null) items.Add(item);
        }
        return items;
    }

    /// <summary>Read exactly one list of items from text, used by READLIST and friends.</summary>
    public static List<ProgramItem> ReadItems(string source, bool foldCase = true) => ReadProgram(source, foldCase);

    private enum TokKind { Word, Quoted, Var, Pipe, OpenList, CloseList, OpenParen, CloseParen }

    private readonly record struct Token(TokKind Kind, string Text, double Number, bool IsNumber, int Line);

    private static List<Token> Tokenize(string source, bool foldCase)
    {
        var list = new List<Token>();
        int i = 0, line = 1;

        while (i < source.Length)
        {
            char c = source[i];

            if (c == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }

            // ';' runs a comment to the end of the line, as in Logo source files.
            if (c == ';')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }

            if (c == '[') { list.Add(new Token(TokKind.OpenList, "[", 0, false, line)); i++; continue; }
            if (c == ']') { list.Add(new Token(TokKind.CloseList, "]", 0, false, line)); i++; continue; }
            if (c == '(') { list.Add(new Token(TokKind.OpenParen, "(", 0, false, line)); i++; continue; }
            if (c == ')') { list.Add(new Token(TokKind.CloseParen, ")", 0, false, line)); i++; continue; }

            // |Case Preserved Word| - never folded, and the bars are dropped.
            if (c == '|')
            {
                int start = i + 1;
                i++;
                while (i < source.Length && source[i] != '|') { if (source[i] == '\n') line++; i++; }
                if (i >= source.Length) throw new ReadException("Unmatched | in source", line);
                string piped = source.Substring(start, i - start);
                i++; // consume closing bar
                list.Add(new Token(TokKind.Pipe, piped, 0, false, line));
                continue;
            }

            // "WORD quotes the next word; "" quotes a single space, ""X gives a leading space.
            if (c == '"')
            {
                i++;
                // "|Hello|" - a quote in front of a case-preserving word keeps its mixed case.
                // This is checked before the delimiter test below, because | is itself a delimiter.
                if (i < source.Length && source[i] == '|')
                {
                    int start = i + 1;
                    i++;
                    while (i < source.Length && source[i] != '|') { if (source[i] == '\n') line++; i++; }
                    if (i >= source.Length) throw new ReadException("Unmatched | in source", line);
                    i++;
                    list.Add(new Token(TokKind.Quoted, source.Substring(start, i - start - 1), 0, false, line));
                    continue;
                }
                if (i >= source.Length || IsDelimiter(source[i]) || source[i] == '"')
                {
                    // A bare quote, or a doubled quote: an empty / space-prefixed word.
                    int spaces = 0;
                    while (i < source.Length && source[i] == '"') { spaces++; i++; }
                    string pad = new string(' ', spaces);
                    if (i < source.Length && !IsDelimiter(source[i]))
                    {
                        string w = ReadWordText(source, ref i);
                        pad += foldCase ? w.ToUpperInvariant() : w;
                    }
                    list.Add(new Token(TokKind.Quoted, pad, 0, false, line));
                    continue;
                }
                string word = ReadWordText(source, ref i);
                list.Add(new Token(TokKind.Quoted, foldCase ? word.ToUpperInvariant() : word, 0, false, line));
                continue;
            }

            // :NAME is a variable reference.
            if (c == ':')
            {
                i++;
                if (i < source.Length && source[i] == '|')
                {
                    int start = i + 1;
                    i++;
                    while (i < source.Length && source[i] != '|') { if (source[i] == '\n') line++; i++; }
                    if (i >= source.Length) throw new ReadException("Unmatched | in source", line);
                    i++;
                    list.Add(new Token(TokKind.Var, source.Substring(start, i - start - 1), 0, false, line));
                    continue;
                }
                string name = ReadWordText(source, ref i);
                if (name.Length == 0) throw new ReadException("A : must be followed by a name", line);
                list.Add(new Token(TokKind.Var, foldCase ? name.ToUpperInvariant() : name, 0, false, line));
                continue;
            }

            string raw = ReadWordText(source, ref i);
            if (raw.Length == 0)
            {
                // Nothing consumable: skip the stray character rather than looping forever.
                i++;
                continue;
            }
            string text = foldCase ? raw.ToUpperInvariant() : raw;

            if (TryNumber(text, out double value))
                list.Add(new Token(TokKind.Word, text, value, true, line));
            else
                list.Add(new Token(TokKind.Word, text, 0, false, line));
        }

        return list;
    }

    /// <summary>Word text runs until a delimiter. Radix prefixes (#H) and operators stay in the word.</summary>
    private static string ReadWordText(string source, ref int i)
    {
        int start = i;
        while (i < source.Length && !IsDelimiter(source[i])) i++;
        return source.Substring(start, i - start);
    }

    private static bool IsDelimiter(char c) =>
        char.IsWhiteSpace(c) || c is '[' or ']' or '(' or ')' or '"' or ';' or '|';

    /// <summary>Longest name Logo accepts, so a runaway word cannot fill the workspace dictionary.</summary>
    public const int MaxNameLength = 64;

    /// <summary>
    /// True when <paramref name="name"/> is something a child may put on a procedure or a variable:
    /// letters, digits and underscore, one to <see cref="MaxNameLength"/> of them.
    /// </summary>
    /// <remarks>
    /// The reader stops a word at a space or a bracket and nothing else, so before this check
    /// <c>TO SQUARE:</c> quietly defined a procedure literally named <c>SQUARE:</c> - which could
    /// then never be called, because <c>SQUARE:</c> at the listener reads as a name plus a colon.
    /// Punctuation names are a trap: they look like they worked, and the child meets the failure
    /// later, in a program that "should" run. Real Apple Logo was more generous here, but this is
    /// a teaching machine and a clear refusal beats a name that silently misfires.
    /// </remarks>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength) return false;

        foreach (char c in name)
            if (!(char.IsAsciiLetter(c) || char.IsAsciiDigit(c) || c == '_')) return false;

        return true;
    }

    /// <summary>
    /// Recognises decimal numbers (with sign, point and exponent) and Terrapin's radix forms
    /// <c>#B</c>inary, <c>#O</c>ctal, <c>#D</c>ecimal and <c>#H</c>ex.
    /// </summary>
    internal static bool TryNumber(string text, out double value)
    {
        value = 0;
        if (text.Length == 0) return false;

        if (text[0] == '#' && text.Length > 2)
        {
            char radix = char.ToUpperInvariant(text[1]);
            string digits = text.Substring(2);
            int from = radix switch
            {
                'B' => 2,
                'O' => 8,
                'H' => 16,
                'D' => 10,
                _ => 0,
            };
            if (from == 0) return false;
            bool negative = false;
            if (digits.Length > 0 && (digits[0] == '-' || digits[0] == '+'))
            {
                negative = digits[0] == '-';
                digits = digits.Substring(1);
            }
            if (digits.Length == 0) return false;
            double acc = 0;
            foreach (char ch in digits)
            {
                int d = HexValue(ch);
                if (d < 0 || d >= from) return false;
                acc = acc * from + d;
            }
            value = negative ? -acc : acc;
            return true;
        }

        // A lone '#' or an all-symbol word is not a number.
        if (text.Length == 1 && !char.IsDigit(text[0])) return false;
        if (!char.IsDigit(text[0]) && text[0] != '-' && text[0] != '+') return false;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)) return false;
        if (text.Contains('E') || text.Contains('e') || text.Contains('.'))
        {
            value = parsed;
            return true;
        }
        // Guard against words like "1ST" that TryParse may have handled as something odd.
        foreach (char ch in text)
            if (!char.IsDigit(ch) && ch != '-' && ch != '+') return false;
        value = parsed;
        return true;
    }

    private static int HexValue(char c)
    {
        if (c is >= '0' and <= '9') return c - '0';
        if (c is >= 'A' and <= 'F') return c - 'A' + 10;
        if (c is >= 'a' and <= 'f') return c - 'a' + 10;
        return -1;
    }

    private static ProgramItem? ReadItem(List<Token> tokens, ref int pos, bool foldCase)
    {
        if (pos >= tokens.Count) return null;
        Token t = tokens[pos];
        int line = t.Line;

        switch (t.Kind)
        {
            case TokKind.OpenList:
            {
                pos++;
                var children = ReadGroup(tokens, ref pos, TokKind.CloseList, line, "list", foldCase);
                return ProgramItem.ListItem(children, line);
            }
            case TokKind.OpenParen:
            {
                pos++;
                var children = ReadGroup(tokens, ref pos, TokKind.CloseParen, line, "group", foldCase);
                return ProgramItem.ParenItem(children, line);
            }
            case TokKind.CloseList:
            case TokKind.CloseParen:
                throw new ReadException($"Unexpected {t.Text}", line);
            case TokKind.Quoted:
            case TokKind.Pipe:
                pos++;
                return ProgramItem.Quoted(t.Text, line);
            case TokKind.Var:
                pos++;
                return ProgramItem.Var(t.Text, line);
            default:
                pos++;
                return t.IsNumber ? ProgramItem.Num(t.Number, line) : ProgramItem.Bare(t.Text, line);
        }
    }

    private static List<ProgramItem> ReadGroup(List<Token> tokens, ref int pos, TokKind closer, int openLine, string what, bool foldCase)
    {
        var children = new List<ProgramItem>();
        while (pos < tokens.Count && tokens[pos].Kind != closer)
        {
            if (tokens[pos].Kind is TokKind.CloseList or TokKind.CloseParen)
                throw new ReadException($"Mismatched {tokens[pos].Text} - expected the end of the {what}", tokens[pos].Line);

            var item = ReadItem(tokens, ref pos, foldCase);
            if (item is not null) children.Add(item);
        }
        if (pos >= tokens.Count)
            throw new ReadException($"Unterminated {what} - missing {(closer == TokKind.CloseList ? "]" : ")")}", openLine);
        pos++; // consume the closer
        return children;
    }

    /// <summary>
    /// Converts an already-read item into a self-evaluating value. Bare words and piped words
    /// become words; nested lists keep their structure; a variable reference inside a literal
    /// keeps its ':' spelling so RUN can evaluate it later, which is what Logo does.
    /// </summary>
    public static LogoValue ToLiteral(ProgramItem item)
    {
        switch (item.Kind)
        {
            case ItemKind.Number:
                return LogoValue.FromNumber(item.Number);
            case ItemKind.VarRef:
                return LogoValue.FromWord(":" + item.Text);
            case ItemKind.List:
            {
                var list = new List<LogoValue>(item.Children.Count);
                foreach (var child in item.Children) list.Add(ToLiteral(child));
                return LogoValue.FromList(list);
            }
            case ItemKind.Paren:
            {
                var list = new List<LogoValue>(item.Children.Count);
                foreach (var child in item.Children) list.Add(ToLiteral(child));
                return LogoValue.FromParenGroup(list);
            }
            case ItemKind.QuotedWord:
                // The quote is remembered, so RUN can tell data from a command name.
                return LogoValue.FromQuotedWord(item.Text);
            default:
                return LogoValue.FromWord(item.Text);
        };
    }
}
