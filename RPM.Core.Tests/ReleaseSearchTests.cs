using RPM.Core.Metadata.Providers;
using RPM.Core.Net;
using RPM.Fingerprint.Catalog;

namespace RPM.Core.Tests;

/// <summary>
/// Searching every catalogue at once. What matters: one source being down does not lose the others,
/// a vinyl import sees vinyl first, and the expensive part, scoring track lengths, is spent on a
/// shortlist and not on everything the search returned.
/// </summary>
public class ReleaseSearchTests
{
    private sealed class FakeProvider(string id, params ReleaseSearchResult[] results) : IMetadataProvider
    {
        public string Id => id;
        public string DisplayName => id;
        public bool IsAvailable { get; init; } = true;
        public bool SupportsCatalogNumberSearch { get; init; }
        public bool Throws { get; init; }

        /// <summary>Says it did not answer, as a provider does when its catalogue is down.</summary>
        public bool Fails { get; init; }

        /// <summary>Every listing request goes unanswered.</summary>
        public bool ListingsFail { get; init; }

        public int Lookups { get; private set; }
        public Dictionary<string, IReadOnlyList<ReleaseTrackInfo>> Listings { get; } = new();

        public Task<Asked<IReadOnlyList<ReleaseSearchResult>>> SearchAsync(ReleaseQuery q, CancellationToken ct = default)
            => Throws
                ? throw new InvalidOperationException("catalogue is down")
                : Task.FromResult(Fails
                    ? Asked.Failed<IReadOnlyList<ReleaseSearchResult>>("HTTP 502")
                    : Asked.Answer<IReadOnlyList<ReleaseSearchResult>>(results));

        public Task<Asked<ReleaseDetail?>> GetReleaseAsync(string releaseId, CancellationToken ct = default)
        {
            Lookups++;
            if (ListingsFail)
                return Task.FromResult(Asked.Failed<ReleaseDetail?>("HTTP 502"));
            if (!Listings.TryGetValue(releaseId, out var tracks))
                return Task.FromResult(Asked.Answer<ReleaseDetail?>(null));
            return Task.FromResult(Asked.Answer<ReleaseDetail?>(
                new ReleaseDetail(new ReleaseRef(id, releaseId), "T", "A", 1977, null, null, tracks)));
        }
    }

    /// <summary>A search across every available catalogue, merged and ordered as the lookup window
    /// does it: each one is asked on its own, the answers are kept and the failures are left out.</summary>
    private static async Task<IReadOnlyList<ReleaseSearchResult>> Found(ReleaseSearch search, ReleaseQuery query)
    {
        var answers = await Task.WhenAll(search.Available.Select(p => search.SearchOneAsync(p, query)));
        return ReleaseSearch.Order(
            [.. answers.SelectMany(a => a.Answers(out var found, out _) ? found : Array.Empty<ReleaseSearchResult>())],
            query);
    }

    private static ReleaseSearchResult Result(string id, string provider = "musicbrainz",
        string? format = null, double relevance = 0.5, uint year = 1977, int trackCount = 3) =>
        new(new ReleaseRef(provider, id), "Rumours", "Fleetwood Mac", year, year.ToString(),
            "GB", format, trackCount, Relevance: relevance);

    private static ReleaseTrackInfo Track(uint n, double? len) => new(n, $"T{n}", "A", len);

    [Fact]
    public async Task Every_available_catalogue_is_asked_and_the_results_merge()
    {
        var search = new ReleaseSearch([
            new FakeProvider("musicbrainz", Result("mb1")),
            new FakeProvider("discogs", Result("dg1", provider: "discogs")),
        ]);

        var results = await Found(search, new ReleaseQuery("rumours"));

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Ref.Provider == "musicbrainz");
        Assert.Contains(results, r => r.Ref.Provider == "discogs");
    }

    [Fact]
    public async Task A_catalogue_that_is_down_does_not_lose_the_others()
    {
        var search = new ReleaseSearch([
            new FakeProvider("discogs", Result("dg1", provider: "discogs")) { Throws = true },
            new FakeProvider("musicbrainz", Result("mb1")),
        ]);

        var down = await search.SearchOneAsync(search.Providers[0], new ReleaseQuery("rumours"));
        var up = await search.SearchOneAsync(search.Providers[1], new ReleaseQuery("rumours"));

        // The one that threw is unanswered, not empty: the window says it failed and not that it
        // found none. The other one's answer stands.
        Assert.IsType<Asked<IReadOnlyList<ReleaseSearchResult>>.Unanswered>(down);
        Assert.Equal("musicbrainz", Assert.Single(Assert.IsType<Asked<IReadOnlyList<ReleaseSearchResult>>.Answered>(up).Value).Ref.Provider);
    }

    [Fact]
    public async Task A_catalogue_that_says_it_did_not_answer_is_unanswered_and_not_empty()
    {
        var search = new ReleaseSearch([new FakeProvider("discogs", Result("dg1", provider: "discogs")) { Fails = true }]);

        Assert.IsType<Asked<IReadOnlyList<ReleaseSearchResult>>.Unanswered>(
            await search.SearchOneAsync(search.Providers[0], new ReleaseQuery("rumours")));
    }

    /// <summary>A listing that did not arrive leaves the row unscored and carries no listing, so picking
    /// it asks again instead of taking "no listing" from a request that failed.</summary>
    [Fact]
    public async Task A_listing_that_did_not_arrive_is_unscored_and_not_carried()
    {
        var provider = new FakeProvider("musicbrainz", Result("r1")) { ListingsFail = true };
        var search = new ReleaseSearch([provider]);
        var results = await Found(search, new ReleaseQuery("rumours"));

        var ranked = await search.RankAsync(results, [100, 150, 220], []);

        Assert.Null(ranked[0].Match);
        Assert.Null(ranked[0].Detail);
    }

    [Fact]
    public async Task An_unavailable_provider_is_skipped_not_asked()
    {
        // Discogs before the user has connected their account: not an error, just not there.
        var discogs = new FakeProvider("discogs", Result("dg1", provider: "discogs")) { IsAvailable = false };
        var search = new ReleaseSearch([discogs, new FakeProvider("musicbrainz", Result("mb1"))]);

        var results = await Found(search, new ReleaseQuery("rumours"));

        Assert.Single(results);
        Assert.Single(search.Available);
    }

    [Fact]
    public async Task A_vinyl_import_sees_vinyl_pressings_first()
    {
        var search = new ReleaseSearch([
            new FakeProvider("musicbrainz",
                Result("cd", format: "CD", relevance: 0.9),
                Result("lp", format: "12\" Vinyl", relevance: 0.4)),
        ]);

        var results = await Found(search, new ReleaseQuery("rumours", Media: ReleaseMedia.Vinyl));

        Assert.Equal("lp", results[0].Ref.Id);   // despite the CD scoring higher on the query itself
    }

    [Fact]
    public async Task The_medium_asked_for_leads_whichever_one_it_is()
    {
        // The same rule for every medium, not a vinyl special case: what the user is holding comes
        // first, and the catalogue's own relevance only breaks ties inside it.
        var search = new ReleaseSearch([
            new FakeProvider("musicbrainz",
                Result("lp", format: "12\" Vinyl", relevance: 0.9),
                Result("tape", format: "Cassette", relevance: 0.4)),
        ]);

        var results = await Found(search, new ReleaseQuery("rumours", Media: ReleaseMedia.Cassette));

        Assert.Equal("tape", results[0].Ref.Id);
    }

    [Fact]
    public async Task Without_a_vinyl_hint_the_catalogues_own_relevance_leads()
    {
        var search = new ReleaseSearch([
            new FakeProvider("musicbrainz",
                Result("cd", format: "CD", relevance: 0.9),
                Result("lp", format: "12\" Vinyl", relevance: 0.4)),
        ]);

        var results = await Found(search, new ReleaseQuery("rumours"));

        Assert.Equal("cd", results[0].Ref.Id);
    }

    /// <summary>
    /// The vinyl filter has to be a filter and not a preference: a famous album is overwhelmingly
    /// CDs in the catalogue (thirteen to one for "Rumours" in the MusicBrainz index), so ranking
    /// alone would push the pressing being hunted off the first page entirely.
    /// </summary>
    [Fact]
    public void The_vinyl_filter_is_a_query_not_a_sort()
    {
        // Pinned as a constant and not asserted against the network: MusicBrainz's format field is
        // NOT tokenised, so format:Vinyl matches nothing while format:"12\" Vinyl" matches
        // everything. The quotes are the only difference.
        Assert.Contains("format:\"12\\\" Vinyl\"", RPM.Fingerprint.External.MusicBrainzClient.VinylFormats);
        Assert.Contains("format:\"7\\\" Vinyl\"", RPM.Fingerprint.External.MusicBrainzClient.VinylFormats);
        Assert.Contains("format:\"Vinyl\"", RPM.Fingerprint.External.MusicBrainzClient.VinylFormats);
        Assert.Contains("format:\"Shellac\"", RPM.Fingerprint.External.MusicBrainzClient.VinylFormats);
    }

    /// <summary>Each lookup takes a second because it is throttled. Twenty-five of them take half a
    /// minute, so only the shortlist is scored and the rest keep the free ordering.</summary>
    [Fact]
    public async Task Only_the_shortlist_costs_a_lookup()
    {
        var provider = new FakeProvider("musicbrainz",
            Enumerable.Range(1, 10).Select(i => Result($"r{i}")).ToArray());
        foreach (var i in Enumerable.Range(1, 10))
            provider.Listings[$"r{i}"] = [Track(1, 100), Track(2, 100), Track(3, 100)];

        var search = new ReleaseSearch([provider]) { ShortlistSize = 3 };
        var results = await Found(search, new ReleaseQuery("rumours"));

        var ranked = await search.RankAsync(results, [100, 100, 100], []);

        Assert.Equal(3, provider.Lookups);
        Assert.Equal(10, ranked.Count);                        // everything is still listed
        Assert.Equal(3, ranked.Count(r => r.Match is not null)); // only the shortlist is scored
    }

    [Fact]
    public async Task The_release_whose_lengths_match_is_ranked_first()
    {
        var provider = new FakeProvider("musicbrainz", Result("wrong"), Result("right"));
        provider.Listings["wrong"] = [Track(1, 200), Track(2, 200), Track(3, 200)];
        provider.Listings["right"] = [Track(1, 100), Track(2, 150), Track(3, 220)];

        var search = new ReleaseSearch([provider]);
        var results = await Found(search, new ReleaseQuery("rumours"));

        var ranked = await search.RankAsync(results, [100, 150, 220], []);

        Assert.Equal("right", ranked[0].Release.Ref.Id);
        Assert.True(ranked[0].Match!.Score > ranked[1].Match!.Score);
    }

    [Fact]
    public async Task Scores_are_reported_as_they_land()
    {
        var provider = new FakeProvider("musicbrainz", Result("a"), Result("b"));
        provider.Listings["a"] = [Track(1, 100), Track(2, 150), Track(3, 220)];
        provider.Listings["b"] = [Track(1, 100), Track(2, 150), Track(3, 220)];

        var search = new ReleaseSearch([provider]);
        var results = await Found(search, new ReleaseQuery("rumours"));

        var seen = new List<string>();
        await search.RankAsync(results, [100, 150, 220], [], onRanked: r => seen.Add(r.Release.Ref.Id));

        Assert.Equal(2, seen.Count);   // the list can re-order under the user rather than block on all of it
    }

    [Fact]
    public async Task An_art_provider_supplies_the_cover_for_the_releases_it_owns()
    {
        // The rpm art provider speaks for the releases that the rpm catalogue returns.
        var search = new ReleaseSearch([new FakeProvider("rpm", Result("mb1", provider: "rpm"))]);
        var results = await Found(search, new ReleaseQuery("rumours"));

        var ranked = await search.RankAsync(results, [], [new RpmReleaseArtProvider(new CoverApi("https://art/mb1.jpg"))]);

        Assert.Equal("https://art/mb1.jpg", ranked[0].CoverUrl);
    }

    /// <summary>A pressing that the archives hold no picture of gets no address. A provider that
    /// built the URL itself would give one that fails with 404 only when something tries to draw it,
    /// and that would put an "art available" mark on releases that have none.</summary>
    [Fact]
    public async Task A_release_with_no_art_yields_no_cover_address()
    {
        var search = new ReleaseSearch([new FakeProvider("rpm", Result("mb1", provider: "rpm"))]);
        var results = await Found(search, new ReleaseQuery("rumours"));

        var ranked = await search.RankAsync(results, [], [new RpmReleaseArtProvider(new CoverApi(null))]);

        Assert.Null(ranked[0].CoverUrl);
    }

    /// <summary>An archive that did not answer gives a row no picture, the same as one with none. The
    /// difference matters only where something is written down, and nothing is here.</summary>
    [Fact]
    public async Task A_cover_the_archive_did_not_answer_for_is_no_picture()
    {
        var search = new ReleaseSearch([new FakeProvider("rpm", Result("mb1", provider: "rpm"))]);
        var results = await Found(search, new ReleaseQuery("rumours"));

        var ranked = await search.RankAsync(results, [], [new RpmReleaseArtProvider(new CoverApi("https://art/mb1.jpg") { Fails = true })]);

        Assert.Null(ranked[0].CoverUrl);
    }

    [Fact]
    public async Task A_cover_the_search_already_gave_is_not_asked_for_again()
    {
        // Discogs returns a cover with the result; nothing should override it.
        var inline = Result("dg1", provider: "discogs") with { CoverUrl = "https://img.discogs.com/x.jpg" };
        var search = new ReleaseSearch([new FakeProvider("discogs", inline)]);
        var results = await Found(search, new ReleaseQuery("rumours"));

        var ranked = await search.RankAsync(results, [], [new RpmReleaseArtProvider(new CoverApi("https://art/should-not-win.jpg"))]);

        Assert.Equal("https://img.discogs.com/x.jpg", ranked[0].CoverUrl);
    }

    /// <summary>A catalogue API that answers about covers and nothing else.</summary>
    private sealed class CoverApi(string? url) : IRpmMetadataApi
    {
        public bool IsAvailable => true;

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

        public Task<Asked<IReadOnlyList<CatalogRelease>>> SearchAsync(
            string text, string? artist, int limit, ReleaseMedia media,
            string? catalogNumber, string? country, uint? year, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Asked<CatalogReleaseDetail?>> GetReleaseAsync(string releaseId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Asked<CatalogDisc?>> LookupDiscAsync(string discId, string? toc, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Asked<string?>> GetCoverUrlAsync(string releaseId, int maxSize, CancellationToken ct = default)
            => Task.FromResult(Fails ? Asked.Failed<string?>("HTTP 502") : Asked.Answer(url));

        /// <summary>The cover request does not answer.</summary>
        public bool Fails { get; init; }
    }
}
