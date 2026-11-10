using RPM.Core.Sharing;

namespace RPM.Core.Nas;

/// <summary>
/// What the NAS holds, as far as this computer knows, and when it may be asked again. The uploader asks
/// <see cref="NeedsUpload"/> on every tick with what this computer has now; nothing here depends on an
/// event having arrived.
/// </summary>
public sealed class NasUploadState
{
    /// <summary>The first wait after a failed upload. It doubles with each failure.</summary>
    public static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(30);

    /// <summary>The longest wait after failed uploads.</summary>
    public static readonly TimeSpan LongestWait = TimeSpan.FromMinutes(5);

    /// <summary>What one upload gave a NAS: its catalogue versions and the trust list.</summary>
    public sealed record Snapshot(string NasDeviceId, string OwnerVersion, string GuestVersion, string Trust);

    private Snapshot? _last;
    private int _failures;
    private DateTime _notBefore = DateTime.MinValue;
    private readonly Dictionary<long, string> _pictures = [];

    /// <summary>The trust list as one comparable value.</summary>
    public static string TrustKey(IReadOnlyList<NasTrusted> trust)
    {
        ArgumentNullException.ThrowIfNull(trust);
        return string.Join(';', trust.OrderBy(t => t.PublicKey, StringComparer.Ordinal).Select(t => $"{t.PublicKey}:{(int)t.Kind}"));
    }

    /// <summary>Whether the NAS needs <paramref name="now"/>: it differs from what was last uploaded, and no
    /// failure asks to wait.</summary>
    public bool NeedsUpload(Snapshot now, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(now);
        return utcNow >= _notBefore && now != _last;
    }

    /// <summary>The NAS took <paramref name="sent"/>.</summary>
    public void Uploaded(Snapshot sent)
    {
        ArgumentNullException.ThrowIfNull(sent);
        if (_last?.NasDeviceId != sent.NasDeviceId)
            _pictures.Clear();
        _last = sent;
        _failures = 0;
        _notBefore = DateTime.MinValue;
    }

    /// <summary>An upload failed. Returns how long to wait before the next try.</summary>
    public TimeSpan Failed(DateTime utcNow)
    {
        _failures++;
        var seconds = Math.Min(LongestWait.TotalSeconds, FirstWait.TotalSeconds * Math.Pow(2, _failures - 1));
        var wait = TimeSpan.FromSeconds(seconds);
        _notBefore = utcNow + wait;
        return wait;
    }

    /// <summary>Whether the playlist picture with <paramref name="stamp"/> still has to go to the NAS last
    /// uploaded to.</summary>
    public bool PictureNeeded(long playlistId, string stamp) =>
        !(_pictures.TryGetValue(playlistId, out var sent) && sent == stamp);

    public void PictureSent(long playlistId, string stamp) => _pictures[playlistId] = stamp;
}
