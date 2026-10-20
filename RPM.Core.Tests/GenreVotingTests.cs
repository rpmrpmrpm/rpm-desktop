using System.Text.Json;
using RPM.Core.Metadata;
using RPM.Core.Metadata.Providers;
using RPM.Fingerprint.Catalog;
using RPM.Fingerprint.External;

namespace RPM.Core.Tests;

/// <summary>
/// A catalogue's voted genre list, from the MusicBrainz answer to the shelf and the style the import and the
/// album editor are filled with.
/// </summary>
public class GenreVotingTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Votes_on_the_release_and_its_group_are_summed_and_ranked()
    {
        var release = Json("""{"genres":[{"name":"trip hop","count":2},{"name":"electronic","count":1}]}""");
        var group = Json("""{"genres":[{"name":"electronic","count":5},{"name":"downtempo","count":2}]}""");

        Assert.Equal(["Electronic", "Trip Hop", "Downtempo"], MusicBrainzRelations.VotedGenres(release, group));
    }

    [Fact]
    public void A_release_without_genres_gives_an_empty_list()
    {
        Assert.Empty(MusicBrainzRelations.VotedGenres(Json("""{"id":"r"}"""), default));
        Assert.Empty(MusicBrainzRelations.VotedGenres(Json("""{"genres":[]}""")));
    }

    [Theory]
    [InlineData(new[] { "Electronic", "Trip Hop", "Downtempo" }, "Electronic / Dance", "Trip Hop")]
    [InlineData(new[] { "Rock", "Alternative Rock" }, "Rock", "Alternative Rock")]
    [InlineData(new[] { "Trip Hop", "Electronic" }, "Electronic / Dance", "Trip Hop")]
    [InlineData(new[] { "Rock", "Indie Rock" }, "Indie", "Indie Rock")]
    [InlineData(new[] { "Electronic", "Deep House" }, "House", "Deep House")]
    [InlineData(new[] { "Rock", "Synth-Pop" }, "Rock", null)]
    [InlineData(new[] { "Rock", "Heavy Metal", "Hard Rock" }, "Rock", "Hard Rock")]
    [InlineData(new[] { "Hip Hop" }, "Hip-Hop / Rap", null)]
    [InlineData(new[] { "Albums I Own", "Rock" }, "Rock", null)]
    public void A_voted_list_is_filed_on_our_shelves(string[] voted, string genre, string? subgenre)
    {
        var picked = Genres.PickVoted(voted);

        Assert.Equal(genre, picked.Genre);
        Assert.Equal(subgenre, picked.Subgenre);
    }

    [Fact]
    public void The_style_always_hangs_off_the_shelf_it_is_paired_with()
    {
        string[][] lists =
        [
            ["Rock", "Indie Rock"], ["Pop", "Hard Rock"], ["Electronic", "Deep House", "Techno"],
            ["Jazz", "Soul"], ["Folk", "Alternative Rock", "Singer-Songwriter"],
        ];

        foreach (var voted in lists)
        {
            var picked = Genres.PickVoted(voted);
            if (picked.HasSubgenre)
                Assert.Contains(Subgenres.Under(picked.Genre), s => s.Name == picked.Subgenre);
        }
    }

    [Fact]
    public void A_list_with_nothing_of_ours_picks_nothing()
        => Assert.False(Genres.PickVoted(["Albums I Own", "Seen Live"]).IsKnown);

    [Fact]
    public void A_style_is_taken_under_a_shelf_that_is_already_set()
    {
        Assert.Equal("Soft Rock", Genres.StyleUnder("Rock", ["Pop", "Soft Rock"]));
        Assert.Null(Genres.StyleUnder("Rock", ["Pop", "Synth-Pop"]));
        Assert.Null(Genres.StyleUnder("Rock", null));
    }

    [Fact]
    public void A_listing_with_a_voted_list_fills_genre_and_style_from_our_lists()
    {
        var listing = new CatalogReleaseDetail("r", "Mezzanine", "Massive Attack", 1998, [], [],
            Genre: "Electronic", Genres: ["Electronic", "Trip Hop", "Downtempo"]);

        var detail = RpmMetadataProvider.Detail(listing);

        Assert.Equal("Electronic / Dance", detail.Genre);
        Assert.Equal("Trip Hop", detail.Subgenre);
        Assert.Equal(["Electronic", "Trip Hop", "Downtempo"], detail.Genres);
    }

    [Fact]
    public void A_voted_list_with_nothing_of_ours_leaves_the_genre_blank()
    {
        var listing = new CatalogReleaseDetail("r", "Record", "Act", 1999, [], [],
            Genre: "Albums I Own", Genres: ["Albums I Own"]);

        var detail = RpmMetadataProvider.Detail(listing);

        Assert.Null(detail.Genre);
        Assert.Null(detail.Subgenre);
    }

    [Fact]
    public void An_older_server_with_one_genre_is_read_as_before()
    {
        var detail = RpmMetadataProvider.Detail(
            new CatalogReleaseDetail("r", "Record", "Act", 1999, [], [], Genre: "Deep House"));

        Assert.Equal("House", detail.Genre);
        Assert.Equal("Deep House", detail.Subgenre);
        Assert.Null(detail.Genres);

        // Its one word is still shown when it is not ours, because it is the only answer there is.
        Assert.Equal("Albums I Own", RpmMetadataProvider.Detail(
            new CatalogReleaseDetail("r", "Record", "Act", 1999, [], [], Genre: "Albums I Own")).Genre);
    }
}
