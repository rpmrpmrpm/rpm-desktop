using RPM.Core.Cloud;
using RPM.Fingerprint;

namespace RPM.Desktop.Services.Cloud;

/// <summary>
/// Where a track is read from when its bytes are not on this disk.
///
/// <para>
/// A presigned URL is not a download of anything. The same signature plays the same track for as long as it
/// is valid. So a signed URL is remembered, rather than paid for at every start, restart and seek. The
/// look-ahead signs the URLs of the next few tracks before they are needed. This makes starting a cloud
/// track cost the same as starting a file: a decoder opening, and nothing else.
/// </para>
///
/// <para>
/// The desktop always asks for the original, unlike the phone. It is on a fixed line, and this is the
/// machine the collection came from. There is no reason to give somebody a smaller encoding of their own
/// record. So the blob always exists, and nothing here waits on a transcode.
/// </para>
/// </summary>
public sealed class CloudStreamService
{
    private readonly RpmCloudClient _cloud;
    private readonly AuthService _auth;
    private readonly ILogger<CloudStreamService> _log;

    /// <summary>Signed URLs held in memory. The server signs for six hours, and they are kept for four.
    /// That margin means a URL is never handed to a decoder just before it expires.</summary>
    private readonly Dictionary<string, (string Url, DateTimeOffset Until)> _urls = new(StringComparer.Ordinal);

    private static readonly TimeSpan UrlLife = TimeSpan.FromHours(4);

    /// <summary>A ceiling on the number of URLs kept, so a machine left playing for a week does not collect
    /// one per track ever heard. Nothing clever decides which are dropped, because each one costs only one
    /// round trip to replace.</summary>
    private const int UrlsKept = 500;

    private readonly object _gate = new();

    public CloudStreamService(RpmCloudClient cloud, AuthService auth, ILogger<CloudStreamService> log)
    {
        _cloud = cloud;
        _auth = auth;
        _log = log;

        // Somebody else's music must not be left behind for the next person who signs in. A URL is this
        // account's right to read this account's records.
        _auth.Changed += () => { if (!_auth.IsLoggedIn) Forget(); };
    }

    public bool CanStream => _auth.IsLoggedIn;

    /// <summary>
    /// Says whose records are whose. This is handed in after construction, not taken in the constructor,
    /// because sharing is built on top of this class and cannot also sit underneath it. It is null in a
    /// build with no sharing, where every record belongs to this account.
    /// </summary>
    public Sharing.SharingService? Sharing { get; set; }

    /// <summary>
    /// Where to read a cloud track from: a presigned URL that supports range requests. ffmpeg opens it the
    /// same way it opens a file, so a streamed track seeks like a local one.
    ///
    /// <para>Null when the account cannot be asked, because the user is signed out or offline. The caller
    /// treats that the same as a missing file: this track cannot be played right now.</para>
    /// </summary>
    public async Task<string?> UrlAsync(string rpmId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rpmId) || !_auth.IsLoggedIn) return null;

        // A track id names a row in the storage of the account that uploaded it, so a guest's track id is
        // not an id to ask about here. See PeerTrust.OwnCloudId. A guest's cloud copy is signed under the
        // grant instead (SharingService.SharedCloudUrlAsync), and their record on their own disk plays from
        // their own computer. If guest ids were asked here, the look-ahead would ask this account's cloud
        // about eight tracks of somebody else's at every track change, and get a 404 every time.
        if (Sharing is { } sharing && sharing.OwnCloudId(rpmId) is null) return null;

        lock (_gate)
            if (_urls.TryGetValue(rpmId, out var known) && known.Until > DateTimeOffset.UtcNow)
                return known.Url;

        try
        {
            var (url, _) = await _cloud.GetStreamAsync(CloudShelf.Own, rpmId, AudioLadder.Original, ct);

            lock (_gate)
            {
                if (_urls.Count >= UrlsKept) _urls.Clear();
                _urls[rpmId] = (url, DateTimeOffset.UtcNow.Add(UrlLife));
            }

            return url;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "[Cloud] Could not sign a stream for {Id}", rpmId);
            return null;
        }
    }

    /// <summary>
    /// Signs the URLs for tracks that are about to be needed, so that the sound does not wait for a round
    /// trip when the playhead reaches them.
    ///
    /// <para>Nothing waits on this, and nothing fails because of it. A track whose URL was not warmed signs
    /// its own URL when it starts.</para>
    /// </summary>
    public async Task WarmAsync(IReadOnlyList<string> rpmIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rpmIds);
        if (!_auth.IsLoggedIn) return;

        foreach (var id in rpmIds)
        {
            if (ct.IsCancellationRequested) break;
            await UrlAsync(id, ct);
        }
    }

    private void Forget()
    {
        lock (_gate) _urls.Clear();
    }
}
