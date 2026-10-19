using RPM.Core.Analysis;

namespace RPM.Core.Tests;

/// <summary>
/// Holds the local tempo to the thing it is for: predicting where the beats will be over a whole
/// changeover, off a grid whose beats are each a frame or two from where the music is.
///
/// <para>The model reads beats on a twenty millisecond grid and scatters around the truth by about
/// that much. Every test here builds a record at an exact tempo, scatters its beats the way the
/// model does, and asks what tempo comes back, because that error, multiplied by the length of a
/// changeover, is the drift a beat-match has to survive.</para>
/// </summary>
public class LocalTempoSpanTests
{
    private const double Bpm = 128;
    private const int BeatsPerBar = 4;

    /// <summary>Sixteen milliseconds, which is the scatter of the beat model on the GTZAN
    /// data set. Drawn from a fixed sequence, so a failure is the same failure twice.</summary>
    private const double Scatter = 0.016;

    /// <summary>TransitionPlanner.MaxDriftSeconds, in milliseconds.</summary>
    private const double MaxDriftMs = 15;

    private static TrackAnalysis Scattered(int beats = 600)
    {
        double period = 60.0 / Bpm;
        var times = new List<double>(beats);

        // Offsets in whole frames, which is how the model is wrong: it cannot place a beat between
        // frames, so the error is quantised. Drawn from a fixed sequence rather than a repeating
        // pattern. A pattern that divides into the span being read cancels itself exactly and
        // gives a better result for any span that is a multiple of it.
        ulong seed = 0x9E3779B97F4A7C15;
        for (int i = 0; i < beats; i++)
        {
            seed = (seed * 6364136223846793005) + 1442695040888963407;
            double frames = (long)(seed >> 33) % 3 - 1;
            times.Add(i * period + frames * Scatter);
        }

        return new TrackAnalysis
        {
            FilePath = "/x.wav",
            DurationSeconds = beats * period,
            Bpm = Bpm,
            BpmConfidence = 1,
            BeatsPerBar = BeatsPerBar,
            BeatTimes = times,
        };
    }

    /// <summary>The drift a tempo error produces over one changeover, in milliseconds.</summary>
    private static double DriftMs(double readBpm, int bars) =>
        Math.Abs(readBpm - Bpm) / Bpm * bars * BeatsPerBar * (60.0 / Bpm) * 1000;

    /// <summary>
    /// The drift across the whole record rather than at one point on it.
    ///
    /// <para>A short span is not reliably wrong, it is unreliable: at some points on the record its
    /// two end beats happen to be scattered the same way and it reads the tempo exactly. Asking it
    /// once and finding it right proves nothing.</para>
    /// </summary>
    private static (double Median, double Worst) Across(TrackAnalysis analysis, int spanBeats, int bars)
    {
        var drifts = new List<double>();
        for (double at = 20; at < analysis.DurationSeconds - 20; at += 0.5)
            drifts.Add(DriftMs(analysis.BpmAt(at, spanBeats), bars));

        drifts.Sort();
        return (drifts[drifts.Count / 2], drifts[^1]);
    }

    [Fact]
    public void A_tempo_read_over_a_bar_cannot_carry_a_changeover()
    {
        // Measured: 206 ms at the middle of the record and 627 ms at worst, against a budget of 15.
        var (median, worst) = Across(Scattered(), BeatsPerBar, 16);

        Assert.True(median > 10 * MaxDriftMs, $"median {median:F0} ms");
        Assert.True(worst > 20 * MaxDriftMs, $"worst {worst:F0} ms");
    }

    [Fact]
    public void A_tempo_read_over_the_changeover_can()
    {
        // Measured: 2.9 ms at the middle and 13.7 at worst. Inside the budget even at its worst,
        // which is the whole claim.
        var (median, worst) = Across(Scattered(), 16 * BeatsPerBar, 16);

        Assert.True(median <= MaxDriftMs, $"median {median:F1} ms against a budget of {MaxDriftMs}");
        Assert.True(worst <= MaxDriftMs, $"worst {worst:F1} ms against a budget of {MaxDriftMs}");
    }

    [Fact]
    public void Every_doubling_of_the_span_roughly_halves_the_drift()
    {
        var analysis = Scattered();

        double bar = Across(analysis, BeatsPerBar, 16).Median;
        double four = Across(analysis, 4 * BeatsPerBar, 16).Median;
        double sixteen = Across(analysis, 16 * BeatsPerBar, 16).Median;

        Assert.True(four < bar / 2, $"{four:F1} against {bar:F1}");
        Assert.True(sixteen < four / 2, $"{sixteen:F1} against {four:F1}");
    }

    [Fact]
    public void A_record_that_changes_tempo_is_still_read_where_it_is_asked()
    {
        // Half at 120 and half at 140, so a reading taken in either half must be that half's tempo
        // and not the average of the record. This is the whole reason the reading is local, and a
        // longer span must not cost it.
        var times = new List<double>();
        double at = 0;
        for (int i = 0; i < 400; i++)
        {
            times.Add(at);
            at += 60.0 / (i < 200 ? 120 : 140);
        }

        var analysis = new TrackAnalysis
        {
            FilePath = "/x.wav",
            DurationSeconds = at,
            Bpm = 130,
            BpmConfidence = 1,
            BeatsPerBar = BeatsPerBar,
            BeatTimes = times,
        };

        Assert.Equal(120, analysis.BpmAt(times[60], 16 * BeatsPerBar), 0);
        Assert.Equal(140, analysis.BpmAt(times[340], 16 * BeatsPerBar), 0);
    }

    [Fact]
    public void A_grid_too_short_for_the_span_asked_for_is_read_over_what_there_is()
    {
        double period = 60.0 / Bpm;
        var analysis = new TrackAnalysis
        {
            FilePath = "/x.wav",
            DurationSeconds = 10 * period,
            Bpm = Bpm,
            BpmConfidence = 1,
            BeatsPerBar = BeatsPerBar,
            BeatTimes = [.. Enumerable.Range(0, 10).Select(i => i * period)],
        };

        Assert.Equal(Bpm, analysis.BpmAt(1, 16 * BeatsPerBar), 3);
    }
}
