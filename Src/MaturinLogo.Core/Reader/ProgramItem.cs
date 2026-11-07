namespace MaturinLogo.Core.Reader;

public enum ItemKind
{
    /// <summary>A bare word: a procedure call in command position, a name in expression position.</summary>
    BareWord,

    /// <summary>A quoted word: <c>"HELLO</c> or <c>|Hello|</c> - evaluates to itself.</summary>
    QuotedWord,

    /// <summary>A variable reference: <c>:NAME</c>.</summary>
    VarRef,

    /// <summary>A number literal, including the #B/#O/#D/#H radix forms.</summary>
    Number,

    /// <summary>A bracketed list literal <c>[...]</c> - self-evaluating.</summary>
    List,

    /// <summary>A parenthesized group <c>(...)</c> - runs its statements, yields the last output.</summary>
    Paren,
}

/// <summary>
/// One item of read program text. The reader produces a tree, so <see cref="ItemKind.List"/> and
/// <see cref="ItemKind.Paren"/> carry their children instead of bracket tokens.
/// </summary>
public sealed class ProgramItem
{
    public ItemKind Kind { get; }

    /// <summary>Text for words. Original spelling is preserved for <c>|piped|</c> words.</summary>
    public string Text { get; }

    public double Number { get; }

    /// <summary>Children of a list or paren group.</summary>
    public IReadOnlyList<ProgramItem> Children { get; }

    /// <summary>1-based source line, so editor errors can point at the offending line.</summary>
    public int Line { get; }

    private ProgramItem(ItemKind kind, string text, double number, IReadOnlyList<ProgramItem>? children, int line)
    {
        Kind = kind;
        Text = text;
        Number = number;
        Children = children ?? System.Array.Empty<ProgramItem>();
        Line = line;
    }

    public static ProgramItem Bare(string text, int line = 0) => new(ItemKind.BareWord, text, 0, null, line);
    public static ProgramItem Quoted(string text, int line = 0) => new(ItemKind.QuotedWord, text, 0, null, line);
    public static ProgramItem Var(string text, int line = 0) => new(ItemKind.VarRef, text, 0, null, line);
    public static ProgramItem Num(double value, int line = 0) => new(ItemKind.Number, "", value, null, line);
    public static ProgramItem ListItem(IReadOnlyList<ProgramItem> children, int line = 0) => new(ItemKind.List, "", 0, children, line);
    public static ProgramItem ParenItem(IReadOnlyList<ProgramItem> children, int line = 0) => new(ItemKind.Paren, "", 0, children, line);

    /// <summary>True when this item is a bare word equal to <paramref name="word"/>, ignoring case.</summary>
    public bool IsBare(string word) => Kind == ItemKind.BareWord && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);

    /// <summary>The item's text in Logo-value form: numbers print, lists and groups are bracketed.</summary>
    public string AsWordText() => Kind switch
    {
        ItemKind.Number => Values.Printer.FormatNumber(Number),
        ItemKind.QuotedWord or ItemKind.BareWord => Text,
        ItemKind.VarRef => ":" + Text,
        ItemKind.List => "[" + string.Join(" ", Children.Select(c => c.AsWordText())) + "]",
        ItemKind.Paren => "(" + string.Join(" ", Children.Select(c => c.AsWordText())) + ")",
        _ => Text,
    };

    public override string ToString() => Kind switch
    {
        ItemKind.BareWord => Text,
        ItemKind.QuotedWord => "\"" + Text,
        ItemKind.VarRef => ":" + Text,
        ItemKind.Number => Values.Printer.FormatNumber(Number),
        ItemKind.List => "[" + string.Join(" ", Children) + "]",
        ItemKind.Paren => "(" + string.Join(" ", Children) + ")",
        _ => "?",
    };
}
