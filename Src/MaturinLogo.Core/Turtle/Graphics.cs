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

    /// <summary>The first six indices are the hardware palette; the rest are the enhanced //e range.</summary>
    public const int HiResCount = 6;

    /// <summary>True when <paramref name="index"/> is one of the six colours a real hi-res screen made.</summary>
    public static bool IsHiRes(int index) => index % Count < HiResCount;

    /// <summary>
    /// The index to write text over a swatch of <paramref name="index"/> in: white on the dark
    /// colours, black on the bright ones. Computed from the hex triplet rather than hard-coded, so
    /// a colour changed in the table above changes its own caption with it.
    /// </summary>
    public static int ContrastIndex(int index) => IsDark(index) ? 2 : Background;

    /// <summary>
    /// True when <paramref name="index"/> is dark enough to need light text on it. Split out because
    /// the colour chart is the one place a palette entry has to be judged rather than merely drawn,
    /// and a test wants to ask the same question the drawing does.
    /// </summary>
    public static bool IsDark(int index)
    {
        string hex = Hex[index % Hex.Length];
        int r = Convert.ToInt32(hex.Substring(1, 2), 16);
        int g = Convert.ToInt32(hex.Substring(3, 2), 16);
        int b = Convert.ToInt32(hex.Substring(5, 2), 16);

        // Perceived brightness rather than a plain average: green dominates how bright a colour
        // looks to an eye, which is why bright green needs a dark caption and blue does not.
        double luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        return luminance <= 140;
    }
}

/// <summary>
/// The turtle's shapes. <c>SHAPE</c> picks one by number and the browser draws it, so the list
/// lives here: it is what the interpreter validates against, what the quick reference prints, and
/// what the test in <c>MachineTests</c> compares the JavaScript against, so the two cannot drift.
/// </summary>
public static class TurtleShapes
{
    /// <summary>Shape names in index order, as the reference and the error hint show them.</summary>
    public static readonly string[] Names =
    [
        "ARROW", "TRIANGLE", "TURTLE", "DOT", "DIAMOND", "BALL",
    ];

    public const int Count = 6;

    /// <summary>Highest shape index SHAPE accepts.</summary>
    public const int MaxIndex = Count - 1;

    /// <summary>"0 arrow, 1 triangle, ..." - the text SHAPE's help and its error both use.</summary>
    public static string Describe() =>
        string.Join(", ", Names.Select((n, i) => $"{i} {n.ToLowerInvariant()}"));
}

/// <summary>One straight line the turtle drew.</summary>
public sealed record DrawSegment(double X1, double Y1, double X2, double Y2, int Color, double Width);

/// <summary>One LABEL text stamp on the graphics page.</summary>
public sealed record DrawLabel(string Text, double X, double Y, int Color);

/// <summary>
/// One palette swatch for the VIEWCOLORS chart: a filled box, its number and name inside it.
/// </summary>
/// <remarks>
/// Not part of what a turtle can draw, so it never enters <see cref="GraphicsScene"/> - the chart is
/// painted straight to the canvas by the host. It lives here because the browser's item vocabulary
/// belongs to the picture, not to a screen. The box is always outlined in
/// <paramref name="TextColor"/>, which is the only way index 0 - black on a black background - reads
/// as a swatch rather than a hole.
/// </remarks>
public sealed record DrawSwatch(
    double X, double Y, double Width, double Height, int Color, string Text, int TextColor);

/// <summary>
/// One shape card for the VIEWSHAPES chart: a box, the turtle itself drawn in the middle of it, and
/// a caption placed by the chart rather than by the browser.
/// </summary>
/// <remarks>
/// Separate from <see cref="DrawSwatch"/> on purpose. A swatch fills its box, so a shape would be
/// invisible inside it; and a swatch centres its label, so a caption under the box would be drawn
/// through the shape. Two presentations that disagree about where everything goes are two records.
/// <para>
/// <paramref name="Shape"/> is the index a child types into SHAPE, and <paramref name="ShapeLength"/>
/// is how long to draw it in scene units. The browser paints the card with the same turtle painter
/// that draws the live cursor, so the chart cannot promise a shape the turtle does not have; the
/// painter's own fixed length is right beside a line being drawn and far too small to read inside a
/// card, so the chart says how big it wants it and the painter obeys. That keeps the layout decisions
/// in Core, where they can be tested.
/// </para>
/// </remarks>
public sealed record DrawShapeCard(
    double X, double Y, double Width, double Height,
    int Shape, int Color, string Text, int TextColor,
    double TextX, double TextY, double ShapeLength);

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

    /// <remarks>
    /// There is deliberately no <c>Add(DrawSwatch)</c> or <c>Add(DrawShapeCard)</c>. The charts are
    /// viewers painted straight to the canvas, and an overload here would be one keystroke away from
    /// putting a chart into the picture - where it would be saved into the session and exported into
    /// the child's .logo file, so their spiral came back with boxes painted over it.
    /// </remarks>
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
