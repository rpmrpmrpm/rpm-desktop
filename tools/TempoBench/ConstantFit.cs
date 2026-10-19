using RPM.Core.Analysis;

namespace TempoBench;

/// <summary>
/// One tempo for the whole record, fitted to the model's beats by least squares.
///
/// <para>A candidate to replace what <see cref="TempoWindows"/> does, and the baseline to compare
/// with. Records made in a sequencer have exactly one tempo, so a line through their beats is not an
/// approximation of the tempo, it <em>is</em> the tempo. A line is also the only fit whose error gets
/// smaller as the record gets longer. A piecewise fit uses its extra freedom to follow detection
/// noise, and every bend in it is drift that a changeover must carry.</para>
///
/// <para>In the benchmark, not in Core, because it is being measured, not shipped.</para>
/// </summary>
internal static class ConstantFit
{
    /// <summary>Beats closer together than this fraction of the period are one beat detected twice.
    /// The model marks peaks on a 20 ms grid and its picker only requires them to be three frames
    /// apart, so a pair 80 ms apart is normal output and is not two beats of any music.</summary>
    private const double SameBeatWithin = 0.6;

    /// <summary>A beat further than this from the line's position for it is not on the line. A
    /// quarter of a beat: beyond that, the grid is audibly wrong.</summary>
    private const double OutlierBeats = 0.25;

    /// <param name="Bpm">The fitted tempo, or 0 when there was not enough to fit.</param>
    /// <param name="Scatter">How far the beats are from the line, in beats, as the median absolute
    /// residual. This shows whether one tempo describes the record: near zero means a sequencer,
    /// above a tenth means the tempo changes.</param>
    /// <param name="OnTheLine">The fraction of beats within <see cref="OutlierBeats"/> of the line.</param>
    /// <param name="Merged">How many double detections were merged before the fit.</param>
    internal readonly record struct Result(double Bpm, double Scatter, double OnTheLine, int Merged);

    /// <summary>
    /// How precisely a tempo can be read, measured without a reference to compare with.
    ///
    /// <para>The two halves of a record made in a sequencer have exactly the same tempo. So if each
    /// half is fitted separately, the difference between them does not come from the record. It is
    /// the measurement's own noise, doubled, because each half has its own noise.</para>
    ///
    /// <para>This is the only way to measure precision on the music that matters. A tempo must be
    /// correct to about a twentieth of a percent for a beat-match to hold over a changeover. No
    /// annotated corpus of dance music exists, and the annotated corpora are thirty-second excerpts
    /// of music played by people, where the two halves really differ, so the result would measure
    /// the band, not this code.</para>
    ///
    /// <para>Returns the difference as a percentage of the tempo, or 0 when the record is too short
    /// to split. A record that really changes tempo appears here as noise, so use the median over a
    /// corpus, not any one track.</para>
    /// </summary>
    public static double SplitHalfPercent(BeatReading? reading)
    {
        if (reading is not { BeatTimes.Count: >= 32 })
            return 0;

        var beats = reading.BeatTimes;
        int middle = beats.Count / 2;

        var first = Of(new BeatReading
        {
            BeatTimes = [.. beats.Take(middle)],
            DownbeatTimes = [],
        });

        var second = Of(new BeatReading
        {
            BeatTimes = [.. beats.Skip(middle)],
            DownbeatTimes = [],
        });

        if (first.Bpm <= 0 || second.Bpm <= 0)
            return 0;

        // Put both at the same octave before comparing: a half read at another octave is a different
        // reading of the same tempo. That is a real fault, but not the one measured here.
        double a = first.Bpm, b = second.Bpm;
        while (a > b * 1.5) b *= 2;
        while (a < b / 1.5) b /= 2;

        return Math.Abs(a - b) / ((a + b) / 2) * 100;
    }

    public static Result Of(BeatReading? reading)
    {
        if (reading is null || reading.BeatTimes.Count < 8)
            return default;

        var beats = Merge(reading.BeatTimes, out int merged);
        if (beats.Count < 8)
            return default;

        double period = MedianGap(beats);
        if (period <= 0)
            return default;

        // Number the beats against the current period, fit a line to the numbering, and take the
        // new period from the line. Three passes: the numbering changes only while the period is
        // still changing, and the period settles almost at once on any record a line can describe.
        for (int pass = 0; pass < 3; pass++)
        {
            double fitted = FitPeriod(beats, period);
            if (fitted <= 0)
                return default;

            if (Math.Abs(fitted - period) < 1e-9)
            {
                period = fitted;
                break;
            }

            period = fitted;
        }

        var residuals = Residuals(beats, period, out double slope);
        if (slope <= 0)
            return default;

        residuals.Sort();
        double scatter = residuals[residuals.Count / 2];
        double onLine = (double)residuals.Count(r => r <= OutlierBeats) / residuals.Count;

        return new Result(60.0 / slope, scatter, onLine, merged);
    }

    /// <summary>Merges beats that the model detected twice into one beat, at their midpoint.</summary>
    private static List<double> Merge(IReadOnlyList<double> beats, out int merged)
    {
        merged = 0;
        double period = MedianGap(beats);
        if (period <= 0)
            return [.. beats];

        var kept = new List<double>(beats.Count) { beats[0] };
        for (int i = 1; i < beats.Count; i++)
        {
            if (beats[i] - kept[^1] < SameBeatWithin * period)
            {
                kept[^1] = (kept[^1] + beats[i]) / 2;
                merged++;
            }
            else
            {
                kept.Add(beats[i]);
            }
        }

        return kept;
    }

    private static double MedianGap(IReadOnlyList<double> beats)
    {
        if (beats.Count < 2)
            return 0;

        var gaps = new List<double>(beats.Count - 1);
        for (int i = 1; i < beats.Count; i++)
            if (beats[i] - beats[i - 1] > 0)
                gaps.Add(beats[i] - beats[i - 1]);

        if (gaps.Count == 0)
            return 0;

        gaps.Sort();
        return gaps[gaps.Count / 2];
    }

    /// <summary>The period of the least-squares line through the beats, numbered against
    /// <paramref name="period"/>. A gap of two periods is numbered as two, so a beat that the model
    /// missed does not change the result.</summary>
    private static double FitPeriod(List<double> beats, double period)
    {
        Number(beats, period, out var index);
        return Slope(beats, index);
    }

    private static void Number(List<double> beats, double period, out double[] index)
    {
        index = new double[beats.Count];
        for (int i = 1; i < beats.Count; i++)
        {
            double steps = Math.Round((beats[i] - beats[i - 1]) / period);
            index[i] = index[i - 1] + Math.Max(1, steps);
        }
    }

    private static double Slope(List<double> beats, double[] index)
    {
        double n = beats.Count;
        double mx = index.Average(), my = beats.Average();
        double cov = 0, var = 0;
        for (int i = 0; i < n; i++)
        {
            cov += (index[i] - mx) * (beats[i] - my);
            var += (index[i] - mx) * (index[i] - mx);
        }

        return var > 0 ? cov / var : 0;
    }

    /// <summary>How far each beat is from the fitted line, in beats.</summary>
    private static List<double> Residuals(List<double> beats, double period, out double slope)
    {
        Number(beats, period, out var index);
        slope = Slope(beats, index);
        if (slope <= 0)
            return [];

        double mx = index.Average(), my = beats.Average();
        double intercept = my - slope * mx;

        var residuals = new List<double>(beats.Count);
        for (int i = 0; i < beats.Count; i++)
            residuals.Add(Math.Abs(beats[i] - (intercept + slope * index[i])) / slope);

        return residuals;
    }
}
