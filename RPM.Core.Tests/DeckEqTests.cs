using RPM.Core.Audio;

namespace RPM.Core.Tests;

/// <summary>
/// What the three knobs actually do to a record, measured, not assumed.
///
/// <para>A biquad is six coefficients, and a sign error in any of them still produces a filter: one
/// that runs, stays stable, and has the wrong shape. So nothing here reads the coefficients. Every
/// test puts a tone of a known frequency through the filter and measures its level at the output.
/// That is what a DJ needs from an EQ: when the bass is turned down, the bass goes down and nothing
/// else changes.</para>
/// </summary>
public class DeckEqTests
{
    private const int Rate = 44100, Channels = 2;

    /// <summary>Tones on each side of each crossing, and one in the middle of every band.</summary>
    private const double DeepBass = 60, Bass = 150, LowMid = 500, Mid = 1000, HighMid = 2500,
                         Presence = 6000, Air = 12000;

    /// <summary>
    /// How much of a tone at <paramref name="hz"/> survives the filter, in dB.
    ///
    /// <para>Measured after a settling period is discarded, because a biquad started from a cleared
    /// delay line takes a few cycles to reach its steady state. A reading taken during that period
    /// measures the transient.</para>
    /// </summary>
    private static double GainDb(DeckEq eq, double hz)
    {
        const int settle = Rate / 2, measure = Rate;

        var block = new float[(settle + measure) * Channels];
        for (int f = 0; f < settle + measure; f++)
        {
            var v = (float)(0.5 * Math.Sin(2 * Math.PI * hz * f / Rate));
            for (int c = 0; c < Channels; c++)
                block[f * Channels + c] = v;
        }

        eq.Reset();
        eq.Process(block, settle + measure);

        double sum = 0;
        for (int f = settle; f < settle + measure; f++)
        {
            double v = block[f * Channels];
            sum += v * v;
        }

        double rms = Math.Sqrt(sum / measure);
        double reference = 0.5 / Math.Sqrt(2);          // RMS of the tone that went in

        return 20 * Math.Log10(Math.Max(rms, 1e-12) / reference);
    }

    private static DeckEq Eq(double low, double mid, double high)
    {
        var eq = new DeckEq(Rate, Channels);
        eq.SetGains(low, mid, high);
        return eq;
    }

    // ── flat ─────────────────────────────────────────────────────────────────

    /// <summary>Three knobs at the centre change nothing anywhere. This is the first thing an EQ must
    /// get right.</summary>
    [Theory]
    [InlineData(DeepBass)]
    [InlineData(Bass)]
    [InlineData(LowMid)]
    [InlineData(Mid)]
    [InlineData(HighMid)]
    [InlineData(Presence)]
    [InlineData(Air)]
    public void Flat_changes_nothing(double hz)
    {
        double db = GainDb(Eq(0, 0, 0), hz);
        Assert.True(Math.Abs(db) <= 0.1, $"{hz} Hz moved {db:+0.00;-0.00} dB with the EQ flat");
    }

    /// <summary>A deck whose knobs were never touched is not filtered at all: the block comes back
    /// byte for byte, so an untouched deck costs nothing.</summary>
    [Fact]
    public void An_untouched_deck_is_left_alone_entirely()
    {
        var eq = new DeckEq(Rate, Channels);
        Assert.True(eq.IsFlat);

        var block = new float[256 * Channels];
        var before = new float[block.Length];
        for (int i = 0; i < block.Length; i++)
            before[i] = block[i] = (float)Math.Sin(i * 0.05);

        eq.Process(block, 256);
        Assert.Equal(before, block);
    }

    // ── what each knob owns ──────────────────────────────────────────────────

    /// <summary>
    /// Cutting the bass removes the kick and the bassline and leaves the rest of the record as it is.
    /// </summary>
    [Fact]
    public void The_bass_knob_takes_the_bottom_and_leaves_the_body()
    {
        var eq = Eq(low: -12, mid: 0, high: 0);

        Assert.True(GainDb(eq, DeepBass) <= -9, $"60 Hz only came down {GainDb(eq, DeepBass):F1} dB");
        Assert.True(GainDb(eq, Bass) <= -7, $"150 Hz only came down {GainDb(eq, Bass):F1} dB");

        // The vocal and everything above it stays at the same level.
        Assert.True(Math.Abs(GainDb(eq, Mid)) <= 1.5, $"1 kHz moved {GainDb(eq, Mid):+0.0;-0.0} dB");
        Assert.True(Math.Abs(GainDb(eq, Presence)) <= 0.5, $"6 kHz moved {GainDb(eq, Presence):+0.0;-0.0} dB");
        Assert.True(Math.Abs(GainDb(eq, Air)) <= 0.5, $"12 kHz moved {GainDb(eq, Air):+0.0;-0.0} dB");
    }

    /// <summary>
    /// Cutting the mid removes the body of the record (the vocal, the snare, the keys) and leaves
    /// the kick below it and the hats above it.
    /// </summary>
    [Fact]
    public void The_mid_knob_takes_the_body_and_leaves_the_kick_and_the_hats()
    {
        var eq = Eq(low: 0, mid: -12, high: 0);

        Assert.True(GainDb(eq, Mid) <= -9, $"1 kHz only came down {GainDb(eq, Mid):F1} dB");

        // The kick is still there, and so is the air.
        Assert.True(GainDb(eq, DeepBass) >= -2, $"60 Hz came down {GainDb(eq, DeepBass):F1} dB with only the mid cut");
        Assert.True(GainDb(eq, Air) >= -2, $"12 kHz came down {GainDb(eq, Air):F1} dB with only the mid cut");
    }

    /// <summary>
    /// Cutting the high removes the hats and the air and leaves the vocal intact. This is why the
    /// crossing is at 4 kHz and not lower.
    /// </summary>
    [Fact]
    public void The_high_knob_takes_the_air_and_leaves_the_vocal()
    {
        var eq = Eq(low: 0, mid: 0, high: -12);

        Assert.True(GainDb(eq, Air) <= -9, $"12 kHz only came down {GainDb(eq, Air):F1} dB");
        Assert.True(GainDb(eq, Presence) <= -6, $"6 kHz only came down {GainDb(eq, Presence):F1} dB");

        // A voice is around this frequency, and it is unchanged.
        Assert.True(Math.Abs(GainDb(eq, Mid)) <= 1.5, $"1 kHz moved {GainDb(eq, Mid):+0.0;-0.0} dB");
        Assert.True(Math.Abs(GainDb(eq, DeepBass)) <= 0.5, $"60 Hz moved {GainDb(eq, DeepBass):+0.0;-0.0} dB");
    }

    // ── the crossings ────────────────────────────────────────────────────────

    /// <summary>
    /// The bands divide where the class says they divide. A shelf is half way down at its corner, so
    /// that is where the crossing is measured: 300 Hz between bass and mid, 4 kHz between mid and
    /// high.
    ///
    /// <para>This test fails if somebody changes a constant and nothing else. The layout is the same
    /// as Pioneer's, which DJs already know, and a bass knob that also cuts the vocal works against
    /// them.</para>
    /// </summary>
    [Fact]
    public void The_bands_divide_where_they_are_documented_to()
    {
        // Half of a 24 dB cut, within the tolerance a shelf's own slope allows.
        double atLow = GainDb(Eq(low: -24, mid: 0, high: 0), DeckEq.LowHz);
        Assert.InRange(atLow, -17, -7);

        double atHigh = GainDb(Eq(low: 0, mid: 0, high: -24), DeckEq.HighHz);
        Assert.InRange(atHigh, -17, -7);
    }

    // ── the travel ───────────────────────────────────────────────────────────

    /// <summary>
    /// The knobs have the same range as a DJM's: six dB up, twenty-six down. Boost is the smaller
    /// side on purpose: an EQ is mainly used to cut, and two boosted records clip when summed.
    /// </summary>
    [Fact]
    public void The_travel_is_the_travel_a_mixer_has()
    {
        var boosted = Eq(low: 50, mid: 50, high: 50);
        Assert.Equal(DeckEq.MaxBoostDb, boosted.LowDb, 9);
        Assert.Equal(DeckEq.MaxBoostDb, boosted.MidDb, 9);
        Assert.Equal(DeckEq.MaxBoostDb, boosted.HighDb, 9);

        Assert.True(GainDb(boosted, DeepBass) > 3, "the bass knob at its top barely lifted 60 Hz");

        // Down, the limit is the kill, not the knob's range: a changeover goes further than the knob
        // can, and the clamp is the limit for a changeover.
        var cut = Eq(low: -100, mid: -100, high: -100);
        Assert.Equal(DeckEq.KillDb, cut.LowDb, 9);
        Assert.Equal(DeckEq.KillDb, cut.MidDb, 9);
        Assert.Equal(DeckEq.KillDb, cut.HighDb, 9);

        // The knob's own limit is a deep cut, not yet a kill: the band is still there.
        Assert.InRange(GainDb(Eq(low: DeckEq.MaxCutDb, mid: 0, high: 0), DeepBass), -30, -20);
    }

    /// <summary>
    /// The kill is a full kill, not only a deep cut: a band turned all the way down is gone, not
    /// twenty-six dB lower.
    /// </summary>
    [Theory]
    [InlineData(DeepBass)]
    [InlineData(Air)]
    public void A_killed_band_is_gone_from_the_room(double hz)
    {
        bool low = hz < DeckEq.LowHz;
        var eq = Eq(
            low: low ? DeckEq.KillDb : 0,
            mid: 0,
            high: low ? 0 : DeckEq.KillDb);

        double db = GainDb(eq, hz);
        Assert.True(db <= -40, $"{hz} Hz was only {db:F1} dB down with the band killed");
    }

    /// <summary>
    /// A knob is smooth over its whole range: no gain between the limits produces a jump in the
    /// output. A step here is an audible click on a control that is moved while the record plays.
    /// </summary>
    [Fact]
    public void Turning_a_knob_moves_the_sound_smoothly()
    {
        double last = 0;

        // All the way to the kill, which is where a bass swap goes. There is no special case at the
        // bottom: the filter is really built at every value down to the limit.
        for (double db = 0; db > DeckEq.KillDb + 0.5; db -= 0.5)
        {
            double now = GainDb(Eq(low: db, mid: 0, high: 0), DeepBass);

            if (db < 0)
                Assert.True(
                    now <= last + 0.01,
                    $"turning the bass from {db + 0.5} to {db} made 60 Hz louder");

            Assert.True(
                Math.Abs(now - last) <= 1.5,
                $"60 Hz jumped {Math.Abs(now - last):F2} dB between {db + 0.5} and {db} on the knob");

            last = now;
        }
    }

    /// <summary>No knob setting makes the filter unstable. An unstable biquad grows to infinity and
    /// ruins the whole mix, and it is usually caused by extreme settings that nobody tested.</summary>
    [Theory]
    [InlineData(6, 6, 6)]
    [InlineData(-26, -26, -26)]
    [InlineData(6, -26, 6)]
    [InlineData(-26, 6, -26)]
    [InlineData(-60, -60, -60)]
    [InlineData(-60, 6, -60)]
    public void No_setting_makes_the_filter_run_away(double low, double mid, double high)
    {
        var eq = Eq(low, mid, high);

        var block = new float[4096 * Channels];
        var random = new Random(7);

        // New noise every block, never the filter's own output fed back in: forty passes of a boost
        // over its own result is a gain of hundreds of dB and says nothing about the filter. The test
        // checks whether the delay line settles, which needs a long run of ordinary input, not a
        // loop.
        for (int pass = 0; pass < 40; pass++)
        {
            for (int i = 0; i < block.Length; i++)
                block[i] = (float)(random.NextDouble() * 2 - 1);

            eq.Process(block, 4096);

            foreach (float v in block)
                Assert.True(
                    float.IsFinite(v) && Math.Abs(v) < 100,
                    $"the filter ran away to {v} on block {pass}");
        }
    }
}
