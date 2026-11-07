using MaturinLogo.Core.Interpreter;
using MaturinLogo.Core.Screen;
using MaturinLogo.Core.Turtle;
using MaturinLogo.Core.Values;

namespace MaturinLogo.Core.Sessions;

/// <summary>
/// Everything one Logo session holds: the source the child wrote, the procedures and variables in
/// the workspace, the turtle, and the picture as a replayable item list. Restoring a session puts
/// all of it back, so a child can close the browser mid-drawing and pick up where they left off.
/// </summary>
public sealed class LogoSession
{
    /// <summary>Storage format version, so an old session can be migrated instead of lost.</summary>
    public const int FormatVersion = 1;

    public int Version { get; init; } = FormatVersion;

    /// <summary>File-style name shown everywhere: SESSION1.logo.</summary>
    public string Name { get; init; } = "SESSION1.logo";

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Updated whenever anything is saved; shown on the Alt-I dashboard.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The Logo source the child wrote in the editor or typed at the listener.</summary>
    public string Source { get; set; } = "";

    /// <summary>EASY or ADVANCED, remembered per session.</summary>
    public LogoMode Mode { get; set; } = LogoMode.Easy;

    /// <summary>Lines of Logo written, counted by the engine.</summary>
    public int LinesWritten { get; set; }

    public TurtleSnapshot Turtle { get; set; } = new();

    /// <summary>Variables as name/value pairs of Logo text, which keeps the stored form readable.</summary>
    public List<StoredValue> Globals { get; set; } = new();

    /// <summary>
    /// Property lists from PUT, keyed by the name they hang off. Stored alongside the globals
    /// because a child who runs PUT and then reloads expects GET to still answer.
    /// </summary>
    public List<StoredProperty> Properties { get; set; } = new();

    /// <summary>Procedures stored as source text, so loading re-parses rather than trusting a binary shape.</summary>
    public List<string> Procedures { get; set; } = new();

    /// <summary>The drawn picture, replayable at any canvas size.</summary>
    public List<SceneItem> Scene { get; set; } = new();

    public int Background { get; set; } = ApplePalette.Background;

    /// <summary>The text that was on screen, so a session comes back with its transcript.</summary>
    public List<string> ScreenLines { get; set; } = new();

    /// <summary>Commands typed at the listener, oldest first, for arrow-key recall.</summary>
    public List<string> History { get; set; } = new();

    /// <summary>How many text rows the LOWER split reserves, remembered per session.</summary>
    public int LowerLines { get; set; } = 4;

    /// <summary>Snapshot a running engine into a session, ready to be stored.</summary>
    public static LogoSession Capture(Engine engine, string name, string source, IEnumerable<string> history)
    {
        var session = new LogoSession
        {
            Name = name,
            Source = source,
            Mode = engine.Mode,
            LinesWritten = engine.LinesWritten,
            LowerLines = engine.LowerLines,
            Background = engine.Scene.Background,
            Turtle = TurtleSnapshot.From(engine.Turtle),
            UpdatedAt = DateTime.UtcNow,
        };

        foreach (var (key, value) in engine.Workspace.Globals)
            session.Globals.Add(new StoredValue { Name = key, Text = Printer.Format(value, readable: true) });

        foreach (var (thing, bag) in engine.Workspace.Properties)
            foreach (var (property, value) in bag)
                session.Properties.Add(new StoredProperty
                {
                    Name = thing,
                    Property = property,
                    Text = Printer.Format(value, readable: true),
                });

        foreach (var proc in engine.Workspace.Procedures.Values)
            session.Procedures.Add(ProcedureText.Format(proc));

        foreach (var item in engine.Scene.Items)
        {
            var stored = SceneItem.From(item);
            if (stored is not null) session.Scene.Add(stored);
        }

        foreach (var line in history) session.History.Add(line);

        for (int r = 0; r < TextScreen.Rows; r++)
            session.ScreenLines.Add(engine.Screen.GetRowText(r));

        return session;
    }

    /// <summary>Push a session back into an engine, as if it had just been run.</summary>
    public void Restore(Engine engine)
    {
        engine.Mode = Mode;
        engine.LinesWritten = LinesWritten;
        engine.Workspace.Clear();

        Turtle.ApplyTo(engine.Turtle);
        engine.Scene.Background = Background;

        var items = new List<object>(Scene.Count);
        foreach (var item in Scene)
        {
            var restored = item.ToItem();
            if (restored is not null) items.Add(restored);
        }
        engine.TurtleView.Restore(items, Background);

        // The workspace is rebuilt by replaying the saved procedures and globals, which re-parses
        // them and so validates them against the current interpreter rather than trusting stored
        // internals. Anything that no longer parses is skipped, keeping the rest of the session.
        foreach (var text in Procedures)
            engine.DefineProcedureFromSource(text);

        foreach (var global in Globals)
            engine.RestoreGlobal(global.Name, global.Text);

        foreach (var property in Properties)
            engine.RestoreProperty(property.Name, property.Property, property.Text);

        if (LowerLines > 0) engine.ShowLower(LowerLines);
        else engine.ShowFullText();
    }
}

/// <summary>One stored value as Logo text, paired with the name it was kept under.</summary>
public sealed class StoredValue
{
    public string Name { get; set; } = "";

    /// <summary>Printer-readable text, which the loader reads back with the Logo reader.</summary>
    public string Text { get; set; } = "";
}

/// <summary>
/// One stored property: the value PUT put on a name, under a property. Three parts because a
/// property list hangs off a thing, not off the workspace.
/// </summary>
public sealed class StoredProperty
{
    /// <summary>The word the property was put on, without its quote.</summary>
    public string Name { get; set; } = "";

    /// <summary>Which property of that word, without its quote.</summary>
    public string Property { get; set; } = "";

    /// <summary>Printer-readable text, which the loader reads back with the Logo reader.</summary>
    public string Text { get; set; } = "";
}

/// <summary>The turtle's position and pen, small enough to inline in a session.</summary>
public sealed class TurtleSnapshot
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Heading { get; set; }
    public bool PenDown { get; set; } = true;
    public int PenColor { get; set; } = 2;
    public double PenSize { get; set; } = 1;
    public int Shape { get; set; }
    public bool Visible { get; set; } = true;

    public static TurtleSnapshot From(TurtleState turtle) => new()
    {
        X = turtle.X,
        Y = turtle.Y,
        Heading = turtle.Heading,
        PenDown = turtle.PenDown,
        PenColor = turtle.PenColor,
        PenSize = turtle.PenSize,
        Shape = turtle.Shape,
        Visible = turtle.Visible,
    };

    public void ApplyTo(TurtleState turtle)
    {
        turtle.X = X;
        turtle.Y = Y;
        turtle.Heading = Heading;
        turtle.PenDown = PenDown;
        turtle.PenColor = PenColor;
        turtle.PenSize = PenSize;
        turtle.Shape = Shape;
        turtle.Visible = Visible;
    }
}

/// <summary>
/// One drawn thing in stored form. Segments and labels are the only shapes the scene holds, so a
/// small tagged record covers the whole picture without serialising a class hierarchy.
/// </summary>
public sealed class SceneItem
{
    public string Kind { get; set; } = "segment";
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }
    public string Text { get; set; } = "";
    public int Color { get; set; }
    public double Width { get; set; } = 1;

    public static SceneItem? From(object item) => item switch
    {
        DrawSegment s => new SceneItem
        {
            Kind = "segment",
            X1 = s.X1,
            Y1 = s.Y1,
            X2 = s.X2,
            Y2 = s.Y2,
            Color = s.Color,
            Width = s.Width,
        },
        DrawLabel l => new SceneItem
        {
            Kind = "label",
            X1 = l.X,
            Y1 = l.Y,
            Text = l.Text,
            Color = l.Color,
        },
        _ => null,
    };

    public object? ToItem() => Kind switch
    {
        "segment" => new DrawSegment(X1, Y1, X2, Y2, Color, Width),
        "label" => new DrawLabel(Text, X1, Y1, Color),
        _ => null,
    };
}
