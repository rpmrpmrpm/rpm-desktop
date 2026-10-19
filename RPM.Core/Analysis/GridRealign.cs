namespace RPM.Core.Analysis;

/// <summary>What happened to a grid that came from another machine's copy of the track.</summary>
/// <param name="Analysis">The reading to use: shifted onto this file, or with its grid removed when
/// the two copies are not close enough to share one grid.</param>
/// <param name="ShiftSeconds">How far the grid had to move.</param>
/// <param name="Agreement">How well the shifted beats landed on this file's own onsets, 0..1.</param>
/// <param name="Kept">False when the grid was discarded and only the parts of the reading that do
/// not depend on the start of the file were kept.</param>
public readonly record struct Realigned(
    TrackAnalysis Analysis, double ShiftSeconds, double Agreement, bool Kept);

/// <summary>
/// Puts another machine's beat grid onto this machine's copy of the track.
///
/// <para><b>Why this is needed.</b> Every other key in the pool identifies one copy: the same
/// bytes, the same decoded samples, or the same id in the tags. A reading found under one of those
/// describes the file being analysed and can be used as it is. The fingerprint name identifies the
/// <em>music</em>, which is all that two people who ripped the same CD have in common, and two rips
/// of one CD do not start at the same sample. Different drives read at different offsets, and one
/// ripper keeps the pre-gap while another drops it. A grid copied unchanged is then wrong by that
/// difference for the whole track: correct tempo, wrong "one". That is worse than no grid, because
/// with no grid the DJ blends and it sounds deliberate.</para>
///
/// <para><b>What is borrowed and what is calculated again.</b> Two values need a model and are
/// copied without loss: which octave the tempo is in, and which beat of the bar is the one. Neither
/// depends on where the file starts. The position of the beats does depend on it, so it is
/// calculated here from this copy's own audio, which is cheap: the edges show roughly how far apart
/// the two copies are, and the onsets give the exact offset.</para>
///
/// <para>Pure, so it can be read and tested without a decoder. The caller supplies the onset
/// envelope, which every caller already has.</para>
/// </summary>
public static class GridRealign
{
    /// <summary>
    /// How much the lengths of the two copies may differ before the grid is refused, as a share.
    ///
    /// <para>This guards against a different master, as opposed to a different rip. A remaster, a
    /// vinyl transfer or a PAL speed-up runs at a slightly different rate. A grid from one, placed on
    /// the other, is right at the start and a beat out by the end. A single offset cannot fix this,
    /// and the onset check does not catch it, because the early beats land correctly. Half a percent
    /// over four minutes is a beat and a half of drift, which is already too much.</para>
    /// </summary>
    public const double MaxSpeedDifference = 0.005;

    /// <summary>How far the search looks for the true phase, in beats on either side of the position
    /// that the edges suggest. Half a beat covers every possible phase. A larger range would find a
    /// different beat and treat it as the same one.</summary>
    private const double SearchBeats = 0.5;

    /// <summary>The step of the phase search, as a share of a beat. A beat at 128 BPM is 469 ms, so a
    /// fiftieth of a beat is 9 ms, which is less than a listener hears as a mix being early.</summary>
    private const double SearchStep = 0.02;

    /// <summary>Below this value, the shifted beats do not land on this track's onsets, and the grid
    /// does not describe it. The value is low on purpose: the test is "the beats are on the beat",
    /// not "this is a good track".</summary>
    private const double MinAgreement = 0.15;

    /// <summary>
    /// Fits <paramref name="borrowed"/> onto this copy.
    /// </summary>
    /// <param name="borrowed">The reading from the pool, matched by fingerprint.</param>
    /// <param name="ourDuration">The length of this copy in seconds.</param>
    /// <param name="ourEdges">Where the audio of this copy starts and stops, if measured.</param>
    /// <param name="onsets">This copy's onset envelope.</param>
    /// <param name="onsetRate">Onset envelope frames per second.</param>
    public static Realigned Fit(
        TrackAnalysis borrowed,
        double ourDuration,
        AudioSpan ourEdges,
        ReadOnlySpan<float> onsets,
        double onsetRate)
    {
        ArgumentNullException.ThrowIfNull(borrowed);

        // Nothing to fit. A reading with no grid does not depend on the file start, so it is used
        // as it is.
        if (borrowed.BeatTimes.Count < 8 || borrowed.Bpm <= 0)
            return new Realigned(borrowed with { DurationSeconds = ourDuration }, 0, 0, Kept: false);

        // A different master, not a different rip. Nothing here can fix that, and using the grid
        // would give a drift of a beat and a half across the track.
        if (borrowed.DurationSeconds > 0 && ourDuration > 0)
        {
            double difference = Math.Abs(borrowed.DurationSeconds - ourDuration) / ourDuration;
            if (difference > MaxSpeedDifference)
                return new Realigned(Without(borrowed, ourDuration), 0, 0, Kept: false);
        }

        // The offset between the two copies according to the edges. Both edges come from a
        // threshold, so the estimate is good to a few tens of milliseconds. The onset search below
        // refines it.
        double guess = 0;
        if (ourEdges.IsKnown && borrowed.HasEdges)
            guess = ourEdges.StartSeconds - borrowed.AudioStartSeconds;

        double beat = 60.0 / borrowed.Bpm;
        var shifted = Search(borrowed.BeatTimes, guess, beat, onsets, onsetRate, out double agreement);

        // The beats do not land on anything in this track. The two files may be different recordings
        // with similar fingerprints, or the grid may not belong to this copy.
        if (agreement < MinAgreement)
            return new Realigned(Without(borrowed, ourDuration), 0, agreement, Kept: false);

        var moved = Move(borrowed, shifted, ourDuration);
        return new Realigned(moved, shifted, agreement, Kept: true);
    }

    /// <summary>
    /// The offset that puts the most beats on the strongest onsets, searched around the value that
    /// the edges suggested.
    /// </summary>
    private static double Search(
        IReadOnlyList<double> beats,
        double guess,
        double beatSeconds,
        ReadOnlySpan<float> onsets,
        double onsetRate,
        out double agreement)
    {
        agreement = 0;
        if (onsets.Length == 0 || onsetRate <= 0 || beatSeconds <= 0)
            return guess;

        // The average onset value of this track. The score then means "stronger than usual under a
        // beat" and not "this track is loud".
        double total = 0;
        for (int i = 0; i < onsets.Length; i++)
            total += onsets[i];
        double average = total / onsets.Length;
        if (average <= 0)
            return guess;

        double best = guess, bestScore = 0;

        for (double step = -SearchBeats; step <= SearchBeats; step += SearchStep)
        {
            double offset = guess + step * beatSeconds;
            double score = 0;
            int counted = 0;

            foreach (double at in beats)
            {
                int frame = (int)Math.Round((at + offset) * onsetRate);
                if (frame < 0 || frame >= onsets.Length)
                    continue;

                score += onsets[frame];
                counted++;
            }

            if (counted < 8)
                continue;

            double mean = score / counted / average;
            if (mean > bestScore)
            {
                bestScore = mean;
                best = offset;
            }
        }

        // A grid on the onsets scores well above the average frame. A grid anywhere else scores
        // about the average. The scale is set so that "twice the average frame" is certainty.
        agreement = Math.Clamp((bestScore - 1) / 1.0, 0, 1);
        return best;
    }

    /// <summary>The same reading with every time in it moved, and limited to the length of this
    /// copy.</summary>
    private static TrackAnalysis Move(TrackAnalysis borrowed, double shift, double ourDuration)
    {
        double end = ourDuration > 0 ? ourDuration : borrowed.DurationSeconds;

        var beats = new List<double>(borrowed.BeatTimes.Count);
        foreach (double at in borrowed.BeatTimes)
        {
            double moved = at + shift;
            if (moved >= 0 && moved <= end)
                beats.Add(moved);
        }

        // The bar phase is counted from the first beat in the list, so removing beats from the
        // front rotates it. The index is corrected by the number of beats that were removed.
        int dropped = borrowed.BeatTimes.Count - beats.Count > 0
            ? CountBefore(borrowed.BeatTimes, shift)
            : 0;

        int firstDownbeat = borrowed.BeatsPerBar > 0
            ? ((borrowed.FirstDownbeat - dropped) % borrowed.BeatsPerBar + borrowed.BeatsPerBar) % borrowed.BeatsPerBar
            : borrowed.FirstDownbeat;

        return borrowed with
        {
            DurationSeconds = end,
            BeatTimes = beats,
            FirstDownbeat = firstDownbeat,

            // These values describe the copy that the reading was made from, not this copy. This
            // machine measures its own (the decode is already done), so they are cleared and not
            // copied.
            AudioStartSeconds = 0,
            AudioEndSeconds = 0,
            BeatFirstSeconds = 0,
            BeatLastSeconds = 0,

            IntroEndSeconds = Math.Clamp(borrowed.IntroEndSeconds + shift, 0, end),
            OutroStartSeconds = Math.Clamp(borrowed.OutroStartSeconds + shift, 0, end),
            DropSeconds = [.. borrowed.DropSeconds.Select(d => d + shift).Where(d => d >= 0 && d <= end)],
        };
    }

    /// <summary>How many beats fell off the front when the grid moved.</summary>
    private static int CountBefore(IReadOnlyList<double> beats, double shift)
    {
        int before = 0;
        foreach (double at in beats)
            if (at + shift < 0)
                before++;
        return before;
    }

    /// <summary>
    /// The reading with everything that depends on where the file starts removed.
    ///
    /// <para>What is left is still useful, which is why this is not a plain refusal. The key, the
    /// energy and how hard the track hits are properties of the music and do not change between
    /// copies. A DJ blends a track with no grid anyway, and now knows its key.</para>
    /// </summary>
    private static TrackAnalysis Without(TrackAnalysis borrowed, double ourDuration) =>
        borrowed with
        {
            DurationSeconds = ourDuration > 0 ? ourDuration : borrowed.DurationSeconds,
            Bpm = 0,
            BpmConfidence = 0,
            BeatTimes = [],
            FirstDownbeat = 0,
            IntroEndSeconds = 0,
            OutroStartSeconds = 0,
            AudioStartSeconds = 0,
            AudioEndSeconds = 0,
            BeatFirstSeconds = 0,
            BeatLastSeconds = 0,
            DropSeconds = [],
            Phrases = default,
        };
}
