using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The cloud mark one track line wears, and whether a record's lines wear one at all. Both are
/// decisions about what the listener is told, so they are made here rather than in a view: the
/// wall's drawer, the album list and the album page all draw the same tracklist and must say the
/// same thing about it.
/// </summary>
public class TrackCloudBadgeTests
{
    [Theory]
    [InlineData(CloudState.Queued, false, AlbumCloudBadge.Working)]
    [InlineData(CloudState.Uploading, false, AlbumCloudBadge.Working)]
    [InlineData(CloudState.Synced, false, AlbumCloudBadge.Full)]
    [InlineData(CloudState.CloudOnly, false, AlbumCloudBadge.Offloaded)]
    [InlineData(CloudState.Local, false, AlbumCloudBadge.None)]
    public void It_reads_the_track_s_own_state(CloudState state, bool selected, AlbumCloudBadge expected)
        => Assert.Equal(expected, TrackCloudBadge.For(state, selected));

    /// <summary>Chosen and not yet in the cloud: the outline cloud, which is the weakest of the five
    /// marks and the only one that means "meant to be there, but is not".</summary>
    [Fact]
    public void A_chosen_track_that_has_not_gone_up_wears_the_outline_cloud()
    {
        Assert.Equal(AlbumCloudBadge.Partial, TrackCloudBadge.For(CloudState.Local, selected: true));
        // A failed upload is the same statement: the backoff will try again, and until it does the track
        // is chosen and not yet in the cloud.
        Assert.Equal(AlbumCloudBadge.Partial, TrackCloudBadge.For(CloudState.Error, selected: true));
    }

    /// <summary>A track that is in the cloud says so whether or not the selection flag survived: the
    /// state is the stronger fact.</summary>
    [Fact]
    public void Being_up_there_beats_the_selection_flag()
    {
        Assert.Equal(AlbumCloudBadge.Full, TrackCloudBadge.For(CloudState.Synced, selected: true));
        Assert.Equal(AlbumCloudBadge.Offloaded, TrackCloudBadge.For(CloudState.CloudOnly, selected: true));
    }

    /// <summary>An error on a track nobody chose is not a cloud mark: the cloud is neither holding
    /// this track nor asked to hold it.</summary>
    [Fact]
    public void An_unchosen_track_wears_nothing()
        => Assert.Equal(AlbumCloudBadge.None, TrackCloudBadge.For(CloudState.Error, selected: false));

    // ── whether the record's lines speak at all ──

    /// <summary>A record that is wholly in the cloud says so once, on its tile. Sixteen identical
    /// clouds down the tracklist would repeat that fact sixteen times and hide the case worth
    /// finding.</summary>
    [Fact]
    public void Lines_stay_silent_when_the_whole_record_agrees()
    {
        Assert.False(TrackCloudBadge.WorthShowing(
            [AlbumCloudBadge.Full, AlbumCloudBadge.Full, AlbumCloudBadge.Full]));
        Assert.False(TrackCloudBadge.WorthShowing(
            [AlbumCloudBadge.None, AlbumCloudBadge.None]));
        Assert.False(TrackCloudBadge.WorthShowing([AlbumCloudBadge.Working]));
        Assert.False(TrackCloudBadge.WorthShowing([]));
    }

    /// <summary>A subset, which is the whole point of the mark.</summary>
    [Fact]
    public void Lines_speak_when_the_record_disagrees_with_itself()
    {
        Assert.True(TrackCloudBadge.WorthShowing(
            [AlbumCloudBadge.Full, AlbumCloudBadge.None, AlbumCloudBadge.None]));
        // Same set, different order: it is a fact about the record, not about which line comes first.
        Assert.True(TrackCloudBadge.WorthShowing(
            [AlbumCloudBadge.None, AlbumCloudBadge.None, AlbumCloudBadge.Full]));
        // Wholly chosen, but one of them is still on its way: the record is not saying one thing.
        Assert.True(TrackCloudBadge.WorthShowing(
            [AlbumCloudBadge.Full, AlbumCloudBadge.Working]));
    }

    // ── when the engine has stopped ──

    /// <summary>
    /// A full store is not a queue that moves slowly. It is a queue that does not move. The line says
    /// what it is (chosen, not in the cloud) instead of claiming to be on its way for however many
    /// days it takes the user to buy space or free some. That claim is also what makes the mark pulse,
    /// so this stops the mark pulsing over nothing.
    /// </summary>
    [Fact]
    public void A_queued_line_rests_while_nothing_is_being_sent()
    {
        Assert.Equal(AlbumCloudBadge.Partial,
            TrackCloudBadge.For(CloudState.Queued, selected: true, uploadsStopped: true));
        Assert.Equal(AlbumCloudBadge.Partial,
            TrackCloudBadge.For(CloudState.Uploading, selected: true, uploadsStopped: true));
    }

    /// <summary>Everything else is a fact about the track, not about the queue, and does not move.</summary>
    [Fact]
    public void A_stopped_engine_changes_nothing_that_is_already_settled()
    {
        Assert.Equal(AlbumCloudBadge.Full,
            TrackCloudBadge.For(CloudState.Synced, selected: true, uploadsStopped: true));
        Assert.Equal(AlbumCloudBadge.Offloaded,
            TrackCloudBadge.For(CloudState.CloudOnly, selected: true, uploadsStopped: true));
        Assert.Equal(AlbumCloudBadge.None,
            TrackCloudBadge.For(CloudState.Local, selected: false, uploadsStopped: true));
    }

    /// <summary>With the queue running, the mark is as normal.</summary>
    [Fact]
    public void A_queued_line_is_working_while_the_queue_moves()
        => Assert.Equal(AlbumCloudBadge.Working,
            TrackCloudBadge.For(CloudState.Queued, selected: true, uploadsStopped: false));

    /// <summary>
    /// A line of somebody else's record. The mark answers how the song gets here, so the cloud row
    /// that means "using none of my disk" on your own shelf means "the store has it too" on theirs.
    /// Cyan is kept for the case where the store is the only way left.
    /// </summary>
    [Fact]
    public void A_borrowed_line_says_how_the_song_reaches_this_computer()
    {
        Assert.Equal(AlbumCloudBadge.Full,
            TrackCloudBadge.ForBorrowed(CloudState.CloudOnly, ownerReachable: true));
        Assert.Equal(AlbumCloudBadge.Offloaded,
            TrackCloudBadge.ForBorrowed(CloudState.CloudOnly, ownerReachable: false));
        Assert.Equal(AlbumCloudBadge.PeerLink,
            TrackCloudBadge.ForBorrowed(CloudState.Local, ownerReachable: true));
    }
}
