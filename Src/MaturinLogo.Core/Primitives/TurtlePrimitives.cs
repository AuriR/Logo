using MaturinLogo.Core.Interpreter;
using MaturinLogo.Core.Turtle;
using MaturinLogo.Core.Values;

namespace MaturinLogo.Core.Primitives;

/// <summary>
/// The turtle commands. Distances and angles are in Apple II hi-res units and Logo degrees, so
/// SQUARE 50 really is 50 pixels on a side and REPEAT 4 [FD 50 RT 90] is the lesson everyone remembers.
/// </summary>
public static class TurtlePrimitives
{
    public static void Register(PrimitiveTable t)
    {
        t.Add("FD", 1, "Move the turtle forward by a number of steps.", "FD 50",
            CommandFamily.Turtle, (e, a, _) => { e.TurtleView.Move(Args.Number(a, 0, "FD", e.Mode)); e.MarkGraphicsDirty(); e.Host.Invalidate(); return Args.Done(); },
            aliases: new[] { "FORWARD" });

        t.Add("BK", 1, "Move the turtle backward without turning around.", "BK 20",
            CommandFamily.Turtle, (e, a, _) => { e.TurtleView.Move(-Args.Number(a, 0, "BK", e.Mode)); e.MarkGraphicsDirty(); e.Host.Invalidate(); return Args.Done(); },
            aliases: new[] { "BACK" });

        t.Add("RT", 1, "Turn the turtle right (clockwise) by that many degrees.", "RT 90",
            CommandFamily.Turtle, (e, a, _) => { e.TurtleView.Rotate(Args.Number(a, 0, "RT", e.Mode)); e.MarkGraphicsDirty(); e.Host.Invalidate(); return Args.Done(); },
            aliases: new[] { "RIGHT" });

        t.Add("LT", 1, "Turn the turtle left (counter-clockwise) by that many degrees.", "LT 90",
            CommandFamily.Turtle, (e, a, _) => { e.TurtleView.Rotate(-Args.Number(a, 0, "LT", e.Mode)); e.MarkGraphicsDirty(); e.Host.Invalidate(); return Args.Done(); },
            aliases: new[] { "LEFT" });

        t.Add("SETH", 1, "Point the turtle at an absolute heading: 0 north, 90 east, 180 south, 270 west.", "SETH 180",
            CommandFamily.Turtle, (e, a, _) => { e.TurtleView.SetHeading(Args.Number(a, 0, "SETH", e.Mode)); e.MarkGraphicsDirty(); e.Host.Invalidate(); return Args.Done(); },
            aliases: new[] { "SETHEADING" });

        t.Add("SETX", 1, "Move the turtle to an x position without drawing.", "SETX 50",
            CommandFamily.Turtle, (e, a, _) => { e.TurtleView.SetXY(Args.Number(a, 0, "SETX", e.Mode), e.Turtle.Y); e.MarkGraphicsDirty(); e.Host.Invalidate(); return Args.Done(); });

        t.Add("SETY", 1, "Move the turtle to a y position without drawing.", "SETY 40",
            CommandFamily.Turtle, (e, a, _) => { e.TurtleView.SetXY(e.Turtle.X, Args.Number(a, 0, "SETY", e.Mode)); e.MarkGraphicsDirty(); e.Host.Invalidate(); return Args.Done(); });

        t.Add("SETXY", 2, "Move the turtle to a position given as two numbers or as a list [x y].", "SETXY 20 30",
            CommandFamily.Turtle, (e, a, _) =>
            {
                if (a.Count == 1 && a[0].Kind == LogoKind.List && a[0].Items.Count == 2)
                {
                    e.TurtleView.SetXY(Args.Number(a[0].Items, 0, "SETXY", e.Mode), Args.Number(a[0].Items, 1, "SETXY", e.Mode));
                }
                else
                {
                    e.TurtleView.SetXY(Args.Number(a, 0, "SETXY", e.Mode), Args.Number(a, 1, "SETXY", e.Mode));
                }
                    e.MarkGraphicsDirty();
                    e.Host.Invalidate();
                return Args.Done();
            });

        t.Add("HOME", 0, "Send the turtle to the middle of the screen, pointing north.", "HOME",
            CommandFamily.Turtle, (e, a, _) => { e.TurtleView.Home(); e.MarkGraphicsDirty(); e.Host.Invalidate(); return Args.Done(); });

        t.Add("POS", 0, "Report the turtle's position as a list [x y].", "PRINT POS",
            CommandFamily.Turtle, (e, a, _) =>
                Args.Result(LogoValue.FromList(new[] { LogoValue.FromNumber(e.Turtle.X), LogoValue.FromNumber(e.Turtle.Y) })),
            isReporter: true);

        t.Add("XCOR", 0, "Report the turtle's x position.", "PRINT XCOR",
            CommandFamily.Turtle, (e, a, _) => Args.Number(e.Turtle.X), isReporter: true);

        t.Add("YCOR", 0, "Report the turtle's y position.", "PRINT YCOR",
            CommandFamily.Turtle, (e, a, _) => Args.Number(e.Turtle.Y), isReporter: true);

        t.Add("HEADING", 0, "Report the direction the turtle is facing, in degrees.", "PRINT HEADING",
            CommandFamily.Turtle, (e, a, _) => Args.Number(e.Turtle.Heading), isReporter: true);

        t.Add("TOWARDS", 2, "Report which way the turtle would have to point to head straight at that spot.", "PRINT TOWARDS 30 40",
            CommandFamily.Turtle, (e, a, _) =>
            {
                double dx = Args.Number(a, 0, "TOWARDS", e.Mode) - e.Turtle.X;
                double dy = Args.Number(a, 1, "TOWARDS", e.Mode) - e.Turtle.Y;
                if (dx == 0 && dy == 0)
                    throw new LogoException(LogoErrorCode.OutOfRange, "TOWARDS can't point at the spot the turtle is already on",
                        "Try a position that is not where the turtle stands.", subject: "TOWARDS");
                double angle = Math.Atan2(dx, dy) * 180.0 / Math.PI;
                if (angle < 0) angle += 360.0;
                return Args.Number(angle);
            }, isReporter: true);

        t.Add("PU", 0, "Lift the pen so the turtle moves without drawing.", "PU FD 100",
            CommandFamily.Turtle, (e, a, _) => { e.Turtle.PenDown = false; return Args.Done(); },
            aliases: new[] { "PENUP" });

        t.Add("PD", 0, "Put the pen down so the turtle draws again.", "PD FD 100",
            CommandFamily.Turtle, (e, a, _) => { e.Turtle.PenDown = true; return Args.Done(); },
            aliases: new[] { "PENDOWN" });

        t.Add("PC", 0, "Report the pen's current colour number.", "PRINT PC",
            CommandFamily.Turtle, (e, a, _) => Args.Number(e.Turtle.PenColor), isReporter: true);

        t.Add("SETPC", 1, "Choose the pen colour, 0 to 15: 0 black, 1 green, 2 white, 3 purple, 4 orange, 5 blue.", "SETPC 4",
            CommandFamily.Turtle, (e, a, _) => { e.Turtle.PenColor = Args.Color(a, 0, "SETPC", e.Mode); return Args.Done(); },
            aliases: new[] { "SETPENCOLOR", "PENCOLOR" });

        t.Add("PENSIZE", 1, "Make the pen thicker, in pixels.", "PENSIZE 3",
            CommandFamily.Turtle, (e, a, _) =>
            {
                double size = Args.Number(a, 0, "PENSIZE", e.Mode);
                if (size < 1 || size > 16)
                    throw new LogoException(LogoErrorCode.OutOfRange, ErrorText.OutOfRange("PENSIZE", e.Mode),
                        "PENSIZE takes a number from 1 to 16.", subject: "PENSIZE");
                e.Turtle.PenSize = size;
                e.MarkGraphicsDirty();
                e.Host.Invalidate();
                return Args.Done();
            }, aliases: new[] { "SETPS" });

        t.Add("ST", 0, "Show the turtle.", "ST",
            CommandFamily.Turtle, (e, a, _) => { e.Turtle.Visible = true; e.MarkGraphicsDirty(); e.Host.Invalidate(); return Args.Done(); },
            aliases: new[] { "SHOWTURTLE" });

        t.Add("HT", 0, "Hide the turtle so only the picture shows.", "HT",
            CommandFamily.Turtle, (e, a, _) => { e.Turtle.Visible = false; e.MarkGraphicsDirty(); e.Host.Invalidate(); return Args.Done(); },
            aliases: new[] { "HIDETURTLE" });

        t.Add("LABEL", 1, "Print text on the graphics page where the turtle stands.", "LABEL \"HELLO",
            CommandFamily.Turtle, (e, a, _) =>
            {
                e.TurtleView.Label(a[0].AsWordText());
                e.Host.Invalidate();
                return Args.Done();
            });

        t.Add("CIRCLE", 1, "Draw a circle with its centre that many steps ahead of the turtle.", "CIRCLE 40",
            CommandFamily.Turtle, (e, a, _) => { e.TurtleView.Circle(Args.Number(a, 0, "CIRCLE", e.Mode)); e.MarkGraphicsDirty(); e.Host.Invalidate(); return Args.Done(); });

        t.Add("ARC", 2, "Draw an arc: the number of degrees to sweep, then the radius.", "ARC 180 40",
            CommandFamily.Turtle, (e, a, _) =>
            {
                e.TurtleView.Arc(Args.Number(a, 0, "ARC", e.Mode), Args.Number(a, 1, "ARC", e.Mode));
                e.MarkGraphicsDirty();
                e.Host.Invalidate();
                return Args.Done();
            });

        t.Add("CLEAN", 0, "Erase the picture but leave the turtle where it is.", "CLEAN",
            CommandFamily.Turtle, (e, a, _) => { e.TurtleView.Clean(); e.MarkGraphicsDirty(); e.Host.Invalidate(); return Args.Done(); });

        t.Add("CS", 0, "Erase the picture and send the turtle home.", "CS",
            CommandFamily.Turtle, (e, a, _) => { e.TurtleView.Clear(); e.MarkGraphicsDirty(); e.Host.Invalidate(); return Args.Done(); },
            aliases: new[] { "CLEARSCREEN" });

        t.Add("SHAPE", 1, "Choose the turtle's shape: " + TurtleShapes.Describe() + ". Type VIEWSHAPES to see them.", "SHAPE 2",
            CommandFamily.Turtle, (e, a, _) =>
            {
                int shape = (int)Math.Round(Args.Number(a, 0, "SHAPE", e.Mode));
                if (shape is < 0 or > TurtleShapes.MaxIndex)
                    throw new LogoException(LogoErrorCode.OutOfRange, ErrorText.OutOfRange("SHAPE", e.Mode),
                        $"SHAPE takes a number from 0 to {TurtleShapes.MaxIndex} - or type VIEWSHAPES.", subject: "SHAPE");
                e.Turtle.Shape = shape;
                e.MarkGraphicsDirty();
                e.Host.Invalidate();
                return Args.Done();
            }, aliases: new[] { "SETSHAPE" });

        // VIEWSHAPES sits beside SHAPE rather than with the other VIEW* command because it answers
        // the same question: a child who just typed SHAPE 4 wants to know what that looks like, and
        // HELP SHAPE is where they are already looking. The chart is a viewer painted straight to the
        // canvas, so nothing enters the scene and their drawing is untouched by looking at this.
        t.Add("VIEWSHAPES", 0,
            "Show every turtle shape with its number, drawn the way the turtle wears it.",
            "VIEWSHAPES",
            CommandFamily.Turtle, (e, a, _) =>
            {
                e.RequestShapes();
                return Args.Done();
            }, aliases: new[] { "VIEWSHAPE", "SHAPES" });
    }
}
