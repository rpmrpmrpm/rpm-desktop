namespace RPM.Core.Cloud;

/// <summary>Whether the cloud chip has anything to say.</summary>
public enum CloudBatchPhase
{
    Idle,

    /// <summary>Work is in progress.</summary>
    Running,

    /// <summary>Everything finished. The ring is full and the chip is about to go away.</summary>
    Finished,

    /// <summary>Stopped part-way because the user asked for it. It differs from
    /// <see cref="Finished"/> in the way that matters: the work is NOT done, so the ring stays where it
    /// was and the chip stays visible with a way to start again.</summary>
    Paused,
}

/// <summary>Which sentence the chip is left saying. The numbers come from the ledger. The words belong
/// to the interface, because they are translated.</summary>
public enum CloudSummary
{
    /// <summary>Nothing to say: no batch, or a batch that turned out to have no work in it.</summary>
    Silent,

    /// <summary>The batch is open and nothing has been taken up yet.</summary>
    Preparing,

    /// <summary>"11 of 60": the running count while the work goes on.</summary>
    Running,

    /// <summary>Finished, with some of it refused.</summary>
    Failed,

    /// <summary>Finished, and it freed disk space. This is the one outcome the user cannot see for
    /// themselves, because the tracks are still in the library and still playable.</summary>
    Freed,

    /// <summary>Finished, all of it done.</summary>
    Done,

    /// <summary>Stopped part-way, with the rest still queued: "12 of 200 done (paused)". It has its own
    /// sentence because <see cref="Done"/> over a queue that is still full would be wrong. If a pause
    /// ended the batch the same way a finished run does, the ring would fill and every row would read as
    /// done while most of the library had not been sent.
    /// </summary>
    Paused,
}

/// <summary>
/// The cloud chip's bookkeeping: which tracks a batch has touched, how far along it is, and when it
/// is over.
///
/// <para>
/// It is separate from the chip itself because none of it is drawing. The engine reports from three
/// upload workers at once and from menus that overlap them. Every rule here exists because one of
/// those paths broke the ring: a wave of twelve filling it and starting again, a removal ending a
/// batch while an upload was still running, a retry adding a second row for one track, and a pause
/// logging a failure that the user caused on purpose.
/// </para>
///
/// <para>Not thread-safe, on purpose: the chip marshals every report onto the one thread its list is
/// bound to, and a lock here would only hide a caller that did not do this.</para>
/// </summary>
public sealed class CloudJobLedger
{
    private readonly Dictionary<long, Entry> _byTrack = [];
    private readonly List<long> _order = [];

    private int _openBatches;
    private int _expected;

    private sealed class Entry
    {
        public CloudOp Op;
        public bool Finished;
        public bool Failed;
        public long Bytes;
    }

    public CloudBatchPhase Phase { get; private set; } = CloudBatchPhase.Idle;

    /// <summary>Tracks taken up so far, in the order they were reached.</summary>
    public IReadOnlyList<long> Tracks => _order;

    public int Total => _order.Count;
    public int Finished => _byTrack.Values.Count(e => e.Finished);
    public int Failed => _byTrack.Values.Count(e => e.Failed);

    /// <summary>What a batch that deleted local copies made room for. Only offloads count: an
    /// upload's bytes went up, they did not come off the disk.</summary>
    public long FreedBytes => _byTrack.Values
        .Where(e => e.Op == CloudOp.Offload && e is { Finished: true, Failed: false })
        .Sum(e => e.Bytes);

    /// <summary>
    /// How much of what was asked for is done, from 0 to 1, counted in tracks.
    ///
    /// <para>It is measured against everything the batch expects, not against the current wave. The
    /// engine takes up tracks twelve at a time, so a two-hundred-track album would otherwise fill the
    /// ring, declare itself done and start again, sixteen times.</para>
    /// </summary>
    public double Fraction { get; private set; }

    public CloudSummary Summary { get; private set; } = CloudSummary.Silent;

    /// <summary>The pair the running summary is written with: "11 of 60".</summary>
    public (int Finished, int Total) Progress => (Finished, Math.Max(_expected, _order.Count));

    /// <summary>
    /// Opens a batch, and says whether the display has to be emptied first.
    ///
    /// <para>A batch that has finished is never added to: the ring would read as half done as soon as
    /// new work arrived. New work starts a new list.</para>
    /// </summary>
    /// <returns>True when the caller must drop the rows it is showing.</returns>
    public bool StartBatch()
    {
        var cleared = false;
        if (Phase is CloudBatchPhase.Finished or CloudBatchPhase.Paused)
        {
            ClearCore();
            cleared = true;
        }

        _openBatches++;
        Phase = CloudBatchPhase.Running;
        if (Summary == CloudSummary.Silent)
            Summary = CloudSummary.Preparing;
        return cleared;
    }

    /// <summary>How many tracks the batch will touch in all: the ones taken up plus whatever is still
    /// queued behind them. It only grows: a later wave that reports a smaller queue means the queue is
    /// draining, not that the work is shrinking.</summary>
    public void Expect(int stillQueued)
    {
        _expected = Math.Max(_expected, _order.Count + Math.Max(0, stillQueued));
        Recount();
    }

    /// <summary>
    /// Takes a track up.
    /// </summary>
    /// <returns>True when this is a track the batch has not seen, and the caller adds a row. False when
    /// it is the same track again (a retry, or a removal right after an upload), which reuses its row
    /// and does not add a duplicate.</returns>
    public bool Begin(long trackId, CloudOp op)
    {
        // A track is being worked on, so the chip is up. The engine opens a batch before it touches
        // anything, but a path that forgets to must not fill a list that nobody can see.
        if (Phase != CloudBatchPhase.Running)
        {
            if (Phase is CloudBatchPhase.Finished or CloudBatchPhase.Paused) ClearCore();
            Phase = CloudBatchPhase.Running;
        }

        if (_byTrack.TryGetValue(trackId, out var existing))
        {
            existing.Finished = false;
            existing.Failed = false;
            existing.Op = op;
            Recount();
            return false;
        }

        _byTrack[trackId] = new Entry { Op = op };
        _order.Add(trackId);
        Recount();
        return true;
    }

    /// <summary>Finishes a track. Unknown tracks are ignored: the engine reports more than the chip was
    /// asked to watch.</summary>
    public void Complete(long trackId, long bytes = 0)
    {
        if (!_byTrack.TryGetValue(trackId, out var entry)) return;
        entry.Bytes = bytes;
        entry.Finished = true;
        entry.Failed = false;
        Recount();
    }

    /// <summary>Marks a track as refused. The row stays on the list, because a failure that disappears
    /// is a failure that the user never saw.</summary>
    public void Fail(long trackId)
    {
        if (!_byTrack.TryGetValue(trackId, out var entry)) return;
        entry.Finished = true;
        entry.Failed = true;
        Recount();
    }

    /// <summary>
    /// Drops a track that was neither done nor refused. A pause in the middle of the work puts the track
    /// back in the queue, and a log saying it failed would be wrong about the user's own button press.
    /// </summary>
    /// <returns>True when there was a row to drop.</returns>
    public bool Abandon(long trackId)
    {
        if (!_byTrack.Remove(trackId)) return false;
        _order.Remove(trackId);
        if (_order.Count == 0) ClearCore();
        Recount();
        return true;
    }

    /// <summary>
    /// Closes a batch. The last batch to close decides what the chip says. A batch opened by a removal
    /// can overlap the upload run, and the first of the two to end must not clear the chip while the
    /// other is still running.
    /// </summary>
    /// <returns>True when this closed the last open batch.</returns>
    public bool EndBatch()
    {
        if (_openBatches > 0) _openBatches--;
        if (_openBatches > 0) return false;

        if (_order.Count == 0)
        {
            ClearCore();   // a kick that found nothing due is never worth a chip
            return true;
        }

        Phase = CloudBatchPhase.Finished;
        Fraction = 1;
        Summary = Failed > 0 ? CloudSummary.Failed
                : FreedBytes > 0 ? CloudSummary.Freed
                : CloudSummary.Done;
        return true;
    }

    /// <summary>
    /// Closes a batch that was stopped part-way, because the user pressed pause.
    ///
    /// <para>It does everything <see cref="EndBatch"/> does about open batches, and nothing that
    /// <see cref="EndBatch"/> does about the ring. EndBatch fills the ring and calls the run Done, which
    /// is wrong after a pause: stopping a two-hundred-track upload after twelve tracks would leave a
    /// full ring, the sentence "12 tracks done." and a list in which every row reads as finished, while
    /// the other 188 tracks still sit untouched in the queue. The counts here are the true ones: what
    /// was actually finished, out of what the batch expected.</para>
    /// </summary>
    /// <returns>True when this closed the last open batch.</returns>
    public bool PauseBatch()
    {
        if (_openBatches > 0) _openBatches--;
        if (_openBatches > 0) return false;

        // Note what is NOT here: EndBatch's shortcut "an empty list is never worth a chip". An empty list
        // means something different on this path. A pause can land while three tracks are inside ffmpeg.
        // All three go back in the queue, and the list is empty again. Removing the chip then would leave
        // the engine in a state that only the user can leave, and would remove the only button that
        // leaves it. A paused chip stays visible whether or not anything finished first.
        Phase = CloudBatchPhase.Paused;
        Summary = CloudSummary.Paused;
        return true;   // Fraction stays exactly where the work got to
    }

    /// <summary>Clears the chip once its last message has been read. A paused batch can be put away the
    /// same way: the queue is in the database either way, and dismissing only means the user has
    /// finished reading. See <see cref="CloudBatchPhase.Paused"/>.</summary>
    /// <returns>True when there was something to clear.</returns>
    public bool Dismiss()
    {
        if (Phase is not (CloudBatchPhase.Finished or CloudBatchPhase.Paused)) return false;
        ClearCore();
        return true;
    }

    private void ClearCore()
    {
        _byTrack.Clear();
        _order.Clear();
        Phase = CloudBatchPhase.Idle;
        Fraction = 0;
        Summary = CloudSummary.Silent;
        _expected = 0;
    }

    /// <summary>
    /// Works out the ring and the line under it again.
    ///
    /// <para>It does NOT decide that the batch is over. When every row is done, it only means that no
    /// worker is in the middle of a file. Between two waves of an upload that is true for a moment, and
    /// a chip that treated it as the end would announce twelve tracks done, fade out, and start again
    /// on the thirteenth.</para>
    /// </summary>
    private void Recount()
    {
        if (Phase is CloudBatchPhase.Finished or CloudBatchPhase.Paused) return;

        var (finished, total) = Progress;
        Fraction = total == 0 ? 0 : Math.Min(1, (double)finished / total);
        if (total > 0)
            Summary = CloudSummary.Running;
    }
}
