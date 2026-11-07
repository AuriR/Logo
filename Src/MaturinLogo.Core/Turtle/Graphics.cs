namespace MaturinLogo.Core.Turtle;

/// <summary>
/// The Apple II hi-res colours Logo drew with. Keeping the palette as indices means the browser
/// layer can restyle it (for example the green phosphor look) without the interpreter caring.
/// </summary>
public static class ApplePalette
{
    /// <summary>Index 0 is always the background colour.</summary>
    public const int Background = 0;

    public static readonly string[] Names =
    {
        "BLACK", "GREEN", "WHITE", "PURPLE", "ORANGE", "BLUE",
        "CYAN", "MAGENTA", "RED", "YELLOW", "BROWN", "PINK",
        "LTBLUE", "LTGRAY", "DKGRAY", "GRAY",
    };

    /// <summary>Hex colours for the 16 Logo pen colours; the first six are true Apple II hi-res.</summary>
    public static readonly string[] Hex =
    {
        "#000000", "#33CC33", "#FFFFFF", "#AA66FF", "#FF9933", "#5588FF",
        "#33CCCC", "#FF55AA", "#FF3333", "#FFFF33", "#AA6633", "#FFB8C6",
        "#88BBFF", "#C8C8C8", "#777777", "#999999",
    };

    public const int Count = 16;

    /// <summary>Resolve a colour input (number, or colour name word) to a palette index.</summary>
    public static bool TryResolve(object? value, out int index)
    {
        index = 0;
        switch (value)
        {
            case Values.LogoValue v when v.Kind == Values.LogoKind.Number:
                int n = (int)Math.Round(v.Number);
                if (n < 0 || n >= Count) return false;
                index = n;
                return true;
            case Values.LogoValue v when v.Kind == Values.LogoKind.Word:
                int found = Array.FindIndex(Names, name => string.Equals(name, v.Word, StringComparison.OrdinalIgnoreCase));
                if (found < 0) return false;
                index = found;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Apple Logo's SETBG accepted 0-15; anything else is out of range.</summary>
    public static bool InRange(int index) => index >= 0 && index < Count;
}

/// <summary>One straight line the turtle drew.</summary>
public sealed record DrawSegment(double X1, double Y1, double X2, double Y2, int Color, double Width);

/// <summary>One LABEL text stamp on the graphics page.</summary>
public sealed record DrawLabel(string Text, double X, double Y, int Color);

/// <summary>Where the turtle (or an erased label) was painted, so the picture can be restored.</summary>
public sealed record StampItem(double X, double Y, int Shape, int Color);

/// <summary>
/// Everything drawn on the graphics page, as a replayable item list rather than a bitmap. A
/// replayable scene is what lets a session be saved as source and restored exactly, and lets
/// the browser layer redraw at any canvas size.
/// </summary>
public sealed class GraphicsScene
{
    /// <summary>Logical drawing space: Apple II hi-res was 280x192 with the turtle origin centred.</summary>
    public const int SpaceWidth = 280;
    public const int SpaceHeight = 192;
    public const double OriginX = SpaceWidth / 2.0;
    public const double OriginY = SpaceHeight / 2.0;

    public List<object> Items { get; } = new();

    public int Background { get; set; } = ApplePalette.Background;

    /// <summary>True once anything has been drawn, so the host knows to show the graphics page.</summary>
    public bool HasContent => Items.Count > 0;

    public void Add(DrawSegment segment) => Items.Add(segment);
    public void Add(DrawLabel label) => Items.Add(label);
    public void Add(StampItem stamp) => Items.Add(stamp);

    public void Clear()
    {
        Items.Clear();
        Background = ApplePalette.Background;
    }

    /// <summary>Convert turtle space (origin centre, y up) to scene space (origin top-left, y down).</summary>
    public static double ToSceneX(double x) => OriginX + x;
    public static double ToSceneY(double y) => OriginY - y;
}

/// <summary>Receives turtle drawing. Implemented by the scene in Core; the browser reads the scene.</summary>
public interface IGraphicsHost
{
    GraphicsScene Scene { get; }
    void Draw(DrawSegment segment);
    void Stamp(DrawLabel label);
    void EraseAll();
    void SetBackground(int color);
}

/// <summary>The turtle's own state. Heading is in Logo degrees: 0 = north, increasing clockwise.</summary>
public sealed class TurtleState
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Heading { get; set; }
    public bool PenDown { get; set; } = true;
    public int PenColor { get; set; } = 2;   // Apple Logo started with white pen
    public double PenSize { get; set; } = 1;
    public int Shape { get; set; }
    public bool Visible { get; set; } = true;

    /// <summary>Screen position of the tip, in turtle space.</summary>
    public (double X, double Y) Tip => (X, Y);

    public void Home()
    {
        X = 0;
        Y = 0;
        Heading = 0;
    }
}
