using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>The shelves an artist's page is cut into, as Pegasus reads them. The same rule as the desktop
/// artist page and the phone's ArtistShelves.</summary>
public class ArtistShelvesTests
{
    private static AlbumFacts Year(int year) => new("", "", 0, year, 0);

    /// <summary>No record carries a type: every one is shelved by its name.</summary>
    private static readonly IReadOnlyDictionary<string, ReleaseType> NoTypes = new Dictionary<string, ReleaseType>();

    [Fact]
    public void Each_record_lands_on_one_shelf_by_its_name()
    {
        var act = new ArtistWallItem(
            "Queen",
            Albums:
            [
                "Queen/A Night at the Opera",
                "Queen/Live at Wembley",
                "Queen/Greatest Hits",
                "Queen/Flash Gordon (Original Soundtrack)",
                "Queen/Bohemian Rhapsody - Single",
                "Queen/Live Killers - Single",
            ],
            Together: ["Queen & David Bowie/Hot Space Duets"],
            Appearances: ["Various/Live Aid"],
            Tracks: 60);

        var shelves = ArtistShelves.Of(act, new Dictionary<string, AlbumFacts>(), NoTypes);

        Assert.Equal(["Queen/A Night at the Opera"], shelves.Records);
        Assert.Equal(["Queen & David Bowie/Hot Space Duets"], shelves.Together);
        Assert.Equal(["Queen/Live at Wembley"], shelves.Live);
        Assert.Equal(["Queen/Flash Gordon (Original Soundtrack)"], shelves.Soundtracks);
        Assert.Equal(["Queen/Greatest Hits"], shelves.Compilations);

        // A single first: "Live Killers - Single" is a single, not a live record.
        Assert.Equal(2, shelves.Singles.Count);
        Assert.Contains("Queen/Live Killers - Single", shelves.Singles);

        // Somebody else's record stays an appearance whatever its name says.
        Assert.Equal(["Various/Live Aid"], shelves.Appearances);
        Assert.False(shelves.IsEmpty);
    }

    [Fact]
    public void A_shelf_is_newest_first_with_the_undated_last()
    {
        var act = new ArtistWallItem("A", ["A/Old", "A/Undated", "A/New", "A/Also New"], [], [], 4);
        var facts = new Dictionary<string, AlbumFacts>
        {
            ["A/Old"] = Year(1990),
            ["A/New"] = Year(2020),
            ["A/Also New"] = Year(2020),
        };

        var shelves = ArtistShelves.Of(act, facts, NoTypes);

        // Two from one year fall back to the title.
        Assert.Equal(["A/Also New", "A/New", "A/Old", "A/Undated"], shelves.Records);
    }

    [Fact]
    public void Own_in_page_order_leaves_out_appearances()
    {
        var act = new ArtistWallItem("A", ["A/Album", "A/Live"], [], ["B/Guest"], 3);

        var shelves = ArtistShelves.Of(act, new Dictionary<string, AlbumFacts>(), NoTypes);

        Assert.Equal(["A/Album", "A/Live"], shelves.OwnInPageOrder);
    }

    [Fact]
    public void A_type_set_on_a_record_wins_over_its_name()
    {
        var act = new ArtistWallItem(
            "Hole", ["Hole/Live Through This", "Hole/Celebrity Skin", "Hole/My Body, the Hand Grenade"], [], [], 30);
        var types = new Dictionary<string, ReleaseType>
        {
            ["Hole/Live Through This"] = ReleaseType.Album,
            ["Hole/My Body, the Hand Grenade"] = ReleaseType.Compilation,
        };

        var shelves = ArtistShelves.Of(act, new Dictionary<string, AlbumFacts>(), types);

        // Named like a live record, set as an album.
        Assert.Contains("Hole/Live Through This", shelves.Records);
        Assert.Empty(shelves.Live);
        // Named like an album, set as a compilation.
        Assert.Equal(["Hole/My Body, the Hand Grenade"], shelves.Compilations);
    }

    [Fact]
    public void EPs_and_mixtapes_have_shelves_of_their_own_and_come_only_from_a_set_type()
    {
        var act = new ArtistWallItem("Drake", ["Drake/So Far Gone", "Drake/Scary Hours", "Drake/Views"], [], [], 40);
        var types = new Dictionary<string, ReleaseType>
        {
            ["Drake/So Far Gone"] = ReleaseType.Mixtape,
            ["Drake/Scary Hours"] = ReleaseType.Ep,
        };

        var shelves = ArtistShelves.Of(act, new Dictionary<string, AlbumFacts>(), types);

        Assert.Equal(["Drake/So Far Gone"], shelves.Mixtapes);
        Assert.Equal(["Drake/Scary Hours"], shelves.Eps);
        Assert.Equal(["Drake/Views"], shelves.Records);
        Assert.Equal(["Drake/Views", "Drake/Scary Hours", "Drake/So Far Gone"], shelves.OwnInPageOrder);
    }
}
