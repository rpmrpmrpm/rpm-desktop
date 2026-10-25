using RPM.Core.Playback;

namespace RPM.Core.Tests;

/// <summary>
/// A list that ends going on as a mixtape: when it carries on, what the mixtape starts from, and what it
/// leaves out.
/// </summary>
public class MixtapeTests
{
    // ── when a list carries on ──

    [Fact]
    public void The_last_song_of_a_list_carries_on()
    {
        Assert.True(Mixtape.CarriesOn(playing: 11, count: 12, playingIsRadio: false, repeat: false, shuffle: false, djSet: false));
    }

    [Fact]
    public void A_single_carries_on()
    {
        Assert.True(Mixtape.CarriesOn(playing: 0, count: 1, playingIsRadio: false, repeat: false, shuffle: false, djSet: false));
    }

    [Theory]
    [InlineData(0, 12)]
    [InlineData(10, 12)]
    [InlineData(-1, 12)]
    [InlineData(0, 0)]
    public void A_song_before_the_end_does_not_carry_on(int playing, int count)
    {
        Assert.False(Mixtape.CarriesOn(playing, count, playingIsRadio: false, repeat: false, shuffle: false, djSet: false));
    }

    /// <summary>A radio's own song is topped up by its radio; a second radio after it would be a second
    /// mixtape on top of the first.</summary>
    [Fact]
    public void A_radios_own_song_does_not_carry_on()
    {
        Assert.False(Mixtape.CarriesOn(4, 5, playingIsRadio: true, repeat: false, shuffle: false, djSet: false));
    }

    [Fact]
    public void Repeat_shuffle_and_a_dj_set_do_not_carry_on()
    {
        Assert.False(Mixtape.CarriesOn(4, 5, playingIsRadio: false, repeat: true, shuffle: false, djSet: false));
        Assert.False(Mixtape.CarriesOn(4, 5, playingIsRadio: false, repeat: false, shuffle: true, djSet: false));
        Assert.False(Mixtape.CarriesOn(4, 5, playingIsRadio: false, repeat: false, shuffle: false, djSet: true));
    }

    // ── what it starts from ──

    [Fact]
    public void The_mixtape_starts_from_the_lists_first_song_with_the_whole_list_in_its_history()
    {
        var seed = Mixtape.Seed(["a/01.flac", "a/02.flac", "a/03.flac"]);

        Assert.NotNull(seed);
        Assert.Equal("a/01.flac", seed.Value.Origin);
        Assert.Equal(["a/01.flac", "a/02.flac", "a/03.flac"], seed.Value.History);
    }

    /// <summary>A playlist can hold a song twice, and can hold songs from outside the collection; the radio's
    /// history names each song it knows once.</summary>
    [Fact]
    public void Songs_outside_the_collection_and_repeats_are_left_out_of_the_seed()
    {
        var seed = Mixtape.Seed([null, "b/01.flac", "a/01.flac", "B/01.FLAC", ""]);

        Assert.NotNull(seed);
        Assert.Equal("b/01.flac", seed.Value.Origin);
        Assert.Equal(["b/01.flac", "a/01.flac"], seed.Value.History);
    }

    [Fact]
    public void A_list_the_collection_does_not_hold_has_no_seed()
    {
        Assert.Null(Mixtape.Seed([null, null]));
        Assert.Null(Mixtape.Seed([]));
    }

    // ── what it leaves out ──

    [Fact]
    public void The_queue_and_the_songs_heard_lately_are_spared()
    {
        var spared = Mixtape.Spared(["a/01.flac"], ["c/05.flac", "d/02.flac"]);

        Assert.Equal(3, spared.Count);
        Assert.Contains("a/01.flac", spared);
        Assert.Contains("C/05.FLAC", spared);
        Assert.Contains("d/02.flac", spared);
    }

    [Fact]
    public void Only_the_latest_listens_are_spared()
    {
        var heard = Enumerable.Range(0, Mixtape.Spares + 10).Select(i => $"h/{i:000}.flac").ToList();

        var spared = Mixtape.Spared([], heard);

        Assert.Equal(Mixtape.Spares, spared.Count);
        Assert.Contains("h/000.flac", spared);
        Assert.DoesNotContain($"h/{Mixtape.Spares:000}.flac", spared);
    }
}
