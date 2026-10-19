using RPM.Core.Audio;

namespace RPM.Core.Analysis;

/// <summary>
/// Folds the spectrum onto the twelve pitch classes: how much C is in this track, how much C#, and
/// so on, with every octave counted together. This fold keeps the key information and ignores the
/// arrangement. The same chord played by a bass, a pad and a lead falls into the same twelve
/// bins, so the profile describes the harmony and not the instruments.
///
/// <para>This is not the Chromaprint of <see cref="RPM.Fingerprint"/>. That runs fpcalc to get
/// AcoustID-compatible sub-fingerprints, which are hashes and cannot be read as pitch. This class
/// computes a plain twelve-bin chromagram, so the key detector has real pitch values.</para>
/// </summary>
public static class ChromaAnalyzer
{
    /// <summary>A longer window than the onset envelope uses, because pitch needs frequency
    /// resolution. At 22050 Hz a 4096-point frame resolves about 5.4 Hz, enough to separate
    /// semitones down to about C2. The hop is a quarter of the frame, because chords last longer
    /// than beats.</summary>
    public const int FrameSize = 4096;
    public const int HopSize = 1024;

    /// <summary>The lowest and highest pitch counted. Below about 65 Hz the bins are too coarse to
    /// place a semitone. Above about 2 kHz the content is overtones and noise, which only blur the
    /// profile.</summary>
    private const double MinHz = 65.4, MaxHz = 2093.0;

    /// <summary>Frames per second of <see cref="ComputeFrames"/>, for matching its rows to
    /// anything measured in seconds.</summary>
    public static double FrameRate(int sampleRate) => (double)sampleRate / HopSize;

    /// <summary>
    /// The track's average chroma: twelve non-negative weights, normalised so the largest is 1.
    /// Frames quieter than the track's own noise floor are skipped, so a long fade-out does not pull
    /// the profile towards the hum under it.
    /// </summary>
    public static double[] Compute(ReadOnlySpan<float> mono, int sampleRate) =>
        Average(ComputeFrames(mono, sampleRate));

    /// <summary>Sums a chromagram into one profile and normalises it. Loud frames count for more,
    /// which suits a key detector: the chorus decides the key, not the intro.</summary>
    public static double[] Average(IReadOnlyList<double[]> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var chroma = new double[12];
        foreach (var frame in frames)
            for (int i = 0; i < 12; i++)
                chroma[i] += frame[i];

        double max = 0;
        foreach (var v in chroma) max = Math.Max(max, v);
        if (max > 0)
            for (int i = 0; i < 12; i++)
                chroma[i] /= max;
        return chroma;
    }

    /// <summary>
    /// The chromagram itself: one row of twelve raw (unnormalised) weights per frame. Silence is a
    /// row of zeroes, so the row index always maps to a time.
    ///
    /// <para>The downbeat detector reads it row by row. Bar lines are where the harmony changes. In
    /// four-on-the-floor dance music that is the <em>only</em> thing that marks them: every beat has
    /// a kick, so the strength of the beats cannot tell the first beat from the third.</para>
    /// </summary>
    public static double[][] ComputeFrames(ReadOnlySpan<float> mono, int sampleRate)
    {
        if (mono.Length < FrameSize || sampleRate <= 0)
            return [];

        int frames = 1 + (mono.Length - FrameSize) / HopSize;
        var rows = new double[frames][];
        var window = Spectrum.BuildHann(FrameSize);
        var re = new double[FrameSize];
        var im = new double[FrameSize];
        int bins = FrameSize / 2;
        double binHz = (double)sampleRate / FrameSize;

        // The pitch class of each bin, calculated once: bin to semitones from A440 to class.
        var binClass = new int[bins];
        for (int b = 0; b < bins; b++)
        {
            double hz = b * binHz;
            if (hz < MinHz || hz > MaxHz)
            {
                binClass[b] = -1;
                continue;
            }
            // 9 = A's pitch class (C = 0), so a bin at 440 Hz lands on A.
            double semitones = 12 * Math.Log2(hz / 440.0) + 9;
            int pitchClass = (int)Math.Round(semitones) % 12;
            if (pitchClass < 0) pitchClass += 12;
            binClass[b] = pitchClass;
        }

        for (int f = 0; f < frames; f++)
        {
            var chroma = new double[12];
            rows[f] = chroma;

            int start = f * HopSize;
            double energy = 0;
            for (int i = 0; i < FrameSize; i++)
            {
                var sample = mono[start + i];
                energy += sample * sample;
                re[i] = sample * window[i];
                im[i] = 0;
            }

            // Near-silence has no harmony worth adding to the average.
            if (Math.Sqrt(energy / FrameSize) < 1e-4)
                continue;

            Spectrum.Transform(re, im);

            for (int b = 1; b < bins; b++)
            {
                int pitchClass = binClass[b];
                if (pitchClass < 0)
                    continue;
                double magnitude = Math.Sqrt(re[b] * re[b] + im[b] * im[b]);
                chroma[pitchClass] += magnitude;
            }
        }

        return rows;
    }
}
