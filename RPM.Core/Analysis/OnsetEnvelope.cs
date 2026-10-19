using RPM.Core.Audio;

namespace RPM.Core.Analysis;

/// <summary>
/// The onset strength envelope: one number per analysis frame that says how much new sound started
/// in that frame. Everything rhythmic downstream (tempo, the beat grid, the downbeat) is read from
/// this curve and not from the waveform, because a kick drum is clear in the spectrum's rate of
/// change and hard to see in its amplitude.
///
/// <para>This is spectral flux, computed in the standard way: log-power magnitude spectrum per frame,
/// half-wave rectified difference from the previous frame, summed across bins. The half-wave
/// rectification is important: only <i>rising</i> energy counts, so a note ending is not counted
/// as a note starting.</para>
/// </summary>
public static class OnsetEnvelope
{
    /// <summary>Analysis frame and hop, in samples at <see cref="TrackAnalyzer.AnalysisRate"/>.
    /// 1024/512 at 22050 Hz gives about 43 frames a second. This places a beat to
    /// within about 12 ms, and a five-minute track is only about 12,000 frames.</summary>
    public const int FrameSize = 1024;
    public const int HopSize = 512;

    /// <summary>Frames per second of the returned envelope. Use it to turn frame indices into
    /// seconds.</summary>
    public static double FrameRate(int sampleRate) => (double)sampleRate / HopSize;

    /// <summary>
    /// The time when a frame's onset happened. A frame is identified by where its window
    /// <i>starts</i>, but the Hann window gives most weight to the middle, so a transient is loudest
    /// in the frame where it is at the centre, which is half a frame after that frame's start. If the
    /// index is used directly, every beat is about 23 ms early, and the grid sits before the kick and
    /// not on it. A fractional frame is accepted, for a refined beat position.
    /// </summary>
    public static double FrameTime(double frame, int sampleRate) =>
        sampleRate <= 0 ? 0 : (frame * HopSize + FrameSize * 0.5) / sampleRate;

    /// <summary>
    /// Builds the envelope for a mono signal. The result is normalised to zero mean and unit
    /// standard deviation: the trackers downstream use the shape of the peaks, not how loud the
    /// record was mastered.
    /// </summary>
    public static float[] Compute(ReadOnlySpan<float> mono)
    {
        if (mono.Length < FrameSize)
            return [];

        int frames = 1 + (mono.Length - FrameSize) / HopSize;
        var window = Spectrum.BuildHann(FrameSize);
        var re = new double[FrameSize];
        var im = new double[FrameSize];
        int bins = FrameSize / 2;

        var previous = new double[bins];
        var flux = new float[frames];

        for (int f = 0; f < frames; f++)
        {
            int start = f * HopSize;
            for (int i = 0; i < FrameSize; i++)
            {
                re[i] = mono[start + i] * window[i];
                im[i] = 0;
            }
            Spectrum.Transform(re, im);

            double sum = 0;
            for (int b = 0; b < bins; b++)
            {
                // Log power matches how loudness is heard, and stops a loud bass bin from being the
                // only thing the flux shows.
                double magnitude = Math.Sqrt(re[b] * re[b] + im[b] * im[b]);
                double power = Math.Log(1 + 1000 * magnitude);
                double rise = power - previous[b];
                previous[b] = power;
                if (rise > 0)
                    sum += rise;
            }
            flux[f] = (float)sum;
        }

        // The first frame has no previous frame to rise from, so it would read as one huge onset. Set
        // it to the second frame's value, so the tempo estimator does not lock onto a false spike.
        if (frames > 1)
            flux[0] = flux[1];

        Normalise(flux);
        return flux;
    }

    /// <summary>Zero mean, unit deviation, then a floor at zero. The trackers need a curve whose
    /// peaks stand out from a flat baseline, not a signal centred on zero.</summary>
    private static void Normalise(float[] values)
    {
        if (values.Length == 0)
            return;

        double mean = 0;
        foreach (var v in values) mean += v;
        mean /= values.Length;

        double variance = 0;
        foreach (var v in values) variance += (v - mean) * (v - mean);
        double deviation = Math.Sqrt(variance / values.Length);
        if (deviation < 1e-9)
            deviation = 1;

        for (int i = 0; i < values.Length; i++)
            values[i] = (float)Math.Max(0, (values[i] - mean) / deviation);
    }
}
