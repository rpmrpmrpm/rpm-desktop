using RPM.Core.Playback;

namespace RPM.Core.Tests;

/// <summary>
/// What counts as having listened to something. Getting this wrong makes "most played" a record of
/// what somebody skipped past fastest, and the number looks perfectly plausible either way.
/// </summary>
public class ListenTests
{
    [Fact]
    public void Flicking_past_a_track_is_not_a_play()
    {
        Assert.False(Listen.Counts(heardSeconds: 3, lengthSeconds: 240));
    }

    [Fact]
    public void Half_a_track_is_a_play()
    {
        Assert.True(Listen.Counts(heardSeconds: 120, lengthSeconds: 240));
    }

    [Fact]
    public void One_second_short_of_half_is_not()
    {
        Assert.False(Listen.Counts(heardSeconds: 119, lengthSeconds: 240));
    }

    [Fact]
    public void A_track_played_to_the_end_is_a_play()
    {
        Assert.True(Listen.Counts(heardSeconds: 239.5, lengthSeconds: 240));
    }

    /// <summary>The reason the four-minute rule exists: without it a twenty-minute track would need
    /// ten minutes before it counted, which is too long for a normal listen.</summary>
    [Fact]
    public void Four_minutes_of_a_long_side_is_enough()
    {
        Assert.True(Listen.Counts(heardSeconds: 240, lengthSeconds: 20 * 60));
        Assert.False(Listen.Counts(heardSeconds: 239, lengthSeconds: 20 * 60));
    }

    [Fact]
    public void A_short_track_never_counts()
    {
        // An interlude, a locked groove, a needle drop. Heard in full, and still not a play.
        Assert.False(Listen.Counts(heardSeconds: 20, lengthSeconds: 20));
    }

    [Fact]
    public void Exactly_thirty_seconds_is_long_enough_to_be_a_track()
    {
        Assert.True(Listen.Counts(heardSeconds: 15, lengthSeconds: 30));
    }

    /// <summary>The failure this is really guarding: a source that opened, produced nothing and
    /// ended. The engine reports that as a track that finished, and an album of them would otherwise
    /// come back as the most played album in the library.</summary>
    [Fact]
    public void A_track_that_made_no_sound_is_not_a_play()
    {
        Assert.False(Listen.Counts(heardSeconds: 0, lengthSeconds: 240));
    }

    [Fact]
    public void A_row_with_no_length_counts_for_nothing()
    {
        // Not backfilled. Guessing at a length here would count every flick past every untagged
        // track, and those are exactly the rows a phone meets first.
        Assert.False(Listen.Counts(heardSeconds: 300, lengthSeconds: 0));
    }

    [Fact]
    public void Negative_nonsense_is_not_a_play()
    {
        Assert.False(Listen.Counts(heardSeconds: -1, lengthSeconds: 240));
    }
}
