using RPM.Core.Analysis;

namespace RPM.Core.Tests;

/// <summary>
/// Turning a loudness reading into a gain, and reading the ones that other people's taggers wrote.
///
/// <para>The clamp is the part worth testing hardest. Every failure it prevents is inaudible as a
/// failure: the track plays at the level asked for, with its peaks squared off, which sounds like a
/// bad master and not like a bug in the player.</para>
/// </summary>
public class ReplayGainTests
{
    /// <summary>A track already at the reference level is left alone. This is the commonest case, and
    /// the one where doing anything at all would be wrong.</summary>
    [Fact]
    public void A_track_at_the_reference_level_is_not_touched()
        => Assert.Equal(0, ReplayGain.GainDb(ReplayGain.ReferenceLufs), 6);

    /// <summary>Modern masters sit well above the reference and come down; older and quieter ones go
    /// up. The sign is the whole contract.</summary>
    [Theory]
    [InlineData(-9.0, -9.0)]      // loud master: turned down
    [InlineData(-23.0, 5.0)]      // quiet master: turned up
    [InlineData(-18.0, 0.0)]
    public void The_gain_is_the_distance_to_the_reference(double lufs, double expected)
        => Assert.Equal(expected, ReplayGain.GainDb(lufs), 6);

    /// <summary>A track nobody measured gets no gain, not an infinite one.</summary>
    [Fact]
    public void An_unmeasured_track_gets_no_gain()
    {
        Assert.Equal(0, ReplayGain.GainDb(double.NegativeInfinity), 6);
        Assert.Equal(0, ReplayGain.For(double.NegativeInfinity, 0.5), 6);
    }

    /// <summary>
    /// The clamp only ever holds a gain back.
    ///
    /// <para>A quiet recording with quiet peaks (a solo piano, most of the classical shelf) has
    /// headroom to spare, and the ceiling is a limit, not a target. Pushing the gain up to meet the
    /// ceiling would be the opposite of levelling.</para>
    /// </summary>
    [Fact]
    public void A_track_with_headroom_to_spare_keeps_the_gain_it_asked_for()
    {
        // 6 dB up on a track peaking at 0.1 lands near 0.2, nowhere near the ceiling.
        Assert.Equal(6.0, ReplayGain.Clamp(6.0, truePeak: 0.1), 6);
    }

    /// <summary>A boost that would push the peaks over the ceiling is cut back to exactly the
    /// ceiling. This is the case that would otherwise clip.</summary>
    [Fact]
    public void A_boost_is_cut_back_to_the_ceiling()
    {
        // Peaking at full scale already, so the ceiling allows exactly its own value and no more.
        double gain = ReplayGain.Clamp(6.0, truePeak: 1.0);

        Assert.Equal(ReplayGain.PeakCeilingDb, gain, 6);
        Assert.True(20 * Math.Log10(1.0 * ReplayGain.ToLinear(gain)) <= ReplayGain.PeakCeilingDb + 1e-9);
    }

    /// <summary>
    /// A loud master is turned DOWN, and the clamp must not stand in the way of that.
    ///
    /// <para>The headroom of a track peaking at full scale is negative, and a naive minimum against
    /// it would be right here by accident. The test is that the attenuation asked for survives when
    /// it is larger than the headroom demands.</para>
    /// </summary>
    [Fact]
    public void An_attenuation_is_never_turned_into_a_boost()
    {
        double gain = ReplayGain.For(lufs: -6.0, truePeak: 1.0);

        Assert.Equal(-12.0, gain, 6);
        Assert.True(gain < 0);
    }

    /// <summary>Nothing moves further than <see cref="ReplayGain.MaxGainDb"/>. A reading that asks
    /// for more than that is almost always a mis-measured file, and obeying it would do more harm
    /// than good.</summary>
    [Theory]
    [InlineData(-60.0)]
    [InlineData(-90.0)]
    public void A_wild_reading_cannot_move_a_track_further_than_the_limit(double lufs)
        => Assert.Equal(ReplayGain.MaxGainDb, ReplayGain.For(lufs, truePeak: 0.001), 6);

    /// <summary>A track whose peak was never measured is still levelled. There is simply nothing to
    /// clamp against (the usual case for tags written before 2014), and that is better than no gain at
    /// all.</summary>
    [Fact]
    public void A_missing_peak_does_not_stop_the_gain()
        => Assert.Equal(5.0, ReplayGain.Clamp(5.0, truePeak: 0), 6);

    /// <summary>Zero dB is a multiplier of one. The one arithmetic slip possible here would make every
    /// unmeasured track silent.</summary>
    [Fact]
    public void No_gain_is_a_multiplier_of_one()
    {
        Assert.Equal(1.0, ReplayGain.ToLinear(0), 6);
        Assert.Equal(2.0, ReplayGain.ToLinear(6.0206), 3);
        Assert.Equal(0.5, ReplayGain.ToLinear(-6.0206), 3);
    }

    /// <summary>
    /// An album's loudness is the average of its energy, not of its decibels.
    ///
    /// <para>A record with a loud song and a quiet one is nearly as loud as the loud one. That is what
    /// a listener hears and what levelling has to match. Averaging the two dB figures instead would
    /// put the album three dB below where it belongs and turn the whole thing up into the
    /// ceiling.</para>
    /// </summary>
    [Fact]
    public void Album_loudness_averages_the_energy_rather_than_the_decibels()
    {
        double album = ReplayGain.AlbumLufs([(-10.0, 200), (-20.0, 200)]);

        Assert.True(album > -13.5 && album < -12.5,
            $"two tracks at -10 and -20 LUFS should average near -12.6 LUFS, got {album:F2}");
    }

    /// <summary>Length counts: a short interlude does not drag a whole side down to its own
    /// level.</summary>
    [Fact]
    public void A_short_track_counts_for_less_than_a_long_one()
    {
        double even = ReplayGain.AlbumLufs([(-8.0, 300), (-20.0, 300)]);
        double lopsided = ReplayGain.AlbumLufs([(-8.0, 300), (-20.0, 10)]);

        Assert.True(lopsided > even,
            "a ten-second quiet track should barely move the album, where a five-minute one does");
    }

    /// <summary>An album nobody measured has no loudness, and not a loudness of nothing.</summary>
    [Fact]
    public void An_album_with_nothing_measured_has_no_loudness()
        => Assert.True(double.IsNegativeInfinity(ReplayGain.AlbumLufs([])));

    /// <summary>Tracks with no reading are skipped and not counted as silence: half an album measured
    /// is still a usable album reading.</summary>
    [Fact]
    public void Unmeasured_tracks_do_not_drag_an_album_down()
    {
        double all = ReplayGain.AlbumLufs([(-12.0, 200), (-12.0, 200)]);
        double half = ReplayGain.AlbumLufs([(-12.0, 200), (double.NegativeInfinity, 200)]);

        Assert.Equal(all, half, 6);
    }

    /// <summary>
    /// The tag as a dozen taggers over twenty-five years have written it.
    ///
    /// <para>Every one of these is a real spelling found in real libraries. A reader that accepted
    /// only the canonical one would reject a perfectly good reading and decode the file again for
    /// nothing.</para>
    /// </summary>
    [Theory]
    [InlineData("-6.54 dB", -6.54)]
    [InlineData("-6.54dB", -6.54)]
    [InlineData("+3.21 dB", 3.21)]
    [InlineData("  -6.54 DB  ", -6.54)]
    [InlineData("0.00 dB", 0)]
    [InlineData("-6.54", -6.54)]
    public void A_gain_tag_is_read_however_it_was_written(string tag, double expected)
    {
        Assert.True(ReplayGain.TryParseGain(tag, out var gain));
        Assert.Equal(expected, gain, 4);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("dB")]
    [InlineData("loud")]
    public void A_gain_tag_that_says_nothing_is_declined(string? tag)
        => Assert.False(ReplayGain.TryParseGain(tag, out _));

    /// <summary>A peak tag is a linear amplitude. Anything that will not parse, or that says the
    /// track has no peak, is rejected and not smoothed over: a wrong peak clamps a gain to the wrong
    /// place.</summary>
    [Theory]
    [InlineData("0.987654", 0.987654)]
    [InlineData("1.000000", 1.0)]
    [InlineData("1.148000", 1.148)]      // over full scale: legal, and exactly why the tag exists
    public void A_peak_tag_is_read_as_a_linear_amplitude(string tag, double expected)
    {
        Assert.True(ReplayGain.TryParsePeak(tag, out var peak));
        Assert.Equal(expected, peak, 6);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.5")]
    [InlineData("")]
    [InlineData("none")]
    public void A_peak_tag_that_says_nothing_usable_is_declined(string tag)
        => Assert.False(ReplayGain.TryParsePeak(tag, out _));

    /// <summary>What is written is what is read back. The tags travel to other players, so the
    /// spelling is a contract and not a detail.</summary>
    [Theory]
    [InlineData(-6.54)]
    [InlineData(0)]
    [InlineData(3.2)]
    public void A_gain_survives_being_written_and_read_again(double gain)
    {
        Assert.True(ReplayGain.TryParseGain(ReplayGain.FormatGain(gain), out var round));
        Assert.Equal(gain, round, 2);
    }

    /// <summary>
    /// A stored gain stands in for a measurement, so that one number reaches playback whether it was
    /// read out of a file or worked out here.
    /// </summary>
    [Fact]
    public void A_tagged_gain_reads_back_as_the_loudness_it_implies()
    {
        // A tagger levelling to -18 writes "+5 dB", which states that the track is at -23.
        double lufs = ReplayGain.LufsFromGain(5.0);

        Assert.Equal(-23.0, lufs, 6);
        Assert.Equal(5.0, ReplayGain.GainDb(lufs), 6);
    }
}
