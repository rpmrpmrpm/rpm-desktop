using RPM.Core.Analysis;

namespace RPM.Core.Tests;

/// <summary>
/// What the paused view shows: what the analyser knows about the bar under the playhead, as plain
/// arithmetic over a reading.
/// </summary>
public class BarFactsTests
{
    [Fact]
    public void A_bar_knows_its_place_in_the_count()
    {
        // 120 BPM in four: a bar every two seconds, downbeat on zero.
        var analysis = Analysis(bpm: 120, duration: 120);

        var facts = BarFacts.At(analysis, 9.0);

        Assert.True(facts.HasGrid);
        Assert.Equal(8.0, facts.BarStartSeconds, 3);
        Assert.Equal(2.0, facts.BarSeconds, 3);

        // Bars: 0-2 is bar 1, 2-4 bar 2, 8-10 bar 5, and bar 5 is the start of the second phrase.
        Assert.Equal(5, facts.BarNumber);
        Assert.Equal(1, facts.PhraseSlot);
        Assert.Equal(4, facts.PhraseBars);

        var next = BarFacts.At(analysis, 11.0);
        Assert.Equal(6, next.BarNumber);
        Assert.Equal(2, next.PhraseSlot);
    }

    [Fact]
    public void A_record_with_no_grid_still_reports_its_levels()
    {
        var analysis = Analysis(bpm: 0, duration: 60) with
        {
            EnergyCurve = Curve(60, 0.1f),
        };

        var facts = BarFacts.At(analysis, 30);

        Assert.False(facts.HasGrid);
        Assert.Equal(0, facts.BarNumber);
        Assert.True(facts.Energy > 0);
    }

    [Fact]
    public void The_bar_says_which_stretch_of_the_record_it_is_in()
    {
        var analysis = Analysis(bpm: 120, duration: 200) with
        {
            IntroEndSeconds = 16,
            OutroStartSeconds = 180,
            AudioStartSeconds = 1,
            AudioEndSeconds = 199,
        };

        Assert.Equal(BarSection.BeforeTheMusic, BarFacts.At(analysis, 0.2).Section);
        Assert.Equal(BarSection.Intro, BarFacts.At(analysis, 8).Section);
        Assert.Equal(BarSection.Body, BarFacts.At(analysis, 100).Section);
        Assert.Equal(BarSection.Outro, BarFacts.At(analysis, 190).Section);
    }

    [Fact]
    public void The_bands_are_read_across_the_bar_and_the_drop_is_signed()
    {
        // Quiet bass for the first ten seconds, loud after, and one drop at 32 s.
        var bass = Curve(120, 0.02f);
        for (int i = 20; i < bass.Length; i++)
            bass[i] = 0.3f;

        var analysis = Analysis(bpm: 120, duration: 120) with
        {
            BassCurve = bass,
            MidCurve = Curve(120, 0.1f),
            TopCurve = Curve(120, 0.1f),
            VocalCurve = Curve(120, 0.4f),
            DropSeconds = [32],
        };

        var early = BarFacts.At(analysis, 4);
        var late = BarFacts.At(analysis, 30);

        Assert.NotNull(early.Bass);
        Assert.NotNull(late.Bass);
        Assert.True(late.Bass > early.Bass, "the loud stretch reads louder");
        Assert.Equal(0.4, late.VocalShare!.Value, 2);

        Assert.Equal(28, early.SecondsToNearestDrop!.Value, 1);
        Assert.Equal(2, late.SecondsToNearestDrop!.Value, 1);
        Assert.True(BarFacts.At(analysis, 40).SecondsToNearestDrop!.Value < 0, "a passed drop is behind");
    }

    [Fact]
    public void A_bar_past_the_tracked_beats_says_it_stands_on_arithmetic()
    {
        var analysis = Analysis(bpm: 120, duration: 200) with { BeatLastSeconds = 100 };

        Assert.False(BarFacts.At(analysis, 50).OnExtrapolatedGrid);
        Assert.True(BarFacts.At(analysis, 150).OnExtrapolatedGrid);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static float[] Curve(double seconds, float value)
    {
        var curve = new float[(int)(seconds / EnergyProfile.StepSeconds)];
        Array.Fill(curve, value);
        return curve;
    }

    private static TrackAnalysis Analysis(double bpm, double duration)
    {
        var beats = new List<double>();
        if (bpm > 0)
            for (double at = 0; at < duration; at += 60.0 / bpm)
                beats.Add(at);

        return new TrackAnalysis
        {
            FilePath = "x.flac",
            DurationSeconds = duration,
            Bpm = bpm,
            BpmConfidence = bpm > 0 ? 0.9 : 0,
            BeatTimes = beats,
            FirstDownbeat = 0,
            BeatsPerBar = 4,
            EnergyCurve = Curve(duration, 0.2f),
            Intensity = 0.5,
        };
    }
}
