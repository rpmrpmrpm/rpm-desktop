using System.Security.Cryptography;
using System.Text;

namespace RPM.Core.Net;

/// <summary>
/// The small vocabulary the cache and its callers share: how a request asks to be cached, how a response
/// says where it came from, and what a key is.
/// </summary>
public static class WebCache
{
    /// <summary>Attached to a request to cache it, or to cache it differently from the client's default.
    /// See <see cref="Cache"/>.</summary>
    public static readonly HttpRequestOptionsKey<WebCachePolicy> PolicyOption = new("rpm.webcache.policy");

    /// <summary>Attached to a request to skip the cache and go to the network. It is used by the refresh
    /// action, and for the retry of a body too large to hold in memory.</summary>
    public static readonly HttpRequestOptionsKey<bool> BypassOption = new("rpm.webcache.bypass");

    /// <summary>Where the response came from, once it has been through <see cref="HttpCacheHandler"/>.
    /// Absent on a client with no cache in its pipeline.</summary>
    public static readonly HttpRequestOptionsKey<WebCacheSource> SourceOption = new("rpm.webcache.source");

    /// <summary>The same value, sent as a response header. It is set only when the answer did not come from
    /// the network. It is for callers that cannot reference this assembly (RPM.Fingerprint is below it),
    /// and for reading traffic captured by a proxy.</summary>
    public const string SourceHeader = "X-RPM-Cache";

    /// <summary>Cache this one under this policy.</summary>
    public static HttpRequestMessage Cache(this HttpRequestMessage request, WebCachePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(policy);
        request.Options.Set(PolicyOption, policy);
        return request;
    }

    /// <summary>Ask the server, whatever is remembered. The answer still replaces what was there, so
    /// browsing on from here is fast again.</summary>
    public static HttpRequestMessage Refresh(this HttpRequestMessage request, bool bypass = true)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Options.Set(BypassOption, bypass);
        return request;
    }

    /// <summary>Where this response came from. <see cref="WebCacheSource.Network"/> when nothing in the
    /// pipeline was caching it.</summary>
    public static WebCacheSource CacheSource(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.RequestMessage is { } request
            && request.Options.TryGetValue(SourceOption, out var source)
                ? source
                : WebCacheSource.Network;
    }

    /// <summary>
    /// What a row is filed under: the method, the URL, and whatever else makes this a different question
    /// (see <see cref="WebCachePolicy.Vary"/>).
    ///
    /// <para>The key is a hash, not the whole text, because a URL can be too long for a comfortable primary
    /// key. The hash is also a fixed, filesystem-safe name for a body stored on disk. The readable URL is
    /// kept in its own column, for people reading the table.</para>
    /// </summary>
    public static string KeyFor(HttpRequestMessage request, WebCachePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(policy);

        var material = $"{request.Method.Method}\n{request.RequestUri}\n{policy.Vary}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    /// <summary>
    /// A fresh message carrying the same GET.
    ///
    /// <para>The cache sends a copy rather than the caller's own request, and keeps the original intact.
    /// Two things need that: a body too large to hold is fetched again with the cache out of the way, and a
    /// request already handed to the pipeline cannot be handed to it twice.</para>
    /// </summary>
    public static HttpRequestMessage CloneGet(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var copy = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
        };

        foreach (var header in request.Headers)
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);

        foreach (var option in (IDictionary<string, object?>)request.Options)
            ((IDictionary<string, object?>)copy.Options)[option.Key] = option.Value;

        return copy;
    }
}
