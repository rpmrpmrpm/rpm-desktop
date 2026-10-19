namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// How much of the lyric sheet is showing, for a playhead sitting between two lines.
///
/// <para>
/// An LRC file says when each line starts, but never how long it lasts. So the only thing that can be known
/// about a wait is its length from one line to the next. A long enough wait is taken to be a break, not a
/// short pause. The words go, because a line that nobody is singing, left lit for the length of a solo,
/// makes the sheet look as if it lost its place rather than as if the song paused.
/// </para>
///
/// <para>
/// There are two lengths, because the two waits differ. Mid-song, the words on screen are still worth
/// reading. The line just sung may be long, and a slow singer needs room, so the sheet waits
/// <see cref="AfterBreak"/> before it removes anything. Before the first line there is nothing worth
/// keeping on screen. A sheet of dim, unsung lines over an intro only says that the song has not started.
/// That goes at <see cref="AfterIntro"/>, and the record's own name takes its place (see
/// PegasusChrome.LyricIntro).
/// </para>
///
/// <para>
/// This is a function of the playhead only, with no state and no fade left running from an earlier
/// position. So a seek lands correctly: dropped into the middle of a break, the sheet is already away, and
/// placed on a verse, it is already up. This also makes the rule testable without a window, which is why it
/// is here and not inside the chrome.
/// </para>
/// </summary>
internal static class LyricHush
{
    /// <summary>How long the wait before the first line must be before the sheet is taken away. It is
    /// short, because an unstarted sheet is not worth looking at, and the record's name in the middle of
    /// the screen takes its place.</summary>
    internal const double AfterIntro = 3.0;

    /// <summary>How long a wait between two lines must be before the same happens. It is much longer than
    /// the intro value. The line on screen may still be sung after its start time, and an LRC file gives no
    /// durations, so the sheet cannot tell. It is given the benefit of the doubt for a long time. Only a
    /// wait that no singing could fill counts as a break.</summary>
    internal const double AfterBreak = 20.0;

    /// <summary>How long the words take to go, once a wait has proved to be a break.</summary>
    internal const double Out = 1.0;

    /// <summary>How long the words take to come back. It is measured back from the start of the next line,
    /// so the words are in front of the reader before the singer starts. It is longer than
    /// <see cref="Out"/>, because arriving late is the worse error: a reader who missed the first half of a
    /// line cannot go back for it.</summary>
    internal const double In = 2.0;

    /// <summary>
    /// The sheet's own opacity, nought to one, before easing.
    /// </summary>
    /// <param name="pos">The playhead, in seconds.</param>
    /// <param name="from">When the current wait began:
    /// the line just sung, or the start of the record before the first one.</param>
    /// <param name="until">When the next line lands, or <see cref="double.PositiveInfinity"/> after the
    /// last one. That is the true value for a wait that never ends. The arithmetic below reads it as "not
    /// soon" without a special case, so the words stay away through an outro.</param>
    /// <param name="intro">Whether this is the wait before the first line, which is held to the shorter
    /// <see cref="AfterIntro"/>.</param>
    internal static double Show(double pos, double from, double until, bool intro)
    {
        // A wait short enough to be part of the singing. This is the common case and needs one comparison.
        // Most records have no gap longer than a short pause between lines.
        if (until - from <= (intro ? AfterIntro : AfterBreak))
            return 1;

        // Going is the fade out after the line just sung. Coming is the fade in before the next one. The
        // larger value wins, so the two never fight over the middle of a long break. There both are zero
        // and the sheet is hidden.
        var going = 1 - Math.Clamp((pos - from) / Out, 0, 1);
        var coming = 1 - Math.Clamp((until - pos) / In, 0, 1);
        return Math.Max(going, coming);
    }
}
