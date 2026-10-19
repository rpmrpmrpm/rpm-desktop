using RPM.Core.Metadata.Providers;
using RPM.Core.Net;
using RPM.Fingerprint.Catalog;

namespace RPM.Core.Tests;

/// <summary>
/// The RPM metadata provider's own decisions: how it folds a release's media into one row, what
/// counts as a record, and its widen-when-empty behaviour for a media-filtered search. The upstream
/// source is faked (<see cref="FakeApi"/>) so the grouping is exercised directly, with no network.
/// </summary>
public class RpmMetadataProviderTests
{
    private static CatalogRelease Candidate(
        string id, string? format, uint mediumPosition = 1, int trackCount = 5) =>
        new(id, "Rumours", "Fleetwood Mac", 1977, "1977", "GB", format, mediumPosition, trackCount,
            "Warner Bros. Records", "K 56344", null, 100);

    private static async Task<IReadOnlyList<ReleaseSearchResult>> Search(
        IReadOnlyList<CatalogRelease> results, ReleaseMedia media = ReleaseMedia.Any,
        IReadOnlyList<CatalogRelease>? whenWidened = null, FakeApi? probe = null)
    {
        var api = probe ?? new FakeApi(results, whenWidened);
        var provider = new RpmMetadataProvider(api);
        return Answer(await provider.SearchAsync(new ReleaseQuery("Rumours", "Fleetwood Mac", media)));
    }

    private static T Answer<T>(Asked<T> asked)
        => asked.Match(value => value, why => throw new Xunit.Sdk.XunitException("unanswered: " + why));

    [Fact]
    public async Task The_media_of_one_release_fold_into_one_row()
    {
        var folded = await Search([
            Candidate("r1", "12\" Vinyl", mediumPosition: 1, trackCount: 6),
            Candidate("r1", "12\" Vinyl", mediumPosition: 2, trackCount: 5),
        ]);

        var row = Assert.Single(folded);
        Assert.Equal(11, row.TrackCount);          // both records, as recorded
        Assert.Equal("12\" Vinyl", row.Format);
    }

    [Fact]
    public async Task A_set_that_mixes_media_says_so_rather_than_picking_one()
    {
        var folded = await Search([
            Candidate("r1", "12\" Vinyl", 1, 6),
            Candidate("r1", "CD", 2, 6),
        ]);

        var row = Assert.Single(folded);
        Assert.Equal("12\" Vinyl + CD", row.Format);
        Assert.True(row.IsVinyl);   // there is a record in the box
    }

    [Fact]
    public async Task Separate_releases_stay_separate()
    {
        var folded = await Search([Candidate("r1", "12\" Vinyl"), Candidate("r2", "CD")]);
        Assert.Equal(2, folded.Count);
    }

    [Fact]
    public async Task A_record_is_recognised_at_any_of_its_sizes()
    {
        Assert.True((await Row("12\" Vinyl")).IsVinyl);
        Assert.True((await Row("7\" Vinyl")).IsVinyl);
        Assert.True((await Row("Vinyl")).IsVinyl);
        Assert.False((await Row("CD")).IsVinyl);
        Assert.False((await Row(null)).IsVinyl);
    }

    [Fact]
    public async Task A_vinyl_only_search_that_finds_no_records_widens_rather_than_dead_ends()
    {
        // Empty for the vinyl-filtered pass, a CD listing when widened.
        var probe = new FakeApi([], whenWidened: [Candidate("r1", "CD")]);
        var folded = await Search([], ReleaseMedia.Vinyl, probe: probe);

        Assert.Single(folded);
        Assert.Equal(2, probe.Calls);   // asked narrow, then widened
    }

    [Fact]
    public async Task Any_narrowed_medium_widens_when_it_finds_nothing()
    {
        // Not a vinyl privilege: a cassette search that comes back empty is no more of an answer than
        // a vinyl one, and the listing of another pressing still names the tracks.
        var probe = new FakeApi([], whenWidened: [Candidate("r1", "CD")]);
        var folded = await Search([], ReleaseMedia.Cassette, probe: probe);

        Assert.Single(folded);
        Assert.Equal(2, probe.Calls);
    }

    [Fact]
    public async Task An_unnarrowed_search_that_finds_nothing_is_asked_once()
    {
        var probe = new FakeApi([], whenWidened: [Candidate("r1", "CD")]);
        var folded = await Search([], ReleaseMedia.Any, probe: probe);

        // The widened pass IS the pass that ran, so nothing was found and nothing is retried.
        Assert.Single(folded);
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task An_artist_with_no_album_is_a_search_rather_than_a_refusal()
    {
        // What clearing the album box asks for: everything this artist put out. The provider must not
        // require an album. Otherwise it would answer with nothing and look as if the cleared album
        // still drove the search.
        var probe = new FakeApi([Candidate("r1", "CD")]);
        var provider = new RpmMetadataProvider(probe);

        var found = Answer(await provider.SearchAsync(new ReleaseQuery("", "The Corrs", ReleaseMedia.Cd)));

        Assert.Single(found);
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task An_empty_search_asks_nobody_anything()
    {
        var probe = new FakeApi([Candidate("r1", "CD")]);
        var provider = new RpmMetadataProvider(probe);

        Assert.Empty(Answer(await provider.SearchAsync(new ReleaseQuery(""))));
        Assert.Equal(0, probe.Calls);
    }

    // ── a failure is not "nothing" ──

    /// <summary>The API not answering is not "no releases". It must not come back as an empty list,
    /// or the lookup window would say there were none.</summary>
    [Fact]
    public async Task A_search_the_api_did_not_answer_is_unanswered()
    {
        var provider = new RpmMetadataProvider(new FakeApi([]) { WidenedFails = true });

        Assert.IsType<Asked<IReadOnlyList<ReleaseSearchResult>>.Unanswered>(
            await provider.SearchAsync(new ReleaseQuery("Rumours")));
    }

    /// <summary>A narrowed search that failed is not widened. The widened pass is only for a medium
    /// that the catalogue ANSWERED has nothing on. Widening after a failure would show CD pressings
    /// as if there were no records.</summary>
    [Fact]
    public async Task A_narrowed_search_that_failed_is_not_widened()
    {
        var probe = new FakeApi([], whenWidened: [Candidate("r1", "CD")]) { NarrowFails = true };
        var provider = new RpmMetadataProvider(probe);

        Assert.IsType<Asked<IReadOnlyList<ReleaseSearchResult>>.Unanswered>(
            await provider.SearchAsync(new ReleaseQuery("Rumours", Media: ReleaseMedia.Vinyl)));
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task A_widened_search_that_failed_is_unanswered_and_not_the_narrow_emptiness()
    {
        var probe = new FakeApi([]) { WidenedFails = true };
        var provider = new RpmMetadataProvider(probe);

        Assert.IsType<Asked<IReadOnlyList<ReleaseSearchResult>>.Unanswered>(
            await provider.SearchAsync(new ReleaseQuery("Rumours", Media: ReleaseMedia.Vinyl)));
        Assert.Equal(2, probe.Calls);
    }

    /// <summary>"No such release" is an answer (the server's 404); not answering is not.</summary>
    [Fact]
    public async Task A_release_nobody_knows_is_an_answer_and_one_not_answered_is_not()
    {
        var unknown = new RpmMetadataProvider(new FakeApi([]));
        Assert.Null(Answer(await unknown.GetReleaseAsync("r1")));

        var down = new RpmMetadataProvider(new FakeApi([]) { Release = Asked.Failed<CatalogReleaseDetail?>("HTTP 502") });
        Assert.IsType<Asked<ReleaseDetail?>.Unanswered>(await down.GetReleaseAsync("r1"));
    }

    [Fact]
    public void The_provider_is_always_available_and_names_no_upstream()
    {
        var provider = new RpmMetadataProvider(new FakeApi([]));
        Assert.True(provider.IsAvailable);
        Assert.Equal("rpm", provider.Id);
    }

    private static Task<ReleaseSearchResult> Row(string? format) =>
        Search([Candidate("x", format)]).ContinueWith(t => t.Result.Single());

    /// <summary>An API with nobody signed in to it is a provider that cannot answer, and says so.
    /// Answering "always available" and then returning nothing would be drawn by the lookup window as
    /// "no releases", blaming the catalogue for a missing session.</summary>
    [Fact]
    public void A_provider_is_available_only_while_its_api_is()
    {
        Assert.True(new RpmMetadataProvider(new FakeApi([])).IsAvailable);
        Assert.False(new RpmMetadataProvider(new FakeApi([]) { IsAvailable = false }).IsAvailable);
    }

    /// <summary>A stand-in catalogue API. Returns one list for the media-filtered pass and another when
    /// widened, so the provider's fold and its widen-when-empty retry can both be exercised.</summary>
    private sealed class FakeApi(
        IReadOnlyList<CatalogRelease> results, IReadOnlyList<CatalogRelease>? whenWidened = null)
        : IRpmMetadataApi
    {
        public bool IsAvailable { get; init; } = true;

    public Task<Asked<CatalogPapers>> GetPapersAsync(
        string artist, string? album, int tracks, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<Asked<IReadOnlyList<CatalogArtist>>> GetArtistsNamedAsync(
        string name, int limit, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<Asked<CatalogArtist?>> GetArtistAsync(string mbid, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<Asked<CatalogPlays>> GetPlaysAsync(
        string artist, string album, int tracks, string? releaseMbid, CancellationToken ct = default)
        => throw new NotSupportedException();

        private readonly IReadOnlyList<CatalogRelease> _widened = whenWidened ?? results;
        public int Calls { get; private set; }

        /// <summary>The pass on one medium does not answer.</summary>
        public bool NarrowFails { get; init; }

        /// <summary>The pass on every medium does not answer.</summary>
        public bool WidenedFails { get; init; }

        public Asked<CatalogReleaseDetail?> Release { get; init; } = Asked.Answer<CatalogReleaseDetail?>(null);

        public Task<Asked<IReadOnlyList<CatalogRelease>>> SearchAsync(
            string text, string? artist, int limit, ReleaseMedia media,
            string? catalogNumber, string? country, uint? year, CancellationToken ct = default)
        {
            Calls++;
            var widened = media == ReleaseMedia.Any;
            return Task.FromResult((widened ? WidenedFails : NarrowFails)
                ? Asked.Failed<IReadOnlyList<CatalogRelease>>("HTTP 502")
                : Asked.Answer(widened ? _widened : results));
        }

        public Task<Asked<CatalogReleaseDetail?>> GetReleaseAsync(string releaseId, CancellationToken ct = default)
            => Task.FromResult(Release);

        public Task<Asked<CatalogDisc?>> LookupDiscAsync(string discId, string? toc, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Asked<string?>> GetCoverUrlAsync(string releaseId, int maxSize, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
