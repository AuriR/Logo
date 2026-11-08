using MaturinLogo.Core.Primitives;
using MaturinLogo.Core.Screen;
using MaturinLogo.Core.Turtle;

namespace MaturinLogo.Core.Machine;

/// <summary>
/// The turtle's shapes laid out as cards for VIEWSHAPES, and as text for anywhere the canvas is not.
/// </summary>
/// <remarks>
/// The same argument as <see cref="ColorChart"/>: a shape is something you have to *see*, and a grid
/// of one phosphor colour can only describe it. The cards are painted by the browser's own turtle
/// painter rather than by a second copy of each shape, so this chart cannot show a shape the turtle
/// is not capable of wearing - the one thing a chart that exists to settle "what does SHAPE 4 look
/// like" must not get wrong.
/// <para>
/// Geometry is in scene units (280x192, origin top-left, y down) and the caption position is computed
/// here rather than guessed by the browser, so `interop.js` stays a dumb painter and the layout can be
/// tested without one.
/// </para>
/// </remarks>
public static class ShapeChart
{
    /// <summary>Three across, so each card is wide enough to hold a shape and still read as a card.</summary>
    public const int Columns = 3;

    public const double BoxWidth = 86;
    public const double BoxHeight = 62;
    public const double GapX = 8;
    public const double GapY = 8;

    /// <summary>Room for the caption, which goes *under* the box because inside it is where the shape is.</summary>
    public const double Caption = 14;

    /// <summary>Left margin, which centres the three columns in the 280-unit picture.</summary>
    public static readonly double Left =
        (GraphicsScene.SpaceWidth - Columns * BoxWidth - (Columns - 1) * GapX) / 2;

    public const double Top = 10;

    /// <summary>
    /// The ink of a card, and the colour the shape is drawn in: white, which reads on the chart's
    /// black page without having to be chosen against a swatch the way the colour chart's captions do.
    /// </summary>
    public const int Ink = 2;

    /// <summary>
    /// How long the turtle's own painter draws a shape on the live page, in scene units - the same
    /// `Math.max(4, 9 * scale)` the painter uses when it is not told otherwise.
    /// </summary>
    public const double TurtleLength = 9;

    /// <summary>
    /// A shape drawn at the turtle's own size would be a speck in a 62-unit card, so the chart asks
    /// for one about three times as long and keeps a margin either side of it.
    /// </summary>
    public const double ShapeLength = 26;

    private static double RowHeight => BoxHeight + Caption + GapY;

    /// <summary>Bottom edge of the last caption, in scene units.</summary>
    public static double Bottom(int count = TurtleShapes.Count) =>
        Top + Math.Ceiling(count / (double)Columns) * RowHeight - GapY;

    /// <summary>
    /// One card per shape, carrying the number a child types into SHAPE and the name the quick
    /// reference uses - so the chart, `SHAPE`'s own error message and the reference all say the same
    /// six words.
    /// </summary>
    public static IEnumerable<DrawShapeCard> Cards()
    {
        for (int i = 0; i < TurtleShapes.Count; i++)
        {
            int col = i % Columns;
            int row = i / Columns;

            double x = Left + col * (BoxWidth + GapX);
            double y = Top + row * RowHeight;

            yield return new DrawShapeCard(
                x, y, BoxWidth, BoxHeight,
                Shape: i,
                Color: Ink,
                Text: $"{i} {TurtleShapes.Names[i]}",
                TextColor: Ink,
                // Centred under the box, not inside it.
                TextX: x + BoxWidth / 2,
                TextY: y + BoxHeight + Caption / 2,
                ShapeLength: ShapeLength);
        }
    }

    /// <summary>
    /// The shapes as text rows, for the listener when there is no canvas.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Greedily packed to the full 40 columns rather than fixed into two or three per row, because this
    /// has to survive EASY mode's four-row window: anything longer scrolls the top off and leaves a
    /// child staring at the last few shapes with no idea what they are a list of. One per row is six
    /// rows, and two per row is three - which with the caller's headline is exactly the window, so any
    /// transcript already on the last row pushes shape 0 off. Packed, the six shapes are two rows.
    /// </para>
    /// <para>
    /// No heading of its own, for the same reason: the headline the caller prints already says what the
    /// list is, and a heading above it would only push a shape off the top.
    /// </para>
    /// </remarks>
    public static List<string> TextLines() =>
        TextWrap.Pack(Enumerable
            .Range(0, TurtleShapes.Count)
            .Select(i => $"{i} {TurtleShapes.Names[i]}"));

    /// <summary>
    /// The line printed under the text list, 39 columns so it never wraps. Names the key that opens
    /// the picture, not Alt-T, which switches the grid between text and graphics and is one of the
    /// keys a chart deliberately ignores - a hint that does nothing is worse than no hint.
    /// </summary>
    public const string Headline = "THE SHAPES - SHAPE n - ALT-W SHOWS THEM";
}
