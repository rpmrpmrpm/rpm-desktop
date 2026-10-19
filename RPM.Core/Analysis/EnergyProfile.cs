namespace RPM.Core.Analysis;

/// <summary>
/// The track's loudness over time, one reading every <see cref="StepSeconds"/>. Two classes use it:
/// the cue-point finder, which uses the shape to tell the intro from the body, and the set planner,
/// which uses the level to avoid sudden jumps between a very quiet track and a very loud one.
/// </summary>
public static class EnergyProfile
{
    /// <summary>Half a second per reading. It is short enough to show where a drop lands, and long
    /// enough that a five-minute track is only 600 numbers, which can be stored in the database
    /// as they are.</summary>
    public const double StepSeconds = 0.5;

    /// <summary>RMS per step, in 0..1 (linear amplitude, not dB). The consumers compare it with its
    /// own median, not with an absolute level.</summary>
    public static float[] Compute(ReadOnlySpan<float> mono, int sampleRate)
    {
        if (mono.Length == 0 || sampleRate <= 0)
            return [];

        int step = Math.Max(1, (int)(sampleRate * StepSeconds));
        int steps = Math.Max(1, mono.Length / step);
        var levels = new float[steps];

        for (int s = 0; s < steps; s++)
        {
            int start = s * step;
            int end = Math.Min(mono.Length, start + step);
            double sum = 0;
            for (int i = start; i < end; i++)
                sum += (double)mono[i] * mono[i];
            levels[s] = (float)Math.Sqrt(sum / Math.Max(1, end - start));
        }

        return levels;
    }

    /// <summary>
    /// The same reading taken on the bass only: everything under <paramref name="cutoffHz"/>.
    ///
    /// <para>This shows whether the <em>beat</em> has started, which is a different question from
    /// whether the track is loud. A spoken intro over claps is as loud as the song, so a full-band
    /// meter reads it as the body of the track. It has almost nothing below 200 Hz, because a kick
    /// drum is what puts energy there. For this reason cue points are found on this curve.</para>
    /// </summary>
    public static float[] ComputeLowBand(ReadOnlySpan<float> mono, int sampleRate, double cutoffHz = 200)
    {
        if (mono.Length == 0 || sampleRate <= 0)
            return [];

        // Second-order Butterworth low pass (RBJ cookbook, Q = 1/sqrt2), run twice to make the
        // cut-off steeper. One pass leaves too much of a clap's body in the reading, and a clap
        // could not then be told apart from a kick.
        double w0 = 2 * Math.PI * cutoffHz / sampleRate;
        double cos = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * 0.70710678);
        double a0 = 1 + alpha;
        double b0 = (1 - cos) / 2 / a0, b1 = (1 - cos) / a0, b2 = b0;
        double a1 = -2 * cos / a0, a2 = (1 - alpha) / a0;

        int step = Math.Max(1, (int)(sampleRate * StepSeconds));
        int steps = Math.Max(1, mono.Length / step);
        var levels = new float[steps];

        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;      // first pass
        double u1 = 0, u2 = 0, v1 = 0, v2 = 0;      // second pass
        for (int s = 0; s < steps; s++)
        {
            int start = s * step;
            int end = Math.Min(mono.Length, start + step);
            double sum = 0;
            for (int i = start; i < end; i++)
            {
                double x = mono[i];
                double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
                x2 = x1; x1 = x; y2 = y1; y1 = y;

                double v = b0 * y + b1 * u1 + b2 * u2 - a1 * v1 - a2 * v2;
                u2 = u1; u1 = y; v2 = v1; v1 = v;

                sum += v * v;
            }
            levels[s] = (float)Math.Sqrt(sum / Math.Max(1, end - start));
        }

        return levels;
    }

    /// <summary>
    /// The track's loudness over time, split into the three ranges that a mix is made of, plus how
    /// much of it is in the range of a voice.
    /// </summary>
    /// <param name="Full">The whole signal, the same as <see cref="Compute"/> returns.</param>
    /// <param name="Bass">Under <see cref="BassHz"/>: the kick and the bassline. A bass swap takes
    /// this range out of the outgoing track and gives it to the incoming one. It also shows whether
    /// the beat has started at all.</param>
    /// <param name="Mid">Between <see cref="BassHz"/> and <see cref="TopHz"/>: where a voice, a
    /// guitar and the body of nearly every instrument sit.</param>
    /// <param name="Top">Above <see cref="TopHz"/>: hats and air, the part that stays audible
    /// longest in a fade.</param>
    /// <param name="Vocal">The mid range's share of the whole, 0..1. It is high where something
    /// holds the middle of the track, usually a voice. It is not a singer detector, but it is enough
    /// to stop a mix from putting two voices on top of each other.</param>
    public readonly record struct Bands(
        float[] Full, float[] Bass, float[] Mid, float[] Top, float[] Vocal);

    /// <summary>Where the bass range stops. A kick drum's body is under this; a clap's is not.</summary>
    public const double BassHz = 200;

    /// <summary>Where the top range begins. Above it are hats and air, and almost nothing a person
    /// would hum.</summary>
    public const double TopHz = 4000;

    /// <summary>
    /// All four curves in one pass over the samples.
    ///
    /// <para>It uses two filter chains, not four. Everything under 200 Hz and everything under 4 kHz
    /// are measured, and the other two ranges are the differences between those and the whole signal.
    /// The energy of separate bands adds in the square, so subtracting under the root is exact enough
    /// for a reading that is compared with its own median. It costs nothing, while two more cascaded
    /// biquads would cost a pass each.</para>
    ///
    /// <para>One call is cheaper than calling <see cref="Compute"/> and <see cref="ComputeLowBand"/>
    /// one after the other, which reads the track twice to get two of the four curves.</para>
    /// </summary>
    public static Bands ComputeBands(ReadOnlySpan<float> mono, int sampleRate)
    {
        if (mono.Length == 0 || sampleRate <= 0)
            return new Bands([], [], [], [], []);

        var bass = new Biquad(BassHz, sampleRate);
        var top = new Biquad(TopHz, sampleRate);

        int step = Math.Max(1, (int)(sampleRate * StepSeconds));
        int steps = Math.Max(1, mono.Length / step);

        var full = new float[steps];
        var bassLevels = new float[steps];
        var midLevels = new float[steps];
        var topLevels = new float[steps];
        var vocal = new float[steps];

        for (int s = 0; s < steps; s++)
        {
            int start = s * step;
            int end = Math.Min(mono.Length, start + step);
            double sumFull = 0, sumBass = 0, sumUnderTop = 0;

            for (int i = start; i < end; i++)
            {
                double x = mono[i];
                double low = bass.Next(x);
                double underTop = top.Next(x);

                sumFull += x * x;
                sumBass += low * low;
                sumUnderTop += underTop * underTop;
            }

            int count = Math.Max(1, end - start);
            double rmsFull = Math.Sqrt(sumFull / count);
            double rmsBass = Math.Sqrt(sumBass / count);
            double mid = Math.Sqrt(Math.Max(0, sumUnderTop - sumBass) / count);
            double high = Math.Sqrt(Math.Max(0, sumFull - sumUnderTop) / count);

            full[s] = (float)rmsFull;
            bassLevels[s] = (float)rmsBass;
            midLevels[s] = (float)mid;
            topLevels[s] = (float)high;

            // A share, not a level: a quiet passage carried by a voice counts as vocal, and a loud
            // passage of only kick and hats does not.
            vocal[s] = rmsFull > 1e-6 ? (float)Math.Clamp(mid / rmsFull, 0, 1) : 0;
        }

        return new Bands(full, bassLevels, midLevels, topLevels, vocal);
    }

    /// <summary>The level that the track spends most of its time at: its "body" loudness. It is the
    /// median, not the mean, so a long quiet intro or a fade-out does not lower it.</summary>
    public static double Median(ReadOnlySpan<float> levels)
    {
        if (levels.Length == 0)
            return 0;
        var sorted = levels.ToArray();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

    /// <summary>
    /// One number for how hard the track hits, 0..1: the body loudness rescaled so that ordinary
    /// music covers most of the range. The planner sorts on it when it builds an energy arc.
    /// </summary>
    public static double Intensity(ReadOnlySpan<float> levels) => Rescale(Median(levels));

    /// <summary>
    /// The same 0..1 scale applied to one loudness reading. -36 dBFS gives 0 and -6 dBFS gives 1.
    /// Below the floor is a very quiet track and above the top is a very loud master. Both values
    /// are clamped.
    /// </summary>
    public static double Rescale(double rms)
    {
        if (rms <= 0)
            return 0;
        double db = 20 * Math.Log10(rms);
        return Math.Clamp((db + 36) / 30, 0, 1);
    }
}
