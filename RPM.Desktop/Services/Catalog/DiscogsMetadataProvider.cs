using RPM.Core.Metadata.Providers;
using RPM.Core.Net;

namespace RPM.Desktop.Services.Catalog;

/// <summary>
/// Discogs, as a catalogue and as a source of sleeves.
///
/// <para>It is both, because its search returns a cover with every result. MusicBrainz says nothing about
/// art, and the Cover Art Archive has to be asked separately. This is why art is a provider in its own
/// right, and not a service called by whichever part first needs a picture.</para>
///
/// <para>Discogs is worth the extra code. It is a <i>collector's</i> database, built by the people who own
/// the records, so an obscure pressing that MusicBrainz does not list is often there. This class only
/// connects the parts. <see cref="DiscogsService"/> does the fetching, through the rpm API and never
/// straight to Discogs. <see cref="DiscogsJson"/> does the reading, and it can be tested without an
/// account.</para>
/// </summary>
public sealed class DiscogsMetadataProvider(DiscogsService discogs) : IMetadataProvider, IReleaseArtProvider
{
    public const string ProviderId = DiscogsJson.ProviderId;

    public string Id => ProviderId;

    public string DisplayName => "Discogs";

    /// <summary>True once the user has connected their account. Not being connected is a normal state, not
    /// an error: the search asks whichever provider can answer, and skips the rest.</summary>
    public bool IsAvailable => discogs.MayAnswer;

    /// <summary>True. Discogs' general search matches catalogue numbers and barcodes as well as titles, so
    /// a code typed into the one search box finds the pressing directly.</summary>
    public bool SupportsCatalogNumberSearch => true;

    public bool Owns(ReleaseRef release) => string.Equals(release.Provider, ProviderId, StringComparison.Ordinal);

    /// <summary>Null when Discogs was not in a position to be asked. The provider is normally skipped
    /// before it gets here, because <see cref="IsAvailable"/> is read first. But sign-out can happen
    /// between that read and this call. The other catalogue's results still stand. This result says the
    /// search did not run, and never that nothing was found.</summary>
    public Task<Asked<IReadOnlyList<ReleaseSearchResult>>> SearchAsync(
        ReleaseQuery query, CancellationToken ct = default) =>
        ReadAsync(
            () => discogs.SearchAsync(query.Album, query.Artist, query.Media, query.Limit,
                query.CatalogNumber, query.Country, query.Year, query.Barcode, ct),
            json => DiscogsJson.ParseSearch(json, query.Limit),
            ct);

    public Task<Asked<ReleaseDetail?>> GetReleaseAsync(string releaseId, CancellationToken ct = default) =>
        ReadAsync<ReleaseDetail?>(
            () => discogs.ReleaseAsync(releaseId, ct),
            json => DiscogsJson.ParseRelease(json, releaseId),
            ct);

    /// <summary>Called when a result arrived without a cover. For Discogs this means the search had none to
    /// give. It is called again, at a real size, once a release has been chosen. The search returns a 150
    /// px thumbnail, which is not a sleeve.</summary>
    public Task<Asked<string?>> FrontCoverUrlAsync(
        ReleaseRef release, int maxSize = 250, CancellationToken ct = default) =>
        Owns(release)
            ? ReadAsync(() => discogs.ReleaseAsync(release.Id, ct), json => DiscogsJson.FrontCoverUrl(json, maxSize), ct)
            : Task.FromResult(Asked.Answer<string?>(null));

    /// <summary>The release's other photographs: the disc, the labels and the run-out. They are read from
    /// the release document, the same one the cover comes from. This is asked only once someone has chosen
    /// a pressing, the one time a second fetch of the document is worth making.</summary>
    public Task<Asked<IReadOnlyList<ReleaseImage>>> OtherImagesAsync(
        ReleaseRef release, CancellationToken ct = default) =>
        Owns(release)
            ? ReadAsync(() => discogs.ReleaseAsync(release.Id, ct), DiscogsJson.OtherImages, ct)
            : Task.FromResult(Asked.Answer<IReadOnlyList<ReleaseImage>>([]));

    /// <summary>
    /// One read of the Discogs proxy, and its parse. Every way of not getting an answer is treated as a
    /// failure: Discogs not in a position to be asked (<see cref="DiscogsService"/> returns null for that),
    /// Discogs refusing (it throws), no network, and a body that cannot be read.
    /// </summary>
    private static async Task<Asked<T>> ReadAsync<T>(
        Func<Task<string?>> fetch, Func<string, T> parse, CancellationToken ct)
    {
        try
        {
            return await fetch() is { } json
                ? Asked.Answer(parse(json))
                : Asked.Failed<T>("Discogs could not be asked");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Asked.Failed<T>($"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
