using System.Text.Json;
using System.Text.Json.Serialization;
using RPM.Core.Net;
using RPM.Core.Radio;
using RPM.Desktop.Services.Platform;

namespace RPM.Desktop.Services.Catalog;

/// <summary>
/// The result of reading one broadcaster's website: the pictures it declares, and the line it uses to
/// describe itself. It is kept per host, so each site is read once rather than once per visit.
///
/// <para><b>Why this exists.</b> The logo search asks a station's own site when the directory has no usable
/// picture for it. For the Netherlands, that is 164 of 400 stations. Keeping each page in the ordinary web
/// cache is costly. A front page is 177 KB on average and up to 5.4 MB. In one measurement, 431 pages
/// took 74.4 MB, a third of a 256 MB store. The store was full and evicting entries within twelve hours.
/// The pages pushed out the album artwork, the station logos the search had just found, and each other. So
/// every visit to the radio pane walked every site again.</para>
///
/// <para>A page is only an intermediate step. The search needs a few addresses and a sentence, which is a
/// few hundred bytes. That is what is kept here. The page itself is now fetched without being stored (see
/// <c>RemoteImage.PageAsync</c> and <see cref="PageKeep"/>).</para>
///
/// <para>The row is kept in the same store as everything else fetched from outside. It uses the <c>radio</c>
/// category and the same thirty days, so clearing the cache also clears this, and nothing needs its
/// own housekeeping. The row has a made-up address, not the site's own. It is not what the site
/// answered. It is
/// what was concluded from the answer. A row that claimed to be the page would be served to anything that
/// later asked for the page.</para>
/// </summary>
public static partial class StationSites
{
    /// <summary>What one reading concluded. Both halves come from one fetch, so they are kept as one
    /// record. Asking for them separately would fetch the page twice.</summary>
    /// <param name="Icons">Every address worth trying, best first.</param>
    /// <param name="Says">The broadcaster's own line, or null where the page declared none.</param>
    public sealed record Found(IReadOnlyList<StationIcon> Icons, string? Says);

    /// <summary>
    /// What was concluded about this host before, or null if it has not been read, the row has expired, or
    /// what is stored cannot be read back.
    ///
    /// <para>A host that was read and gave NOTHING is also a stored answer: an empty icon list. Storing it
    /// is the point. These are the sites the search would otherwise read again on every visit for no gain.
    /// They are the common case among the stations that get this far.</para>
    /// </summary>
    public static Found? Read(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return null;

        try
        {
            if (AppCache.Store.Read(KeyFor(host)) is not { Body.Length: > 0 } row)
                return null;

            var kept = JsonSerializer.Deserialize(row.Body, StationSitesJson.Default.Kept);
            if (kept?.Icons is null)
                return null;

            return new Found(
                [.. kept.Icons.Select(i => new StationIcon(i.Url, (StationIconKind)i.Kind))],
                kept.Says);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;   // An unreadable row counts as a host that has not been read.
        }
    }

    /// <summary>
    /// Files what this host's site said. This overwrites the old row. A site that has been redesigned has a
    /// new answer. The row is keyed on the host, not on what was found.
    /// </summary>
    public static void Write(string host, Found found)
    {
        ArgumentNullException.ThrowIfNull(found);

        if (string.IsNullOrWhiteSpace(host))
            return;

        try
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(
                new Kept(
                    [.. found.Icons.Select(i => new KeptIcon(i.Url, (int)i.Kind))],
                    found.Says),
                StationSitesJson.Default.Kept);

            var now = DateTimeOffset.UtcNow.UtcTicks;

            AppCache.Store.Write(new WebCacheEntry
            {
                Key = KeyFor(host),
                Url = Address(host),
                Category = "radio",
                Status = 200,
                ContentType = "application/json",
                FetchedUtcTicks = now,
                ExpiresUtcTicks = now + Life.Ticks,
                Body = body,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Failing to save costs a re-read, which is what happens without this cache. It must not stop
            // the search that was about to succeed.
        }
    }

    /// <summary>Kept for as long as everything else fetched from outside is kept. A station's site changes
    /// its logo about as often as a record changes its sleeve.</summary>
    private static readonly TimeSpan Life = TimeSpan.FromDays(30);

    /// <summary>The address the row uses. It is not http, so nothing can mistake it for a fetch of the
    /// site. The host is included, so someone reading the cache can see what the row is about.</summary>
    private static string Address(string host) => $"rpm://station-site/{host}";

    /// <summary>The row's key. It is made the same way the http cache makes its own, so the two cannot
    /// collide. It is a hash of the address, which no real fetch will ever use.</summary>
    private static string KeyFor(string host)
        => Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Address(host.ToLowerInvariant()))));

    /// <summary>The stored shape. It is deliberately not <see cref="Found"/>. What is on disk outlives the
    /// code that wrote it, so the kind is stored as a number and the record is plain.</summary>
    private sealed record Kept(IReadOnlyList<KeptIcon> Icons, string? Says);

    private sealed record KeptIcon(string Url, int Kind);

    /// <summary>The stored shape. A published build is Native AOT, which serializes from this and never by
    /// reflection.</summary>
    [JsonSerializable(typeof(Kept))]
    private sealed partial class StationSitesJson : JsonSerializerContext;
}
