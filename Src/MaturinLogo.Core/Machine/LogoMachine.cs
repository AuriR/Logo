using System.Text;
using System.Text.Json;
using MaturinLogo.Core.Interpreter;
using MaturinLogo.Core.Primitives;
using MaturinLogo.Core.Screen;
using MaturinLogo.Core.Sessions;
using MaturinLogo.Core.Turtle;
using MaturinLogo.Core.Values;
using EngineLayout = MaturinLogo.Core.Interpreter.Engine.ScreenLayout;

namespace MaturinLogo.Core.Machine;

/// <summary>Which full-screen view owns the keyboard right now.</summary>
public enum AppScreen
{
    Splash,
    Listener,
    Help,
    Sessions,
    Editor,
    Info,
    Settings,
    About,
    WhatsNew,
    Colors,
    Shapes,
    Examples,
    Demo,
}

/// <summary>
/// The whole application except for Blazor and JavaScript: owns the engine, decides what every key
/// means, keeps the listener's line buffer, and drives sessions.
/// </summary>
/// <remarks>
/// Kept free of Razor and IJSRuntime so the key routing and the editing behaviour can be tested as
/// plain C#. The browser is reached through the delegate properties, which the component wires up
/// once after the first render.
/// </remarks>
public sealed class LogoMachine
{
    public Engine Engine { get; }
    public TextScreen Screen => Engine.Screen;

    public AppScreen ScreenKind { get; private set; } = AppScreen.Splash;

    /// <summary>Raised whenever anything on screen changed; the component repaints in response.</summary>
    public event Action? RenderRequested;

    /// <summary>
    /// Raised when Logo itself faults. The browser layer sends these to the console and an on-screen
    /// strip so a bug is diagnosable instead of invisible.
    /// </summary>
    public event Action<string>? ErrorReported;

    public void ReportError(string text) => ErrorReported?.Invoke(text);

    public string Theme { get; private set; } = "green";
    public bool CrtEmulation { get; private set; }

    public bool StorageAvailable { get; set; } = true;

    /// <summary>True while a Logo program is running, so the UI can show it is busy.</summary>
    public bool IsBusy { get; private set; }

    /// <summary>Set when a running program should stop; the host checks it while yielding.</summary>
    public bool BreakRequested { get; set; }

    // ---- browser hooks, assigned by the component -------------------------------

    public Func<string, Task<string?>>? ReadStoreFunc { get; set; }
    public Func<string, string, Task<bool>>? WriteStoreFunc { get; set; }
    public Func<string, string, Task>? DownloadFunc { get; set; }
    public Func<string, bool, Task>? SetThemeFunc { get; set; }
    public Func<string, Task<string?>>? PickFileFunc { get; set; }

    /// <summary>Hand the whole picture to the canvas: items as JSON, background, and rows of text below.</summary>
    public Func<string, int, int, Task>? ReplayGraphicsFunc { get; set; }

    /// <summary>Hand the canvas only the strokes drawn since the last call, so a long drawing is not
    /// re-sent in full every frame.</summary>
    public Func<string, Task>? AppendGraphicsFunc { get; set; }

    /// <summary>Send the turtle's live position, heading, colour and visibility to the canvas.</summary>
    public Func<string, Task>? SetTurtleFunc { get; set; }

    /// <summary>Release the browser for one frame so a long program cannot freeze the page.</summary>
    public Func<Task>? YieldFrameFunc { get; set; }

    /// <summary>
    /// Play a tone at <paramref name="hertz"/> for <paramref name="seconds"/>. Returns once the tone
    /// is queued rather than once it has finished, so a program with a beep in it can still be
    /// broken with ESC and a spiral does not crawl.
    /// </summary>
    public Func<double, double, Task>? PlayToneFunc { get; set; }

    /// <summary>Forget every tone still waiting to sound.</summary>
    public Action? StopTonesFunc { get; set; }

    // ---- session store ---------------------------------------------------------

    public SessionStore.StoreFile Store { get; private set; } = new();

    public string CurrentName { get; private set; } = "";

    public IReadOnlyList<LogoSession> Sessions => Store.Sessions;

    // ---- listener line buffer ---------------------------------------------------

    private readonly StringBuilder _line = new();
    private int _caret;
    private int _historyIndex = -1;
    private readonly List<string> _history = new();

    /// <summary>
    /// The rows the draft occupies on the grid: where it starts, which column it begins at, how
    /// much of it is shown and which of its rows are scrolled out of view. The painter owns these
    /// rows and nothing else may write into them.
    /// </summary>
    /// <remarks>
    /// Keeping the geometry as one value rather than scattered fields is what makes it movable:
    /// when the draft has to change size or position, the machine deletes the rows it owned and
    /// repaints it whole. Erasing cell by cell using the *new* geometry used to strand the *old*
    /// copy on screen, which is how a draft ended up duplicated under every Alt key that prints,
    /// and how ghosts survived a reload inside the saved transcript.
    /// </remarks>
    private sealed record PaintedBlock(int Anchor, int Base, int Length, int FirstRow, int VisibleRows, bool Prompted)
    {
        public int End => Anchor + VisibleRows - 1;
    }

    private PaintedBlock? _block;

    public IReadOnlyList<string> History => _history;

    /// <summary>The text typed but not yet run.</summary>
    public string InputText => _line.ToString();

    /// <summary>Where the caret sits within that text.</summary>
    public int InputCaret => _caret;

    /// <summary>True when a running program is waiting for the child to type an answer.</summary>
    public bool AwaitingRead => _readTcs is not null;

    private TaskCompletionSource<string?>? _readTcs;

    // ---- overlays ---------------------------------------------------------------

    private List<string> _helpLines = new();
    private int _helpTop;
    private int _sessionIndex;

    /// <summary>
    /// One stashed screen per open overlay. A stack is what lets the editor open the session
    /// manager and come back to the editor: with a single slot, the manager would return to the
    /// listener underneath and the child would lose their place in the program.
    /// </summary>
    private sealed record StashedScreen(
        string[] Chars,
        CellAttr[][] Attrs,
        (int Row, int Col) Cursor,
        AppScreen ReturnTo,
        PaintedBlock? Block);

    private readonly Stack<StashedScreen> _overlays = new();

    /// <summary>The splash's idle clock; cancelled the moment a key starts Logo.</summary>
    private CancellationTokenSource? _splashIdleCts;

    public LogoMachine()
    {
        Engine = new Engine { Host = new WasmHost(this) };
        Engine.Screen.Changed += () => RequestRender();
        Engine.LayoutChanged += () => _ = ReplayGraphicsAsync();

        // When Logo scrolls the screen, everything on it - including the rows the draft owns -
        // moves up by a row. The block has to move with it or the painter would keep clearing rows
        // it no longer draws on.
        Engine.Screen.Scrolled += count =>
        {
            if (_block is null) return;
            PaintedBlock moved = _block with { Anchor = _block.Anchor - count };
            _block = moved.Anchor < Screen.WindowTop ? null : moved;
        };
    }

    public void RequestRender() => RenderRequested?.Invoke();

    // ---- start-up ---------------------------------------------------------------

    /// <summary>
    /// Load the store from the browser, then show the splash. A first-time child is given a session
    /// immediately, so "press any key" leads straight to a working listener with somewhere to save.
    /// </summary>
    public async Task StartAsync()
    {
        if (ReadStoreFunc is not null)
        {
            string? json = await ReadStoreFunc.Invoke(SessionStore.StorageKey);
            Store = SessionStore.Deserialize(json) ?? new SessionStore.StoreFile();
        }

        Theme = string.Equals(Store.Theme, "amber", StringComparison.OrdinalIgnoreCase) ? "amber" : "green";
        CrtEmulation = Store.CrtEmulation;
        Engine.Mode = ParseMode(Store.Mode);
        Engine.Speed = Store.Speed is >= Engine.MinSpeed and <= Engine.MaxSpeed ? Store.Speed : Engine.MaxSpeed;
        Engine.SoundEnabled = Store.SoundEnabled;
        await ApplyThemeAsync();

        // A brand-new store has no current name, so open the first session rather than leaving the
        // child with a splash that promises nothing.
        CurrentName = Store.Sessions.Any(s => s.Name == Store.CurrentName)
            ? Store.CurrentName
            : Store.Sessions.FirstOrDefault()?.Name ?? "";

        if (Store.Sessions.Count == 0)
        {
            string? name = SessionNaming.NextName(Array.Empty<string>(), Store.LastIssuedSessionNumber);
            if (name is not null)
            {
                Store.Sessions.Add(SessionStore.Create(name));
                Store.LastIssuedSessionNumber = SessionNaming.NumberOf(name);
                CurrentName = name;
            }
        }

        // Persist the current selection. This is deliberately not routed through SaveAsync: a
        // session records the engine's live state, and at start-up the engine is empty, so saving
        // here would overwrite the child's stored work with nothing.
        Store.CurrentName = CurrentName;
        await WriteStoreAsync();

        ShowSplash();
    }

    private static LogoMode ParseMode(string mode) =>
        string.Equals(mode, "ADVANCED", StringComparison.OrdinalIgnoreCase) ? LogoMode.Advanced : LogoMode.Easy;

    public async Task ApplyThemeAsync()
    {
        if (SetThemeFunc is not null) await SetThemeFunc.Invoke(Theme, CrtEmulation);
        RequestRender();
    }

    public void ToggleTheme()
    {
        Theme = Theme == "green" ? "amber" : "green";
        Store.Theme = Theme;
        _ = SaveSettingsAsync();
        _ = ApplyThemeAsync();
        Say(Theme == "green" ? "GREEN MONITOR" : "AMBER MONITOR");
    }

    public void ToggleCrt()
    {
        CrtEmulation = !CrtEmulation;
        Store.CrtEmulation = CrtEmulation;
        _ = SaveSettingsAsync();
        _ = ApplyThemeAsync();
        Say(CrtEmulation ? "CRT EMULATION ON" : "CRT EMULATION OFF");
    }

    /// <summary>
    /// Turn Logo's sound on or off, for a classroom, a shared computer, or a child who does not want
    /// to be heard. Switching off also silences the notes already queued, because "off" that lets
    /// the rest of a tune finish is not off.
    /// </summary>
    public void ToggleSound()
    {
        Engine.SoundEnabled = !Engine.SoundEnabled;
        if (!Engine.SoundEnabled) Engine.StopSounds();
        Store.SoundEnabled = Engine.SoundEnabled;
        _ = SaveSettingsAsync();
        Say(Engine.SoundEnabled ? "SOUND ON" : "SOUND OFF");
    }

    public void ToggleMode()
    {
        Engine.Mode = Engine.Mode == LogoMode.Easy ? LogoMode.Advanced : LogoMode.Easy;
        Store.Mode = Engine.Mode.ToString();
        _ = SaveSettingsAsync();

        if (ScreenKind == AppScreen.Splash) return;
        Say(Engine.Mode == LogoMode.Easy
            ? "EASY MODE - HINTS ON, SPLIT SCREEN"
            : "ADVANCED MODE - STRICT ERRORS, EDITOR UNLOCKED");
    }

    /// <summary>
    /// Set the drawing speed, clamped to Logo's 25-100. The settings screen arrows move it, and
    /// SETSPEED arrives here through the engine, so both paths persist it the same way.
    /// </summary>
    public void SetSpeed(int speed)
    {
        Engine.Speed = Math.Clamp(speed, Engine.MinSpeed, Engine.MaxSpeed);
        Store.Speed = Engine.Speed;
        _ = SaveSettingsAsync();
    }

    /// <summary>
    /// The operating system's reduce-motion setting, reported by the browser. While it is on,
    /// speed is ignored and everything draws instantly - the child asked every application for
    /// that, and Logo is not special enough to override it.
    /// </summary>
    public bool ReducedMotion
    {
        get => Engine.InstantDraw;
        set => Engine.InstantDraw = value;
    }

    private Task SaveSettingsAsync()
    {
        Store.Mode = Engine.Mode.ToString();
        Store.Speed = Engine.Speed;
        Store.SoundEnabled = Engine.SoundEnabled;
        Store.CurrentName = CurrentName;
        return WriteStoreAsync();
    }

    // ---- screens ----------------------------------------------------------------

    public void ShowSplash()
    {
        DiscardOverlay();
        ScreenKind = AppScreen.Splash;
        SplashArt.Draw(Screen, CurrentName, StorageAvailable, LogoWhatsNew.IsUnread(Store.WhatsNewVersion));
        RequestRender();
        StartSplashIdleTimer();
    }

    /// <summary>
    /// The splash is an attract screen: left alone it starts the demo so a passing child sees Logo
    /// doing something. Any key press means "someone is here", which restarts the clock, and a key
    /// that starts Logo cancels it for good.
    /// </summary>
    private void StartSplashIdleTimer()
    {
        _splashIdleCts?.Cancel();
        var cts = new CancellationTokenSource();
        _splashIdleCts = cts;
        _ = SplashIdleAsync(cts.Token);
    }

    private async Task SplashIdleAsync(CancellationToken token)
    {
        try { await Task.Delay(DemoIdleTimeout, token); }
        catch (TaskCanceledException) { return; }

        // Only the splash starts itself: if a key already moved on, this timer is stale.
        if (!token.IsCancellationRequested && ScreenKind == AppScreen.Splash) StartDemo();
    }

    /// <summary>Restart the idle clock while still on the splash; no-op once Logo has started.</summary>
    private void RestartSplashIdle()
    {
        if (ScreenKind == AppScreen.Splash) StartSplashIdleTimer();
    }

    /// <summary>Leave the splash and start the listener, picking up the session where it was left.</summary>
    public void EnterLogo()
    {
        DiscardOverlay();
        _splashIdleCts?.Cancel();

        ScreenKind = AppScreen.Listener;
        _line.Clear();
        _caret = 0;
        _block = null;
        _historyIndex = -1;

        // EASY opens on the turtle page straight away: the splash already taught the child what to
        // type, and watching FD 100 happen is the whole point of the program.
        if (Engine.Mode == LogoMode.Easy) Engine.ShowLower(4);
        else Engine.ShowFullText();

        LogoSession? stored = FindSession(CurrentName);

        if (stored is not null && (stored.Procedures.Count > 0 || stored.Globals.Count > 0 || stored.Scene.Count > 0))
        {
            // Coming back to a session must not lose it: the workspace, the transcript and the
            // picture are put back exactly as they were saved.
            LoadSession(CurrentName);
            return;
        }

        Engine.ResetWorkspace();
        Engine.Screen.Clear();
        SplashArt.DrawGreeting(Screen, CurrentName, Engine.Mode);

        // PutLine writes without moving the cursor, so park it below the greeting or the prompt
        // would land on top of it.
        Screen.CursorRow = Math.Min(Screen.WindowBottom, Screen.WindowTop + 2);
        Screen.CursorCol = 0;

        Prompt();
        RequestRender();
        _ = ReplayGraphicsAsync();
    }

    // ---- the listener -----------------------------------------------------------

    /// <summary>
    /// Offer a prompt for whatever is in the buffer. The "> " belongs to the draft block rather than
    /// being written into the transcript, so it moves and is erased together with the line it
    /// introduces - which is why this only flips the mode and lets the painter do the drawing.
    /// </summary>
    private void Prompt()
    {
        _showPrompt = true;
        _readBaseCol = 2;
        PaintInput();
    }

    /// <summary>
    /// The longest line that can be typed. Fixed rather than window-sized: the window shrinks when
    /// the turtle page opens, so a limit based on the current window would let a child type a line
    /// that could no longer be drawn after ALT-T.
    /// </summary>
    public const int MaxInputLength = 256;

    /// <summary>False while a running program is asking for a line, so its prompt is not erased.</summary>
    private bool _showPrompt = true;

    /// <summary>Column a waiting program left the cursor at, where its answer is typed.</summary>
    private int _readBaseCol;

    /// <summary>
    /// Column the draft starts at. Normally 2, just after the "> " prompt. When a program is waiting
    /// on READ there is no prompt to draw - the program's own question is on the screen - so the
    /// answer starts wherever that program left the cursor.
    /// </summary>
    private int LineBaseCol => _showPrompt ? 2 : _readBaseCol;

    private static int RowIndexOf(int index, int baseCol) => (baseCol + index) / TextScreen.Cols;

    private static int ColumnOf(int index, int baseCol) => (baseCol + index) % TextScreen.Cols;

    /// <summary>Rows the text covers, plus the row the caret sits on: a line that exactly fills a
    /// row puts the caret at the start of the next one, and that row must be inside the block.</summary>
    private static int BlockRows(int length, int caret, int baseCol) =>
        Math.Max((baseCol + length - 1) / TextScreen.Cols + 1, RowIndexOf(caret, baseCol) + 1);

    /// <summary>
    /// The geometry the draft has at a given anchor row, inside the current window. A block taller
    /// than the room below its anchor shows the rows around the caret, because writing past the last
    /// row is what used to throw and deafen the keyboard for the rest of the session.
    /// </summary>
    private PaintedBlock BlockFor(int anchor, int length, int caret)
    {
        int baseCol = LineBaseCol;
        int rows = BlockRows(length, caret, baseCol);
        int capacity = Math.Max(1, Screen.WindowBottom - anchor + 1);
        int shown = Math.Min(rows, capacity);
        int firstRow = rows > shown
            ? Math.Clamp(RowIndexOf(caret, baseCol) - shown + 1, 0, rows - shown)
            : 0;

        return new PaintedBlock(anchor, baseCol, length, firstRow, shown, _showPrompt);
    }

    /// <summary>
    /// Paint the draft into the grid at the anchor row, wrapping across rows exactly as the Apple II
    /// wrapped a long typed line, and scrolling the transcript up if it runs out of room.
    /// </summary>
    /// <remarks>
    /// Both the block being replaced *and* the block about to be drawn are erased first. Erasing
    /// only the new geometry is what left a draft on screen twice: the anchor moves whenever the
    /// machine prints a line, whenever ALT-T resizes the window, and whenever an overlay closes, and
    /// the copy at the old rows was never revisited. Those ghosts were not cosmetic - ENTER glued
    /// them onto the next command, and ALT-S saved them into the transcript for good.
    /// </remarks>
    private void PaintInput()
    {
        if (ScreenKind != AppScreen.Listener) return;

        int caret = Math.Min(_caret, _line.Length);
        int anchor = DraftAnchor;

        int rows = BlockRows(_line.Length, caret, LineBaseCol);
        while (anchor + rows - 1 > Screen.WindowBottom && anchor > Screen.WindowTop)
        {
            // The draft has outgrown the room left under its anchor. Scrolling moves the whole
            // window up a row, and the Scrolled hook brings the block being replaced along with it.
            Screen.ScrollUp();
            anchor--;
        }

        PaintedBlock next = BlockFor(anchor, _line.Length, caret);

        EraseBlock(_block);
        EraseBlock(next);

        if (_showPrompt) Screen.Put(next.Anchor, 0, "> ");
        WriteCells(next, _line.ToString());

        // Park the engine's cursor just after the draft. Anything Logo writes without asking the
        // painter first - a program run from the editor, or a file loaded with ALT-O - then lands
        // below the line being typed instead of across it, which is what used to make the next
        // repaint erase the output along with the block it had printed into.
        int lastRow = next.Anchor + Math.Min(RowIndexOf(next.Length, next.Base), next.VisibleRows - 1);
        Screen.CursorRow = Math.Clamp(lastRow, Screen.WindowTop, Screen.WindowBottom);
        Screen.CursorCol = Math.Clamp(ColumnOf(next.Length, next.Base), 0, TextScreen.Cols - 1);

        _block = next;
    }

    /// <summary>
    /// Blank every cell a block owns. For an answer to a program the columns before the cursor are
    /// left alone, because those hold the question the program asked.
    /// </summary>
    private void EraseBlock(PaintedBlock? block)
    {
        if (block is null) return;

        for (int r = block.Anchor; r <= block.End; r++)
        {
            if (r < 0 || r >= TextScreen.Rows) continue;
            for (int c = block.Prompted ? 0 : block.Base; c < TextScreen.Cols; c++)
            {
                Screen.Chars[r, c] = ' ';
                Screen.Attrs[r, c] = CellAttr.None;
            }
        }
    }

    private void WriteCells(PaintedBlock block, string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (!TryCellOf(block, i, out int row, out int col)) continue;
            Screen.Chars[row, col] = text[i];
        }
    }

    /// <summary>
    /// The grid cell for a position in a block, false when that part of the line is scrolled out of
    /// view. Every read and write the painter makes goes through here, which is what keeps a line
    /// taller than the window from reaching past the last row of the grid.
    /// </summary>
    private static bool TryCellOf(PaintedBlock block, int index, out int row, out int col)
    {
        int rowIdx = RowIndexOf(index, block.Base);
        col = ColumnOf(index, block.Base);
        row = block.Anchor + rowIdx - block.FirstRow;

        return rowIdx >= block.FirstRow
            && rowIdx < block.FirstRow + block.VisibleRows
            && row >= 0 && row < TextScreen.Rows
            && col < TextScreen.Cols;
    }

    /// <summary>
    /// Give the rows the draft owned back to the transcript, closing up the gap so the child's work
    /// moves down to fill it. Called before anything prints and before the draft is repainted
    /// somewhere else, so a message and the line being typed can never end up on the same row.
    /// </summary>
    private void ReleaseBlock()
    {
        if (_block is null) return;

        PaintedBlock old = _block;
        _block = null;

        // While an overlay owns the grid those rows are someone else's content, and deleting them
        // would cut a hole in the quick reference or the file list. The stash keeps the block for
        // the way back, so dropping it here is enough.
        if (OverlayOwnsScreen) return;

        if (!_showPrompt)
        {
            // Answering a program: the echoed text stays where it is, because it shares its row with
            // the question the program asked. Printing simply continues after it.
            Screen.CursorRow = Math.Min(old.Anchor + RowIndexOf(old.Length, old.Base), Screen.WindowBottom);
            Screen.CursorCol = ColumnOf(old.Length, old.Base);
            return;
        }

        Screen.DeleteRows(old.Anchor, old.VisibleRows);
        Screen.CursorRow = Math.Min(old.Anchor, Screen.WindowBottom);
        Screen.CursorCol = 0;
    }

    /// <summary>
    /// Turn the draft into a real line of the transcript, so it stays on screen once the block that
    /// held it is gone. This is what ENTER does before running the command.
    /// </summary>
    private void CommitDraftToTranscript(string text)
    {
        PaintedBlock? old = _block;
        _block = null;

        if (old is not null && !_showPrompt)
        {
            // The answer is already painted in the right place; just drop below it.
            Screen.CursorRow = Math.Min(old.Anchor + RowIndexOf(old.Length, old.Base), Screen.WindowBottom);
            Screen.CursorCol = ColumnOf(old.Length, old.Base);
            Screen.NewLine();
            return;
        }

        // The draft's rows go away and the command is rewritten from column 0 of the row it started
        // on. Writing at the cursor as it stands would drop the line into the middle of whatever
        // else is on that row - the painter parks the cursor just past the draft so Logo's output
        // cannot land on it, and that is exactly the column that used to splice the command into a
        // message: "TEXT - THE > PRINT 101N IS TEXT".
        int anchor = old?.Anchor ?? DraftAnchor;
        Screen.DeleteRows(anchor, old?.VisibleRows ?? 1);
        Screen.CursorRow = Math.Min(anchor, Screen.WindowBottom);
        Screen.CursorCol = 0;

        Screen.Write(text.Length > 0 ? "> " + text : ">");
        Screen.NewLine();
    }

    /// <summary>
    /// The row the draft is anchored to. While a block is on screen that is where it starts; when
    /// there is none, the draft belongs at the row the machine's output left off on.
    /// </summary>
    private int DraftAnchor => _block?.Anchor
        ?? Math.Clamp(Screen.CursorRow, Screen.WindowTop, Screen.WindowBottom);

    /// <summary>
    /// Where the caret sits, using the same arithmetic as PaintInput. Used to draw the blinking
    /// cursor, so it must always name a cell that exists.
    /// </summary>
    public (int Row, int Col) CaretPosition()
    {
        if (ScreenKind == AppScreen.Editor) return EditorCaret;
        if (ScreenKind != AppScreen.Listener) return (Screen.CursorRow, Screen.CursorCol);

        PaintedBlock block = _block ?? BlockFor(DraftAnchor, _line.Length, Math.Min(_caret, _line.Length));
        int last = block.Base + Math.Min(_caret, block.Length);
        int rowIdx = Math.Clamp(last / TextScreen.Cols, block.FirstRow, block.FirstRow + block.VisibleRows - 1);
        return (Math.Min(block.Anchor + rowIdx - block.FirstRow, Screen.WindowBottom),
                Math.Min(last % TextScreen.Cols, TextScreen.Cols - 1));
    }

    /// <summary>Feed one key token into the machine.</summary>
    public void HandleKey(string token)
    {
        // Browsers report an unshifted letter as typed, so the keyboard delivers "n" where every
        // handler below matches "N". Unit tests press the uppercase token directly, which is why
        // only a real browser caught that ALT-L -> N made no new session and D deleted nothing.
        if (token.Length == 1) token = token.ToUpperInvariant();

        switch (ScreenKind)
        {
            case AppScreen.Splash:
                // A few keys still work here, so a child who prefers amber or ADVANCED mode can
                // choose it before starting. Everything else starts Logo: the splash is the one
                // screen whose whole job is "press a key", so a shortcut it does not advertise had
                // better not exist. Any press also restarts the idle clock that starts the demo,
                // because a hand on the keyboard means the attract loop should wait.
                RestartSplashIdle();
                if (!SplashKey(token)) EnterLogo();
                break;
            case AppScreen.Listener: HandleListenerKey(token); break;
            case AppScreen.Help: HandleHelpKey(token); break;
            case AppScreen.Sessions: HandleSessionKey(token); break;
            case AppScreen.Editor: HandleEditorKey(token); break;
            case AppScreen.Info: HandleInfoKey(token); break;
            case AppScreen.Settings: HandleSettingsKey(token); break;
            case AppScreen.About: HandleAboutKey(token); break;
            case AppScreen.WhatsNew: HandleWhatsNewKey(token); break;
            case AppScreen.Colors:
            case AppScreen.Shapes: HandleViewerKey(token); break;
            case AppScreen.Examples: HandleExamplesKey(token); break;
            case AppScreen.Demo: HandleDemoKey(token); break;
        }
    }

    private void HandleListenerKey(string token)
    {
        // A program parked on READ owns the keyboard: whatever is typed answers the program, not
        // the listener. Checked before the busy test because a program waiting on READ is running.
        if (AwaitingRead)
        {
            if (token == "ENTER") CompleteRead(_line.ToString());
            else if (token == "ESC") CompleteRead(null);
            else EditLine(token);
            RequestRender();
            return;
        }

        // While a program runs, the only thing the child can do is break out of it.
        if (IsBusy)
        {
            if (token is "ESC" or "A-B") RequestBreak();
            return;
        }

        if (TryGlobalKey(token)) return;

        if (token == "ENTER") { SubmitLine(); return; }

        EditLine(token);
        RequestRender();
    }

    /// <summary>
    /// The Alt keys that are safe from the splash: they change a setting rather than opening a view
    /// over the child's work. `Alt-,` opens settings, and WHAT'S NEW opens when there is a release
    /// the child has not read.
    /// </summary>
    /// <remarks>
    /// W is only honoured while the splash is actually advertising it, so the screen keeps its
    /// promise: every key it shows does what it says, and every key it does not show starts Logo.
    /// About lives under `Alt-,` rather than on a bare letter, because a child pressing A at "PRESS
    /// ANY KEY" means to start.
    /// </remarks>
    private bool SplashKey(string token)
    {
        switch (token)
        {
            case "A-M": ToggleMode(); return true;
            case "A-G": ToggleTheme(); return true;
            case "A-C": ToggleCrt(); return true;
            case "A-COMMA": ShowSettings(); return true;
            case "W" when LogoWhatsNew.IsUnread(Store.WhatsNewVersion): ShowWhatsNew(); return true;
            default: return false;
        }
    }

    private bool TryGlobalKey(string token)
    {
        switch (token)
        {
            case "A-?": case "A-Q": case "A-SLASH": case "C-SLASH": ShowHelp(); return true;
            case "A-M": ToggleMode(); return true;
            case "A-G": ToggleTheme(); return true;
            case "A-C": ToggleCrt(); return true;
            case "A-I": ShowInfo(); return true;
            case "A-COMMA": ShowSettings(); return true;
            case "A-V": ShowColors(); return true;
            case "A-W": ShowShapes(); return true;
            case "A-L": ShowSessions(); return true;
            case "A-X": ShowExamples(); return true;
            case "A-S": _ = SaveThenSay("SAVED " + TextSessionName); return true;
            // Ctrl+S is the same save. Browsers open their own page-save dialog for it, so the key
            // has to be swallowed and given a meaning rather than left dead.
            case "C-S": _ = SaveThenSay("SAVED " + TextSessionName); return true;
            case "A-D": _ = DownloadAsync(); return true;
            case "A-O": _ = ImportAsync(); return true;
            case "A-E": OpenEditor(); return true;
            case "A-T": ToggleTextGraphics(); return true;
            case "A-B": RequestBreak(); return true;
            default: return false;
        }
    }

    private async Task SaveThenSay(string message)
    {
        await SaveAsync();

        // Never announce a save that did not happen. localStorage throws QuotaExceededError once a
        // session's picture gets big - a few long drawings fill the few megabytes an origin is
        // given - and the child who believes "SAVED" and closes the tab loses everything.
        Announce(StorageAvailable ? message : "COULD NOT SAVE - NO ROOM LEFT IN THE BROWSER");
    }

    /// <summary>
    /// Report something to the child, wherever they are looking. The listener has a transcript to
    /// print into; the editor does not, so it uses its status line. Without this, ALT-S and ALT-D
    /// inside the editor did their work and said nothing at all.
    /// </summary>
    private void Announce(string message)
    {
        if (ScreenKind == AppScreen.Editor)
        {
            _statusMessage = message;
            _ = FlushEditorStatusAsync();
            PaintEditor();
            return;
        }

        Say(message);
    }

    /// <summary>Clear the editor's status line once the child has had a moment to read it.</summary>
    private async Task FlushEditorStatusAsync()
    {
        await Task.Delay(2500);
        if (_statusMessage.Length == 0) return;

        _statusMessage = "";
        if (ScreenKind == AppScreen.Editor) RequestRender();
    }

    /// <summary>Insert one character or run one editing key over the line buffer.</summary>
    private void EditLine(string token)
    {
        switch (token)
        {
            case "BKSP":
                if (_caret > 0) { _caret--; _line.Remove(_caret, 1); }
                break;
            case "DEL":
                if (_caret < _line.Length) _line.Remove(_caret, 1);
                break;
            case "LEFT": if (_caret > 0) _caret--; break;
            case "RIGHT": if (_caret < _line.Length) _caret++; break;
            case "HOME": _caret = 0; break;
            case "END": _caret = _line.Length; break;
            case "UP": RecallHistory(backwards: true); break;
            case "DOWN": RecallHistory(backwards: false); break;
            case "TAB": Insert(" "); break;
            case "SPACE": Insert(" "); break;
            case "ESC": ClearLine(); break;
            default:
                if (IsPlainChar(token)) Insert(token.ToUpperInvariant());
                break;
        }

        PaintInput();
    }

    private static bool IsPlainChar(string token) =>
        token.Length == 1 && !token.StartsWith("A-", StringComparison.Ordinal)
                          && !token.StartsWith("C-", StringComparison.Ordinal);

    private void Insert(string text)
    {
        // Refuse characters that would not fit on the screen rather than accepting them and hiding
        // them: a child who cannot see what they typed cannot edit it.
        int room = MaxInputLength - _line.Length;
        if (room <= 0) return;
        if (text.Length > room) text = text.Substring(0, room);

        _line.Insert(_caret, text);
        _caret += text.Length;
    }

    private void ClearLine()
    {
        _line.Clear();
        _caret = 0;
        PaintInput();
    }

    /// <summary>
    /// Walk the command history. The index counts from the newest entry, so UP goes back through
    /// what was typed and DOWN comes forward again to a blank line.
    /// </summary>
    private void RecallHistory(bool backwards)
    {
        if (_history.Count == 0) return;

        if (backwards) _historyIndex = Math.Min(_historyIndex + 1, _history.Count - 1);
        else _historyIndex = Math.Max(_historyIndex - 1, -1);

        _line.Clear();
        if (_historyIndex >= 0)
            _line.Append(_history[_history.Count - 1 - _historyIndex]);
        _caret = _line.Length;
    }

    /// <summary>Commit the typed line to the transcript and run it.</summary>
    private void SubmitLine()
    {
        string text = _line.ToString().Trim();

        if (AwaitingRead) { CompleteRead(text); return; }

        // The draft becomes a line of the transcript, so the command and its output can never share
        // a row and the line stays on screen after the block that held it is given back.
        CommitDraftToTranscript(text);

        if (text.Length > 0)
        {
            _history.Add(text);
            _historyIndex = -1;
        }

        _line.Clear();
        _caret = 0;

        if (text.Length == 0) { Prompt(); RequestRender(); return; }

        _ = RunAsync(text);
    }

    /// <summary>Run Logo source and print whatever it produces or complains about.</summary>
    public async Task RunAsync(string source)
    {
        if (IsBusy) return;

        IsBusy = true;
        BreakRequested = false;
        Engine.ClearBye();
        Engine.ClearSettings();
        Engine.ClearColors();
        Engine.ClearShapes();

        // A program can be started from the editor and the session manager as well as from the
        // prompt, and those paths leave the listener's draft block sitting on rows the program is
        // about to print onto. Releasing them first is what stops the closing repaint from erasing
        // the output along with the block.
        ReleaseBlock();
        RequestRender();

        try
        {
            LogoValue result = await Engine.RunAsync(source);
            ReportToplevel(result);
        }
        catch (LogoException ex)
        {
            // ESC/A-B during a run is not an error - the child asked to stop. Printing "BREAK"
            // would confuse them, and in demo mode it leaks onto the stashed screen.
            if (ex.Code != LogoErrorCode.BreakRequested)
                PrintError(ex);
        }
        catch (Exception ex)
        {
            // A fault that is not a Logo error is a bug in Logo itself. It must be said out loud:
            // RunAsync is started without awaiting it, so an unobserved exception would leave the
            // child staring at a screen that simply stopped answering.
            WriteWrapped($"SOMETHING BROKE: {ex.GetType().Name}", CellAttr.Reverse);
            WriteWrapped(ex.Message, CellAttr.Dim);
            ErrorReported?.Invoke(ex.ToString());
        }
        finally
        {
            IsBusy = false;
            Engine.BreakRequested = false;
        }

        if (Engine.ByeRequested)
        {
            await SaveAsync();
            ShowSplash();
            return;
        }

        // SETTINGS asked for the screen. It opens after everything else has finished - saving and
        // repainting included - because an overlay stashes whatever is underneath it, and the
        // transcript has to be complete before it does.
        if (Engine.SettingsRequested)
        {
            Engine.ClearSettings();
            await SaveAsync();
            ShowSettings();
            return;
        }

        // Same for the colour chart: it opens once the line has finished, so a program that drew
        // something and then asked to see the palette gets both.
        if (Engine.ColorsRequested)
        {
            Engine.ClearColors();
            await SaveAsync();
            ShowColors();
            return;
        }

        // ...and the same for the shape chart. Checked after colours, so a line that asks for both
        // settles on one rather than opening a viewer and immediately replacing it.
        if (Engine.ShapesRequested)
        {
            Engine.ClearShapes();
            await SaveAsync();
            ShowShapes();
            return;
        }

        Prompt();
        PaintInput();
        await SaveAsync();
        await ReplayGraphicsAsync();
        RequestRender();
    }

    /// <summary>
    /// Logo shows the value of a reporter typed at the listener, which is why FD 10 prints nothing
    /// but FIRST "LOGO answers L.
    /// </summary>
    private void ReportToplevel(LogoValue value)
    {
        if (value.IsEmpty || value.Kind == LogoKind.None) return;
        Screen.WriteLine(Printer.Format(value, readable: true));
    }

    private void PrintError(LogoException ex)
    {
        var messageLines = WrapToScreen(ex.Message).ToList();

        // The hint is EASY mode's teaching half of the message; ADVANCED stays terse, as Apple
        // Logo was. In EASY mode the text window can be only four rows tall, so a hint that does
        // not fit would scroll the error itself off the top of the screen - the child would read
        // the tail of an explanation and never see what went wrong. The hint is trimmed to leave
        // the message on screen.
        var hintLines = Engine.Mode == LogoMode.Easy && !string.IsNullOrEmpty(ex.Hint)
            ? WrapToScreen(ex.Hint).ToList()
            : [];

        int room = Math.Max(1, Screen.WindowHeight - 1);
        int hintRoom = Math.Max(0, room - messageLines.Count);
        if (hintLines.Count > hintRoom) hintLines.RemoveRange(hintRoom, hintLines.Count - hintRoom);

        foreach (string line in messageLines) Screen.WriteLine(line, CellAttr.Reverse);
        foreach (string line in hintLines) Screen.WriteLine(line, CellAttr.Dim);
    }

    /// <summary>
    /// Print a one-line message at the listener. The draft is taken off the grid first and repainted
    /// afterwards, which is what keeps a message from ever landing on the line being typed - and
    /// keeps the line from being left behind in two places when the message makes it move.
    /// </summary>
    private void Say(string text)
    {
        if (ScreenKind != AppScreen.Listener) return;

        ReleaseBlock();
        WriteWrapped(text, CellAttr.Dim);
        PaintInput();
        RequestRender();
    }

    /// <summary>
    /// Print a message at the cursor, wrapping it to the screen width. Logo's friendly errors and
    /// hints are longer than 40 columns, and a message cut off mid-word is worse than no message.
    /// </summary>
    private void WriteWrapped(string text, CellAttr attr)
    {
        foreach (string line in WrapToScreen(text))
            Screen.WriteLine(line, attr);
    }

    private static IEnumerable<string> WrapToScreen(string text) => TextWrap.Wrap(text, TextScreen.Cols);

    public void RequestBreak()
    {
        BreakRequested = true;
        Engine.RequestBreak();

        // A program broken mid-drawing should not keep playing the notes it queued on its way out.
        // The tones are already scheduled on the browser's audio clock, so nothing else stops them.
        Engine.StopSounds();
    }

    private void CompleteRead(string? text)
    {
        TaskCompletionSource<string?>? tcs = _readTcs;
        _readTcs = null;

        // The answer stays on the row the program's question is on, and the cursor drops below it,
        // so whatever the program prints next starts after what the child typed rather than writing
        // over it.
        CommitDraftToTranscript(text ?? "");

        _line.Clear();
        _caret = 0;
        _showPrompt = true;
        _readBaseCol = 2;

        tcs?.TrySetResult(text);
    }

    // ---- graphics ---------------------------------------------------------------

    /// <summary>ALT-T: swap between the split screen and a whole screen of text.</summary>
    public void ToggleTextGraphics()
    {
        // The draft is taken off the grid before the window changes size. Its rows would otherwise
        // be stranded: the block was measured against the old window, and the new one has different
        // bottom rows, so repainting alone leaves the whole previous copy sitting on screen.
        ReleaseBlock();

        if (Engine.Layout == EngineLayout.FullText)
        {
            Engine.ShowLower(Engine.Mode == LogoMode.Easy ? 4 : 1);
            Say("SPLIT - GRAPHICS ABOVE, TEXT BELOW");
        }
        else
        {
            Engine.ShowFullText();
            Say("TEXT - THE WHOLE SCREEN IS TEXT");
        }

        PaintInput();
        RequestRender();
    }

    /// <summary>
    /// Send the picture to the canvas. Segments are stored in turtle space (y up, origin centred)
    /// and converted here, because a canvas works top-down like every other bitmap.
    /// </summary>
    /// <summary>
    /// Number of scene items already handed to the canvas. A running program only sends what is new,
    /// because re-sending a 10,000-line spiral every frame would get slower as it drew.
    /// </summary>
    private int _sentItems;

    /// <summary>Background colour last sent to the canvas, so a colour change triggers a replay.</summary>
    private int _sentBackground;

    /// <summary>Convert one scene item into the JSON shape the canvas expects.</summary>
    private static object? Encode(object item) => item switch
    {
        DrawSegment s => new
        {
            kind = "segment",
            x1 = GraphicsScene.ToSceneX(s.X1),
            y1 = GraphicsScene.ToSceneY(s.Y1),
            x2 = GraphicsScene.ToSceneX(s.X2),
            y2 = GraphicsScene.ToSceneY(s.Y2),
            color = s.Color,
            width = s.Width,
        },
        DrawLabel l => new
        {
            kind = "label",
            x1 = GraphicsScene.ToSceneX(l.X),
            y1 = GraphicsScene.ToSceneY(l.Y),
            text = l.Text,
            color = l.Color,
        },
        _ => null,
    };

    /// <summary>
    /// Send the whole picture to the canvas. Used on start-up and after a session load, when the
    /// canvas holds nothing that matches the engine.
    /// </summary>
    public async Task ReplayGraphicsAsync()
    {
        // While the colour chart owns the canvas it *is* the picture, and a layout change is part of
        // opening and closing it. Replaying here would wipe the chart mid-gesture.
        if (ReplayGraphicsFunc is null || _viewerOpen) return;

        // In full-text mode there is no picture to show: the text owns every row.
        int textRows = Engine.Layout == EngineLayout.FullText ? TextScreen.Rows : Engine.LowerLines;

        var payload = new List<object>(Engine.Scene.Items.Count);
        foreach (object item in Engine.Scene.Items)
        {
            object? encoded = Encode(item);
            if (encoded is not null) payload.Add(encoded);
        }

        _sentItems = Engine.Scene.Items.Count;
        _sentBackground = Engine.Scene.Background;
        string json = JsonSerializer.Serialize(payload);

        // The turtle is live state rather than part of the replayed picture, so it travels with the
        // replay and again after each append; otherwise it lags behind the line it is drawing.
        await SetTurtleAsync();
        await ReplayGraphicsFunc.Invoke(json, Engine.Scene.Background, textRows);
    }

    /// <summary>Push the turtle's position, heading, colour and visibility to the canvas.</summary>
    public async Task SetTurtleAsync()
    {
        if (SetTurtleFunc is null || _viewerOpen) return;

        var turtle = new
        {
            x = GraphicsScene.ToSceneX(Engine.Turtle.X),
            y = GraphicsScene.ToSceneY(Engine.Turtle.Y),
            heading = Engine.Turtle.Heading,
            color = Engine.Turtle.PenColor,
            visible = Engine.Turtle.Visible,
            // Shape and size travel with the turtle: SHAPE and PENSIZE used to change only the
            // engine's copy, which is why the command ran and nothing on screen moved.
            shape = Engine.Turtle.Shape,
            size = Engine.Turtle.PenSize,
            // How long the browser should take to glide there, so the tween and SETSPEED agree
            // instead of the canvas guessing. 0 means jump, which is what speed 100 and reduced
            // motion both mean.
            ms = Engine.InstantDraw ? 0 : FrameDelayForTween(Engine.Speed),
        };

        await SetTurtleFunc.Invoke(JsonSerializer.Serialize(turtle));
    }

    /// <summary>
    /// Milliseconds the canvas spends moving between two turtle states. A frame's pause is already
    /// the engine's delay, so the tween is matched to it: at speed 25 the turtle crosses in about a
    /// quarter of a second, at 100 it does not tween at all.
    /// </summary>
    private static int FrameDelayForTween(int speed) => Math.Clamp(Engine.FrameDelayMs(speed) / 2, 0, 240);

    /// <summary>
    /// Send only the strokes added since the last call. If items were removed (a CLS or CLEAN) or
    /// the background changed, this falls back to a full replay, because appending would desynchronise.
    /// </summary>
    public async Task AppendGraphicsAsync()
    {
        // The chart owns the canvas; strokes from a running program are held until it closes.
        if (_viewerOpen) return;

        int total = Engine.Scene.Items.Count;

        // A program that changes the screen colour (SETSCRNCOLOR, or CLEAN, which redraws over the
        // background) adds few or no items, so appending alone would leave the old colour on screen.
        // Anything that is not a new stroke has to go through the full replay.
        if (AppendGraphicsFunc is null || total < _sentItems || Engine.Scene.Background != _sentBackground)
        {
            await ReplayGraphicsAsync();
            return;
        }

        if (total == _sentItems) return;

        var fresh = new List<object>(total - _sentItems);
        for (int i = _sentItems; i < total; i++)
        {
            object? encoded = Encode(Engine.Scene.Items[i]);
            if (encoded is not null) fresh.Add(encoded);
        }

        _sentItems = total;
        if (fresh.Count > 0)
        {
            await AppendGraphicsFunc.Invoke(JsonSerializer.Serialize(fresh));
            // The turtle moved while these strokes were drawn, so it has to move on screen too.
            await SetTurtleAsync();
        }
    }

    // ---- help -------------------------------------------------------------------

    public void ShowHelp()
    {
        // Wrapped once, here, so the pager's row count is the number of rows the child actually
        // sees rather than the number of lines the reference happened to be written with.
        _helpLines = WrapRows(QuickReference.ReferenceLines(Engine.Primitives));
        _helpTop = 0;
        OpenOverlay(AppScreen.Help);
        PaintHelp();
        RequestRender();
    }

    private void PaintHelp() => PaintPaged(_helpLines, ref _helpTop);

    private void HandleHelpKey(string token)
    {
        if (ScrollPaged(token, _helpLines, ref _helpTop))
        {
            PaintHelp();
            RequestRender();
            return;
        }

        if (token is "ESC" or "A-?" or "A-Q" or "A-SLASH" or "C-SLASH") CloseOverlay();
    }

    // ---- info dashboard ---------------------------------------------------------

    public void ShowInfo()
    {
        OpenOverlay(AppScreen.Info);
        PaintInfo();
        RequestRender();
    }

    private void PaintInfo()
    {
        Screen.Clear();
        Screen.DrawBox(0, 0, TextScreen.Rows, TextScreen.Cols, "INFO", CellAttr.None);

        int row = 3;
        void Line(string label, string value)
        {
            // The label owns its own column and the value wraps into whatever is left inside the
            // frame - so a long turtle position or a wide session name continues on the next row
            // rather than painting over the box's right border.
            Screen.Put(row, 3, label + ":");
            int valueCol = Math.Min(3 + label.Length + 2, TextScreen.Cols - 12);
            int room = TextScreen.Cols - 1 - valueCol;
            var parts = TextWrap.Wrap(value, room);
            Screen.Put(row, valueCol, parts[0]);
            for (int i = 1; i < parts.Count && row + i < TextScreen.Rows - 9; i++)
                Screen.Put(row + i, valueCol, parts[i]);
            row += Math.Max(1, parts.Count);
        }

        Line("FILE", CurrentName);
        Line("MODE", Engine.Mode.ToString());
        Line("VERSION", LogoInfo.VersionText);
        Line("LINES", Engine.LinesWritten.ToString());
        Line("COMMANDS", Engine.Primitives.All.Count.ToString());
        Line("PROCEDURES", Engine.Workspace.Procedures.Count.ToString());
        Line("VARIABLES", Engine.Workspace.Globals.Count.ToString());
        Line("STROKES", Engine.Scene.Items.Count.ToString());
        Line("SPEED", Engine.InstantDraw ? "INSTANT" : Engine.Speed.ToString());
        Line("TURTLE", $"X {Engine.Turtle.X:F0}  Y {Engine.Turtle.Y:F0}  H {Engine.Turtle.Heading:F0}");
        Line("SAVE SPACE", StorageAvailable ? "OK" : "NOT AVAILABLE");

        // The key list comes from the same table the quick reference prints, so the two can never
        // disagree about which keys exist - and neither can exceed the screen.
        int keyRow = TextScreen.Rows - 2 - QuickReference.KeyLines().Count();
        foreach (string line in QuickReference.KeyLines())
        {
            if (keyRow >= TextScreen.Rows - 2) break;
            Screen.Put(keyRow, 2, Fit(line), CellAttr.Dim);
            keyRow++;
        }
    }

    private void HandleInfoKey(string token)
    {
        if (token is "ESC" or "A-I" or "ENTER") CloseOverlay();
    }

    // ---- settings ---------------------------------------------------------------

    /// <summary>The rows the settings screen offers, in the order it shows them.</summary>
    private enum Setting
    {
        Monitor,
        Crt,
        Mode,
        Speed,
        Sound,
        Motion,
        Help,
        Colors,
        Shapes,
        Examples,
        Demo,
        WhatsNew,
        About,
    }

    /// <summary>Which settings row is highlighted, as an index into <see cref="SettingRows"/>.</summary>
    private int _settingsIndex;

    /// <summary>
    /// The rows in the order the screen shows them. The separator is not a setting: it is drawn as
    /// a blank band so the toggles and the pages read as two groups, and the arrows jump over it.
    /// Keeping the layout in one list is what stops the highlight from landing on the gap.
    /// </summary>
    private static readonly (Setting? Value, string Label)[] SettingRows =
    [
        (Setting.Monitor, "MONITOR"),
        (Setting.Crt, "CRT"),
        (Setting.Mode, "MODE"),
        (Setting.Speed, "SPEED"),
        (Setting.Sound, "SOUND"),
        (Setting.Motion, "REDUCED MOTION"),
        (null, ""),
        (Setting.Help, "HELP"),
        (Setting.Colors, "COLOR CHART"),
        (Setting.Shapes, "SHAPE CHART"),
        (Setting.Examples, "EXAMPLES"),
        (Setting.Demo, "DEMO MODE"),
        (Setting.WhatsNew, "WHAT'S NEW"),
        (Setting.About, "ABOUT"),
    ];

    public void ShowSettings()
    {
        _settingsIndex = 0;
        OpenOverlay(AppScreen.Settings);
        PaintSettings();
        RequestRender();
    }

    private void PaintSettings()
    {
        Screen.Clear();

        // A frame around the list rather than the whole screen. A full-height box left six empty
        // rows under the list, which reads as a broken screen on a grid that cannot scroll.
        int boxTop = 1;
        int boxHeight = SettingRows.Length + 4;
        Screen.DrawBox(boxTop, 0, boxHeight, TextScreen.Cols, "SETTINGS", CellAttr.None);

        string Value(Setting setting) => setting switch
        {
            Setting.Monitor => Theme.ToUpperInvariant(),
            Setting.Crt => CrtEmulation ? "ON" : "OFF",
            Setting.Mode => Engine.Mode.ToString().ToUpperInvariant(),
            Setting.Speed => Engine.InstantDraw ? "INSTANT" : $"{Engine.Speed} {SpeedBar(Engine.Speed)}",
            Setting.Sound => Engine.SoundEnabled ? "ON" : "OFF",
            Setting.Motion => Engine.InstantDraw ? "ON" : "OFF",
            Setting.Help => "OPEN",
            Setting.Colors => "OPEN",
            Setting.Shapes => "OPEN",
            Setting.Examples => "OPEN",
            Setting.Demo => "START",
            Setting.WhatsNew => LogoWhatsNew.IsUnread(Store.WhatsNewVersion) ? "READ IT" : $"SEEN {LogoInfo.Version}",
            _ => LogoInfo.Version,
        };

        for (int i = 0; i < SettingRows.Length; i++)
        {
            int row = boxTop + 2 + i;
            var (setting, label) = SettingRows[i];
            if (setting is null) continue;      // the separator band is left as the cleared grid

            bool selected = i == _settingsIndex;
            // 38 columns written at column 1: the box's borders sit on columns 0 and 39, and a
            // full-width PutLine would erase them. The highlight still spans the whole row inside
            // the frame, which is what makes this screen's cursor obvious on a grid with no mouse.
            string line = (selected ? ">" : " ") + " " + label;
            string value = Value(setting.Value);
            if (line.Length + value.Length + 2 > TextScreen.Cols - 2)
                value = value.Substring(0, Math.Max(0, TextScreen.Cols - 2 - line.Length - 2));

            Screen.Put(row, 1, line.PadRight(TextScreen.Cols - 2 - value.Length) + value,
                       selected ? CellAttr.Reverse : CellAttr.None);
        }

        int footer = boxTop + boxHeight + 1;
        Screen.Put(footer, 2, " UP DOWN CHOOSE", CellAttr.Dim);
        Screen.Put(footer + 1, 2, " LEFT RIGHT CHANGE", CellAttr.Dim);
        Screen.Put(footer + 2, 2, " ENTER OPEN OR TOGGLE", CellAttr.Dim);
        Screen.Put(footer + 3, 2, " ESC BACK", CellAttr.Dim);
    }

    /// <summary>
    /// The speed as a bar between two arrows, 25 at the left and 100 at the right, so the child can
    /// see how far along it is rather than reading a bare number.
    /// </summary>
    /// <remarks>
    /// Plain ASCII on purpose. The bitmap font is the only font the machine has, and a block or
    /// geometric character it does not carry would show as an empty box - which on a terminal is
    /// indistinguishable from a bug. The shape is the one the notes asked for: an arrow, a filled
    /// track, an empty track, and the closing arrow.
    /// </remarks>
    public static string SpeedBar(int speed)
    {
        const int track = 16;
        int filled = (int)Math.Round((speed - Engine.MinSpeed) * (double)track / (Engine.MaxSpeed - Engine.MinSpeed));
        filled = Math.Clamp(filled, 0, track);
        return "<" + new string('=', filled) + new string('-', track - filled) + ">";
    }

    private void HandleSettingsKey(string token)
    {
        switch (token)
        {
            case "UP": Move(-1); return;
            case "DOWN": Move(+1); return;
            case "HOME": Select(0); return;
            case "END": Select(SettingRows.Length - 1); return;

            case "ENTER":
                // ENTER on a toggle does the same thing as the arrows: a two-value setting has
                // nowhere sensible to go on Enter, and a child should not have to guess which key
                // the machine happened to implement.
                if (OpenOrToggle()) return;      // a page opened; repainting settings would cover it
                break;

            case "LEFT": case "RIGHT":
                Adjust(CurrentSetting(), token == "RIGHT" ? +1 : -1);
                break;

            case "ESC": case "A-COMMA": CloseOverlay(); return;
            default: return;
        }

        PaintSettings();
        RequestRender();
    }

    /// <summary>Highlighted row's setting; null only for the separator, which the arrows skip.</summary>
    private Setting? CurrentSetting() => SettingRows[_settingsIndex].Value;

    /// <summary>Move the highlight, skipping the separator so the cursor never lands on a gap.</summary>
    private void Move(int delta)
    {
        int index = _settingsIndex;
        for (int step = 0; step < SettingRows.Length; step++)
        {
            index = (index + delta + SettingRows.Length) % SettingRows.Length;
            if (SettingRows[index].Value is not null) break;
        }
        _settingsIndex = index;
        PaintSettings();
        RequestRender();
    }

    private void Select(int index)
    {
        if (SettingRows[index].Value is null) index += 1;
        _settingsIndex = Math.Clamp(index, 0, SettingRows.Length - 1);
        PaintSettings();
        RequestRender();
    }

    /// <summary>
    /// ENTER on the highlighted row. Returns true when it opened a page, in which case the caller
    /// must not repaint - the settings list would be drawn straight back over the page that just
    /// opened, which is the same bug that once hid the session manager behind the editor.
    /// </summary>
    private bool OpenOrToggle()
    {
        switch (CurrentSetting())
        {
            case Setting.Help: ShowHelp(); return true;
            case Setting.WhatsNew: ShowWhatsNew(); return true;
            case Setting.About: ShowAbout(); return true;
            case Setting.Colors: ShowColors(); return true;
            case Setting.Shapes: ShowShapes(); return true;
            // Examples and Demo open as overlays on top of whatever is showing - settings here, or
            // the listener when reached by ALT-X. That is what makes ESC put the child exactly where
            // they were: back in settings if that is where they came from, at the prompt otherwise.
            case Setting.Examples: ShowExamples(); return true;
            case Setting.Demo: StartDemo(); return true;
            case Setting.Motion: return false;      // the operating system owns this one
            case Setting.Speed: Adjust(Setting.Speed, +5); return false;
            default: ToggleCurrent(); return false;
        }
    }

    private void ToggleCurrent()
    {
        switch (CurrentSetting())
        {
            case Setting.Monitor: ToggleTheme(); break;
            case Setting.Crt: ToggleCrt(); break;
            case Setting.Mode: ToggleMode(); break;
            case Setting.Sound: ToggleSound(); break;
        }
    }

    /// <summary>Move one setting by <paramref name="delta"/> steps. Rows that only tell do nothing.</summary>
    private void Adjust(Setting? setting, int delta)
    {
        switch (setting)
        {
            case Setting.Monitor: ToggleTheme(); break;
            case Setting.Crt: ToggleCrt(); break;
            case Setting.Mode: ToggleMode(); break;
            case Setting.Sound: ToggleSound(); break;
            case Setting.Speed:
                // Stepping wraps between the ends so a child who holds the key down sweeps the
                // whole range instead of stopping at a wall.
                int next = Engine.Speed + delta * 5;
                if (next > Engine.MaxSpeed) next = Engine.MinSpeed;
                if (next < Engine.MinSpeed) next = Engine.MaxSpeed;
                SetSpeed(next);
                break;
        }
    }

    // ---- about ------------------------------------------------------------------

    public void ShowAbout()
    {
        OpenOverlay(AppScreen.About);
        SplashArt.DrawAbout(Screen, CurrentName, StorageAvailable);
        RequestRender();
    }

    private void HandleAboutKey(string token)
    {
        if (token is "ESC" or "ENTER" or "A") CloseOverlay();
    }

    // ---- the picture viewers: colour chart and shape chart ----------------------------

    /// <summary>
    /// True while a viewer - the colour chart or the shape chart - owns the canvas. Two things depend
    /// on it: the picture's own replay and append paths stand down so a running program cannot paint
    /// over the chart mid-loop, and closing a chart knows it has to put the picture back rather than
    /// leaving swatches or cards over the child's drawing.
    /// </summary>
    /// <remarks>
    /// One flag for both charts, because a canvas can only show one of them at a time and everything
    /// that depends on the state - the guards, the close, the discard - is identical. Which chart is
    /// up is already in <see cref="ScreenKind"/>.
    /// </remarks>
    private bool _viewerOpen;

    /// <summary>
    /// VIEWCOLORS: the palette as the canvas actually draws it, each swatch labelled with its number
    /// and name.
    /// </summary>
    /// <remarks>
    /// The chart goes on the canvas rather than the text grid because the grid is one phosphor
    /// colour and cannot show red at all - a colour chart drawn in green on green would be a joke.
    /// It reuses the replay path rather than adding an interop call: the swatches are sent to the
    /// same function that draws a spiral, so the browser layer learned one new item kind and nothing
    /// else.
    /// <para>
    /// It is a viewer, not a drawing. Putting the chart in the child's own scene would have saved it
    /// into the session and exported it into their .logo file, so a downloaded spiral would come
    /// back with sixteen boxes painted over it.
    /// </para>
    /// </remarks>
    public void ShowColors() =>
        ShowViewer(AppScreen.Colors, PaintColorsAsync,
            ColorChart.Headline, ColorChart.TextLines);

    /// <summary>
    /// VIEWSHAPES: the six turtle shapes, each drawn by the same painter that wears them, with the
    /// number a child types into SHAPE beside it.
    /// </summary>
    public void ShowShapes() =>
        ShowViewer(AppScreen.Shapes, PaintShapesAsync,
            ShapeChart.Headline, ShapeChart.TextLines);

    /// <summary>
    /// Open one of the picture viewers over the current screen and paint it.
    /// </summary>
    /// <remarks>
    /// Both charts need the same three things, which is why they share this: a canvas to draw on, the
    /// viewer flag that keeps a running program from painting through them, and zero text rows so the
    /// canvas covers the whole grid and every entry is legible at once. The engine's own layout is
    /// left alone - the canvas is an opaque rectangle over the grid, so on close the ordinary replay
    /// puts the child's picture and their text rows back exactly as they were.
    /// </remarks>
    private void ShowViewer(AppScreen kind, Func<Task> paint, string headline, Func<IEnumerable<string>> textFallback)
    {
        if (ReplayGraphicsFunc is null)
        {
            // No canvas to draw on, which in the shipped app cannot happen - a browser always has one.
            // This is the headless host, and a test, so the rule that matters is that neither chart may
            // ever open a blank screen: print the same list as text instead. The grid is one phosphor
            // colour, so a colour list names its entries rather than showing them, but the numbers are
            // the part a child actually types and those come over perfectly well.
            //
            // The list first and the headline last, because a sixteen-entry palette scrolls a four-row
            // window clean off the top - an instruction printed before it would be the one thing the
            // child cannot see. Announce rather than Say for the headline, because a chart can be asked
            // for from the editor, where Say would print into the child's source.
            foreach (string line in textFallback())
                Say(line);

            Announce(headline);
            return;
        }

        _viewerOpen = true;
        OpenOverlay(kind);
        _ = paint();
        RequestRender();
    }

    /// <summary>
    /// Swatches are scene items sent as JSON, but the chart is a viewer rather than a drawing: nothing
    /// here goes into Engine.Scene, so it is never saved into the session or exported.
    /// </summary>
    private async Task PaintColorsAsync()
    {
        if (ReplayGraphicsFunc is null) return;

        var payload = ColorChart.Swatches().Select(s => new
        {
            kind = "swatch",
            x1 = s.X,
            y1 = s.Y,
            width = s.Width,
            height = s.Height,
            color = s.Color,
            text = s.Text,
            textcolor = s.TextColor,
        });

        // Background 0 (black) whatever the child's picture is using: a chart of colours laid over
        // their own background would compare every swatch against it. No text rows, because the
        // chart owns every row while it is up.
        await ReplayGraphicsFunc.Invoke(JsonSerializer.Serialize(payload), 0, 0);
    }

    /// <summary>
    /// The shape cards, in the same vocabulary. Each carries the shape index and how long to draw it,
    /// and the browser paints it with the turtle's own painter - so this chart shows the shapes the
    /// turtle actually has rather than a second drawing of what someone thinks they look like.
    /// </summary>
    private async Task PaintShapesAsync()
    {
        if (ReplayGraphicsFunc is null) return;

        var payload = ShapeChart.Cards().Select(c => new
        {
            kind = "shape",
            x1 = c.X,
            y1 = c.Y,
            width = c.Width,
            height = c.Height,
            shape = c.Shape,
            shapelength = c.ShapeLength,
            color = c.Color,
            text = c.Text,
            textcolor = c.TextColor,
            textx = c.TextX,
            texty = c.TextY,
        });

        await ReplayGraphicsFunc.Invoke(JsonSerializer.Serialize(payload), 0, 0);
    }

    /// <summary>
    /// A viewer owns the keyboard while it is up, so the only keys it answers are the ones that leave
    /// it or switch to the other chart. Everything else - including Alt-L, Alt-E and Alt-T, which
    /// would stack another view underneath it or change the view it returns to - is ignored rather
    /// than half-applied.
    /// </summary>
    /// <remarks>
    /// The three "open a page" keys close the chart <i>and then</i> open their page, because a child
    /// who presses Alt-, while looking at swatches means settings and not "go back to the listener and
    /// press it again". Re-dispatching is safe: the chart is closed, so the key goes to the listener's
    /// own global handling rather than back in here.
    /// </remarks>
    private void HandleViewerKey(string token)
    {
        switch (token)
        {
            case "ESC": case "ENTER":
                CloseOverlay();
                return;

            // Alt-V and Alt-W switch between the two charts rather than closing the one you are on,
            // because a child flipping between "what colour is 9" and "what is shape 4" is doing one
            // task, not opening and closing screens. Pressing the key of the chart already shown
            // closes it, which is what a toggle has to do to be discoverable at all.
            case "A-V":
                OpenOtherViewer(AppScreen.Colors, PaintColorsAsync,
                    ColorChart.Headline, ColorChart.TextLines);
                return;

            case "A-W":
                OpenOtherViewer(AppScreen.Shapes, PaintShapesAsync,
                    ShapeChart.Headline, ShapeChart.TextLines);
                return;

            case "A-I": case "A-COMMA": case "A-?": case "A-Q": case "A-SLASH": case "C-SLASH":
                // Where the chart will hand the screen back to, read before it closes.
                AppScreen returningTo = _overlays.Count > 0 ? _overlays.Peek().ReturnTo : AppScreen.Listener;
                CloseOverlay();

                // Opened from the settings list, the child came *from* that page, so going back is
                // the whole answer - re-dispatching Alt-, here would close settings as well and drop
                // them at the listener for a key they pressed once. Only a chart opened at the
                // listener carries its key forward. The second test is the recursion guard: if the
                // close did not actually leave a viewer, re-dispatching would call this same handler
                // with the same token, forever.
                if (returningTo == AppScreen.Listener
                    && ScreenKind is not (AppScreen.Colors or AppScreen.Shapes)) HandleKey(token);
                return;
        }
    }

    private void OpenOtherViewer(AppScreen kind, Func<Task> paint, string headline, Func<IEnumerable<string>> textFallback)
    {
        if (ScreenKind == kind)
        {
            CloseOverlay();
            return;
        }

        // Close then open, so the stash underneath stays whatever the first chart was opened from -
        // a chart reached from settings switches in place and still returns to settings.
        CloseOverlay();
        ShowViewer(kind, paint, headline, textFallback);
    }

    // ---- what's new --------------------------------------------------------------

    private List<string> _whatsNewLines = new();
    private int _whatsNewTop;

    public void ShowWhatsNew()
    {
        // Wrapped here for the same reason the reference is: the pager counts rows, and a row that
        // overflowed would be counted once and drawn as two.
        _whatsNewLines = WrapRows(LogoWhatsNew.Lines());
        _whatsNewTop = 0;

        // Reading the page is what marks the release as seen, so the splash stops offering it.
        if (!string.Equals(Store.WhatsNewVersion, LogoInfo.Version, StringComparison.OrdinalIgnoreCase))
        {
            Store.WhatsNewVersion = LogoInfo.Version;
            _ = SaveSettingsAsync();
        }

        OpenOverlay(AppScreen.WhatsNew);
        PaintWhatsNew();
        RequestRender();
    }

    private void PaintWhatsNew()
    {
        PaintPaged(_whatsNewLines, ref _whatsNewTop);
    }

    private void HandleWhatsNewKey(string token)
    {
        if (ScrollPaged(token, _whatsNewLines, ref _whatsNewTop))
        {
            PaintWhatsNew();
            RequestRender();
            return;
        }

        if (token is "ESC" or "W") CloseOverlay();
    }

    // ---- examples ----------------------------------------------------------------

    /// <summary>Which example the highlight sits on, as an index into <see cref="LogoExamples.All"/>.</summary>
    private int _examplesIndex;

    public void ShowExamples()
    {
        _examplesIndex = 0;
        OpenOverlay(AppScreen.Examples);
        PaintExamples();
        RequestRender();
    }

    /// <summary>
    /// The numbered list of pre-loaded programs. Ten rows fit the grid with room for a title and an
    /// instruction line, so there is no paging - a child who wants example 9 presses 9, not arrows.
    /// </summary>
    private void PaintExamples()
    {
        Screen.Clear();
        Screen.DrawBox(0, 0, TextScreen.Rows, TextScreen.Cols, "EXAMPLES", CellAttr.None);

        int row = 3;
        for (int i = 0; i < LogoExamples.Count && row < TextScreen.Rows - 2; i++)
        {
            LogoExamples.Example ex = LogoExamples.All[i];
            string line = $"{ex.Index}  {ex.Title}";
            Screen.Put(row, 3, line, i == _examplesIndex ? CellAttr.Reverse : CellAttr.None);
            row++;
        }

        // The footer is the whole contract: a number picks and runs at once, ENTER runs whatever is
        // highlighted, ESC goes back. Saying it here means a child never has to guess which key does what.
        Screen.PutLine(TextScreen.Rows - 2, Fit("0-9 PICK AND RUN   ENTER RUN   ESC BACK"), CellAttr.Dim);
    }

    private void HandleExamplesKey(string token)
    {
        switch (token)
        {
            case "ESC": CloseOverlay(); return;
            case "UP": _examplesIndex = Math.Max(0, _examplesIndex - 1); PaintExamples(); RequestRender(); return;
            case "DOWN": _examplesIndex = Math.Min(LogoExamples.Count - 1, _examplesIndex + 1); PaintExamples(); RequestRender(); return;
            case "ENTER": RunExample(_examplesIndex); return;
        }

        // A digit both moves the highlight and runs that example at once - one key from list to picture.
        if (token.Length == 1 && char.IsDigit(token[0]))
        {
            int index = token[0] - '0';
            if (index < LogoExamples.Count) RunExample(index);
        }
    }

    /// <summary>Close the list and run the chosen example from the listener, so it behaves exactly as typed.</summary>
    private void RunExample(int index)
    {
        LogoExamples.Example? ex = LogoExamples.ByIndex(index);
        if (ex is null) return;

        CloseOverlay();

        // Put the code into the prompt and run it through SubmitLine, so the child sees exactly what
        // will run - the same line they would get by typing it themselves. Running RunAsync directly
        // used to skip the listener entirely: no code on screen, just a picture appearing out of nowhere.
        _line.Clear();
        _caret = 0;
        _line.Append(ex.Source);
        _caret = _line.Length;
        PaintInput();
        SubmitLine();
    }

    // ---- demo mode ----------------------------------------------------------------

    /// <summary>Cancels the running demo's typing and flash loops, and breaks any example mid-run.</summary>
    private CancellationTokenSource? _demoCts;

    /// <summary>True from StartDemo until StopDemo has closed the overlay - guards a double ESC.</summary>
    private bool _demoActive;

    /// <summary>True while an in-flight run is being awaited before the overlay closes. A second ESC
    /// during that window must be ignored, or it would close the overlay early and then again when
    /// the awaited run finishes.</summary>
    private bool _demoStopping;

    /// <summary>The RunAsync task for the example currently executing in the demo loop, so StopDemo
    /// can await it before restoring the screen underneath.</summary>
    private Task? _demoRunTask;

    /// <summary>The child's workspace as it was when the demo started, restored on exit so an example
    /// that is broken mid-run cannot leave its half-defined procedures in the saved session.</summary>
    private LogoSession? _demoBefore;

    /// <summary>How long the splash sits before it starts the demo on its own. Short in tests.</summary>
    public TimeSpan DemoIdleTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The pause between typed characters in the demo. Short in tests.</summary>
    public int DemoTypeDelayMs { get; set; } = 56;

    /// <summary>The pause after an example finishes, so a child can see what it drew before the next
    /// one wipes the screen. Short in tests.</summary>
    public int DemoPauseMs { get; set; } = 3000;

    /// <summary>
    /// Start the attract loop: each example is typed out character by character, run, and then the
    /// next one takes over, forever. The screen it covers is stashed like any overlay, so ESC puts
    /// the child exactly where they were - the splash, or settings if that is where they came from.
    /// </summary>
    public void StartDemo()
    {
        StopDemo();
        _demoStopping = false;
        _demoRunTask = null;

        OpenOverlay(AppScreen.Demo);
        Screen.Clear();
        _demoActive = true;
        // The demo runs real programs, and a finished run saves the session. Capturing the child's
        // workspace first - and restoring it after every example and on exit - is what keeps TRI:
        // and FLOWER: from leaking into their saved work when they press ESC.
        _demoBefore = LogoSession.Capture(Engine, CurrentName, "", _history);
        var cts = new CancellationTokenSource();
        _demoCts = cts;
        RequestRender();

        // Two loops share one token: the typing loop and the banner flash. One ESC cancels both,
        // and breaks an example that is mid-run, so nothing keeps drawing after the demo ends.
        _ = DemoLoopAsync(cts.Token);
        _ = FlashBannerAsync(cts.Token);
    }

    /// <summary>End the demo: stop the loops, break any running example, and restore the screen underneath.</summary>
    public void StopDemo()
    {
        // _demoStopping covers the window where an in-flight run is being awaited. The overlay is
        // already closed by then, so a second ESC here would just re-run the exit cleanup.
        if (!_demoActive || _demoStopping) return;

        _demoCts?.Cancel();
        _demoCts = null;

        // ESC can land while an example is running. The break makes RunAsync finish at its next
        // step so we don't have to wait out the whole program.
        if (IsBusy) RequestBreak();

        Task? runTask = _demoRunTask;
        _demoRunTask = null;

        // Close the overlay right away: ESC must put the child back where they were immediately,
        // even mid-run. The screen underneath is restored from its stash, so the run's closing
        // Prompt/PaintInput land on a normal listener (no-ops there) and SaveAsync writes to it -
        // harmless, because _demoActive stays true until the run ends, which keeps that save from
        // persisting demo procedures. The workspace restore below runs after the run finishes so
        // nothing rewrites half-defined procedures on top of it.
        Engine.TurtleView.Home();
        CloseOverlay();

        if (runTask is not null && !runTask.IsCompleted)
        {
            _demoStopping = true;
            _ = FinishDemoStopAsync(runTask);
        }
        else
        {
            FinishDemoStop();
        }
    }

    private async Task FinishDemoStopAsync(Task runTask)
    {
        try { await runTask; } catch { /* RunAsync handles its own errors */ }
        FinishDemoStop();
    }

    private void FinishDemoStop()
    {
        _demoActive = false;
        _demoStopping = false;

        // Put the child's workspace back: a broken example may have left half-defined procedures
        // behind, and RunAsync's closing save would otherwise write them into the session.
        _demoBefore?.Restore(Engine);
        _demoBefore = null;
    }

    private async Task DemoLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                // Let the finished picture sit for a beat before wiping it, so a child can see what
                // was drawn. Skipped on the very first iteration because there is nothing to show yet.
                if (_demoIndex > 0)
                    await Task.Delay(DemoPauseMs, token);

                LogoExamples.Example ex = LogoExamples.All[_demoIndex % LogoExamples.Count];
                _demoIndex++;

                // A fresh page per example: the turtle goes home, the picture clears, and the grid
                // resets, so each one starts from a blank screen exactly as it would if typed at the prompt.
                // The canvas follows on its own: after the restore below drops the engine's item count
                // to zero while _sentItems still holds the last example's strokes, this example's first
                // stroke sees total < _sentItems and triggers a full replay that wipes the old picture.
                Engine.TurtleView.Clear();
                Screen.Clear();
                RequestRender();

                await TypeExampleAsync(ex.Source, token);
                if (token.IsCancellationRequested) return;

                var runTask = RunAsync(ex.Source);
                _demoRunTask = runTask;
                await runTask;
                _demoRunTask = null;
                // Restore only the workspace (procedures/globals), not the picture: a full restore
                // would clear the scene back to its pre-demo state and erase what was just drawn, so
                // the pause would show nothing. The child needs to see the finished example.
                _demoBefore?.RestoreWorkspaceOnly(Engine);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Type the source one character at a time, wrapping across rows like a real terminal would.
    /// The demo is not the listener, so it draws straight to the grid - PaintInput would refuse
    /// to draw outside the listener screen.
    /// </summary>
    private async Task TypeExampleAsync(string source, CancellationToken token)
    {
        int row = Math.Max(1, Screen.WindowTop + 1);
        int col = 0;
        for (int i = 0; i < source.Length; i++)
        {
            if (token.IsCancellationRequested) return;

            // Wrap to the next row when we reach the right edge.
            if (col >= TextScreen.Cols)
            {
                col = 0;
                row++;
                if (row > Screen.WindowBottom) row = Math.Max(1, Screen.WindowTop + 1);
            }

            Screen.Put(row, col, source[i].ToString(), CellAttr.None);
            col++;
            RequestRender();
            await Task.Delay(DemoTypeDelayMs, token);
        }

        // A beat between "finished typing" and the picture appearing is what sells that Logo read it.
        await Task.Delay(Math.Max(200, DemoTypeDelayMs * 4), token);
    }

    /// <summary>Flash the banner on row 0 - over the drawing area, which is fine while it lasts.</summary>
    private async Task FlashBannerAsync(CancellationToken token)
    {
        bool shown = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                Screen.PutCentered(0, "DEMO MODE - ESC TO EXIT", shown ? CellAttr.None : CellAttr.Reverse);
                RequestRender();
                shown = !shown;
                await Task.Delay(500, token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private int _demoIndex;

    /// <summary>Only ESC does anything in the demo: everything else is part of the show.</summary>
    private void HandleDemoKey(string token)
    {
        if (token == "ESC") StopDemo();
    }

    // ---- paging ------------------------------------------------------------------

    /// <summary>
    /// Paint a paged overlay: as many rows as fit, then a reverse-video footer saying where the
    /// child is. Callers hand in rows already wrapped to the grid, which is what
    /// <see cref="WrapRows"/> is for - a line wider than the screen used to be cut off, and the
    /// cut-off help text and the clipped session rows were both that.
    /// </summary>
    private void PaintPaged(List<string> rows, ref int top)
    {
        Screen.Clear();

        int visible = TextScreen.Rows - 1;
        top = Math.Clamp(top, 0, Math.Max(0, rows.Count - visible));
        for (int row = 0; row < visible && top + row < rows.Count; row++)
            Screen.PutLine(row, Fit(rows[top + row]));

        int last = Math.Min(top + visible - 1, rows.Count - 1);
        Screen.PutLine(TextScreen.Rows - 1, Fit($"{top + 1}-{last + 1} OF {rows.Count}  ARROWS SCROLL"), CellAttr.Reverse);
    }

    /// <summary>Wrap every line to the screen width, so a paged overlay can never overflow it.</summary>
    private static List<string> WrapRows(IEnumerable<string> lines)
    {
        var rows = new List<string>();
        foreach (string line in lines) rows.AddRange(WrapToScreen(line));
        return rows;
    }

    /// <summary>One of the scroll keys on a paged overlay; false for anything else.</summary>
    private static bool ScrollPaged(string token, List<string> lines, ref int top)
    {
        int visible = TextScreen.Rows - 1;
        int lastTop = Math.Max(0, lines.Count - visible);
        switch (token)
        {
            case "UP": top = Math.Max(0, top - 1); return true;
            case "DOWN": top = Math.Min(lastTop, top + 1); return true;
            case "PGUP": top = Math.Max(0, top - (visible - 1)); return true;
            case "PGDN": top = Math.Min(lastTop, top + visible - 1); return true;
            case "HOME": top = 0; return true;
            case "END": top = lastTop; return true;
            default: return false;
        }
    }

    // ---- overlay plumbing -------------------------------------------------------

    /// <summary>
    /// Show an overlay, first stashing the screen it covers. An overlay reuses the same 40x24 grid,
    /// so the transcript has to come back exactly as it was.
    /// </summary>
    private void OpenOverlay(AppScreen kind)
    {
        if (ScreenKind == kind) return;

        var stashed = new StashedScreen(
            Chars: RowTexts(),
            Attrs: CloneAttrs(),
            Cursor: (Screen.CursorRow, Screen.CursorCol),
            ReturnTo: ScreenKind,
            Block: _block);

        _overlays.Push(stashed);
        ScreenKind = kind;
    }

    private string[] RowTexts()
    {
        var rows = new string[TextScreen.Rows];
        for (int r = 0; r < TextScreen.Rows; r++) rows[r] = Screen.GetRowText(r);
        return rows;
    }

    private CellAttr[][] CloneAttrs()
    {
        var all = new CellAttr[TextScreen.Rows][];
        for (int r = 0; r < TextScreen.Rows; r++)
        {
            var row = new CellAttr[TextScreen.Cols];
            for (int c = 0; c < TextScreen.Cols; c++) row[c] = Screen.Attrs[r, c];
            all[r] = row;
        }
        return all;
    }

    private void CloseOverlay()
    {
        if (_overlays.Count == 0) return;

        StashedScreen stashed = _overlays.Pop();

        // The chart hid the picture behind an opaque canvas, so the picture comes back before the
        // grid does: leaving it until after the repaint would show the child's drawing one frame
        // under the swatches.
        bool wasChart = stashed.ReturnTo is AppScreen.Colors or AppScreen.Shapes || _viewerOpen;
        if (wasChart) _viewerOpen = false;

        Restore(stashed);
        ScreenKind = stashed.ReturnTo;

        // The stashed screen already held the prompt and whatever had been typed, so the listener
        // is redrawn rather than prompted again. Writing a fresh prompt here would leave two "> "
        // lines - and the other views have no draft at all, so painting one would put "> " under
        // the splash turtle or over the settings list.
        if (stashed.ReturnTo == AppScreen.Listener) PaintInput();
        else if (stashed.ReturnTo == AppScreen.Editor) PaintEditor();
        else if (stashed.ReturnTo == AppScreen.Splash)
            // The splash is repainted rather than restored, because the page just closed may have
            // changed what it says: reading What's New marks the release as seen, and the offer
            // would otherwise stay bright on the splash forever.
            ShowSplash();
        else if (stashed.ReturnTo == AppScreen.Settings)
            // Same reason: opening What's New from here marks the release read, and the stashed
            // copy of the list still says READ IT. Settings is drawn from live state, so it is
            // repainted rather than trusted to a snapshot that is now a moment out of date.
            PaintSettings();

        RequestRender();

        if (wasChart) _ = ReplayGraphicsAsync();
    }

    private void Restore(StashedScreen stashed)
    {
        for (int r = 0; r < TextScreen.Rows; r++)
        {
            Screen.PutLine(r, stashed.Chars[r]);
            for (int c = 0; c < TextScreen.Cols; c++) Screen.Attrs[r, c] = stashed.Attrs[r][c];
        }

        (Screen.CursorRow, Screen.CursorCol) = stashed.Cursor;

        // The snapshot already contains the draft exactly as it was painted, so the block that
        // describes it comes back with it. Re-deriving the anchor from the engine's cursor instead
        // would make the painter erase a row it never drew on and eat a line of the transcript.
        _block = stashed.Block;
    }

    /// <summary>
    /// Empty the overlay stack. Used when the view changes underneath everything - leaving the
    /// splash, or BYE - because a stashed screen for a view that no longer exists cannot be
    /// returned to.
    /// </summary>
    /// <remarks>
    /// A picture viewer is dropped here too, and for the same reason: `_viewerOpen` stands the
    /// replay, append and turtle paths down, so a viewer discarded without clearing the flag leaves
    /// the canvas frozen over the child's picture forever - every drawing after that would be made
    /// and never seen. Unlike <see cref="CloseOverlay"/>, nothing is restored to be looked at, so
    /// the picture is repainted straight away.
    /// </remarks>
    private void DiscardOverlay()
    {
        _overlays.Clear();

        if (_viewerOpen)
        {
            _viewerOpen = false;
            _ = ReplayGraphicsAsync();
        }
    }

    private static string Fit(string text) =>
        text.Length <= TextScreen.Cols ? text : text.Substring(0, TextScreen.Cols);

    // ---- sessions ---------------------------------------------------------------

    private LogoSession? FindSession(string name) => Store.Sessions.FirstOrDefault(s => s.Name == name);

    public void ShowSessions()
    {
        OpenOverlay(AppScreen.Sessions);
        _sessionIndex = Math.Max(0, Store.Sessions.FindIndex(s => s.Name == CurrentName));
        PaintSessions();
        RequestRender();
    }

    /// <summary>Set when the cap was reached, so the manager can say so on its own screen.</summary>
    private bool _sessionsFull;

    private void PaintSessions()
    {
        Screen.Clear();
        Screen.PutCentered(0, "SESSIONS", CellAttr.Bold);
        Screen.PutCentered(1, $"{Store.Sessions.Count} OF {SessionNaming.MaxSessions}", CellAttr.Dim);

        // Twelve sessions exactly fill the rows between the header and the footer, so the full
        // warning replaces the help line rather than the list: a child must still be able to see
        // which file they are on in order to delete one.
        int listStart = 3;
        int listRows = TextScreen.Rows - listStart - 2;
        for (int i = 0; i < Store.Sessions.Count && i < listRows; i++)
        {
            LogoSession session = Store.Sessions[i];
            string marker = session.Name == CurrentName ? "*" : " ";
            // Name, then the line count and date right-aligned into the remaining columns. The old
            // format ran to 41 characters for a two-digit session number, which is how the last
            // digit of a date disappeared off the right-hand edge.
            string stats = $"{session.LinesWritten} LINES {session.UpdatedAt:MM-dd}";
            string line = marker + " " + session.Name;
            line = line.Length + stats.Length + 1 > TextScreen.Cols
                ? line.Substring(0, Math.Max(0, TextScreen.Cols - stats.Length - 1)) + " " + stats
                : line.PadRight(TextScreen.Cols - stats.Length) + stats;
            Screen.PutLine(listStart + i, line, i == _sessionIndex ? CellAttr.Reverse : CellAttr.None);
        }

        int footer = TextScreen.Rows - 2;
        if (_sessionsFull)
        {
            Screen.PutLine(footer, Fit($"NO ROOM FOR {SessionNaming.MaxSessions} MORE - DELETE ONE"), CellAttr.Reverse);
            Screen.PutLine(footer + 1, "D DELETE   ESC BACK");
        }
        else
        {
            Screen.PutLine(footer, "ENTER OPEN   N NEW   D DELETE");
            Screen.PutLine(footer + 1, "ESC BACK");
        }
    }

    private void HandleSessionKey(string token)
    {
        switch (token)
        {
            case "UP": _sessionIndex = Math.Max(0, _sessionIndex - 1); break;
            case "DOWN": _sessionIndex = Math.Min(Store.Sessions.Count - 1, _sessionIndex + 1); break;
            case "N":
                // Stay in the manager when the cap is reached, so the warning is on the screen the
                // child is looking at and they can delete something without starting over.
                if (NewSession()) CloseOverlay();
                else PaintSessions();
                return;
            case "D": DeleteSelected(); break;
            case "ENTER":
                if (_sessionIndex >= 0 && _sessionIndex < Store.Sessions.Count)
                {
                    LoadSession(Store.Sessions[_sessionIndex].Name);
                    DiscardOverlay();
                    return;
                }
                return;
            case "ESC": CloseOverlay(); return;
            default: return;
        }
        PaintSessions();
        RequestRender();
    }

    private void DeleteSelected()
    {
        if (Store.Sessions.Count == 0) return;

        _sessionIndex = Math.Clamp(_sessionIndex, 0, Store.Sessions.Count - 1);
        string name = Store.Sessions[_sessionIndex].Name;
        Store.Sessions.RemoveAt(_sessionIndex);

        // Room has opened up, so the "no room" warning is no longer true.
        _sessionsFull = false;

        if (CurrentName == name)
        {
            if (Store.Sessions.Count == 0) NewSession();
            else CurrentName = Store.Sessions[Math.Min(_sessionIndex, Store.Sessions.Count - 1)].Name;
        }

        _sessionIndex = Math.Min(_sessionIndex, Math.Max(0, Store.Sessions.Count - 1));
        _ = SaveAsync();
    }

    /// <summary>
    /// Make the next free SESSIONn.logo and switch to it. Deleted numbers are never reused.
    /// Returns false when the cap has been reached.
    /// </summary>
    public bool NewSession()
    {
        string? name = SessionNaming.NextName(
            Store.Sessions.Select(s => s.Name).ToList(),
            Store.LastIssuedSessionNumber);

        if (name is null)
        {
            _sessionsFull = true;
            Say($"NO ROOM FOR MORE THAN {SessionNaming.MaxSessions} SESSIONS - DELETE ONE WITH ALT-L");
            return false;
        }

        _sessionsFull = false;
        Store.Sessions.Add(SessionStore.Create(name));
        Store.LastIssuedSessionNumber = Math.Max(Store.LastIssuedSessionNumber, SessionNaming.NumberOf(name));
        CurrentName = name;
        Store.CurrentName = name;
        _ = SaveAsync();
        return true;
    }

    /// <summary>Load a session's saved workspace, transcript and picture back into the engine.</summary>
    public void LoadSession(string name)
    {
        LogoSession? session = FindSession(name);
        if (session is null) return;

        CurrentName = name;
        Store.CurrentName = name;

        Engine.ResetWorkspace();
        Engine.Screen.Clear();
        session.Restore(Engine);

        _history.Clear();
        foreach (string line in session.History) _history.Add(line);

        // The transcript was captured with the same window layout it is coming back to, so each row
        // goes home to the row it came from.
        for (int r = 0; r < Math.Min(TextScreen.Rows, session.ScreenLines.Count); r++)
            Screen.PutLine(r, session.ScreenLines[r]);

        // Resume at the first empty text row, or at the bottom of the window if it is full.
        int row = Screen.WindowTop;
        for (int r = Screen.WindowTop; r <= Screen.WindowBottom; r++)
            if (Screen.GetRowText(r).Trim().Length > 0) row = Math.Min(r + 1, Screen.WindowBottom);

        Screen.CursorRow = row;
        Screen.CursorCol = 0;

        ScreenKind = AppScreen.Listener;
        _line.Clear();
        _caret = 0;
        Prompt();
        PaintInput();
        _ = ReplayGraphicsAsync();
        _ = SaveAsync();
        RequestRender();
    }

    /// <summary>True when a help, session, editor or info page owns the 40x24 grid.</summary>
    private bool OverlayOwnsScreen => ScreenKind != AppScreen.Listener;

    /// <summary>Capture the running workspace and write the whole store back to the browser.</summary>
    public async Task SaveAsync()
    {
        // The demo runs real programs, and RunAsync saves on its way out. Without this guard every
        // example's procedures (TRI:, FLOWER:) would be written into the child's own session - they'd
        // open their work later and find a flower they never drew. The in-memory engine is restored
        // separately; this stops the saved file from ever seeing the demo's state.
        if (_demoActive) return;

        // In the editor this saves the file the editor was opened for, which after ALT-L -> N is not
        // the session on screen. The alternative is a program typed in one file landing in another.
        string name = TextSessionName;

        LogoSession? session = FindSession(name);
        if (session is null)
        {
            session = SessionStore.Create(name);
            Store.Sessions.Add(session);
        }

        // Capture returns a new object; copy its mutable parts onto the stored session so the
        // creation date and identity survive a save.
        LogoSession fresh = LogoSession.Capture(Engine, name, session.Source, _history);
        session.Mode = fresh.Mode;
        session.LinesWritten = fresh.LinesWritten;
        session.Turtle = fresh.Turtle;
        session.Globals = fresh.Globals;
        session.Properties = fresh.Properties;
        session.Procedures = fresh.Procedures;
        session.Scene = fresh.Scene;
        session.Background = fresh.Background;
        session.History = fresh.History;
        session.LowerLines = fresh.LowerLines;

        // The transcript is only taken from the grid while the listener owns it. Saving from the
        // sessions menu would otherwise store the menu itself as the child's work, and on reload
        // they would find their own typing replaced by a file list. Nothing Logo prints happens
        // while an overlay is up, so the transcript from the last listener save is still correct.
        if (!OverlayOwnsScreen) session.ScreenLines = fresh.ScreenLines;

        session.UpdatedAt = DateTime.UtcNow;

        Store.CurrentName = CurrentName;
        Store.Theme = Theme;
        Store.CrtEmulation = CrtEmulation;
        Store.Mode = Engine.Mode.ToString();
        Store.Speed = Engine.Speed;
        Store.SoundEnabled = Engine.SoundEnabled;

        await WriteStoreAsync();
    }

    private async Task WriteStoreAsync()
    {
        if (WriteStoreFunc is null) return;
        StorageAvailable = await WriteStoreFunc.Invoke(SessionStore.StorageKey, SessionStore.Serialize(Store));
    }

    /// <summary>ALT-D: hand the child a real .logo file they can keep or print.</summary>
    public async Task DownloadAsync()
    {
        if (DownloadFunc is null) return;
        await SaveAsync();
        LogoSession? session = FindSession(TextSessionName);
        if (session is null) return;

        await DownloadFunc.Invoke(session.Name, LogoSourceExporter.Export(session));
        Announce("DOWNLOADED " + session.Name);
    }

    /// <summary>
    /// The file the child's work lives in. From the listener that is the session on screen; from the
    /// editor it is the session the editor was opened for, which may no longer be the current one.
    /// </summary>
    private string TextSessionName =>
        ScreenKind == AppScreen.Editor && _editorSessionName.Length > 0 ? _editorSessionName : CurrentName;

    /// <summary>ALT-O: load a .logo file the child picked, then run it.</summary>
    public async Task ImportAsync()
    {
        if (PickFileFunc is null) return;
        string? text = await PickFileFunc.Invoke(".logo,.txt,text/plain");
        if (string.IsNullOrWhiteSpace(text)) return;

        LogoSession? session = FindSession(TextSessionName);
        if (session is not null)
        {
            session.Source = text;

            // Inside the editor the buffer is what the child sees, so a loaded program has to appear
            // in it. Leaving the old text on screen would invite an ESC right afterwards to overwrite
            // the file that was just loaded.
            if (ScreenKind == AppScreen.Editor)
            {
                _editorLines = text.Replace("\r\n", "\n").Split('\n').ToList();
                if (_editorLines.Count == 0) _editorLines.Add("");
                _editorRow = 0;
                _editorCol = 0;
                _editorTop = 0;
                KeepCaretVisible();
                PaintEditor();
            }

            await SaveAsync();
        }

        // Inside the editor the loaded program belongs in the buffer, where the child can read and
        // change it. Running it there would print over the text they are looking at, and the editor
        // has no transcript to show for it.
        if (ScreenKind == AppScreen.Editor)
        {
            Announce("LOADED PROGRAM - ALT-R RUNS IT");
            return;
        }

        await RunAsync(text);
        Announce("LOADED PROGRAM");
    }

    // ---- editor -----------------------------------------------------------------

    private List<string> _editorLines = new();
    private int _editorRow, _editorCol, _editorTop;

    /// <summary>
    /// The file the buffer belongs to. Captured when the editor opens, because the child can reach
    /// the session manager from inside the editor and make or load another session - and work typed
    /// here must still land in the file it was started in, not whatever is current afterwards.
    /// </summary>
    private string _editorSessionName = "";

    /// <summary>One line of feedback for the editor, which has no transcript to print into.</summary>
    private string _statusMessage = "";

    /// <summary>ADVANCED only: edit the session's whole source, then run it.</summary>
    public void OpenEditor()
    {
        if (Engine.Mode != LogoMode.Advanced)
        {
            Say("THE EDITOR IS ADVANCED MODE - PRESS ALT-M FIRST");
            return;
        }

        // ALT-E again while already editing is a no-op. Reopening would reload the buffer from the
        // last saved source and silently throw away everything typed since.
        if (ScreenKind == AppScreen.Editor) return;

        OpenOverlay(AppScreen.Editor);
        LogoSession? session = FindSession(CurrentName);
        _editorSessionName = CurrentName;
        _statusMessage = "";
        _editorLines = (session?.Source ?? "").Replace("\r\n", "\n").Split('\n').ToList();
        if (_editorLines.Count == 0) _editorLines.Add("");
        _editorRow = 0;
        _editorCol = 0;
        _editorTop = 0;
        PaintEditor();
        RequestRender();
    }

    private void PaintEditor()
    {
        Screen.Clear();
        int visibleRows = TextScreen.Rows - 1;
        for (int row = 0; row < visibleRows && _editorTop + row < _editorLines.Count; row++)
            Screen.PutLine(row, Fit(_editorLines[_editorTop + row]));

        // The last row doubles as the status line: the editor has no transcript, so an Alt key that
        // reports something would otherwise report it nowhere at all.
        string footer = _statusMessage.Length > 0 ? _statusMessage : "ESC SAVE   ALT-R RUN   ALT-L FILES";
        Screen.PutLine(TextScreen.Rows - 1, Fit(footer), CellAttr.Reverse);
    }

    private void HandleEditorKey(string token)
    {
        // Every action below reads the session - save, download, run, the file list. They have to
        // see what is in the buffer now, not what was saved when the editor was opened.
        ApplyEditor();

        if (TryGlobalKey(token))
        {
            // Only repaint if the key left us in the editor. ALT-L opens the file list, and
            // repainting here drew the program straight back over it - the child saw the editor
            // while the machine was busy handling session-manager keys.
            if (ScreenKind == AppScreen.Editor)
            {
                PaintEditor();
                RequestRender();
            }
            return;
        }

        string current = _editorLines[_editorRow];
        bool changed = true;

        switch (token)
        {
            case "ESC":
                CommitEditor();
                CloseOverlay();
                return;
            case "A-R":
                CommitEditor();
                CloseOverlay();
                _ = RunAsync(string.Join("\n", _editorLines));
                return;
            case "UP": _editorRow = Math.Max(0, _editorRow - 1); _editorCol = Math.Min(_editorCol, _editorLines[_editorRow].Length); break;
            case "DOWN": _editorRow = Math.Min(_editorLines.Count - 1, _editorRow + 1); _editorCol = Math.Min(_editorCol, _editorLines[_editorRow].Length); break;
            case "LEFT": _editorCol = Math.Max(0, _editorCol - 1); break;
            case "RIGHT": _editorCol = Math.Min(current.Length, _editorCol + 1); break;
            case "HOME": _editorCol = 0; break;
            case "END": _editorCol = current.Length; break;
            case "PGUP": _editorTop = Math.Max(0, _editorTop - (TextScreen.Rows - 2)); break;
            case "PGDN": _editorTop = Math.Min(Math.Max(0, _editorLines.Count - 1), _editorTop + TextScreen.Rows - 2); break;
            case "BKSP":
                if (_editorCol > 0) _editorLines[_editorRow] = current.Remove(_editorCol - 1, 1);
                else if (_editorRow > 0) JoinLines();
                _editorCol = Math.Min(_editorCol, _editorLines[_editorRow].Length);
                break;
            case "DEL":
                if (_editorCol < current.Length) _editorLines[_editorRow] = current.Remove(_editorCol, 1);
                break;
            case "ENTER":
                _editorLines[_editorRow] = current.Substring(0, _editorCol);
                _editorLines.Insert(_editorRow + 1, current.Substring(_editorCol));
                _editorRow++;
                _editorCol = 0;
                break;
            case "TAB": case "SPACE":
                string insert = token == "TAB" ? "  " : " ";
                _editorLines[_editorRow] = current.Insert(_editorCol, insert);
                _editorCol += insert.Length;
                break;
            default:
                if (IsPlainChar(token))
                {
                    // Insert, never overwrite. A child who presses LEFT to fix a word in the middle
                    // of a line expects the rest of the line to move right, not to be eaten.
                    _editorLines[_editorRow] = current.Insert(_editorCol, token.ToUpperInvariant());
                    _editorCol++;
                }
                else changed = false;
                break;
        }

        if (!changed) return;
        KeepCaretVisible();
        PaintEditor();
        RequestRender();
    }

    /// <summary>
    /// Copy the buffer into the session it was opened for. In-memory only, because this runs on
    /// every keystroke so that saving, downloading and the file list see the current text - a
    /// serialize of the whole store (picture included) per character would be far too slow.
    /// </summary>
    private void ApplyEditor()
    {
        if (ScreenKind != AppScreen.Editor) return;

        LogoSession? session = FindSession(_editorSessionName);
        if (session is null)
        {
            // The file was deleted from under the editor. Say so on the status line rather than
            // editing a buffer with nowhere to go.
            _statusMessage = "THAT FILE IS GONE - ESC LEAVES THE EDITOR";
            return;
        }

        session.Source = string.Join("\n", _editorLines);
    }

    /// <summary>
    /// Store what the child wrote. Saving here is the point of ESC: the source has to reach the
    /// browser before anything else can overwrite it.
    /// </summary>
    private void CommitEditor()
    {
        ApplyEditor();
        _ = SaveAsync();
    }

    private void JoinLines()
    {
        string previous = _editorLines[_editorRow - 1];
        _editorCol = previous.Length;
        _editorLines[_editorRow - 1] = previous + _editorLines[_editorRow];
        _editorLines.RemoveAt(_editorRow);
        _editorRow--;
    }

    private void KeepCaretVisible()
    {
        int visibleRows = TextScreen.Rows - 1;
        if (_editorRow < _editorTop) _editorTop = _editorRow;
        else if (_editorRow >= _editorTop + visibleRows) _editorTop = _editorRow - visibleRows + 1;
        _editorTop = Math.Max(0, _editorTop);
    }

    public (int Row, int Col) EditorCaret => (_editorRow - _editorTop, Math.Min(_editorCol, TextScreen.Cols - 1));

    // ---- the host ---------------------------------------------------------------

    /// <summary>
    /// How the engine talks back: yield so the browser can paint, notice the break key, and ask the
    /// child for a line when Logo says READ.
    /// </summary>
    private sealed class WasmHost(LogoMachine machine) : IEngineHost
    {
        public async Task YieldAsync()
        {
            // The pause that makes SETSPEED mean something. The engine yields once per slice of
            // evaluation, so this is the only place a program can be slowed down without changing
            // how fast it computes. Speed 100 and the system's reduced-motion setting both arrive
            // as no delay at all.
            int delay = machine.Engine.InstantDraw ? 0 : Engine.FrameDelayMs(machine.Engine.Speed);
            if (delay > 0 && machine.BreakRequested) delay = 0;

            if (machine.YieldFrameFunc is { } yieldFrame) await yieldFrame();

            // Repaint the picture as a program draws it, so REPEAT 4 [FD 100 RT 90] animates
            // instead of appearing all at once when the program finishes.
            await machine.AppendGraphicsAsync();

            if (delay > 0) await Task.Delay(delay);
        }

        /// <summary>
        /// Flush graphics to the browser and apply the SETSPEED delay so each frame is visible.
        /// Only called before the first yield boundary (short programs), where per-frame animation
        /// matters. Long programs rely on YieldAsync() at slice boundaries instead.
        /// </summary>
        public async Task FlushGraphicsAsync()
        {
            // Append new strokes. This also sends the turtle state when there are new items,
            // but SHAPE/PENSIZE/ST/HT change the turtle without adding items, so we always
            // send the turtle state here to be safe.
            await machine.AppendGraphicsAsync();
            await machine.SetTurtleAsync();

            // Pause so the child can see each frame before the next one draws.
            int delay = machine.Engine.InstantDraw ? 0 : Engine.FrameDelayMs(machine.Engine.Speed);
            if (delay > 0 && !machine.BreakRequested)
                await Task.Delay(delay);
        }

        public bool IsBreakRequested() => machine.BreakRequested;

        public Task<string?> ReadLineAsync()
        {
            machine._readTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

            // The answer is typed where the program left the cursor, which is not the prompt the
            // child pressed ENTER at - a program may have printed several lines since then. The
            // listener's own "> " is suppressed here: drawing it would erase the question the
            // program just asked, which is the only reason the child is being asked to type.
            //
            // The listener's half-typed line is dropped as well. Without this, a draft sitting at
            // the prompt answered the program's READ the moment ENTER was pressed, which is the
            // opposite of what the child sees and typed.
            machine._line.Clear();
            machine._caret = 0;
            machine._block = null;
            machine._readBaseCol = machine.Screen.CursorCol;
            machine._showPrompt = false;
            machine.RequestRender();
            return machine._readTcs.Task;
        }

        public void Invalidate() => machine.RequestRender();

        public Task PlayToneAsync(double hertz, double seconds) =>
            machine.PlayToneFunc is { } play ? play(hertz, seconds) : Task.CompletedTask;

        public void StopTones() => machine.StopTonesFunc?.Invoke();
    }
}
