using RPM.Core.Analysis;

namespace RPM.Core.Tests;

/// <summary>
/// The four things the analyser reads off the bands rather than off the audio: where the phrases
/// fall, how steadily the record is played, where the beat comes back, and how loud it is at a cue.
///
/// <para>All of them are arithmetic over curves already computed, and all of them are allowed to
/// answer "unknown". A record with no phrasing must not be given one, because a changeover landed
/// on an invented phrase is worse than one landed on a bar.</para>
/// </summary>
public class TrackShapeTests
{
    private const double Bpm = 120;                    // two beats a second, a bar every two seconds
    private const int BeatsPerBar = 4;

    /// <summary>A grid of <paramref name="bars"/> bars at 120 BPM, starting at zero.</summary>
    private static double[] Grid(int bars)
    {
        var beats = new double[bars * BeatsPerBar + 1];
        for (int i = 0; i < beats.Length; i++)
            beats[i] = i * 60.0 / Bpm;
        return beats;
    }

    /// <summary>A bass curve where the named bars are loud and the rest are quiet. One bar is two
    /// seconds, which is four readings.</summary>
    private static float[] BassWithBarsLoud(int bars, Func<int, bool> loud)
    {
        int perBar = (int)(2.0 / EnergyProfile.StepSeconds);
        var curve = new float[bars * perBar];
        for (int bar = 0; bar < bars; bar++)
            for (int i = 0; i < perBar; i++)
                curve[bar * perBar + i] = loud(bar) ? 0.8f : 0.1f;
        return curve;
    }

    [Fact]
    public void A_record_that_changes_every_eight_bars_is_read_as_eights()
    {
        // Loud for eight, quiet for eight, over and over: the change points are every eight bars.
        var bass = BassWithBarsLoud(32, bar => bar / 8 % 2 == 0);

        var phrases = TrackShape.Phrases(bass, Grid(32), firstDownbeat: 0, BeatsPerBar);

        Assert.True(phrases.IsKnown);
        Assert.Equal(8, phrases.Bars);
        Assert.Equal(0, phrases.FirstBar);
        Assert.True(phrases.Confidence > 0);
    }

    /// <summary>The phase matters as much as the length: a record whose phrases start two bars in is
    /// mixed two bars wrong by a grid that assumes they start at the first bar.</summary>
    [Fact]
    public void The_phase_is_where_the_changes_actually_are()
    {
        var bass = BassWithBarsLoud(32, bar => (bar + 6) / 8 % 2 == 0);

        var phrases = TrackShape.Phrases(bass, Grid(32), firstDownbeat: 0, BeatsPerBar);

        Assert.Equal(8, phrases.Bars);
        Assert.Equal(2, phrases.FirstBar);
    }

    /// <summary>
    /// FirstBar is counted in bars from the FIRST DOWNBEAT, not from the top of the file.
    ///
    /// <para>Every other test here passes <c>firstDownbeat: 0</c>, where the two are the same number
    /// and the difference cannot show. Anything that turns this reading back into times has to add the
    /// downbeat in. See MixLabTimeline.DrawPhrases, which draws these boundaries on the waveform and
    /// would be a whole bar out on any record whose grid does not begin on its one.</para>
    /// </summary>
    [Fact]
    public void The_phase_is_counted_from_the_first_downbeat_and_not_from_the_file()
    {
        // Changes at absolute bars 0, 8, 16... and a grid whose first downbeat is one bar in. Counted
        // from there, the next change is seven bars away.
        var bass = BassWithBarsLoud(40, bar => bar / 8 % 2 == 0);

        var atTheTop = TrackShape.Phrases(bass, Grid(40), firstDownbeat: 0, BeatsPerBar);
        var aBarIn = TrackShape.Phrases(bass, Grid(40), firstDownbeat: BeatsPerBar, BeatsPerBar);

        Assert.Equal(8, atTheTop.Bars);
        Assert.Equal(0, atTheTop.FirstBar);

        Assert.Equal(8, aBarIn.Bars);
        Assert.Equal(7, aBarIn.FirstBar);
    }

    [Fact]
    public void A_record_that_never_changes_is_given_no_phrase_grid()
    {
        var bass = BassWithBarsLoud(32, _ => true);

        var phrases = TrackShape.Phrases(bass, Grid(32), firstDownbeat: 0, BeatsPerBar);

        Assert.False(phrases.IsKnown);
        Assert.Equal(0, phrases.Bars);
    }

    /// <summary>
    /// Steadiness is the difference between where the beats were heard and where the ruler puts them.
    ///
    /// <para>Not the ruler's own intervals: the grid is a straight line through the tracked beats by
    /// the time anything downstream sees it, so measuring it would answer 1 for every record. Over a
    /// real library of forty-two tracks that gave one distinct value.</para>
    /// </summary>
    [Fact]
    public void A_machine_steady_record_reads_as_steady_and_a_drifting_one_does_not()
    {
        var grid = Grid(16);

        // Heard exactly where the ruler says: a sequencer.
        Assert.Equal(1, TrackShape.TempoStability(grid, grid), 3);

        // Heard a fifth of a beat away from it, on and off, all the way through: a band.
        var wandering = grid.Select((t, i) => t + (i % 2 == 0 ? 0.1 : -0.1)).ToArray();
        Assert.True(TrackShape.TempoStability(wandering, grid) < 0.2);

        // And never exactly zero, which is reserved: zero is what a reading borrowed from the pool
        // carries, and it has to be tellable from the loosest record ever pressed.
        Assert.True(TrackShape.TempoStability(wandering, grid) > TrackShape.Unmeasured);
    }

    /// <summary>An unknown is not the worst case. A record nobody measured is held like any other,
    /// because most records are sequenced, and the alternative would shorten every fade in a library
    /// the pool answered for.</summary>
    [Fact]
    public void A_record_nobody_measured_is_held_like_any_other()
    {
        var unmeasured = new TrackAnalysis { FilePath = "/a.flac", TempoStability = TrackShape.Unmeasured };
        var steady = new TrackAnalysis { FilePath = "/b.flac", TempoStability = 0.9 };
        var loose = new TrackAnalysis { FilePath = "/c.flac", TempoStability = 0.1 };

        Assert.True(unmeasured.IsSteadyEnoughToHold);
        Assert.True(steady.IsSteadyEnoughToHold);
        Assert.False(loose.IsSteadyEnoughToHold);
    }

    /// <summary>A tracker that dropped a beat must not report the whole record after it as drift:
    /// the offset is taken against the nearest line, not the matching one.</summary>
    [Fact]
    public void A_dropped_beat_is_not_read_as_a_record_that_wanders()
    {
        var grid = Grid(16);
        var missingOne = grid.Where((_, i) => i != 7).ToArray();

        Assert.Equal(1, TrackShape.TempoStability(missingOne, grid), 3);
    }

    [Fact]
    public void A_record_with_too_few_beats_has_no_stability_to_report()
    {
        Assert.Equal(0, TrackShape.TempoStability([0, 0.5, 1.0], Grid(16)));
        Assert.Equal(0, TrackShape.TempoStability(Grid(16), [0, 0.5, 1.0]));
    }

    /// <summary>The breakdown and the bar the kick comes back on: the best place in the record to
    /// land another one.</summary>
    [Fact]
    public void The_bar_the_kick_returns_on_is_found()
    {
        // Body for eight bars, gone for four, back for the rest.
        var bass = BassWithBarsLoud(24, bar => bar < 8 || bar >= 12);

        var drops = TrackShape.Drops(bass, Grid(24), firstDownbeat: 0, BeatsPerBar);

        var drop = Assert.Single(drops);
        Assert.Equal(12 * 2.0, drop, 1);          // bar 12, and a bar is two seconds at 120 BPM
    }

    [Fact]
    public void A_record_that_never_breaks_down_has_no_drops()
    {
        Assert.Empty(TrackShape.Drops(BassWithBarsLoud(24, _ => true), Grid(24), 0, BeatsPerBar));
    }

    /// <summary>A changeover does not meet the record's average, it meets the record at the cue. The
    /// two can be a long way apart.</summary>
    [Fact]
    public void The_level_at_a_cue_is_read_where_the_cue_is()
    {
        // Quiet first ten seconds, loud after.
        var levels = new float[120];
        for (int i = 0; i < levels.Length; i++)
            levels[i] = i < 20 ? 0.02f : 0.3f;

        double intro = TrackShape.LevelAt(levels, 2);
        double body = TrackShape.LevelAt(levels, 40);

        Assert.True(body > intro + 0.2, $"body {body:F2} should be well above intro {intro:F2}");
    }

    /// <summary>
    /// The shortest spacing that explains the record wins.
    ///
    /// <para>Scored on their own, longer spacings always win: they sample fewer bars, so they can land
    /// on the biggest changes and skip every bar that would count against them. Over a real library
    /// that read every record as thirty-twos, which says nothing about the music.</para>
    /// </summary>
    [Fact]
    public void Eights_are_preferred_to_thirty_twos_when_both_fit()
    {
        // Changes every eight bars. Thirty-twos "work" here too (every 32nd bar is also an 8th), and
        // they are the wrong answer.
        var bass = BassWithBarsLoud(64, bar => bar / 8 % 2 == 0);

        var phrases = TrackShape.Phrases(bass, Grid(64), firstDownbeat: 0, BeatsPerBar);

        Assert.Equal(8, phrases.Bars);
    }

    /// <summary>And a record that really is built in thirty-twos still reads as one: the shorter
    /// spacing is only taken when it explains the record nearly as well.</summary>
    [Fact]
    public void A_record_built_in_thirty_twos_still_reads_as_one()
    {
        var bass = BassWithBarsLoud(128, bar => bar / 32 % 2 == 0);

        var phrases = TrackShape.Phrases(bass, Grid(128), firstDownbeat: 0, BeatsPerBar);

        Assert.Equal(32, phrases.Bars);
    }
}
