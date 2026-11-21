namespace RPM.Core.Lyrics;

/// <summary>
/// Which piece of a <see cref="LyricScore"/> is shown while its words are lit one by one. The Android phone
/// app's <c>KaraokeLines</c> has the same rules, so the two screens move on at the same moments.
///
/// <para>An LRC file says when each line starts and never when it ends. Where the words of a line have been
/// measured off the record, the record says more than the stamps: when the singer really starts a line and
/// when the last word is done. These rules use that to decide which line is up.</para>
///
/// <para>Pure, and in Core, so tests can check the rules without a window.</para>
/// </summary>
public static class KaraokeLines
{
    /// <summary>How long before its first measured word a line takes over, in seconds. It is about when the
    /// light starts to arrive on the word (see <see cref="KaraokeLight.GlyphSoftness"/>).</summary>
    public const double Lead = 0.35;

    /// <summary>The longest a line waits past its own stamp for its first measured word, in seconds.</summary>
    public const double WaitLongest = 4.0;

    /// <summary>The earliest before its own stamp a line takes over for its first measured word, in
    /// seconds.</summary>
    public const double Earliest = 1.0;

    /// <summary>
    /// The piece to show at <paramref name="seconds"/>, or -1 before the first line. This is not always
    /// the piece the stamps point at (<see cref="LyricScore.IndexAt"/>).
    ///
    /// <para><b>A line is held while it is still sung.</b> It is normal for a singer to hold the last word of
    /// a line into the start of the next one, and for a stamp to be written a beat early. Where the line's
    /// words were measured, the line stays up until its last word is done, unless the next line's first
    /// word has already started. Two lines cannot be sung at once.</para>
    ///
    /// <para><b>A line waits for its voice.</b> A line measured to start well after its stamp would stand
    /// there unlit until the singer reached it. So the piece before it stays up until <see cref="Lead"/>
    /// before the line's first measured word, but never longer than <see cref="WaitLongest"/> past the
    /// stamp. A measurement that went wrong cannot hold the lyrics back for longer than that.</para>
    ///
    /// <para><b>A line takes over as its voice arrives.</b> A stamp is often a little after the voice starts
    /// the line, by up to half a second in quick singing. Waiting for the stamp would show the line late,
    /// with its first words already sung and lit all at once. So the next line takes over <see cref="Lead"/>
    /// before its first measured word. It does so no earlier than <see cref="Earliest"/> before its stamp,
    /// and not while the line before is still on its last measured word, unless the next line's first word
    /// has already started.</para>
    ///
    /// <para>A line whose words were shared out by length, and not measured, has no measured start or end.
    /// It goes by the stamps.</para>
    /// </summary>
    /// <param name="score">The score being shown.</param>
    /// <param name="seconds">The record's time.</param>
    /// <param name="measured">The measured words of a piece, by its place in the score. It answers null
    /// for a piece whose words were not measured. It is not asked about a rest.</param>
    public static int Held(LyricScore score, double seconds, Func<int, IReadOnlyList<WordSpan>?> measured)
    {
        ArgumentNullException.ThrowIfNull(score);
        ArgumentNullException.ThrowIfNull(measured);

        var at = score.IndexAt(seconds);
        var stays = HeldBack(score, at, seconds, measured);
        if (stays != at)
            return stays;

        var next = at + 1;
        if (next >= score.Parts.Count || Words(score, next, measured) is not { } coming)
            return at;

        var starts = coming[0].From;
        if (seconds < starts - Lead || seconds < score.Parts[next].From - Earliest)
            return at;

        if (at >= 0 && Words(score, at, measured) is { } singing && singing[^1].To > seconds && starts > seconds)
            return at;

        return next;
    }

    /// <summary><see cref="Held"/> without moving on early: the piece before <paramref name="at"/> while it
    /// is still sung, or while the words of <paramref name="at"/> are still well ahead.</summary>
    private static int HeldBack(LyricScore score, int at, double seconds, Func<int, IReadOnlyList<WordSpan>?> measured)
    {
        if (at < 0)
            return at;

        // Where the line really begins: its first measured word, or its stamp where it was not measured.
        double? measuredStart = Words(score, at, measured) is { } words ? words[0].From : null;
        var stamp = score.Parts[at].From;

        var last = at - 1;
        if (last >= 0 && Words(score, last, measured) is { } previous && seconds < previous[^1].To)
            return seconds < (measuredStart ?? stamp) ? last : at;

        if (measuredStart is { } starts && seconds < starts - Lead && seconds < stamp + WaitLongest)
            return last;

        return at;
    }

    /// <summary>The measured words of a piece, or null for a rest and for a piece with none.</summary>
    private static IReadOnlyList<WordSpan>? Words(LyricScore score, int part, Func<int, IReadOnlyList<WordSpan>?> measured)
        => !score.Parts[part].IsRest && measured(part) is { Count: > 0 } words ? words : null;
}
