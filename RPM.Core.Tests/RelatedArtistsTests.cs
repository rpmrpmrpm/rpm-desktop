using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>The related artists row on an artist's page: the Android TV app's rule (RelatedArtistsTest.kt),
/// read off the collection alone.</summary>
public class RelatedArtistsTests
{
    private static readonly IReadOnlyDictionary<string, string> NoTags = new Dictionary<string, string>();

    private static ArtistWallItem Act(
        string name, IReadOnlyList<string> albums, IReadOnlyList<string>? together = null,
        IReadOnlyList<string>? appearances = null, int tracks = 10)
        => new(name, albums, together ?? [], appearances ?? [], tracks);

    private static readonly IReadOnlyList<GenreWallItem> Genres =
    [
        new("Rock", [], ["r1", "r2", "r3", "r4", "r5"]),
        new("Jazz", [], ["j1", "j2", "j3"]),
        new(GenreWall.Unclassified, [], ["u1", "u2"]),
    ];

    private static IReadOnlyList<string> Related(
        ArtistWallItem me, IReadOnlyList<ArtistWallItem> wall,
        IReadOnlyDictionary<string, string>? tags = null, IReadOnlyList<IReadOnlyCollection<string>>? lists = null)
        => [.. RelatedArtists.Of(me, wall, Genres, tags ?? NoTags, lists ?? []).Select(a => a.Name)];

    [Fact]
    public void A_record_made_together_counts_before_a_genre_in_common()
    {
        var me = Act("Me", ["r1"], together: ["j3"]);
        var partner = Act("Partner", ["j2"], together: ["j3"]);
        var rocker = Act("Rocker", ["r2", "r3"]);

        Assert.Equal(["Partner", "Rocker"], Related(me, [me, partner, rocker]));
    }

    [Fact]
    public void An_act_of_other_genres_and_no_record_in_common_is_not_related()
    {
        var me = Act("Me", ["r1", "r2"]);
        var jazz = Act("Jazz act", ["j1"]);

        Assert.Empty(Related(me, [me, jazz]));
    }

    [Fact]
    public void The_act_itself_and_the_compilations_are_never_related()
    {
        var me = Act("Me", ["r1"]);
        var various = Act("Various Artists", ["r2"]);
        var again = Act("me", ["r3"]);

        Assert.Empty(Related(me, [me, various, again]));
    }

    [Fact]
    public void Records_on_no_card_do_not_make_acts_alike()
    {
        var me = Act("Me", ["u1"]);
        var other = Act("Other", ["u2"]);

        Assert.Empty(Related(me, [me, other]));
    }

    [Fact]
    public void A_tie_goes_to_the_act_with_more_songs()
    {
        var me = Act("Me", ["r1"]);
        var small = Act("Small", ["r2"], tracks: 3);
        var big = Act("Big", ["r3"], tracks: 40);

        Assert.Equal(["Big", "Small"], Related(me, [me, small, big]));
    }

    [Fact]
    public void Records_on_no_card_with_the_same_tag_are_alike()
    {
        var tags = new Dictionary<string, string>
        {
            ["u1"] = "Singer-Songwriter",
            ["u2"] = " singer-songwriter ",
            ["r5"] = "Rock",
        };
        var me = Act("Me", ["u1"]);
        var alike = Act("Alike", ["u2"]);
        var rocker = Act("Rocker", ["r5"]);

        Assert.Equal(["Alike"], Related(me, [me, alike, rocker], tags));
    }

    [Fact]
    public void Acts_on_the_same_small_lists_are_related()
    {
        var me = Act("Me", ["u1"]);
        var company = Act("Company", ["u2"]);
        var stranger = Act("Stranger", ["j1"]);

        // "chill" holds only the two of them; "all" holds the stranger and the company but not me.
        var lists = new List<IReadOnlyCollection<string>> { new[] { "u1", "u2" }, new[] { "j1", "u2" } };

        Assert.Equal(["Company"], Related(me, [me, company, stranger], lists: lists));
    }

    [Fact]
    public void At_most_twelve()
    {
        var me = Act("Me", ["r1"]);
        var many = Enumerable.Range(1, 20).Select(i => Act($"Act {i}", ["r2"])).ToList();

        Assert.Equal(RelatedArtists.Most, Related(me, [me, .. many]).Count);
    }
}
