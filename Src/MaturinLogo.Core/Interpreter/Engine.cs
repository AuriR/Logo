using MaturinLogo.Core.Primitives;
using MaturinLogo.Core.Reader;
using MaturinLogo.Core.Screen;
using MaturinLogo.Core.Turtle;
using MaturinLogo.Core.Values;

namespace MaturinLogo.Core.Interpreter;

/// <summary>EASY gives friendly errors, hints and the split screen; ADVANCED is strict and terse.</summary>
public enum LogoMode
{
    Easy,
    Advanced,
}

/// <summary>
/// What the engine needs from its host. Tests use <see cref="NullHost"/> so the interpreter runs
/// with no browser; the WASM UI supplies a host that yields to the renderer, which is what keeps
/// the page responsive while a long program runs.
/// </summary>
public interface IEngineHost
{
    /// <summary>Give the UI a chance to paint, flush graphics, and notice a break request.</summary>
    Task YieldAsync();

    /// <summary>Flush pending graphics changes to the browser without adding speed delay.</summary>
    Task FlushGraphicsAsync();

    /// <summary>True when the child pressed Esc / Alt-B.</summary>
    bool IsBreakRequested();

    /// <summary>Read one typed line for READ / READLINE / READLIST.</summary>
    Task<string?> ReadLineAsync();

    /// <summary>Ask the host to repaint the screen and the graphics page.</summary>
    void Invalidate();

    /// <summary>
    /// Play a tone for the child to hear. Returns as soon as the tone is queued rather than when it
    /// finishes: a program that waited out every note could not be broken with ESC, and a spiral
    /// with a beep in it would crawl.
    /// </summary>
    Task PlayToneAsync(double hertz, double seconds);

    /// <summary>Forget every tone still waiting, which is what turning sound off means.</summary>
    void StopTones();
}

/// <summary>Host for unit tests and headless runs: never yields, never breaks, no typed input.</summary>
public sealed class NullHost : IEngineHost
{
    public static readonly NullHost Instance = new();

    public Task YieldAsync() => Task.CompletedTask;
    public Task FlushGraphicsAsync() => Task.CompletedTask;
    public bool IsBreakRequested() => false;
    public Task<string?> ReadLineAsync() => Task.FromResult<string?>(null);
    public void Invalidate() { }
    public Task PlayToneAsync(double hertz, double seconds) => Task.CompletedTask;
    public void StopTones() { }
}

/// <summary>A name binding frame. Logo scopes dynamically, so Parent is the caller's frame.</summary>
public sealed class Frame
{
    private readonly Dictionary<string, LogoValue> _vars = new(StringComparer.Ordinal);

    public Frame? Parent { get; }
    public bool IsGlobal { get; }

    /// <summary>Names declared LOCAL in this frame; MAKE writes here rather than globally.</summary>
    public HashSet<string> DeclaredLocals { get; } = new(StringComparer.Ordinal);

    public Frame(Frame? parent, bool isGlobal = false)
    {
        Parent = parent;
        IsGlobal = isGlobal;
    }

    public bool TryGet(string name, out LogoValue value) => _vars.TryGetValue(name, out value!);

    public void Set(string name, LogoValue value) => _vars[name] = value;

    public IReadOnlyDictionary<string, LogoValue> Variables => _vars;

    /// <summary>Walk the dynamic chain to find the frame that owns <paramref name="name"/>.</summary>
    public Frame? FindOwner(string name)
    {
        for (Frame? f = this; f is not null; f = f.Parent)
            if (f._vars.ContainsKey(name)) return f;
        return null;
    }
}

/// <summary>
/// A stream of read items the evaluator pulls inputs from. Logo's semantics are "pull": a
/// procedure asks for its inputs and the evaluator reads them off the source that follows the call.
/// </summary>
public sealed class SourceStream
{
    public IReadOnlyList<ProgramItem> Items { get; }
    public int Index { get; set; }

    /// <summary>True when this stream is a procedure body itself, which enables tail calls.</summary>
    public bool IsBody { get; }

    /// <summary>True for a group inside a body, so STOP and OUTPUT written there still act on the
    /// enclosing procedure, as they do in Logo.</summary>
    public bool NestedInBody { get; }

    public SourceStream(IReadOnlyList<ProgramItem> items, bool isBody = false, bool nestedInBody = false)
    {
        Items = items;
        IsBody = isBody;
        NestedInBody = nestedInBody;
    }

    /// <summary>Inside a procedure body, either the body itself or a nested group.</summary>
    public bool InBody => IsBody || NestedInBody;

    public bool AtEnd => Index >= Items.Count;

    public ProgramItem Peek(int offset = 0) =>
        Index + offset < Items.Count ? Items[Index + offset] : ProgramItem.Bare("");

    /// <summary>A child stream for a list or group found in this one.</summary>
    public SourceStream Child(IReadOnlyList<ProgramItem> items) =>
        new(items, isBody: false, nestedInBody: InBody);
}

/// <summary>Internal control-flow signal: OUTPUT returned a value from a procedure.</summary>
public sealed class OutputSignal : Exception
{
    public LogoValue Value { get; }
    public OutputSignal(LogoValue value) : base("output") => Value = value;
}

/// <summary>Internal control-flow signal: STOP ended a procedure.</summary>
public sealed class StopSignal : Exception
{
    public StopSignal() : base("stop") { }
}

/// <summary>
/// The Logo interpreter: reads source, evaluates it with Logo's input-consuming semantics, drives
/// the turtle and writes to the text screen. Cooperative - it yields to the host every
/// <see cref="StepSlice"/> steps so an endless loop cannot freeze a WASM page.
/// </summary>
public sealed class Engine
{
    /// <summary>Evaluation steps between cooperative yields.</summary>
    public const int StepSlice = 3000;

    /// <summary>
    /// Beyond this many nested calls a program is almost certainly runaway recursion. Kept modest
    /// because WASM runs on a small stack and each Logo level costs several frames; legitimate
    /// deep recursion is tail-recursive and gets trampolined instead of nesting.
    /// </summary>
    public const int MaxDepth = 200;

    /// <summary>
    /// Beyond this many passes a WHILE/UNTIL/REPEAT loop has certainly gone wrong. The number is a
    /// tradeoff: heavy but legitimate drawing (a 3,600-step spiral, a countdown to 5,000) fits with
    /// room to spare, while a loop that never ends reports in well under a second instead of
    /// leaving a child staring at a frozen screen wondering if Logo is broken.
    /// </summary>
    public const int MaxLoopPasses = 50_000;

    /// <summary>
    /// How many tail-call jumps one procedure may make. Tail calls are turned into jumps, so they
    /// never grow the stack and the depth guard below cannot see them: without this budget,
    /// <c>TO FOREVER OUTPUT FOREVER END</c> would spin forever instead of reporting. A child
    /// counting down from 20,000 fits comfortably; a loop that never ends does not.
    /// </summary>
    public const int MaxTailJumps = 100_000;

    public TextScreen Screen { get; }
    public GraphicsScene Scene { get; }
    public Workspace Workspace { get; }
    public TurtleState Turtle { get; }
    public TurtleGraphics TurtleView { get; }
    public IEngineHost Host { get; set; }
    public LogoMode Mode { get; set; } = LogoMode.Easy;

    /// <summary>When false, quoted and piped words keep mixed case (SETCASE FALSE).</summary>
    public bool FoldCase { get; set; } = true;

    /// <summary>Lines of Logo the child has written this session; shown on the Alt-I dashboard.</summary>
    public int LinesWritten { get; set; }

    /// <summary>Set by the host when the child asks to break out.</summary>
    public bool BreakRequested { get; set; }

    /// <summary>TRUE/FALSE remembered by TEST, which IF with only one branch reads.</summary>
    public bool LastTest { get; set; }

    /// <summary>The 1-based pass number of the innermost REPEAT, readable as :COUNTER.</summary>
    public int CurrentLoopIndex { get; set; }

    /// <summary>The item FOREACH is working on, readable as the special name ?.</summary>
    public LogoValue LoopItem { get; set; } = LogoValue.None;

    /// <summary>How many CATCH forms are currently on the way in, so THROW can refuse without one.</summary>
    public int ThrowDepth { get; set; }

    /// <summary>The value a THROW is carrying back to its CATCH.</summary>
    public LogoValue? ThrownValue { get; set; }

    /// <summary>Let a primitive check for Esc at the top of a long-running loop.</summary>
    public Task StepForBreakAsync() => StepAsync();

    /// <summary>Seed chosen with RANDSEED; null means Logo's own changing seed.</summary>
    public int? RandomSeed { get; set; }

    /// <summary>The random source primitives draw from, re-created when a seed is chosen.</summary>
    public Random Random { get; private set; } = new();

    public void ResetRandom() => Random = RandomSeed is int seed ? new Random(seed) : new Random();

    /// <summary>Set when BYE is typed, so the host can save and return to the splash screen.</summary>
    public bool ByeRequested { get; private set; }

    public void RequestBye() => ByeRequested = true;

    public void ClearBye() => ByeRequested = false;

    /// <summary>Set when SETTINGS is typed; the host opens the settings overlay once the line returns.</summary>
    public bool SettingsRequested { get; private set; }

    public void RequestSettings() => SettingsRequested = true;

    public void ClearSettings() => SettingsRequested = false;

    /// <summary>Set when VIEWCOLORS is typed; the host opens the colour chart once the line returns.</summary>
    public bool ColorsRequested { get; private set; }

    public void RequestColors() => ColorsRequested = true;

    public void ClearColors() => ColorsRequested = false;

    /// <summary>Set when VIEWSHAPES is typed; the host opens the shape chart once the line returns.</summary>
    public bool ShapesRequested { get; private set; }

    public void RequestShapes() => ShapesRequested = true;

    public void ClearShapes() => ShapesRequested = false;

    /// <summary>
    /// How fast a program draws, on Logo's 25-100 scale where 100 is as fast as the machine goes
    /// and 25 is the slowest useful speed. SETSPEED sets it; the host turns it into a pause per
    /// animation frame.
    /// </summary>
    public int Speed { get; set; } = 50;

    /// <summary>Slowest speed SETSPEED accepts; the range the child asked for, 25 to 100.</summary>
    public const int MinSpeed = 25;

    /// <summary>Fastest speed, which means "no pause at all".</summary>
    public const int MaxSpeed = 100;

    /// <summary>
    /// True when Logo may make a sound. Off is for a classroom, a shared computer, and a child who
    /// does not want to be heard; SETSOUND and the settings screen both reach it, and it is
    /// remembered with the monitor colour rather than per session.
    /// </summary>
    public bool SoundEnabled { get; set; } = true;

    /// <summary>Ask the host for a tone, unless sound is off - which is the one check every caller would otherwise repeat.</summary>
    public Task PlaySoundAsync(double hertz, double seconds) =>
        SoundEnabled ? Host.PlayToneAsync(hertz, seconds) : Task.CompletedTask;

    /// <summary>Silence anything queued. Safe to call when nothing is playing.</summary>
    public void StopSounds() => Host.StopTones();

    /// <summary>
    /// True when the operating system has been asked to reduce motion, in which case speed is
    /// ignored and everything draws instantly.
    /// </summary>
    /// <remarks>
    /// This is the accessibility setting a user turned on for every application on their machine,
    /// and a Logo that animates a spiral over several seconds is exactly the kind of motion they
    /// asked not to be given. It overrides SETSPEED rather than merely setting a default, because
    /// the alternative is a child who cannot switch it off from the keyboard.
    /// </remarks>
    public bool InstantDraw { get; set; }

    /// <summary>Milliseconds to pause on each animation frame at <paramref name="speed"/>.</summary>
    public static int FrameDelayMs(int speed) => speed >= MaxSpeed ? 0 : (MaxSpeed - speed) * 6;


    /// <summary>Work out a single stored value as an expression, used to re-run a loop test.</summary>
    public async Task<LogoValue> EvalOneValueAsync(LogoValue value, Frame env)
    {
        // A list written as a loop test, like WHILE [LESS :N 5], holds program items rather than
        // data, so its contents are run as an expression instead of being read as a literal.
        if (value.Kind == LogoKind.List && !value.IsParenGroup && LooksLikeExpression(value))
        {
            var items = new List<ProgramItem>(value.Items.Count);
            foreach (var element in value.Items) items.Add(ValueToItem(element));
            var expressionStream = new SourceStream(items);
            LogoValue result = LogoValue.None;
            while (!expressionStream.AtEnd) result = await EvalAsync(expressionStream, env);
            return result;
        }

        var single = new SourceStream(new[] { ValueToItem(value) });
        return await EvalAsync(single, env);
    }

    /// <summary>True when a list starts with a known command name, i.e. it is code rather than data.</summary>
    private bool LooksLikeExpression(LogoValue list) =>
        list.Items.Count > 0 && list.Items[0].Kind == LogoKind.Word &&
        (Primitives.Contains(list.Items[0].Word) || Workspace.HasProcedure(list.Items[0].Word));

    /// <summary>
    /// Which layout the screen is using. Apple Logo swapped the whole screen for graphics; EASY
    /// mode keeps a live text strip at the bottom (LOWER n) so a child can type and watch at once.
    /// </summary>
    public ScreenLayout Layout { get; private set; } = ScreenLayout.FullText;

    /// <summary>How many text rows the LOWER split reserves, when in Split mode.</summary>
    public int LowerLines { get; private set; } = 4;

    /// <summary>Raised when the layout changes so the host can resize the canvas.</summary>
    public event Action? LayoutChanged;

    /// <summary>TEXT: give the whole screen back to text.</summary>
    public void ShowFullText()
    {
        Layout = ScreenLayout.FullText;
        Screen.SetTextWindow(0);
        LayoutChanged?.Invoke();
    }

    /// <summary>GRAPH: give the whole screen to the graphics page.</summary>
    public void ShowFullGraphics()
    {
        Layout = ScreenLayout.Split;
        LowerLines = 0;
        Screen.SetTextWindow(TextScreen.Rows);
        LayoutChanged?.Invoke();
    }

    /// <summary>LOWER n: reserve the top of the screen for graphics, leaving n text rows below.</summary>
    public void ShowLower(int lines)
    {
        lines = Math.Clamp(lines, 1, TextScreen.Rows - 2);
        Layout = ScreenLayout.Split;
        LowerLines = lines;
        Screen.SetTextWindow(TextScreen.Rows - lines);
        LayoutChanged?.Invoke();
    }

    /// <summary>Which of the two Apple Logo screen layouts is active.</summary>
    public enum ScreenLayout
    {
        FullText,
        Split,
    }

    public PrimitiveTable Primitives { get; }

    private Frame? _globalFrame;
    private Frame GlobalFrame => _globalFrame ??= new Frame(parent: null, isGlobal: true);

    /// <summary>The frame currently being executed, used by MAKE / THING and the screen primitives.</summary>
    public Frame CurrentFrame { get; private set; }

    private int _steps;

    /// <summary>
    /// Set by turtle primitives when they change the scene. When non-zero, StepAsync flushes
    /// graphics to the browser so changes are visible before the next command runs. This is what
    /// makes short programs like REPEAT 4 [FD 50 RT 90] animate instead of appearing all at once.
    /// </summary>
    private int _pendingGraphics;

    /// <summary>True after the first yield boundary has been reached during this program run.
    /// Once true, we stop flushing on every graphics change (too expensive for long programs)
    /// and rely on yield-boundary flushes instead.</summary>
    private bool _hasYielded;

    public void MarkGraphicsDirty() => _pendingGraphics++;
    private int _depth;

    /// <summary>Tail-call jumps made by the current top-level run, bounded by <see cref="MaxTailJumps"/>.</summary>
    private int _tailJumps;

    private PendingTail? _pendingTail;

    /// <summary>
    /// Whether a procedure call reaching the end of the current stream may be replayed as a jump
    /// instead of a deeper frame. This is the interpreter's "am I in tail position?" bit, and it is
    /// only ever true for the value an <c>OUTPUT</c> hands back, or for the body's last statement.
    /// <para>
    /// It is cleared the moment evaluation dives into something whose result gets combined or
    /// consumed - an operator's right side, another call's inputs, a bracketed list, a group in
    /// parentheses. That is what keeps <c>OUTPUT :N * FACT :N - 1</c> honest: FACT is an operand, so
    /// it nests, while <c>OUTPUT COUNTDOWN :N - 1</c> is the whole answer, so it jumps.
    /// </para>
    /// </summary>
    private bool _tailPosition;

    private sealed class PendingTail
    {
        public UserProcedure Procedure { get; init; } = null!;
        public IReadOnlyList<LogoValue> Args { get; init; } = Array.Empty<LogoValue>();
        public Frame Env { get; init; } = null!;
    }

    public Engine(TextScreen? screen = null, GraphicsScene? scene = null, IEngineHost? host = null)
    {
        Screen = screen ?? new TextScreen();
        Scene = scene ?? new GraphicsScene();
        Workspace = new Workspace();
        Turtle = new TurtleState();
        Host = host ?? NullHost.Instance;
        CurrentFrame = GlobalFrame;
        Primitives = PrimitiveTable.CreateDefault();
        Workspace.IsPrimitiveName = Primitives.Contains;
        TurtleView = new TurtleGraphics(Scene, Turtle);
    }

    // ------------------------------------------------------------------ entry points

    /// <summary>Run Logo source in command position; returns the value of the last expression.</summary>
    public async Task<LogoValue> RunAsync(string source)
    {
        var items = LogoReader.ReadProgram(source, FoldCase);
        LinesWritten += CountSourceLines(source);
        _tailJumps = 0;
        ResetGraphicsFlushState();
        try
        {
            return await RunStreamAsync(new SourceStream(items), GlobalFrame);
        }
        finally
        {
            // Flush any remaining graphics so short programs animate completely.
            // Swallow flush errors here: a failed flush must not mask the program's real error.
            try { await FlushPendingGraphicsAsync(); } catch { /* best effort */ }
        }
    }

    /// <summary>Evaluate text as an expression (used by tests and EVAL).</summary>
    public async Task<LogoValue> EvalTextAsync(string source)
    {
        var items = LogoReader.ReadProgram(source, FoldCase);
        if (items.Count == 0)
            throw new LogoException(LogoErrorCode.NoValue, "I need something to evaluate");
        _tailJumps = 0;
        var stream = new SourceStream(items);
        LogoValue value = LogoValue.None;
        while (!stream.AtEnd) value = await EvalAsync(stream, GlobalFrame);
        return value;
    }

    /// <summary>Execute a list of values as commands - the implementation behind RUN.</summary>
    public async Task<LogoValue> RunListAsync(LogoValue list, Frame env, bool inBody = false)
    {
        // A bracketed list is its own sequence: the value it ends with is handed to whoever ran it,
        // so a call inside it is not the caller's tail. OUTPUT re-arms the flag for its own list.
        _tailPosition = false;
        _pendingTail = null;
        if (list.Kind != LogoKind.List)
            throw new LogoException(LogoErrorCode.BadInputType,
                "RUN needs a list to run",
                "Try RUN [FD 10 RT 90] - the input goes in brackets.",
                subject: Printer.Format(list, true));

        var items = new List<ProgramItem>(list.Items.Count);
        foreach (var element in list.Items) items.Add(ValueToItem(element));
        return await RunStreamAsync(new SourceStream(items, nestedInBody: inBody), env);
    }

    /// <summary>Stop at the next yield point; wired to Esc.</summary>
    public void RequestBreak() => BreakRequested = true;

    // ------------------------------------------------------------------ stepping

    private async Task StepAsync()
    {
        // Two separate concerns: flushing graphics to the browser, and applying the SETSPEED delay.
        // Before the first yield boundary (short programs), flush on every graphics change so the
        // child sees each step animate. After that (long programs), only flush at yield boundaries
        // to avoid the overhead of 60,000+ browser calls for a spiral.

        if (!_hasYielded && _pendingGraphics > 0)
        {
            _pendingGraphics = 0;
            await Host.FlushGraphicsAsync();
        }

        if (_steps++ < StepSlice) return;
        _steps = 0;
        _hasYielded = true;
        if (BreakRequested || Host.IsBreakRequested())
            throw new LogoException(LogoErrorCode.BreakRequested, "BREAK");
        await Host.YieldAsync();
        if (BreakRequested || Host.IsBreakRequested())
            throw new LogoException(LogoErrorCode.BreakRequested, "BREAK");
    }

    /// <summary>Flush any pending graphics changes. Called when a program finishes so short programs animate.</summary>
    public async Task FlushPendingGraphicsAsync()
    {
        if (_pendingGraphics > 0)
        {
            _pendingGraphics = 0;
            await Host.FlushGraphicsAsync();
        }
    }

    /// <summary>Reset the graphics-flush state at the start of a new program run.</summary>
    public void ResetGraphicsFlushState() => _hasYielded = false;

    private static int CountSourceLines(string source)
    {
        if (source.Trim().Length == 0) return 0;
        int count = 1;
        foreach (char c in source) if (c == '\n') count++;
        return count;
    }

    // ------------------------------------------------------------------ command execution

    /// <summary>Run commands until the stream is exhausted, returning the last value seen.</summary>
    public async Task<LogoValue> RunStreamAsync(SourceStream stream, Frame env)
    {
        LogoValue last = LogoValue.None;
        while (!stream.AtEnd)
            last = await RunOneCommandAsync(stream, env);
        return last;
    }

    private async Task<LogoValue> RunOneCommandAsync(SourceStream stream, Frame env)
    {
        // Each statement starts fresh: a jump candidate can only come from the value this statement
        // is actually handing back, never from one that finished earlier in the body.
        _tailPosition = false;
        ProgramItem item = stream.Items[stream.Index++];
        await StepAsync();

        switch (item.Kind)
        {
            case ItemKind.BareWord:
                return await RunBareWordAsync(item, stream, env);

            case ItemKind.Paren:
                return await RunGroupAsync(item.Children, stream, env, requireValue: false);

            case ItemKind.List:
                // A bracketed list alone on a line is a command list, the same as RUN.
                return await RunListAsync(LogoReader.ToLiteral(item), env, stream.InBody);

            case ItemKind.QuotedWord:
                // "WORD on its own line prints itself - how children discover quoting.
                Screen.WriteLine(item.Text);
                return LogoValue.FromWord(item.Text);

            default:
                // A number or variable reference in command position simply evaluates.
                return await EvalAsync(stream, env, item);
        }
    }

    /// <summary>
    /// A parenthesized group, which is how Logo calls something with a chosen number of inputs:
    /// <c>(FD 10 RT 90)</c> runs two commands, <c>(PRINT "A "B)</c> hands PRINT both words, and
    /// <c>(GREET)</c> lets a procedure use its defaults.
    /// <para>
    /// The head's own arity decides how much it takes. A fixed-input command takes exactly that
    /// many and anything after it runs as a separate command; a variadic command (PRINT, WORD) or
    /// a procedure with optional inputs takes everything left.
    /// </para>
    /// </summary>
    private async Task<LogoValue> RunGroupAsync(IReadOnlyList<ProgramItem> children, SourceStream stream, Frame env, bool requireValue)
    {
        if (children.Count == 0)
        {
            if (requireValue)
                throw new LogoException(LogoErrorCode.NoValue, "() has no value to hand back",
                    "Put a reporter inside the brackets, like (DOUBLE 5).", line: 0);
            return LogoValue.None;
        }

        ProgramItem head = children[0];
        // A group's value is handed to whatever asked for it, so a call inside the brackets is not
        // the caller's tail; the trampoline would have to model the group's own frame to be honest.
        _tailPosition = false;
        if (head.Kind == ItemKind.BareWord && !IsSyntacticWord(head.Text))
        {
            bool takesEverything = false;
            bool isCallable = false;

            if (Primitives.TryGet(head.Text, out Primitive? primitive) && primitive is not null && !primitive.Syntactic)
            {
                isCallable = true;
                takesEverything = primitive.Variadic;
            }
            else if (Workspace.TryGetProcedure(head.Text, out UserProcedure? proc) && proc is not null)
            {
                isCallable = true;
                // A procedure with optional or catch-all inputs wants everything that follows.
                takesEverything = proc.Formals.Any(f => f.Kind != FormalKind.Required);
            }

            if (isCallable)
            {
                var rest = new List<ProgramItem>(children.Count - 1);
                for (int i = 1; i < children.Count; i++) rest.Add(children[i]);
                var argStream = new SourceStream(rest, nestedInBody: stream.InBody);

                var args = new List<LogoValue>();
                if (takesEverything)
                {
                    while (!argStream.AtEnd) args.Add(await EvalAsync(argStream, env));
                }
                else
                {
                    int wanted = Primitives.TryGet(head.Text, out Primitive? p) && p is not null
                        ? p.Inputs
                        : Workspace.ProcedureRequiredInputs(head.Text);
                    for (int i = 0; i < wanted; i++)
                    {
                        if (argStream.AtEnd)
                            throw new LogoException(LogoErrorCode.NotEnoughInputs,
                                ErrorText.NotEnoughInputs(head.Text, wanted, i, Mode),
                                $"({head.Text} ...) needs {wanted} input{(wanted == 1 ? "" : "s")} inside the brackets.",
                                subject: head.Text);
                        args.Add(await EvalAsync(argStream, env));
                    }
                }

                LogoValue result = Primitives.TryGet(head.Text, out Primitive? direct) && direct is not null
                    ? await InvokePrimitiveDirectAsync(direct, args, env)
                    : await InvokeUserAsync(Workspace.RequireProcedure(head.Text), args, env);

                // Anything the head did not take is still part of this group.
                while (!argStream.AtEnd) result = await RunOneCommandAsync(argStream, env);

                if (requireValue && result.Kind == LogoKind.None)
                    throw new LogoException(LogoErrorCode.NoValue,
                        $"({string.Join(" ", children)}) has no value to hand back",
                        "Only a reporter (something that OUTPUTs) can be used inside ().", head.Line);
                return result;
            }
        }

        // No callable head, so this group is an expression rather than a list of commands:
        // (:N - 1) and (3 + 4) are how a child writes the maths they mean. Running these items in
        // command position instead would try to execute "-" as a verb and answer "I don't know the
        // name -", which is exactly the confusion to avoid.
        var sequence = new SourceStream(children, nestedInBody: stream.InBody);
        LogoValue last = LogoValue.None;
        while (!sequence.AtEnd) last = await EvalAsync(sequence, env);

        if (requireValue && last.Kind == LogoKind.None)
            throw new LogoException(LogoErrorCode.NoValue,
                $"({string.Join(" ", children)}) has no value to hand back",
                "Only a reporter (something that OUTPUTs) can be used inside ().", head.Line);
        return last;
    }

    private async Task<LogoValue> RunBareWordAsync(ProgramItem item, SourceStream stream, Frame env)
    {
        string name = item.Text;

        if (string.Equals(name, "TO", StringComparison.OrdinalIgnoreCase))
        {
            DefineProcedure(stream, env);
            return LogoValue.None;
        }

        if (string.Equals(name, "END", StringComparison.OrdinalIgnoreCase))
            throw new LogoException(LogoErrorCode.UnterminatedDefinition,
                "END without a TO", "END closes a procedure that TO started.", item.Line);

        if (string.Equals(name, "STOP", StringComparison.OrdinalIgnoreCase))
        {
            if (stream.InBody) throw new StopSignal();
            return LogoValue.None;
        }

        if (string.Equals(name, "OUTPUT", StringComparison.OrdinalIgnoreCase))
        {
            if (stream.AtEnd)
                throw new LogoException(LogoErrorCode.NoValue, "OUTPUT needs something to give back",
                    "For example: OUTPUT :SIZE + 1", item.Line);

            // OUTPUT's expression is the value the procedure hands back, so a call that finishes
            // the stream here can be replayed as a jump. Anything nested inside it - an operator's
            // right side, another call's inputs - clears the flag again.
            _pendingTail = null;
            bool outerTail = _tailPosition;
            _tailPosition = stream.InBody;
            LogoValue value;
            try
            {
                value = await EvalAsync(stream, env);
            }
            finally
            {
                _tailPosition = outerTail;
            }

            if (stream.InBody) throw new OutputSignal(value);
            return value;
        }

        if (Primitives.TryGet(name, out Primitive? primitive) && primitive is not null)
            return await InvokePrimitiveAsync(primitive, stream, env, item.Line);

        if (Workspace.TryGetProcedure(name, out UserProcedure? proc) && proc is not null)
        {
            // Command position: whatever the call returns is thrown away, so nothing it contains
            // can be handed back to the caller - the flag comes off before its inputs are read.
            _tailPosition = false;
            var args = await ConsumeInputsAsync(proc, stream, env, item.Line);

            // Tail call: the body's last statement, with nothing left to run, can replace the
            // current frame instead of growing the stack. This lets a child write a deep recursive
            // spiral without running out of stack.
            if (stream.IsBody && stream.AtEnd)
            {
                _pendingTail = new PendingTail { Procedure = proc, Args = args, Env = env };
                return LogoValue.None;
            }

            return await InvokeUserAsync(proc, args, env);
        }

        if (TryLookupVariable(name, env, out LogoValue? variable) && variable!.Kind == LogoKind.List)
            return await RunListAsync(variable, env, stream.InBody);

        throw new LogoException(LogoErrorCode.NotAProcedure,
            ErrorText.NotAProcedure(name, Mode),
            ErrorText.HintNotAProcedure(name, Primitives, Workspace), item.Line, name);
    }

    private async Task<LogoValue> InvokePrimitiveAsync(Primitive primitive, SourceStream stream, Frame env, int line)
    {
        IReadOnlyList<LogoValue> args = primitive.Syntactic
            ? Array.Empty<LogoValue>()
            : await ConsumeInputsAsync(primitive, stream, env, line);

        var saved = CurrentFrame;
        CurrentFrame = env;
        try
        {
            if (primitive.Syntactic && primitive.StreamHandler is not null)
                return await primitive.StreamHandler(this, stream, env);
            if (primitive.Handler is not null)
                return await primitive.Handler(this, args, env);
            throw new LogoException(LogoErrorCode.Internal, $"{primitive.Name} is not implemented yet");
        }
        finally
        {
            CurrentFrame = saved;
            await StepAsync();
        }
    }

    /// <summary>
    /// Call a primitive with inputs that are already values. Used by APPLY, and by anything that
    /// has to invoke a procedure it was handed as data.
    /// </summary>
    public async Task<LogoValue> InvokePrimitiveDirectAsync(Primitive primitive, IReadOnlyList<LogoValue> args, Frame env)
    {
        var saved = CurrentFrame;
        CurrentFrame = env;
        try
        {
            if (primitive.Handler is not null)
                return await primitive.Handler(this, args, env);
            throw new LogoException(LogoErrorCode.Internal, $"{primitive.Name} cannot be applied directly");
        }
        finally
        {
            CurrentFrame = saved;
        }
    }

    /// <summary>Invoke a user procedure, trampolining tail calls so deep recursion stays cheap.</summary>
    public async Task<LogoValue> InvokeUserAsync(UserProcedure proc, IReadOnlyList<LogoValue> args, Frame callerEnv)
    {
        if (++_depth > MaxDepth)
        {
            _depth--;
            throw new LogoException(LogoErrorCode.RunawayLoop,
                ErrorText.Runaway(Mode), ErrorText.RunawayHint(Mode), subject: proc.Name);
        }

        try
        {
            UserProcedure current = proc;
            IReadOnlyList<LogoValue> currentArgs = args;
            Frame caller = callerEnv;

            while (true)
            {
                _pendingTail = null;
                var frame = BindFormals(current, currentArgs, caller);
                var saved = CurrentFrame;
                CurrentFrame = frame;
                LogoValue result;
                try
                {
                    result = await RunBodyAsync(current.Body, frame);
                }
                catch (OutputSignal out_)
                {
                    result = out_.Value;
                }
                catch (StopSignal)
                {
                    result = LogoValue.None;
                }
                finally
                {
                    CurrentFrame = saved;
                }

                var tail = _pendingTail;
                _pendingTail = null;
                if (tail is null) return result;

                // A jump costs no stack, so the depth guard above cannot see runaway tail
                // recursion; this budget is what turns "TO FOREVER OUTPUT FOREVER END" into a
                // message rather than a frozen page.
                if (++_tailJumps > MaxTailJumps)
                    throw new LogoException(LogoErrorCode.RunawayLoop,
                        ErrorText.Runaway(Mode), ErrorText.RunawayHint(Mode), subject: tail.Procedure.Name);

                current = tail.Procedure;
                currentArgs = tail.Args;
                caller = tail.Env;
            }
        }
        finally
        {
            _depth--;
        }
    }

    private async Task<LogoValue> RunBodyAsync(IReadOnlyList<ProgramItem> body, Frame env)
    {
        _tailPosition = false;
        var stream = new SourceStream(body, isBody: true);
        LogoValue last = LogoValue.None;
        while (!stream.AtEnd)
            last = await RunOneCommandAsync(stream, env);
        return last;
    }

    private Frame BindFormals(UserProcedure proc, IReadOnlyList<LogoValue> args, Frame callerEnv)
    {
        var frame = new Frame(callerEnv);
        for (int i = 0; i < proc.Formals.Count; i++)
        {
            var formal = proc.Formals[i];
            switch (formal.Kind)
            {
                case FormalKind.Required:
                    frame.Set(formal.Name, i < args.Count ? args[i] : LogoValue.None);
                    break;
                case FormalKind.Optional:
                    frame.Set(formal.Name, i < args.Count ? args[i] : formal.Default ?? LogoValue.None);
                    break;
                case FormalKind.Rest:
                    var rest = new List<LogoValue>();
                    for (int j = i; j < args.Count; j++) rest.Add(args[j]);
                    frame.Set(formal.Name, LogoValue.FromList(rest));
                    break;
            }
        }
        return frame;
    }

    // ------------------------------------------------------------------ expression evaluation

    /// <summary>Read one item from the stream and evaluate it as an expression.</summary>
    public async Task<LogoValue> EvalAsync(SourceStream stream, Frame env)
    {
        if (stream.AtEnd)
            throw new LogoException(LogoErrorCode.NotEnoughInputs, "That needs one more input",
                "Every command needs the number of inputs it asks for - type HELP for a list.");
        var item = stream.Items[stream.Index++];
        return await ApplyInfixAsync(stream, env, await EvalAsync(stream, env, item));
    }

    /// <summary>
    /// Logo's arithmetic reads as maths rather than as nested calls: after a value, a following
    /// <c>+ - * /</c> takes the value on its left and one operand on its right. Strictly left to
    /// right with no precedence, exactly as Apple Logo worked - so <c>2 + 3 * 4</c> is 20, not 14.
    /// </summary>
    private async Task<LogoValue> ApplyInfixAsync(SourceStream stream, Frame env, LogoValue left)
    {
        while (!stream.AtEnd && stream.Peek().Kind == ItemKind.BareWord &&
               IsInfixOperator(stream.Peek().Text))
        {
            string op = stream.Items[stream.Index++].Text;
            // Once an operator is seen, this expression's value is a computed thing rather than
            // something a call handed back, so no later call in it can be a tail call.
            _tailPosition = false;
            // The right side is one operand only; a following operator belongs to the whole result,
            // which is what keeps the reading left-to-right.
            LogoValue right = await EvalOperandAsync(stream, env, op);
            left = Arithmetic.Apply(op, left, right, Mode);
        }
        return left;
    }

    /// <summary>The operators Logo treats as infix.</summary>
    internal static bool IsInfixOperator(string name) => name is "+" or "-" or "*" or "/";

    /// <summary>
    /// Words the engine intercepts before dispatch, so they cannot be treated as the head of a
    /// parenthesized call - <c>(TO ...)</c> is not a thing Logo does.
    /// </summary>
    private static bool IsSyntacticWord(string name) =>
        name.Equals("TO", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("END", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("STOP", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("OUTPUT", StringComparison.OrdinalIgnoreCase);

    /// <summary>Evaluate exactly one operand, without absorbing any operators that follow it.</summary>
    private async Task<LogoValue> EvalOperandAsync(SourceStream stream, Frame env, string op)
    {
        if (stream.AtEnd)
            throw new LogoException(LogoErrorCode.NotEnoughInputs,
                ErrorText.NotEnoughInputs(op, 2, 1, Mode),
                $"{op} needs a number on each side, like 2 {op} 3.", subject: op);
        var item = stream.Items[stream.Index++];
        return await EvalAsync(stream, env, item);
    }

    /// <summary>Evaluate an already-read item as an expression.</summary>
    public async Task<LogoValue> EvalAsync(SourceStream stream, Frame env, ProgramItem item)
    {
        await StepAsync();

        switch (item.Kind)
        {
            case ItemKind.Number:
                return LogoValue.FromNumber(item.Number);

            case ItemKind.QuotedWord:
                return LogoValue.FromWord(item.Text);

            case ItemKind.List:
                return LogoReader.ToLiteral(item);

            case ItemKind.VarRef:
                if (TryLookupVariable(item.Text, env, out LogoValue? value)) return value!;
                if (string.Equals(item.Text, "?", StringComparison.Ordinal))
                    throw new LogoException(LogoErrorCode.NoValue, "? only has a value inside FOREACH",
                        "Try FOREACH [1 2 3] [PRINT ?] - ? is the item being worked on.", item.Line, item.Text);
                throw new LogoException(LogoErrorCode.NoValue,
                    ErrorText.NoValue(item.Text, Mode),
                    ErrorText.HintNoValue(item.Text, Primitives, Workspace), item.Line, item.Text);

            case ItemKind.Paren:
                // In expression position a group must hand back a value, and its head may take a
                // chosen number of inputs - which is how (GREET) uses defaults and (PRINT "A "B)
                // prints two things.
                return await RunGroupAsync(item.Children, stream, env, requireValue: true);

            case ItemKind.BareWord:
                return await EvalBareWordAsync(item, stream, env);

            default:
                return LogoValue.None;
        }
    }

    private async Task<LogoValue> EvalBareWordAsync(ProgramItem item, SourceStream stream, Frame env)
    {
        string name = item.Text;

        if (Primitives.TryGet(name, out Primitive? primitive) && primitive is not null)
        {
            if (primitive.Syntactic && primitive.StreamHandler is not null)
            {
                var savedFrame = CurrentFrame;
                CurrentFrame = env;
                try { return await primitive.StreamHandler(this, stream, env); }
                finally { CurrentFrame = savedFrame; }
            }
            return await InvokePrimitiveAsync(primitive, stream, env, item.Line);
        }

        if (Workspace.TryGetProcedure(name, out UserProcedure? proc) && proc is not null)
        {
            // This is the path an OUTPUT takes, and the one that was missing: without it a
            // tail-recursive reporter like COUNTDOWN nested one real frame per call and overflowed
            // the stack. The flag is dropped before the inputs are read, because an input's value
            // is fed to this call rather than returned, so nothing inside it can be a tail call.
            bool tail = _tailPosition;
            _tailPosition = false;
            var args = await ConsumeInputsAsync(proc, stream, env, item.Line);
            if (tail && stream.AtEnd)
            {
                _pendingTail = new PendingTail { Procedure = proc, Args = args, Env = env };
                return LogoValue.None;
            }

            return await InvokeUserAsync(proc, args, env);
        }

        if (TryLookupVariable(name, env, out LogoValue? value)) return value!;

        // TRUE and FALSE are values, not procedures, so `SETCASE FALSE` and `SETSOUND FALSE` mean
        // what the manual says they mean. Logo prints a boolean as one of those two words, and until
        // now reading it back as a bare word fell through to "I don't know the name FALSE" - which
        // made the printed form of a boolean unusable as its own input.
        //
        // Last in the chain rather than first, so nothing that used to resolve stops resolving: a
        // child who defined a procedure named TRUE, or made a variable called FALSE, keeps theirs.
        // This is the rule that let `MAKE 1 "X` through untouched for the same reason.
        if (name is "TRUE") return LogoValue.True;
        if (name is "FALSE") return LogoValue.False;

        throw new LogoException(LogoErrorCode.NotAProcedure,
            ErrorText.NotAProcedure(name, Mode),
            ErrorText.HintNotAProcedure(name, Primitives, Workspace), item.Line, name);
    }

    /// <summary>Consume the inputs a primitive declares, evaluating each one from the stream.</summary>
    private async Task<IReadOnlyList<LogoValue>> ConsumeInputsAsync(Primitive primitive, SourceStream stream, Frame env, int line)
    {
        // Inputs are fed to this call, not handed back to the caller, so nothing inside them is a
        // tail call. Same reasoning for the procedure overload below.
        _tailPosition = false;

        // A variadic primitive declares Inputs = -1, meaning "as many as there are"; a list
        // cannot be given a negative capacity, so the hint is clamped before it is used.
        var args = new List<LogoValue>(Math.Max(0, primitive.Inputs));
        for (int i = 0; i < primitive.Inputs; i++)
        {
            if (stream.AtEnd)
            {
                if (primitive.Variadic) break;
                throw new LogoException(LogoErrorCode.NotEnoughInputs,
                    ErrorText.NotEnoughInputs(primitive.Name, primitive.Inputs, args.Count, Mode),
                    ErrorText.HintInputs(primitive), line, primitive.Name);
            }
            args.Add(await EvalAsync(stream, env));
        }

        if (primitive.Variadic)
        {
            // A variadic primitive keeps eating inputs to the end of the group, but stops at a
            // following command name so "PRINT 1 FD 100" does not swallow the FD.
            while (!stream.AtEnd && !StartsNewCommand(stream.Peek()))
                args.Add(await EvalAsync(stream, env));
        }

        if (!primitive.Variadic && args.Count != primitive.Inputs)
            throw new LogoException(LogoErrorCode.NotEnoughInputs,
                ErrorText.NotEnoughInputs(primitive.Name, primitive.Inputs, args.Count, Mode),
                ErrorText.HintInputs(primitive), line, primitive.Name);

        return args;
    }

    private async Task<IReadOnlyList<LogoValue>> ConsumeInputsAsync(UserProcedure proc, SourceStream stream, Frame env, int line)
    {
        _tailPosition = false;
        var args = new List<LogoValue>(proc.RequiredInputs);
        for (int i = 0; i < proc.RequiredInputs; i++)
        {
            if (stream.AtEnd)
                throw new LogoException(LogoErrorCode.NotEnoughInputs,
                    ErrorText.NotEnoughInputs(proc.Name, proc.RequiredInputs, args.Count, Mode),
                    $"TO {proc.Name} needs {proc.RequiredInputs} input{(proc.RequiredInputs == 1 ? "" : "s")} - for example {ExampleFor(proc)}.",
                    line, proc.Name);
            args.Add(await EvalAsync(stream, env));
        }
        return args;
    }

    /// <summary>
    /// A following item starts a new command when it is a bare word naming a <em>command</em> -
    /// something that acts rather than reports. This keeps <c>FD 10 RT 90</c> as two calls and
    /// <c>PRINT 1 PRINT 2</c> as two lines, while still letting <c>PRINT SUM 3 4</c> and
    /// <c>PRINT IFELSE ...</c> work, because a reporter can only ever be an input.
    /// </summary>
    private bool StartsNewCommand(ProgramItem item)
    {
        if (item.Kind != ItemKind.BareWord) return false;

        if (Primitives.TryGet(item.Text, out Primitive? primitive) && primitive is not null)
            return !primitive.IsReporter;

        if (Workspace.TryGetProcedure(item.Text, out UserProcedure? proc) && proc is not null)
            return !proc.IsReport;

        // An unknown word is left for the evaluator to complain about rather than swallowed.
        return false;
    }

    // ------------------------------------------------------------------ TO ... END

    private void DefineProcedure(SourceStream stream, Frame env)
    {
        if (stream.AtEnd)
            throw new LogoException(LogoErrorCode.UnterminatedDefinition, "TO needs a name",
                "Try TO SQUARE, then the lines the procedure should run, then END.");

        string name = stream.Items[stream.Index++].AsWordText();
        if (name.Length == 0)
            throw new LogoException(LogoErrorCode.UnterminatedDefinition, "TO needs a name");

        // A name has to be callable. `TO SQUARE:` used to define a procedure named SQUARE:, which
        // could then never be run - the listener reads SQUARE: as a name and a colon. Refusing at
        // definition time tells the child now, instead of leaving a procedure that looks saved and
        // is unreachable.
        if (!Reader.LogoReader.IsValidName(name))
            throw new LogoException(LogoErrorCode.BadName,
                ErrorText.BadName(name, Mode), ErrorText.HintBadName("procedure name", Mode), subject: name);

        // A child cannot redefine Logo's own words - neither the syntactic ones the engine
        // intercepts, nor any primitive. Otherwise TO REPEAT would silently break the language.
        if (PrimitiveTable.IsReserved(name) || Primitives.Contains(name))
            throw new LogoException(LogoErrorCode.ReadOnly,
                $"I can't teach {name} new tricks - that is a Logo word",
                ErrorText.HintReserved(name, Mode), subject: name);

        var formals = new List<FormalInput>();
        while (!stream.AtEnd && stream.Peek().Kind == ItemKind.VarRef)
        {
            var formal = stream.Items[stream.Index++];
            if (!Reader.LogoReader.IsValidName(formal.Text))
                throw new LogoException(LogoErrorCode.BadName,
                    ErrorText.BadName(":" + formal.Text, Mode),
                    ErrorText.HintBadName("input name", Mode), subject: formal.Text);
            formals.Add(new FormalInput(formal.Text, FormalKind.Required));
        }

        // Optional inputs are written as [:NAME value]; a lone [:NAME] collects the rest.
        while (!stream.AtEnd && stream.Peek().Kind == ItemKind.List &&
               stream.Peek().Children.Count is >= 1 and <= 2 &&
               stream.Peek().Children[0].Kind == ItemKind.VarRef)
        {
            var group = stream.Items[stream.Index++];
            string formalName = group.Children[0].Text;
            if (!Reader.LogoReader.IsValidName(formalName))
                throw new LogoException(LogoErrorCode.BadName,
                    ErrorText.BadName(":" + formalName, Mode),
                    ErrorText.HintBadName("input name", Mode), subject: formalName);
            if (group.Children.Count == 2)
                formals.Add(new FormalInput(formalName, FormalKind.Optional, LogoReader.ToLiteral(group.Children[1])));
            else
                formals.Add(new FormalInput(formalName, FormalKind.Rest));
        }

        var body = new List<ProgramItem>();
        int depth = 1;
        while (!stream.AtEnd)
        {
            var item = stream.Items[stream.Index++];
            if (item.IsBare("TO")) depth++;
            else if (item.IsBare("END"))
            {
                depth--;
                if (depth == 0) break;
            }
            body.Add(item);
        }

        if (depth != 0)
            throw new LogoException(LogoErrorCode.UnterminatedDefinition,
                ErrorText.MissingEnd(name, Mode), $"Finish {name} with END on a line of its own.", subject: name);

        var proc = new UserProcedure(name, formals, body, BodyHasOutput(body));
        Workspace.SetProcedure(proc);
    }

    private static bool BodyHasOutput(IReadOnlyList<ProgramItem> body)
    {
        foreach (var item in body)
        {
            if (item.IsBare("OUTPUT")) return true;
            if (item.Kind is ItemKind.List or ItemKind.Paren && BodyHasOutput(item.Children)) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ variables

    public bool TryLookupVariable(string name, Frame env, out LogoValue? value)
    {
        // The special names Logo provides without anyone having to MAKE them.
        switch (name)
        {
            case "?":
                // ? only means something while FOREACH is working on an item.
                if (LoopItem.Kind == LogoKind.None) { value = null; return false; }
                value = LoopItem;
                return true;
            case "COUNTER":
                value = LogoValue.FromNumber(CurrentLoopIndex);
                return true;
            case "CASE":
                value = LogoValue.FromBoolean(FoldCase);
                return true;
            case "MODE":
                value = LogoValue.FromWord(Mode == LogoMode.Easy ? "EASY" : "ADVANCED");
                return true;
        }

        var owner = env.FindOwner(name);
        if (owner is not null && owner.TryGet(name, out LogoValue local))
        {
            value = local;
            return true;
        }
        if (Workspace.TryGetGlobal(name, out LogoValue? global) && global is not null)
        {
            value = global;
            return true;
        }
        value = null;
        return false;
    }

    /// <summary>MAKE's write rule: assign to the innermost existing binding, else make a global.</summary>
    public void MakeVariable(string name, LogoValue value, Frame env)
    {
        var owner = env.FindOwner(name);
        if (owner is not null)
        {
            owner.Set(name, value);
            return;
        }
        Workspace.SetGlobal(name, value);
    }

    // ------------------------------------------------------------------ property lists (PUT/GET)

    public void PropertyPut(string name, string property, LogoValue value) => Workspace.PutProperty(name, property, value);

    public bool PropertyGet(string name, string property, out LogoValue? value)
    {
        return Workspace.GetProperty(name, property, out value);
    }

    public void PropertyRemove(string name, string property) => Workspace.RemoveProperty(name, property);

    /// <summary>Declare LOCAL names in the current frame, starting empty.</summary>
    public void DeclareLocals(IEnumerable<string> names, Frame env)
    {
        foreach (var name in names)
        {
            env.DeclaredLocals.Add(name);
            if (!env.TryGet(name, out _)) env.Set(name, LogoValue.None);
        }
    }

    // ------------------------------------------------------------------ helpers

    internal static ProgramItem ValueToItem(LogoValue value) => value.Kind switch
    {
        LogoKind.Number => ProgramItem.Num(value.Number),
        LogoKind.List => ValueToItemFromList(value),
        LogoKind.Boolean => ProgramItem.Bare(value.Bool ? "TRUE" : "FALSE"),
        LogoKind.Procedure => ProgramItem.Bare(value.Word),
        LogoKind.None => ProgramItem.Bare(""),
        // A word that was stored quoted stays quoted, so RUN treats it as data rather than as the
        // name of a procedure to call.
        _ => value.IsQuoted ? ProgramItem.Quoted(value.Word) : ClassifyWord(value.Word),
    };

    private static ProgramItem ValueToItemFromList(LogoValue value)
    {
        var children = new List<ProgramItem>(value.Items.Count);
        foreach (var item in value.Items) children.Add(ValueToItem(item));
        return value.IsParenGroup ? ProgramItem.ParenItem(children) : ProgramItem.ListItem(children);
    }

    /// <summary>A stored word that began with ':' keeps its reference form so RUN can evaluate it.</summary>
    private static ProgramItem ClassifyWord(string word) =>
        word.StartsWith(':') && word.Length > 1 ? ProgramItem.Var(word.Substring(1)) : ProgramItem.Bare(word);

    private static string ExampleFor(UserProcedure proc)
    {
        var parts = new List<string> { proc.Name };
        for (int i = 0; i < Math.Min(proc.RequiredInputs, 2); i++) parts.Add("5");
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Define a procedure from stored source text, used when a session is loaded. Re-parsing
    /// rather than trusting a stored internal shape means an old session is validated against the
    /// interpreter it is about to run in.
    /// </summary>
    public bool DefineProcedureFromSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        try
        {
            var items = LogoReader.ReadProgram(source, FoldCase);

            // Stored text is a whole "TO ... END", but DefineProcedure is called with TO already
            // consumed (the engine intercepts it before defining). Stepping over it here is what
            // makes a saved procedure re-parse the same way a typed one does.
            int start = 0;
            if (items.Count > 0 && items[0].IsBare("TO")) start = 1;

            var stream = new SourceStream(items.Skip(start).ToList());
            DefineProcedure(stream, GlobalFrame);
            return true;
        }
        catch (LogoException)
        {
            // A procedure that no longer parses is skipped rather than failing the whole load;
            // the child keeps every other part of the session. Anything else is our bug, so it
            // is allowed to surface instead of hiding a broken restore behind a silent no-op.
            return false;
        }
    }

    /// <summary>Restore one stored global by reading its text back as a Logo value.</summary>
    public void RestoreGlobal(string name, string text)
    {
        try
        {
            var items = LogoReader.ReadProgram(text, FoldCase);
            if (items.Count == 0) return;
            Workspace.SetGlobal(name, LogoReader.ToLiteral(items[0]));
        }
        catch (Exception)
        {
            // Same reasoning as DefineProcedureFromSource: skip one bad value, keep the session.
        }
    }

    /// <summary>
    /// Restore one property put on a name, so (PUT "SHAPE "COLOR "RED) survives a reload. Property
    /// lists are how Logo keeps several facts about one thing, and losing them would make GET answer
    /// "no property" on something the child can see they stored.
    /// </summary>
    public void RestoreProperty(string name, string property, string text)
    {
        try
        {
            var items = LogoReader.ReadProgram(text, FoldCase);
            if (items.Count == 0) return;
            Workspace.PutProperty(name, property, LogoReader.ToLiteral(items[0]));
        }
        catch (Exception)
        {
            // Skip one bad property rather than failing the whole load.
        }
    }

    /// <summary>Reset to a fresh Logo session: no procedures, no variables, turtle home.</summary>
    public void ResetWorkspace()
    {
        Workspace.Clear();
        _globalFrame = null;
        _pendingTail = null;
        Turtle.Home();
        Turtle.PenDown = true;
        Turtle.PenColor = 2;
        Turtle.PenSize = 1;
        Turtle.Visible = true;
        LinesWritten = 0;
    }
}
