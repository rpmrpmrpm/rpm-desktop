using RPM.Core.Analysis;

namespace RPM.Core.Tests;

/// <summary>
/// Which records get levelled, what they get levelled against, and which setting wins.
///
/// <para>Every one of these decisions fails silently. A record levelled that should have been left
/// alone plays perfectly, at a level nobody chose, and reports nothing. So the order of precedence
/// between the three scopes is tested here, instead of being found from a symphony that was
/// flattened.</para>
/// </summary>
public class NormalizationTests
{
    private static readonly Level Loud = new(-9.0, 0.99);      // a modern master
    private static readonly Level Quiet = new(-23.0, 0.4);     // an old, dynamic one
    private static readonly Level Album = new(-12.0, 0.99);

    // ── whether it happens at all ──

    [Fact]
    public void Off_means_nothing_is_touched()
    {
        var decision = Normalization.Resolve(new NormalizeContext(NormalizeMode.Off), Loud, Album);

        Assert.Equal(NormalizeBasis.None, decision.Basis);
        Assert.Equal(0, decision.GainDb, 6);
        Assert.False(decision.Applies);
    }

    [Fact]
    public void On_by_default_a_loud_master_is_brought_down()
    {
        var decision = Normalization.Resolve(new NormalizeContext(NormalizeMode.Track), Loud, Level.Unknown);

        Assert.Equal(NormalizeBasis.Track, decision.Basis);
        Assert.Equal(-9.0, decision.GainDb, 6);
    }

    // ── the classical rule ──

    /// <summary>
    /// The main case for the rule. A symphony's quiet movement is meant to be quiet: the difference
    /// between it and the finale is part of the performance, and levelling would treat that difference
    /// as two records at different levels and try to remove it.
    /// </summary>
    [Fact]
    public void A_classical_record_is_left_alone_by_default()
    {
        var context = new NormalizeContext(NormalizeMode.Album, AlbumIsClassical: true);

        Assert.False(Normalization.IsOn(context));
        Assert.Equal(0, Normalization.Resolve(context, Quiet, Album).GainDb, 6);
    }

    [Fact]
    public void A_classical_record_is_levelled_when_the_listener_asks_for_that()
    {
        var context = new NormalizeContext(NormalizeMode.Album, IncludeClassical: true, AlbumIsClassical: true);

        Assert.True(Normalization.IsOn(context));
        Assert.Equal(NormalizeBasis.Album, Normalization.Resolve(context, Quiet, Album).Basis);
    }

    /// <summary>The rule is about classical records only. A pop record is levelled whatever the
    /// classical setting says.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_classical_rule_does_not_touch_anything_else(bool includeClassical)
    {
        var context = new NormalizeContext(NormalizeMode.Album, IncludeClassical: includeClassical);

        Assert.True(Normalization.IsOn(context));
    }

    // ── the per-album override ──

    [Fact]
    public void A_record_switched_off_is_left_alone_however_the_settings_read()
    {
        var context = new NormalizeContext(NormalizeMode.Album, AlbumOverride: false);

        Assert.False(Normalization.IsOn(context));
        Assert.Equal(0, Normalization.Resolve(context, Loud, Album).GainDb, 6);
    }

    /// <summary>
    /// Switching one record ON beats the classical rule.
    ///
    /// <para>A user who turns levelling on for a box set has said something more specific than
    /// "leave classical alone". If the record's kind overruled that, the setting would seem not to
    /// work, which is the worst way for a setting to fail.</para>
    /// </summary>
    [Fact]
    public void A_record_switched_on_beats_the_classical_rule()
    {
        var context = new NormalizeContext(NormalizeMode.Album, AlbumIsClassical: true, AlbumOverride: true);

        Assert.True(Normalization.IsOn(context));
        Assert.Equal(NormalizeBasis.Album, Normalization.Resolve(context, Quiet, Album).Basis);
    }

    /// <summary>It also beats the global off switch, for the same reason in the other
    /// direction.</summary>
    [Fact]
    public void A_record_switched_on_beats_the_global_off_switch()
    {
        var context = new NormalizeContext(NormalizeMode.Off, AlbumOverride: true);

        Assert.True(Normalization.IsOn(context));
    }

    /// <summary>Null is the common case and means "nobody has set anything for this record". It
    /// passes the question to the rules below it instead of answering it.</summary>
    [Fact]
    public void No_answer_for_a_record_leaves_the_settings_in_charge()
    {
        Assert.True(Normalization.IsOn(new NormalizeContext(NormalizeMode.Album, AlbumOverride: null)));
        Assert.False(Normalization.IsOn(new NormalizeContext(NormalizeMode.Off, AlbumOverride: null)));
    }

    // ── which reading is used ──

    [Fact]
    public void Album_mode_levels_the_record_as_a_whole()
    {
        var decision = Normalization.Resolve(new NormalizeContext(NormalizeMode.Album), Loud, Album);

        Assert.Equal(NormalizeBasis.Album, decision.Basis);
        Assert.Equal(-6.0, decision.GainDb, 6);        // the album is at -12, the reference is -18
    }

    /// <summary>
    /// Shuffle uses the track's own reading even in album mode.
    ///
    /// <para>Other players that do this use the same convention, and it is correct: the quiet song on
    /// a record is meant to be quiet in its place, and is just a quiet song when it plays between two
    /// songs from other records.</para>
    /// </summary>
    [Fact]
    public void A_shuffled_queue_levels_each_track_on_its_own()
    {
        var decision = Normalization.Resolve(
            new NormalizeContext(NormalizeMode.Album, InAlbumOrder: false), Loud, Album);

        Assert.Equal(NormalizeBasis.Track, decision.Basis);
        Assert.Equal(-9.0, decision.GainDb, 6);
    }

    /// <summary>Album mode on a record the pass has not finished measuring falls back to the track's
    /// own reading. A half-measured library plays at even levels instead of waiting.</summary>
    [Fact]
    public void Album_mode_falls_back_to_the_track_when_the_record_is_not_measured_through()
    {
        var decision = Normalization.Resolve(new NormalizeContext(NormalizeMode.Album), Loud, Level.Unknown);

        Assert.Equal(NormalizeBasis.Track, decision.Basis);
        Assert.Equal(-9.0, decision.GainDb, 6);
    }

    [Fact]
    public void Track_mode_ignores_the_album_reading_entirely()
    {
        var decision = Normalization.Resolve(new NormalizeContext(NormalizeMode.Track), Loud, Album);

        Assert.Equal(NormalizeBasis.Track, decision.Basis);
        Assert.Equal(-9.0, decision.GainDb, 6);
    }

    // ── nothing measured ──

    [Fact]
    public void A_track_nobody_has_measured_plays_untouched()
    {
        var decision = Normalization.Resolve(
            new NormalizeContext(NormalizeMode.Album), Level.Unknown, Level.Unknown);

        Assert.Equal(NormalizeBasis.None, decision.Basis);
        Assert.Equal(0, decision.GainDb, 6);
    }

    /// <summary>A file the meter could not read is stored as negative infinity, which is a stored
    /// answer, not a missing value. It must not be treated as a measurement.</summary>
    [Fact]
    public void A_track_measured_as_unmeasurable_plays_untouched()
    {
        var unmeasurable = new Level(double.NegativeInfinity, null);

        Assert.False(unmeasurable.Known);
        Assert.Equal(NormalizeBasis.None,
            Normalization.Resolve(new NormalizeContext(NormalizeMode.Track), unmeasurable, Level.Unknown).Basis);
    }

    // ── clipping ──

    /// <summary>
    /// A quiet record is turned up only as far as its peaks allow.
    ///
    /// <para>A dynamic recording at −23 LUFS needs 5 dB, and 5 dB on something already peaking near
    /// full scale is 5 dB of clipping. This limit is the reason the true peak is measured.</para>
    /// </summary>
    [Fact]
    public void A_boost_is_held_back_by_the_peaks()
    {
        var atTheCeiling = new Level(-23.0, 1.0);

        var decision = Normalization.Resolve(new NormalizeContext(NormalizeMode.Track), atTheCeiling, Level.Unknown);

        Assert.Equal(ReplayGain.PeakCeilingDb, decision.GainDb, 6);
        Assert.True(1.0 * decision.Linear <= 1.0);
    }

    /// <summary>The same record with enough headroom gets the whole gain it needs.</summary>
    [Fact]
    public void A_boost_is_not_held_back_when_there_is_room()
    {
        var decision = Normalization.Resolve(new NormalizeContext(NormalizeMode.Track), Quiet, Level.Unknown);

        Assert.Equal(5.0, decision.GainDb, 6);
    }

    /// <summary>An album gain is limited by the whole record's loudest peak. Limiting each track by
    /// its own peak would turn one song down relative to the others, which is exactly what album mode
    /// exists to prevent.</summary>
    [Fact]
    public void An_album_boost_is_held_back_by_the_records_loudest_moment()
    {
        var quietAlbumLoudPeak = new Level(-24.0, 1.0);

        var decision = Normalization.Resolve(
            new NormalizeContext(NormalizeMode.Album), Quiet, quietAlbumLoudPeak);

        Assert.Equal(NormalizeBasis.Album, decision.Basis);
        Assert.Equal(ReplayGain.PeakCeilingDb, decision.GainDb, 6);
    }

    // ── the pipeline's own question ──

    /// <summary>A gain too small to hear is not worth taking the audio pipeline off its bypass
    /// for.</summary>
    [Fact]
    public void An_inaudible_gain_does_not_count_as_applying()
    {
        var alreadyThere = new Level(ReplayGain.ReferenceLufs, 0.5);

        var decision = Normalization.Resolve(new NormalizeContext(NormalizeMode.Track), alreadyThere, Level.Unknown);

        Assert.False(decision.Applies);
        Assert.Equal(1f, decision.Linear, 4);
    }
}
