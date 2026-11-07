using MaturinLogo.Core.Interpreter;
using MaturinLogo.Core.Values;

namespace MaturinLogo.Core.Turtle;

/// <summary>
/// Turtle geometry. Positions are in Apple II hi-res units with the origin at the centre and y
/// increasing upward, so FD 100 looks exactly as it did on a real Apple II. Every move appends a
/// segment to the scene rather than touching pixels, which is what lets a picture be saved as
/// source and restored perfectly at any canvas size.
/// </summary>
public sealed class TurtleGraphics
{
    private readonly GraphicsScene _scene;
    private readonly TurtleState _turtle;

    public TurtleGraphics(GraphicsScene scene, TurtleState turtle)
    {
        _scene = scene;
        _turtle = turtle;
    }

    /// <summary>Move forward (or backward when <paramref name="distance"/> is negative), drawing when the pen is down.</summary>
    public void Move(double distance)
    {
        double radians = _turtle.Heading * Math.PI / 180.0;
        double x1 = _turtle.X, y1 = _turtle.Y;
        _turtle.X += distance * Math.Sin(radians);
        _turtle.Y += distance * Math.Cos(radians);

        if (_turtle.PenDown && distance != 0)
            _scene.Add(new DrawSegment(x1, y1, _turtle.X, _turtle.Y, _turtle.PenColor, _turtle.PenSize));
    }

    public void Rotate(double degrees) => _turtle.Heading = Normalize(_turtle.Heading + degrees);

    /// <summary>
    /// Sweep an arc of <paramref name="sweepDegrees"/> with the centre <paramref name="radius"/>
    /// units from the turtle: positive sweeps turn right, negative turn left.
    /// <para>
    /// Each slice is drawn as the true chord of the circle - turn half the slice, walk
    /// 2r*sin(delta/2), turn the other half. A chord's endpoints lie exactly on the circle and
    /// its direction bisects the turn, so the turtle stays on the arc with no drift to correct.
    /// </para>
    /// </summary>
    public void Arc(double sweepDegrees, double radius)
    {
        if (radius <= 0 || sweepDegrees == 0) return;

        int slices = Math.Clamp((int)(Math.Abs(sweepDegrees) / 4), 4, 180);
        double delta = sweepDegrees / slices;
        double chord = 2 * radius * Math.Sin(Math.Abs(delta) * Math.PI / 360.0);

        for (int i = 0; i < slices; i++)
        {
            Rotate(delta / 2);
            Move(chord);
            Rotate(delta / 2);
        }
    }

    /// <summary>Draw a full circle of <paramref name="radius"/> centred ahead of the turtle, as Apple Logo's CIRCLE does.</summary>
    public void Circle(double radius)
    {
        if (radius <= 0) return;
        Arc(360, radius);
    }

    /// <summary>Jump to an absolute position; the pen does not draw while moving.</summary>
    public void SetXY(double x, double y)
    {
        _turtle.X = x;
        _turtle.Y = y;
    }

    public void Home() => _turtle.Home();

    public void SetHeading(double degrees) => _turtle.Heading = Normalize(degrees);

    /// <summary>Draw text at the turtle, the way Apple Logo's LABEL worked.</summary>
    public void Label(string text)
    {
        if (text.Length == 0) return;
        _scene.Add(new DrawLabel(text, _turtle.X, _turtle.Y, _turtle.PenColor));
    }

    /// <summary>Clear every drawn item without moving the turtle.</summary>
    public void Clean() => _scene.Items.Clear();

    /// <summary>Clear the picture and send the turtle home.</summary>
    public void Clear()
    {
        _scene.Items.Clear();
        _turtle.Home();
    }

    /// <summary>Replay the scene from a saved item list, used when a session is loaded.</summary>
    public void Restore(IEnumerable<object> items, int background)
    {
        _scene.Items.Clear();
        foreach (var item in items) _scene.Items.Add(item);
        _scene.Background = background;
    }

    /// <summary>Heading in Logo's 0-359 range, keeping fractional headings for smooth spirals.</summary>
    private static double Normalize(double degrees)
    {
        double value = degrees % 360.0;
        if (value < 0) value += 360.0;
        return value;
    }
}
