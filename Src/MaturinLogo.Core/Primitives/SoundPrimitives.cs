using System.Globalization;
using System.Text.RegularExpressions;
using MaturinLogo.Core.Interpreter;
using MaturinLogo.Core.Values;

namespace MaturinLogo.Core.Primitives;

/// <summary>
/// Sound: the <c>TOOT</c> command and the switch that turns it off. The browser produces the tone
/// (a square wave on the Web Audio clock, which is what makes it sound like an 8-bit machine);
/// Core decides the pitch, the length, and whether a tone is allowed at all.
/// </summary>
/// <remarks>
/// Coleco ADAM SmartLOGO and Apple Logo II both had TOOT, and it is the command that turns a
/// drawing program into a game: a coin picked up, a wall hit, a level cleared. It is deliberately
/// the only thing in the language that leaves the screen.
/// <para>
/// A tone does not block the interpreter. The browser queues tones on its own audio clock, so
/// <c>TOOT 440 0.2 TOOT 550 0.2</c> is two notes in sequence however fast Logo runs the two
/// commands - and a program that escapes its loop can be broken with ESC without a half-second
/// wait in the middle of it.
/// </para>
/// </remarks>
public static class SoundPrimitives
{
    /// <summary>Seconds a TOOT lasts when the child does not say.</summary>
    public const double DefaultDuration = 0.15;

    /// <summary>
    /// Lowest pitch accepted, in hertz. Below this a square wave is a click rather than a note, and
    /// a child who typed 0 would hear nothing and assume the command was broken.
    /// </summary>
    public const double MinHertz = 16;

    /// <summary>Highest pitch accepted. Past this the tone is a whistle at best and inaudible at worst.</summary>
    public const double MaxHertz = 8000;

    public const double MinDuration = 0.02;
    public const double MaxDuration = 5;

    public static void Register(PrimitiveTable t)
    {
        // Variadic rather than two inputs, so `TOOT 440` is legal and `TOOT 440 0.5` is too. A
        // third number is refused below rather than ignored: a child who typed three meant
        // something by the third.
        t.Add("TOOT", -1,
            "Play a note. The pitch is hertz or a note name like \"A4; the length in seconds is optional.",
            "TOOT 440 0.3",
            CommandFamily.System, async (e, a, _) =>
            {
                if (a.Count == 0)
                    throw new LogoException(LogoErrorCode.NotEnoughInputs,
                        ErrorText.NotEnoughInputs("TOOT", 1, 0, e.Mode),
                        "Try TOOT 440, or TOOT \"A4 0.5 for a half-second note.", subject: "TOOT");

                if (a.Count > 2)
                    throw new LogoException(LogoErrorCode.TooManyInputs,
                        "TOOT takes a pitch and an optional length",
                        "Try TOOT 440 or TOOT 440 0.5 - nothing more.", subject: "TOOT");

                double hertz = Pitch(a[0], "TOOT", e.Mode);
                double seconds = a.Count > 1 ? Seconds(a[1], "TOOT", e.Mode) : DefaultDuration;

                try { await e.PlaySoundAsync(hertz, seconds); }
                catch { /* A sound failure must not abort the child's whole program. */ }
                return LogoValue.None;
            }, variadic: true);

        t.Add("SETSOUND", 1, "Turn Logo's sound on or off.", "SETSOUND FALSE",
            CommandFamily.System, (e, a, _) =>
            {
                e.SoundEnabled = Args.Bool(a, 0, "SETSOUND", e.Mode);
                if (!e.SoundEnabled) e.StopSounds();     // switching off must silence what is already queued
                return Args.Done();
            });

        t.Add("SOUND", 0, "Report whether Logo makes a sound.", "PRINT SOUND",
            CommandFamily.System, (e, a, _) => Args.Flag(e.SoundEnabled), isReporter: true);
    }

    /// <summary>
    /// A pitch from a number of hertz, or from a note name such as <c>A4</c>, <c>C#5</c> or
    /// <c>Bb3</c>. Note names exist because a child who plays an instrument already knows them, and
    /// because <c>TOOT "C4 TOOT "E4 TOOT "G4</c> reads as the chord it sounds like.
    /// </summary>
    public static double Pitch(LogoValue value, string name, LogoMode mode)
    {
        if (value.Kind == LogoKind.Number)
            return InRange(value.Number, name, mode);

        string word = Args.Word([value], 0, name, mode).Trim();
        if (LogoPitch.TryToHertz(word, out double hertz)) return InRange(hertz, name, mode);

        // A word that parses as a plain number, so TOOT :P works when P holds "440".
        if (double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            return InRange(parsed, name, mode);

        throw new LogoException(LogoErrorCode.BadNumber,
            $"{word} is not a pitch I know",
            "Use hertz like TOOT 440, or a note name like TOOT \"A4.", subject: name);
    }

    private static double InRange(double hertz, string name, LogoMode mode)
    {
        if (hertz < MinHertz || hertz > MaxHertz)
            throw new LogoException(LogoErrorCode.OutOfRange,
                ErrorText.OutOfRange(name, mode),
                $"A pitch has to be between {MinHertz:F0} and {MaxHertz:F0} hertz - that is about "
                + $"{LogoPitch.NoteName(hertz < MinHertz ? MinHertz : MaxHertz)} at the "
                + (hertz < MinHertz ? "low" : "high") + " end.",
                subject: name);
        return hertz;
    }

    public static double Seconds(LogoValue value, string name, LogoMode mode)
    {
        double seconds = Args.Number([value], 0, name, mode);
        if (seconds < MinDuration || seconds > MaxDuration)
            throw new LogoException(LogoErrorCode.OutOfRange,
                ErrorText.OutOfRange(name, mode),
                $"A note lasts from {MinDuration} to {MaxDuration} seconds. Try TOOT 440 0.3.",
                subject: name);
        return seconds;
    }
}

/// <summary>
/// Note names to hertz, at equal temperament with A4 = 440. Kept separate from the primitives
/// because the table below is the kind of thing a test wants to check note by note.
/// </summary>
public static partial class LogoPitch
{
    private static readonly Dictionary<string, int> Semitones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["C"] = 0, ["D"] = 2, ["E"] = 4, ["F"] = 5, ["G"] = 7, ["A"] = 9, ["B"] = 11,
    };

    /// <summary>A4, the pitch a piano tuner and a Web Audio demo both agree on.</summary>
    public const double ConcertA = 440;

    [GeneratedRegex(@"^([A-G])([#B♯♭]{0,2})([0-8])?$", RegexOptions.IgnoreCase)]
    private static partial Regex NotePattern();

    /// <summary>
    /// True when <paramref name="note"/> is a note name, reporting its hertz. The octave may be
    /// left off, which means the fourth - middle C - because that is where a beginner starts.
    /// </summary>
    /// <remarks>
    /// The accidental is matched upper case as well, because Logo folds every word to capitals
    /// before a primitive ever sees it: a child who types <c>TOOT "Bb3</c> hands us <c>BB3</c>, and
    /// a flat that stopped being a flat in the reader would be a note a piano does not have.
    /// </remarks>
    public static bool TryToHertz(string note, out double hertz)
    {
        hertz = 0;
        Match match = NotePattern().Match(note.Trim());
        if (!match.Success) return false;

        if (!Semitones.TryGetValue(match.Groups[1].Value, out int base_)) return false;

        string accidentals = match.Groups[2].Value.ToUpperInvariant();
        if (accidentals.Length > 0 && accidentals.Distinct().Count() > 1) return false;  // "#b" is nonsense

        // One semitone per sign, so "B" and "bb" are a flat and a double flat.
        int offset = accidentals switch
        {
            "" => 0,
            "#" or "♯" => 1,
            "B" or "♭" => -1,
            "##" or "♯♯" => 2,                       // F##4 is G4, and a child may spell it that way
            "BB" or "♭♭" => -2,
            _ => 0,
        };

        // An octave of 4 is MIDI 60, so the number a child types is the number a piano says.
        int octave = match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : 4;
        hertz = ToHertz((octave + 1) * 12 + base_ + offset);
        return true;
    }

    /// <summary>Equal temperament: every semitone is the twelfth root of two.</summary>
    public static double ToHertz(int midi) => ConcertA * Math.Pow(2, (midi - 69) / 12.0);

    public static int ToMidi(double hertz) => (int)Math.Round(69 + 12 * Math.Log2(hertz / ConcertA));

    /// <summary>The nearest note's name, for error messages that have to say what a number means.</summary>
    public static string NoteName(double hertz)
    {
        int midi = ToMidi(hertz);
        string[] names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

        // The octave is spelled as a whole number, because this string is handed back to
        // TryToHertz by the tests - and a decimal point formatted by some other culture would
        // not parse, which is a bug that only appears on somebody else's machine.
        return names[((midi % 12) + 12) % 12] + (midi / 12 - 1).ToString(CultureInfo.InvariantCulture);
    }
}
