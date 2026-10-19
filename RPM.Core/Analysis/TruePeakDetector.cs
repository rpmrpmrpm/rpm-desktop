namespace RPM.Core.Analysis;

/// <summary>
/// The loudest the waveform actually gets, as opposed to the loudest sample in it.
///
/// <para>The two are not the same, and the difference is what clips a converter on a file that no
/// meter called clipped. Samples are points on a curve: between two samples at 0.98 the curve that
/// the reconstruction filter draws can pass 1.0, and the digital-to-analogue stage plays that curve.
/// BS.1770-4 Annex 2 handles this by resampling four times finer before measuring, which is what
/// this class does.</para>
///
/// <para>This is worth the arithmetic because a gain is clamped against this number. Turning a
/// quiet recording up is the whole point of normalising, and the amount it can safely go up by is
/// decided by the peak that is really there, not the one that happens to have been sampled.</para>
/// </summary>
internal sealed class TruePeakDetector
{
    /// <summary>Four times the stored rate, as the standard asks for.</summary>
    private const int Phases = 4;

    /// <summary>Taps per phase. Twelve is enough that the interpolated curve is within a few
    /// hundredths of a dB of the real one, and short enough that the delay line is nothing.</summary>
    private const int Taps = 12;

    /// <summary>Which tap the sample being interpolated from sits at.</summary>
    private const int Centre = Taps / 2 - 1;

    /// <summary>The interpolator, phase by phase. Built once for the process, because it depends on
    /// nothing but the two constants above.</summary>
    private static readonly double[][] Kernel = BuildKernel();

    private readonly int _channels;

    /// <summary>The last <see cref="Taps"/> samples of each channel, as a ring.</summary>
    private readonly float[] _history;
    private int _at;

    public TruePeakDetector(int channels)
    {
        _channels = channels;
        _history = new float[Taps * channels];
    }

    public double SamplePeak { get; private set; }

    public double TruePeak { get; private set; }

    public void Add(ReadOnlySpan<float> interleaved, int frames)
    {
        for (int f = 0; f < frames; f++)
        {
            int at = f * _channels;

            for (int c = 0; c < _channels; c++)
            {
                float x = interleaved[at + c];
                double magnitude = Math.Abs(x);
                if (magnitude > SamplePeak)
                    SamplePeak = magnitude;

                _history[_at * _channels + c] = x;
            }

            _at = (_at + 1) % Taps;

            // Every phase of the window that just moved. Phase 0 is the sample itself and is already
            // covered by SamplePeak, so the loop starts at 1. TruePeak still has to be at least the
            // sample peak, which is what the check after the loop is for.
            for (int p = 1; p < Phases; p++)
            {
                var kernel = Kernel[p];
                for (int c = 0; c < _channels; c++)
                {
                    double sum = 0;
                    for (int t = 0; t < Taps; t++)
                        sum += kernel[t] * _history[((_at + t) % Taps) * _channels + c];

                    double magnitude = Math.Abs(sum);
                    if (magnitude > TruePeak)
                        TruePeak = magnitude;
                }
            }

            if (SamplePeak > TruePeak)
                TruePeak = SamplePeak;
        }
    }

    /// <summary>
    /// A windowed-sinc interpolator, one set of taps per quarter-sample offset.
    ///
    /// <para>The taps are derived rather than tabulated: a table of coefficients from elsewhere
    /// cannot be checked, and the two numbers this is built from (four phases, twelve taps) are the
    /// only decisions in it. Each phase is normalised to unit gain at DC so that a constant signal
    /// interpolates to itself, which is the one error a short kernel would otherwise make visible.</para>
    /// </summary>
    private static double[][] BuildKernel()
    {
        var kernel = new double[Phases][];

        for (int p = 0; p < Phases; p++)
        {
            var taps = new double[Taps];
            double offset = (double)p / Phases;
            double sum = 0;

            for (int t = 0; t < Taps; t++)
            {
                double distance = Centre + offset - t;

                // Blackman over the tap span, which keeps the stopband low enough that the
                // interpolation does not invent a peak of its own.
                double phase = 2 * Math.PI * t / (Taps - 1);
                double window = 0.42 - 0.5 * Math.Cos(phase) + 0.08 * Math.Cos(2 * phase);

                taps[t] = Sinc(distance) * window;
                sum += taps[t];
            }

            for (int t = 0; t < Taps; t++)
                taps[t] /= sum;

            kernel[p] = taps;
        }

        return kernel;
    }

    private static double Sinc(double x) =>
        Math.Abs(x) < 1e-9 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
}
