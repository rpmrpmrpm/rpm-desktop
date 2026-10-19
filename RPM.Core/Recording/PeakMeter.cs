namespace RPM.Core.Recording;

/// <summary>Instantaneous level of one channel over a block of samples, in dBFS. <see cref="Clipped"/>
/// latches when any sample in the block reached full scale.</summary>
public readonly record struct ChannelLevel(float PeakDb, float RmsDb, bool Clipped)
{
    public static readonly ChannelLevel Silent = new(PeakMeter.MinDb, PeakMeter.MinDb, false);
}

/// <summary>Per-channel levels of one PCM block, which is the reading a peak meter draws.</summary>
public sealed record MeterFrame(IReadOnlyList<ChannelLevel> Channels)
{
    public static MeterFrame Silent(int channels) =>
        new(Enumerable.Repeat(ChannelLevel.Silent, channels).ToArray());
}

/// <summary>
/// Peak/RMS metering of interleaved float PCM, in dBFS (0 dB = full scale). Pure and stateless so it
/// can be unit-tested directly; the live meter feeds it capture blocks and pairs it with a
/// <see cref="PeakHold"/> for the falling peak indicator. Uses the same 20·log10 dBFS convention as
/// <c>SilenceDetector</c>.
/// </summary>
public static class PeakMeter
{
    /// <summary>Floor returned for digital silence (avoids -inf).</summary>
    public const float MinDb = -100f;

    /// <summary>A sample at or above this magnitude counts as clipping. Just shy of full scale so a run
    /// of maxed samples (the shape of real clipping) is caught without flagging a lone peak.</summary>
    public const float ClipCeiling = 0.999f;

    /// <summary>dBFS for a linear amplitude in 0..1 (magnitude). Clamped to
    /// <see cref="MinDb"/>.</summary>
    public static float ToDb(double amplitude) =>
        amplitude <= 0 ? MinDb : (float)Math.Max(MinDb, 20.0 * Math.Log10(amplitude));

    /// <summary>Measures peak, RMS and clipping for each channel of an interleaved float
    /// block.</summary>
    /// <param name="interleaved">Interleaved samples; length should be a multiple of
    /// <paramref name="channels"/>.</param>
    /// <param name="channels">Channel count (1 mono, 2 stereo).</param>
    public static MeterFrame Measure(ReadOnlySpan<float> interleaved, int channels)
    {
        if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));

        Span<double> peak = channels <= 8 ? stackalloc double[channels] : new double[channels];
        Span<double> sumSq = channels <= 8 ? stackalloc double[channels] : new double[channels];
        Span<bool> clip = channels <= 8 ? stackalloc bool[channels] : new bool[channels];
        peak.Clear(); sumSq.Clear(); clip.Clear();

        int frames = interleaved.Length / channels;
        for (int f = 0; f < frames; f++)
        {
            int baseIdx = f * channels;
            for (int c = 0; c < channels; c++)
            {
                float s = interleaved[baseIdx + c];
                double mag = Math.Abs(s);
                if (mag > peak[c]) peak[c] = mag;
                sumSq[c] += (double)s * s;
                if (mag >= ClipCeiling) clip[c] = true;
            }
        }

        var result = new ChannelLevel[channels];
        for (int c = 0; c < channels; c++)
        {
            double rms = frames > 0 ? Math.Sqrt(sumSq[c] / frames) : 0;
            result[c] = new ChannelLevel(ToDb(peak[c]), ToDb(rms), clip[c]);
        }
        return new MeterFrame(result);
    }
}

/// <summary>The falling "peak hold" marker on a meter: jumps up instantly to a new peak, then decays at
/// a fixed dB/second. Time is passed in so it stays deterministic and testable.</summary>
public sealed class PeakHold(double decayDbPerSecond = 12.0)
{
    private float _holdDb = PeakMeter.MinDb;

    public float ValueDb => _holdDb;

    /// <summary>Feeds a fresh peak reading and advances the decay by
    /// <paramref name="elapsedSeconds"/>.</summary>
    public float Update(float peakDb, double elapsedSeconds)
    {
        float decayed = _holdDb - (float)(decayDbPerSecond * Math.Max(0, elapsedSeconds));
        _holdDb = Math.Max(peakDb, decayed);
        if (_holdDb < PeakMeter.MinDb) _holdDb = PeakMeter.MinDb;
        return _holdDb;
    }

    public void Reset() => _holdDb = PeakMeter.MinDb;
}
