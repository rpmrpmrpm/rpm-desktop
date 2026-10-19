namespace RPM.Fingerprint.External;

/// <summary>Thrown when a process that is not the RPM API puts a request to a music catalogue.</summary>
public sealed class CatalogueReachedDirectlyException(Uri address)
    : InvalidOperationException(
        $"A catalogue was called directly: {address.Host}. Only the RPM API may reach a catalogue; "
        + "everything else asks the API, which holds the keys, the rate gate and the shared cache. "
        + "Add or use an endpoint on RPM.Lambda.Api instead of calling this host.")
{
    public Uri Address { get; } = address;
}

/// <summary>
/// Which process is allowed to reach a music catalogue, and the check that enforces it.
///
/// <para><b>Why this is a type and not only a rule in the documentation.</b> A rule that exists only on
/// paper is easy to break, and several paths did break it. The track-info pane made up to six MusicBrainz
/// calls per record from the listener's own IP address. The play counts made four more calls. A
/// reachability probe pinged the service from every machine in a loop. None of these looked like a
/// catalogue client, because each one built a URL by hand and handed it to a shared fetcher. So a review
/// had nothing to catch. A rule that can only be enforced by noticing is not enforced.</para>
///
/// <para><b>Denied is the default, and no head states otherwise.</b> The API says
/// <see cref="ThisProcessIsTheApi"/> once at startup. The desktop, the phone and every other head say
/// nothing at all, and inherit the refusal. Forgetting therefore stops a catalogue call rather than
/// permitting one, which is the direction that fails safely.</para>
///
/// <para><b>What is not a catalogue, and is deliberately allowed:</b> Wikipedia and Wikidata, because
/// which one to read is a question about the reader's own language. The Cover Art Archive and the
/// artwork CDNs, including fanart.tv's <c>assets.fanart.tv</c>, are also allowed, because they carry
/// image bytes rather than answers.</para>
///
/// <para>fanart.tv's API (<c>webservice.fanart.tv</c>) is a catalogue. It is asked on RPM's project key,
/// by the API only, and the user's personal key is added there.</para>
/// </summary>
public static class CatalogueReach
{
    /// <summary>
    /// The hosts only the API may put a request to. A suffix match, so "api.listenbrainz.org" and
    /// "labs.api.listenbrainz.org" are both covered by one entry and a new subdomain cannot slip past.
    /// </summary>
    private static readonly string[] Catalogues =
    [
        "musicbrainz.org",
        "listenbrainz.org",
        "acoustid.org",
        "audd.io",
        "theaudiodb.com",
        "api.discogs.com",
        "sonovault.now",
        "webservice.fanart.tv",
    ];

    private static bool _isApi;

    /// <summary>Said once, by the RPM API, at startup. It holds the keys, the rate gate and the shared
    /// cache, so it is the one process for which reaching a catalogue is the correct thing to do.</summary>
    public static void ThisProcessIsTheApi() => _isApi = true;

    /// <summary>
    /// The paths on a catalogue's own hosts that carry PICTURES rather than answers.
    ///
    /// <para>The rule above says artwork CDNs are outside it, because they carry image bytes rather than
    /// answers. A host match caught one anyway. TheAudioDB serves its artwork from <c>r2.theaudiodb.com</c>,
    /// an object store with no key and no rate gate. A suffix match on <c>theaudiodb.com</c> took it in
    /// with the API. So every artist photographed only by that source would be unreachable from the whole app,
    /// including the artists wall, the now-playing header and the track pane. They all fetch through the
    /// one guarded pipe.</para>
    ///
    /// <para>This is a path rather than a host. A catalogue that moves its images back onto its main host
    /// stays covered. An API that adds an <c>/images/</c> endpoint would be the one thing this lets
    /// through. That is bytes, which is what the exception is for.</para>
    /// </summary>
    private static readonly string[] ArtworkPaths = ["/images/"];

    /// <summary>Whether this address is a picture on one of those hosts rather than a question for
    /// one.</summary>
    public static bool IsArtwork(Uri address)
    {
        foreach (var path in ArtworkPaths)
        {
            if (address.AbsolutePath.StartsWith(path, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>True where a request to this address would be refused. Cheap enough to ask on every
    /// request and the only question the guard has.</summary>
    public static bool Refuses(Uri address) => !_isApi && IsCatalogue(address) && !IsArtwork(address);

    /// <summary>Throws where this process may not put the request. Called by the app's HTTP pipeline
    /// and by the MusicBrainz fetcher, so a hand-built URL is caught as surely as a client is.</summary>
    public static void Check(Uri address)
    {
        if (Refuses(address))
            throw new CatalogueReachedDirectlyException(address);
    }

    /// <summary>Whether the address belongs to one of the catalogues, whoever is asking.</summary>
    public static bool IsCatalogue(Uri address)
    {
        var host = address.Host;

        foreach (var catalogue in Catalogues)
        {
            if (host.Equals(catalogue, StringComparison.OrdinalIgnoreCase)
                || (host.Length > catalogue.Length
                    && host[^catalogue.Length..].Equals(catalogue, StringComparison.OrdinalIgnoreCase)
                    && host[^(catalogue.Length + 1)] == '.'))
                return true;
        }

        return false;
    }
}
