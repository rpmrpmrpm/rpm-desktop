namespace RPM.Core.Net;

/// <summary>
/// How long one kind of lookup is worth keeping, and under what name.
///
/// <para>These are the app's rules, not the services' rules. The catalogues rpm reads from (MusicBrainz,
/// Wikipedia, the radio directory, the artwork service) send cache headers that last minutes or hours,
/// because they are written for browsers and proxies that hold a page while a person reads it. The app
/// wants the opposite. An answer about a record released in 1973 is as true next month as it is today, and
/// asking again on every launch costs a rate-limited round trip in front of a pane that must open at once.
/// So the caller decides how fresh an answer must be, and the server's own headers are not used.</para>
/// </summary>
/// <param name="Category">Which cache this belongs to: "art", "radio", "facts". Kept on the row so a
/// setting can report what is held and clear one kind without clearing the rest.</param>
/// <param name="MaxAge">How long an answer is served without asking again.</param>
/// <param name="MissMaxAge">How long "there is none" is believed. Shorter than <paramref name="MaxAge"/> on
/// purpose: catalogues gain entries every day, so a 404 is a fact about today, while a record's credits are
/// a fact about 1973.</param>
public sealed record WebCachePolicy(string Category, TimeSpan MaxAge, TimeSpan MissMaxAge)
{
    /// <summary>
    /// Anything beyond the URL that makes this a different question, or null.
    ///
    /// <para>Use this for a URL whose answer has changed shape while the URL stayed the same. Without it,
    /// an old cached answer would be served until it expires. The artwork lookup is one case (see
    /// <c>ArtworkService.Answers</c>). A different value here is a different cache namespace. The next
    /// lookup asks the network, and the rows under the old value expire on their own.</para>
    /// </summary>
    public string? Vary { get; init; }

    /// <summary>The default policy: keep an answer for a month, and a missing answer for a week.</summary>
    public static WebCachePolicy For(string category) =>
        new(category, TimeSpan.FromDays(30), TimeSpan.FromDays(7));

    /// <summary>How long this particular answer is good for.</summary>
    public TimeSpan AgeFor(int status) => status is 404 or 410 ? MissMaxAge : MaxAge;
}

/// <summary>Where a response came from. Carried on the response so a diagnostic can tell a cold lookup from
/// a warm one without timing it.</summary>
public enum WebCacheSource
{
    /// <summary>Not cacheable, or cached and then fetched: it came off the network.</summary>
    Network,

    /// <summary>Served from the store without asking anyone.</summary>
    Hit,

    /// <summary>Stale, asked about, and the server said it had not changed.</summary>
    Revalidated,

    /// <summary>Stale, and the server could not be reached. The old answer is served instead of none at
    /// all.</summary>
    Stale,
}
