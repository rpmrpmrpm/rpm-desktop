namespace RPM.Core.Audio;

/// <summary>
/// Winamp-style 10-band equaliser: a cascade of RBJ peaking biquads per channel plus a pre-amp,
/// applied to interleaved float PCM in place. Gains can be changed at any time. The new
/// coefficients take effect on the next block, so nothing needs to restart.
///
/// <para>Two shelves are added at the end of the same cascade: bass and treble, the tone controls of
/// an amplifier. They are a separate pair and not a rewrite of the two end bands. The bands form a
/// curve that the listener draws, while the shelves change the whole bottom or the whole top of the
/// spectrum. A preset that sets the bands must not move the shelves.</para>
///
/// <para><b>Why a shelf and not another band.</b> A peaking filter is a bump: its response returns to
/// 0 dB on both sides of the centre, so the 60 Hz band lifts the sound around 60 Hz and lets 30 Hz
/// fall away again. A low shelf keeps its gain all the way down. That is the difference between an
/// equaliser and a tone control, and it is why the shelves exist.</para>
/// </summary>
public sealed class Equalizer
{
    /// <summary>Centre frequencies in Hz, the same ten bands as Winamp.</summary>
    public static readonly int[] Bands = { 60, 170, 310, 600, 1000, 3000, 6000, 12000, 14000, 16000 };

    /// <summary>Half-gain frequency of the bass shelf: the frequency at which it has reached half of
    /// its dB gain. It is 250 Hz, the same as Audacity's Bass and Treble effect.</summary>
    public const double BassHz = 250;

    /// <summary>Half-gain frequency of the treble shelf. It is 4 kHz, also the same as Audacity.</summary>
    public const double TrebleHz = 4000;

    /// <summary>Stages in the cascade: a biquad per band, then the bass shelf, then the treble
    /// one.</summary>
    private static readonly int Stages = Bands.Length + 2;
    private static readonly int BassStage = Bands.Length;
    private static readonly int TrebleStage = Bands.Length + 1;

    private const double Q = 1.0;               // ~1 octave per band
    private const double MaxGainDb = 12;

    private readonly int _sampleRate;
    private readonly int _channels;

    // coefficients per band (shared across channels)
    private readonly double[] _b0, _b1, _b2, _a1, _a2;
    // transposed-direct-form-II state per band per channel
    private readonly double[] _z1, _z2;

    private double _preampGain = 1.0;           // linear
    private volatile bool _bypass = true;       // flat + 0 dB pre-amp: skip the maths entirely

    public Equalizer(int sampleRate, int channels)
    {
        if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channels));

        _sampleRate = sampleRate;
        _channels = channels;

        int n = Stages;
        _b0 = new double[n]; _b1 = new double[n]; _b2 = new double[n];
        _a1 = new double[n]; _a2 = new double[n];
        _z1 = new double[n * channels];
        _z2 = new double[n * channels];

        SetGains(0, new double[Bands.Length], 0, 0);
    }

    /// <summary>True when the EQ is flat and the pre-amp is 0 dB (processing is a no-op).</summary>
    public bool IsBypassed => _bypass;

    /// <summary>
    /// Sets the pre-amp, each band's gain, and the two tone shelves (all dB, all clamped to ±12).
    /// Safe to call while audio is being processed. It takes effect on the next block.
    /// </summary>
    /// <param name="bassDb">The low shelf, <see cref="BassHz"/>. Zero means no shelf.</param>
    /// <param name="trebleDb">The high shelf, <see cref="TrebleHz"/>. Zero means no shelf.</param>
    /// <remarks>
    /// The shelves have no default value, so every caller must state them, including callers that
    /// want no tone shaping. With a default, a caller that forgot the argument would silently remove
    /// the tone settings of the listener. Making the argument required prevents that mistake.
    /// </remarks>
    public void SetGains(
        double preampDb, IReadOnlyList<double> bandGainsDb, double bassDb, double trebleDb)
    {
        ArgumentNullException.ThrowIfNull(bandGainsDb);

        bool flat = Math.Abs(preampDb) < 0.01;
        _preampGain = Math.Pow(10, Math.Clamp(preampDb, -MaxGainDb, MaxGainDb) / 20);

        for (int i = 0; i < Bands.Length; i++)
        {
            double gainDb = i < bandGainsDb.Count ? Math.Clamp(bandGainsDb[i], -MaxGainDb, MaxGainDb) : 0;
            if (Math.Abs(gainDb) >= 0.01) flat = false;
            SetPeaking(i, Bands[i], gainDb);
        }

        double bass = Math.Clamp(bassDb, -MaxGainDb, MaxGainDb);
        double treble = Math.Clamp(trebleDb, -MaxGainDb, MaxGainDb);
        if (Math.Abs(bass) >= 0.01 || Math.Abs(treble) >= 0.01) flat = false;
        SetShelf(BassStage, BassHz, bass, low: true);
        SetShelf(TrebleStage, TrebleHz, treble, low: false);

        _bypass = flat;
    }

    /// <summary>RBJ audio-EQ-cookbook peaking filter.</summary>
    private void SetPeaking(int band, double centreHz, double gainDb)
    {
        double a = Math.Pow(10, gainDb / 40);
        double w0 = 2 * Math.PI * centreHz / _sampleRate;
        double cos = Math.Cos(w0);
        double alpha = Math.Sin(w0) / (2 * Q);

        double b0 = 1 + alpha * a;
        double b1 = -2 * cos;
        double b2 = 1 - alpha * a;
        double a0 = 1 + alpha / a;
        double a1 = -2 * cos;
        double a2 = 1 - alpha / a;

        _b0[band] = b0 / a0;
        _b1[band] = b1 / a0;
        _b2[band] = b2 / a0;
        _a1[band] = a1 / a0;
        _a2[band] = a2 / a0;
    }

    /// <summary>
    /// RBJ audio-EQ-cookbook shelving filter, low or high.
    ///
    /// <para><paramref name="cornerHz"/> is the cookbook's w0, which for a shelf is the half-gain
    /// frequency: where the shelf has reached half of its dB gain, not where it starts or ends. This
    /// is the same definition that Audacity uses, so the same number gives the same result.</para>
    ///
    /// <para>The shelf slope S is 1, the steepest the cookbook allows without the response
    /// overshooting before it settles. At S = 1 the alpha term simplifies to sin(w0)/sqrt(2), so the
    /// general form is not written out here.</para>
    /// </summary>
    private void SetShelf(int stage, double cornerHz, double gainDb, bool low)
    {
        double a = Math.Pow(10, gainDb / 40);
        double w0 = 2 * Math.PI * cornerHz / _sampleRate;
        double cos = Math.Cos(w0);
        double alpha = Math.Sin(w0) / Math.Sqrt(2);
        double twoSqrtA = 2 * Math.Sqrt(a) * alpha;
        double ap1 = a + 1, am1 = a - 1;

        double b0, b1, b2, a0, a1, a2;
        if (low)
        {
            b0 = a * (ap1 - am1 * cos + twoSqrtA);
            b1 = 2 * a * (am1 - ap1 * cos);
            b2 = a * (ap1 - am1 * cos - twoSqrtA);
            a0 = ap1 + am1 * cos + twoSqrtA;
            a1 = -2 * (am1 + ap1 * cos);
            a2 = ap1 + am1 * cos - twoSqrtA;
        }
        else
        {
            b0 = a * (ap1 + am1 * cos + twoSqrtA);
            b1 = -2 * a * (am1 + ap1 * cos);
            b2 = a * (ap1 + am1 * cos - twoSqrtA);
            a0 = ap1 - am1 * cos + twoSqrtA;
            a1 = 2 * (am1 - ap1 * cos);
            a2 = ap1 - am1 * cos - twoSqrtA;
        }

        _b0[stage] = b0 / a0;
        _b1[stage] = b1 / a0;
        _b2[stage] = b2 / a0;
        _a1[stage] = a1 / a0;
        _a2[stage] = a2 / a0;
    }

    /// <summary>Clears the filter memory (after a seek or track change, so the tail of the previous
    /// audio cannot carry over into the new audio).</summary>
    public void Reset()
    {
        Array.Clear(_z1);
        Array.Clear(_z2);
    }

    /// <summary>Filters <paramref name="frameCount"/> interleaved frames in place.</summary>
    public void Process(float[] interleaved, int frameCount)
    {
        ArgumentNullException.ThrowIfNull(interleaved);
        if (_bypass) return;

        int stages = Stages;
        int samples = frameCount * _channels;
        if (samples > interleaved.Length) throw new ArgumentOutOfRangeException(nameof(frameCount));

        for (int i = 0; i < samples; i++)
        {
            int ch = _channels == 1 ? 0 : i & 1;
            double x = interleaved[i];

            for (int b = 0; b < stages; b++)
            {
                int s = b * _channels + ch;
                double y = _b0[b] * x + _z1[s];
                _z1[s] = _b1[b] * x - _a1[b] * y + _z2[s];
                _z2[s] = _b2[b] * x - _a2[b] * y;
                x = y;
            }

            x *= _preampGain;
            interleaved[i] = (float)Math.Clamp(x, -1.0, 1.0);   // guard the output stage
        }
    }
}
