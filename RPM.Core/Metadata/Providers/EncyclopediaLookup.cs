using System.Net.Http;
using RPM.Core.Net;
using RPM.Fingerprint.Catalog;    // NameMatch, which moved here upstream
using RPM.Fingerprint.External;

namespace RPM.Core.Metadata.Providers;

/// <summary>
/// What the free encyclopedias say about the record now playing: MusicBrainz gives the facts, Wikipedia
/// gives the prose, and the row of outside pages comes with them.
///
/// <para>The order matters. An album is looked up first, even though the pane leads with the artist. A
/// release-group search names its artist WITH their MusicBrainz id attached. So the album the user owns
/// identifies the artist, and a name that four bands share does not. The artist is searched by name only
/// when there is no album to go on.</para>
///
/// <para><b>"No article" and "Wikipedia did not answer" are different answers.</b> The first is an
/// <see cref="Asked{T}.Answered"/> holding null. It may be written down. The second is
/// <see cref="Asked{T}.Unanswered"/>, and it must not be written down. A dossier saved with its article
/// missing, because one request failed, would keep that record without its article for thirty days.</para>
///
/// <para>The two fetchers are separate because they have different rate limits. MusicBrainz allows one
/// request per second per IP and refuses the rest, so its fetcher is the app's shared, throttled one.
/// Wikipedia and Wikidata have no such limit, so they are asked directly.</para>
/// </summary>
public sealed partial class EncyclopediaLookup(FetchJson web)
{
    private const string WikidataApi = "https://www.wikidata.org/w/api.php";

    /// <summary>
    /// The app's connection to the encyclopedias, and to nothing else.
    ///
    /// <para>There is no catalogue fetcher here. Catalogue calls go through the RPM API, which holds the
    /// rate limit and a cache that all listeners share. What is left here is Wikipedia and Wikidata. They
    /// are here because the article to read depends on the reader's language. Neither service is
    /// rate-limited.</para>
    /// </summary>
    /// <param name="userAgent">Wikipedia asks clients to send a real user agent, as a courtesy.</param>
    public static EncyclopediaLookup Live(HttpClient http, string userAgent) => new(
        web: async (url, ct) =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(userAgent);
            using var response = await http.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : null;
        });

    /// <summary>
    /// The papers the API answered with, turned into what the pane draws. Each side's Wikipedia article is
    /// fetched from here.
    ///
    /// <para><b>The catalogue calls are not made here, and cannot be.</b> Only the RPM API may reach a
    /// catalogue. It holds the keys, the rate gate and the cache that all listeners share.
    /// <see cref="RPM.Fingerprint.External.CatalogueReach"/> refuses the request, rather than trusting
    /// anyone to remember. What is left on this side is the encyclopedia. It is here for a reason. Which
    /// Wikipedia to read depends on the person reading: their own language first, then English. An answer
    /// cached for everybody could only be cached in one language.</para>
    ///
    /// <para>The entity is taken from the wire record where the API put one there. It is found in the links
    /// only where the API did not. Both are the same question asked of different shapes.</para>
    ///
    /// <para>Unanswered if either article's request failed. The dossier is then not half-built and returned
    /// as though it were whole. The caller would write it down.</para>
    /// </summary>
    public async Task<Asked<TrackDossier>> ArticlesForAsync(
        CatalogPapers papers, IReadOnlyList<string>? languages = null, CancellationToken ct = default)
    {
        var wikis = languages is { Count: > 0 } ? languages : ["en"];

        var artist = Profile(papers.Artist);
        var album = Profile(papers.Album);

        // Both at once. Wikipedia is not rate-limited, and the second request should not wait for the first
        // when neither depends on the other.
        var artistArticle = ArticleAsync(papers.Artist?.WikidataId, artist?.Pages, wikis, ct);
        var albumArticle = ArticleAsync(null, album?.Pages, wikis, ct);

        // The line-up as it stands, with faces, from the same encyclopedia and at the same time. It is part of
        // the dossier, so it is kept with it and painted once. See LineupAsync.
        var lineup = LineupAsync(papers.Artist?.WikidataId, artist?.Members, ct);
        await Task.WhenAll(artistArticle, albumArticle, lineup);
        if (artist is not null && lineup.Result is { } current)
            artist = artist with { Members = current };

        return artistArticle.Result.Match(
            aboutThem => albumArticle.Result.Match(
                aboutIt => Asked.Answer(new TrackDossier(artist, album, aboutThem, aboutIt)),
                why => Asked.Failed<TrackDossier>("the album's article: " + why)),
            why => Asked.Failed<TrackDossier>("the artist's article: " + why));
    }

    /// <summary>The act as the pane holds them. Null where the API found nobody. That is an ordinary
    /// answer, not a failure.</summary>
    public static ArtistProfile? Profile(CatalogArtist? artist)
        => artist is null || artist.Name.Length == 0 ? null : new ArtistProfile(
            Mbid: artist.Id,
            Name: artist.Name,
            Disambiguation: artist.Disambiguation,
            Kind: artist.Kind,
            Country: artist.Country,
            Area: artist.Area,
            Began: artist.Began,
            Ended: artist.Ended,
            HasEnded: artist.HasEnded,
            Genres: artist.Genres,
            Links: Pages(artist.Links),
            Members: artist.Members is not { Count: > 0 } members ? null :
                [.. members.Select(m => new BandMember(m.Name, m.Id, m.Born, m.Died, m.HasDied))],
            // Read from the links rather than carried. The wire record keeps one relation per site, and
            // every Discogs number is needed to rank an act's records. See ArtistProfile.DiscogsIds.
            DiscogsArtistIds: artist.Links is not { Count: > 0 } links ? null :
                [.. links.Select(l => ReferenceLinks.DiscogsArtistId(l.Url))
                         .Where(id => id is { Length: > 0 }).Select(id => id!).Distinct()]);

    /// <summary>The record as the pane holds it.</summary>
    public static AlbumProfile? Profile(CatalogAlbum? album)
        => album is null || album.Title.Length == 0 ? null : new AlbumProfile(
            Mbid: album.Id,
            Title: album.Title,
            Artist: album.Artist,
            Disambiguation: album.Disambiguation,
            FirstReleased: album.FirstReleased,
            PrimaryType: album.PrimaryType,
            SecondaryTypes: album.SecondaryTypes,
            Genres: album.Genres,
            Links: Pages(album.Links));

    /// <summary>The outside pages, one per site, as the row under the card lists them.</summary>
    private static IReadOnlyList<ReferenceLink>? Pages(IReadOnlyList<CatalogLink>? links)
        => links is not { Count: > 0 }
            ? null
            : ReferenceLinks.Merge(
                [.. links.Select(l => new ReferenceLink(ReferenceLinks.Classify(l.Url, l.Relation), l.Url))]);

    /// <summary>How many artists a name search offers. Five is enough that the act meant is among them for
    /// any name people actually share. It is few enough to read at a glance in a narrow pane.</summary>
    public const int MaxArtistsOffered = 5;

    /// <summary>
    /// What Wikipedia says about one of the app's genres.
    ///
    /// <para><b>Named outright, never searched.</b> The rest of this class does not find an article by
    /// searching a name. A confident paragraph about the wrong band is worse than no paragraph. See
    /// <see cref="ArticleAsync"/>. A genre is the one subject where that rule does not apply. There are
    /// about thirty genres. They are the app's own closed list. Which article describes each one is a fact
    /// that can be written down and stays correct. So it is written down, in <see cref="Encyclopedias"/>,
    /// and nothing here guesses.</para>
    ///
    /// <para><b>English, and only English.</b> The titles in that table are English titles. Asking
    /// fr.wikipedia for "Rock music" finds nothing. The other languages reach their translations through
    /// Wikidata's sitelinks. A genre has no entity id to start from, so that route is not open here. A page
    /// that says what it is reading is better than one that quietly shows nothing outside one locale.
    /// Anything shown from this names its source. That is where a reader learns the article is
    /// English.</para>
    ///
    /// <para>Null for a genre that has nothing written down for it (<see cref="Genres.Unknown"/>, and
    /// anything that is not one of <see cref="Genres.All"/>). Nothing is asked for those. Unanswered when
    /// Wikipedia does not answer. That is a different thing. A null answer is kept. An unanswered one is
    /// asked again. Keeping an unanswered result would leave a genre's column empty until the app
    /// restarted.</para>
    /// </summary>
    /// <param name="genre">One of <see cref="Genres.All"/>, as the app spells it.</param>
    public async Task<Asked<EncyclopediaArticle?>> GenreArticleAsync(
        string? genre, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(genre))
            return Asked.Answer<EncyclopediaArticle?>(null);

        foreach (var (name, title) in Encyclopedias)
            if (name.Equals(genre, StringComparison.OrdinalIgnoreCase))
                return await ExtractAsync("en", title, ct);

        return Asked.Answer<EncyclopediaArticle?>(null);
    }

    /// <summary>
    /// Whether there is an article to ask for at all. This is answered without asking.
    ///
    /// <para><b>Why this exists, rather than waiting for the fetch to return null.</b> The genre page keeps
    /// its article in a column of its own. A genre with nothing written about it should not have that
    /// column. See <c>GenreDetailView</c>, which removes the column for the two shelves that are not kinds
    /// of music. Learning this from the fetch means learning it a network round trip after the page is on
    /// screen. Removing a column from under a reader who is already reading is worse than never showing
    /// it.</para>
    ///
    /// <para>So the answer is read from <see cref="Encyclopedias"/>. That is a closed table compiled into
    /// the app. The answer is the same before the request as after it, and it is the same offline. A test
    /// holds the two together, not this comment. A genre added to the table without this method agreeing
    /// would be a page with an article that no column was drawn for.</para>
    /// </summary>
    /// <param name="genre">One of <see cref="Genres.All"/>, as the app spells it.</param>
    public static bool Describes(string? genre)
    {
        if (string.IsNullOrWhiteSpace(genre))
            return false;

        foreach (var (name, _) in Encyclopedias)
            if (name.Equals(genre, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>
    /// Which Wikipedia article describes each of the app's genres.
    ///
    /// <para>These are stated, not derived from the label. The two are different kinds of text. A shelf
    /// label is short, so it fits in a list to pick from. An article title is whatever the encyclopedia
    /// chose. "Metal" is a chemical element there, "Rock" is a stone, and "Musical" is an adjective. Half
    /// of these would land on the wrong page or on a disambiguation list.</para>
    ///
    /// <para>Redirects are followed by the request (see <see cref="ExtractAsync"/>). So a title that is a
    /// redirect today still reaches the article it points to. That is why the looser entries here are
    /// safe.</para>
    ///
    /// <para>A genre missing from this table has no card. That is a valid state, not a gap.
    /// <see cref="Genres.Unknown"/> is not a kind of music, so it has nothing to read about.</para>
    /// </summary>
    private static readonly (string Genre, string Title)[] Encyclopedias =
    [
        ("Rock", "Rock music"),
        ("Indie", "Indie rock"),
        ("Pop", "Pop music"),
        ("Disco", "Disco"),
        ("Funk", "Funk"),
        ("Soul", "Soul music"),
        ("R&B", "Contemporary R&B"),
        ("Rap", "Hip hop music"),
        ("Trap", "Trap music"),
        ("Old School Hip Hop", "Old-school hip hop"),
        ("Hip-Hop / Rap", "Hip hop music"),
        ("House", "House music"),
        ("Techno", "Techno"),
        ("Club", "Club music"),
        ("Electronic / Dance", "Electronic dance music"),
        ("Jazz", "Jazz"),
        ("Blues", "Blues"),
        ("Country", "Country music"),
        ("Folk", "Folk music"),
        ("Classical", "Classical music"),
        ("Neo-Classical", "Neoclassicism (music)"),
        ("Opera", "Opera"),
        ("Metal", "Heavy metal music"),
        ("Punk", "Punk rock"),
        ("Noise", "Noise music"),
        ("Avant-Garde", "Avant-garde music"),
        ("Reggae", "Reggae"),
        ("Latin", "Latin music"),
        ("World", "World music"),
        ("Religious", "Religious music"),
        ("Soundtrack", "Film score"),
        ("Video Game", "Video game music"),
        ("Musical", "Musical theatre"),
        ("Spoken Word", "Spoken word"),
    ];

    // ── Wikipedia, by way of Wikidata ──

    /// <summary>
    /// The article for whatever these links point at, in the best language it exists in.
    ///
    /// <para>Wikidata is tried first. MusicBrainz points to Wikidata, and a person matched the entity. A
    /// direct Wikipedia link is the older form. It is used only when there is no Wikidata entity. Nothing
    /// here searches Wikipedia by name. An article found by name is a guess about which band was meant. A
    /// confident paragraph about the wrong band is worse than none.</para>
    /// </summary>
    /// <param name="entity">The Wikidata entity the API already named, where it did. This saves digging the
    /// same id out of the links. It is also the only source of the id for an act whose links carry no
    /// Wikidata relation.</param>
    /// <returns>Null when there is no article to be had. That means no entity and no link, or an entity
    /// with no page in any of the languages. Unanswered when Wikidata or Wikipedia did not answer. If the
    /// Wikidata read fails, the Wikipedia link is not tried. That link is the older form and may name a
    /// different article. The honest answer is that this article was not reached.</returns>
    private async Task<Asked<EncyclopediaArticle?>> ArticleAsync(
        string? entity, IReadOnlyList<ReferenceLink>? links, IReadOnlyList<string> languages,
        CancellationToken ct)
    {
        if (entity is not { Length: > 0 } && links is not { Count: > 0 })
            return Asked.Answer<EncyclopediaArticle?>(null);

        // One entity is tried. A second Wikidata request for the same card would be a round trip spent on a
        // question already answered.
        if (Entities(entity, links).FirstOrDefault() is { } known)
        {
            var url = $"{WikidataApi}?action=wbgetentities&format=json&props=sitelinks&ids={Uri.EscapeDataString(known)}";

            switch (await ReadAsync(url, body => WikipediaJson.ParseSitelink(body, languages), ct))
            {
                case Asked<(string Language, string Title)?>.Unanswered failed:
                    return Asked.Failed<EncyclopediaArticle?>(failed.Why);
                case Asked<(string Language, string Title)?>.Answered { Value: { } sitelink }:
                    return await ExtractAsync(sitelink.Language, sitelink.Title, ct);
            }
        }

        foreach (var link in links ?? [])
            if (ReferenceLinks.WikipediaArticle(link.Url) is { } article)
                return await ExtractAsync(article.Language, article.Title, ct);

        return Asked.Answer<EncyclopediaArticle?>(null);
    }

    /// <summary>The entity the API named, then any the links carry.</summary>
    private static IEnumerable<string> Entities(string? named, IReadOnlyList<ReferenceLink>? links)
    {
        if (named is { Length: > 0 })
            yield return named;

        foreach (var link in links ?? [])
            if (ReferenceLinks.WikidataEntityId(link.Url) is { } entity)
                yield return entity;
    }

    /// <summary>One article's lead section. Null for a page that does not exist, and for a disambiguation
    /// page (see <see cref="WikipediaJson.ParseExtract"/>). Unanswered when Wikipedia did not
    /// answer.</summary>
    private Task<Asked<EncyclopediaArticle?>> ExtractAsync(string language, string title, CancellationToken ct)
    {
        // formatversion 1 keys the pages by id, which is the shape the parser reads. redirects=1 makes a
        // title that Wikidata still spells the old way land on the article it was renamed to.
        var url = $"https://{language}.wikipedia.org/w/api.php?action=query&format=json&formatversion=1"
                + "&prop=extracts%7Cpageimages%7Cinfo%7Cpageprops&inprop=url&ppprop=disambiguation"
                + "&exintro=1&explaintext=1&piprop=thumbnail&pithumbsize=480&redirects=1"
                + $"&titles={Uri.EscapeDataString(title)}";

        return ReadAsync(url, body => WikipediaJson.ParseExtract(body, language), ct);
    }

    // ── one request ──

    /// <summary>
    /// One request and its parse. Every way of not getting a readable answer is kept as a failure. These
    /// are: a throw (no network, a time-out), a status that was not a success (the fetcher returns null for
    /// those), and a body the parser could not read. What the parser returns, including null, is the
    /// answer.
    ///
    /// <para>A cancellation the caller asked for is rethrown, not kept. It is neither an answer nor a
    /// failure of the service.</para>
    /// </summary>
    private async Task<Asked<T>> ReadAsync<T>(string url, Func<string, T> parse, CancellationToken ct)
    {
        string? body;
        try
        {
            body = await web(url, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Asked.Failed<T>($"{Host(url)}: {ex.GetType().Name}: {ex.Message}");
        }

        if (body is null)
            return Asked.Failed<T>($"{Host(url)}: refused");

        try
        {
            return Asked.Answer(parse(body));
        }
        catch (Exception ex)
        {
            return Asked.Failed<T>($"{Host(url)}: unreadable answer: {ex.Message}");
        }
    }

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
}
