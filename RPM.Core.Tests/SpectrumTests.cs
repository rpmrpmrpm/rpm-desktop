using RPM.Core.Audio;

namespace RPM.Core.Tests;

public class SpectrumTests
{
    private const int Rate = 44100;
    private const int N = 2048;

    /// <summary>Builds a Hann-windowed sine frame, ready for <see cref="Spectrum.Transform"/>.</summary>
    private static (double[] Re, double[] Im) SineFrame(double hz, double amplitude = 1.0, int n = N)
    {
        var hann = Spectrum.BuildHann(n);
        var re = new double[n];
        var im = new double[n];
        for (int i = 0; i < n; i++)
            re[i] = amplitude * Math.Sin(2 * Math.PI * hz * i / Rate) * hann[i];
        return (re, im);
    }

    private static (double[] Re, double[] Im) SilentFrame(int n = N) => (new double[n], new double[n]);

    // ── FFT ──

    [Fact]
    public void Transform_PeaksAtTheBinOfTheInputTone()
    {
        const double hz = 1000;
        var (re, im) = SineFrame(hz);
        Spectrum.Transform(re, im);

        int peak = 1;
        double best = 0;
        for (int i = 1; i < N / 2; i++)
        {
            var mag = Math.Sqrt(re[i] * re[i] + im[i] * im[i]);
            if (mag > best) { best = mag; peak = i; }
        }

        var expected = (int)Math.Round(hz / ((double)Rate / N));
        Assert.InRange(peak, expected - 1, expected + 1);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(440)]
    [InlineData(5000)]
    [InlineData(12000)]
    public void Transform_LocatesToneAcrossTheSpectrum(double hz)
    {
        var (re, im) = SineFrame(hz);
        Spectrum.Transform(re, im);

        int peak = 1;
        double best = 0;
        for (int i = 1; i < N / 2; i++)
        {
            var mag = Math.Sqrt(re[i] * re[i] + im[i] * im[i]);
            if (mag > best) { best = mag; peak = i; }
        }

        var peakHz = peak * ((double)Rate / N);
        Assert.InRange(peakHz, hz - 30, hz + 30);
    }

    [Fact]
    public void Transform_OfSilenceIsAllZero()
    {
        var (re, im) = SilentFrame();
        Spectrum.Transform(re, im);
        Assert.All(re, v => Assert.Equal(0, v, 9));
        Assert.All(im, v => Assert.Equal(0, v, 9));
    }

    [Fact]
    public void Transform_RejectsNonPowerOfTwoLength()
        => Assert.Throws<ArgumentException>(() => Spectrum.Transform(new double[3], new double[3]));

    // ── Hann window ──

    [Fact]
    public void BuildHann_IsZeroAtTheEdgesAndOneInTheMiddle()
    {
        var w = Spectrum.BuildHann(N);
        Assert.Equal(0, w[0], 5);
        Assert.Equal(0, w[^1], 5);
        Assert.Equal(1, w[N / 2], 2);
    }

    [Fact]
    public void BuildHann_IsSymmetric()
    {
        var w = Spectrum.BuildHann(N);
        for (int i = 0; i < N / 2; i++)
            Assert.Equal(w[i], w[N - 1 - i], 5);
    }

    // ── band mapping ──

    [Fact]
    public void MapBands_ReturnsRequestedCountAllWithinZeroToOne()
    {
        var (re, im) = SineFrame(1000);
        Spectrum.Transform(re, im);
        var bands = Spectrum.MapBands(re, im, 20, Rate);

        Assert.Equal(20, bands.Length);
        Assert.All(bands, b => Assert.InRange(b, 0f, 1f));
    }

    [Fact]
    public void MapBands_SilenceFloorsEveryBandAtZero()
    {
        var (re, im) = SilentFrame();
        Spectrum.Transform(re, im);
        var bands = Spectrum.MapBands(re, im, 20, Rate);
        Assert.All(bands, b => Assert.Equal(0f, b));
    }

    [Fact]
    public void MapBands_PutsTheLoudestBandWhereTheToneIs()
    {
        const double hz = 1000;
        var (re, im) = SineFrame(hz);
        Spectrum.Transform(re, im);
        var bands = Spectrum.MapBands(re, im, 20, Rate);

        int loudest = Array.IndexOf(bands, bands.Max());

        // which log-spaced band should contain 1 kHz
        int expected = (int)(20 * Math.Log(hz / Spectrum.MinHz) / Math.Log(Spectrum.MaxHz / Spectrum.MinHz));
        Assert.InRange(loudest, expected - 1, expected + 1);
    }

    [Fact]
    public void MapBands_LouderToneReadsHigherThanQuieterOne()
    {
        var (loudRe, loudIm) = SineFrame(1000, amplitude: 1.0);
        Spectrum.Transform(loudRe, loudIm);
        var loud = Spectrum.MapBands(loudRe, loudIm, 20, Rate);

        var (quietRe, quietIm) = SineFrame(1000, amplitude: 0.05);
        Spectrum.Transform(quietRe, quietIm);
        var quiet = Spectrum.MapBands(quietRe, quietIm, 20, Rate);

        Assert.True(loud.Max() > quiet.Max(), $"loud {loud.Max()} should exceed quiet {quiet.Max()}");
    }

    [Fact]
    public void MapBands_LeavesHeadroom_FullScaleToneDoesNotPinTheMeter()
    {
        // A full-scale tone should read high but must NOT clamp to 1.0. That headroom keeps every bar
        // from sitting at the top of the visualiser.
        var (re, im) = SineFrame(1000, amplitude: 1.0);
        Spectrum.Transform(re, im);
        var bands = Spectrum.MapBands(re, im, 20, Rate);

        var peak = bands.Max();
        Assert.InRange(peak, 0.5f, 0.99f);
    }

    [Fact]
    public void MapBands_TypicalMusicLevelSitsMidRange()
    {
        // A tone at about -20 dBFS stands in for ordinary programme material: it should land
        // mid-meter, not pinned at either rail.
        var (re, im) = SineFrame(1000, amplitude: 0.1);
        Spectrum.Transform(re, im);
        var bands = Spectrum.MapBands(re, im, 20, Rate);

        Assert.InRange(bands.Max(), 0.25f, 0.85f);
    }

    [Fact]
    public void MapBands_QuietBandsStayBelowTheLoudOne()
    {
        var (re, im) = SineFrame(1000);
        Spectrum.Transform(re, im);
        var bands = Spectrum.MapBands(re, im, 20, Rate);

        int loudest = Array.IndexOf(bands, bands.Max());
        // a band two steps away from the tone must be clearly lower
        int far = loudest >= 2 ? loudest - 2 : loudest + 2;
        Assert.True(bands[far] < bands[loudest]);
    }

    [Fact]
    public void MapBands_RejectsZeroBands()
    {
        var (re, im) = SilentFrame();
        Assert.Throws<ArgumentOutOfRangeException>(() => Spectrum.MapBands(re, im, 0, Rate));
    }
}
