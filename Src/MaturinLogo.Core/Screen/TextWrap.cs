namespace MaturinLogo.Core.Screen;

/// <summary>
/// Word wrapping for the 40-column grid. Everything the machine prints on the emulated screen -
/// the quick reference, the INFO dashboard, Logo's own messages - has to fit inside it, and a line
/// cut off mid-word reads as a fault rather than as a message.
/// </summary>
/// <remarks>
/// One implementation rather than three: the reference, the listener and the overlay painters all
/// wrapped (or truncated) their own way, which is how a help entry ending in an ellipsis and an
/// INFO row running off the right edge survived at the same time.
/// </remarks>
public static class TextWrap
{
    /// <summary>The width every caller wraps to: the screen is 40 columns and nothing scrolls sideways.</summary>
    public const int Width = TextScreen.Cols;

    /// <summary>
    /// Break <paramref name="text"/> into rows no longer than <paramref name="width"/>, preferring
    /// to split at <paramref name="separator"/> so a word is never cut in half. A single piece that
    /// is longer than the whole width is hard-broken - an unbreakable overflow is worse than an
    /// ugly split, and this is what a very long procedure name in the reference does.
    /// </summary>
    public static List<string> Wrap(string text, int width = Width, string separator = " ")
    {
        if (width < 1) width = 1;

        var lines = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            lines.Add("");
            return lines;
        }

        var current = new System.Text.StringBuilder();

        foreach (string piece in text.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            string word = piece;

            while (word.Length > width)
            {
                if (current.Length > 0) { lines.Add(current.ToString()); current.Clear(); }
                lines.Add(word.Substring(0, width));
                word = word.Substring(width);
            }

            if (current.Length == 0)
            {
                current.Append(word);
            }
            else if (current.Length + separator.Length + word.Length <= width)
            {
                current.Append(separator).Append(word);
            }
            else
            {
                lines.Add(current.ToString());
                current.Clear().Append(word);
            }
        }

        if (current.Length > 0) lines.Add(current.ToString());
        if (lines.Count == 0) lines.Add("");
        return lines;
    }

    /// <summary>
    /// Wrap and then hang the continuation lines under the first one, which is what a help entry
    /// needs: the description starts in its own column, and the overflow must line up with it
    /// rather than hard against the left edge.
    /// </summary>
    public static List<string> WrapIndented(string text, int width, string first, string continuation)
    {
        int room = Math.Max(1, width - Math.Max(first.Length, continuation.Length));
        var rows = Wrap(text, room);
        var result = new List<string>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
            result.Add((i == 0 ? first : continuation) + rows[i]);
        return result;
    }

    /// <summary>Pad or cut to exactly <paramref name="width"/> columns.</summary>
    public static string Column(string text, int width) =>
        text.Length >= width ? text.Substring(0, width) : text + new string(' ', width - text.Length);

    /// <summary>Centre within <paramref name="width"/>, truncating rather than overflowing.</summary>
    public static string Center(string text, int width = Width) => text.Length >= width
        ? text.Substring(0, width)
        : new string(' ', (width - text.Length) / 2) + text;

    /// <summary>
    /// Lay short items out across a fixed width, two spaces apart, starting a new row when the current
    /// one is full.
    /// </summary>
    /// <remarks>
    /// For a reference table of small entries - a palette, the turtle's shapes - where one item per row
    /// would be six rows and a fixed number of columns would either waste half the grid or truncate a
    /// name. Greedy packing uses the whole width and keeps every entry whole: an item too long for the
    /// remaining row goes on the next one rather than being cut, because a name chopped in half is
    /// worse than a short row.
    /// </remarks>
    public static List<string> Pack(IEnumerable<string> entries, int width = Width)
    {
        var rows = new List<string>();
        var row = new System.Text.StringBuilder();

        foreach (string entry in entries)
        {
            if (row.Length > 0 && row.Length + 2 + entry.Length > width)
            {
                rows.Add(row.ToString());
                row.Clear();
            }

            if (row.Length > 0) row.Append("  ");
            row.Append(entry);
        }

        if (row.Length > 0) rows.Add(row.ToString());
        return rows;
    }
}
