using RPM.Core.Audio;

namespace RPM.Core.Tests;

public class EqualizerTests
{
    private const int Rate = 44100;

    private static float[] Tone(double hz, int frames, int channels, double amplitude = 0.5)
    {
        var buf = new float[frames * channels];
        for (int i = 0; i < frames; i++)
        {
            var v = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate));
            for (int c = 0; c < channels; c++) buf[i * channels + c] = v;
        }
        return buf;
    }

    /// <summary>RMS of the back half, so the filter's start-up transient doesn't skew the reading.</summary>
    private static double SettledRms(float[] buf)
    {
        int from = buf.Length / 2;
        double sum = 0;
        for (int i = from; i < buf.Length; i++) sum += buf[i] * (double)buf[i];
        return Math.Sqrt(sum / (buf.Length - from));
    }

    private static double Rms(float[] buf)
    {
        double sum = 0;
        foreach (var v in buf) sum += v * (double)v;
        return Math.Sqrt(sum / buf.Length);
    }

    private static double[] Gains(int band, double db)
    {
        var g = new double[Equalizer.Bands.Length];
        if (band >= 0) g[band] = db;
        return g;
    }

    // ── bypass ──

    [Fact]
    public void FlatEq_IsBypassed()
    {
        var eq = new Equalizer(Rate, 2);
        Assert.True(eq.IsBypassed);
    }

    [Fact]
    public void FlatEq_LeavesTheSignalUntouched()
    {
        var eq = new Equalizer(Rate, 2);
        var buf = Tone(1000, 512, 2);
        var original = (float[])buf.Clone();

        eq.Process(buf, 512);

        Assert.Equal(original, buf);
    }

    [Fact]
    public void AnyNonZeroGain_LeavesBypass()
    {
        var eq = new Equalizer(Rate, 2);
        eq.SetGains(0, Gains(band: 4, db: 6), 0, 0);
        Assert.False(eq.IsBypassed);
    }

    [Fact]
    public void PreampAlone_LeavesBypass()
    {
        var eq = new Equalizer(Rate, 2);
        eq.SetGains(3, Gains(-1, 0), 0, 0);
        Assert.False(eq.IsBypassed);
    }

    // ── band gain ──

    [Fact]
    public void BoostingABand_MakesAToneAtThatFrequencyLouder()
    {
        const int band = 4;                          // 1 kHz
        double hz = Equalizer.Bands[band];

        var flat = Tone(hz, 8192, 1);
        var boosted = Tone(hz, 8192, 1);

        var eq = new Equalizer(Rate, 1);
        eq.SetGains(0, Gains(band, 10), 0, 0);
        eq.Process(boosted, 8192);

        Assert.True(SettledRms(boosted) > SettledRms(flat) * 1.5,
            $"boosted {SettledRms(boosted)} should clearly exceed flat {SettledRms(flat)}");
    }

    [Fact]
    public void CuttingABand_MakesAToneAtThatFrequencyQuieter()
    {
        const int band = 4;
        double hz = Equalizer.Bands[band];

        var flat = Tone(hz, 8192, 1);
        var cut = Tone(hz, 8192, 1);

        var eq = new Equalizer(Rate, 1);
        eq.SetGains(0, Gains(band, -10), 0, 0);
        eq.Process(cut, 8192);

        Assert.True(SettledRms(cut) < SettledRms(flat) * 0.7,
            $"cut {SettledRms(cut)} should be clearly below flat {SettledRms(flat)}");
    }

    [Fact]
    public void BoostingOneBand_BarelyTouchesADistantFrequency()
    {
        // boost 60 Hz, measure a 6 kHz tone: it should be almost unaffected
        var flat = Tone(6000, 8192, 1);
        var processed = Tone(6000, 8192, 1);

        var eq = new Equalizer(Rate, 1);
        eq.SetGains(0, Gains(band: 0, db: 12), 0, 0);
        eq.Process(processed, 8192);

        var ratio = SettledRms(processed) / SettledRms(flat);
        Assert.InRange(ratio, 0.85, 1.15);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(9)]
    public void EveryBandRespondsAtItsOwnCentreFrequency(int band)
    {
        double hz = Equalizer.Bands[band];
        var flat = Tone(hz, 16384, 1);
        var boosted = Tone(hz, 16384, 1);

        var eq = new Equalizer(Rate, 1);
        eq.SetGains(0, Gains(band, 12), 0, 0);
        eq.Process(boosted, 16384);

        Assert.True(SettledRms(boosted) > SettledRms(flat) * 1.3,
            $"band {band} ({hz} Hz): boosted {SettledRms(boosted)} vs flat {SettledRms(flat)}");
    }

    // ── pre-amp ──

    [Fact]
    public void NegativePreamp_AttenuatesTheWholeSignal()
    {
        var buf = Tone(1000, 4096, 1, amplitude: 0.5);
        var before = Rms(buf);

        var eq = new Equalizer(Rate, 1);
        eq.SetGains(-6, Gains(-1, 0), 0, 0);      // -6 dB ≈ ×0.5
        eq.Process(buf, 4096);

        Assert.InRange(Rms(buf) / before, 0.4, 0.6);
    }

    // ── safety / state ──

    [Fact]
    public void Output_IsClampedToFullScale()
    {
        var buf = Tone(1000, 4096, 1, amplitude: 0.99);
        var eq = new Equalizer(Rate, 1);
        eq.SetGains(12, Gains(band: 4, db: 12), 0, 0);   // deliberately drive it into clipping
        eq.Process(buf, 4096);

        Assert.All(buf, v => Assert.InRange(v, -1f, 1f));
    }

    [Fact]
    public void Reset_ClearsFilterMemory_SoTheSameInputGivesTheSameOutput()
    {
        var eq = new Equalizer(Rate, 1);
        eq.SetGains(0, Gains(band: 4, db: 10), 0, 0);

        var first = Tone(1000, 1024, 1);
        eq.Process(first, 1024);

        eq.Reset();

        var second = Tone(1000, 1024, 1);
        eq.Process(second, 1024);

        Assert.Equal(first, second);
    }

    [Fact]
    public void StereoIsFilteredPerChannel_IdenticalChannelsStayIdentical()
    {
        var eq = new Equalizer(Rate, 2);
        eq.SetGains(0, Gains(band: 4, db: 10), 0, 0);

        var buf = Tone(1000, 2048, 2);
        eq.Process(buf, 2048);

        for (int i = 0; i < 2048; i++)
            Assert.Equal(buf[i * 2], buf[i * 2 + 1], 5);
    }

    [Fact]
    public void GainsAreClampedToTwelveDb()
    {
        var eq = new Equalizer(Rate, 1);
        var wild = Tone(1000, 8192, 1);
        eq.SetGains(0, Gains(band: 4, db: 100), 0, 0);   // absurd request
        eq.Process(wild, 8192);

        var sane = Tone(1000, 8192, 1);
        var eq2 = new Equalizer(Rate, 1);
        eq2.SetGains(0, Gains(band: 4, db: 12), 0, 0);
        eq2.Process(sane, 8192);

        // clamped to +12 dB, so it must behave exactly like an explicit +12 dB request
        Assert.Equal(SettledRms(sane), SettledRms(wild), 3);
    }

    [Fact]
    public void RejectsUnsupportedChannelCounts()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new Equalizer(Rate, 3));

    // ── the tone shelves ──

    /// <summary>Decibels of gain the settled output has over the input, at one frequency.</summary>
    private static double GainDbAt(double hz, Action<Equalizer> tune)
    {
        var eq = new Equalizer(Rate, 1);
        tune(eq);
        var buf = Tone(hz, 16384, 1, amplitude: 0.2);
        double before = SettledRms(buf);
        eq.Process(buf, 16384);
        return 20 * Math.Log10(SettledRms(buf) / before);
    }

    [Fact]
    public void FlatShelves_LeaveTheEqBypassed()
    {
        var eq = new Equalizer(Rate, 2);
        eq.SetGains(0, Gains(-1, 0), 0, 0);
        Assert.True(eq.IsBypassed);
    }

    [Theory]
    [InlineData(12.0)]
    [InlineData(-12.0)]
    public void ABassShelfAloneTakesTheEqOutOfBypass(double db)
    {
        var eq = new Equalizer(Rate, 2);
        eq.SetGains(0, Gains(-1, 0), db, 0);
        Assert.False(eq.IsBypassed);
    }

    [Fact]
    public void ATrebleShelfAloneTakesTheEqOutOfBypass()
    {
        var eq = new Equalizer(Rate, 2);
        eq.SetGains(0, Gains(-1, 0), 0, 12);
        Assert.False(eq.IsBypassed);
    }

    /// <summary>
    /// This is why the shelves exist, instead of turning up the two end bands.
    ///
    /// <para>A peaking filter is a bump: away from its centre its response returns to 0 dB, so the
    /// 60 Hz band raises 60 Hz and lets 25 Hz fall back towards no boost. A low shelf keeps its gain
    /// all the way down. If this test fails because the shelf has become a twelfth band, the
    /// feature is gone but the controls are still on screen.</para>
    /// </summary>
    [Fact]
    public void TheBassShelfHoldsItsGainBelowTheCorner_WhereTheLowestBandDoesNot()
    {
        double shelf = GainDbAt(25, eq => eq.SetGains(0, Gains(-1, 0), 12, 0));
        double band = GainDbAt(25, eq => eq.SetGains(0, Gains(band: 0, db: 12), 0, 0));

        Assert.True(shelf > 10, $"the shelf should still be near +12 dB at 25 Hz, was {shelf:F2}");
        Assert.True(band < shelf - 3, $"the 60 Hz band should have fallen away by 25 Hz, was {band:F2}");
    }

    /// <summary>The same argument at the other end: above the treble corner the shelf stays up where
    /// the top band has come back down.</summary>
    [Fact]
    public void TheTrebleShelfHoldsItsGainAboveTheCorner_WhereTheTopBandDoesNot()
    {
        double shelf = GainDbAt(18000, eq => eq.SetGains(0, Gains(-1, 0), 0, 12));
        double band = GainDbAt(18000, eq => eq.SetGains(0, Gains(band: 5, db: 12), 0, 0));

        Assert.True(shelf > 10, $"the shelf should still be near +12 dB at 18 kHz, was {shelf:F2}");
        Assert.True(band < shelf - 3, $"the 3 kHz band should have fallen away by 18 kHz, was {band:F2}");
    }

    /// <summary>The documented meaning of the corner frequency: half the shelf's dB gain, not its
    /// start or its end. Both shelves here are defined that way, the same as Audacity's two.</summary>
    [Fact]
    public void TheCornerFrequencyIsWhereHalfTheGainHasBeenReached()
    {
        double atCorner = GainDbAt(Equalizer.BassHz, eq => eq.SetGains(0, Gains(-1, 0), 12, 0));
        Assert.InRange(atCorner, 5.0, 7.0);
    }

    [Fact]
    public void TheShelvesAreClampedToTwelveDb()
    {
        double wild = GainDbAt(25, eq => eq.SetGains(0, Gains(-1, 0), 100, 0));
        double sane = GainDbAt(25, eq => eq.SetGains(0, Gains(-1, 0), 12, 0));
        Assert.Equal(sane, wild, 3);
    }

    /// <summary>A shelf and the bands are separate stages of one cascade, so their gains add; one does
    /// not replace the other. This is what "an extra pair" means in decibels.</summary>
    [Fact]
    public void AShelfAddsToTheBandsRatherThanReplacingThem()
    {
        double bandOnly = GainDbAt(60, eq => eq.SetGains(0, Gains(band: 0, db: 6), 0, 0));
        double both = GainDbAt(60, eq => eq.SetGains(0, Gains(band: 0, db: 6), 6, 0));

        Assert.True(both > bandOnly + 3, $"band {bandOnly:F2} dB, band+shelf {both:F2} dB");
    }
}
