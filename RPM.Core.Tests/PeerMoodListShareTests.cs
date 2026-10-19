using RPM.Core.Cloud;
using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>
/// The owner's generated lists, sent to whoever can see their shelf.
///
/// <para>They are sent inside the catalogue, not pushed separately: the catalogue is already fetched
/// on every visit and already has a version hash, so including the cut IN that hash is what makes a
/// new cut reach a borrower. The rest of these tests are about what must NOT happen: a borrower
/// re-cutting lists the owner has never seen, and a list trimmed to what one connection could play
/// being kept after that connection is gone.</para>
/// </summary>
public class PeerMoodListShareTests
{
    private static PeerTrack Track(string id) => new()
    {
        RpmId = id,
        RelativePath = $"a/{id}.flac",
        Title = id,
    };

    private static MoodListsPayload Cut(params MoodListPayload[] lists) => new(1, lists);

    // ── the version, which is what makes a new cut arrive ──

    [Fact]
    public void A_fresh_cut_is_a_fresh_catalog()
    {
        // A new cut changes no track row. A version computed without it would answer "still the
        // same" to a borrower with a whole shelf of new lists to collect, and the change would then
        // wait for the next track to be added, possibly for ever.
        var tracks = new[] { Track("a"), Track("b") };

        var before = PeerCatalog.VersionOf(tracks, [], [],
            PeerCatalogBuilder.MoodLists(Cut(new MoodListPayload("Calm", "Rock", 0, ["a"]))));
        var after = PeerCatalog.VersionOf(tracks, [], [],
            PeerCatalogBuilder.MoodLists(Cut(new MoodListPayload("Calm", "Rock", 0, ["b"]))));

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void An_unchanged_cut_is_the_same_catalog()
    {
        // The other half: a borrower on its second visit is told "still that one" in a few hundred
        // bytes, but a cut that hashed differently every time would send the whole listing on every
        // visit.
        var tracks = new[] { Track("a") };
        var lists = PeerCatalogBuilder.MoodLists(Cut(new MoodListPayload("Calm", null, 0, ["a"])));

        Assert.Equal(
            PeerCatalog.VersionOf(tracks, [], [], lists),
            PeerCatalog.VersionOf(tracks, [], [], lists));
    }

    [Fact]
    public void The_order_of_the_lists_does_not_change_the_version()
    {
        // Hashed in a fixed order, so two builds that enumerate the same cut differently give one
        // catalogue, not two.
        var tracks = new[] { Track("a") };
        var one = new MoodListPayload("Calm", "Rock", 0, ["a"]);
        var two = new MoodListPayload("Danceable", "Pop", 1980, ["a"]);

        Assert.Equal(
            PeerCatalog.VersionOf(tracks, [], [], PeerCatalogBuilder.MoodLists(Cut(one, two))),
            PeerCatalog.VersionOf(tracks, [], [], PeerCatalogBuilder.MoodLists(Cut(two, one))));
    }

    [Fact]
    public void A_shelf_with_no_cut_still_has_a_version()
    {
        // What an older build sends, and what a collection with no measurements sends.
        Assert.False(string.IsNullOrEmpty(PeerCatalog.VersionOf([Track("a")], [], [], [])));
    }

    // ── what is sent ──

    [Fact]
    public void A_list_crosses_whole_and_in_order()
    {
        var lists = PeerCatalogBuilder.MoodLists(
            Cut(new MoodListPayload("Calm", "Rock", 1970, ["a", "b", "c"])));

        var list = Assert.Single(lists);
        Assert.Equal("Calm", list.Kind);
        Assert.Equal("Rock", list.Genre);
        Assert.Equal(1970, list.Decade);
        Assert.Equal(["a", "b", "c"], list.RpmIds);
    }

    [Fact]
    public void Nothing_is_trimmed_to_what_one_connection_could_play()
    {
        // Which records a borrower can reach is not a fact about the list, and the answer changes: on
        // the owner's network every id can be requested from the owner's machine, and off it only the
        // ones in the cloud resolve. Trimming here would fix one of those two answers into a
        // catalogue that outlives the connection it was fetched over.
        var lists = PeerCatalogBuilder.MoodLists(
            Cut(new MoodListPayload("Sleep", null, 0, ["local-only", "in-the-cloud"])));

        Assert.Equal(2, lists.Single().RpmIds.Count);
    }

    [Fact]
    public void A_catalog_carries_the_cut_beside_the_tracks()
    {
        var catalog = PeerCatalogBuilder.Catalog(
            [], "Their laptop", [], [],
            PeerCatalogBuilder.MoodLists(Cut(new MoodListPayload("Calm", null, 0, ["a"]))));

        Assert.Single(catalog.MoodLists);
        Assert.Equal("Calm", catalog.MoodLists[0].Kind);
    }

    [Fact]
    public void An_older_build_sends_no_lists_and_that_is_a_shelf_without_them()
    {
        var catalog = PeerCatalogBuilder.Catalog([], "Their laptop", [], [], []);

        Assert.Empty(catalog.MoodLists);
    }
}
