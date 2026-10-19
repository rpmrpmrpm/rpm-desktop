using RPM.Core.Metadata;
using RPM.Core.Metadata.Providers;
using RPM.Core.Net;
using RPM.Fingerprint.Catalog;
using RPM.Fingerprint.External;

namespace RPM.Core.Tests;

/// <summary>
/// The route by which a track's dossier is fetched, with the network replaced by a dictionary. So
/// these tests check the ROUTE, not the parsing: which calls are made, in what order, and which ones
/// are not made at all. Two rules are central. The album is looked up before the artist, because a
/// release-group search names its artist with an id, and identifying an artist through the record
/// the user owns is more reliable than through a name four bands share. And nothing ever searches
/// Wikipedia by title: an article found that way is a guess, and a confident paragraph about the
/// wrong band is worse than no paragraph.
/// </summary>
public class EncyclopediaLookupTests
{
    /// <summary>A stand-in for one of the two services: returns a fixed body for any URL that contains
    /// a known fragment, records every request, and returns null otherwise, which is what a real
    /// service does for something it does not know.</summary>
    private sealed class Service
    {
        private readonly Dictionary<string, string> _answers = new(StringComparer.Ordinal);

        public List<string> Asked { get; } = [];

        public Exception? Throws { get; set; }

        public Service Answer(string fragment, string body)
        {
            _answers[fragment] = body;
            return this;
        }

        public Task<string?> FetchAsync(string url, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Asked.Add(url);

            if (Throws is { } failure)
                throw failure;

            foreach (var (fragment, body) in _answers)
                if (url.Contains(fragment, StringComparison.Ordinal))
                    return Task.FromResult<string?>(body);

            return Task.FromResult<string?>(null);
        }

        public bool AskedFor(string fragment) =>
            Asked.Any(url => url.Contains(fragment, StringComparison.Ordinal));
    }

    private static string Sitelinks(string entity, string title) => $$"""
    { "entities": { "{{entity}}": { "sitelinks": { "enwiki": { "title": "{{title}}" } } } } }
    """;

    private static string Extract(string title, string text) => $$"""
    { "query": { "pages": { "1": { "title": "{{title}}", "extract": "{{text}}" } } } }
    """;

    // ── the papers the API returns, turned into what the pane draws ──────────────────────────────

    /// <summary>
    /// What the client side does: the mapping, and the two articles.
    ///
    /// <para>The catalogue rules (a release group credited to somebody else, an album against the
    /// single named after it, an act found by alias) belong to the API and are tested there, in
    /// <c>PapersTests</c>. <see cref="RPM.Fingerprint.External.CatalogueReach"/> refuses a direct
    /// MusicBrainz call from the app.</para>
    /// </summary>
    private static EncyclopediaLookup Lookup(Service web) => new(web.FetchAsync);

    private static CatalogArtist Band(params CatalogLink[] links) => new(
        Name: "Fleetwood Mac", Id: "mb-fm", Kind: "Group", Country: "GB", Area: "London",
        Began: "1967-07", Ended: null, HasEnded: false, WikidataId: "Q106648",
        Genres: ["rock"], Links: links,
        Members: [new CatalogArtistMember("Stevie Nicks", "mb-sn", "1948-05-26")]);

    [Fact]
    public void The_act_maps_across_field_for_field()
    {
        var artist = EncyclopediaLookup.Profile(Band(new CatalogLink("https://x.com/fm", "social network")));

        Assert.Equal("mb-fm", artist?.Mbid);
        Assert.Equal("Fleetwood Mac", artist?.Name);
        Assert.Equal("Group", artist?.Kind);
        Assert.Equal("London", artist?.Area);
        Assert.Equal("1967-07", artist?.Began);
        Assert.Equal(["rock"], artist?.Tags);
        Assert.Equal("Stevie Nicks", artist?.Lineup.Single().Name);
        Assert.Equal("1948-05-26", artist?.Lineup.Single().Born);
    }

    /// <summary>The two link rows merge into one, one entry per site: the same rule as the row under
    /// the card, applied to the wire record.</summary>
    [Fact]
    public void The_pages_merge_to_one_per_site()
    {
        var artist = EncyclopediaLookup.Profile(Band(
            new CatalogLink("https://www.discogs.com/artist/1234", "discogs"),
            new CatalogLink("https://www.discogs.com/artist/5678", "discogs"),
            new CatalogLink("https://fleetwoodmac.com/", "official homepage")));

        Assert.Equal(2, artist?.Pages.Count);

        // And every Discogs number is kept separately from the merged row, because the card that ranks
        // an act's records cannot use the one link the page shows. See ArtistProfile.DiscogsIds.
        Assert.Equal(["1234", "5678"], artist?.DiscogsIds);
    }

    /// <summary>An act that was not found is null, not a card with an empty name on it.</summary>
    [Fact]
    public void Nobody_maps_to_nothing()
    {
        Assert.Null(EncyclopediaLookup.Profile((CatalogArtist?)null));
        Assert.Null(EncyclopediaLookup.Profile((CatalogAlbum?)null));
    }

    /// <summary>The entity the API names is used, and the links are not searched for a second one.
    /// For an act whose links have no Wikidata relation, it is the only place the entity is found.</summary>
    [Fact]
    public async Task The_article_is_reached_through_the_entity_the_api_named()
    {
        var web = new Service()
            .Answer("wbgetentities", """{"entities":{"Q106648":{"sitelinks":{"enwiki":{"title":"Fleetwood Mac"}}}}}""")
            .Answer("wikipedia.org", Extract("Fleetwood Mac", "A band."));

        var dossier = Answer(await Lookup(web).ArticlesForAsync(new CatalogPapers(Artist: Band()), ["en"]));

        Assert.Equal("A band.", dossier.ArtistArticle?.Extract);
        Assert.Contains(web.Asked, url => url.Contains("Q106648"));
    }

    /// <summary>An act with no entity anywhere gets no article, and the rest of the card still shows:
    /// a lookup that partly worked is still worth drawing.</summary>
    [Fact]
    public async Task An_act_with_no_entity_still_fills_the_card()
    {
        var dossier = Answer(await Lookup(new Service()).ArticlesForAsync(
            new CatalogPapers(Artist: Band() with { WikidataId = null }), ["en"]));

        Assert.Null(dossier.ArtistArticle);
        Assert.Equal("Fleetwood Mac", dossier.Artist?.Name);
    }

    /// <summary>Nothing found returns an empty dossier, and never throws.</summary>
    [Fact]
    public async Task Empty_papers_are_an_empty_dossier()
        => Assert.True(Answer(await Lookup(new Service()).ArticlesForAsync(CatalogPapers.Empty, ["en"])).IsEmpty);

    // ── a failure is not nothing ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Wikipedia not answering is not "there is no article".
    ///
    /// <para>The two must be different answers. The service stores a dossier on disk for thirty days,
    /// so if a network failure looked like "no article", a record opened during a short outage would
    /// have no article for a month.</para>
    /// </summary>
    [Fact]
    public async Task Wikipedia_refusing_is_unanswered_and_not_a_dossier_without_its_article()
    {
        // The entity resolves; the article request gets no answer (the fetcher's null is a status that
        // was not a success).
        var web = new Service()
            .Answer("wbgetentities", Sitelinks("Q106648", "Fleetwood Mac"));

        var asked = await Lookup(web).ArticlesForAsync(new CatalogPapers(Artist: Band()), ["en"]);

        Assert.IsType<Asked<TrackDossier>.Unanswered>(asked);
    }

    [Fact]
    public async Task Wikidata_throwing_is_unanswered()
    {
        var web = new Service { Throws = new HttpRequestException("no route to host") };

        var asked = await Lookup(web).ArticlesForAsync(new CatalogPapers(Artist: Band()), ["en"]);

        Assert.IsType<Asked<TrackDossier>.Unanswered>(asked);
    }

    /// <summary>An answer that cannot be read is not an answer either. A response with a changed shape
    /// must not be read and stored as "no article".</summary>
    [Fact]
    public async Task An_unreadable_answer_is_unanswered()
    {
        var web = new Service()
            .Answer("wbgetentities", Sitelinks("Q106648", "Fleetwood Mac"))
            .Answer("wikipedia.org", "<html>Service Unavailable</html>");

        var asked = await Lookup(web).ArticlesForAsync(new CatalogPapers(Artist: Band()), ["en"]);

        Assert.IsType<Asked<TrackDossier>.Unanswered>(asked);
    }

    /// <summary>A page Wikipedia says does not exist IS an answer: the act has no article.</summary>
    [Fact]
    public async Task A_missing_page_is_an_answer_with_no_article()
    {
        var web = new Service()
            .Answer("wbgetentities", Sitelinks("Q106648", "Fleetwood Mac"))
            .Answer("wikipedia.org", """{"query":{"pages":{"-1":{"title":"Fleetwood Mac","missing":""}}}}""");

        var dossier = Answer(await Lookup(web).ArticlesForAsync(new CatalogPapers(Artist: Band()), ["en"]));

        Assert.Null(dossier.ArtistArticle);
        Assert.Equal("Fleetwood Mac", dossier.Artist?.Name);
    }

    /// <summary>A cancellation the caller asked for is thrown, not kept as either kind of answer.</summary>
    [Fact]
    public async Task A_cancelled_lookup_throws()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Lookup(new Service()).ArticlesForAsync(new CatalogPapers(Artist: Band()), ["en"], cancelled.Token));
    }

    private static TrackDossier Answer(Asked<TrackDossier> asked)
        => asked.Match(dossier => dossier, why => throw new Xunit.Sdk.XunitException("unanswered: " + why));

    // ── which genres there is anything to read about ──────────────────────────────────────────────

    /// <summary>
    /// Every kind of music the app files under has an article named for it, and the two shelves that
    /// are not kinds of music do not.
    ///
    /// <para><b>The LAYOUT depends on this, not only the card.</b> The genre page gives its article and
    /// its related genres a column of their own, and removes that column for a genre that has neither.
    /// This makes "Other" a page of shelves across the full width, with no empty 440-point column
    /// beside it.</para>
    ///
    /// <para>The other half of that condition, the related genres, is limited to what the collection
    /// actually holds, so it can be empty on a small library for a genre with many neighbours. The
    /// article half prevents that from mattering: it is a fact about the app, not about the user's
    /// records, so a real kind of music keeps its column on an empty library. A genre added to
    /// <see cref="Genres.All"/> and not to the table would break this silently, and only for the
    /// users with the least music.</para>
    /// </summary>
    [Fact]
    public void Every_genre_but_the_catch_alls_has_an_article_named_for_it()
    {
        string[] catchAlls = [Genres.Unknown, "Other"];

        foreach (var genre in Genres.All.Where(g => !catchAlls.Contains(g, StringComparer.OrdinalIgnoreCase)))
            Assert.True(EncyclopediaLookup.Describes(genre),
                $"{genre} is a kind of music with no article named for it, so its page loses its column");

        foreach (var catchAll in catchAlls)
        {
            Assert.False(EncyclopediaLookup.Describes(catchAll),
                $"{catchAll} is not a kind of music and should have nothing to read about");

            // The other half of the same condition. If either one is true the column stays, so both
            // must be false for the page to give the shelves the whole width.
            Assert.Empty(Genres.Related(catchAll));
        }
    }

    /// <summary>The predicate compares genre names the same way as the rest of the app, and returns
    /// false for everything that is not one of them, including empty values a caller can pass.</summary>
    [Theory]
    [InlineData("Rock", true)]
    [InlineData("rock", true)]
    [InlineData("R&B", true)]
    [InlineData("Skiffle", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void An_article_is_found_however_the_genre_is_cased(string? genre, bool described)
        => Assert.Equal(described, EncyclopediaLookup.Describes(genre));

    /// <summary>
    /// A genre's article not arriving is not "nothing is written about this genre".
    ///
    /// <para>If the article were fetched once a session and the result kept, null included, one
    /// refused request would leave a genre's column empty until the app restarts.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_genres_article_not_arriving_is_unanswered()
        => Assert.IsType<Asked<EncyclopediaArticle?>.Unanswered>(
            await Lookup(new Service()).GenreArticleAsync("Rock"));

    /// <summary>A genre the table does not name gets no article, and no request is made: the table is
    /// compiled in, so asking again on every visit would be a request that cannot succeed.</summary>
    [Theory]
    [InlineData("Other")]
    [InlineData("Skiffle")]
    [InlineData("")]
    [InlineData(null)]
    public async Task A_genre_the_table_does_not_name_is_answered_at_nothing(string? genre)
    {
        var web = new Service();

        var asked = await Lookup(web).GenreArticleAsync(genre);

        Assert.Null(Assert.IsType<Asked<EncyclopediaArticle?>.Answered>(asked).Value);
        Assert.Empty(web.Asked);
    }

    [Fact]
    public async Task A_genres_article_that_arrives_is_the_answer()
    {
        var web = new Service().Answer("titles=Latin%20music", Extract("Latin music", "Latin music is..."));

        var asked = await Lookup(web).GenreArticleAsync("Latin");

        Assert.Equal("Latin music is...",
            Assert.IsType<Asked<EncyclopediaArticle?>.Answered>(asked).Value?.Extract);
    }
}
