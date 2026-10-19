namespace RPM.Core.Audio;

/// <summary>The noise floor of one recording, measured from a stretch that holds only noise: one magnitude
/// per FFT bin, averaged over every window of the sample.
///
/// <para>It stores the FFT size and sample rate it was measured at. A profile measured at one rate
/// describes different frequencies at another, so <see cref="NoiseReducer"/> rejects a mismatch rather than
/// mapping the bins across.</para>
///
/// <para>Channels are averaged into one profile. Surface noise is not a stereo image, and one profile per
/// record is what the user is tuning.</para>
/// </summary>
public sealed record NoiseProfile(int FftSize, int SampleRate, IReadOnlyList<double> Magnitudes)
{
    /// <summary>Window size for the analysis. At 44.1 kHz, 2048 gives a 21 Hz bin. That resolves surface
    /// noise without smearing a short sound across half a second.</summary>
    public const int DefaultFftSize = 2048;

    /// <summary>Bins a real FFT of <see cref="FftSize"/> samples produces, DC and Nyquist
    /// included.</summary>
    public int Bins => FftSize / 2 + 1;

    /// <summary>Measures the floor of <paramref name="interleaved"/>. Windows are Hann and hop by a
    /// quarter of <paramref name="fftSize"/>. This is the same analysis <see cref="NoiseReducer"/> uses, so the two
    /// can be compared directly. Power is averaged over every window of every channel, and the square root
    /// is stored, giving a magnitude.
    /// </summary>
    public static NoiseProfile Measure(
        ReadOnlySpan<float> interleaved, int channels, int sampleRate, int fftSize = DefaultFftSize)
    {
        if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
        if (fftSize < 64 || (fftSize & (fftSize - 1)) != 0)
            throw new ArgumentException("fft size must be a power of two", nameof(fftSize));

        int frames = interleaved.Length / channels;
        if (frames < fftSize)
            throw new ArgumentException(
                $"a noise profile needs at least {fftSize} frames, got {frames}", nameof(interleaved));

        int hop = fftSize / 4;
        int bins = fftSize / 2 + 1;
        var window = Spectrum.BuildHann(fftSize);
        var power = new double[bins];
        var re = new double[fftSize];
        var im = new double[fftSize];
        int windows = 0;

        for (int ch = 0; ch < channels; ch++)
        {
            for (int start = 0; start + fftSize <= frames; start += hop)
            {
                for (int i = 0; i < fftSize; i++)
                {
                    re[i] = interleaved[(start + i) * channels + ch] * window[i];
                    im[i] = 0;
                }
                Spectrum.Transform(re, im);
                for (int b = 0; b < bins; b++)
                    power[b] += re[b] * re[b] + im[b] * im[b];
                windows++;
            }
        }

        var magnitudes = new double[bins];
        for (int b = 0; b < bins; b++)
            magnitudes[b] = Math.Sqrt(power[b] / windows);

        return new NoiseProfile(fftSize, sampleRate, magnitudes);
    }
}
