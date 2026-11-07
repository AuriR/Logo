using System.Text;
using MaturinLogo.Core.Screen;

namespace MaturinLogo.Core.Screen;

/// <summary>Cell attribute bits. Apple II text mode had no bold or underline, so these carry
/// reverse video and the emphasis used by splash art, headers and highlighted rows.</summary>
[Flags]
public enum CellAttr : byte
{
    None = 0,
    Bold = 1,
    Reverse = 2,
    Dim = 4,
}

/// <summary>
/// The 40x24 character screen as the Apple II saw it: a fixed grid of cells with a cursor and a
/// text window that can shrink to make room for the graphics page (Logo's LOWER command).
/// Pure text state - it knows nothing about the browser.
/// </summary>
public sealed class TextScreen
{
    public const int Cols = 40;
    public const int Rows = 24;

    public readonly char[,] Chars = new char[Rows, Cols];
    public readonly CellAttr[,] Attrs = new CellAttr[Rows, Cols];

    /// <summary>First row the text cursor may use. Rows above belong to the graphics page.</summary>
    public int WindowTop { get; private set; }

    /// <summary>Last row the text cursor may use (inclusive).</summary>
    public int WindowBottom { get; private set; } = Rows - 1;

    public int CursorRow { get; set; }
    public int CursorCol { get; set; }

    /// <summary>Attribute applied to newly written text (INVERSE/NORMAL toggle this).</summary>
    public CellAttr PenAttr { get; set; } = CellAttr.None;

    /// <summary>Raised whenever anything changes so the host can repaint once per step.</summary>
    public event Action? Changed;

    public TextScreen() => Clear();

    /// <summary>Number of text rows currently available.</summary>
    public int WindowHeight => WindowBottom - WindowTop + 1;

    /// <summary>Reserve the rows above <paramref name="top"/> for graphics, as Logo LOWER does.</summary>
    public void SetTextWindow(int top)
    {
        top = Math.Clamp(top, 0, Rows - 1);
        if (top == WindowTop) return;
        WindowTop = top;
        ClampCursor();
        Raise();
    }

    /// <summary>
    /// Blank the whole grid, including the rows behind the graphics page.
    /// </summary>
    /// <remarks>
    /// The text window is deliberately left alone. Logo's CLS erases what is on screen; it does not
    /// hand the graphics page back to text, so a child typing CLS inside LOWER 4 keeps their turtle
    /// area. Only the constructor resets the window, which is what a fresh machine means.
    /// </remarks>
    public void Clear()
    {
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
            {
                Chars[r, c] = ' ';
                Attrs[r, c] = CellAttr.None;
            }
        CursorRow = WindowTop;
        CursorCol = 0;
        PenAttr = CellAttr.None;
        Raise();
    }

    /// <summary>Blank the grid and start a whole new text screen, window included.</summary>
    public void Reset()
    {
        WindowTop = 0;
        WindowBottom = Rows - 1;
        Clear();
    }

    /// <summary>Clear only the text window, leaving the rows behind the graphics page untouched.</summary>
    public void ClearText()
    {
        for (int r = WindowTop; r <= WindowBottom; r++)
            for (int c = 0; c < Cols; c++)
            {
                Chars[r, c] = ' ';
                Attrs[r, c] = CellAttr.None;
            }
        CursorRow = WindowTop;
        CursorCol = 0;
        Raise();
    }

    /// <summary>Clear one row of the grid.</summary>
    public void ClearRow(int row, CellAttr attr = CellAttr.None)
    {
        if (row < 0 || row >= Rows) return;
        for (int c = 0; c < Cols; c++)
        {
            Chars[row, c] = ' ';
            Attrs[row, c] = attr;
        }
        Raise();
    }

    /// <summary>Move the cursor to the start of the text window's top row.</summary>
    public void Home()
    {
        CursorRow = WindowTop;
        CursorCol = 0;
        Raise();
    }

    /// <summary>Write text at an absolute position - used by splash art and overlay screens.</summary>
    public void Put(int row, int col, string text, CellAttr attr = CellAttr.None)
    {
        if (row < 0 || row >= Rows) return;
        for (int i = 0; i < text.Length; i++)
        {
            int c = col + i;
            if (c < 0) continue;
            if (c >= Cols) break;
            Chars[row, c] = text[i];
            Attrs[row, c] = attr;
        }
        Raise();
    }

    /// <summary>Write a whole row, padded or truncated to exactly 40 columns.</summary>
    public void PutLine(int row, string text, CellAttr attr = CellAttr.None)
    {
        ClearRow(row, attr);
        Put(row, 0, text, attr);
    }

    /// <summary>Center one line of text within the row.</summary>
    public void PutCentered(int row, string text, CellAttr attr = CellAttr.None)
    {
        ClearRow(row, attr);
        Put(row, Math.Max(0, (Cols - text.Length) / 2), text, attr);
    }

    /// <summary>Write text at the cursor and leave the cursor where the text ended.</summary>
    public void PutAndAdvance(int row, int col, string text, CellAttr attr = CellAttr.None)
    {
        Put(row, col, text, attr);
        CursorRow = row;
        CursorCol = Math.Min(col + text.Length, Cols - 1);
        Raise();
    }

    /// <summary>Write text at the cursor, scrolling the window when the cursor runs off the bottom.</summary>
    public void Write(string text, CellAttr? attr = null)
    {
        CellAttr a = attr ?? PenAttr;
        foreach (char ch in text)
        {
            if (ch == '\n') { NewLine(); continue; }
            if (ch == '\r') continue;
            if (ch == '\b')
            {
                if (CursorCol > 0)
                {
                    CursorCol--;
                    Chars[CursorRow, CursorCol] = ' ';
                }
                continue;
            }
            Chars[CursorRow, CursorCol] = ch;
            Attrs[CursorRow, CursorCol] = a;
            CursorCol++;
            if (CursorCol >= Cols) NewLine();
        }
        Raise();
    }

    /// <summary>Advance to column 0 of the next row, scrolling if needed.</summary>
    public void NewLine()
    {
        CursorCol = 0;
        if (CursorRow < WindowBottom) CursorRow++;
        else ScrollUp();
        Raise();
    }

    /// <summary>Write text then start a new line.</summary>
    public void WriteLine(string text, CellAttr? attr = null)
    {
        Write(text, attr);
        NewLine();
    }

    /// <summary>Scroll the text window up one line, keeping content on the last row.</summary>
    public void ScrollUp()
    {
        for (int r = WindowTop; r < WindowBottom; r++)
            for (int c = 0; c < Cols; c++)
            {
                Chars[r, c] = Chars[r + 1, c];
                Attrs[r, c] = Attrs[r + 1, c];
            }
        for (int c = 0; c < Cols; c++)
        {
            Chars[WindowBottom, c] = ' ';
            Attrs[WindowBottom, c] = CellAttr.None;
        }
        CursorRow = WindowBottom;
        Raise();
    }

    /// <summary>Scroll the text window down one line (used by the paged quick reference).</summary>
    public void ScrollDown()
    {
        for (int r = WindowBottom; r > WindowTop; r--)
            for (int c = 0; c < Cols; c++)
            {
                Chars[r, c] = Chars[r - 1, c];
                Attrs[r, c] = Attrs[r - 1, c];
            }
        for (int c = 0; c < Cols; c++)
        {
            Chars[WindowTop, c] = ' ';
            Attrs[WindowTop, c] = CellAttr.None;
        }
        CursorRow = WindowTop;
        Raise();
    }

    /// <summary>Fill a rectangular block of cells, used for the Alt-I dashboard panel.</summary>
    public void FillBlock(int row, int col, int height, int width, char fill, CellAttr attr)
    {
        for (int r = row; r < row + height && r < Rows; r++)
            for (int c = col; c < col + width && c < Cols; c++)
            {
                if (r < 0 || c < 0) continue;
                Chars[r, c] = fill;
                Attrs[r, c] = attr;
            }
        Raise();
    }

    /// <summary>Draw a box outline with double-line characters and a title.</summary>
    public void DrawBox(int row, int col, int height, int width, string title, CellAttr attr)
    {
        if (height < 2 || width < 2) return;
        for (int c = col + 1; c < col + width - 1 && c < Cols; c++)
        {
            Put(row, c, "\u2550", attr);
            Put(row + height - 1, c, "\u2550", attr);
        }
        for (int r = row + 1; r < row + height - 1 && r < Rows; r++)
        {
            Put(r, col, "\u2551", attr);
            Put(r, col + width - 1, "\u2551", attr);
        }
        Put(row, col, "\u2554", attr);
        Put(row, col + width - 1, "\u2557", attr);
        Put(row + height - 1, col, "\u255A", attr);
        Put(row + height - 1, col + width - 1, "\u255D", attr);
        if (title.Length > 0)
            Put(row, col + 2, " " + title + " ", attr);
    }

    /// <summary>The text of one row with trailing spaces trimmed.</summary>
    public string GetRowText(int row)
    {
        if (row < 0 || row >= Rows) return "";
        var sb = new StringBuilder(Cols);
        for (int c = 0; c < Cols; c++) sb.Append(Chars[row, c]);
        return sb.ToString().TrimEnd();
    }

    /// <summary>Snapshot of the whole grid as text lines - captured into a session for restore.</summary>
    public string[] Snapshot()
    {
        var lines = new string[Rows];
        for (int r = 0; r < Rows; r++)
        {
            var sb = new StringBuilder(Cols);
            for (int c = 0; c < Cols; c++) sb.Append(Chars[r, c]);
            lines[r] = sb.ToString();
        }
        return lines;
    }

    private void ClampCursor()
    {
        if (CursorRow < WindowTop) CursorRow = WindowTop;
        if (CursorRow > WindowBottom) CursorRow = WindowBottom;
        if (CursorCol < 0) CursorCol = 0;
        if (CursorCol >= Cols) CursorCol = Cols - 1;
    }

    private void Raise() => Changed?.Invoke();
}
