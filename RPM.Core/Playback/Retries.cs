namespace RPM.Core.Playback;

/// <summary>
/// How many goes one track gets before the player moves on.
///
/// <para>
/// A track that stopped rather than ended is opened again. This turns a stream that drops in the middle of
/// a track into a short pause instead of a skip. Without a limit the retries would loop forever: a file
/// that is shorter than its tag says stops at the same place every time, and the album would stall there
/// with no message. That is worse than the skip.
/// </para>
///
/// <para>
/// The count is kept per track and reset when a different track starts. A run of pauses on one album does
/// not use up a budget for the whole session.
/// </para>
/// </summary>
public sealed class Retries
{
    /// <summary>Three goes. Enough for a connection that drops and comes back, and few enough that
    /// a track which cannot play does not hold the queue until the listener presses stop.</summary>
    public const int Most = 3;

    private readonly int _most;
    private string? _track;
    private int _used;

    public Retries(int most = Most) => _most = Math.Max(1, most);

    /// <summary>How many goes this track has had.</summary>
    public int Used(string rpmId) => Same(rpmId) ? _used : 0;

    /// <summary>
    /// Whether <paramref name="rpmId"/> may be opened again, counting this go against it.
    /// </summary>
    public bool MayRetry(string rpmId)
    {
        ArgumentException.ThrowIfNullOrEmpty(rpmId);

        if (!Same(rpmId))
        {
            _track = rpmId;
            _used = 0;
        }

        if (_used >= _most) return false;

        _used++;
        return true;
    }

    /// <summary>A track that plays normally has recovered from its earlier trouble. Called when a track
    /// starts normally, so a later stumble on the same track gets the full number of goes again.</summary>
    public void Forget()
    {
        _track = null;
        _used = 0;
    }

    private bool Same(string rpmId) => string.Equals(_track, rpmId, StringComparison.Ordinal);
}
