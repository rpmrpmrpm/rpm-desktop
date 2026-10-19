using RPM.Core.Audio;

namespace RPM.Core.Analysis;

/// <summary>
/// The log-mel spectrogram that the beat model reads, built exactly as the model was trained.
///
/// <para>A model is a fixed set of weights over a fixed input format. If the mel is built with a
/// symmetric window instead of a periodic one, or scaled by a different constant, the model still
/// returns beats, but they are less accurate. No error is raised, so the mistake is hard to see.
/// Every constant here is therefore copied from the model's own preprocessing, and
/// <c>MelSpectrogramTests</c> compares the output with a reference built by that
/// preprocessing.</para>
///
/// <para>Matches torchaudio's <c>MelSpectrogram</c> as beat_this configures it: centred frames with
/// reflected padding, a periodic Hann window, magnitudes divided by the square root of the window
/// length, Slaney mel spacing with no area normalisation, and <c>log1p(1000 x)</c> on the way
/// out.</para>
/// </summary>
public static class MelSpectrogram
{
    public const int SampleRate = 22050;
    public const int FftSize = 1024;
    public const int HopSize = 441;      // 20 ms, so 50 frames a second
    public const int MelBands = 128;
    public const double MinHz = 30, MaxHz = 11000;

    /// <summary>What the magnitudes are multiplied by before the logarithm. It is large so that quiet
    /// detail is well above zero, where <c>log1p</c> still separates small differences.</summary>
    private const double LogMultiplier = 1000;

    /// <summary>Frames per second of the result. Use it to turn a frame index into a time.</summary>
    public const double FrameRate = (double)SampleRate / HopSize;

    /// <summary>How many frames <paramref name="samples"/> samples produce. One per hop, plus the
    /// frame centred on the first sample. An empty signal gives no frames.</summary>
    public static int FrameCount(int samples) => samples <= 0 ? 0 : 1 + samples / HopSize;

    private static readonly float[] Window = BuildPeriodicHann(FftSize);
    private static readonly float[][] Filters = BuildMelFilters();

    /// <summary>
    /// The spectrogram of a mono signal at <see cref="SampleRate"/>, as one array of
    /// <see cref="MelBands"/> values per frame.
    /// </summary>
    public static float[][] Compute(ReadOnlySpan<float> mono)
    {
        int frames = FrameCount(mono.Length);
        var result = new float[frames][];
        if (frames == 0)
            return result;

        int bins = FftSize / 2 + 1;
        var re = new double[FftSize];
        var im = new double[FftSize];
        var magnitude = new double[bins];

        // The magnitudes are divided by the square root of the window length. It is computed once here,
        // not per bin per frame.
        double scale = 1.0 / Math.Sqrt(FftSize);

        for (int f = 0; f < frames; f++)
        {
            // Centred frames: frame f is the window centred on sample f*hop, so it starts half a
            // window earlier. The first frames therefore begin before the signal does, and the
            // reflection below fills in those samples.
            int start = f * HopSize - FftSize / 2;
            for (int i = 0; i < FftSize; i++)
            {
                re[i] = Reflected(mono, start + i) * Window[i];
                im[i] = 0;
            }
            Spectrum.Transform(re, im);

            for (int b = 0; b < bins; b++)
                magnitude[b] = Math.Sqrt(re[b] * re[b] + im[b] * im[b]) * scale;

            var row = new float[MelBands];
            for (int m = 0; m < MelBands; m++)
            {
                var filter = Filters[m];
                double sum = 0;
                for (int b = 0; b < bins; b++)
                    sum += filter[b] * magnitude[b];
                row[m] = (float)Math.Log(1 + LogMultiplier * sum);
            }
            result[f] = row;
        }

        return result;
    }

    /// <summary>
    /// The sample at <paramref name="at"/>, with the signal mirrored at both ends.
    ///
    /// <para>The model was trained with reflected padding, not silence. With silence, a track that
    /// starts on the beat would look as if it starts after half a window of silence.</para>
    /// </summary>
    private static float Reflected(ReadOnlySpan<float> mono, int at)
    {
        if (mono.Length == 1)
            return mono[0];

        // The index is folded repeatedly, not once: a window can be longer than the signal when a
        // track is only a second or two long.
        int period = 2 * (mono.Length - 1);
        int folded = ((at % period) + period) % period;
        return mono[folded < mono.Length ? folded : period - folded];
    }

    /// <summary>
    /// The periodic Hann window, used for spectra. Its last sample is the one <em>before</em> the
    /// cycle closes.
    ///
    /// <para><see cref="Spectrum.BuildHann"/> is the symmetric window, which divides by
    /// <c>n - 1</c> instead. The two differ very slightly and both look like a Hann window on a
    /// plot, but the model's weights were fitted against this one.</para>
    /// </summary>
    private static float[] BuildPeriodicHann(int n)
    {
        var window = new float[n];
        for (int i = 0; i < n; i++)
            window[i] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * i / n)));
        return window;
    }

    /// <summary>Hertz to Slaney mels: linear up to 1 kHz, logarithmic above it.</summary>
    private static double HzToMel(double hz)
    {
        const double linearStep = 200.0 / 3;
        const double breakHz = 1000.0;
        double breakMel = breakHz / linearStep;
        double logStep = Math.Log(6.4) / 27.0;
        return hz >= breakHz ? breakMel + Math.Log(hz / breakHz) / logStep : hz / linearStep;
    }

    /// <summary>Slaney mels back to hertz.</summary>
    private static double MelToHz(double mel)
    {
        const double linearStep = 200.0 / 3;
        const double breakHz = 1000.0;
        double breakMel = breakHz / linearStep;
        double logStep = Math.Log(6.4) / 27.0;
        return mel >= breakMel ? breakHz * Math.Exp(logStep * (mel - breakMel)) : mel * linearStep;
    }

    /// <summary>
    /// One triangular filter per band, each rising from the previous band's centre to its own and
    /// falling to the next one's, with the centres spaced evenly in mels.
    ///
    /// <para>There is no area normalisation: the wide filters at the top end keep more total weight
    /// than the narrow ones at the bottom, as in the model's training. Dividing each filter by its
    /// width (the librosa default) would tilt every spectrum the model sees.</para>
    /// </summary>
    private static float[][] BuildMelFilters()
    {
        int bins = FftSize / 2 + 1;

        // Bin centres, spaced up to the Nyquist frequency. It is calculated with integer division,
        // as the reference does, and this sets the position of the last bin.
        var binHz = new double[bins];
        double nyquist = SampleRate / 2;
        for (int b = 0; b < bins; b++)
            binHz[b] = nyquist * b / (bins - 1);

        // Band edges: two more than the number of bands, because every band needs one edge to rise
        // from and one to fall to.
        var edges = new double[MelBands + 2];
        double minMel = HzToMel(MinHz), maxMel = HzToMel(MaxHz);
        for (int i = 0; i < edges.Length; i++)
            edges[i] = MelToHz(minMel + (maxMel - minMel) * i / (edges.Length - 1));

        var filters = new float[MelBands][];
        for (int m = 0; m < MelBands; m++)
        {
            var filter = new float[bins];
            double left = edges[m], centre = edges[m + 1], right = edges[m + 2];
            for (int b = 0; b < bins; b++)
            {
                double rising = (binHz[b] - left) / (centre - left);
                double falling = (right - binHz[b]) / (right - centre);
                filter[b] = (float)Math.Max(0, Math.Min(rising, falling));
            }
            filters[m] = filter;
        }
        return filters;
    }
}
