using System.Text.Json;

namespace RPM.Core.Metadata.Providers;

/// <summary>
/// One of an artist's records, as Discogs' own collectors have it: what it is called, when it came out,
/// and how many of them keep a copy.
/// </summary>
/// <param name="Have">How many Discogs users have it in their collection. This is the whole measure of
/// "popular" here. Be clear about what it measures: not plays, sales or charts, but how many record
/// collectors own one. On a site that is a database of physical pressings, that is the honest reading. It
/// is also the only figure the API supplies.</param>
/// <param name="Want">How many users are looking for one. This is kept because the pair says more than the
/// first number alone. A record that twenty thousand people own is famous. A record that two thousand own
/// and ten thousand want is rare.</param>
/// <param name="Url">The page on Discogs, so each row links to it.</param>
/// <param name="IsMaster">Whether this is the album itself or one pressing of it. It is carried here, not
/// worked out again from the id. Ids are unique only within their own kind. Master 1234 and release 1234
/// are two different records. A rule that asked "is this id one of the masters" would keep a pressing just
/// because an unrelated album had the same number.</param>
public sealed record PopularRelease(
    long Id,
    string Title,
    int Year,
    int Have,
    int Want,
    string? Thumb,
    string Url,
    bool IsMaster);

/// <summary>
/// What asking Discogs for an artist's best-known records came back with.
///
/// <para>There are three answers, not a list that may be empty. An empty list hid the one case the reader
/// can act on. Discogs is read through the listener's own connected account, because the API signs requests
/// with their tokens, and there is no other way in. So the most common reason for no card is that they have
/// never connected an account. As an empty list, that looked like "this artist has no records on Discogs",
/// and the card was simply missing, with no reason shown.</para>
/// </summary>
public abstract record PopularAnswer
{
    private PopularAnswer() { }

    /// <summary>Their records, most-owned first.</summary>
    public sealed record Found(IReadOnlyList<PopularRelease> Releases) : PopularAnswer;

    /// <summary>Discogs works on this deployment, but this listener has not connected an account. This is
    /// the one answer worth printing. It is not a failure, and the reader can do something about
    /// it.</summary>
    public sealed record NotConnected : PopularAnswer;

    /// <summary>Nothing to show, and nothing the reader can do. Examples: no Discogs page for this artist,
    /// Discogs not set up on this deployment, a rate limit, or no network. The card stays hidden.</summary>
    public sealed record Nothing : PopularAnswer;

    public static readonly PopularAnswer NoAccount = new NotConnected();
    public static readonly PopularAnswer None = new Nothing();

    /// <summary>Records if there are any, and <see cref="None"/> rather than an empty card if there are
    /// not. So the one place that decides whether a card is worth showing is here.</summary>
    public static PopularAnswer Of(IReadOnlyList<PopularRelease> releases)
        => releases.Count > 0 ? new Found(releases) : None;
}

/// <summary>
/// Reads Discogs' list of an artist's releases.
///
/// <para><b>Sorted here, not by the API, which does not sort by popularity.</b> The artist-releases
/// endpoint sorts by year, title or format only. It has no popularity order. Every row does carry the
/// community's collection and wantlist counts, so the ordering is made here from those counts.</para>
///
/// <para><b>Narrowed to the records that are the artist's own.</b> The endpoint returns every credit an
/// artist has. That includes their own albums, compilations where they are one track, remixes they made for
/// other people, and sleeves they designed. Only role "Main" is the artist's own work. Masters are
/// preferred over bare releases, for the same reason the encyclopedia lookup prefers release groups.
/// "Rumours" is one album, not its forty pressings. An unfiltered list of a famous artist is mostly those
/// forty pressings.</para>
/// </summary>
public static class DiscogsPopular
{
    /// <summary>The records on ONE page of one artist's releases, ranked. This is for a caller that has
    /// exactly one page to read. Tests state their cases in this shape.</summary>
    public static IReadOnlyList<PopularRelease> Parse(string json, int limit = 5)
        => Rank(Read(json), limit);

    /// <summary>
    /// The artist's own records on one page of the endpoint's answer, in the order the page gives them.
    ///
    /// <para>Not ranked. Ranking is a separate step. An artist's records can be spread over several pages.
    /// Where a catalogue lists an artist under more than one Discogs number, they can also be spread over
    /// several artists. Ranking one page at a time would give "the most owned record on whichever page was
    /// read first". That is not a fact about the artist.</para>
    /// </summary>
    public static IReadOnlyList<PopularRelease> Read(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("releases", out var releases)
                || releases.ValueKind != JsonValueKind.Array)
                return [];

            var found = new List<PopularRelease>();

            foreach (var release in releases.EnumerateArray())
            {
                if (Text(release, "role") is not "Main")
                    continue;

                var title = Text(release, "title");
                if (string.IsNullOrWhiteSpace(title))
                    continue;

                var id = Number(release, "id");
                var isMaster = Text(release, "type") is "master";
                var (have, want) = Community(release);

                found.Add(new PopularRelease(
                    id,
                    title!,
                    (int)Number(release, "year"),
                    have,
                    want,
                    Text(release, "thumb"),
                    isMaster
                        ? $"https://www.discogs.com/master/{id}"
                        : $"https://www.discogs.com/release/{id}",
                    isMaster));
            }

            return found;
        }
        catch
        {
            return [];   // not the JSON we were promised
        }
    }

    /// <summary>
    /// How many pages the endpoint says it has, or 1 when it does not say.
    ///
    /// <para>A caller walks more than one page for this reason. The endpoint will not sort by popularity.
    /// Year, title and format are the whole of its <c>sort</c> options. So the counts must be read from the
    /// rows, and rows the reader never asked for cannot be counted. For an artist with more records than
    /// fit on one page, one page is a ranking of an arbitrary part of the catalogue.</para>
    /// </summary>
    public static int PageCount(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("pagination", out var pagination)
                   && Number(pagination, "pages") is > 0 and var pages
                ? (int)pages
                : 1;
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>
    /// Everything gathered, most-owned first, one entry per record.
    ///
    /// <para>The same record reaches this list twice when it was gathered twice. The walk overlaps where an
    /// API ignores a page number. An artist listed under two Discogs numbers has the records they share on
    /// both. Duplicates are removed by the Discogs page each row opens. That is the one thing that
    /// identifies a record across both kinds. An album and a pressing of it are different rows with
    /// different numbers.</para>
    /// </summary>
    public static IReadOnlyList<PopularRelease> Rank(IEnumerable<PopularRelease> gathered, int limit = 5)
    {
        var found = gathered
            .GroupBy(r => r.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // If the answer holds masters at all, the bare releases beside them are pressings of something
        // already listed, so they are dropped. If it holds none, which happens for an artist nobody has
        // grouped yet, the releases are the answer. Dropping them would empty the card.
        var kept = found.Any(r => r.IsMaster)
            ? found.Where(r => r.IsMaster).ToList()
            : found;

        return kept
            .OrderByDescending(r => r.Have)
            .ThenByDescending(r => r.Want)
            .Take(limit)
            .ToList();
    }

    /// <summary>How many have it and how many want it. Both are 0 when Discogs did not say, which it does
    /// not do on every row. 0 is the right value for the ordering in either case.</summary>
    private static (int Have, int Want) Community(JsonElement release)
    {
        if (!release.TryGetProperty("stats", out var stats)
            || !stats.TryGetProperty("community", out var community))
            return (0, 0);

        return ((int)Number(community, "in_collection"), (int)Number(community, "in_wantlist"));
    }

    private static string? Text(JsonElement e, string name)
        => e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long Number(JsonElement e, string name)
        => e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt64(out var number)
            ? number
            : 0;
}
