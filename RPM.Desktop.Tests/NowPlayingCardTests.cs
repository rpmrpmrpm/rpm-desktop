using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Tests;

/// <summary>
/// What the system's Now Playing tile is told, and when it is told again.
///
/// <para>The tile draws from a statement and then keeps counting on its own, so both parts matter. The
/// statement must be whole and internally consistent: a needle past the end of the record draws a
/// jammed scrubber. The restatement must be rare enough that the normal drift of a 400 ms tick never
/// causes one, while a seek always does.</para>
/// </summary>
public class NowPlayingCardTests
{
    /// <summary>Blank tags are lines the tile leaves out, not empty lines it draws.</summary>
    [Fact]
    public void Blank_tags_are_missing_rather_than_empty()
    {
        var card = NowPlayingCard.For("Blue Monday", "  ", "", 421, 0, playing: true);

        Assert.Equal("Blue Monday", card.Title);
        Assert.Null(card.Artist);
        Assert.Null(card.Album);
    }

    [Fact]
    public void Tags_are_trimmed()
    {
        var card = NowPlayingCard.For(" Blue Monday ", " New Order ", " Power, Corruption & Lies ", 421, 0, true);

        Assert.Equal("Blue Monday", card.Title);
        Assert.Equal("New Order", card.Artist);
        Assert.Equal("Power, Corruption & Lies", card.Album);
    }

    /// <summary>A track with no title at all still goes on the tile: what is playing is a fact, and an
    /// untitled file is one of the things that can be playing.</summary>
    [Fact]
    public void A_track_with_no_title_is_still_a_card()
        => Assert.Equal("", NowPlayingCard.For(null, "New Order", null, 421, 12, true).Title);

    /// <summary>The needle never reads past the end of the record.</summary>
    [Fact]
    public void The_needle_is_held_inside_the_record()
        => Assert.Equal(421, NowPlayingCard.For("Blue Monday", null, null, 421, 500, true).Elapsed);

    [Fact]
    public void A_needle_before_the_start_reads_as_the_start()
        => Assert.Equal(0, NowPlayingCard.For("Blue Monday", null, null, 421, -3, true).Elapsed);

    /// <summary>Nonsense out of an engine mid-load is not passed on.</summary>
    [Fact]
    public void Numbers_that_are_not_numbers_are_dropped()
    {
        var card = NowPlayingCard.For("Blue Monday", null, null, double.NaN, double.PositiveInfinity, true);

        Assert.Equal(0, card.Duration);
        Assert.Equal(0, card.Elapsed);
        Assert.True(card.IsLive);
    }

    /// <summary>A station has no end to count towards and no needle. The tile draws it live, not as a
    /// scrubber pinned at the start.</summary>
    [Fact]
    public void A_station_is_live_and_has_no_needle()
    {
        var card = NowPlayingCard.For("Aphex Twin, Xtal", "NTS Radio", null, 0, 90, playing: true);

        Assert.True(card.IsLive);
        Assert.Equal(0, card.Elapsed);
    }

    // ── when the system has to be told again ─────────────────────────────────────────────────────

    private static NowPlayingCard Playing(double elapsed) =>
        NowPlayingCard.For("Blue Monday", "New Order", "Power, Corruption & Lies", 421, elapsed, true);

    /// <summary>The tick that only moved the playhead: the tile counted to the same place itself, so it
    /// is not told anything.</summary>
    [Fact]
    public void An_ordinary_tick_says_nothing()
        => Assert.False(NowPlayingCard.NeedsRestating(Playing(30), toldSecondsAgo: 0.4, Playing(30.4)));

    /// <summary>A minute of counting with no update is still the tile's own arithmetic.</summary>
    [Fact]
    public void A_minute_of_counting_says_nothing()
        => Assert.False(NowPlayingCard.NeedsRestating(Playing(30), toldSecondsAgo: 60, Playing(90)));

    /// <summary>A seek is the needle landing where the tile's counting would not have reached.</summary>
    [Fact]
    public void A_seek_is_told()
        => Assert.True(NowPlayingCard.NeedsRestating(Playing(30), toldSecondsAgo: 0.4, Playing(200)));

    /// <summary>Backwards too. The tile counts in one direction, so a seek back is also restated.</summary>
    [Fact]
    public void A_seek_backwards_is_told()
        => Assert.True(NowPlayingCard.NeedsRestating(Playing(200), toldSecondsAgo: 0.4, Playing(30)));

    /// <summary>Pausing changes the statement even where the needle has not moved.</summary>
    [Fact]
    public void A_pause_is_told()
    {
        var paused = NowPlayingCard.For("Blue Monday", "New Order", "Power, Corruption & Lies", 421, 30, false);

        Assert.True(NowPlayingCard.NeedsRestating(Playing(30), toldSecondsAgo: 0.4, paused));
    }

    /// <summary>A paused tile does not count. So a needle that has not moved needs no update, but one
    /// that has moved does.</summary>
    [Fact]
    public void A_paused_tile_is_only_told_when_the_needle_moves()
    {
        var paused = NowPlayingCard.For("Blue Monday", "New Order", "Power, Corruption & Lies", 421, 30, false);
        var scrubbed = NowPlayingCard.For("Blue Monday", "New Order", "Power, Corruption & Lies", 421, 200, false);

        Assert.False(NowPlayingCard.NeedsRestating(paused, toldSecondsAgo: 60, paused));
        Assert.True(NowPlayingCard.NeedsRestating(paused, toldSecondsAgo: 60, scrubbed));
    }

    /// <summary>A new record is a new statement, needle or no needle.</summary>
    [Fact]
    public void A_new_record_is_told()
    {
        var next = NowPlayingCard.For("The Perfect Kiss", "New Order", "Low-Life", 512, 0, true);

        Assert.True(NowPlayingCard.NeedsRestating(Playing(30), toldSecondsAgo: 0.4, next));
    }
}
