using RPM.Core.Net;
using RPM.Core.Splitting;
using RPM.Fingerprint.Catalog;

namespace RPM.Core.Metadata.Providers;

/// <summary>
/// A search result with everything known about it: what the catalogue says, where its cover is, and, once
/// the lookup has been paid for, how well its track lengths match the audio in hand. It also holds the
/// listing that was fetched to find that out, so the caller does not fetch it a second time.
/// </summary>
/// <param name="Match">Null until the listing has been fetched and scored. Null does not mean the release
/// is bad. It means nobody has looked yet.</param>
/// <param name="Detail">The full listing fetched while scoring, when there was one. It is kept so that
/// adopting the release does not repeat the throttled lookup.</param>
public sealed record RankedRelease(
    ReleaseSearchResult Release, string? CoverUrl, ReleaseMatch? Match, ReleaseDetail? Detail = null)
{
    /// <summary>The position in the list. Once the match is known it leads, because it is the only value
    /// here that has checked the recording. Before that, the provider's own relevance leads, adjusted by
    /// the things that make a pressing likely.</summary>
    public double Rank => Match is { } m ? m.Score : Release.Relevance;
}

/// <summary>
/// Searches every catalogue at once and ranks the results against the recording.
///
/// <para>Ranking happens in two waves, by design. No provider returns track lengths from a search. Every
/// provider rate-limits its lookups. Scoring 25 results by their lengths would cost 25 throttled lookups,
/// which is about half a minute of waiting. So the first wave shows results at once, ordered by what came
/// without a lookup. The second wave scores only the shortlist, and re-orders the list as each answer
/// arrives.</para>
///
/// <para>The match score cannot do everything, and it is not asked to. Two pressings that share a master
/// have identical listings, so they will always tie. Country, year, format and catalogue number separate
/// them. These are shown on screen for the user to read.</para>
/// </summary>
public sealed class ReleaseSearch(IReadOnlyList<IMetadataProvider> providers)
{
    /// <summary>How many results are worth a throttled lookup each. Past the first few, the user will read
    /// the sleeve, not the score.</summary>
    public int ShortlistSize { get; init; } = 5;

    /// <summary>Every catalogue the app knows about, connected or not. The lookup window shows each one's
    /// progress, including "not connected" for a provider it cannot ask yet.</summary>
    public IReadOnlyList<IMetadataProvider> Providers => providers;

    /// <summary>Providers that can answer right now.</summary>
    public IReadOnlyList<IMetadataProvider> Available =>
        providers.Where(p => p.IsAvailable).ToList();

    /// <summary>
    /// One provider's answer: its releases, or the fact that it did not answer.
    ///
    /// <para>The first wave of a search is one call per available provider, run side by side. One catalogue
    /// failing does not lose the other answers, and the window can show each catalogue's progress as it
    /// arrives. A catalogue that is down is reported as unanswered. An empty list means the catalogue
    /// answered and has no matches, so the window does not confuse the two.</para>
    ///
    /// <para>A provider that throws is unanswered, the same as one that says so. A cancellation the caller
    /// asked for is passed on.</para>
    /// </summary>
    public async Task<Asked<IReadOnlyList<ReleaseSearchResult>>> SearchOneAsync(
        IMetadataProvider provider, ReleaseQuery query, CancellationToken ct = default)
    {
        try
        {
            return await provider.SearchAsync(query, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Asked.Failed<IReadOnlyList<ReleaseSearchResult>>($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The free ordering: what the provider thought of the query, then the things that make a pressing
    /// plausible for what is being imported. This is public so that a caller which merges the providers'
    /// answers itself, for per-source progress, can order them the same way this class does.
    /// </summary>
    public static IReadOnlyList<ReleaseSearchResult> Order(
        IReadOnlyList<ReleaseSearchResult> results, ReleaseQuery query) =>
        results
            // Providers are asked to filter by medium themselves. This only matters for a provider that
            // could not, or that widened the search because it found none in that medium.
            .OrderByDescending(r => query.Media.Matches(r.Format))
            .ThenByDescending(r => r.Relevance)
            // a listing with no tracks says nothing
            .ThenByDescending(r => r.TrackCount > 0)
            // the first pressing over the reissues
            .ThenBy(r => r.Year == 0 ? uint.MaxValue : r.Year)
            .ToList();

    /// <summary>
    /// Second wave: fetch the listings of the top <see cref="ShortlistSize"/> results and score them
    /// against <paramref name="detectedLengths"/>. Each result is reported as it arrives, so the list can
    /// re-order while the user is looking at it, instead of making them wait for all of it.
    /// </summary>
    /// <param name="detectedLengths">The detected tracks' durations, in order.</param>
    /// <param name="onRanked">Called for each release as its score arrives. It runs on whatever thread the
    /// lookup completed on.</param>
    public async Task<IReadOnlyList<RankedRelease>> RankAsync(
        IReadOnlyList<ReleaseSearchResult> results,
        IReadOnlyList<double> detectedLengths,
        IReadOnlyList<IReleaseArtProvider> artProviders,
        Action<RankedRelease>? onRanked = null,
        CancellationToken ct = default)
    {
        var ranked = new List<RankedRelease>(results.Count);

        for (int i = 0; i < results.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var result = results[i];
            string? cover = await CoverUrlAsync(result, artProviders, ct);

            // Only the shortlist is worth a throttled lookup. The rest keep the free ordering.
            ReleaseMatch? match = null;
            ReleaseDetail? detail = null;
            if (i < ShortlistSize && detectedLengths.Count > 0)
                (match, detail) = await MatchAsync(result, detectedLengths, ct);

            var entry = new RankedRelease(result, cover, match, detail);
            ranked.Add(entry);
            onRanked?.Invoke(entry);
        }

        return ranked.OrderByDescending(r => r.Rank).ToList();
    }

    private async Task<(ReleaseMatch?, ReleaseDetail?)> MatchAsync(
        ReleaseSearchResult result, IReadOnlyList<double> detectedLengths, CancellationToken ct)
    {
        var provider = providers.FirstOrDefault(p => p.Id == result.Ref.Provider);
        if (provider is null)
            return (null, null);

        try
        {
            // Not answered: the release is unscored, and no listing is kept. So picking this row asks for
            // its listing again, instead of treating a failed request as "no listing".
            if (!(await provider.GetReleaseAsync(result.Ref.Id, ct)).Answers(out var detail, out _))
                return (null, null);

            if (detail is null || detail.Tracks.Count == 0)
                return (null, detail);

            // The recording is usually the whole release. On a multi-disc set it may be a single disc, the
            // same "album in one file is one disc" case the splitter handles. Score the whole listing and
            // each disc, and keep whichever fits the audio best. That way a double album still appears when
            // only one of its discs is in hand.
            var match = ReleaseMatcher.Measure(
                detail.Tracks.Select(t => t.LengthSeconds).ToList(), detectedLengths);
            if (detail.DiscCount > 1)
            {
                foreach (var disc in detail.Tracks.Where(t => t.Disc > 0).GroupBy(t => t.Disc))
                {
                    var discMatch = ReleaseMatcher.Measure(
                        disc.Select(t => t.LengthSeconds).ToList(), detectedLengths);
                    if (discMatch.Score > match.Score)
                        match = discMatch;
                }
            }
            return (match, detail);
        }
        // No listing means unscored, not a wrong match. A cancellation is not caught here, so the caller
        // still sees it.
        catch (Exception ex) when (ex is not OperationCanceledException) { return (null, null); }
    }

    /// <summary>The cover the provider supplied, or else the cover from the archive that owns this
    /// release.</summary>
    private static async Task<string?> CoverUrlAsync(
        ReleaseSearchResult result, IReadOnlyList<IReleaseArtProvider> artProviders, CancellationToken ct)
    {
        if (result.CoverUrl is { Length: > 0 })
            return result.CoverUrl;   // Discogs returns one with the search, so nothing more to ask

        foreach (var art in artProviders.Where(a => a.Owns(result.Ref)))
        {
            // A row without a picture looks the same whether the archive has none or did not answer.
            // Nothing is recorded either way, so both cases are handled the same here.
            try
            {
                if ((await art.FrontCoverUrlAsync(result.Ref, 250, ct)).Answers(out var url, out _)
                    && url is { Length: > 0 })
                    return url;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                /* the next archive, or no picture */
            }
        }
        return null;
    }
}
