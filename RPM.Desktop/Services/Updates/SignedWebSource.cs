using System.Text;

using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace RPM.Desktop.Services.Updates;

/// <summary>
/// Velopack's web source, reading the signed feed in place of the plain one. See <see
/// cref="FeedSignature"/>.
///
/// <para>A feed that does not verify, or that names a package without a SHA-256, is refused with an
/// exception. <see cref="AppUpdates"/> reports that as a feed that could not be read. Packages are
/// downloaded the way Velopack's own source downloads them. Velopack then checks each one's size and
/// SHA-256 against the verified feed.</para>
///
/// <para>Older copies of the app read <c>releases.&lt;channel&gt;.json</c>, which has no signature. That
/// file is still published for them, and it holds the same feed.</para>
/// </summary>
/// <param name="baseUrl">The channel's folder on the update host, ending in a slash.</param>
/// <param name="publicKey">The key that the feed must be signed with: <see cref="FeedSignature.PublicKey"/>
/// in the app, and a test key in the tests.</param>
/// <param name="downloader">What fetches files. Null means Velopack's own.</param>
internal sealed class SignedWebSource(string baseUrl, byte[] publicKey, IFileDownloader? downloader)
    : SimpleWebSource(baseUrl, downloader)
{
    public override async Task<VelopackAssetFeed> GetReleaseFeed(
        IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null,
        VelopackAsset? latestLocalRelease = null)
    {
        var url = new Uri(BaseUri, FeedSignature.FileName(channel)).ToString();
        logger.Info($"Downloading the signed feed from '{url}'.");

        var signed = await Downloader.DownloadString(url, null, Timeout).ConfigureAwait(false);
        var feed = VelopackAssetFeed.FromJson(Encoding.UTF8.GetString(FeedSignature.Open(signed, channel, publicKey)));

        // Velopack falls back to SHA-1 for an entry that has no SHA-256. Every feed that the release writes
        // has SHA-256. So an entry without one is refused, rather than checked the weaker way.
        foreach (var asset in feed.Assets)
        {
            if (asset.SHA256 is not { Length: 64 })
                throw new InvalidDataException($"{asset.FileName} has no SHA-256 in the feed.");
        }

        return feed;
    }
}
