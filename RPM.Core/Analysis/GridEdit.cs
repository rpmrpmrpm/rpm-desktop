namespace RPM.Core.Analysis;

/// <summary>
/// Manual changes to a reading: where the bar lines fall, the tempo the track is counted at, and
/// the two points where a changeover meets the track.
///
/// <para>The grid is not a list of markers that are moved one at a time, although a screen full of
/// beat lines may suggest it. <see cref="BeatTracker.Regularize"/> replaces the tracked beats with
/// one straight line through the whole track, on purpose: a tracker that drops a beat would
/// otherwise rotate the bar phase for everything after it. Every reader later in the pipeline
/// assumes this regularity: <see cref="TrackAnalysis.BarAtOrBefore"/>,
/// <see cref="TrackAnalysis.NextDownbeatAfter"/> and the changeover timed on them all step by a
/// fixed period. So editing the grid means editing the three numbers that define the line, and
/// the hundreds of beats follow from them.</para>
///
/// <para>Every method here returns a new reading marked <see cref="TrackAnalysis.IsEdited"/>, which
/// keeps a person's manual edit out of the analysis cache and out of the shared pool.</para>
///
/// <para>A new grid also changes the values that were calculated against the old grid. Some look
/// like facts about the track but depend on the grid: the phrase is a length in bars and a bar to
/// start counting from, and the drops are times snapped to bar lines. These are calculated again
/// here. The other values either read the grid when asked (<see cref="TrackAnalysis.BpmAt"/>,
/// <see cref="TrackAnalysis.BarIndexAt"/> and <see cref="TrackAnalysis.FirstWholeBar"/> all walk the
/// array, so they are correct as soon as it changes), or cannot be calculated again at all. The
/// second case is <see cref="TrackAnalysis.TempoStability"/>, which is reset to unmeasured instead of
/// guessed.</para>
/// </summary>
public static class GridEdit
{
    /// <summary>
    /// The track counted again from a tempo, a time that is to be a bar line, and the number of beats
    /// in the bar. Every beat and every bar line follows from those three values.
    /// </summary>
    /// <param name="downbeatSeconds">A time that is to be a downbeat. Any downbeat will do, because
    /// the line is periodic and naming one names them all.</param>
    public static TrackAnalysis Regrid(
        TrackAnalysis analysis, double bpm, double downbeatSeconds, int beatsPerBar)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        if (bpm <= 0 || beatsPerBar < 1 || analysis.DurationSeconds <= 0)
            return analysis;

        double period = 60.0 / bpm;

        // The first beat of the line at or after the start of the file. The named downbeat has index
        // -k in that line, which fixes the bar phase below.
        int k = (int)Math.Ceiling(-downbeatSeconds / period - 1e-9);
        double first = downbeatSeconds + k * period;

        int count = (int)Math.Floor((analysis.DurationSeconds - first) / period) + 1;
        if (count < 2)
            return analysis;

        var beats = new double[count];
        for (int i = 0; i < count; i++)
            beats[i] = first + i * period;

        // Which of the early beats is a bar line: the smallest index that is a whole number of bars
        // away from the named downbeat.
        int firstDownbeat = (((-k) % beatsPerBar) + beatsPerBar) % beatsPerBar;

        return analysis with
        {
            Bpm = bpm,
            BeatTimes = beats,
            FirstDownbeat = firstDownbeat,
            BeatsPerBar = beatsPerBar,

            // Everything that was counted in bars of the old grid is counted again in bars of the new
            // grid.
            //
            // The phrase must be recalculated. It is a length in bars and a bar number to start from.
            // If the track is now counted at the other octave, a thirty-two bar phrase becomes
            // sixteen bars. If the downbeat moves by one beat, the first bar of the phrase is no longer
            // a bar where a phrase begins. MixInFinder loops the track at Phrases.Bars, so a stale
            // value would cut every loop to the wrong length, with no visible error because the number
            // still looks like a phrase.
            //
            // Both values are calculated from the bass curve and the grid, and both are already
            // available, so this is cheap and needs no decode. When the bands were not measured there
            // is nothing to calculate from, so the old values are kept. Rows analysed before the bands
            // existed have no bands.
            Phrases = analysis.HasBands
                ? TrackShape.Phrases(analysis.BassCurve, beats, firstDownbeat, beatsPerBar)
                : analysis.Phrases,

            // The drops are times and not bar numbers, so they are less affected by an edit. But they
            // are snapped to bar lines, and those lines have just moved. The double drop needs one
            // track's drop to land on another's to the sample.
            DropSeconds = analysis.HasBands
                ? TrackShape.Drops(analysis.BassCurve, beats, firstDownbeat, beatsPerBar)
                : analysis.DropSeconds,

            // A person placed this line, so the question of how sure the analyser was does not apply.
            // Full confidence, otherwise every reader that checks it would treat a deliberate answer
            // as a doubtful one.
            BpmConfidence = 1,
            DownbeatConfidence = 1,

            // Tempo stability cannot be recalculated. It is the distance between the beats as they
            // were heard and the grid laid over them. The grid has been replaced, and the heard beats
            // are not stored anywhere, so there is nothing to measure the new grid against. It is set
            // to unmeasured, which means "treat it like any other track".
            TempoStability = TrackShape.Unmeasured,

            IsEdited = true,
        };
    }

    /// <summary>
    /// The same line, moved along the track. This is what a DJ does when the beats are right but the
    /// "one" is on the three.
    /// </summary>
    public static TrackAnalysis NudgePhase(TrackAnalysis analysis, double seconds)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (analysis.Bpm <= 0 || analysis.BeatTimes.Count == 0 || Math.Abs(seconds) < 1e-9)
            return analysis;

        return Regrid(
            analysis,
            analysis.Bpm,
            FirstDownbeatSeconds(analysis) + seconds,
            analysis.BeatsPerBar);
    }

    /// <summary>
    /// The track counted at twice or half the speed, with the bar lines kept where they are.
    ///
    /// <para>This is the most useful correction to be able to make. The octave of a reading is a coin
    /// toss on a track that can be counted either way, and a wrong octave makes every bar in the
    /// changeover twice as long as it should be: a sixteen-bar mix runs for thirty-two bars, and
    /// nothing in the plan looks wrong.</para>
    /// </summary>
    /// <param name="factor">2 to count it twice as fast, 0.5 to count it half.</param>
    public static TrackAnalysis Octave(TrackAnalysis analysis, double factor)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (analysis.Bpm <= 0 || factor <= 0)
            return analysis;

        return Regrid(
            analysis,
            analysis.Bpm * factor,
            FirstDownbeatSeconds(analysis),
            analysis.BeatsPerBar);
    }

    /// <summary>
    /// Makes the given time a bar line. This is what a click on the waveform means: not "put a beat
    /// here" but "the one is here, and the rest of the line follows from it".
    /// </summary>
    public static TrackAnalysis DownbeatAt(TrackAnalysis analysis, double seconds)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        return analysis.Bpm <= 0
            ? analysis
            : Regrid(analysis, analysis.Bpm, seconds, analysis.BeatsPerBar);
    }

    /// <summary>Where a changeover meets this track: the point where the next track is mixed in, the
    /// point where this track is mixed out, or either one alone.</summary>
    public static TrackAnalysis WithCues(
        TrackAnalysis analysis, double? introEndSeconds = null, double? outroStartSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (introEndSeconds is null && outroStartSeconds is null)
            return analysis;

        double end = analysis.DurationSeconds > 0 ? analysis.DurationSeconds : double.MaxValue;
        double intro = Math.Clamp(introEndSeconds ?? analysis.IntroEndSeconds, 0, end);
        double outro = Math.Clamp(outroStartSeconds ?? analysis.OutroStartSeconds, 0, end);

        return analysis with
        {
            IntroEndSeconds = intro,
            OutroStartSeconds = outro,
            IsEdited = true,
        };
    }

    /// <summary>Where the first bar line of the grid falls. This is the phase of the whole line.</summary>
    public static double FirstDownbeatSeconds(TrackAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (analysis.BeatTimes.Count == 0)
            return 0;

        int at = Math.Clamp(analysis.FirstDownbeat, 0, analysis.BeatTimes.Count - 1);
        return analysis.BeatTimes[at];
    }

    /// <summary>
    /// What was changed, compared with what was measured. The value of an edit is in this difference,
    /// so it is calculated once here and not by each caller that wants to record it.
    /// </summary>
    public static IReadOnlyList<string> Diff(TrackAnalysis before, TrackAnalysis after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var changes = new List<string>(6);

        if (Math.Abs(before.Bpm - after.Bpm) > 0.005)
            changes.Add($"bpm {before.Bpm:F2} -> {after.Bpm:F2}");

        if (before.BeatsPerBar != after.BeatsPerBar)
            changes.Add($"beatsPerBar {before.BeatsPerBar} -> {after.BeatsPerBar}");

        double phaseBefore = FirstDownbeatSeconds(before);
        double phaseAfter = FirstDownbeatSeconds(after);
        if (Math.Abs(phaseBefore - phaseAfter) > 0.002)
            changes.Add($"downbeat {phaseBefore:F3}s -> {phaseAfter:F3}s ({(phaseAfter - phaseBefore) * 1000:+0;-0} ms)");

        if (Math.Abs(before.IntroEndSeconds - after.IntroEndSeconds) > 0.002)
            changes.Add($"introEnd {before.IntroEndSeconds:F2}s -> {after.IntroEndSeconds:F2}s");

        if (Math.Abs(before.OutroStartSeconds - after.OutroStartSeconds) > 0.002)
            changes.Add($"outroStart {before.OutroStartSeconds:F2}s -> {after.OutroStartSeconds:F2}s");

        return changes;
    }
}
