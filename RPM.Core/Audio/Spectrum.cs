namespace RPM.Core.Audio;

/// <summary>Pure DSP for the RPaMp visualiser: a radix-2 FFT, and a mapping of PCM into log-spaced bands
/// with display levels from 0 to 1. It has no audio I/O, so it can be unit tested directly. The app pairs
/// it with a window of PCM decoded by ffmpeg.
/// </summary>
public static class Spectrum
{
    /// <summary>Hann analysis window. It reduces spectral leakage. Its coherent gain is 0.5.</summary>
    public static float[] BuildHann(int n)
    {
        if (n < 2) throw new ArgumentOutOfRangeException(nameof(n));
        var w = new float[n];
        for (int i = 0; i < n; i++)
            w[i] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * i / (n - 1))));
        return w;
    }

    /// <summary>In-place iterative radix-2 Cooley-Tukey FFT. The length must be a power of two.</summary>
    public static void Transform(double[] re, double[] im)
    {
        ArgumentNullException.ThrowIfNull(re);
        ArgumentNullException.ThrowIfNull(im);
        int n = re.Length;
        if (n != im.Length) throw new ArgumentException("re/im length mismatch");
        if (n < 2 || (n & (n - 1)) != 0) throw new ArgumentException("length must be a power of two", nameof(re));

        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    double xr = re[b] * cr - im[b] * ci;
                    double xi = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi;
                    re[a] += xr; im[a] += xi;
                    double ncr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = ncr;
                }
            }
        }
    }

    /// <summary>The lowest and highest frequencies covered by the band mapping, in Hz.</summary>
    public const double MinHz = 40, MaxHz = 16000;

    /// <summary>Folds a transformed frame into <paramref name="bands"/> log-spaced levels from 0 to 1.
    /// Magnitudes are normalised to amplitude. The factor is 4/N: 2/N for the transform, and ×2 for the
    /// Hann window's 0.5 coherent gain. A full-scale tone then reads about 0 dBFS. The window from about
    /// -64 to +12 dBFS is mapped to 0..1. The headroom stops ordinary music from pinning the meter.
    /// </summary>
    public static float[] MapBands(double[] re, double[] im, int bands, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(re);
        ArgumentNullException.ThrowIfNull(im);
        if (bands < 1) throw new ArgumentOutOfRangeException(nameof(bands));

        int n = re.Length;
        double binHz = (double)sampleRate / n;
        double norm = 4.0 / n;
        var result = new float[bands];

        for (int b = 0; b < bands; b++)
        {
            double f0 = MinHz * Math.Pow(MaxHz / MinHz, (double)b / bands);
            double f1 = MinHz * Math.Pow(MaxHz / MinHz, (double)(b + 1) / bands);
            int i0 = Math.Max(1, (int)(f0 / binHz));
            int i1 = Math.Min(n / 2, (int)(f1 / binHz));
            if (i1 <= i0) i1 = i0 + 1;
            if (i0 >= n / 2) { result[b] = 0; continue; }

            double sum = 0;
            for (int i = i0; i < i1 && i < n / 2; i++)
                sum += Math.Sqrt(re[i] * re[i] + im[i] * im[i]);
            double avg = sum / (i1 - i0) * norm;

            double db = 20 * Math.Log10(avg + 1e-6) + b * 0.18; // slight high-frequency lift
            result[b] = (float)Math.Clamp((db + 64) / 76, 0, 1);
        }
        return result;
    }
}
