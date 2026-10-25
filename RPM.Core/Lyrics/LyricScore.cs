namespace RPM.Core.Lyrics;

/// <summary>
/// One piece of a <see cref="LyricScore"/>: a line of words, or a rest where nothing is sung.
/// </summary>
/// <param name="Id">Where the piece stands in the score, from nought.</param>
/// <param name="Section">The stretch of singing the piece belongs to. A rest carries the number of the
/// stretch it closes. So the words before a rest and the rest are shown together, and the words after it
/// are not.</param>
/// <param name="Line">The sheet line the piece is drawn on. For a rest that a wordless line marks, it is
/// that line. It is -1 for a rest that no line marks (a long wait, and the outro).</param>
/// <param name="IsRest">Whether nothing is sung in this piece.</param>
/// <param name="From">When the piece starts, in seconds.</param>
/// <param name="Until">When the next piece starts, or <see cref="double.PositiveInfinity"/> after the
/// last.</param>
public readonly record struct LyricPart(int Id, int Section, int Line, bool IsRest, double From, double Until);

/// <summary>
/// A lyric sheet cut into what is sung and what is waited through. It is the iPhone's and the Android TV
/// app's <c>LyricScore</c>, so a sheet is cut the same way on every screen.
///
/// <para>An LRC file says when each line starts but not how long it lasts. A break in the singing is
/// either a line with no words, or a wait that no singing could fill (<see cref="Rest"/> seconds or more).
/// Both become a rest between two stretches of words. A screen asks the score which piece the playhead is
/// in, which stretch that is, and how far through a rest it is. Only the stretch being sung is shown, so
/// the next verse does not stand on screen through a solo.</para>
///
/// <para>Pure, and in Core, so tests can check the rule without a window.</para>
/// </summary>
public sealed class LyricScore
{
    /// <summary>How long a wait between two lines must be to count as a break and not a breath, in
    /// seconds. The line on screen may still be sung after its start, and an LRC file gives no durations,
    /// so only a wait that no singing could fill counts.</summary>
    public const double Rest = 20.0;

    /// <summary>How long the last line before a rest that no line marks stays up, counted from its own
    /// time, in seconds. It is less than half of <see cref="Rest"/>, so it can never fill a whole
    /// rest.</summary>
    public const double Sung = 8.0;

    /// <summary>A score with no pieces, for a sheet that has no lines.</summary>
    public static LyricScore Empty { get; } = new([]);

    private LyricScore(IReadOnlyList<LyricPart> parts) => Parts = parts;

    /// <summary>The pieces, in time order.</summary>
    public IReadOnlyList<LyricPart> Parts { get; }

    /// <summary>
    /// Which piece the playhead is in, or -1 before the first line. The intro is not a piece of its own:
    /// a screen counts in to the first line there.
    ///
    /// <para>A binary search, because a screen asks once per frame.</para>
    /// </summary>
    public int IndexAt(double seconds)
    {
        int low = 0, high = Parts.Count - 1, found = -1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (Parts[mid].From <= seconds)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }
        return found;
    }

    /// <summary>The score of <paramref name="sheet"/>.</summary>
    public static LyricScore Of(LyricSheet sheet)
    {
        var lines = sheet.Lines;
        if (lines.Count == 0)
            return Empty;

        var parts = new List<LyricPart>(lines.Count + 4);
        var section = 0;
        double? lastSang = null;

        for (var at = 0; at < lines.Count; at++)
        {
            var line = lines[at];
            var next = at + 1 < lines.Count ? lines[at + 1].Seconds : double.PositiveInfinity;

            // A line with nothing to sing is a rest that the writer marked. It closes the stretch before it.
            if (LyricSheet.IsWordless(line.Text))
            {
                parts.Add(new LyricPart(parts.Count, section, at, true, line.Seconds, next));
                section++;
                lastSang = null;
                continue;
            }

            // A wait that no line marks: the rest closes the stretch before it, and this line opens the next.
            if (lastSang is { } sang && line.Seconds - sang >= Rest)
            {
                parts.Add(new LyricPart(parts.Count, section, -1, true, sang + Sung, line.Seconds));
                section++;
            }

            parts.Add(new LyricPart(parts.Count, section, at, false, line.Seconds, next));
            lastSang = line.Seconds;
        }

        // The outro: a rest that never ends, after the last line that has words.
        if (lastSang is { } last)
            parts.Add(new LyricPart(parts.Count, section, -1, true, last + Sung, double.PositiveInfinity));

        // Each piece runs until the next piece starts. A line's Until was set to the next LINE, and a rest
        // that no line marks may stand between them.
        for (var i = 0; i + 1 < parts.Count; i++)
            if (parts[i].Until > parts[i + 1].From)
                parts[i] = parts[i] with { Until = parts[i + 1].From };

        return new LyricScore(parts);
    }

    /// <summary>How far through a rest the playhead is, from nought to one. A rest with no end never
    /// fills.</summary>
    public static double Through(double seconds, double from, double until)
    {
        var run = until - from;
        if (!double.IsFinite(run) || run <= 0)
            return 0;
        return Math.Clamp((seconds - from) / run, 0, 1);
    }

    /// <summary>How strong a line is drawn, by how many pieces it stands from the one being sung: 1,
    /// 0.42, 0.24 and 0.12, then nothing. The same steps on the iPhone and the TV.</summary>
    public static double Fade(int distance) => distance switch
    {
        0 => 1.0,
        1 => 0.42,
        2 => 0.24,
        3 => 0.12,
        _ => 0.0,
    };
}
