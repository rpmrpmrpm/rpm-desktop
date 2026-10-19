namespace RPM.Core.Ripping;

/// <summary>
/// How hard to try on a disc that will not read, and when to give up on a track.
///
/// A damaged disc fails in two ways, and only one of them looks like an error. The drive either returns a
/// read error, which is loud and can be counted, or it says nothing at all. In that case it keeps re-seeking
/// the same scratch, and the read never comes back. The second case hangs a rip forever. So a block that
/// takes too long counts as damage, just as an error does.
/// </summary>
public sealed record RipReadPolicy
{
    public static readonly RipReadPolicy Default = new();

    /// <summary>Re-reads of a block the drive refused, before the block is written off. A dirty disc often
    /// gives a block up on the second or third try.</summary>
    public int Retries { get; init; } = 2;

    /// <summary>How many blocks one track may lose before the track itself is given up. A few lost blocks are
    /// a click or two. Dozens of lost blocks make a track not worth keeping, and the rest of the disc is
    /// usually fine.</summary>
    public int MaxBadBlocks { get; init; } = 8;

    /// <summary>How long one block read may take before the drive is counted as stuck. A block is tens of
    /// kilobytes, so a few seconds is already generous. A drive that has stopped responding never finishes.</summary>
    public TimeSpan BlockTimeout { get; init; } = TimeSpan.FromSeconds(20);
}

/// <summary>
/// The running account of one track's read: the policy in force and the damage found so far. The reader
/// reports every block it could not read to this object. When the track's budget is used up, the report
/// throws, and the ripper skips the track instead of failing the whole rip.
///
/// There is one object per track. Nothing here is thread-safe, because one loop reads a track.
/// </summary>
public sealed class RipReadLog(RipReadPolicy? policy = null)
{
    public RipReadPolicy Policy { get; } = policy ?? RipReadPolicy.Default;

    /// <summary>Blocks the drive would not read. Each one's place in the track is filled with silence, so the
    /// track still runs to its proper length, and everything after the damage stays in time.</summary>
    public int BadBlocks { get; private set; }

    /// <summary>What the drive said about the last block it lost. This is used in the message for a skipped track.</summary>
    public string? LastError { get; private set; }

    /// <summary>Records a block that would not read after every retry.</summary>
    /// <exception cref="DiscTrackUnreadableException">The track's budget is used up. This track is not worth
    /// ripping, so the rip should move on to the next one.</exception>
    public void BlockFailed(string reason)
    {
        BadBlocks++;
        LastError = reason;

        if (BadBlocks > Policy.MaxBadBlocks)
            throw new DiscTrackUnreadableException($"{BadBlocks} unreadable blocks ({reason})");
    }
}

/// <summary>
/// One block read, with the policy's retries and patience applied. This is the shared part of what every
/// <see cref="IDiscReader"/> and the DVD sector stream do around their own platform read.
/// </summary>
public static class DiscBlockReader
{
    /// <summary>The block was written off after every retry. Fill its place with silence and carry on.</summary>
    public const int Unreadable = -1;

    /// <summary>
    /// Reads one block, retrying according to the policy. A read that does not come back in time counts as
    /// damage. It is not waited for.
    /// </summary>
    /// <param name="read">The platform read. Returns how many bytes (or sectors, in the caller's unit) it got.</param>
    /// <param name="what">The block, for the message, for example "sectors 1200..1226".</param>
    /// <returns>What <paramref name="read"/> returned, or <see cref="Unreadable"/> when the block was written
    /// off.</returns>
    /// <exception cref="DiscTrackUnreadableException">The track is used up: too many blocks were lost, or the
    /// drive stopped responding. In the second case <see cref="DiscTrackUnreadableException.Stalled"/> is set.
    /// The caller must treat its handle as finished, because the abandoned read still owns it.
    /// </exception>
    public static async Task<int> ReadAsync(Func<CancellationToken, Task<int>> read, RipReadLog log,
        string what, CancellationToken ct)
    {
        Exception? last = null;

        for (var attempt = 0; attempt <= log.Policy.Retries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await WithPatienceAsync(read, log.Policy.BlockTimeout, what, ct);
            }
            catch (DiscTrackUnreadableException)
            {
                // The drive went quiet. Retrying would hand the same stuck handle another read.
                throw;
            }
            catch (Exception ex) when (ex is IOException or DiscReadException)
            {
                last = ex;
            }
        }

        log.BlockFailed($"{what}: {last?.Message ?? "unreadable"}");
        return Unreadable;
    }

    /// <summary>Awaits the read, but only for so long. A read still running when the time is up is abandoned,
    /// not cancelled. A drive stuck in a kernel read does not respond to a cancel token. That is the reason
    /// this exists.</summary>
    private static async Task<int> WithPatienceAsync(Func<CancellationToken, Task<int>> read,
        TimeSpan timeout, string what, CancellationToken ct)
    {
        var work = read(ct);

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var finished = await Task.WhenAny(work, Task.Delay(timeout, timer.Token));
        timer.Cancel();   // Whichever side lost, let it go

        if (finished == work)
            return await work;

        ct.ThrowIfCancellationRequested();   // Stopped by hand, not stuck

        throw new DiscTrackUnreadableException(
            $"{what}: the drive stopped responding for {timeout.TotalSeconds:0}s", stalled: true);
    }
}
