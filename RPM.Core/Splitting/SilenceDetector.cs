namespace RPM.Core.Splitting;

/// <summary>A detected silence span. <see cref="MidSec"/> is the quietest point inside the
/// span, which is where a cut belongs. It is not the arithmetic middle.</summary>
public sealed record SilenceCandidate(double StartSec, double EndSec, double MidSec, double FloorDb);

public sealed record SilenceDetectorOptions
{
    /// <summary>RMS window length. At 50 ms, an 80-minute rip has under 100,000 envelope points.</summary>
    public double WindowSeconds { get; init; } = 0.05;

    /// <summary>Gaps shorter than this are musical rests, not track boundaries.</summary>
    public double MinSilenceSeconds { get; init; } = 0.8;

    /// <summary>Tracks shorter than this are merged into a neighbour. This removes false cuts.</summary>
    public double MinTrackSeconds { get; init; } = 60;

    /// <summary>Absolute ceiling for the silence threshold: quieter than this always counts.</summary>
    public double MaxThresholdDb { get; init; } = -40;

    /// <summary>Headroom added above the measured noise floor. Vinyl crackle is louder than digital
    /// silence.</summary>
    public double NoiseFloorHeadroomDb { get; init; } = 8;
}

/// <summary>
/// Track gap detection over an RMS dB envelope. There are two testable layers.
/// <see cref="EnvelopeAccumulator"/> folds streamed PCM into per-window RMS values. The static
/// analysis functions take an envelope and return candidates, with no side effects. The envelope
/// is scanned here, and ffmpeg's silencedetect output is not parsed. This lets the threshold adapt
/// to the recording's own noise floor. Vinyl surface noise is about -55 dB, not digital silence,
/// which is minus infinity.
/// </summary>
public static class SilenceDetector
{
    private const float MinDb = -100f;

    /// <summary>Streams PCM chunks into a per-window RMS dB envelope without ever holding the
    /// full sample data. Feed via <see cref="Add"/>, then read <see cref="ToDbEnvelope"/>.</summary>
    public sealed class EnvelopeAccumulator(int sampleRate, double windowSeconds = 0.05)
    {
        private readonly int _samplesPerWindow = Math.Max(1, (int)(sampleRate * windowSeconds));
        private readonly List<float> _windowsDb = new();
        private double _sumSquares;
        private int _count;

        public double WindowSeconds { get; } = windowSeconds;

        public void Add(ReadOnlySpan<float> chunk)
        {
            foreach (var sample in chunk)
            {
                _sumSquares += (double)sample * sample;
                if (++_count == _samplesPerWindow)
                    FlushWindow();
            }
        }

        private void FlushWindow()
        {
            double rms = Math.Sqrt(_sumSquares / _count);
            _windowsDb.Add(rms <= 0 ? MinDb : (float)Math.Max(MinDb, 20.0 * Math.Log10(rms)));
            _sumSquares = 0;
            _count = 0;
        }

        public float[] ToDbEnvelope()
        {
            if (_count > 0)
                FlushWindow();
            return _windowsDb.ToArray();
        }
    }

    /// <summary>Finds silence spans in a dB envelope using a threshold adapted to the
    /// envelope's own noise floor (5th percentile + headroom, capped at MaxThresholdDb).</summary>
    public static IReadOnlyList<SilenceCandidate> Detect(
        float[] envelopeDb, double windowSeconds, SilenceDetectorOptions options)
    {
        if (envelopeDb.Length == 0)
            return Array.Empty<SilenceCandidate>();

        double threshold = EffectiveThresholdDb(envelopeDb, options);
        int minWindows = Math.Max(1, (int)(options.MinSilenceSeconds / windowSeconds));

        var result = new List<SilenceCandidate>();
        int runStart = -1;
        for (int i = 0; i <= envelopeDb.Length; i++)
        {
            bool silent = i < envelopeDb.Length && envelopeDb[i] <= threshold;
            if (silent)
            {
                if (runStart < 0) runStart = i;
                continue;
            }
            if (runStart >= 0)
            {
                int runLen = i - runStart;
                if (runLen >= minWindows)
                    result.Add(MakeCandidate(envelopeDb, runStart, i, windowSeconds));
                runStart = -1;
            }
        }
        return result;
    }

    /// <summary>The adaptive silence threshold: noise floor (5th percentile) + headroom, never
    /// louder than the configured ceiling.</summary>
    public static double EffectiveThresholdDb(float[] envelopeDb, SilenceDetectorOptions options)
    {
        var sorted = (float[])envelopeDb.Clone();
        Array.Sort(sorted);
        double floor = sorted[(int)(sorted.Length * 0.05)];
        return Math.Min(options.MaxThresholdDb, floor + options.NoiseFloorHeadroomDb);
    }

    private static SilenceCandidate MakeCandidate(float[] env, int startWin, int endWinExcl, double windowSeconds)
    {
        // Cut at the quietest window of the span (ties resolve to the earliest).
        int quietest = startWin;
        for (int w = startWin; w < endWinExcl; w++)
            if (env[w] < env[quietest]) quietest = w;
        return new SilenceCandidate(
            StartSec: startWin * windowSeconds,
            EndSec: endWinExcl * windowSeconds,
            MidSec: (quietest + 0.5) * windowSeconds,
            FloorDb: env[quietest]);
    }

    /// <summary>Turns silence candidates into an unnamed provisional track list ("Track NN"),
    /// merging any segment shorter than <see cref="SilenceDetectorOptions.MinTrackSeconds"/>
    /// into its predecessor.</summary>
    public static IReadOnlyList<SplitTrack> ToProvisionalTracks(
        IReadOnlyList<SilenceCandidate> silences, double totalSeconds, SilenceDetectorOptions options)
    {
        var boundaries = new List<double> { 0 };
        foreach (var s in silences.OrderBy(s => s.MidSec))
        {
            // Silence at the start or the end is not a boundary between tracks.
            if (s.MidSec <= options.MinTrackSeconds || s.MidSec >= totalSeconds - options.MinTrackSeconds)
                continue;
            if (s.MidSec - boundaries[^1] >= options.MinTrackSeconds)
                boundaries.Add(s.MidSec);
        }
        boundaries.Add(totalSeconds);

        var tracks = new List<SplitTrack>(boundaries.Count - 1);
        for (int i = 0; i < boundaries.Count - 1; i++)
        {
            tracks.Add(new SplitTrack(
                boundaries[i], boundaries[i + 1],
                Title: null, Artist: null,
                TrackNumber: (uint)(i + 1),
                Confidence: 0.6,
                BoundarySource.Silence));
        }
        return tracks;
    }

    /// <summary>Moves a proposed boundary to the quietest envelope window within
    /// ±<paramref name="searchRadiusSec"/>. It is used to place duration-aligned (gapless) cuts.</summary>
    public static double SnapToLocalMinimum(
        float[] envelopeDb, double windowSeconds, double targetSec, double searchRadiusSec)
    {
        if (envelopeDb.Length == 0) return targetSec;
        int center = (int)(targetSec / windowSeconds);
        int radius = Math.Max(0, (int)(searchRadiusSec / windowSeconds));
        int from = Math.Clamp(center - radius, 0, envelopeDb.Length - 1);
        int to = Math.Clamp(center + radius, 0, envelopeDb.Length - 1);
        int best = from;
        for (int w = from; w <= to; w++)
            if (envelopeDb[w] < envelopeDb[best]) best = w;
        return (best + 0.5) * windowSeconds;
    }
}
