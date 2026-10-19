namespace RPM.Core.Audio;

/// <summary>How hard the gate works. Defaults match the values Audacity's interface opens with.</summary>
/// <param name="ReductionDb">How far a bin held closed is attenuated.</param>
/// <param name="Sensitivity">How far above the profile a bin must reach to count as signal, in dB.</param>
/// <param name="FrequencySmoothingBands">Bins either side that an open bin also opens. 0 is off.</param>
/// <param name="AttackSeconds">Time constant for a bin opening.</param>
/// <param name="ReleaseSeconds">Time constant for a bin closing.</param>
public sealed record NoiseReductionSettings(
    double ReductionDb = 12,
    double Sensitivity = 6,
    int FrequencySmoothingBands = 3,
    double AttackSeconds = 0.02,
    double ReleaseSeconds = 0.10);

/// <summary>Spectral noise gate: bins that stay near the measured noise floor are attenuated, and bins
/// that carry signal pass. The design follows the published description of Audacity's noise reduction. The
/// code is not taken from Audacity's source, which is GPL.
///
/// <para>It works on a stream, and the result does not depend on the block size. The same instance with the
/// same settings gives the same samples whether they arrive 50 ms at a time from the capture pump or a
/// whole side at once from the renderer. That lets the monitor be a true preview of the result.</para>
///
/// <para>The latency is built in, not exposed as a short first read. On <see cref="Reset"/> the input
/// buffer is filled with <see cref="LatencyFrames"/> of silence. So every <see cref="Process"/> returns
/// exactly as many frames as it was given, delayed by that much. A caller that needs the output aligned
/// with its input drops that many frames at the start, and feeds the same number of zero frames at the
/// end.</para>
/// </summary>
public sealed class NoiseReducer
{
    /// <summary>Analysis window. Must match the profile's.</summary>
    public const int FftSize = NoiseProfile.DefaultFftSize;

    /// <summary>75% overlap. The squared Hann window summed at this hop is a constant 1.5.</summary>
    public const int Hop = FftSize / 4;

    private const double WindowSquaredSum = 1.5;

    private readonly NoiseReductionSettings _settings;
    private readonly int _channels;
    private readonly int _bins;
    private readonly float[] _window;

    /// <summary>Per-bin power a bin must exceed to count as signal.</summary>
    private readonly double[] _threshold;

    /// <summary>Gate gain per bin per channel, carried between windows so that attack and release have an
    /// effect. Indexed channel-major.</summary>
    private readonly double[] _gain;

    private readonly double[] _re = new double[FftSize];
    private readonly double[] _im = new double[FftSize];
    private readonly double[] _mask;
    private readonly double[] _smoothed;

    /// <summary>Per channel: samples waiting to be analysed, and the overlap-add accumulator they are
    /// written into.</summary>
    private readonly List<float>[] _input;
    private readonly List<float>[] _output;

    /// <summary>Absolute sample index of each buffer's first element, and of the next window to analyse.
    /// These are absolute, not relative, because the two buffers do not advance together. The windows move
    /// by whole hops, and the output moves by whatever block size the caller passed. One shared offset
    /// would be wrong as soon as a block is not a multiple of the hop.</summary>
    private readonly long[] _inputBase;
    private readonly long[] _outputBase;
    private readonly long[] _nextWindow;

    private readonly double _closedGain;
    private readonly double _attackCoefficient;
    private readonly double _releaseCoefficient;

    public NoiseReducer(NoiseProfile profile, NoiseReductionSettings settings, int sampleRate, int channels)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(settings);
        if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
        if (profile.SampleRate != sampleRate)
            throw new ArgumentException(
                $"the noise profile was measured at {profile.SampleRate} Hz, not {sampleRate} Hz", nameof(profile));
        if (profile.FftSize != FftSize)
            throw new ArgumentException(
                $"the noise profile was measured with a {profile.FftSize} window, not {FftSize}", nameof(profile));

        _settings = settings;
        _channels = channels;
        _bins = FftSize / 2 + 1;
        _window = Spectrum.BuildHann(FftSize);

        // The profile holds magnitudes, but the gate compares power, so square them here once.
        double sensitivity = Math.Pow(10, settings.Sensitivity / 10);
        _threshold = new double[_bins];
        for (int b = 0; b < _bins; b++)
        {
            double m = profile.Magnitudes[b];
            _threshold[b] = m * m * sensitivity;
        }

        _gain = new double[_bins * channels];
        _mask = new double[_bins];
        _smoothed = new double[_bins];

        _closedGain = Math.Pow(10, -Math.Abs(settings.ReductionDb) / 20);
        _attackCoefficient = Coefficient(settings.AttackSeconds, sampleRate);
        _releaseCoefficient = Coefficient(settings.ReleaseSeconds, sampleRate);

        _input = new List<float>[channels];
        _output = new List<float>[channels];
        for (int c = 0; c < channels; c++) { _input[c] = new List<float>(); _output[c] = new List<float>(); }
        _inputBase = new long[channels];
        _outputBase = new long[channels];
        _nextWindow = new long[channels];

        Reset();
    }

    /// <summary>How far the output runs behind the input.
    ///
    /// <para>This is a window plus a hop, not just a window. An output sample is finished only when every
    /// window that covers it has been added. Windows start on multiples of the hop, but blocks arrive at
    /// whatever size the caller chose. A pre-roll of one window is enough only when the frames handed over
    /// so far happen to be a multiple of the hop. One more hop covers every other case.</para>
    /// </summary>
    public int LatencyFrames => FftSize + Hop;

    private static double Coefficient(double seconds, int sampleRate) =>
        seconds <= 0 ? 0 : Math.Exp(-(double)Hop / (seconds * sampleRate));

    /// <summary>Clears the overlap state and re-arms the latency, so the previous audio cannot ring into
    /// what comes next.</summary>
    public void Reset()
    {
        for (int c = 0; c < _channels; c++)
        {
            _input[c].Clear();
            _output[c].Clear();
            // Pre-roll: the windows that produce the first output samples see silence before the real
            // audio. That is what makes every Process return as many frames as it was given, whatever size
            // the blocks are.
            for (int i = 0; i < LatencyFrames; i++) _input[c].Add(0);
            _inputBase[c] = 0;
            _outputBase[c] = 0;
            _nextWindow[c] = 0;
        }
        Array.Fill(_gain, 1.0);
    }

    /// <summary>Gates <paramref name="frameCount"/> interleaved frames in place.</summary>
    public void Process(float[] interleaved, int frameCount)
    {
        ArgumentNullException.ThrowIfNull(interleaved);
        if (frameCount <= 0) return;
        if (frameCount * _channels > interleaved.Length)
            throw new ArgumentOutOfRangeException(nameof(frameCount));

        for (int c = 0; c < _channels; c++)
        {
            var input = _input[c];
            for (int f = 0; f < frameCount; f++)
                input.Add(interleaved[f * _channels + c]);

            long available = _inputBase[c] + input.Count;
            while (available - _nextWindow[c] >= FftSize)
            {
                AnalyseWindow(c, (int)(_nextWindow[c] - _inputBase[c]), _nextWindow[c]);
                _nextWindow[c] += Hop;
            }

            // Every window that will ever cover the samples before the next window's start has already read
            // them, so they can be dropped.
            int drop = (int)(_nextWindow[c] - _inputBase[c]);
            if (drop > 0)
            {
                input.RemoveRange(0, drop);
                _inputBase[c] += drop;
            }

            var output = _output[c];
            while (output.Count < frameCount) output.Add(0);
            for (int f = 0; f < frameCount; f++)
                interleaved[f * _channels + c] = output[f];
            output.RemoveRange(0, frameCount);
            _outputBase[c] += frameCount;
        }
    }

    /// <summary>One window: transform, gate, transform back, add into the accumulator.</summary>
    /// <param name="channel">Which channel's buffers to work in.</param>
    /// <param name="offset">Where the window starts inside the channel's input buffer.</param>
    /// <param name="absoluteStart">The same point as an absolute sample index. This places the window in
    /// the output accumulator.</param>
    private void AnalyseWindow(int channel, int offset, long absoluteStart)
    {
        var input = _input[channel];
        for (int i = 0; i < FftSize; i++)
        {
            _re[i] = input[offset + i] * _window[i];
            _im[i] = 0;
        }
        Spectrum.Transform(_re, _im);

        for (int b = 0; b < _bins; b++)
        {
            double power = _re[b] * _re[b] + _im[b] * _im[b];
            _mask[b] = power > _threshold[b] ? 1 : 0;
        }

        SmoothAcrossFrequency();
        SmoothAcrossTime(channel);

        int gainBase = channel * _bins;
        for (int b = 0; b < _bins; b++)
        {
            double g = _gain[gainBase + b];
            _re[b] *= g;
            _im[b] *= g;
            // Mirror the gain onto the negative frequency, so the inverse transform comes back real.
            int mirror = FftSize - b;
            if (b > 0 && mirror < FftSize)
            {
                _re[mirror] *= g;
                _im[mirror] *= g;
            }
        }

        InverseTransform();

        var output = _output[channel];
        // Never negative. The pre-roll guarantees that the next window starts at or after the point the
        // output has been emitted to, so no window adds into a sample that has already been handed out.
        int writeAt = (int)(absoluteStart - _outputBase[channel]);
        while (output.Count < writeAt + FftSize) output.Add(0);
        for (int i = 0; i < FftSize; i++)
            output[writeAt + i] += (float)(_re[i] * _window[i] / WindowSquaredSum);
    }

    /// <summary>An open bin opens its neighbours. Without this the gate chatters. Single bins of a harmonic
    /// open and close between windows, and the result warbles.</summary>
    private void SmoothAcrossFrequency()
    {
        int span = Math.Max(0, _settings.FrequencySmoothingBands);
        if (span == 0)
        {
            Array.Copy(_mask, _smoothed, _bins);
            return;
        }
        for (int b = 0; b < _bins; b++)
        {
            double open = 0;
            int from = Math.Max(0, b - span), to = Math.Min(_bins - 1, b + span);
            for (int i = from; i <= to && open < 1; i++)
                open = Math.Max(open, _mask[i]);
            _smoothed[b] = open;
        }
    }

    /// <summary>Moves each bin's gain towards the value the mask asks for. It uses the attack rate when
    /// opening and the release rate when closing.</summary>
    private void SmoothAcrossTime(int channel)
    {
        int gainBase = channel * _bins;
        for (int b = 0; b < _bins; b++)
        {
            double target = _smoothed[b] > 0 ? 1.0 : _closedGain;
            double current = _gain[gainBase + b];
            double coefficient = target > current ? _attackCoefficient : _releaseCoefficient;
            _gain[gainBase + b] = target + (current - target) * coefficient;
        }
    }

    /// <summary><see cref="Spectrum"/> transforms forwards only. The inverse is the same transform with
    /// both conjugations and a 1/N scale.</summary>
    private void InverseTransform()
    {
        for (int i = 0; i < FftSize; i++) _im[i] = -_im[i];
        Spectrum.Transform(_re, _im);
        for (int i = 0; i < FftSize; i++)
        {
            _re[i] /= FftSize;
            _im[i] = -_im[i] / FftSize;
        }
    }
}
