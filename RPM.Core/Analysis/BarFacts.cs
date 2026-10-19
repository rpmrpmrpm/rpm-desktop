namespace RPM.Core.Analysis;

/// <summary>Which part of the track a bar sits in.</summary>
public enum BarSection
{
    /// <summary>Before the measured start of the audio.</summary>
    BeforeTheMusic,

    /// <summary>Inside the intro, before <see cref="TrackAnalysis.IntroEndSeconds"/>.</summary>
    Intro,

    /// <summary>The main part of the track.</summary>
    Body,

    /// <summary>Past <see cref="TrackAnalysis.OutroStartSeconds"/>.</summary>
    Outro,

    /// <summary>Past the measured end of the audio.</summary>
    AfterTheMusic,
}

/// <summary>
/// Everything measured about one bar of one track, gathered for a screen that is paused.
///
/// <para>The paused view uses this. The set is stopped, the position is on a bar, and the question
/// is what the analyser knows about this bar: its place in the count, its levels band by band, how
/// far the nearest drop is, and whether the grid under it was heard or extrapolated. While the set
/// plays nobody can read twelve numbers per bar. When it is paused they can.</para>
///
/// <para>Pure arithmetic over a <see cref="TrackAnalysis"/>, so it is cheap and testable. The caller
/// asks once per pause or scrub, not once per frame.</para>
/// </summary>
public sealed record BarFacts
{
    /// <summary>Where the question was asked, in seconds from the start of the track.</summary>
    public required double Seconds { get; init; }

    /// <summary>False when the track has no usable grid. Then only the levels below mean anything
    /// and every bar-counting field is zero.</summary>
    public bool HasGrid { get; init; }

    /// <summary>Where this bar begins.</summary>
    public double BarStartSeconds { get; init; }

    /// <summary>The track's own bar length (its count and its tempo, not the mix's).</summary>
    public double BarSeconds { get; init; }

    public int BeatsPerBar { get; init; }

    /// <summary>The bar number, counted from the track's first whole bar: that bar is 1, and the
    /// bars before it are 0 and below. This is the count a DJ says along with the music.</summary>
    public int BarNumber { get; init; }

    /// <summary>Where the bar sits in its phrase: 1 is the phrase head, and <see cref="PhraseBars"/>
    /// is the bar before the next head.</summary>
    public int PhraseSlot { get; init; }

    public int PhraseBars { get; init; }

    /// <summary>True when the grid under this bar is a straight line continued past the last beat
    /// that was actually heard. A mark placed here is placed on arithmetic, not on drums.</summary>
    public bool OnExtrapolatedGrid { get; init; }

    /// <summary>True when that straight line is counted over music that is still playing. This is
    /// the half of <see cref="OnExtrapolatedGrid"/> that deserves a warning (see
    /// <see cref="TrackAnalysis.IsExtrapolatedOverMusicAt"/>). The tail of every track is
    /// extrapolated, and that case does not count here.</summary>
    public bool ExtrapolatedOverMusic { get; init; }

    public BarSection Section { get; init; }

    /// <summary>How hard the bar hits, 0..1 on the same scale as <see cref="TrackAnalysis.Intensity"/>.</summary>
    public double Energy { get; init; }

    /// <summary>Band levels across the bar, 0..1 on the intensity scale. Null when the bands were
    /// not measured.</summary>
    public double? Bass { get; init; }
    public double? Mid { get; init; }
    public double? Top { get; init; }

    /// <summary>How much of the bar's energy is in the range of a voice, 0..1. Null when not
    /// measured.</summary>
    public double? VocalShare { get; init; }

    /// <summary>Seconds to the nearest measured drop: positive when it is ahead, negative when it has
    /// passed. Null when the track has none.</summary>
    public double? SecondsToNearestDrop { get; init; }

    /// <summary>The facts about the bar under <paramref name="seconds"/>.</summary>
    public static BarFacts At(TrackAnalysis analysis, double seconds)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        seconds = Math.Max(0, seconds);

        bool hasGrid = analysis.Bpm > 0 && analysis.BeatTimes.Count > 0;
        double barStart = 0, barLength = 0;
        int barNumber = 0, phraseSlot = 0;

        if (hasGrid)
        {
            barStart = analysis.BarAtOrBefore(seconds);

            // Both values are counted from the grid, not calculated from the tempo. The two agree
            // only while the grid is evenly spaced. ModelBeatGrid bends the grid on a track that
            // does not keep a steady tempo, and there a division is right at the start of the
            // track but names the wrong bar by the outro.
            barLength = analysis.BarSecondsAt(seconds);
            int fromFirstWhole = analysis.BarIndexAt(seconds);

            barNumber = fromFirstWhole + 1;

            int phraseBars = TrackAnalysis.PhraseBars;
            phraseSlot = ((fromFirstWhole % phraseBars) + phraseBars) % phraseBars + 1;
        }

        double? drop = null;
        foreach (double at in analysis.DropSeconds)
        {
            double away = at - seconds;
            if (drop is null || Math.Abs(away) < Math.Abs(drop.Value))
                drop = away;
        }

        double from = hasGrid ? barStart : seconds;
        double to = hasGrid && barLength > 0 ? barStart + barLength : from + 2;

        return new BarFacts
        {
            Seconds = seconds,
            HasGrid = hasGrid,
            BarStartSeconds = barStart,
            BarSeconds = barLength,
            BeatsPerBar = analysis.BeatsPerBar,
            BarNumber = barNumber,
            PhraseSlot = phraseSlot,
            PhraseBars = TrackAnalysis.PhraseBars,
            OnExtrapolatedGrid = hasGrid && analysis.IsExtrapolatedAt(seconds),
            ExtrapolatedOverMusic = hasGrid && analysis.IsExtrapolatedOverMusicAt(seconds),
            Section = SectionAt(analysis, seconds),
            Energy = Level(analysis.EnergyCurve, from, to) ?? 0,
            Bass = Level(analysis.BassCurve, from, to),
            Mid = Level(analysis.MidCurve, from, to),
            Top = Level(analysis.TopCurve, from, to),
            VocalShare = Share(analysis.VocalCurve, from, to),
            SecondsToNearestDrop = drop,
        };
    }

    private static BarSection SectionAt(TrackAnalysis analysis, double seconds)
    {
        if (analysis.HasEdges && seconds < analysis.AudioStartSeconds)
            return BarSection.BeforeTheMusic;
        if (analysis.HasEdges && seconds > analysis.AudibleEndSeconds)
            return BarSection.AfterTheMusic;
        if (analysis.IntroEndSeconds > 0 && seconds < analysis.IntroEndSeconds)
            return BarSection.Intro;
        if (analysis.OutroStartSeconds > 0 && seconds >= analysis.OutroStartSeconds)
            return BarSection.Outro;
        return BarSection.Body;
    }

    /// <summary>The curve's average across the bar, on the intensity scale. Null when not
    /// measured.</summary>
    private static double? Level(IReadOnlyList<float> curve, double from, double to)
    {
        double? mean = Mean(curve, from, to);
        return mean is { } rms ? EnergyProfile.Rescale(rms) : null;
    }

    /// <summary>The curve's average across the bar, unchanged. For curves that are already a
    /// share and not a level.</summary>
    private static double? Share(IReadOnlyList<float> curve, double from, double to) =>
        Mean(curve, from, to);

    private static double? Mean(IReadOnlyList<float> curve, double from, double to)
    {
        if (curve.Count == 0)
            return null;

        int first = Math.Clamp((int)Math.Floor(from / EnergyProfile.StepSeconds), 0, curve.Count - 1);
        int last = Math.Clamp((int)Math.Ceiling(to / EnergyProfile.StepSeconds) - 1, first, curve.Count - 1);

        double sum = 0;
        for (int i = first; i <= last; i++)
            sum += curve[i];
        return sum / (last - first + 1);
    }
}
