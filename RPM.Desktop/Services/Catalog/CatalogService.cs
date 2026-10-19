using System.Net.Http;
using RPM.Core.Art;
using RPM.Core.Metadata.Providers;
using RPM.Core.Splitting;
using RPM.Core.Net;

namespace RPM.Desktop.Services.Catalog;

/// <summary>
/// The catalogues the app can look records up in, and the archives that know what their sleeves look like.
///
/// <para>This is the one place that lists the providers. The rest of the app asks for "a release search"
/// and does not name MusicBrainz. A provider that is both a metadata source and an art source, such as
/// Discogs, is added here once and used for both roles. Art is a provider too, so pictures come from the
/// same place as the other release data.</para>
///
/// <para>Every outside source is reached through the rpm API, with or without keys. The Cover Art Archive
/// needs no key, but it is still reached through the API. Do not build cover addresses in the app: the
/// addresses come from the API, which checks them. See <see cref="RpmReleaseArtProvider"/>. The server
/// caches each answer, so every user who searches for a release gets the same answer.</para>
/// </summary>
public sealed class CatalogService
{
    private readonly List<IMetadataProvider> _metadata;
    private readonly List<IReleaseArtProvider> _art;

    public CatalogService(DiscogsService discogs, IRpmMetadataApi metadataApi)
    {
        var discogsProvider = new DiscogsMetadataProvider(discogs);
        var rpmProvider = new RpmMetadataProvider(metadataApi);

        // rpm comes first. It answers for any release, but Discogs answers only for what its own catalogue
        // holds. Each needs something the user may not have: rpm needs a signed-in session, and Discogs
        // needs a connected account. A provider that is unavailable is skipped, so the search works with
        // whichever providers are present.
        _metadata = [rpmProvider, discogsProvider];

        // The rpm API uses its own release ids and Discogs uses its own. Discogs usually returns a cover
        // with the search results, so it is not asked for one separately.
        _art = [new RpmReleaseArtProvider(metadataApi), discogsProvider];

        Search = new ReleaseSearch(_metadata);
        SplitReleaseLookup = AlbumSplitAnalyzer.ReleaseLookupVia(metadataApi);
    }

    /// <summary>Searches every catalogue that can answer, and ranks what comes back.</summary>
    public ReleaseSearch Search { get; }

    /// <summary>Fetches a release's expected track list for the split aligner, through the rpm catalogue.
    /// Handed to <see cref="AlbumSplitAnalyzer.ReleaseLookup"/> by the split dialog.</summary>
    public Func<string, CancellationToken, Task<Asked<ReleaseInfo?>>> SplitReleaseLookup { get; }

    /// <summary>The archives to ask for a release's cover, in order.</summary>
    public IReadOnlyList<IReleaseArtProvider> ArtProviders => _art;

    /// <summary>
    /// The sleeve of a release the user has picked, downloaded and ready to use.
    ///
    /// <para>The image is fetched at the archive's full size, not at one of its smaller renders. The
    /// pictures in the search list are 96 to 250 px, sized for a row. This one is saved to the album folder
    /// and kept, so the largest size the archive has is wanted here. The copy that goes inside the track
    /// files is made from it here, once, instead of being fetched again.</para>
    ///
    /// <para>This is a separate request, not the thumbnail already on screen. It is made once, when the
    /// user chooses a pressing. A record has no other art to fall back on, so a sleeve that is not fetched
    /// here is a sleeve the album never gets.</para>
    ///
    /// <para>Null when nobody has a picture of this release. This is an ordinary result, not a
    /// failure.</para>
    /// </summary>
    public async Task<Sleeve?> SleeveAsync(ReleaseSearchResult release, CancellationToken ct = default)
    {
        foreach (var provider in _art.Where(a => a.Owns(release.Ref)))
        {
            // An archive with no scan, and one that did not answer, both fall through to the next archive.
            // After the last archive, the search's own thumbnail is used. It is a smaller picture of the
            // right sleeve. It does not mean that no bigger picture exists.
            try
            {
                if ((await provider.FrontCoverUrlAsync(release.Ref, IReleaseArtProvider.OriginalSize, ct))
                        .Answers(out var url, out _)
                    && url is { Length: > 0 }
                    && await DownloadAsync(url, ct) is { } scan)
                    return SleeveScaler.ToSleeve(scan);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                /* an archive with nothing to say is not the import failing */
            }
        }

        // No scan could be found. The search's picture is small, but a small sleeve is still the right
        // sleeve, and it is better than an album with no picture. Being small, it does not need a second
        // tier.
        return release.CoverUrl is { Length: > 0 } thumbnail && await DownloadAsync(thumbnail, ct) is { } small
            ? Sleeve.Of(small)
            : null;
    }

    /// <summary>How many of a release's other pictures are checked. A well documented pressing can have
    /// twenty, but the disc is rarely the twentieth: collectors photograph the sleeve first, then the
    /// record. Past this limit the chance of a find no longer justifies the requests.</summary>
    private const int MaxDiscProbes = 8;

    /// <summary>
    /// The disc art of a release the user has picked. It is cut out of the catalogue's own photographs and
    /// returned as the transparent PNG that the turntable draws.
    ///
    /// <para>The catalogues have no disc art. They have photographs that collectors took: the disc on a
    /// table, the two labels, the run-out groove. These are flat JPEGs with corners, and nothing says which
    /// photograph is which. So each one is fetched at thumbnail size and checked by looking at it. The
    /// roundest image wins, and only then is the full-size upload fetched and stamped (see
    /// <see cref="DiscStamp"/>).</para>
    ///
    /// <para>Null when none of them shows a disc. This is the normal answer for a release photographed with
    /// its sleeve only. It is never an error.</para>
    /// </summary>
    public async Task<byte[]?> DiscArtAsync(ReleaseSearchResult release, CancellationToken ct = default)
    {
        foreach (var provider in _art.Where(a => a.Owns(release.Ref)))
        {
            try
            {
                // With no picture, or no answer, the label is left unstamped. Neither result is saved.
                if (!(await provider.OtherImagesAsync(release.Ref, ct)).Answers(out var images, out _)
                    || images.Count == 0)
                    continue;

                ReleaseImage? best = null;
                byte[]? bestProbe = null;
                var bestScore = DiscStamp.MinScore;

                foreach (var image in images.Take(MaxDiscProbes))
                {
                    if (await DownloadAsync(image.ProbeUrl, ct) is not { } probe)
                        continue;
                    // The thumbnail decides. The upload is only fetched for the winner. A 150 px copy is
                    // small as a picture but large for the question asked of it, which is whether the
                    // object in it is round.
                    if (DiscStamp.Detect(probe.Bytes) is not { } found || found.Score <= bestScore)
                        continue;
                    bestScore = found.Score;
                    best = image;
                    bestProbe = probe.Bytes;
                }

                if (best is null)
                    continue;

                // The upload is used, with the thumbnail as the fallback. A picture good enough to pick is
                // worth stamping at whatever size is available, and a small label is better than no label.
                var full = await DownloadAsync(best.FullUrl, ct);
                if (DiscStamp.Stamp(full?.Bytes ?? bestProbe!, release.IsVinyl) is { } stamped)
                    return stamped;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                /* an archive with nothing to say is not the lookup failing */
            }
        }
        return null;
    }

    private static async Task<AlbumArt?> DownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(url, ct);

            // An address from the archives can go stale or fail, so a bad answer here means no picture. It
            // does not fail the import.
            if (!response.IsSuccessStatusCode)
                return null;

            var mime = response.Content.Headers.ContentType?.MediaType;
            if (mime is { Length: > 0 } && !mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return null;   // An error page is not a sleeve, and tagging it would be wrong.

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length > 0
                ? new AlbumArt(bytes, mime is { Length: > 0 } m ? m : "image/jpeg")
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    /// <summary>The archives need to know who is asking. The user agent must include a URL in parentheses,
    /// or .NET's own header parser rejects the string before it is sent.</summary>
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = Wire.Outside(Destination.Unaddressed, TimeSpan.FromSeconds(20));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("rpm/1.0 ( https://rpmrpmrpm.com )");
        return http;
    }
}
