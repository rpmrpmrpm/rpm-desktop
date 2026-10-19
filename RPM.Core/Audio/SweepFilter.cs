namespace RPM.Core.Audio;

/// <summary>A low pass on one deck's output, swept down as a record is rolled out.
///
/// <para>The move it is for is the one every DJ makes on the way out of a track. As the loop runs and the
/// fader comes down, the top of the record is taken away first and the bottom last. What is left at the end
/// is a kick drum with nothing under it. Everybody in the room knows a record is coming before it arrives,
/// and that expectation is most of what makes the next one land.</para>
///
/// <para>Two RBJ cookbook low passes run in series (12 dB per octave each, 24 dB together). There is one
/// pair of delay lines per channel, so the stereo image is kept. When the cutoff is open (<see
/// cref="OpenHz"/> and above), the filter is skipped entirely. So a deck that is not being rolled out costs
/// nothing.</para>
/// </summary>
public sealed class SweepFilter
{
    /// <summary>At or above this, the filter is out of the way. Nothing anybody can hear is above it, so
    /// the deck is returned untouched instead of being run through a pass-through.</summary>
    public const double OpenHz = 18000;

    /// <summary>Where the sweep ends. This is low enough that only the kick and the bassline are left. It
    /// is not so low that the record disappears before the fader has finished.</summary>
    public const double ClosedHz = 160;

    private readonly int _sampleRate;
    private readonly int _channels;

    // Two stages, per channel: [stage][channel].
    private readonly double[,] _x1, _x2, _y1, _y2;
    private double _b0 = 1, _b1, _b2, _a1, _a2;
    private double _hz = double.NaN;

    public SweepFilter(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _channels = Math.Max(1, channels);
        _x1 = new double[2, _channels];
        _x2 = new double[2, _channels];
        _y1 = new double[2, _channels];
        _y2 = new double[2, _channels];
        SetCutoff(OpenHz);
    }

    /// <summary>The point where a high pass is out of the way. 20 Hz is below the lowest note of any
    /// record, and below most speakers. A sweep parked here does nothing anybody can hear. This is the
    /// high-pass equivalent of <see cref="OpenHz"/> for the low pass.
    /// </summary>
    public const double FlatHz = 20;

    /// <summary>Where a high pass sweep ends. At 2 kHz only the hi-hats and the top of the vocal are left.
    /// Past this point the filter stops taking the record away, and starts to make a noise of its own.
    /// </summary>
    public const double HighestHz = 2000;

    /// <summary>The current cutoff, in Hz. For a low pass, <see cref="OpenHz"/> means out of the way. For a
    /// high pass, <see cref="FlatHz"/> means out of the way.</summary>
    public double CutoffHz => _hz;

    /// <summary>Which way round the filter is. False is the low pass that every roll-out uses. True is the
    /// high pass that a filter fade climbs with.</summary>
    public bool IsHighPass => _high;

    private bool _high;

    /// <summary>True while the filter is doing anything at all.</summary>
    public bool IsClosing => _high ? _hz > FlatHz + 1 : _hz < OpenHz - 1;

    /// <summary>The cutoff of the low pass, in Hz. It is cheap enough to call once per block, and the
    /// sweep is driven this way. It does nothing when the value has not moved far enough to matter.
    /// </summary>
    public void SetCutoff(double hz) => Set(Math.Clamp(hz, ClosedHz, OpenHz), high: false);

    /// <summary>The corner of the high pass, in Hz. This is the other direction, used for the filter fade.
    /// The record is brought in from underneath, rather than closed down over. <see cref="FlatHz"/> means
    /// out of the way.
    /// </summary>
    public void SetHighPass(double hz) => Set(Math.Clamp(hz, FlatHz, HighestHz), high: true);

    private void Set(double hz, bool high)
    {
        if (Math.Abs(hz - _hz) < 1 && high == _high)
            return;

        _hz = hz;
        _high = high;

        double w0 = 2 * Math.PI * hz / _sampleRate;
        double cos = Math.Cos(w0), sin = Math.Sin(w0);

        // Butterworth Q for a single stage. Two of these in series ring a little at the corner, which is
        // what a filter sweep should sound like.
        double alpha = sin / (2 * 0.7071);

        // The RBJ cookbook's two forms differ only in the numerator: the same corner, the same Q, and the
        // same pair of poles. Either the part below the corner or the part above it is kept.
        double b0 = high ? (1 + cos) / 2 : (1 - cos) / 2;
        double b1 = high ? -(1 + cos) : 1 - cos;
        double b2 = b0;
        double a0 = 1 + alpha, a1 = -2 * cos, a2 = 1 - alpha;

        _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0;
        _a1 = a1 / a0; _a2 = a2 / a0;
    }

    /// <summary>Clears the delay lines. This is used when a deck takes a new record, so the tail of the
    /// last record cannot ring into it.</summary>
    public void Reset()
    {
        Array.Clear(_x1); Array.Clear(_x2);
        Array.Clear(_y1); Array.Clear(_y2);
        _hz = double.NaN;
        SetCutoff(OpenHz);
    }

    /// <summary>Filters one interleaved block in place. When open, it does nothing at all.</summary>
    /// <param name="offset">Frames into <paramref name="buffer"/> where the block starts.</param>
    public void Process(float[] buffer, int offset, int frames)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (!IsClosing)
            return;

        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < _channels; c++)
            {
                int i = (offset + f) * _channels + c;
                double x = buffer[i];

                for (int s = 0; s < 2; s++)
                {
                    double y = _b0 * x + _b1 * _x1[s, c] + _b2 * _x2[s, c] - _a1 * _y1[s, c] - _a2 * _y2[s, c];
                    _x2[s, c] = _x1[s, c]; _x1[s, c] = x;
                    _y2[s, c] = _y1[s, c]; _y1[s, c] = y;
                    x = y;
                }

                buffer[i] = (float)x;
            }
        }
    }
}
