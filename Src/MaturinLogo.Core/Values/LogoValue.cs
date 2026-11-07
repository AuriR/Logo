namespace MaturinLogo.Core.Values;

/// <summary>Logo is untyped: every value is a word, a number, a list, TRUE/FALSE, or a procedure.</summary>
public enum LogoKind
{
    /// <summary>"Nothing" - the value a command returns when it has no output.</summary>
    None,
    Number,
    Word,
    List,
    Boolean,
    Procedure,
}

/// <summary>
/// An immutable Logo value. Lists are built up by the reader and then treated as read-only.
/// </summary>
public sealed class LogoValue : IEquatable<LogoValue>
{
    public LogoKind Kind { get; }

    /// <summary>Payload for <see cref="LogoKind.Number"/>.</summary>
    public double Number { get; }

    /// <summary>Payload for <see cref="LogoKind.Word"/> and <see cref="LogoKind.Procedure"/> (the name).</summary>
    public string Word { get; }

    /// <summary>Payload for <see cref="LogoKind.List"/>.</summary>
    public IReadOnlyList<LogoValue> Items { get; }

    /// <summary>
    /// True when a list was written with parentheses, as in <c>[(FD 10)]</c>. Logo keeps that
    /// distinction so RUN can execute the group; it prints as <c>(FD 10)</c>, not <c>[FD 10]</c>.
    /// </summary>
    public bool IsParenGroup { get; }

    /// <summary>Payload for <see cref="LogoKind.Boolean"/>.</summary>
    public bool Bool { get; }

    /// <summary>Payload for <see cref="LogoKind.Procedure"/>.</summary>
    public UserProcedure? Procedure { get; }

    /// <summary>
    /// True when a word was written with a quote (<c>"HELLO</c>) or bars (<c>|Hello|</c>) rather
    /// than bare. Logo keeps that distinction: a quoted word inside a list is data, so
    /// <c>RUN ["A "B]</c> must not try to call procedures named A and B.
    /// </summary>
    public bool IsQuoted { get; }

    private LogoValue(LogoKind kind, double number, string word, IReadOnlyList<LogoValue>? items, bool boolValue, UserProcedure? proc, bool isParen = false, bool isQuoted = false)
    {
        Kind = kind;
        Number = number;
        Word = word;
        Items = items ?? System.Array.Empty<LogoValue>();
        Bool = boolValue;
        Procedure = proc;
        IsParenGroup = isParen;
        IsQuoted = isQuoted;
    }

    public static readonly LogoValue None = new(LogoKind.None, 0, "", null, false, null);
    public static readonly LogoValue True = new(LogoKind.Boolean, 0, "TRUE", null, true, null);
    public static readonly LogoValue False = new(LogoKind.Boolean, 0, "FALSE", null, false, null);

    public static LogoValue FromNumber(double value) => new(LogoKind.Number, value, "", null, false, null);
    public static LogoValue FromWord(string value) => new(LogoKind.Word, 0, value, null, false, null);

    /// <summary>A word that was written quoted, e.g. <c>"HELLO</c> or <c>|Hello|</c>.</summary>
    public static LogoValue FromQuotedWord(string value) => new(LogoKind.Word, 0, value, null, false, null, isQuoted: true);
    public static LogoValue FromList(IReadOnlyList<LogoValue> items) => new(LogoKind.List, 0, "", items, false, null);

    /// <summary>A parenthesized group value, e.g. the <c>(FD 10)</c> inside <c>RUN [(FD 10)]</c>.</summary>
    public static LogoValue FromParenGroup(IReadOnlyList<LogoValue> items) => new(LogoKind.List, 0, "", items, false, null, isParen: true);
    public static LogoValue FromBoolean(bool value) => value ? True : False;
    public static LogoValue FromProcedure(UserProcedure proc) => new(LogoKind.Procedure, 0, proc.Name, null, false, proc);

    public bool IsTrue => Kind switch
    {
        LogoKind.Boolean => Bool,
        LogoKind.None => false,
        _ => true,
    };

    public bool IsEmpty => Kind switch
    {
        LogoKind.List => Items.Count == 0,
        LogoKind.Word => Word.Length == 0,
        LogoKind.None => true,
        _ => false,
    };

    /// <summary>The number payload, or <paramref name="fallback"/> when this is not a number.</summary>
    public double AsNumberOrDefault(double fallback = 0) => Kind == LogoKind.Number ? Number : fallback;

    /// <summary>
    /// The text of the value: words give their text, numbers give their printed form, lists give
    /// their printed form. Used by WORD, SENTENCE, MAKE's name input and the error reporter.
    /// </summary>
    public string AsWordText() => Kind switch
    {
        LogoKind.Word => Word,
        LogoKind.Number => Printer.FormatNumber(Number),
        LogoKind.Boolean => Bool ? "TRUE" : "FALSE",
        LogoKind.Procedure => Word,
        LogoKind.None => "",
        LogoKind.List => Printer.Format(this, readable: false),
        _ => "",
    };

    public bool Equals(LogoValue? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (Kind != other.Kind) return false;
        return Kind switch
        {
            LogoKind.Number => Number.Equals(other.Number),
            LogoKind.Word => string.Equals(Word, other.Word, StringComparison.Ordinal),
            LogoKind.Boolean => Bool == other.Bool,
            LogoKind.Procedure => ReferenceEquals(Procedure, other.Procedure),
            LogoKind.None => true,
            LogoKind.List => ListsEqual(Items, other.Items),
            _ => false,
        };
    }

    private static bool ListsEqual(IReadOnlyList<LogoValue> a, IReadOnlyList<LogoValue> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!a[i].Equals(b[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is LogoValue v && Equals(v);

    public override int GetHashCode() => Kind switch
    {
        LogoKind.Number => Number.GetHashCode(),
        LogoKind.Word => Word.GetHashCode(StringComparison.Ordinal),
        LogoKind.Boolean => Bool.GetHashCode(),
        LogoKind.Procedure => Procedure?.GetHashCode() ?? 0,
        LogoKind.None => 0,
        _ => Items.Count,
    };

    public override string ToString() => Printer.Format(this, readable: true);
}

/// <summary>A procedure defined with TO ... END. The body is already-read program items.</summary>
public sealed class UserProcedure
{
    public string Name { get; }

    /// <summary>Formal inputs in declaration order.</summary>
    public IReadOnlyList<FormalInput> Formals { get; }

    /// <summary>Statements between the header and END.</summary>
    public IReadOnlyList<Reader.ProgramItem> Body { get; }

    /// <summary>True when the body contains OUTPUT, i.e. the procedure is a reporter.</summary>
    public bool IsReport { get; }

    public UserProcedure(string name, IReadOnlyList<FormalInput> formals, IReadOnlyList<Reader.ProgramItem> body, bool isReport)
    {
        Name = name;
        Formals = formals;
        Body = body;
        IsReport = isReport;
    }

    /// <summary>How many inputs a bare (non-parenthesized) call consumes.</summary>
    public int RequiredInputs
    {
        get
        {
            int n = 0;
            foreach (var f in Formals)
            {
                if (f.Kind == FormalKind.Optional || f.Kind == FormalKind.Rest) break;
                n++;
            }
            return n;
        }
    }
}

public enum FormalKind
{
    /// <summary>:NAME - required input.</summary>
    Required,

    /// <summary>[:NAME value] - optional input with a default.</summary>
    Optional,

    /// <summary>[:NAME] as the last formal - collects all remaining inputs as a list.</summary>
    Rest,
}

/// <summary>One formal parameter of a user procedure.</summary>
public sealed class FormalInput
{
    public string Name { get; }
    public FormalKind Kind { get; }
    public LogoValue? Default { get; }

    public FormalInput(string name, FormalKind kind, LogoValue? defaultValue = null)
    {
        Name = name;
        Kind = kind;
        Default = defaultValue;
    }
}
