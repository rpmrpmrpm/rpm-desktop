using RPM.Core.Audio;

namespace RPM.Core.Viz;

/// <summary>
/// What the room hears: three envelopes for the light in it, and a run of log-spaced bands for the
/// spectrum drawn across it.
///
/// <para>This code is kept apart from the desktop's GL stage so that it can be tested. Inside a fragment
/// program, a measurement of music cannot be tested. It is here because the phone draws the same room on
/// Skia, and it must read the music the same way. If two heads folded a spectrum with two sets of
/// arithmetic, they would draw two rooms, however alike the shaders were.</para>
///
/// <para>Nothing here knows where the samples come from. It is handed a tap: <c>CopyPlayFrame</c> on
/// either head. The tap is read at the playhead, not at the decoder. So the picture stays in step with the
/// sound, even when the sound comes out of a speaker in another room.</para>
/// </summary>
public sealed class RoomBands
{
    /// <summary>Columns in the run, so there is one reading per bar. If a different number of columns were
    /// sampled from these, several bars would read the same place in the curve and stand at the same
    /// height. That makes a run look like blocks. One bar is one frequency band.</summary>
    public const int Bands = 128;

    /// <summary>The low end, which swells the figures: bins 1 to 12 of the middle window cover roughly
    /// 20 to 260 Hz. That is the kick and the bass, and nothing above them.</summary>
    private const int BassLo = 1, BassHi = 12;

    /// <summary>The middle, which the second family of figures rides: bins 20 to 90 cover roughly 430 Hz
    /// to 1.9 kHz. That is where the voice and most of the played music sit. It is kept apart from the bass
    /// on purpose. Two families driven by the same envelope would breathe as one body.</summary>
    private const int MidLo = 20, MidHi = 90;

    /// <summary>
    /// The air, which the third family rides: bins 116 to 325 of the SHORT window cover roughly 5 to 14
    /// kHz. That is cymbals, hats, the breath on a consonant, and nothing that carries a tune.
    ///
    /// <para>This is read from the short window, not from the middle window like the two envelopes above
    /// it. That is the reason the third family is separate. What lives up here is transient. A hat is
    /// open and shut inside 40 milliseconds, and a 46 ms window smears it into the one before it. The 23
    /// ms window catches the edge, which is the only part of a cymbal that can be caught. The middle
    /// window also cannot reach this range. Its bins stop at 10.8 kHz, and half of this range is above
    /// that.</para>
    /// </summary>
    private const int AirLo = 116, AirHi = 325;

    private const int Ranges = 3;

    /// <summary>
    /// Where the ranges meet, in Hz.
    ///
    /// <para>The bottom range stops at 430 Hz, because bass fundamentals live below that and a voice does
    /// not. The top range begins at 5 kHz. Presence, around 2.4 kHz, is the edge of a snare and the
    /// consonants of a voice. It belongs to the middle range, because it is the middle of a mix, however
    /// high it sounds when written down.</para>
    /// </summary>
    private static readonly double[] RangeHz = [20.0, 430.0, 5000.0, 16000.0];

    /// <summary>
    /// How much of the run's WIDTH each range is given.
    ///
    /// <para>This is a layout choice. It is the difference between a spectrum and a wall. A straight log
    /// scale over these bins would spend half the run below 480 Hz. Every kick would then draw a solid
    /// block across the left of the screen, all at the same height. Everything above 2 kHz would be
    /// crushed into the last inch and read as a flat line. So the bass is pulled in to the left, and the
    /// room it would take goes to the top end.</para>
    /// </summary>
    private static readonly float[] RangeWidth = [0.45f, 0.35f, 0.20f];

    /// <summary>
    /// How hard the run answers to what it hears: at its two ends, and at its middle.
    ///
    /// <para>The drive is a smooth curve, not three steps. It falls gradually from the bottom of the run
    /// into the middle, and it climbs back out to the top. So there is no seam where one range hands over
    /// to the next. The middle carries the voice and most of the arrangement. Holding it down stops the
    /// centre of the picture being loud all of the time. Doing that in a step would draw a visible edge
    /// where the step happened.</para>
    ///
    /// <para>The two ends are not the same. The bass is the loudest thing in nearly every mix, so it does
    /// not need help. The top end does.</para>
    /// </summary>
    private const float DriveLow = 0.80f;
    private const float DriveHigh = 1.00f;
    private const float DriveMiddle = 0.16f;

    /// <summary>
    /// How tall the rim stands, as a multiple of its own level. The run climbs to it through the top range,
    /// so that it finishes on a rise instead of stopping at a wall.
    ///
    /// <para>This is a curve, not a number for each range. It is exactly one at the boundary between the
    /// middle and top ranges. Any other value there would multiply the two bars beside that boundary by
    /// different figures. That draws a step straight down the picture at 5 kHz.</para>
    /// </summary>
    private const float LeanRim = 1.25f;

    /// <summary>Where a range's own peak lands on the knee's input. It is chosen so that the loudest bar in
    /// a loud passage sits near four fifths of the height. That is high enough to use the picture, and it
    /// is short of the top, because the knee curve never reaches it.</summary>
    private const float RangeReference = 6.0f;

    /// <summary>
    /// How completely a range follows its own level. This is the whole difference between a spectrum and a
    /// meter pinned at the top.
    ///
    /// <para>At one, the gain tracks the peak exactly. Every passage is then drawn at the same height,
    /// because each range is always measured against itself. Below one, the gain follows the peak raised to
    /// this power, which is a weaker response. The ranges stay balanced against each other, but a quiet
    /// passage still looks quiet.</para>
    /// </summary>
    private const float RangeAdapt = 0.40f;

    /// <summary>The quietest value that a range's peak may be taken as. Together with the clamp on the
    /// gain, this sets the ceiling on the gain. Without it, a silent passage would wind the gain up until
    /// the noise floor was drawn as music.</summary>
    private const float RangeFloor = 0.02f;

    /// <summary>Which band each range starts at, worked out from <see cref="RangeWidth"/> once.</summary>
    private static readonly int[] RangeBand = BuildRangeBands();

    /// <summary>The middle band of each range. A range's gain is measured across the whole range. It is
    /// applied at the centre of the range, and it is eased between the centres. See <see
    /// cref="GainAt"/>.</summary>
    private static readonly float[] RangeCentre = BuildRangeCentres();

    private readonly AnalysisWindow _low, _mid, _high;

    private readonly float[] _raw = new float[Bands];      // the bands, as the music actually is
    private readonly float[] _curve = new float[Bands];    // the same bands, joined into one line
    private readonly float[] _bands = new float[Bands];    // the same bands, as they are drawn

    private readonly float[] _rangePeak = [0.30f, 0.20f, 0.12f];
    private readonly float[] _rangeGain = [1f, 1f, 1f];

    private float _bass;         // The low end: a fast attack, a held peak, then a slow release
    private double _bassHold;
    private float _midPulse;     // The middle, which the second family rides
    private double _midHold;
    private float _air;          // The top end. It is quicker again, and the third family rides it.
    private double _airHold;

    /// <summary>
    /// Three windows, one per range.
    ///
    /// <para>Every range is read through a transform sized for what that range has to draw. No single
    /// window suits all of music. The length of a window sets the frequency resolution, and it also sets
    /// the time resolution. The bottom and the top of a spectrum want opposite ends of that
    /// trade-off:</para>
    ///
    /// <list type="bullet">
    /// <item>low 8192: 5.4 Hz bins, a 186 ms window. Below 430 Hz the notes of a bass line are a few hertz
    /// apart, so resolution matters most. A low note lasts far longer than the window. So hearing that far
    /// back costs nothing that can be seen.</item>
    /// <item>mid 2048: 21.5 Hz bins, a 46 ms window. The balanced middle. It is the same length that the
    /// rest of the app analyses with.</item>
    /// <item>high 1024: 43.1 Hz bins, a 23 ms window. Above 5 kHz, nothing needs to be told apart to the
    /// hertz. What is up there is cymbals and transients.</item>
    /// </list>
    /// </summary>
    /// <param name="sampleRate">What the engine is running at. It is not assumed. The figure decides what
    /// every bin means, and a room folded against the wrong rate draws the wrong frequencies.</param>
    public RoomBands(int sampleRate)
    {
        _low = new AnalysisWindow(sampleRate, frameSize: 8192, bins: 160, delayTicks: 3);
        _mid = new AnalysisWindow(sampleRate, frameSize: 2048, bins: 256, delayTicks: 0);
        _high = new AnalysisWindow(sampleRate, frameSize: 1024, bins: 384, delayTicks: 0);
    }

    /// <summary>Where the played samples come from. It returns false when there is nothing at the playhead
    /// yet. That is common in the first moments of a track.</summary>
    public Func<float[], bool>? Tap { get; set; }

    /// <summary>
    /// The low end, with an envelope. This is the figure that the room is lit by.
    ///
    /// <para>It is already the right number for lighting. It has a held peak, a fast attack and a slow
    /// release. So a kick pushes it up and lets it fall. The raw level would flicker at whatever rate the
    /// drummer is playing.</para>
    /// </summary>
    public float Bass => _bass;

    /// <summary>The middle, on its own envelope. It is quicker than the bass in both directions. What it
    /// drives is a family of small figures that should dart where the big ones swell.</summary>
    public float Mid => _midPulse;

    /// <summary>The air: cymbals, hats, the edge of a consonant. It is quicker again and is barely held at
    /// all, because what it answers to has no sustain.</summary>
    public float Air => _air;

    /// <summary>The run. Each band is 0 to 1, log spaced and ready to draw. The values are live: read
    /// them, do not keep them.</summary>
    public ReadOnlySpan<float> Run => _bands;

    /// <summary>
    /// Reads the playing block, keeps the three envelopes, and folds the spectrum into the log-spaced
    /// bands the bars are cut from.
    /// </summary>
    /// <param name="now">The caller's clock, in seconds. The holds are measured against it, so the stage
    /// that owns the frames also owns the time.</param>
    /// <param name="playing">Whether there is anything to hear. Silence eases the run down rather than
    /// cutting it. A spectrum that falls to nothing the instant the music stops looks like a broken
    /// picture, not like a quiet room.</param>
    public void Update(double now, bool playing)
    {
        _low.Update(Tap, playing);
        _mid.Update(Tap, playing);
        _high.Update(Tap, playing);

        // Both of the first two envelopes are read off the MIDDLE window, on purpose. It is 2048 samples
        // long. That is the length the rest of the app analyses at, and the length these envelopes were
        // tuned against. So the lighting responds to a kick as quickly as it always has. The long window
        // would be the obvious choice, and it would be wrong. Its 186 ms is needed for the frequency
        // resolution that the bars need. Using it here would cost the promptness that the lighting
        // depends on.
        var spec = _mid.Level;

        float low = 0f;
        for (int i = BassLo; i < BassHi; i++) low += spec[i];
        low /= BassHi - BassLo;
        low = MathF.Min(1f, low * 1.35f);
        if (low > _bass) { _bass += (low - _bass) * 0.42f; _bassHold = now + 0.10; }
        else if (now >= _bassHold) { _bass += (low - _bass) * 0.055f; }

        float middle = 0f;
        for (int i = MidLo; i < MidHi; i++) middle += spec[i];
        middle /= MidHi - MidLo;
        middle = MathF.Min(1f, middle * 1.9f);
        if (middle > _midPulse) { _midPulse += (middle - _midPulse) * 0.50f; _midHold = now + 0.06; }
        else if (now >= _midHold) { _midPulse += (middle - _midPulse) * 0.11f; }

        // The air comes from the SHORT window. It is the only one of the three that hears a cymbal as an
        // edge, not as a smear. The gain here is much larger than for the other two. There is less energy
        // up here, and the band is wide. So most of the bins averaged into this value are near silent, even
        // while the cymbals are playing. Measured raw, it would never leave the floor.
        var top = _high.Level;
        float air = 0f;
        for (int i = AirLo; i < AirHi; i++) air += top[i];
        air /= AirHi - AirLo;
        air = MathF.Min(1f, air * 3.0f);
        if (air > _air) { _air += (air - _air) * 0.62f; _airHold = now + 0.03; }
        else if (now >= _airHold) { _air += (air - _air) * 0.16f; }

        // Log-spaced bands, as the music actually is. Each octave gets about the same room as any other
        // octave. Without this, a spectrum drawn on a linear scale becomes a wall of bass and a flat line
        // of everything else.
        for (int r = 0; r < Ranges; r++)
        {
            int b0 = RangeBand[r], b1 = RangeBand[r + 1];
            double hzLo = RangeHz[r], hzHi = RangeHz[r + 1];
            double span = hzHi / hzLo;
            int n = b1 - b0;

            for (int band = b0; band < b1; band++)
            {
                // Log spaced within each range, so an octave still gets about the same room as any other
                // octave. Each range is laid out across the width it was given. The three ranges do not
                // share one curve. A shared curve would let the bass take half the screen.
                double loHz = hzLo * Math.Pow(span, (band - b0) / (double)n);
                double hiHz = hzLo * Math.Pow(span, (band + 1 - b0) / (double)n);
                _raw[band] = Fold(loHz, hiHz);
            }
        }

        // A light three-tap smoothing across the run, done before anything is measured. Neighbouring bands
        // of real music are related. A note is not one bin wide, and neither is a cymbal. If each band were
        // drawn as if it knew nothing of its two neighbours, a coherent envelope would turn into noise. The
        // smoothing is light, so neighbouring bars still differ.
        for (int b = 0; b < Bands; b++)
        {
            float l = _raw[Math.Max(0, b - 1)];
            float c = _raw[b];
            float n = _raw[Math.Min(Bands - 1, b + 1)];
            _curve[b] = l * 0.25f + c * 0.50f + n * 0.25f;
        }

        // Each range is measured against its own recent peak. The peak rises quickly and falls over
        // seconds. So a range settles to the level that the music is actually at. It is not scaled by
        // whatever the loudest thing in the whole run is doing.
        for (int r = 0; r < Ranges; r++)
        {
            int b0 = RangeBand[r], b1 = RangeBand[r + 1];
            float peak = 0f;
            for (int i = b0; i < b1; i++) peak = MathF.Max(peak, _curve[i]);

            _rangePeak[r] += (peak - _rangePeak[r]) * (peak > _rangePeak[r] ? 0.40f : 0.014f);
            _rangeGain[r] = Math.Clamp(
                RangeReference / MathF.Pow(MathF.Max(_rangePeak[r], RangeFloor), RangeAdapt), 1f, 24f);
        }

        // One pass down the whole run. Nothing here depends on which range a band falls in. The gain is
        // eased between the centres of the ranges. The drive is a smooth curve across the width. The lean
        // is a curve that reaches one exactly where the top range begins. The ranges still measure
        // themselves separately. They just no longer draw their own boundaries into the picture.
        for (int band = 0; band < Bands; band++)
        {
            float u = band / (float)(Bands - 1);

            // 1 - 4u(1-u) is one at either end of the run, and zero at the exact centre. So the drive eases
            // down from the bass into the middle, and back up into the top end, without a seam.
            float dip = 1f - 4f * u * (1f - u);
            float drive = DriveMiddle + ((u < 0.5f ? DriveLow : DriveHigh) - DriveMiddle) * dip;

            // Gain first, then the curve. The gain brings this part of the run into view. The power, when
            // it is above one, pushes the quiet bands back down. That puts a gap between one bar and the
            // bar beside it. Below one, the power would lift all the bands toward the loudest, and the
            // run would flatten into a block.
            float x = MathF.Pow(_curve[band] * GainAt(band), 1.15f) * drive * LeanAt(u);

            // The knee cannot clip. x/(1+x) keeps rising and never reaches the top. It is half height at
            // x=1, two thirds at x=2, and four fifths at x=4. No value of x reaches the top.
            float v = x / (1f + x);

            // A second ease on top of the spectrum's own. The bins are already smoothed, and the shape is
            // smoothed here. The bar rises almost at once. It falls quickly enough to reach the floor
            // before the next hit. Otherwise a bar would be caught halfway and never reach the floor at
            // all.
            _bands[band] += (v - _bands[band]) * (v > _bands[band] ? 0.94f : 0.80f);
        }
    }

    /// <summary>
    /// The gain for one band, eased between the ranges instead of switched at their edges.
    ///
    /// <para>Each range works out its own gain from its own recent peak. That is the point of the split.
    /// But if that gain were applied to every band in the range, and then changed to a different number
    /// at the boundary, the boundary would be drawn. So a range's gain belongs to its centre. Bands
    /// between two centres take a smoothstep blend of the two gains.</para>
    /// </summary>
    private float GainAt(int band)
    {
        if (band <= RangeCentre[0]) return _rangeGain[0];
        if (band >= RangeCentre[Ranges - 1]) return _rangeGain[Ranges - 1];

        for (int r = 0; r < Ranges - 1; r++)
        {
            if (band > RangeCentre[r + 1]) continue;
            float t = (band - RangeCentre[r]) / (RangeCentre[r + 1] - RangeCentre[r]);
            t = t * t * (3f - 2f * t);
            return _rangeGain[r] + (_rangeGain[r + 1] - _rangeGain[r]) * t;
        }
        return _rangeGain[Ranges - 1];
    }

    /// <summary>The lean at a point along the run: flat until the top range begins, then a smoothstep
    /// climb to <see cref="LeanRim"/> at the rim. One at the boundary, so there is no step there.</summary>
    private static float LeanAt(float u)
    {
        float start = RangeBand[Ranges - 1] / (float)(Bands - 1);
        if (u <= start) return 1f;
        float t = (u - start) / MathF.Max(1e-4f, 1f - start);
        t = t * t * (3f - 2f * t);
        return 1f + (LeanRim - 1f) * t;
    }

    /// <summary>
    /// The level across a span of frequency, read from whichever transform actually resolves it. Hz in,
    /// 0..1 out - the caller never has to know which of the three answered, or how wide that one's bins
    /// happen to be.
    /// </summary>
    private float Fold(double loHz, double hiHz)
    {
        // The crossovers are the range edges. So no range is drawn from two transforms, and there is no
        // seam inside a range.
        var window = hiHz <= RangeHz[1] ? _low : hiHz <= RangeHz[2] ? _mid : _high;
        var src = window.Level;
        int last = window.Bins - 1;

        double loF = loHz / window.BinHz, hiF = hiHz / window.BinHz;

        if (hiF - loF < 1.0)
        {
            // This band is narrower than one bin. That can still happen at the very bottom, even with the
            // long window. Read between the bins instead of rounding to one bin. The value is
            // interpolated between two bins, so neighbouring bands still differ. Rounding to one bin
            // would give the first bars the same number, and they would be drawn as a block.
            double at = (loF + hiF) * 0.5;
            int i0 = Math.Clamp((int)at, 0, last - 1);
            float f = (float)(at - i0);
            return src[i0] * (1f - f) + src[i0 + 1] * f;
        }

        int lo = Math.Clamp((int)loF, 0, last);
        int hi = Math.Clamp((int)hiF, lo + 1, last + 1);

        float s = 0f;
        for (int i = lo; i < hi; i++) s += src[i];
        return s / (hi - lo);
    }

    private static int[] BuildRangeBands()
    {
        var edges = new int[Ranges + 1];
        float acc = 0f;
        for (int r = 0; r < Ranges; r++)
        {
            acc += RangeWidth[r];
            edges[r + 1] = Math.Clamp((int)MathF.Round(acc * Bands), edges[r] + 1, Bands);
        }
        edges[Ranges] = Bands;
        return edges;
    }

    private static float[] BuildRangeCentres()
    {
        var centres = new float[Ranges];
        for (int r = 0; r < Ranges; r++)
            centres[r] = (RangeBand[r] + RangeBand[r + 1] - 1) * 0.5f;
        return centres;
    }

    /// <summary>
    /// One analysis window: a frame of played samples, the transform of it, and the levels it hands back.
    ///
    /// <para>Every window maps the same [-84, -20] dBFS range onto 0..1, and it settles at the same rate.
    /// So three windows of different lengths hand back levels on ONE scale, and the run does not step
    /// where it crosses from one to the next. All that differs between them is how finely they resolve
    /// frequency, and how far back in time they hear.</para>
    ///
    /// <para><b>The delay.</b> The tap fills forward from the playhead. So a window's reading is centred
    /// half its own length ahead of what is coming out of the speakers. That is 12 ms for the short
    /// window and 93 ms for the long one. Left alone, the bottom of the run would be about 80 ms early.
    /// The bass would move before the kick is heard, while everything above it arrives on time. So the
    /// long window's answer is held back through a few ticks. That puts all the bands on the same
    /// instant.</para>
    /// </summary>
    private sealed class AnalysisWindow
    {
        private const double DbFloor = -84.0, DbCeil = -20.0;

        /// <summary>The window length that every other window is referred to, so all three agree about the
        /// level of the same music. It is the middle window, which is also the length the rest of the
        /// app uses.</summary>
        private const int ReferenceFrame = 2048;

        /// <summary>How much of the previous reading carries over, so a bin settles instead of strobing.
        /// It is the same figure that the shared texture uses, for the same reason.</summary>
        private const float Smoothing = 0.5f;

        private readonly int _frameSize;
        private readonly float[] _wave;
        private readonly float[] _hann;
        private readonly double[] _re, _im;
        private readonly float[] _now;      // this tick's reading
        private readonly float[][] _line;   // held back through here
        private readonly bool _passThrough;
        private int _at;

        public AnalysisWindow(int sampleRate, int frameSize, int bins, int delayTicks)
        {
            _frameSize = frameSize;
            _wave = new float[frameSize];
            _hann = Spectrum.BuildHann(frameSize);
            _re = new double[frameSize];
            _im = new double[frameSize];
            _now = new float[bins];

            Bins = bins;
            BinHz = (double)sampleRate / frameSize;
            Level = new float[bins];

            _line = new float[Math.Max(1, delayTicks)][];
            for (int i = 0; i < _line.Length; i++)
                _line[i] = new float[bins];

            // Nought ticks means no delay at all: read straight from this tick's reading.
            _passThrough = delayTicks <= 0;
        }

        /// <summary>Bins kept, and how wide each of them is in Hz.</summary>
        public int Bins { get; }
        public double BinHz { get; }

        /// <summary>The levels to draw from, 0..1. They are delayed onto the same instant as their
        /// neighbours.</summary>
        public float[] Level { get; }

        /// <summary>Reads the playing block and folds it down. A frame that the tap cannot fill eases the
        /// held reading down rather than cutting it. That happens in the first fraction of a second of a
        /// track, when the playhead does not yet have this many samples written ahead of it. Silence does
        /// the same. This runs on every analysis tick, including the ticks spent paused.</summary>
        public void Update(Func<float[], bool>? tap, bool playing)
        {
            if (!playing || tap is null || !tap(_wave))
            {
                for (int i = 0; i < Bins; i++) _now[i] *= Smoothing;
            }
            else
            {
                for (int i = 0; i < _frameSize; i++)
                {
                    _re[i] = _wave[i] * _hann[i];
                    _im[i] = 0;
                }

                Spectrum.Transform(_re, _im);

                // Bins of a shorter window are wider. So each bin holds more of a broadband signal, and
                // it reads hotter, by about 3 dB per halving of the window. Without a correction, the
                // windows disagree about the level of the same music. That disagreement lands on the
                // crossovers, where it would be drawn as a step. Referred to the 2048 window, they agree.
                double bandwidth = _frameSize / (double)ReferenceFrame;

                for (int i = 0; i < Bins; i++)
                {
                    double power = (_re[i] * _re[i] + _im[i] * _im[i])
                                 / ((double)_frameSize * _frameSize) * bandwidth;
                    double db = 10.0 * Math.Log10(power + 1e-12);
                    float level = (float)Math.Clamp((db - DbFloor) / (DbCeil - DbFloor), 0.0, 1.0);

                    // Peak-fast, fall-slow: a rising bin jumps, a falling one eases down.
                    _now[i] = level > _now[i] ? level : _now[i] * Smoothing + level * (1f - Smoothing);
                }
            }

            if (_passThrough)
            {
                Array.Copy(_now, Level, Bins);
                return;
            }

            // Through the delay line: draw the oldest reading, then put this one in its place.
            var oldest = _line[_at];
            Array.Copy(oldest, Level, Bins);
            Array.Copy(_now, oldest, Bins);
            _at = (_at + 1) % _line.Length;
        }
    }
}
