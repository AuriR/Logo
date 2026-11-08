using MaturinLogo.Core.Primitives;
using MaturinLogo.Core.Screen;
using MaturinLogo.Core.Turtle;

namespace MaturinLogo.Core.Machine;

/// <summary>
/// The palette laid out as swatches for VIEWCOLORS, and as text for anywhere the canvas is not.
/// </summary>
/// <remarks>
/// The geometry is in <b>scene</b> units (280x192, origin top-left, y down) because a chart is laid
/// out on the picture rather than driven by a turtle, and a turtle's y grows upward — which for a
/// grid of swatches would only be a chance to get the rows backwards. Both shapes are built here
/// rather than in the browser so the chart can be tested without one, and so the two views of the
/// palette cannot disagree about what colour 9 is called.
/// </remarks>
public static class ColorChart
{
    /// <summary>Swatches per row. Four keeps every box wide enough for its name to fit inside.</summary>
    public const int Columns = 4;

    public const double BoxWidth = 62;
    public const double BoxHeight = 26;
    public const double GapX = 6;
    public const double GapY = 4;

    /// <summary>Room for the caption line under each box.</summary>
    public const double Caption = 10;

    /// <summary>Left margin, which centres the four columns in the 280-unit picture.</summary>
    public static readonly double Left =
        (GraphicsScene.SpaceWidth - Columns * BoxWidth - (Columns - 1) * GapX) / 2;

    /// <summary>Top of the chart, clear of the picture's edge.</summary>
    public const double Top = 6;

    private static double RowHeight => BoxHeight + Caption + GapY;

    /// <summary>Bottom edge of <paramref name="count"/> swatches, in scene units.</summary>
    public static double Bottom(int count = ApplePalette.Count) =>
        Top + Math.Ceiling(count / (double)Columns) * RowHeight;

    /// <summary>
    /// One swatch per palette entry: the box, its number and name inside it, and the caption in the
    /// colour that reads against the box rather than the one that matches it.
    /// </summary>
    public static IEnumerable<DrawSwatch> Swatches()
    {
        for (int i = 0; i < ApplePalette.Count; i++)
        {
            int col = i % Columns;
            int row = i / Columns;

            yield return new DrawSwatch(
                Left + col * (BoxWidth + GapX),
                Top + row * RowHeight,
                BoxWidth,
                BoxHeight,
                Color: i,
                // Number first, then the name: the number is what a child types into SETPC, and the
                // name is what tells them which one they meant.
                Text: $"{i} {ApplePalette.Names[i]}",
                TextColor: ApplePalette.ContrastIndex(i));
        }
    }

    /// <summary>
    /// The same palette as text rows, for the listener's transcript when there is no canvas. The grid
    /// is one phosphor colour, so this names the colours instead of showing them - but the numbers are
    /// the part a child types into SETPC, and those come over perfectly well.
    /// </summary>
    /// <remarks>
    /// Packed like <see cref="ShapeChart.TextLines"/> rather than fixed at two per row, so the two
    /// sibling fallbacks agree about how much room a row has. Four rows for sixteen colours, plus the
    /// headline the caller prints, is exactly EASY mode's window - and the headline goes last, so what
    /// a child reads is the tail of the list rather than its first entry.
    /// </remarks>
    public static List<string> TextLines() =>
        TextWrap.Pack(Enumerable
            .Range(0, ApplePalette.Count)
            .Select(i => $"{i} {ApplePalette.Names[i]}"));

    /// <summary>
    /// The line printed under the text list. Names the key that opens the picture, not Alt-T, which
    /// switches the grid between text and graphics and is one of the keys a chart deliberately
    /// ignores - a hint that does nothing is worse than no hint.
    /// </summary>
    public const string Headline = "THE COLOURS - SETPC n - ALT-V SHOWS THEM";
}
