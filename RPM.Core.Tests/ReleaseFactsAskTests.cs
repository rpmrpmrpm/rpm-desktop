using RPM.Core.Analysis;
using RPM.Core.Library;
using RPM.Core.Metadata.Providers;
using RPM.Fingerprint.Net;

namespace RPM.Core.Tests;

/// <summary>
/// Albums linked through the app's own catalogue are asked about too, and what comes back reaches the same
/// facts the Discogs pass fills.
/// </summary>
public class ReleaseFactsAskTests : IDisposable
{
    private const string Folder = "Artist/Album";
    private const string ReleaseId = "66278f7b-bcab-49b5-8c9a-fa76dcf51d00";

    private readonly string _root;
    private readonly LibraryDatabase _db;

    public ReleaseFactsAskTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmfacts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
        _db.SetAlbumPressing(Folder, RpmMetadataProvider.ProviderId, ReleaseId);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private LibraryDatabase.ReleaseToAsk TheAlbum()
        => Assert.Single(_db.NextReleasesToAsk(RpmMetadataProvider.ProviderId, 10));

    private static ReleaseDetail Release(IReadOnlyList<string>? voted, string? genre = null, string? subgenre = null)
        => new(new ReleaseRef(RpmMetadataProvider.ProviderId, ReleaseId), "Album", "Artist", 2001, null, null, [],
            Genre: genre, Subgenre: subgenre, Genres: voted);

    [Fact]
    public async Task The_voted_words_become_the_albums_facts()
    {
        var reached = await ReleaseFactsAsk.AskAsync(
            _db, new Catalogue(Asked.Answer<ReleaseDetail?>(Release(["electronic", "trip hop"]))), TheAlbum());

        Assert.True(reached);
        var facts = _db.CatalogueFactsByFolder()[Folder];
        Assert.Equal("Electronic / Dance", facts.Genre);
        Assert.Contains("Trip Hop", facts.Styles);
    }

    [Fact]
    public async Task An_older_servers_single_word_is_kept()
    {
        await ReleaseFactsAsk.AskAsync(
            _db, new Catalogue(Asked.Answer<ReleaseDetail?>(Release(null, "House", "Deep House"))), TheAlbum());

        Assert.Contains("Deep House", _db.CatalogueFactsByFolder()[Folder].Styles);
    }

    [Fact]
    public async Task A_catalogue_that_was_not_reached_leaves_the_album_to_be_asked_again()
    {
        var reached = await ReleaseFactsAsk.AskAsync(
            _db, new Catalogue(Asked.Failed<ReleaseDetail?>("offline")), TheAlbum());

        Assert.False(reached);
        Assert.Single(_db.NextReleasesToAsk(RpmMetadataProvider.ProviderId, 10));
    }

    [Fact]
    public async Task A_catalogue_that_answered_with_nothing_is_not_asked_again()
    {
        var reached = await ReleaseFactsAsk.AskAsync(
            _db, new Catalogue(Asked.Answer<ReleaseDetail?>(null)), TheAlbum());

        Assert.True(reached);
        Assert.Empty(_db.NextReleasesToAsk(RpmMetadataProvider.ProviderId, 10));
    }

    private sealed class Catalogue(Asked<ReleaseDetail?> answer) : IMetadataProvider
    {
        public string Id => RpmMetadataProvider.ProviderId;
        public string DisplayName => "RPM";
        public bool IsAvailable => true;
        public bool SupportsCatalogNumberSearch => false;

        public Task<Asked<IReadOnlyList<ReleaseSearchResult>>> SearchAsync(ReleaseQuery query, CancellationToken ct = default)
            => Task.FromResult(Asked.Answer<IReadOnlyList<ReleaseSearchResult>>([]));

        public Task<Asked<ReleaseDetail?>> GetReleaseAsync(string releaseId, CancellationToken ct = default)
            => Task.FromResult(answer);
    }
}
