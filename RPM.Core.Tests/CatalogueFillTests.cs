using RPM.Core.Analysis;
using RPM.Core.Metadata.Providers;
using RPM.Core.Models;

namespace RPM.Core.Tests;

/// <summary>
/// What the import fills in from the catalogue on its own: which release it trusts, and which of a queued file's
/// tags it is allowed to touch.
/// </summary>
public class CatalogueFillTests
{
    private static TrackInfo File(string? genre = null, string? subgenre = null, uint year = 0,
        string album = "Mezzanine", string artist = "Massive Attack", string? releaseId = null)
        => new()
        {
            FilePath = "/q/a.flac",
            Extension = ".flac",
            Album = album,
            AlbumArtist = artist,
            Artist = artist,
            Genre = genre,
            Subgenre = subgenre,
            Year = year,
            MusicBrainzReleaseId = releaseId,
        };

    private static ReleaseDetail Release(
        string? genre = "Electronic / Dance", string? subgenre = "Trip Hop",
        uint year = 2018, uint original = 1998, int tracks = 11,
        IReadOnlyList<string>? voted = null, string title = "Mezzanine", string artist = "Massive Attack")
        => new(new ReleaseRef(RpmMetadataProvider.ProviderId, "r1"), title, artist, year, "GB", "CD",
            [.. Enumerable.Range(1, tracks).Select(i => new ReleaseTrackInfo((uint)i, $"T{i}", artist, 200))],
            OriginalYear: original, Genre: genre, Subgenre: subgenre,
            Genres: voted ?? ["Electronic", "Trip Hop", "Downtempo"]);

    // ── the fill: blanks only ──

    [Fact]
    public void Blanks_are_filled_with_the_shelf_the_style_and_the_first_release_year()
    {
        var filled = CatalogueFill.Fill(File(), Release());

        Assert.Equal("Electronic / Dance", filled.Genre);
        Assert.Equal("Trip Hop", filled.Subgenre);
        Assert.Equal(1998u, filled.Year);
        Assert.Equal(1998u, filled.OriginalYear);
        Assert.True(filled.TagsEdited);
    }

    [Fact]
    public void A_pressing_year_is_used_only_where_the_first_is_unknown()
        => Assert.Equal(2018u, CatalogueFill.Fill(File(), Release(original: 0)).Year);

    [Fact]
    public void Values_already_on_our_lists_are_never_replaced()
    {
        var file = File(genre: "Rock", subgenre: "Hard Rock", year: 1990);

        Assert.Same(file, CatalogueFill.Fill(file, Release() with { OriginalYear = 0 } with { Year = 0 }));
        var filled = CatalogueFill.Fill(file, Release());
        Assert.Equal("Rock", filled.Genre);
        Assert.Equal("Hard Rock", filled.Subgenre);
        Assert.Equal(1990u, filled.Year);
    }

    [Fact]
    public void A_genre_our_lists_cannot_read_is_replaced()
    {
        var filled = CatalogueFill.Fill(File(genre: "Albums I Own"), Release());

        Assert.Equal("Electronic / Dance", filled.Genre);
        Assert.Equal("Trip Hop", filled.Subgenre);
    }

    [Fact]
    public void A_genre_our_lists_can_read_is_translated_not_replaced()
    {
        var filled = CatalogueFill.Fill(File(genre: "deep house"), Release());

        Assert.Equal("House", filled.Genre);
        Assert.Equal("Deep House", filled.Subgenre);
    }

    [Fact]
    public void An_unreadable_genre_is_kept_when_the_release_has_no_shelf_either()
    {
        var filled = CatalogueFill.Fill(File(genre: "Albums I Own"), Release(genre: null, subgenre: null, voted: []));

        Assert.Equal("Albums I Own", filled.Genre);
        Assert.Equal(1998u, filled.Year);
    }

    [Fact]
    public void A_style_is_added_only_under_the_files_own_shelf()
    {
        // The file says Rock. The release says Electronic / Dance and Trip Hop, which is not a corner of Rock,
        // and none of its words is a rock style either.
        var rock = CatalogueFill.Fill(File(genre: "Rock"), Release());
        Assert.Equal("Rock", rock.Genre);
        Assert.True(string.IsNullOrEmpty(rock.Subgenre));

        // One of its words is.
        var soft = CatalogueFill.Fill(File(genre: "Rock"), Release(voted: ["Pop", "Soft Rock"]));
        Assert.Equal("Soft Rock", soft.Subgenre);
    }

    [Fact]
    public void A_style_already_set_is_kept()
        => Assert.Equal("Downtempo",
            CatalogueFill.Fill(File(genre: "Electronic / Dance", subgenre: "Downtempo"), Release()).Subgenre);

    // ── which records are asked about ──

    [Fact]
    public void A_record_whose_files_say_everything_is_not_asked_about()
    {
        Assert.Null(CatalogueFill.AlbumOf([File("Rock", "Hard Rock", 1990), File("Rock", "Hard Rock", 1990)]));
        Assert.NotNull(CatalogueFill.AlbumOf([File("Rock", "Hard Rock", 1990), File("Rock", "Hard Rock", 0)]));
        Assert.NotNull(CatalogueFill.AlbumOf([File("hip hop", "Trap", 1990)]));

        // A shelf with no styles has nothing left to fill once it has a year.
        Assert.Null(CatalogueFill.AlbumOf([File(RPM.Core.Metadata.Genres.Unknown, null, 1990)]));
    }

    [Fact]
    public void A_record_names_its_release_only_when_its_files_agree()
    {
        Assert.Equal("mb1", CatalogueFill.AlbumOf([File(releaseId: "mb1"), File()])!.ReleaseId);
        Assert.Null(CatalogueFill.AlbumOf([File(releaseId: "mb1"), File(releaseId: "mb2")])!.ReleaseId);
    }

    // ── the match ──

    private static CatalogueFill.Album Album(int tracks = 11, string title = "Mezzanine",
        string artist = "Massive Attack", string? releaseId = null)
        => new(artist, title, tracks, releaseId);

    private static ReleaseSearchResult Found(string title = "Mezzanine", string? artist = "Massive Attack",
        int tracks = 11, string id = "r1", double relevance = 1)
        => new(new ReleaseRef(RpmMetadataProvider.ProviderId, id), title, artist, 1998, null, "GB", "CD", tracks,
            Relevance: relevance);

    [Theory]
    [InlineData("Mezzanine", "Massive Attack", 11, true)]
    [InlineData("Mezzanine (Deluxe Edition)", "Massive Attack", 12, true)]
    [InlineData("mezzanine", "massive attack", 13, true)]
    [InlineData("Mezzanine", "The Massive Attack", 11, true)]
    [InlineData("Mezzanine", "Massive Attack", 4, false)]
    [InlineData("Protection", "Massive Attack", 11, false)]
    [InlineData("Mezzanine", "Mad Professor", 11, false)]
    public void Only_a_release_whose_title_artist_and_length_agree_is_trusted(
        string title, string artist, int tracks, bool trusted)
        => Assert.Equal(trusted, CatalogueFill.Confident(Album(), Found(title, artist, tracks)));

    [Fact]
    public void Simon_and_Garfunkel_is_Simon_amp_Garfunkel()
        => Assert.True(CatalogueFill.Confident(
            Album(artist: "Simon & Garfunkel", title: "Bookends"),
            Found("Bookends", "Simon and Garfunkel")));

    private sealed class Catalogue(IReadOnlyList<ReleaseSearchResult>? results, ReleaseDetail? listing)
        : IMetadataProvider
    {
        public List<string> Asked { get; } = [];
        public string Id => RpmMetadataProvider.ProviderId;
        public string DisplayName => "RPM";
        public bool IsAvailable => true;
        public bool SupportsCatalogNumberSearch => false;

        public Task<Asked<IReadOnlyList<ReleaseSearchResult>>> SearchAsync(ReleaseQuery query, CancellationToken ct = default)
        {
            Asked.Add($"search {query.Artist} / {query.Album}");
            return Task.FromResult(results is null
                ? RPM.Fingerprint.Net.Asked.Failed<IReadOnlyList<ReleaseSearchResult>>("offline")
                : RPM.Fingerprint.Net.Asked.Answer(results));
        }

        public Task<Asked<ReleaseDetail?>> GetReleaseAsync(string releaseId, CancellationToken ct = default)
        {
            Asked.Add($"release {releaseId}");
            return Task.FromResult(RPM.Fingerprint.Net.Asked.Answer(listing));
        }
    }

    [Fact]
    public async Task The_closest_sure_release_is_fetched()
    {
        var catalogue = new Catalogue(
            [Found("Mezzanine", tracks: 4, id: "single"), Found(id: "lp", relevance: 0.5),
             Found("Mezzanine (Deluxe)", tracks: 22, id: "box")],
            Release());

        Assert.True((await CatalogueFill.FindAsync(catalogue, Album())).Answers(out var found, out _));
        Assert.NotNull(found);
        Assert.Equal(["search Massive Attack / Mezzanine", "release lp"], catalogue.Asked);
    }

    [Fact]
    public async Task No_sure_release_is_an_answer_of_nothing()
    {
        var catalogue = new Catalogue([Found("Protection")], Release());

        Assert.True((await CatalogueFill.FindAsync(catalogue, Album())).Answers(out var found, out _));
        Assert.Null(found);
        Assert.DoesNotContain(catalogue.Asked, a => a.StartsWith("release", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_listing_whose_length_disagrees_is_not_used()
    {
        var catalogue = new Catalogue([Found(tracks: 0)], Release(tracks: 30));

        Assert.True((await CatalogueFill.FindAsync(catalogue, Album())).Answers(out var found, out _));
        Assert.Null(found);
    }

    [Fact]
    public async Task A_files_own_release_id_is_fetched_without_a_search()
    {
        var catalogue = new Catalogue([], Release(title: "Something Else"));

        Assert.True((await CatalogueFill.FindAsync(catalogue, Album(releaseId: "mb1"))).Answers(out var found, out _));
        Assert.NotNull(found);
        Assert.Equal(["release mb1"], catalogue.Asked);
    }

    [Fact]
    public async Task A_catalogue_that_cannot_be_asked_is_not_an_answer()
        => Assert.False((await CatalogueFill.FindAsync(new Catalogue(null, null), Album())).Answers(out _, out _));
}
