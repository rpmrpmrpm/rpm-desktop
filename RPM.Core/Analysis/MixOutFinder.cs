namespace RPM.Core.Analysis;

/// <summary>
/// Where to leave a long record.
///
/// <para>A set that plays every record to its outro gets slow on long records: seven minutes of one
/// track is time that could have been two tracks. After <see cref="LongRecordSeconds"/> the DJ stops
/// waiting for the outro and looks for a place to leave. Many records have a stretch where the
/// singing stops and the drums continue: the breakdown, the middle eight, the instrumental after
/// the second chorus. A changeover belongs there, because two records can share drums but cannot
/// share voices.</para>
///
/// <para>The search uses the bands that the analyser has already measured, so it costs nothing and
/// works on older analyses. <see cref="TrackAnalysis.VocalCurve"/> is the middle band's share of the
/// whole. It is high where something holds the middle of the record, usually a singer.
/// <see cref="TrackAnalysis.BassCurve"/> shows whether the beat is still there. A stretch that is
/// quiet in the first curve and active in the second has drums and no voice.</para>
///
/// <para>Both curves are judged against the record's own normal level, not an absolute level. A
/// record with a voice on every bar has nowhere to go, and the result is null: the caller plays it
/// to its outro. Cutting it short at an arbitrary three minutes would be worse than playing it
/// in full.</para>
/// </summary>
public static class MixOutFinder
{
    /// <summary>How long a record has to be before a place to leave it is searched for. Three
    /// minutes is the usual length of a record, and by then most tracks have said what they have
    /// to say.</summary>
    public const double LongRecordSeconds = 180;

    /// <summary>How quiet the voice has to be across the whole changeover, as a share of what this
    /// record normally carries in the middle. A stretch has to be clearly emptier than the record's
    /// normal level, not only a quiet bar in a chorus.</summary>
    public const double QuietVoice = 0.6;

    /// <summary>The voice must also stay below this share of its normal level everywhere in the
    /// stretch. Without this limit, a window across the edge of a break could pass on its average
    /// while half of it is still the last chorus.</summary>
    private const double LoudestVoice = 0.75;

    /// <summary>How much of the record's usual bottom end must still be present. A break with no
    /// drums is not a place to bring in another record: there is nothing to mix it over, and the
    /// mix would be two intros.</summary>
    private const double BeatAlive = 0.6;

    /// <summary>
    /// The first stretch after <see cref="LongRecordSeconds"/> that is long enough for the
    /// changeover, with no singing and the beat still present. Null if the record is short, if the
    /// bands were never measured, or if it has no such stretch.
    /// </summary>
    /// <param name="holdSeconds">How long the changeover out of this record will run. All of it
    /// must fall inside the quiet stretch, or the singing returns during the fade.</param>
    /// <returns>A four-bar line, so the changeover starts on a phrase boundary of the record.</returns>
    public static double? Find(TrackAnalysis analysis, double holdSeconds)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        var voice = analysis.VocalCurve;
        var bass = analysis.BassCurve;
        if (voice.Count == 0 || bass.Count == 0)
            return null;                        // analysed before the bands were measured

        double step = EnergyProfile.StepSeconds;

        // Never later than the outro: leaving a record there happens anyway, and this method exists
        // to leave earlier.
        double end = analysis.OutroStartSeconds > 0
            ? Math.Min(analysis.OutroStartSeconds, analysis.AudibleEndSeconds)
            : analysis.AudibleEndSeconds;

        // The stretch has to hold the whole changeover, plus a phrase for the snap to a four-bar
        // line. The point found here moves forward to the next four-bar line, which is up to four
        // bars later. With a window only one bar wider, the fade could begin inside the quiet stretch
        // and end after it, in the singing that this method avoids.
        double slack = analysis.BarSeconds > 0
            ? TrackAnalysis.PhraseBars * analysis.BarSeconds
            : 2 * step;
        double window = Math.Max(step, holdSeconds) + slack;

        if (end - LongRecordSeconds < window)
            return null;                        // too short to have a place to leave

        double voiceBody = Median(voice);
        double bassBody = Median(bass);
        if (voiceBody <= 0 || bassBody <= 0)
            return null;                        // nothing to judge a stretch against

        int width = Math.Max(1, (int)Math.Round(window / step));
        int first = (int)Math.Ceiling(LongRecordSeconds / step);
        int last = Math.Min(voice.Count, bass.Count) - width;
        last = Math.Min(last, (int)(end / step) - width);

        for (int at = first; at <= last; at++)
        {
            double sumVoice = 0, loudest = 0, sumBass = 0;
            for (int i = at; i < at + width; i++)
            {
                sumVoice += voice[i];
                loudest = Math.Max(loudest, voice[i]);
                sumBass += bass[i];
            }

            if (sumVoice / width > voiceBody * QuietVoice)
                continue;                       // somebody is singing over it
            if (loudest > voiceBody * LoudestVoice)
                continue;                       // or comes back part way through
            if (sumBass / width < bassBody * BeatAlive)
                continue;                       // the drums have gone too, so there is nothing to mix over

            // Take the earliest stretch, not the emptiest. The aim is to leave a long record; waiting
            // for the perfect break at five minutes defeats that.
            //
            // The point moves forward to a four-bar line, not back, because the stretch before it is
            // the singing that just ended. A bar too early would put the end of a chorus over the
            // incoming record.
            double found = analysis.PhraseAtOrAfter(at * step);
            return found < end ? found : null;
        }

        return null;
    }

    /// <summary>
    /// How many phrases back a blend looks for a quieter place. Eight: two phrases of changeover and
    /// six of searching, which at 120 BPM is about a minute of record.
    ///
    /// <para>The search is limited because it is only a preference, not a goal. Without a limit it
    /// would find the emptiest bar of the record every time. On most records that is the breakdown
    /// at the halfway point, which is a good place to overlap two records but would throw away a
    /// minute and a half of music. A record is left early only if a better place is close.</para>
    /// </summary>
    public const int PhrasesBack = 8;

    /// <summary>
    /// The latest phrase line at or before <paramref name="latestStart"/> with
    /// <paramref name="holdSeconds"/> of nobody singing after it and the beat still going, or null
    /// when the record has no better place than the one already planned.
    ///
    /// <para>The search goes backwards and returns the LATEST stretch that qualifies, not the
    /// quietest. <see cref="Find"/> does the opposite: it goes forwards and takes the earliest. The
    /// two answer different questions. <see cref="Find"/> leaves a record that has gone on too long,
    /// so any exit is better than the outro. This method already has a place to blend and only asks
    /// whether a better one exists a few phrases earlier. The answer must be near, because every
    /// phrase it moves back is a phrase of the record that is not played.</para>
    ///
    /// <para>It uses the same two curves and thresholds as <see cref="Find"/>, judged against the
    /// record's own normal level. Two records can share drums but cannot share voices.</para>
    /// </summary>
    /// <param name="holdSeconds">How long the blend will run. All of it must fall in the quiet
    /// stretch, or the singing returns during the blend.</param>
    public static double? QuietPhraseBefore(
        TrackAnalysis analysis, double latestStart, double holdSeconds, int phrasesBack = PhrasesBack)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        var voice = analysis.VocalCurve;
        var bass = analysis.BassCurve;
        if (voice.Count == 0 || bass.Count == 0 || holdSeconds <= 0 || phrasesBack < 1)
            return null;                        // analysed before the bands were measured

        double phrase = analysis.PhraseSeconds;
        if (phrase <= 0)
            return null;                        // no tempo, so no phrases to step by

        double voiceBody = Median(voice);
        double bassBody = Median(bass);
        if (voiceBody <= 0 || bassBody <= 0)
            return null;                        // nothing to judge a stretch against

        double step = EnergyProfile.StepSeconds;
        int width = Math.Max(1, (int)Math.Round(holdSeconds / step));
        int readings = Math.Min(voice.Count, bass.Count);

        // Never go back into this record's own intro. An intro is made to be mixed OVER another
        // record, so it is not a place to start a blend out of this record.
        double floorAt = Math.Max(0, analysis.IntroEndSeconds);

        double line = analysis.MeasuredPhraseAtOrBefore(latestStart);

        for (int back = 0; back < phrasesBack; back++, line -= phrase)
        {
            if (line < floorAt - 1e-6)
                break;

            int at = (int)Math.Round(line / step);
            if (at < 0 || at + width > readings)
                continue;

            double sumVoice = 0, loudest = 0, sumBass = 0;
            for (int i = at; i < at + width; i++)
            {
                sumVoice += voice[i];
                loudest = Math.Max(loudest, voice[i]);
                sumBass += bass[i];
            }

            if (sumVoice / width > voiceBody * QuietVoice)
                continue;                       // somebody is singing over it
            if (loudest > voiceBody * LoudestVoice)
                continue;                       // or comes back part way through
            if (sumBass / width < bassBody * BeatAlive)
                continue;                       // the drums have gone too, so there is nothing to mix over

            return line;
        }

        return null;
    }

    /// <summary>The median reading of a curve: what the record normally does. Unlike the average, one
    /// silent or one loud stretch does not move it.</summary>
    private static double Median(IReadOnlyList<float> curve)
    {
        var sorted = new float[curve.Count];
        for (int i = 0; i < curve.Count; i++)
            sorted[i] = curve[i];
        Array.Sort(sorted);
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2.0;
    }
}
