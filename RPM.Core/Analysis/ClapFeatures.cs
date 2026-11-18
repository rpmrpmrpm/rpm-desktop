using RPM.Core.Audio;

namespace RPM.Core.Analysis;

/// <summary>
/// The log-mel spectrogram that the CLAP audio model reads, built exactly as the model's own feature
/// extractor builds it.
///
/// <para>A model is a fixed set of weights over a fixed input format. A mel built with the wrong window,
/// filters or scale still gives an embedding, but a worse one, and no error says so. Every constant here
/// is copied from the extractor (transformers <c>ClapFeatureExtractor</c> with the settings of
/// <c>laion/larger_clap_music_and_speech</c>), and <c>ClapFeaturesTests</c> compares the output with a
/// reference that the extractor wrote (scripts/make_clap_reference.py).</para>
///
/// <para>What the extractor does for a clip of ten seconds or less: repeat the clip as many whole times as
/// fit in ten seconds and pad the rest with silence; take centred frames with reflected padding and a
/// periodic Hann window; take the power spectrum; apply 64 Slaney mel filters with Slaney area
/// normalisation between 50 Hz and 14 kHz; and convert to decibels with a floor of -100 dB. The result
/// is <see cref="Frames"/> frames of <see cref="MelBands"/> values.</para>
///
/// <para>The beat model's <see cref="MelSpectrogram"/> is a different recipe (another rate, no area
/// normalisation, a magnitude and <c>log1p</c>). The two must not share code that hides the
/// difference.</para>
/// </summary>
public static class ClapFeatures
{
    public const int SampleRate = 48000;
    public const int FftSize = 1024;
    public const int HopSize = 480;
    public const int MelBands = 64;
    public const double MinHz = 50, MaxHz = 14000;

    /// <summary>The length of one clip: ten seconds. A shorter clip is repeated and padded to this
    /// length. A longer one is cut to it.</summary>
    public const int ClipSamples = SampleRate * 10;

    /// <summary>Frames per clip: one per hop, plus the frame centred on the first sample.</summary>
    public const int Frames = 1 + ClipSamples / HopSize;

    /// <summary>Values per clip, in the order the model reads them: frame by frame, band by band.</summary>
    public const int ValuesPerClip = Frames * MelBands;

    /// <summary>The power below which a band reads as the floor, and the floor itself in decibels.</summary>
    private const double PowerFloor = 1e-10;

    private static readonly double[] Window = BuildPeriodicHann(FftSize);
    private static readonly Filter[] Filters = BuildMelFilters();

    /// <summary>
    /// The features of one clip of mono audio at <see cref="SampleRate"/>, as <see cref="ValuesPerClip"/>
    /// values, frame by frame.
    ///
    /// <para>A clip longer than ten seconds is cut to its first ten seconds. The extractor cuts at a random
    /// point instead, which would make two readings of one file differ. Callers decode exactly ten seconds,
    /// so this does not happen in the app.</para>
    /// </summary>
    /// <exception cref="ArgumentException">The clip is empty.</exception>
    public static float[] Compute(ReadOnlySpan<float> clip)
    {
        if (clip.IsEmpty)
            throw new ArgumentException("A clip needs at least one sample.", nameof(clip));

        var signal = Fit(clip);
        var result = new float[ValuesPerClip];

        int bins = FftSize / 2 + 1;
        var re = new double[FftSize];
        var im = new double[FftSize];
        var power = new double[bins];

        for (int f = 0; f < Frames; f++)
        {
            // Centred frames: frame f is the window centred on sample f*hop, so it starts half a window
            // earlier. The first and last frames reach past the clip, and the reflection fills them.
            int start = f * HopSize - FftSize / 2;
            for (int i = 0; i < FftSize; i++)
            {
                re[i] = Reflected(signal, start + i) * Window[i];
                im[i] = 0;
            }
            Spectrum.Transform(re, im);

            // The extractor keeps the spectrum as single-precision complex numbers before it squares
            // them. Rounding to float here matches it, not only approximately.
            for (int b = 0; b < bins; b++)
            {
                double r = (float)re[b], i = (float)im[b];
                power[b] = r * r + i * i;
            }

            int row = f * MelBands;
            for (int m = 0; m < MelBands; m++)
            {
                var filter = Filters[m];
                double sum = 0;
                for (int k = 0; k < filter.Weights.Length; k++)
                    sum += filter.Weights[k] * power[filter.First + k];
                result[row + m] = (float)(10 * Math.Log10(Math.Max(PowerFloor, sum)));
            }
        }

        return result;
    }

    /// <summary>
    /// The clip at exactly ten seconds: the first ten seconds of a longer one, or a shorter one repeated
    /// as many whole times as fit and followed by silence. This is the extractor's "repeatpad".
    /// </summary>
    private static float[] Fit(ReadOnlySpan<float> clip)
    {
        var fitted = new float[ClipSamples];
        if (clip.Length >= ClipSamples)
        {
            clip[..ClipSamples].CopyTo(fitted);
            return fitted;
        }

        int repeats = ClipSamples / clip.Length;
        for (int r = 0; r < repeats; r++)
            clip.CopyTo(fitted.AsSpan(r * clip.Length));
        return fitted;
    }

    /// <summary>The sample at <paramref name="at"/>, with the signal mirrored at both ends, without
    /// repeating the edge sample, as numpy's "reflect" padding does.</summary>
    private static float Reflected(float[] signal, int at)
    {
        int period = 2 * (signal.Length - 1);
        int folded = ((at % period) + period) % period;
        return signal[folded < signal.Length ? folded : period - folded];
    }

    /// <summary>The periodic Hann window: its last sample is the one before the cycle closes.</summary>
    private static double[] BuildPeriodicHann(int n)
    {
        var window = new double[n];
        for (int i = 0; i < n; i++)
            window[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / n));
        return window;
    }

    /// <summary>One mel filter: its weights, starting at FFT bin <see cref="First"/>. Only the bins where
    /// the triangle is above zero are kept, so a frame sums a few bins per band and not all 513.</summary>
    private sealed record Filter(int First, double[] Weights);

    /// <summary>Hertz to Slaney mels: linear up to 1 kHz, logarithmic above it.</summary>
    private static double HzToMel(double hz)
    {
        const double breakHz = 1000.0, breakMel = 15.0;
        double logStep = 27.0 / Math.Log(6.4);
        return hz >= breakHz ? breakMel + Math.Log(hz / breakHz) * logStep : 3.0 * hz / 200.0;
    }

    /// <summary>Slaney mels back to hertz.</summary>
    private static double MelToHz(double mel)
    {
        const double breakHz = 1000.0, breakMel = 15.0;
        double logStep = Math.Log(6.4) / 27.0;
        return mel >= breakMel ? breakHz * Math.Exp(logStep * (mel - breakMel)) : 200.0 * mel / 3.0;
    }

    /// <summary>
    /// Triangular filters with centres spaced evenly in Slaney mels, each divided by its width in hertz
    /// (Slaney area normalisation), as librosa builds them.
    /// </summary>
    private static Filter[] BuildMelFilters()
    {
        int bins = FftSize / 2 + 1;

        // Bin centres up to the Nyquist frequency. The extractor writes the top as sampling_rate // 2, an
        // integer division, which is exact at 48 kHz.
        var binHz = new double[bins];
        double nyquist = SampleRate / 2;
        for (int b = 0; b < bins; b++)
            binHz[b] = nyquist * b / (bins - 1);

        var edges = new double[MelBands + 2];
        double minMel = HzToMel(MinHz), maxMel = HzToMel(MaxHz);
        for (int i = 0; i < edges.Length; i++)
            edges[i] = MelToHz(minMel + (maxMel - minMel) * i / (edges.Length - 1));

        var filters = new Filter[MelBands];
        for (int m = 0; m < MelBands; m++)
        {
            double left = edges[m], centre = edges[m + 1], right = edges[m + 2];
            double area = 2.0 / (right - left);
            var weights = new double[bins];
            int first = -1, last = -1;
            for (int b = 0; b < bins; b++)
            {
                double rising = (binHz[b] - left) / (centre - left);
                double falling = (right - binHz[b]) / (right - centre);
                weights[b] = Math.Max(0, Math.Min(rising, falling)) * area;
                if (weights[b] > 0)
                {
                    if (first < 0)
                        first = b;
                    last = b;
                }
            }

            filters[m] = first < 0
                ? new Filter(0, [])
                : new Filter(first, weights[first..(last + 1)]);
        }
        return filters;
    }
}
