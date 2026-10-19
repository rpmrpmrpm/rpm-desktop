namespace RPM.Core.Audio;

/// <summary>
/// The three knobs on a mixer channel: bass, mid, high, one set per deck.
///
/// <para>This is not the same as <see cref="Equalizer"/>. That class is the listener's ten-band
/// curve: a correction for a pair of speakers, set once and left alone, applied to the whole mix on
/// its way out. This class is a performance control. It is changed during a changeover, on one deck at
/// a time, so that two records sound good together: the bass is handed from one to the other, a vocal
/// is reduced so that it does not clash with the arriving record, and the treble is removed from a
/// record that is leaving.</para>
///
/// <para><b>Where the bands divide.</b> At 300 Hz and 4 kHz, which is the layout of Pioneer's DJM
/// mixers, familiar to most DJs. Bass is the kick and the bassline. Mid is the main body of a record:
/// snare, vocal, keys and guitar. High is hats, cymbals and air. The wide mid band makes the layout
/// forgiving: a bass swap can hand over the low end without touching the mid knob, so a vocal stays
/// intact.</para>
///
/// <para><b>How far it goes.</b> A hand control gives +6 dB up and -26 dB down, the same range as a
/// DJM channel EQ. The boost is the smaller part on purpose: a mixer EQ is mainly used to cut, and a
/// knob that could add 12 dB of bass to a record that already has enough would clip when two decks
/// are summed. A changeover reaches further down than any knob (see <see cref="KillDb"/>), because a
/// bass swap must leave the arriving record with no bass at all, not with quiet bass.</para>
///
/// <para>The filters are RBJ cookbook biquads (a low shelf, a peak and a high shelf), with separate
/// state for each channel so the stereo image is kept. Gains should be ramped a block at a time, not
/// changed in one step: a sudden change in a filter's coefficients causes a click, and this control
/// is moved while the record plays.</para>
/// </summary>
public sealed class DeckEq
{
    /// <summary>Where bass ends and mid begins. The kick and the bassline are below it. The body of
    /// the snare and the low part of a voice are above it.</summary>
    public const double LowHz = 300;

    /// <summary>Where mid ends and high begins. Above it there is almost no pitch: hats, cymbals and
    /// the air around a vocal, but not the vocal itself.</summary>
    public const double HighHz = 4000;

    /// <summary>Where the mid band is centred: the geometric mean of the two crossover frequencies.
    /// This puts the peak in the middle of the band as heard, not in the middle of the frequency
    /// numbers.</summary>
    public const double MidHz = 1095;      // sqrt(300 * 4000), to the nearest Hz

    /// <summary>How far a band can be pushed up. It is small on purpose (see the class remarks).</summary>
    public const double MaxBoostDb = 6;

    /// <summary>How far down a user can turn a band: the range of a DJM channel EQ, and the limit for
    /// a knob in the interface. It is not the lowest value this filter accepts. See
    /// <see cref="KillDb"/>, which a changeover uses for the bass and which no knob reaches.</summary>
    public const double MaxCutDb = -26;

    /// <summary>
    /// The band removed completely, and the lowest gain that can be set. A band 60 dB below the
    /// rest of the mix is inaudible in a room. A shelf cannot reach minus infinity the way a
    /// crossover-based isolator can, and at this depth it does not need to.
    ///
    /// <para>The gain reaches this value by ramping down to it, not by a switch at the end of the knob
    /// travel. A switch would change the filter coefficients in one step, which causes a click at every
    /// bass swap. The swap ramps all the way down here, and every value on the way is a real filter
    /// setting.</para>
    /// </summary>
    public const double KillDb = -60;

    private const double MidQ = 0.9;       // wide enough to cover 300 Hz to 4 kHz without a notch

    private readonly int _sampleRate;
    private readonly int _channels;

    private readonly Biquad _low, _mid, _high;

    private double _lowDb = double.NaN, _midDb = double.NaN, _highDb = double.NaN;

    /// <summary>True once a knob has been moved off flat. Until then the deck is not filtered, and
    /// there is no cost for three filters that would only copy their input.</summary>
    private bool _everTouched;

    public DeckEq(int sampleRate, int channels)
    {
        if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channels));

        _sampleRate = sampleRate;
        _channels = channels;

        _low = new Biquad(channels);
        _mid = new Biquad(channels);
        _high = new Biquad(channels);

        SetGains(0, 0, 0);
    }

    /// <summary>Where the three knobs are, in dB. 0 is flat.</summary>
    public double LowDb => _lowDb;
    public double MidDb => _midDb;
    public double HighDb => _highDb;

    /// <summary>True while all three are flat, so the block can be left alone entirely.</summary>
    public bool IsFlat => !_everTouched;

    /// <summary>
    /// Moves the three knobs. It is cheap enough to call once per block, which is how a change
    /// is ramped. A band that has not moved is left alone.
    /// </summary>
    public void SetGains(double lowDb, double midDb, double highDb)
    {
        lowDb = Clamp(lowDb);
        midDb = Clamp(midDb);
        highDb = Clamp(highDb);

        if (Moved(lowDb, _lowDb))
        {
            _lowDb = lowDb;
            _low.LowShelf(_sampleRate, LowHz, lowDb);
        }

        if (Moved(midDb, _midDb))
        {
            _midDb = midDb;
            _mid.Peaking(_sampleRate, MidHz, MidQ, midDb);
        }

        if (Moved(highDb, _highDb))
        {
            _highDb = highDb;
            _high.HighShelf(_sampleRate, HighHz, highDb);
        }

        _everTouched |= Math.Abs(_lowDb) > 0.01 || Math.Abs(_midDb) > 0.01 || Math.Abs(_highDb) > 0.01;
    }

    /// <summary>Moves the bass knob and leaves the other two where they are. A bass swap changes one band.
    /// A setter that took all three values would force every caller to pass the two it does not want to
    /// change.</summary>
    public void SetLow(double db) => SetGains(db, _midDb, _highDb);

    /// <summary>Moves the mid knob alone.</summary>
    public void SetMid(double db) => SetGains(_lowDb, db, _highDb);

    /// <summary>Moves the high knob alone.</summary>
    public void SetHigh(double db) => SetGains(_lowDb, _midDb, db);

    private static double Clamp(double db) => Math.Clamp(db, KillDb, MaxBoostDb);

    private static bool Moved(double want, double have) => double.IsNaN(have) || Math.Abs(want - have) >= 0.01;

    /// <summary>Filters one interleaved block in place. A deck whose knobs have never been moved
    /// skips the filters entirely. Once a knob has been moved, the chain keeps running even when the
    /// gains are flat, because switching the filters in and out would itself cause a step in the
    /// signal.</summary>
    public void Process(float[] buffer, int frames) => Process(buffer, 0, frames);

    /// <param name="offset">Frames into <paramref name="buffer"/> where the block starts. It is non-zero
    /// for a deck that spent the first part of the block waiting for a bar line: the silence before the
    /// start is not the record and must not go through the filter.</param>
    public void Process(float[] buffer, int offset, int frames)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (!_everTouched)
            return;

        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < _channels; c++)
            {
                int i = (offset + f) * _channels + c;
                double x = buffer[i];
                x = _low.Step(x, c);
                x = _mid.Step(x, c);
                x = _high.Step(x, c);
                buffer[i] = (float)x;
            }
        }
    }

    /// <summary>Clears the filter memory. Use it when a deck loads a new record, so the tail of the
    /// previous one cannot carry over into it.</summary>
    public void Reset()
    {
        _low.Reset();
        _mid.Reset();
        _high.Reset();
    }

    /// <summary>
    /// One RBJ cookbook biquad with its own delay line for each channel. It uses direct form I.
    /// </summary>
    private sealed class Biquad(int channels)
    {
        private readonly double[] _x1 = new double[channels], _x2 = new double[channels];
        private readonly double[] _y1 = new double[channels], _y2 = new double[channels];

        private double _b0 = 1, _b1, _b2, _a1, _a2;

        public double Step(double x, int c)
        {
            double y = _b0 * x + _b1 * _x1[c] + _b2 * _x2[c] - _a1 * _y1[c] - _a2 * _y2[c];
            _x2[c] = _x1[c]; _x1[c] = x;
            _y2[c] = _y1[c]; _y1[c] = y;
            return y;
        }

        public void Reset()
        {
            Array.Clear(_x1); Array.Clear(_x2);
            Array.Clear(_y1); Array.Clear(_y2);
        }

        public void LowShelf(int rate, double hz, double gainDb)
        {
            var (a, w0, cos, sin) = Common(rate, hz, gainDb);
            const double slope = 0.9;
            double alpha = sin / 2 * Math.Sqrt((a + 1 / a) * (1 / slope - 1) + 2);
            double twoSqrtAAlpha = 2 * Math.Sqrt(a) * alpha;

            Normalise(
                b0: a * ((a + 1) - (a - 1) * cos + twoSqrtAAlpha),
                b1: 2 * a * ((a - 1) - (a + 1) * cos),
                b2: a * ((a + 1) - (a - 1) * cos - twoSqrtAAlpha),
                a0: (a + 1) + (a - 1) * cos + twoSqrtAAlpha,
                a1: -2 * ((a - 1) + (a + 1) * cos),
                a2: (a + 1) + (a - 1) * cos - twoSqrtAAlpha);
        }

        public void HighShelf(int rate, double hz, double gainDb)
        {
            var (a, w0, cos, sin) = Common(rate, hz, gainDb);
            const double slope = 0.9;
            double alpha = sin / 2 * Math.Sqrt((a + 1 / a) * (1 / slope - 1) + 2);
            double twoSqrtAAlpha = 2 * Math.Sqrt(a) * alpha;

            Normalise(
                b0: a * ((a + 1) + (a - 1) * cos + twoSqrtAAlpha),
                b1: -2 * a * ((a - 1) + (a + 1) * cos),
                b2: a * ((a + 1) + (a - 1) * cos - twoSqrtAAlpha),
                a0: (a + 1) - (a - 1) * cos + twoSqrtAAlpha,
                a1: 2 * ((a - 1) - (a + 1) * cos),
                a2: (a + 1) - (a - 1) * cos - twoSqrtAAlpha);
        }

        public void Peaking(int rate, double hz, double q, double gainDb)
        {
            var (a, w0, cos, sin) = Common(rate, hz, gainDb);
            double alpha = sin / (2 * q);

            Normalise(
                b0: 1 + alpha * a,
                b1: -2 * cos,
                b2: 1 - alpha * a,
                a0: 1 + alpha / a,
                a1: -2 * cos,
                a2: 1 - alpha / a);
        }

        private static (double A, double W0, double Cos, double Sin) Common(int rate, double hz, double gainDb)
        {
            double w0 = 2 * Math.PI * hz / rate;
            return (Math.Pow(10, gainDb / 40), w0, Math.Cos(w0), Math.Sin(w0));
        }

        private void Normalise(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0;
            _a1 = a1 / a0; _a2 = a2 / a0;
        }
    }
}
