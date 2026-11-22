using RPM.Core.Analysis;
using RPM.Core.Library;
using RPM.Core.Metadata.Providers;

namespace RPM.Core.Tests;

/// <summary>
/// What the catalogues said about each album travels from a computer's library to the store's, so a list the store
/// cuts places each record in the same genre and narrows it by the same styles as the computer does. What is sent,
/// what is left out, and why taking a copy in never replaces what a library already holds.
/// </summary>
public class AlbumCatalogueTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _desk;
    private readonly LibraryDatabase _store;

    public AlbumCatalogueTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmcatalogue-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "desk"));
        Directory.CreateDirectory(Path.Combine(_root, "store"));
        _desk = new LibraryDatabase(MirrorLocation.InFolder(Path.Combine(_root, "desk")));
        _store = new LibraryDatabase(MirrorLocation.InFolder(Path.Combine(_root, "store")));
    }

    public void Dispose()
    {
        _desk.Dispose();
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private void Add(string relativePath, string? rpmId)
    {
        var slash = relativePath.LastIndexOf('/');
        _desk.Upsert(new LibraryTrack
        {
            RelativePath = relativePath,
            Folder = relativePath[..slash],
            FileName = relativePath[(slash + 1)..],
            Extension = ".flac",
            SizeBytes = 5_000_000,
            ModifiedUtcTicks = 1,
            DurationSeconds = 200,
            Title = relativePath,
            RpmId = rpmId,
        });
    }

    [Fact]
    public void An_album_reads_the_same_facts_in_the_store_as_on_the_computer()
    {
        Add("Muse/Absolution/01.flac", "rpm-1");
        _desk.SetAlbumPressing("Muse/Absolution", DiscogsJson.ProviderId, "555");
        _desk.SetReleaseMeta(DiscogsJson.ProviderId, "555", ["Rock"], ["Alternative Rock", "Art Rock"]);

        Add("Air/Moon Safari/01.flac", "rpm-2");
        _desk.SetAlbumPressing("Air/Moon Safari", RpmMetadataProvider.ProviderId, "mbid-air");
        _desk.SetReleaseMeta(RpmMetadataProvider.ProviderId, "mbid-air", ["electronic", "trip hop"], ["electronic", "trip hop"]);

        // Discogs filed it under nothing, so the release group's answer is the one that counts.
        Add("Coltrane/Blue Train/01.flac", "rpm-3");
        _desk.SetAlbumPressing("Coltrane/Blue Train", DiscogsJson.ProviderId, "777");
        _desk.SetAlbumReleaseGroup("Coltrane/Blue Train", "group-1");
        _desk.SetReleaseMeta(DiscogsJson.ProviderId, "777", [], []);
        _desk.SetReleaseMeta(LibraryDatabase.ReleaseGroupProvider, "group-1", ["Jazz"], ["Hard Bop"]);

        _store.AcceptAlbumCatalogue(_desk.AlbumCatalogueToSend());

        var there = _store.CatalogueFactsByFolder();
        var here = _desk.CatalogueFactsByFolder();
        Assert.Equal(3, here.Count);
        Assert.Equal(here.Keys.Order(), there.Keys.Order());
        foreach (var (folder, facts) in here)
        {
            Assert.Equal(facts.Genre, there[folder].Genre);
            Assert.Equal(facts.Styles, there[folder].Styles);
        }
    }

    [Fact]
    public void The_catalogues_own_words_and_votes_travel_and_are_mapped_where_they_land()
    {
        Add("A/B/01.flac", "rpm-1");
        _desk.SetAlbumPressing("A/B", RpmMetadataProvider.ProviderId, "mbid-1");
        _desk.SetReleaseMeta(RpmMetadataProvider.ProviderId, "mbid-1", ["rock", "soft rock"], ["rock", "soft rock"], [7, 3]);

        _store.AcceptAlbumCatalogue(_desk.AlbumCatalogueToSend());

        var row = _store.ReadReleaseMeta(RpmMetadataProvider.ProviderId, "mbid-1");
        Assert.NotNull(row);
        Assert.Equal("rock; soft rock", row.RawGenres);
        Assert.Equal("7; 3", row.Votes);
        Assert.Equal(_desk.ReadReleaseMeta(RpmMetadataProvider.ProviderId, "mbid-1")!.MappedStyles, row.MappedStyles);
    }

    /// <summary>The store cuts lists from the music in the cloud. A record that is only on the computer has no
    /// place in them, and sending it would only make the copy bigger.</summary>
    [Fact]
    public void An_album_with_nothing_in_the_cloud_is_not_sent()
    {
        Add("Local/Only/01.flac", rpmId: null);
        Add("Local/Empty/01.flac", rpmId: "");
        _desk.SetAlbumPressing("Local/Only", DiscogsJson.ProviderId, "1");
        _desk.SetAlbumPressing("Local/Empty", DiscogsJson.ProviderId, "1");
        _desk.SetReleaseMeta(DiscogsJson.ProviderId, "1", ["Rock"], ["Soft Rock"]);

        var sent = _desk.AlbumCatalogueToSend();

        Assert.Empty(sent.Albums);
        Assert.Empty(sent.Releases);
    }

    /// <summary>A link with no answer behind it gives the album no facts here, and would give it none there.</summary>
    [Fact]
    public void An_album_no_catalogue_has_answered_about_is_not_sent()
    {
        Add("A/B/01.flac", "rpm-1");
        _desk.SetAlbumPressing("A/B", DiscogsJson.ProviderId, "1");

        var sent = _desk.AlbumCatalogueToSend();

        Assert.Empty(sent.Albums);
        Assert.Empty(sent.Releases);
    }

    [Fact]
    public void Two_albums_of_one_release_send_the_release_once()
    {
        Add("flac/Muse - Absolution/01.flac", "rpm-1");
        Add("mp3/Muse - Absolution/01.mp3", "rpm-2");
        _desk.SetAlbumPressing("flac/Muse - Absolution", DiscogsJson.ProviderId, "555");
        _desk.SetAlbumPressing("mp3/Muse - Absolution", DiscogsJson.ProviderId, "555");
        _desk.SetReleaseMeta(DiscogsJson.ProviderId, "555", ["Rock"], ["Art Rock"]);

        var sent = _desk.AlbumCatalogueToSend();

        Assert.Equal(2, sent.Albums.Count);
        Assert.Single(sent.Releases);
    }

    /// <summary>A copy fills what a library is missing and nothing else. Two computers that linked one record to
    /// two pressings each keep their own answer where it is, and the first copy taken in decides for the rest.</summary>
    [Fact]
    public void Taking_a_copy_in_never_replaces_a_link_or_an_answer_already_held()
    {
        _store.SetAlbumPressing("A/B", DiscogsJson.ProviderId, "1");
        _store.SetReleaseMeta(DiscogsJson.ProviderId, "1", ["Jazz"], ["Vocal Jazz"]);

        var linked = _store.AcceptAlbumCatalogue(new AlbumCatalogue(
            [new CatalogueAlbum("A/B", DiscogsJson.ProviderId, "2", null), new CatalogueAlbum("C/D", DiscogsJson.ProviderId, "1", null)],
            [
                new CatalogueRelease(DiscogsJson.ProviderId, "1", ["Rock"], ["Soft Rock"], null),
                new CatalogueRelease(DiscogsJson.ProviderId, "2", ["House"], ["Deep House"], null),
            ]));

        Assert.Equal(1, linked);
        var facts = _store.CatalogueFactsByFolder();
        Assert.Equal("Jazz", facts["A/B"].Genre);
        Assert.Equal(["Vocal Jazz"], facts["C/D"].Styles);
    }

    [Fact]
    public void An_entry_with_no_folder_or_no_release_is_left_out()
    {
        var linked = _store.AcceptAlbumCatalogue(new AlbumCatalogue(
            [
                new CatalogueAlbum("", DiscogsJson.ProviderId, "1", null),
                new CatalogueAlbum("A/B", DiscogsJson.ProviderId, "", null),
                new CatalogueAlbum("C/D", null, null, null),
            ],
            [new CatalogueRelease("", "1", ["Rock"], [], null), new CatalogueRelease(DiscogsJson.ProviderId, " ", ["Rock"], [], null)]));

        Assert.Equal(0, linked);
        Assert.Empty(_store.CatalogueFactsByFolder());
    }

    [Fact]
    public void The_fingerprint_follows_the_contents_and_not_their_order()
    {
        var a = new CatalogueAlbum("A/B", DiscogsJson.ProviderId, "1", null);
        var b = new CatalogueAlbum("C/D", DiscogsJson.ProviderId, "2", null);
        var one = new CatalogueRelease(DiscogsJson.ProviderId, "1", ["Rock"], ["Soft Rock"], null);
        var two = new CatalogueRelease(DiscogsJson.ProviderId, "2", ["Jazz"], [], null);

        var first = new AlbumCatalogue([a, b], [one, two]).Fingerprint;

        Assert.Equal(first, new AlbumCatalogue([b, a], [two, one]).Fingerprint);
        Assert.NotEqual(first, new AlbumCatalogue([a, b], [one, two with { Styles = ["Vocal Jazz"] }]).Fingerprint);
        Assert.NotEqual(first, new AlbumCatalogue([a, b with { ReleaseId = "1" }], [one, two]).Fingerprint);
        Assert.NotEqual(first, AlbumCatalogue.None.Fingerprint);
    }

    /// <summary>The store fills albums linked through the app's own catalogue from its own copy of that catalogue's
    /// answer. Written the way the computer's pass writes it, the album reads the same facts.</summary>
    [Fact]
    public async Task The_apps_own_catalogue_answer_reads_the_same_whichever_way_it_arrives()
    {
        _desk.SetAlbumPressing("A/B", RpmMetadataProvider.ProviderId, "mbid-1");
        var detail = new ReleaseDetail(new ReleaseRef(RpmMetadataProvider.ProviderId, "mbid-1"), "B", "A", 2001, null, null, [],
            Genre: "Electronic", Subgenre: "Trip Hop", Genres: ["electronic", "trip hop", "downtempo"]);
        await ReleaseFactsAsk.AskAsync(_desk, new Catalogue(detail), Assert.Single(_desk.NextReleasesToAsk(RpmMetadataProvider.ProviderId, 10)));

        _store.AcceptAlbumCatalogue(new AlbumCatalogue(
            [new CatalogueAlbum("A/B", RpmMetadataProvider.ProviderId, "mbid-1", null)],
            [ReleaseFactsAsk.Said(RpmMetadataProvider.ProviderId, "mbid-1", detail.Genres, detail.Genre, detail.Subgenre)]));

        Assert.Equal(_desk.CatalogueFactsByFolder()["A/B"].Styles, _store.CatalogueFactsByFolder()["A/B"].Styles);
        Assert.Equal(_desk.CatalogueFactsByFolder()["A/B"].Genre, _store.CatalogueFactsByFolder()["A/B"].Genre);
    }

    private sealed class Catalogue(ReleaseDetail release) : ReleaseFactsAskTests.Catalogue(RPM.Fingerprint.Net.Asked.Answer<ReleaseDetail?>(release));
}
