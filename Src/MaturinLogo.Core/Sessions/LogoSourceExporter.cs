using System.Text;
using MaturinLogo.Core.Interpreter;
using MaturinLogo.Core.Primitives;
using MaturinLogo.Core.Values;

namespace MaturinLogo.Core.Sessions;

/// <summary>
/// Turns a session into real Logo source a child can keep, print, or hand to a friend. The export
/// is written so that feeding it straight back into Logo reproduces the session: a header comment,
/// then every procedure, then the child's own source, then the commands that would rebuild the
/// picture.
/// </summary>
public static class LogoSourceExporter
{
    public static string Export(LogoSession session)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"; {LogoInfo.VersionText} - {session.Name}");
        sb.AppendLine($"; SAVED {session.UpdatedAt:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine($"; MODE {session.Mode} - {session.LinesWritten} LINES WRITTEN");
        sb.AppendLine($"; {LogoInfo.Homage}");
        sb.AppendLine();

        if (session.Procedures.Count > 0)
        {
            sb.AppendLine("; ---- PROCEDURES THE CHILD WROTE ----");
            foreach (var procedure in session.Procedures)
            {
                sb.AppendLine(procedure.TrimEnd());
                sb.AppendLine();
            }
        }

        if (session.Globals.Count > 0)
        {
            sb.AppendLine("; ---- VARIABLES ----");
            foreach (var global in session.Globals)
                sb.AppendLine($"MAKE \"{global.Name} {global.Text}");
            sb.AppendLine();
        }

        if (session.Source.Trim().Length > 0)
        {
            sb.AppendLine("; ---- PROGRAM ----");
            sb.AppendLine(session.Source.TrimEnd());
            sb.AppendLine();
        }

        // A picture drawn at the listener is not stored as commands, so the export replays the
        // geometry: SETXY/SETH/FD for each segment. That keeps the download genuinely runnable.
        var drawing = RenderDrawing(session);
        if (drawing.Length > 0)
        {
            sb.AppendLine("; ---- THE PICTURE ON SCREEN, REPLAYED ----");
            sb.Append(drawing);
        }

        sb.AppendLine();
        sb.AppendLine($"; END OF {session.Name}");
        return sb.ToString();
    }

    private static StringBuilder RenderDrawing(LogoSession session)
    {
        var sb = new StringBuilder();
        sb.AppendLine("CS");
        if (session.Turtle.PenColor != 2) sb.AppendLine($"SETPC {session.Turtle.PenColor}");

        foreach (var item in session.Scene)
        {
            if (item.Kind == "label")
            {
                sb.AppendLine($"SETXY {Number(item.X1)} {Number(item.Y1)}");
                sb.AppendLine($"LABEL \"{item.Text}");
                continue;
            }

            double dx = item.X2 - item.X1;
            double dy = item.Y2 - item.Y1;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            double heading = Math.Atan2(dx, dy) * 180.0 / Math.PI;

            // Move without drawing, point the right way, then draw the line.
            sb.Append("PU SETXY ").Append(Number(item.X1)).Append(' ').Append(Number(item.Y1))
              .Append(" PD SETH ").Append(heading.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
              .Append(" FD ").Append(distance.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
              .AppendLine();
        }

        sb.AppendLine($"PU SETXY {Number(session.Turtle.X)} {Number(session.Turtle.Y)}");
        sb.AppendLine($"SETH {Number(session.Turtle.Heading)}");
        if (!session.Turtle.PenDown) sb.AppendLine("PU");
        return sb;
    }

    private static string Number(double value) => Printer.FormatNumber(value);

    /// <summary>
    /// Parse Logo source back into a session's parts, used when a child downloads a file and later
    /// brings it back. Procedures are found by TO...END so they can be listed before the program.
    /// </summary>
    public static LogoSession Import(string name, string source, Engine engine)
    {
        var session = new LogoSession { Name = name, Source = source };
        foreach (var block in SplitProcedures(source))
            session.Procedures.Add(block);
        return session;
    }

    /// <summary>Pull each TO...END block out of source text, honouring nesting and comments.</summary>
    public static List<string> SplitProcedures(string source)
    {
        var blocks = new List<string>();
        var current = new StringBuilder();
        int depth = 0;

        foreach (var rawLine in source.Replace("\r\n", "\n").Split('\n'))
        {
            string line = StripComment(rawLine).Trim();
            if (depth == 0 && current.Length == 0 && line.Length == 0) continue;

            current.Append(rawLine).Append('\n');

            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in words)
            {
                if (string.Equals(word, "TO", StringComparison.OrdinalIgnoreCase)) depth++;
                else if (string.Equals(word, "END", StringComparison.OrdinalIgnoreCase) && depth > 0) depth--;
            }

            if (depth == 0 && current.Length > 0 && words.Contains("END", StringComparer.OrdinalIgnoreCase))
            {
                blocks.Add(current.ToString().TrimEnd());
                current.Clear();
            }
        }

        return blocks;
    }

    private static string StripComment(string line)
    {
        int index = line.IndexOf(';');
        return index < 0 ? line : line.Substring(0, index);
    }
}
