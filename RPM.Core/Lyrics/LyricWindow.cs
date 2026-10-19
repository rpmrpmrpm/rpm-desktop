namespace RPM.Core.Lyrics;

/// <summary>
/// The part of a recording where one lyric line is searched for: where it starts, and where it ends.
///
/// <para><b>A type with two points in time, not a start and a length.</b> Forced alignment must use
/// every frame it is given, and a window with spare room is not left empty. The path spreads into the
/// spare room, so the end of the line is placed there too. If the end were worked out as a length, with
/// slack added at each side afterwards, it could drift past the next line's start time. The last words
/// of most lines would then be placed after the next line had already appeared. With two points in time
/// the end can be compared directly with the next line's start time.</para>
///
/// <para>Pure, and in Core. Tests can check the rule without a window. The benchmark that scores a set
/// of weights uses the same code, so it measures the same part of the recording as the app. A benchmark
/// with its own copy of this arithmetic would agree with the app only until one of them changed.</para>
/// </summary>
/// <param name="Opens">Where the window starts, in the recording's own clock. Never negative.</param>
/// <param name="Closes">Where the window ends. See <see cref="Overrun"/>.</param>
public readonly record struct LyricWindow(double Opens, double Closes)
{
    /// <summary>How much of the recording the window covers.</summary>
    public double Length => Closes - Opens;

    /// <summary>How far before a line's start time the window opens, in seconds. LRC start times are
    /// written by hand, and a singer often starts a little before the stamp. Without this slack the
    /// first word could fall outside the window.</summary>
    public const double Slack = 0.6;

    /// <summary>
    /// How far a line may run past the next line's start time before the window ends, in seconds.
    ///
    /// <para><b>The window ends at the next line's own start time, plus this small margin.</b> The
    /// sheet's start times are the best bound available. They are written by hand and are about the
    /// right moment, although they can be a beat out. Without a bound at the next start time, the last
    /// words of most lines would be placed after the next line had appeared, and they would never be
    /// lit.</para>
    ///
    /// <para>The margin is for a word that lands right on the start time. Without it, the edge of the
    /// window would cut that word in half.</para>
    ///
    /// <para>A singer may hold the last word of a line into the start of the next one, and this clips
    /// that hold. That is the safer error. A held note that finishes a moment early is hardly noticed.
    /// A line that runs well past the next start time leaves its words unlit.</para>
    /// </summary>
    public const double Overrun = 0.25;

    /// <summary>
    /// The slowest singing rate assumed, in characters per second. It sets how much audio a line may
    /// be given when the sheet gives no end point.
    ///
    /// <para>The usual end point is <see cref="Overrun"/> past the next line's start time. This cap
    /// covers the lines that have no useful neighbour: the last line of a sheet, and the line before a
    /// long instrumental. An LRC file gives no durations, so such a line appears to last thirty seconds.
    /// Without the cap the model would get thirty seconds of guitar, and it could match the line's
    /// words to the solo. With the cap the placement stays close to the real singing.</para>
    ///
    /// <para>This is deliberately slower than the cap in <c>LyricTiming</c>, which has the opposite job.
    /// That cap makes the sweep finish. This one must not cut the singing short, so it allows more time
    /// for each character.</para>
    /// </summary>
    public const double SlowestChars = 5.0;

    /// <summary>The shortest window, in seconds. It applies when the next line starts almost at once.
    /// Below this there is too little audio for the model to place anything, so the window is widened
    /// to this length instead.</summary>
    public const double LeastWindow = 1.0;

    /// <summary>
    /// The window for a line, given its own start time and the next line's.
    /// </summary>
    /// <param name="text">The line as it will be handed to the model. Its length sets the end point
    /// when the line has no useful neighbour. See <see cref="SlowestChars"/>.</param>
    /// <param name="from">When the line starts, by the sheet.</param>
    /// <param name="until">When the next line starts, or <see cref="double.PositiveInfinity"/> for a
    /// line that has none, such as the last line of a sheet.</param>
    public static LyricWindow For(string? text, double from, double until)
    {
        var opens = Math.Max(0, from - Slack);

        // Both limits are points in the recording. The window ends at the earlier one. Nothing is added
        // to the result afterwards.
        var byNeighbour = double.IsNaN(until) || until >= double.MaxValue / 2
            ? double.MaxValue
            : until + Overrun;
        var byWords = from + (text?.Length ?? 0) / SlowestChars;

        return new LyricWindow(opens, Math.Max(opens + LeastWindow, Math.Min(byWords, byNeighbour)));
    }
}
