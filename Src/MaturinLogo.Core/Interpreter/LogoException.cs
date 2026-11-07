namespace MaturinLogo.Core.Interpreter;

/// <summary>Machine-readable failure tags, used by tests and by the EASY-mode message table.</summary>
public enum LogoErrorCode
{
    NotAProcedure,
    NotEnoughInputs,
    TooManyInputs,
    NoValue,
    BadInputType,
    BadNumber,
    BadList,
    BadItemIndex,
    UnterminatedDefinition,
    AlreadyDefined,
    NotDefined,
    OutputIgnored,
    NoOutput,
    DivisionByZero,
    OutOfRange,
    RunawayLoop,
    BreakRequested,
    ReadOnly,
    Internal,
}

/// <summary>
/// A Logo-level failure (not a crash): the message is what the child sees on screen. Carries an
/// optional hint line, the offending name, and the source line when known.
/// </summary>
public sealed class LogoException : Exception
{
    public LogoErrorCode Code { get; }

    /// <summary>Extra guidance shown only in EASY mode.</summary>
    public string? Hint { get; }

    /// <summary>1-based source line, when the throw site knows it.</summary>
    public int Line { get; }

    /// <summary>The offending name, used for "did you mean" suggestions.</summary>
    public string? Subject { get; }

    public LogoException(LogoErrorCode code, string message, string? hint = null, int line = 0, string? subject = null)
        : base(message)
    {
        Code = code;
        Hint = hint;
        Line = line;
        Subject = subject;
    }

    /// <summary>Return a copy carrying a line number, for throw sites that learn it later.</summary>
    public LogoException WithLine(int line) =>
        new(Code, Message, Hint, line, Subject);
}
