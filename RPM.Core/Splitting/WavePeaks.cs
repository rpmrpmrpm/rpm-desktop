namespace RPM.Core.Splitting;

/// <summary>
/// The shape of a recording: the lowest and highest sample in each short slice of it.
///
/// <para>The RMS envelope the detector works from is one number every 50 ms. That is enough to find a
/// gap, but far too coarse to <i>look</i> at. Placing a cut by eye means seeing where a note stops.
/// At twenty points a second, the last second of a track would be twenty stair-steps. Peaks are
/// taken from the same decode pass at <see cref="DefaultBucketSeconds"/>. That is fine enough that
/// the waveform is still a waveform when the editor is zoomed in to a couple of seconds across.</para>
///
/// <para>Min and max are kept apart rather than folded into one magnitude. A waveform drawn from
/// absolute values is a solid blob. A rectified one misleads when the signal is not symmetric.
/// Two float arrays cost 8 bytes per bucket. An 80-minute side costs about 7 MB, which is the price
/// of being able to see the record.</para>
/// </summary>
public sealed class WavePeaks
{
    /// <summary>5 ms per bucket, which is 200 buckets a second. At the editor's tightest zoom (half a
    /// second across a wide window) there is still less than one bucket per pixel.</summary>
    public const double DefaultBucketSeconds = 0.005;

    private readonly float[] _min;
    private readonly float[] _max;

    public WavePeaks(float[] min, float[] max, double bucketSeconds)
    {
        ArgumentNullException.ThrowIfNull(min);
        ArgumentNullException.ThrowIfNull(max);
        if (min.Length != max.Length)
            throw new ArgumentException("peak arrays must be the same length", nameof(max));
        if (bucketSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(bucketSeconds));

        _min = min;
        _max = max;
        BucketSeconds = bucketSeconds;
    }

    public double BucketSeconds { get; }

    public int Count => _min.Length;

    public double TotalSeconds => Count * BucketSeconds;

    /// <summary>
    /// The extremes over <c>[fromSeconds, toSeconds)</c>: one column of the drawn waveform.
    /// Returns false when the span is outside the recording or holds no bucket. The caller then draws
    /// nothing, not a line at zero.
    /// </summary>
    public bool Range(double fromSeconds, double toSeconds, out float min, out float max)
    {
        min = 0;
        max = 0;
        if (Count == 0 || toSeconds <= fromSeconds)
            return false;

        int from = (int)Math.Floor(fromSeconds / BucketSeconds);
        int to = (int)Math.Ceiling(toSeconds / BucketSeconds);
        if (to <= 0 || from >= Count)
            return false;

        from = Math.Max(0, from);
        to = Math.Min(Count, Math.Max(from + 1, to));

        float lo = _min[from], hi = _max[from];
        for (int i = from + 1; i < to; i++)
        {
            if (_min[i] < lo) lo = _min[i];
            if (_max[i] > hi) hi = _max[i];
        }
        min = lo;
        max = hi;
        return true;
    }

    /// <summary>Folds streamed PCM into buckets without holding the samples. It works the same way as
    /// <see cref="SilenceDetector.EnvelopeAccumulator"/>, and it is fed from the same chunks.</summary>
    public sealed class Accumulator
    {
        private readonly int _samplesPerBucket;
        private readonly List<float> _min = new();
        private readonly List<float> _max = new();
        private float _lo = float.MaxValue;
        private float _hi = float.MinValue;
        private int _count;

        public Accumulator(int sampleRate, double bucketSeconds = DefaultBucketSeconds)
        {
            _samplesPerBucket = Math.Max(1, (int)(sampleRate * bucketSeconds));
            BucketSeconds = (double)_samplesPerBucket / Math.Max(1, sampleRate);
        }

        /// <summary>The bucket length actually used, in whole samples. It can differ slightly from the
        /// value that was asked for. The drawn waveform reads this value and does not assume it.</summary>
        public double BucketSeconds { get; }

        public void Add(ReadOnlySpan<float> chunk)
        {
            foreach (var sample in chunk)
            {
                if (sample < _lo) _lo = sample;
                if (sample > _hi) _hi = sample;
                if (++_count == _samplesPerBucket)
                    Flush();
            }
        }

        private void Flush()
        {
            _min.Add(_lo);
            _max.Add(_hi);
            _lo = float.MaxValue;
            _hi = float.MinValue;
            _count = 0;
        }

        public WavePeaks ToPeaks()
        {
            if (_count > 0)
                Flush();
            return new WavePeaks(_min.ToArray(), _max.ToArray(), BucketSeconds);
        }
    }
}
