using RPM.Core.Net;

namespace RPM.Core.Metadata.Providers;

/// <summary>
/// What a release looks like, according to the rpm API.
///
/// <para>Keyed by <i>release</i>, not by name. That distinction matters here. A name gets one picture
/// for "Fleetwood Mac, Rumours", but a release gets the sleeve of one particular pressing, such as the
/// 1977 UK cut or the 2013 reissue. Choosing between pressings is what a search result is for.</para>
///
/// <para>Requests go through the rpm API, like every other outside source the app uses. The Cover Art
/// Archive needs no key, and a cover's address there follows from the release id. So the app could
/// build the URL itself. But an address that nobody has checked cannot be told apart from a real cover
/// until it fails to load. That is too late for code that needs to know whether a release has art. The
/// server checks each address, caches the answer for everyone, and returns an address or nothing.</para>
///
/// <para>This is separate from the name-keyed artwork lookup (TheAudioDB, then fanart.tv). That lookup
/// answers a different question: what should be on screen while this album plays.</para>
/// </summary>
public sealed class RpmReleaseArtProvider(IRpmMetadataApi api) : IReleaseArtProvider
{
    public const string ProviderId = "rpm-art";

    public string Id => ProviderId;

    /// <summary>Release ids issued by the rpm catalogue, and nothing else. Discogs releases are handled
    /// by their own provider.</summary>
    public bool Owns(ReleaseRef release) =>
        string.Equals(release.Provider, RpmMetadataProvider.ProviderId, StringComparison.Ordinal);

    public async Task<Asked<string?>> FrontCoverUrlAsync(
        ReleaseRef release, int maxSize = 250, CancellationToken ct = default)
        => Owns(release) && !string.IsNullOrWhiteSpace(release.Id)
            ? await api.GetCoverUrlAsync(release.Id, maxSize, ct)
            : Asked.Answer<string?>(null);
}
