using RPM.Core.Analysis;

namespace RPM.Core.Tests;

/// <summary>
/// Where a record starts when it is arriving into silence rather than over another record: the bar
/// the record stops repeating itself on.
/// </summary>
public class MixInFinderTests
{
    private const double Bpm = 128;
    private static readonly double Bar = 4 * 60.0 / Bpm;

    /// <summary>
    /// A record built a bar at a time, so a test can say what each bar holds and then ask where the
    /// record starts.
    /// </summary>
    /// <param name="bar">Given a bar number, the level of each band in it: bass, mid, top.</param>
    private static TrackAnalysis Record(int bars, Func<int, (float Bass, float Mid, float Top)> bar)
    {
        double duration = bars * Bar;
        int steps = (int)(duration / EnergyProfile.StepSeconds);

        var bass = new float[steps];
        var mid = new float[steps];
        var top = new float[steps];

        // Indexed by time, not by a whole number of steps per bar: a bar at 128 BPM is 1.875 s, which
        // is three and three quarter steps, and rounding that walks the curves off the grid.
        for (int i = 0; i < steps; i++)
        {
            var (b, m, t) = bar((int)(i * EnergyProfile.StepSeconds / Bar));
            bass[i] = b;
            mid[i] = m;
            top[i] = t;
        }

        var beats = new List<double>();
        for (double at = 0; at < duration; at += 60.0 / Bpm)
            beats.Add(at);

        return new TrackAnalysis
        {
            FilePath = "/library/arriving.flac",
            DurationSeconds = duration,
            Bpm = Bpm,
            BpmConfidence = 0.9,
            BeatTimes = beats,
            FirstDownbeat = 0,
            BeatsPerBar = 4,
            EnergyCurve = [0.3f, 0.3f],
            Intensity = 0.5,
            BassCurve = bass,
            MidCurve = mid,
            TopCurve = top,
            OutroStartSeconds = duration - 20,
        };
    }

    /// <summary>Which bar a time falls on.</summary>
    private static int BarOf(double seconds) => (int)Math.Round(seconds / Bar);

    [Fact]
    public void A_looping_intro_is_left_at_its_first_deviation()
    {
        // Eight bars of the same loop, four times over, and then the record comes in at bar 32. The
        // loop has a full middle in it, so "where the middle arrives" would answer bar zero. The
        // question is where the record stops repeating, not where it stops being quiet.
        var analysis = Record(96, b => b < 24
            ? (0.40f, 0.30f, 0.20f)
            : (0.40f, 0.60f, 0.35f));

        double at = MixInFinder.WhereItStarts(analysis);

        Assert.Equal(24, BarOf(at));
        Assert.Equal(at, analysis.BarAtOrBefore(at), 3);
    }

    [Fact]
    public void A_kick_only_intro_is_skipped_to_the_bar_the_rest_comes_in_on()
    {
        // Sixteen bars of nothing but a kick. It is a loop like any other; the record starts where
        // something else does.
        var analysis = Record(96, b => b < 16
            ? (0.40f, 0.01f, 0.01f)
            : (0.40f, 0.50f, 0.30f));

        Assert.Equal(16, BarOf(MixInFinder.WhereItStarts(analysis)));
    }

    [Fact]
    public void A_short_intro_is_not_stepped_over()
    {
        // Four bars, which a phrase of eight would walk straight past.
        var analysis = Record(96, b => b < 4
            ? (0.40f, 0.01f, 0.01f)
            : (0.40f, 0.50f, 0.30f));

        Assert.Equal(4, BarOf(MixInFinder.WhereItStarts(analysis)));
    }

    [Fact]
    public void A_record_that_is_going_from_its_first_bar_starts_at_its_first_bar()
    {
        var analysis = Record(96, _ => (0.40f, 0.50f, 0.30f));

        Assert.Equal(0, MixInFinder.WhereItStarts(analysis), 3);
    }

    [Fact]
    public void An_intro_longer_than_a_minute_is_skipped_rather_than_played_through()
    {
        // Fifty bars of loop before the record comes in (a minute and a half). All of it is skipped:
        // being long is a reason to skip an intro, not a reason to give up on finding its end.
        var analysis = Record(200, b => b < 50
            ? (0.40f, 0.02f, 0.15f)
            : (0.45f, 0.55f, 0.35f));

        double at = MixInFinder.WhereItStarts(analysis);

        Assert.True(at > MixInFinder.MaxIntroSeconds, $"started {at:F1}s in, inside the long intro");
        Assert.Equal(50, BarOf(at));
    }

    [Fact]
    public void A_record_that_never_changes_at_all_is_put_on_rather_than_waited_on()
    {
        // No deviation anywhere, so there is nothing to start on. It is not left running for two
        // minutes on the chance that something happens.
        var analysis = Record(200, _ => (0.40f, 0.02f, 0.02f));

        double at = MixInFinder.WhereItStarts(analysis);

        Assert.InRange(at, 0, MixInFinder.MaxIntroSeconds);
    }

    [Fact]
    public void One_bar_unlike_the_last_phrase_is_a_fill_and_not_the_record_starting()
    {
        // A loop with a fill at the end of its fourth phrase. The record itself comes in at bar 40.
        var analysis = Record(128, b =>
            b >= 40 ? (0.45f, 0.60f, 0.40f) :
            b == 31 ? (0.40f, 0.02f, 0.60f) :          // the fill: hats up, everything else out
            (0.40f, 0.20f, 0.15f));

        Assert.Equal(40, BarOf(MixInFinder.WhereItStarts(analysis)));
    }

    [Fact]
    public void A_record_read_before_the_bands_were_measured_falls_back_to_its_intro()
    {
        var analysis = Record(96, _ => (0.40f, 0.50f, 0.30f)) with
        {
            BassCurve = [],
            MidCurve = [],
            TopCurve = [],
            IntroEndSeconds = 30,
        };

        double at = MixInFinder.WhereItStarts(analysis);

        Assert.InRange(at, 30 - Bar, 30);
    }

}
