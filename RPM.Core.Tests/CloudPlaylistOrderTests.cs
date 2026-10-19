using RPM.Core.Cloud;

namespace RPM.Core.Tests;

public class CloudPlaylistOrderTests
{
    private static (long, string?)[] Local(params (long Id, string? Rpm)[] items) => items;

    [Fact]
    public void Rearranges_to_the_order_the_cloud_names()
    {
        var ordered = CloudPlaylistOrder.Rearrange(
            Local((10, "a"), (11, "b"), (12, "c")),
            ["c", "a", "b"]);

        Assert.Equal(new long[] { 12, 10, 11 }, ordered);
    }

    [Fact]
    public void Says_nothing_when_the_two_already_agree()
    {
        var ordered = CloudPlaylistOrder.Rearrange(
            Local((10, "a"), (11, "b")),
            ["a", "b"]);

        // An order already in step is not a change to write.
        Assert.Null(ordered);
    }

    [Fact]
    public void Refuses_a_cloud_copy_holding_a_song_this_one_does_not()
    {
        var ordered = CloudPlaylistOrder.Rearrange(
            Local((10, "a"), (11, "b")),
            ["a", "z"]);

        // A different set is not a reorder.
        Assert.Null(ordered);
    }

    [Fact]
    public void Refuses_a_cloud_copy_of_a_different_length()
    {
        Assert.Null(CloudPlaylistOrder.Rearrange(Local((10, "a"), (11, "b")), ["a"]));
        Assert.Null(CloudPlaylistOrder.Rearrange(Local((10, "a")), ["a", "b"]));
    }

    [Fact]
    public void Keeps_both_copies_of_a_song_held_twice()
    {
        var ordered = CloudPlaylistOrder.Rearrange(
            Local((10, "a"), (11, "b"), (12, "a")),
            ["b", "a", "a"]);

        // The first name takes the first item, so neither copy is lost or duplicated.
        Assert.Equal(new long[] { 11, 10, 12 }, ordered);
    }

    [Fact]
    public void Refuses_when_the_same_song_appears_a_different_number_of_times()
    {
        var ordered = CloudPlaylistOrder.Rearrange(
            Local((10, "a"), (11, "a")),
            ["a", "b"]);

        Assert.Null(ordered);
    }

    [Fact]
    public void Refuses_a_playlist_holding_a_song_the_cloud_cannot_name()
    {
        var ordered = CloudPlaylistOrder.Rearrange(
            Local((10, "a"), (11, null)),
            ["a", "b"]);

        // A song with no rpm id can never be named up there.
        Assert.Null(ordered);
    }

    [Fact]
    public void Says_nothing_about_an_empty_playlist()
    {
        Assert.Null(CloudPlaylistOrder.Rearrange(Local(), []));
    }
}
