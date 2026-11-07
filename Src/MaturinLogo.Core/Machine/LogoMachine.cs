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

    // ---- session store ---------------------------------------------------------

    public SessionStore.StoreFile Store { get; private set; } = new();

    public string CurrentName { get; private set; } = "";

    public IReadOnlyList<LogoSession> Sessions => Store.Sessions;

    // ---- listener line buffer ---------------------------------------------------

    private readonly StringBuilder _line = new();
    private int _caret;
    private int _historyIndex = -1;
    private readonly List<string> _history = new();

    /// <summary>Row the current prompt started on; the typed text is painted from there.</summary>
    private int _anchorRow;

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
        AppScreen ReturnTo);

    private readonly Stack<StashedScreen> _overlays = new();

    public LogoMachine()
    {
        Engine = new Engine { Host = new WasmHost(this) };
        Engine.Screen.Changed += () => RequestRender();
        Engine.LayoutChanged += () => _ = ReplayGraphicsAsync();
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

    private Task SaveSettingsAsync()
    {
        Store.Mode = Engine.Mode.ToString();
        Store.CurrentName = CurrentName;
        return WriteStoreAsync();
    }

    // ---- screens ----------------------------------------------------------------

    public void ShowSplash()
    {
        DiscardOverlay();
        ScreenKind = AppScreen.Splash;
        SplashArt.Draw(Screen, CurrentName, StorageAvailable);
        RequestRender();
    }

    /// <summary>Leave the splash and start the listener, picking up the session where it was left.</summary>
    public void EnterLogo()
    {
        DiscardOverlay();

        ScreenKind = AppScreen.Listener;
        _line.Clear();
        _caret = 0;
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

    /// <summary>Write the prompt and remember the row it started on.</summary>
    private void Prompt()
    {
        if (Engine.Screen.CursorCol != 0) Engine.Screen.NewLine();
        Engine.Screen.Write("> ");
        _anchorRow = Engine.Screen.CursorRow;
        _showPrompt = true;
        _readBaseCol = 2;
        _paintedLength = 0;
    }

    /// <summary>
    /// The longest line that can be typed. Fixed rather than window-sized: the window shrinks when
    /// the turtle page opens, so a limit based on the current window would let a child type a line
    /// that could no longer be drawn after ALT-T.
    /// </summary>
    public const int MaxInputLength = 256;

    /// <summary>
    /// Column the typed line starts at. Normally 2, just after the "> " prompt. When a program is
    /// waiting on READ there is no prompt to draw - the program's own question is on the screen -
    /// so the line starts wherever that program left the cursor.
    /// </summary>
    private int LineBaseCol => _showPrompt ? 2 : _readBaseCol;

    private int RowIndexOf(int index) => (LineBaseCol + index) / TextScreen.Cols;

    private int ColumnOf(int index) => (LineBaseCol + index) % TextScreen.Cols;

    private int RowCountFor(int length) =>
        length == 0 ? 1 : (LineBaseCol + length - 1) / TextScreen.Cols + 1;

    /// <summary>
    /// Which row of the typed line is painted on the anchor row, and how many rows are shown. A
    /// line longer than the text window scrolls inside the window rather than being written past
    /// the end of the grid, which is what used to throw and kill every key after it.
    /// </summary>
    private int _firstRow, _visibleRows = 1, _paintedLength;

    /// <summary>False while a running program is asking for a line, so its prompt is not erased.</summary>
    private bool _showPrompt = true;

    /// <summary>Column a waiting program left the cursor at, where its answer is typed.</summary>
    private int _readBaseCol;

    private void PaintInput()
    {
        if (ScreenKind != AppScreen.Listener) return;

        // Rows the text covers, plus the row the caret sits on: a line that exactly fills a row puts
        // the caret at the start of the next one, and that row has to be inside the visible block.
        int rows = Math.Max(RowCountFor(Math.Max(_line.Length, _paintedLength)), RowIndexOf(_caret) + 1);

        // ALT-T can shrink the window from under a line that was anchored above it, so pull the
        // anchor back inside before anything is measured against it.
        _anchorRow = Math.Clamp(_anchorRow, Screen.WindowTop, Screen.WindowBottom);

        int capacity = Math.Max(1, Screen.WindowBottom - _anchorRow + 1);

        // Scroll the transcript up so the whole line fits, exactly as a terminal does, but never
        // raise the anchor past the top of the window.
        while (_anchorRow + rows - 1 > Screen.WindowBottom && _anchorRow > Screen.WindowTop)
        {
            Screen.ScrollUp();
            _anchorRow--;
        }

        // If the line is still taller than the window - a 256 character line needs seven rows and a
        // split screen shows four - show the rows around the caret instead of writing past the last
        // row, which is what used to throw and deafen the keyboard for the rest of the session.
        capacity = Math.Max(1, Screen.WindowBottom - _anchorRow + 1);
        _firstRow = rows > capacity
            ? Math.Clamp(RowIndexOf(_caret) - capacity + 1, 0, rows - capacity)
            : 0;
        _visibleRows = Math.Min(rows - _firstRow, capacity);

        // Erase the cells this line owns, including the tail of a longer previous paint, so deleting
        // a character leaves no ghost and no cell outside the block is touched.
        int eraseUntil = Math.Max(_line.Length, _paintedLength);
        for (int i = 0; i < eraseUntil; i++)
        {
            if (!TryCellOf(i, out int row, out int col)) continue;
            Screen.Chars[row, col] = ' ';
            Screen.Attrs[row, col] = CellAttr.None;
        }

        for (int i = 0; i < _line.Length; i++)
        {
            if (!TryCellOf(i, out int row, out int col)) continue;
            Screen.Chars[row, col] = _line[i];
        }

        _paintedLength = _line.Length;
    }

    /// <summary>
    /// The grid cell for a position in the typed line, false when that part of the line is scrolled
    /// out of view. Every write the painter makes goes through here, which is what keeps a long line
    /// from reaching past the last row of the window.
    /// </summary>
    private bool TryCellOf(int index, out int row, out int col)
    {
        int rowIdx = RowIndexOf(index);
        col = ColumnOf(index);
        row = _anchorRow + rowIdx - _firstRow;

        return rowIdx >= _firstRow
            && rowIdx < _firstRow + _visibleRows
            && row >= Screen.WindowTop && row <= Screen.WindowBottom
            && col < TextScreen.Cols;
    }

    /// <summary>
    /// Where the caret sits, using the same arithmetic as PaintInput. Used to draw the blinking
    /// cursor, so it must always name a cell that exists.
    /// </summary>
    public (int Row, int Col) CaretPosition()
    {
        if (ScreenKind == AppScreen.Editor) return EditorCaret;
        if (ScreenKind != AppScreen.Listener) return (Screen.CursorRow, Screen.CursorCol);

        int last = LineBaseCol + _caret;
        int rowIdx = Math.Clamp(last / TextScreen.Cols, _firstRow, _firstRow + _visibleRows - 1);
        return (_anchorRow + rowIdx - _firstRow, Math.Min(last % TextScreen.Cols, TextScreen.Cols - 1));
    }

    /// <summary>Feed one key token into the machine.</summary>
    public void HandleKey(string token)
    {
        switch (ScreenKind)
        {
            case AppScreen.Splash:
                // A few Alt keys still work here, so a child who prefers amber or ADVANCED mode can
                // choose it before starting. Anything that would open an overlay is refused: the
                // splash has no transcript to go back to.
                if (!SplashKey(token)) EnterLogo();
                break;
            case AppScreen.Listener: HandleListenerKey(token); break;
            case AppScreen.Help: HandleHelpKey(token); break;
            case AppScreen.Sessions: HandleSessionKey(token); break;
            case AppScreen.Editor: HandleEditorKey(token); break;
            case AppScreen.Info: HandleInfoKey(token); break;
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
    /// The Alt keys that are safe from the splash: they change a setting rather than opening a view.
    /// </summary>
    private bool SplashKey(string token)
    {
        switch (token)
        {
            case "A-M": ToggleMode(); return true;
            case "A-G": ToggleTheme(); return true;
            case "A-C": ToggleCrt(); return true;
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
            case "A-L": ShowSessions(); return true;
            case "A-S": _ = SaveThenSay("SAVED " + CurrentName); return true;
            // Ctrl+S is the same save. Browsers open their own page-save dialog for it, so the key
            // has to be swallowed and given a meaning rather than left dead.
            case "C-S": _ = SaveThenSay("SAVED " + CurrentName); return true;
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
        Say(message);
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

    /// <summary>
    /// Park the engine's cursor just past the text being typed, so anything printed next starts
    /// below the child's line instead of writing over it.
    /// </summary>
    private void MoveCursorPastInput()
    {
        // Park on the row the line ends on, at the column just past its last character. A line
        // taller than the window is scrolled inside the window, so the end is clamped to the last
        // row rather than allowed to point past it.
        int rowIdx = RowIndexOf(_line.Length);
        Screen.CursorRow = Math.Min(_anchorRow + rowIdx, Screen.WindowBottom);
        Screen.CursorCol = ColumnOf(_line.Length);
    }

    /// <summary>Commit the typed line to the transcript and run it.</summary>
    private void SubmitLine()
    {
        string text = _line.ToString().Trim();

        if (AwaitingRead) { CompleteRead(text); return; }

        MoveCursorPastInput();
        Screen.NewLine();

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
        RequestRender();

        try
        {
            LogoValue result = await Engine.RunAsync(source);
            ReportToplevel(result);
        }
        catch (LogoException ex)
        {
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

    private void Say(string text)
    {
        if (ScreenKind != AppScreen.Listener) return;

        // Anything the machine prints must go below the line being typed, not on top of it.
        MoveCursorPastInput();
        Screen.NewLine();
        WriteWrapped(text, CellAttr.Dim);
        Prompt();
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

    private static IEnumerable<string> WrapToScreen(string text)
    {
        if (text.Length <= TextScreen.Cols) { yield return text; yield break; }

        int start = 0;
        while (start < text.Length)
        {
            int end = Math.Min(start + TextScreen.Cols, text.Length);
            if (end < text.Length)
            {
                // Break on a space so a word is never split in half.
                int lastSpace = text.LastIndexOf(' ', end, end - start);
                if (lastSpace > start) end = lastSpace;
            }

            yield return text.Substring(start, end - start).TrimEnd();
            start = end;
            while (start < text.Length && text[start] == ' ') start++;
        }
    }

    public void RequestBreak()
    {
        BreakRequested = true;
        Engine.RequestBreak();
    }

    private void CompleteRead(string? text)
    {
        TaskCompletionSource<string?>? tcs = _readTcs;
        _readTcs = null;

        // Park the engine's cursor just past the answer that was echoed at the prompt, then drop to
        // a new row, so whatever the program prints next starts below what the child typed instead
        // of writing over it.
        MoveCursorPastInput();
        Screen.NewLine();

        _line.Clear();
        _caret = 0;
        _showPrompt = true;
        _readBaseCol = 2;
        _paintedLength = 0;

        tcs?.TrySetResult(text);
    }

    // ---- graphics ---------------------------------------------------------------

    /// <summary>ALT-T: swap between the split screen and a whole screen of text.</summary>
    public void ToggleTextGraphics()
    {
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
        _anchorRow = Screen.CursorRow;
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
        if (ReplayGraphicsFunc is null) return;

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
        if (SetTurtleFunc is null) return;

        var turtle = new
        {
            x = GraphicsScene.ToSceneX(Engine.Turtle.X),
            y = GraphicsScene.ToSceneY(Engine.Turtle.Y),
            heading = Engine.Turtle.Heading,
            color = Engine.Turtle.PenColor,
            visible = Engine.Turtle.Visible,
        };

        await SetTurtleFunc.Invoke(JsonSerializer.Serialize(turtle));
    }

    /// <summary>
    /// Send only the strokes added since the last call. If items were removed (a CLS or CLEAN) or
    /// the background changed, this falls back to a full replay, because appending would desynchronise.
    /// </summary>
    public async Task AppendGraphicsAsync()
    {
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
        _helpLines = QuickReference.ReferenceLines(Engine.Primitives);
        _helpTop = 0;
        OpenOverlay(AppScreen.Help);
        PaintHelp();
        RequestRender();
    }

    private void PaintHelp()
    {
        Screen.Clear();
        for (int row = 0; row < TextScreen.Rows - 1 && _helpTop + row < _helpLines.Count; row++)
            Screen.PutLine(row, Fit(_helpLines[_helpTop + row]));

        int last = Math.Min(_helpTop + TextScreen.Rows - 2, _helpLines.Count - 1);
        Screen.PutLine(TextScreen.Rows - 1, Fit($"{_helpTop + 1}-{last + 1} OF {_helpLines.Count}  ARROWS SCROLL"), CellAttr.Reverse);
    }

    private void HandleHelpKey(string token)
    {
        int lastTop = Math.Max(0, _helpLines.Count - (TextScreen.Rows - 1));
        switch (token)
        {
            case "UP": _helpTop = Math.Max(0, _helpTop - 1); break;
            case "DOWN": _helpTop = Math.Min(lastTop, _helpTop + 1); break;
            case "PGUP": _helpTop = Math.Max(0, _helpTop - (TextScreen.Rows - 2)); break;
            case "PGDN": _helpTop = Math.Min(lastTop, _helpTop + TextScreen.Rows - 2); break;
            case "HOME": _helpTop = 0; break;
            case "END": _helpTop = lastTop; break;
            case "ESC": case "A-?": case "A-Q": case "A-SLASH": case "C-SLASH": CloseOverlay(); return;
            default: return;
        }
        PaintHelp();
        RequestRender();
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
            Screen.Put(row, 3, Fit($"{label}: {value}"));
            row++;
        }

        Line("FILE", CurrentName);
        Line("MODE", Engine.Mode.ToString());
        Line("VERSION", LogoInfo.VersionText);
        Line("LINES", Engine.LinesWritten.ToString());
        Line("COMMANDS", Engine.Primitives.All.Count.ToString());
        Line("PROCEDURES", Engine.Workspace.Procedures.Count.ToString());
        Line("VARIABLES", Engine.Workspace.Globals.Count.ToString());
        Line("STROKES", Engine.Scene.Items.Count.ToString());
        Line("TURTLE", $"X {Engine.Turtle.X:F0}  Y {Engine.Turtle.Y:F0}  H {Engine.Turtle.Heading:F0}");
        Line("SAVE SPACE", StorageAvailable ? "OK" : "NOT AVAILABLE");

        Screen.Put(15, 3, "ALT-? HELP    ALT-M MODE   ALT-L FILES");
        Screen.Put(16, 3, "ALT-G COLOR   ALT-C CRT    ALT-D FILE");
        Screen.Put(17, 3, "ALT-E EDITOR  ALT-T TEXT   ALT-B BREAK");
        Screen.Put(18, 3, "ALT-O OPEN A .logo FILE");
    }

    private void HandleInfoKey(string token)
    {
        if (token is "ESC" or "A-I" or "ENTER") CloseOverlay();
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
            ReturnTo: ScreenKind);

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
        Restore(stashed);
        ScreenKind = stashed.ReturnTo;

        // The stashed screen already held the prompt and whatever had been typed, so it is redrawn
        // rather than prompted again. Writing a fresh prompt here would leave two "> " lines.
        if (stashed.ReturnTo == AppScreen.Editor) PaintEditor();
        else PaintInput();

        RequestRender();
    }

    private void Restore(StashedScreen stashed)
    {
        for (int r = 0; r < TextScreen.Rows; r++)
        {
            Screen.PutLine(r, stashed.Chars[r]);
            for (int c = 0; c < TextScreen.Cols; c++) Screen.Attrs[r, c] = stashed.Attrs[r][c];
        }

        (Screen.CursorRow, Screen.CursorCol) = stashed.Cursor;
        _anchorRow = stashed.Cursor.Row;
    }

    /// <summary>
    /// Empty the overlay stack. Used when the view changes underneath everything - leaving the
    /// splash, or BYE - because a stashed screen for a view that no longer exists cannot be
    /// returned to.
    /// </summary>
    private void DiscardOverlay() => _overlays.Clear();

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
            string line = $"{marker} {session.Name}  {session.LinesWritten} LINES  {session.UpdatedAt:MM-dd HH:mm}";
            Screen.PutLine(listStart + i, Fit(line), i == _sessionIndex ? CellAttr.Reverse : CellAttr.None);
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
        LogoSession? session = FindSession(CurrentName);
        if (session is null)
        {
            session = SessionStore.Create(CurrentName);
            Store.Sessions.Add(session);
        }

        // Capture returns a new object; copy its mutable parts onto the stored session so the
        // creation date and identity survive a save.
        LogoSession fresh = LogoSession.Capture(Engine, CurrentName, session.Source, _history);
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
        LogoSession? session = FindSession(CurrentName);
        if (session is null) return;

        await DownloadFunc.Invoke(CurrentName, LogoSourceExporter.Export(session));
        Say("DOWNLOADED " + CurrentName);
    }

    /// <summary>ALT-O: load a .logo file the child picked, then run it.</summary>
    public async Task ImportAsync()
    {
        if (PickFileFunc is null) return;
        string? text = await PickFileFunc.Invoke(".logo,.txt,text/plain");
        if (string.IsNullOrWhiteSpace(text)) return;

        LogoSession? session = FindSession(CurrentName);
        if (session is not null) { session.Source = text; await SaveAsync(); }

        await RunAsync(text);
        Say("LOADED PROGRAM");
    }

    // ---- editor -----------------------------------------------------------------

    private List<string> _editorLines = new();
    private int _editorRow, _editorCol, _editorTop;

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

        Screen.PutLine(TextScreen.Rows - 1, Fit("ESC SAVE   ALT-R RUN   ALT-L FILES"), CellAttr.Reverse);
    }

    private void HandleEditorKey(string token)
    {
        if (TryGlobalKey(token))
        {
            PaintEditor();
            RequestRender();
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
    /// Store what the child wrote. Saving here is the point of ESC: the source has to reach the
    /// browser before anything else can overwrite it.
    /// </summary>
    private void CommitEditor()
    {
        LogoSession? session = FindSession(CurrentName);
        if (session is null) return;

        session.Source = string.Join("\n", _editorLines);
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
            if (machine.YieldFrameFunc is { } yieldFrame) await yieldFrame();

            // Repaint the picture as a program draws it, so REPEAT 4 [FD 100 RT 90] animates
            // instead of appearing all at once when the program finishes.
            await machine.AppendGraphicsAsync();
        }

        public bool IsBreakRequested() => machine.BreakRequested;

        public Task<string?> ReadLineAsync()
        {
            machine._readTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

            // The answer is typed where the program left the cursor, which is not the prompt the
            // child pressed ENTER at - a program may have printed several lines since then. The
            // listener's own "> " is suppressed here: drawing it would erase the question the
            // program just asked, which is the only reason the child is being asked to type.
            machine._anchorRow = machine.Screen.CursorRow;
            machine._readBaseCol = machine.Screen.CursorCol;
            machine._showPrompt = false;
            machine._paintedLength = 0;
            machine.RequestRender();
            return machine._readTcs.Task;
        }

        public void Invalidate() => machine.RequestRender();
    }
}
