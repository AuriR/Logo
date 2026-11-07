using System.Text;
using MaturinLogo.Core.Screen;

namespace MaturinLogo.Core.Machine;

/// <summary>
/// Turns the 40x24 cell buffer into one HTML string.
/// </summary>
/// <remarks>
/// The whole grid is emitted at once and swapped in as a MarkupString rather than diffed cell by
/// cell. Blazor would otherwise compare 960 spans every repaint, which is both slow and prone to
/// leaving stale characters behind when a row shrinks. This is the approach the RetroWorks
/// AppleWorks port proved out in a browser.
/// <para>
/// Every character is HTML-escaped on the way out, and the cursor is applied as a class on the
/// cell it sits on, so nothing Logo prints can inject markup into the page.
/// </para>
/// </remarks>
public static class ScreenGrid
{
    /// <summary>
    /// Build the grid. The cursor is drawn where the caller says rather than from the screen's own
    /// position, because the listener and the editor put the caret somewhere the text screen knows
    /// nothing about.
    /// </summary>
    public static string Build(TextScreen screen, int cursorRow, int cursorCol, bool cursorVisible)
    {
        var sb = new StringBuilder(TextScreen.Rows * TextScreen.Cols * 24);

        for (int row = 0; row < TextScreen.Rows; row++)
        {
            sb.Append("<div class=\"ml-row\">");
            for (int col = 0; col < TextScreen.Cols; col++)
            {
                sb.Append("<span class=\"ml-cell");

                string attrClass = AttrClass(screen.Attrs[row, col]);
                if (attrClass.Length > 0) sb.Append(' ').Append(attrClass);

                if (cursorVisible && row == cursorRow && col == cursorCol) sb.Append(" cursor");

                sb.Append("\">").Append(Escape(screen.Chars[row, col])).Append("</span>");
            }
            sb.Append("</div>");
        }

        return sb.ToString();
    }

    private static string AttrClass(CellAttr attr) => attr switch
    {
        CellAttr.None => "",
        CellAttr.Bold => "a-b",
        CellAttr.Reverse => "a-r",
        CellAttr.Dim => "a-dim",
        CellAttr.Bold | CellAttr.Reverse => "a-b a-r",
        CellAttr.Bold | CellAttr.Dim => "a-b a-dim",
        CellAttr.Reverse | CellAttr.Dim => "a-r a-dim",
        CellAttr.Bold | CellAttr.Reverse | CellAttr.Dim => "a-b a-r a-dim",
        _ => "",
    };

    /// <summary>
    /// Escape one character. Blank cells are written as a non-breaking space: a span holding a
    /// plain space can collapse to zero width, which would let a row drift out of its 40 columns.
    /// </summary>
    private static string Escape(char c) => c switch
    {
        ' ' => "&nbsp;",
        '<' => "&lt;",
        '>' => "&gt;",
        '&' => "&amp;",
        '"' => "&quot;",
        '\'' => "&#39;",
        _ => c.ToString(),
    };
}
