using MaturinLogo.Core.Interpreter;
using MaturinLogo.Core.Values;

namespace MaturinLogo.Core.Primitives;

/// <summary>Topic areas used by the quick reference and by "did you mean" hints.</summary>
public enum CommandFamily
{
    Turtle,
    Math,
    Words,
    Lists,
    Logic,
    Data,
    Control,
    Output,
    Screen,
    System,
}

/// <summary>
/// One Logo primitive. Arity matters: Logo pulls exactly <see cref="Inputs"/> evaluated items
/// from the source that follows the name, which is why FD 10 RT 90 is two calls, not one.
/// </summary>
public sealed class Primitive
{
    public string Name { get; }

    /// <summary>Alternative spellings, e.g. FORWARD for FD. All fold to upper case.</summary>
    public IReadOnlyList<string> Aliases { get; }

    /// <summary>
    /// How many inputs a bare call consumes. Zero for a variadic primitive, which takes as many
    /// as follow it, and for the infix operators, which the engine handles between two values.
    /// </summary>
    public int Inputs
    {
        get
        {
            if (Variadic || Syntactic) return 0;
            return Math.Max(0, DeclaredInputs);
        }
    }

    /// <summary>The arity as declared by the registration table; -1 means variadic.</summary>
    public int DeclaredInputs { get; }

    /// <summary>True when the primitive keeps consuming inputs to the end of its group (PRINT, WORD).</summary>
    public bool Variadic { get; }

    /// <summary>True when the primitive reads unevaluated items itself (IF, REPEAT, TO, MAKE).</summary>
    public bool Syntactic { get; }

    /// <summary>True when the primitive reports a value, so EASY mode prints its result.</summary>
    public bool IsReporter { get; }

    /// <summary>
    /// Hidden commands (single-letter aliases, duplicate spellings) are callable but left out of
    /// the quick reference so the screen stays readable for a child.
    /// </summary>
    public bool Hidden { get; }

    public string Doc { get; }

    public string Example { get; }

    public CommandFamily Family { get; }

    /// <summary>Handler for primitives whose inputs are already evaluated.</summary>
    public Func<Engine, IReadOnlyList<LogoValue>, Frame, Task<LogoValue>>? Handler { get; }

    /// <summary>Handler for syntactic primitives, which pull raw items from the stream.</summary>
    public Func<Engine, SourceStream, Frame, Task<LogoValue>>? StreamHandler { get; }

    private Primitive(PrimitiveSpec spec)
    {
        Name = spec.Name;
        Aliases = spec.Aliases;
        DeclaredInputs = spec.Inputs;
        Variadic = spec.Variadic;
        Syntactic = spec.Syntactic;
        IsReporter = spec.IsReporter;
        Hidden = spec.Hidden;
        Doc = spec.Doc;
        Example = spec.Example;
        Family = spec.Family;
        Handler = spec.Handler;
        StreamHandler = spec.StreamHandler;
    }

    internal static Primitive From(PrimitiveSpec spec) => new(spec);
}

/// <summary>Builder record used by the registration tables; keeps the declarations readable.</summary>
public sealed class PrimitiveSpec
{
    public string Name { get; init; } = "";
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();
    public int Inputs { get; init; }
    public bool Variadic { get; init; }
    public bool Syntactic { get; init; }
    public bool IsReporter { get; init; }
    public bool Hidden { get; init; }
    public string Doc { get; init; } = "";
    public string Example { get; init; } = "";
    public CommandFamily Family { get; init; }
    public Func<Engine, IReadOnlyList<LogoValue>, Frame, Task<LogoValue>>? Handler { get; init; }
    public Func<Engine, SourceStream, Frame, Task<LogoValue>>? StreamHandler { get; init; }
}

/// <summary>
/// Every primitive Logo knows, indexed by name and alias. This single table drives execution,
/// the quick reference text, and the "did you mean" suggestions, so the help can never drift
/// away from what is actually implemented.
/// </summary>
public sealed class PrimitiveTable
{
    private readonly Dictionary<string, Primitive> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Primitive> _ordered = new();

    /// <summary>Names a child may not redefine with TO.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "TO", "END", "LOCAL", "OUTPUT", "STOP", "ELSE", "THEN", "DO", "INTO", "TEST", "IF",
    };

    public IReadOnlyList<Primitive> All => _ordered;

    public bool TryGet(string name, out Primitive primitive) => _byName.TryGetValue(name, out primitive!);

    public bool Contains(string name) => _byName.ContainsKey(name);

    public static bool IsReserved(string name) => Reserved.Contains(name);

    public IEnumerable<Primitive> InFamily(CommandFamily family) =>
        _ordered.Where(p => p.Family == family);

    /// <summary>Register one primitive from a spec.</summary>
    public void Add(PrimitiveSpec spec)
    {
        var primitive = Primitive.From(spec);

        // Every name must land on exactly one behaviour. An alias that quietly overwrote another
        // command is the worst kind of bug here, because the language still looks complete: "LT"
        // aliased to LESS turned the turtle's turn-left into a comparison, and a single-letter
        // alias like F for FIRST meant a child could not name a procedure F without a fight.
        Claim(primitive.Name, primitive);
        foreach (var alias in primitive.Aliases) Claim(alias, primitive);
        _ordered.Add(primitive);
    }

    private void Claim(string name, Primitive primitive)
    {
        if (_byName.TryGetValue(name, out Primitive? existing))
            throw new InvalidOperationException(
                $"'{name}' is claimed by both {existing.Name} and {primitive.Name}.");

        _byName[name] = primitive;
    }

    /// <summary>Convenience: register a plain (evaluated-inputs) primitive.</summary>
    public void Add(string name, int inputs, string doc, string example, CommandFamily family,
        Func<Engine, IReadOnlyList<LogoValue>, Frame, Task<LogoValue>> handler,
        bool isReporter = false, IReadOnlyList<string>? aliases = null, bool variadic = false)
    {
        Add(new PrimitiveSpec
        {
            Name = name,
            Inputs = inputs,
            Doc = doc,
            Example = example,
            Family = family,
            Handler = handler,
            IsReporter = isReporter,
            Aliases = aliases ?? Array.Empty<string>(),
            Variadic = variadic,
        });
    }

    /// <summary>Convenience: register a syntactic primitive that reads raw items itself.</summary>
    public void AddSyntactic(string name, int inputs, string doc, string example, CommandFamily family,
        Func<Engine, SourceStream, Frame, Task<LogoValue>> handler,
        bool isReporter = false, IReadOnlyList<string>? aliases = null)
    {
        Add(new PrimitiveSpec
        {
            Name = name,
            Inputs = inputs,
            Doc = doc,
            Example = example,
            Family = family,
            StreamHandler = handler,
            Syntactic = true,
            IsReporter = isReporter,
            Aliases = aliases ?? Array.Empty<string>(),
        });
    }

    /// <summary>
    /// Logo's syntactic words - TO, END, STOP, OUTPUT - which the engine intercepts before
    /// primitive dispatch, so they are not callable procedures. They are kept here purely so the
    /// quick reference and "did you mean" suggestions can mention them.
    /// </summary>
    public IReadOnlyList<Keyword> Keywords => _keywords;

    private readonly List<Keyword> _keywords = new();

    /// <summary>Document a syntactic word that the engine handles itself.</summary>
    public void AddKeyword(string name, string doc, string example, CommandFamily family) =>
        _keywords.Add(new Keyword(name, doc, example, family));

    /// <summary>One documented syntactic word.</summary>
    public sealed record Keyword(string Name, string Doc, string Example, CommandFamily Family);

    /// <summary>Build the full Apple Logo II primitive set.</summary>
    public static PrimitiveTable CreateDefault()
    {
        var table = new PrimitiveTable();
        TurtlePrimitives.Register(table);
        MathPrimitives.Register(table);
        WordPrimitives.Register(table);
        ListPrimitives.Register(table);
        DataPrimitives.Register(table);
        ControlPrimitives.Register(table);
        OutputPrimitives.Register(table);
        ScreenPrimitives.Register(table);
        SystemPrimitives.Register(table);
        SoundPrimitives.Register(table);
        return table;
    }
}
