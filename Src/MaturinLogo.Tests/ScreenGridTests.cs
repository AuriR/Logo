using System.Text.RegularExpressions;
using MaturinLogo.Core.Machine;
using MaturinLogo.Core.Screen;

namespace MaturinLogo.Core.Tests;

/// <summary>
/// The grid is emitted as raw HTML and injected with MarkupString, so these tests are the only
/// thing standing between a Logo program and the page's DOM. They check escaping, and that the
/// grid is always exactly 40 cells by 24 rows no matter what is on the screen.
/// </summary>
public class ScreenGridTests
{
    private static string Render(TextScreen screen, int cursorRow = -1, int cursorCol = -1, bool cursor = false) =>
        ScreenGrid.Build(screen, cursorRow, cursorCol, cursor);

    [Fact]
    public void An_empty_screen_is_24_rows_of_40_cells()
    {
        string html = Render(new TextScreen());

        Assert.Equal(TextScreen.Rows, Count(html, "<div class=\"ml-row\">"));
        Assert.Equal(TextScreen.Rows * TextScreen.Cols, Count(html, "<span class=\"ml-cell"));
    }

    [Fact]
    public void Html_metacharacters_printed_by_logo_cannot_become_markup()
    {
        var screen = new TextScreen();

        // This is what a program can put on screen: PRINT "|<script>alert(1)</script>" and the
        // reader keeps the punctuation as ordinary characters.
        screen.PutLine(0, "<script>alert(1)</script>");
        screen.PutLine(1, "A & B \"C\" 'D'");
        screen.PutLine(2, "1 > 0");

        string html = Render(screen);

        // Each character lives in its own span, so the entities appear separately rather than as a
        // whole escaped phrase. The structural test below is what proves no raw "<" survives.
        // "<script>" must not survive as a tag. The bare word alert(1) is harmless and does appear,
        // because only the bracket characters are markup; the structural test proves nothing else
        // can open a tag.
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;", html, StringComparison.Ordinal);
        Assert.Contains("&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&amp;", html, StringComparison.Ordinal);
        Assert.Contains("&quot;", html, StringComparison.Ordinal);
        Assert.Contains("&#39;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void An_attribute_cannot_be_injected_through_a_character()
    {
        var screen = new TextScreen();
        screen.PutLine(0, "\" onmouseover=\"evil()");

        string html = Render(screen);

        // The quote is escaped, so it cannot close the class attribute and start a new one.
        Assert.DoesNotContain("onmouseover", html, StringComparison.Ordinal);
        Assert.DoesNotContain("\" \"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Blank_cells_are_written_as_non_breaking_space_so_a_row_keeps_its_width()
    {
        var screen = new TextScreen();
        screen.PutLine(0, "A");

        string html = Render(screen);

        // Every one of the 960 cells is emitted, blanks included, or the grid would not be a grid.
        Assert.Equal(TextScreen.Rows * TextScreen.Cols, Count(html, "<span class=\"ml-cell"));

        // Row 0 holds one letter; the other 959 cells are blank and written as &nbsp;.
        Assert.Equal(TextScreen.Rows * TextScreen.Cols - 1, Count(html, "&nbsp;"));
    }

    [Fact]
    public void Reverse_and_dim_attributes_reach_the_markup_as_classes()
    {
        var screen = new TextScreen();
        screen.PutLine(0, "ERR", CellAttr.Reverse);
        screen.PutLine(1, "hint", CellAttr.Dim);

        string html = Render(screen);

        Assert.Contains("ml-cell a-r", html, StringComparison.Ordinal);
        Assert.Contains("ml-cell a-dim", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cursor_is_a_class_on_one_cell_and_moves_when_told_to()
    {
        var screen = new TextScreen();
        screen.PutLine(3, "X");

        string html = Render(screen, 3, 5, cursor: true);

        Assert.Equal(1, Count(html, "cursor\""));
        Assert.Contains("ml-cell cursor\"", html, StringComparison.Ordinal);

        string hidden = Render(screen, 3, 5, cursor: false);
        Assert.Equal(0, Count(hidden, " cursor"));
    }

    [Fact]
    public void A_cursor_outside_the_grid_adds_nothing()
    {
        string html = Render(new TextScreen(), cursorRow: 99, cursorCol: 99, cursor: true);
        Assert.Equal(0, Count(html, " cursor"));
    }

    [Fact]
    public void The_html_is_balanced_so_blazor_cannot_leak_structure()
    {
        var screen = new TextScreen();
        screen.PutLine(0, "><div></span>");

        string html = Render(screen);

        Assert.Equal(TextScreen.Rows, Count(html, "<div"));
        Assert.Equal(TextScreen.Rows, Count(html, "</div>"));
        Assert.Equal(TextScreen.Rows * TextScreen.Cols, Count(html, "<span"));
        Assert.Equal(TextScreen.Rows * TextScreen.Cols, Count(html, "</span>"));
    }

    [Theory]
    [InlineData("PRINT \"HELLO")]
    [InlineData("PRINT \"|<b>bold|\"")]
    [InlineData("TO SQUARE :S REPEAT 4 [FD :S RT 90] END")]
    [InlineData("MAKE \"LIST [1 2 3]")]
    [InlineData("PRINT [1 2 [3 <4>]]")]
    public async Task Anything_a_program_can_print_stays_a_balanced_fixed_grid(string source)
    {
        var engine = new MaturinLogo.Core.Interpreter.Engine();
        await engine.RunAsync(source);

        string html = Render(engine.Screen);

        Assert.Equal(TextScreen.Rows, Count(html, "<div class=\"ml-row\">"));
        Assert.Equal(TextScreen.Rows * TextScreen.Cols, Count(html, "<span class=\"ml-cell"));
        Assert.Equal(0, Count(html, "<script"));

        // The structure is exactly 24 rows of 40 cells, each holding plain text. That shape is what
        // the CSS depends on to keep the grid 40 columns wide, so it is asserted directly rather
        // than trusting the escape function alone.
        string[] rows = html.Split("<div class=\"ml-row\">", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(TextScreen.Rows, rows.Length);

        foreach (string row in rows)
        {
            Assert.Equal(TextScreen.Cols, Count(row, "<span class=\"ml-cell"));

            // Each cell is "<span class=\"ml-cell...>TEXT</span>"; nothing after the tag's own
            // closing bracket may be another tag.
            foreach (Match cell in CellText.Matches(row))
                Assert.DoesNotContain("<", cell.Groups[1].Value, StringComparison.Ordinal);
        }
    }

    /// <summary>Capture the text of each cell, between the span tag and its closing tag.</summary>
    private static readonly Regex CellText = new("<span class=\"ml-cell[^\"]*\">(.*?)</span>", RegexOptions.Singleline);

    private static int Count(string haystack, string needle)
    {
        int count = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
