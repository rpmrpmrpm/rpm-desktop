using RPM.Core.Models;

namespace RPM.Core.Ripping;

/// <summary>
/// The handle a running rip is steered by. It is held by whoever started the rip, not by the ripper.
///
/// There are three different things a user can mean by "make it stop". They are kept apart because each
/// one leaves the disc in a different state:
/// <list type="bullet">
/// <item>Cancel: the rip's own <see cref="CancellationToken"/>. It throws the whole staging folder away,
/// half album and all. This class does not handle it.</item>
/// <item><see cref="Stop"/>: end the rip where it stands and KEEP the tracks that already landed. Only the
/// track being read is abandoned, and its partly written file is deleted.</item>
/// <item><see cref="SkipTrack"/>: give up on the track being read and carry on with the next one. This is
/// the way out of a scratch that the drive would otherwise grind at forever.</item>
/// </list>
/// </summary>
public sealed class RipControl
{
    private readonly object _gate = new();

    /// <summary>Cancels the track being ripped, and nothing else. The ripper replaces it for each track.</summary>
    private CancellationTokenSource? _track;

    /// <summary>True once <see cref="Stop"/> has been asked for: the ripper starts no further track.</summary>
    public bool StopRequested { get; private set; }

    /// <summary>Ends the rip here, keeping what has already been ripped.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            StopRequested = true;
            CancelTrack(_track);
        }
    }

    /// <summary>Abandons the track being read and moves on to the next one.</summary>
    public void SkipTrack()
    {
        lock (_gate)
            CancelTrack(_track);
    }

    /// <summary>Opens the window in which a skip or a stop can abandon the track about to be read. The token
    /// fires for a skip AND for a cancel of the whole rip. The ripper tells them apart by checking the rip's
    /// own token.</summary>
    internal CancellationTokenSource BeginTrack(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate)
        {
            _track = cts;

            // A stop was asked for in the gap between two tracks. The ripper has already passed its own check,
            // so without this the disc would rip one more track than the user asked for.
            if (StopRequested)
                CancelTrack(cts);
        }
        return cts;
    }

    internal void EndTrack(CancellationTokenSource cts)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_track, cts))
                _track = null;
        }
        cts.Dispose();
    }

    /// <summary>Cancel does nothing on a source that the ripper has already disposed. A click that lands in
    /// the gap between two tracks is not an error.</summary>
    private static void CancelTrack(CancellationTokenSource? cts)
    {
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
    }
}

/// <summary>
/// Everything a rip reports through, and the two controls it is driven by. It is one object rather than
/// eight optional parameters. Both rippers take the same set, and the app passes the same instance to
/// whichever ripper the disc needs.
/// </summary>
public sealed class RipHooks
{
    /// <summary>Ticks as the rip moves, for a bar or a ring.</summary>
    public IProgress<RipProgress>? Progress { get; init; }

    /// <summary>Fires once for each track that has finished ripping and been tagged. Its file is on disk
    /// and can be played.</summary>
    public IProgress<TrackInfo>? TrackDone { get; init; }

    /// <summary>Fires once for each track the rip gave up on, by hand or by policy. There is no file to play.
    /// This lets the UI show the gap, rather than quietly renumbering the tracks around it.</summary>
    public IProgress<RipTrackOutcome>? TrackSkipped { get; init; }

    /// <summary>Stop and skip. Null when the caller has nothing to steer with, such as in tests or one-shot rips.</summary>
    public RipControl? Control { get; init; }

    /// <summary>How hard to try on a disc that will not read.</summary>
    public RipReadPolicy Policy { get; init; } = RipReadPolicy.Default;
}
