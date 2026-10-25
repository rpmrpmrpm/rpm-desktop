namespace RPM.Core.Lyrics;

/// <summary>One word's time span in the recording, in seconds from the start of the track.</summary>
public readonly record struct MeasuredWord(double From, double To);

/// <summary>One lyric line as it was measured off the record: its place in the sheet, its time and text
/// there, and its words in reading order.</summary>
public sealed record MeasuredLine(int Index, double Seconds, string Text, IReadOnlyList<MeasuredWord> Words);

/// <summary>One line of a sheet, by its own time and text, as the cloud names the sheet an offset was
/// worked out for.</summary>
public readonly record struct SheetLine(int Index, double Seconds, string Text);

/// <summary>
/// What the cloud keeps of a recording's lyrics: the measured <paramref name="Lines"/>, and how far the
/// sheet is off the record (<paramref name="Offset"/>), worked out for the sheet in <paramref
/// name="OffsetLines"/>.
/// </summary>
/// <param name="Offset">How much later than its sheet the recording is sung, in seconds. Null when it was
/// never worked out; nought when the sheet is in time.</param>
public sealed record MeasuredAnswer(
    IReadOnlyList<MeasuredLine> Lines, double? Offset, IReadOnlyList<SheetLine> OffsetLines)
{
    /// <summary>Nothing measured: a track nobody has measured, or a read that failed.</summary>
    public static MeasuredAnswer None { get; } = new([], null, []);
}

/// <summary>
/// The word timing that a desktop or the transcoder measured from a track's recording, read back from the
/// cloud (<c>cloud/lyric-timing/{rpmId}</c>). It is the Android TV app's <c>MeasuredLyrics</c>.
///
/// <para>A measured line is used only when it is the same line on this machine's sheet: the same text, the
/// same time, and the same number of words (<see cref="ForLine"/>). Every other line keeps the estimate
/// from <see cref="LyricTiming.Sweep"/>, or this machine's own measurement.</para>
///
/// <para>Pure, and in Core, so tests can check the rules without a network.</para>
/// </summary>
public static class MeasuredLyrics
{
    /// <summary>How far apart two times may be and still belong to the same line, in seconds. LRC stamps
    /// are kept to the hundredth.</summary>
    private const double SameMoment = 0.05;

    /// <summary>How far before its stamp a line's last measured word may end and still be taken, in
    /// seconds.</summary>
    private const double Stray = 1.0;

    /// <summary>The measured words of the line <paramref name="text"/> at <paramref name="seconds"/>, as
    /// spans of <paramref name="text"/>, or null when no measured line matches.</summary>
    public static IReadOnlyList<WordSpan>? ForLine(IReadOnlyList<MeasuredLine> measured, string text, double seconds)
    {
        MeasuredLine? line = null;
        foreach (var candidate in measured)
            if (candidate.Text == text && Math.Abs(candidate.Seconds - seconds) < SameMoment)
            {
                line = candidate;
                break;
            }

        if (line is null)
            return null;

        // The same split into words as everything else, so the spans index the same string.
        var shape = LyricTiming.Sweep(text, seconds, double.PositiveInfinity);
        if (shape.Count == 0 || shape.Count != line.Words.Count)
            return null;

        var spans = new WordSpan[shape.Count];
        for (var i = 0; i < spans.Length; i++)
        {
            var word = line.Words[i];
            spans[i] = shape[i] with { From = word.From, To = Math.Max(word.From, word.To) };
        }
        return spans;
    }

    /// <summary>
    /// The measured words of a line, where they fall in the line's own time, or null.
    ///
    /// <para>None may end well before the line starts (<see cref="Stray"/> of slack), and the first may not
    /// start after the next line does (<paramref name="next"/>). Words placed outside that are the
    /// aligner's mistake. Swept by them, the line stood grey for as long as it was sung, so it is swept by
    /// length instead.</para>
    /// </summary>
    public static IReadOnlyList<WordSpan>? Heard(
        IReadOnlyList<MeasuredLine> measured, string text, double seconds, double next)
    {
        if (ForLine(measured, text, seconds) is not { Count: > 0 } words)
            return null;
        if (words[^1].To < seconds - Stray)
            return null;
        if (double.IsFinite(next) && words[0].From > next)
            return null;
        return words;
    }

    /// <summary>Whether <paramref name="measured"/> covers most of <paramref name="sheet"/>'s sung lines,
    /// three in four. Where it does, no new measurement is asked for.</summary>
    public static bool Covers(IReadOnlyList<MeasuredLine> measured, LyricSheet sheet)
    {
        int sung = 0, placed = 0;
        foreach (var line in sheet.Lines)
        {
            if (LyricSheet.IsWordless(line.Text))
                continue;
            sung++;
            if (ForLine(measured, line.Text, line.Seconds) is not null)
                placed++;
        }
        return sung == 0 || placed * 4 >= sung * 3;
    }

    /// <summary>
    /// The offset of <paramref name="answer"/> for <paramref name="sheet"/>: its seconds where it was worked
    /// out for this very sheet, else nought.
    ///
    /// <para>"This very sheet" means every line it was worked out on is in <paramref name="sheet"/>, with the
    /// same text and time. An offset worked out for another sheet of the song would move this one
    /// wrong.</para>
    /// </summary>
    public static double OffsetFor(MeasuredAnswer answer, LyricSheet sheet)
    {
        if (answer.Offset is not { } offset || offset == 0 || answer.OffsetLines.Count == 0)
            return 0;

        foreach (var named in answer.OffsetLines)
        {
            var found = false;
            foreach (var line in sheet.Lines)
                if (line.Text == named.Text && Math.Abs(line.Seconds - named.Seconds) < SameMoment)
                {
                    found = true;
                    break;
                }
            if (!found)
                return 0;
        }
        return offset;
    }

    /// <summary>
    /// <paramref name="lines"/> with their sheet times moved by <paramref name="offset"/>, as <see
    /// cref="LyricSheet.Shifted"/> moves the sheet, so they still match its lines (<see cref="ForLine"/>).
    /// Their words are times in the record already, and they stay where they are.
    /// </summary>
    public static IReadOnlyList<MeasuredLine> Shifted(IReadOnlyList<MeasuredLine> lines, double offset)
        => offset == 0 ? lines : [.. lines.Select(l => l with { Seconds = Math.Max(0, l.Seconds + offset) })];
}
