using RPM.Core.Analysis;

namespace RPM.Core.Tests;

/// <summary>
/// What else changes when a grid is placed by hand. A re-grid replaces the beat grid, and some values
/// measured against the old grid are counted in ITS bars. A phrase is a length in bars and a start
/// bar, so if the track is counted at the other octave, a sixteen-bar phrase must become thirty-two.
/// </summary>
public class GridEditRegridTests
{
    /// <summary>An analysis with a grid and a bass curve that has a phrase in it: loud for six bars of
    /// every eight, so the shortest phrase length that fits the track is eight bars.</summary>
    private static TrackAnalysis Analysed(double bpm = 128, int beats = 512)
    {
        double period = 60.0 / bpm;
        var grid = new double[beats];
        for (int i = 0; i < beats; i++)
            grid[i] = i * period;

        double duration = beats * period;
        var bass = new float[(int)(duration / EnergyProfile.StepSeconds) + 1];
        for (int i = 0; i < bass.Length; i++)
        {
            // In every eight bars the bass is quiet for two. This is how a phrase shows in the bass
            // curve.
            int bar = (int)(i * EnergyProfile.StepSeconds / (period * 4));
            bass[i] = bar % 8 < 6 ? 0.8f : 0.05f;
        }

        return new TrackAnalysis
        {
            FilePath = "/library/phrased.wav",
            DurationSeconds = duration,
            Bpm = bpm,
            BpmConfidence = 1,
            BeatTimes = grid,
            FirstDownbeat = 0,
            BeatsPerBar = 4,
            BassCurve = bass,
            MidCurve = bass,
            TopCurve = bass,
            VocalCurve = bass,
            Phrases = TrackShape.Phrases(bass, grid, 0, 4),
            DropSeconds = TrackShape.Drops(bass, grid, 0, 4),
            TempoStability = 0.9,
        };
    }

    [Fact]
    public void Counting_the_record_at_the_other_octave_counts_its_phrase_again()
    {
        var before = Analysed();
        Assert.True(before.Phrases.IsKnown, "the fixture has no phrase to invalidate");

        var after = GridEdit.Octave(before, 2);

        Assert.Equal(before.Bpm * 2, after.Bpm, 6);

        // A phrase is a span of the track. When the track is counted in twice as many bars, the same
        // music is twice as many bars long. Without this change the phrase would cover half the
        // music, and MixInFinder loops at Phrases.Bars, so every loop would be too short.
        Assert.Equal(before.Phrases.Bars * 2, after.Phrases.Bars);

        // The invariant: the phrase is the same length in seconds.
        Assert.Equal(
            before.BarSeconds * before.Phrases.Bars,
            after.BarSeconds * after.Phrases.Bars,
            3);
    }

    [Fact]
    public void The_drops_are_put_back_on_the_bar_lines_the_edit_moved()
    {
        var before = Analysed();
        Assert.NotEmpty(before.DropSeconds);

        // The first beat of the bar moves by one beat, which moves every bar line in the track.
        var after = GridEdit.NudgePhase(before, 60.0 / before.Bpm);

        Assert.NotEmpty(after.DropSeconds);
        foreach (double drop in after.DropSeconds)
        {
            double bar = after.BarAtOrBefore(drop);
            Assert.True(Math.Abs(bar - drop) < 1e-6,
                $"a drop at {drop:F3}s is {Math.Abs(bar - drop) * 1000:F0} ms off the nearest bar line");
        }
    }

    [Fact]
    public void A_reading_with_no_bands_keeps_what_it_had()
    {
        // A row analysed before the band curves were measured. There is nothing to compute a phrase
        // from, so the old phrase is kept rather than a guessed one.
        var bare = Analysed() with { BassCurve = [], MidCurve = [], TopCurve = [], VocalCurve = [] };
        var after = GridEdit.Octave(bare, 2);

        Assert.Equal(bare.Phrases, after.Phrases);
        Assert.Equal(bare.DropSeconds, after.DropSeconds);
    }

    [Fact]
    public void Steadiness_is_given_up_rather_than_carried_or_guessed()
    {
        // Steadiness is the distance between the detected beats and the grid, and the detected beats
        // are not stored. So a hand-placed grid cannot be scored against them.
        var after = GridEdit.Octave(Analysed(), 2);

        Assert.Equal(TrackShape.Unmeasured, after.TempoStability);
        Assert.True(after.IsSteadyEnoughToHold, "an unmeasured record reads as the worst case");
        Assert.True(after.IsEdited);
    }

    [Fact]
    public void The_readings_that_walk_the_grid_need_no_putting_right()
    {
        // The local tempo, the bar number and the bar count are computed from the beat array each
        // time they are read, so a new array needs nothing recomputed.
        var after = GridEdit.Octave(Analysed(), 2);

        Assert.Equal(after.Bpm, after.BpmAt(30), 6);
        Assert.Equal(after.Bpm, after.BpmAt(after.DurationSeconds - 5), 6);
        Assert.Equal(0, after.BarIndexAt(after.FirstWholeBarSeconds));
    }
}
