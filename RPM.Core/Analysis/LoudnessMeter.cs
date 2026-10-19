namespace RPM.Core.Analysis;

/// <summary>
/// How loud a recording sounds, measured by ITU-R BS.1770-4. EBU R128 and ReplayGain 2.0 are both
/// built on this standard. It allows two tracks to be played at the same level without re-encoding
/// either of them.
///
/// <para>Plain RMS is not enough, which is why <see cref="EnergyProfile"/> is not reused here. A
/// bass-heavy track and a bright one with the same RMS are not equally loud to a listener, and
/// levelling a library on RMS would leave some tracks quieter than others. BS.1770 models what the
/// ear hears: the signal is K-weighted (a high shelf for the response of the head, and a high-pass
/// that removes rumble that is not heard as loudness), measured in 400 ms blocks, and the blocks
/// that are only silence between notes are gated out, so a sparse recording is not judged on its
/// gaps.</para>
///
/// <para>Audio is added one block at a time, not as a whole file, so a track is measured while it
/// decodes and minutes of audio never need to be held in memory. The result is only available at
/// the end, because the gating is relative to the track's own average, and no block can be judged
/// until all of them are in.</para>
/// </summary>
public sealed class LoudnessMeter
{
    /// <summary>The measurement window, as the standard fixes it.</summary>
    private const double BlockSeconds = 0.400;

    /// <summary>Blocks overlap by 75%, so they advance a quarter of a window at a time. The signal is
    /// measured as consecutive sub-blocks: each new sub-block completes another 400 ms window with
    /// the three before it. This gives the overlap without doing any of the arithmetic twice.</summary>
    private const int SubBlocksPerBlock = 4;

    /// <summary>Blocks quieter than this are not part of the programme: leader, the tail of a
    /// fade-out, or the silence between movements. This is the absolute gate of BS.1770-4.</summary>
    private const double AbsoluteGateLufs = -70;

    /// <summary>How far below the track's own ungated average a block must be before it stops
    /// counting. This is the relative gate. It stops a track that is mostly quiet from being
    /// measured as quiet when the part that people listen to is not.</summary>
    private const double RelativeGateLu = 10;

    /// <summary>The calibration constant in BS.1770's loudness equation.</summary>
    private const double Offset = -0.691;

    private readonly int _channels;
    private readonly int _subBlockFrames;

    /// <summary>What each channel contributes to the sum. The front channels count once. The
    /// surround channels count for more, because a sound behind the listener is heard as louder than
    /// the same sound in front (BS.1770-4 Table 3). LFE counts for nothing and is never
    /// included.</summary>
    private readonly double[] _weights;

    // K-weighting: a high shelf then a high-pass, per channel, transposed direct form II.
    private readonly double[] _s1 = new double[5];
    private readonly double[] _s2 = new double[5];
    private readonly double[] _z1a, _z2a, _z1b, _z2b;

    /// <summary>Sum of squares of the weighted signal in the sub-block being filled, per channel.</summary>
    private readonly double[] _sums;
    private int _filled;

    /// <summary>The last <see cref="SubBlocksPerBlock"/> sub-block sums, per channel, stored as a
    /// ring buffer. One 400 ms block is the four sums that are in the ring at that moment.</summary>
    private readonly double[] _ring;
    private int _ringAt;
    private int _ringCount;

    /// <summary>The mean square of every 400 ms block, already summed across channels with their
    /// weights. Gating happens over this at the end.</summary>
    private readonly List<double> _blocks = [];

    private readonly TruePeakDetector _peak;

    /// <param name="channels">The channel count of the frames that will be added. The order is
    /// assumed to be L, R, C, Ls, Rs, which is the order ffmpeg returns.</param>
    public LoudnessMeter(int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);

        _channels = channels;
        _subBlockFrames = Math.Max(1, (int)Math.Round(sampleRate * BlockSeconds / SubBlocksPerBlock));

        _weights = new double[channels];
        for (int c = 0; c < channels; c++)
            _weights[c] = c is 3 or 4 && channels >= 5 ? 1.41 : 1.0;

        Shelf(sampleRate, _s1);
        HighPass(sampleRate, _s2);

        _z1a = new double[channels]; _z2a = new double[channels];
        _z1b = new double[channels]; _z2b = new double[channels];
        _sums = new double[channels];
        _ring = new double[SubBlocksPerBlock * channels];

        _peak = new TruePeakDetector(channels);
    }

    /// <summary>The highest sample seen, as a linear amplitude. Not the same as
    /// <see cref="TruePeak"/> and never higher than it.</summary>
    public double SamplePeak => _peak.SamplePeak;

    /// <summary>
    /// The highest level the waveform reaches <em>between</em> samples, as a linear amplitude. It is
    /// found by interpolating the signal to four times the stored sample rate.
    ///
    /// <para>A gain must be limited against this value and not against the sample peak. A track
    /// mastered right up to full scale has peaks that the samples miss. The sample values may say
    /// 0.99 while the waveform they describe goes over 1.0 between two samples, so a converter would
    /// clip on a file that no meter reported as clipping.</para>
    /// </summary>
    public double TruePeak => _peak.TruePeak;

    /// <summary>True once at least one 400 ms block has been measured. False for a file too short to
    /// have one. Such a file has no loudness, which is different from a loudness of zero.</summary>
    public bool HasSignal => _blocks.Count > 0;

    /// <summary>Measures <paramref name="frames"/> interleaved frames. It can be called as often as
    /// needed with any block size from the decoder, because the block boundaries do not matter.</summary>
    public void Add(ReadOnlySpan<float> interleaved, int frames)
    {
        if (frames <= 0)
            return;

        int samples = frames * _channels;
        if (samples > interleaved.Length)
            throw new ArgumentOutOfRangeException(nameof(frames));

        _peak.Add(interleaved, frames);

        for (int f = 0; f < frames; f++)
        {
            int at = f * _channels;
            for (int c = 0; c < _channels; c++)
            {
                double x = interleaved[at + c];

                // Shelf, then high-pass. Transposed direct form II needs one multiply-add per
                // coefficient and two state words per filter, which keeps a whole-library pass cheap.
                double y = _s1[0] * x + _z1a[c];
                _z1a[c] = _s1[1] * x - _s1[3] * y + _z2a[c];
                _z2a[c] = _s1[2] * x - _s1[4] * y;

                double w = _s2[0] * y + _z1b[c];
                _z1b[c] = _s2[1] * y - _s2[3] * w + _z2b[c];
                _z2b[c] = _s2[2] * y - _s2[4] * w;

                _sums[c] += w * w;
            }

            if (++_filled < _subBlockFrames)
                continue;

            CloseSubBlock();
        }
    }

    /// <summary>Stores the sub-block that has just finished and, once there are four of them, the
    /// 400 ms block that they make up.</summary>
    private void CloseSubBlock()
    {
        int slot = _ringAt * _channels;
        for (int c = 0; c < _channels; c++)
        {
            _ring[slot + c] = _sums[c] / _filled;
            _sums[c] = 0;
        }

        _ringAt = (_ringAt + 1) % SubBlocksPerBlock;
        _filled = 0;
        if (_ringCount < SubBlocksPerBlock)
            _ringCount++;

        // The first three sub-blocks of a track do not complete a window. There is nothing to store
        // until the fourth, and from then on every sub-block completes another block.
        if (_ringCount < SubBlocksPerBlock)
            return;

        double z = 0;
        for (int c = 0; c < _channels; c++)
        {
            double mean = 0;
            for (int s = 0; s < SubBlocksPerBlock; s++)
                mean += _ring[s * _channels + c];
            z += _weights[c] * (mean / SubBlocksPerBlock);
        }

        _blocks.Add(z);
    }

    /// <summary>
    /// The programme loudness in LUFS, gated as BS.1770-4 requires. The value is
    /// <see cref="double.NegativeInfinity"/> for silence, and for anything too short to fill one
    /// 400 ms block.
    /// </summary>
    public double IntegratedLufs
    {
        get
        {
            if (_blocks.Count == 0)
                return double.NegativeInfinity;

            // The absolute gate first. What is left is the programme and not the room tone around it.
            double sum = 0;
            int count = 0;
            foreach (var z in _blocks)
            {
                if (Loudness(z) <= AbsoluteGateLufs)
                    continue;
                sum += z;
                count++;
            }

            if (count == 0)
                return double.NegativeInfinity;

            // Then the relative gate, measured against the average of the blocks that passed the
            // first gate.
            double threshold = Loudness(sum / count) - RelativeGateLu;

            double gated = 0;
            int kept = 0;
            foreach (var z in _blocks)
            {
                double l = Loudness(z);
                if (l <= AbsoluteGateLufs || l <= threshold)
                    continue;
                gated += z;
                kept++;
            }

            return kept == 0 ? double.NegativeInfinity : Loudness(gated / kept);
        }
    }

    private static double Loudness(double meanSquare) =>
        meanSquare <= 0 ? double.NegativeInfinity : Offset + 10 * Math.Log10(meanSquare);

    /// <summary>
    /// The K-weighting high shelf: about +4 dB above 1.7 kHz. It models how the head and the ear
    /// canal raise the high frequencies before the sound is heard.
    ///
    /// <para>The coefficients are calculated from the filter's own parameters and not copied from the
    /// standard's table, because the table is for 48 kHz only and this class must measure at the
    /// sample rate of the decoder. At 48 kHz it reproduces the coefficients published in BS.1770-4
    /// exactly, and a test checks this.</para>
    /// </summary>
    private static void Shelf(int sampleRate, double[] into)
    {
        const double F0 = 1681.974450955533;
        const double GainDb = 3.999843853973347;
        const double Q = 0.7071752369554196;

        double k = Math.Tan(Math.PI * F0 / sampleRate);
        double vh = Math.Pow(10, GainDb / 20);
        double vb = Math.Pow(vh, 0.4996667741545416);
        double a0 = 1 + k / Q + k * k;

        into[0] = (vh + vb * k / Q + k * k) / a0;
        into[1] = 2 * (k * k - vh) / a0;
        into[2] = (vh - vb * k / Q + k * k) / a0;
        into[3] = 2 * (k * k - 1) / a0;
        into[4] = (1 - k / Q + k * k) / a0;
    }

    /// <summary>The RLB high-pass at about 38 Hz. Sub-bass is not heard as loudness, and a track with
    /// a lot of it would otherwise be turned down for something that nobody hears.</summary>
    private static void HighPass(int sampleRate, double[] into)
    {
        const double F0 = 38.13547087602444;
        const double Q = 0.5003270373238773;

        double k = Math.Tan(Math.PI * F0 / sampleRate);
        double den = 1 + k / Q + k * k;

        into[0] = 1;
        into[1] = -2;
        into[2] = 1;
        into[3] = 2 * (k * k - 1) / den;
        into[4] = (1 - k / Q + k * k) / den;
    }
}
