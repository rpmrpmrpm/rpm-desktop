using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using RPM.Core.Cloud;
using RPM.Desktop.Strings;
using Microsoft.UI.Dispatching;

namespace RPM.Desktop.Services.Cloud;

/// <summary>The stage a track is at on its journey. Each stage names what the machine is actually doing. A
/// label such as "syncing" for four minutes says nothing about whether the fan is spinning up for a
/// transcode, or whether the upload is saturated.</summary>
public enum CloudStage
{
    Waiting,
    Transcoding,
    Uploading,
    Removing,
    Restoring,

    /// <summary>Checks with the store that it really holds this recording, before the row is written down
    /// as backed up. This has its own stage because it is the step that makes the claim true.</summary>
    Verifying,

    Done,
    Failed,
}

/// <summary>Whether the cloud chip has anything to say.</summary>
public enum CloudJobState
{
    Idle,

    /// <summary>Work in flight.</summary>
    Running,

    /// <summary>Everything finished. The ring is full, and the chip is on its way out. See
    /// <see cref="CloudJobService.Dismiss"/>.</summary>
    Finished,

    /// <summary>Stopped part-way by the user. The chip stays up, because there is a queue behind this. The
    /// button on the chip is the way back to it.</summary>
    Paused,
}

/// <summary>
/// One track's row in the cloud chip's flyout: what it is, what is being done to it, and how far it has
/// got. The row is mutable and observable. It is updated in place several times a second while the chip is
/// on screen. Replacing the row would restart its bar's animation on every tick.
/// </summary>
[RpmBindable]
public sealed partial class CloudJobRow : INotifyPropertyChanged
{
    private CloudStage _stage;
    private double _fraction;

    public CloudJobRow(long trackId, CloudOp op, string title, string? subtitle)
    {
        TrackId = trackId;
        Op = op;
        Title = title;
        Subtitle = subtitle ?? "";
    }

    public long TrackId { get; }
    public CloudOp Op { get; }
    public string Title { get; }
    public string Subtitle { get; }

    /// <summary>The size of the canonical copy, once there is one. It is the log's record of the
    /// upload.</summary>
    public long Bytes { get; set; }

    public CloudStage Stage
    {
        get => _stage;
        set
        {
            if (_stage == value)
                return;
            _stage = value;
            Raise(nameof(Stage));
            Raise(nameof(StageText));
        }
    }

    /// <summary>0..1 for this track alone.</summary>
    public double Fraction
    {
        get => _fraction;
        set
        {
            if (Math.Abs(_fraction - value) < 0.001)
                return;
            _fraction = value;
            Raise(nameof(Fraction));
        }
    }

    /// <summary>True while something is being done to the track: it is not waiting for its turn, and it
    /// is not over. Only these rows are listed in the flyout.</summary>
    public bool IsWorking => Stage is not (CloudStage.Waiting or CloudStage.Done or CloudStage.Failed);

    /// <summary>Up, down or gone. It is shown as an icon rather than a word, so the text beside it is free
    /// to show the stage, which is the part that changes.</summary>
    public string OpGlyph => Op switch
    {
        CloudOp.Remove => "\uf1f8",    // bin
        CloudOp.Restore => "\uf063",   // arrow down
        CloudOp.Offload => "\uf0c2",   // cloud: the copy that remains
        _ => "\uf062",                 // arrow up
    };

    /// <summary>What is being done to the track now. The flyout lists only rows that are
    /// <see cref="IsWorking"/>, so the other stages have no words here. A refused track is counted on the
    /// line above the list, and its reason is on the cloud settings page.</summary>
    public string StageText => Stage switch
    {
        CloudStage.Transcoding => Loc.Get("CloudChip_StageTranscoding"),
        CloudStage.Uploading => Loc.Get("CloudChip_StageUploading"),
        CloudStage.Removing => Loc.Get("CloudChip_StageRemoving"),
        CloudStage.Restoring => Loc.Get("CloudChip_StageRestoring"),
        CloudStage.Verifying => Loc.Get("CloudChip_StageVerifying"),
        _ => "",
    };

    /// <summary>Marks the track as refused. The reason is not kept on the row: the log book has it, and
    /// the cloud settings page shows it.</summary>
    internal void Fail()
    {
        Fraction = 1;
        Stage = CloudStage.Failed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// The cloud's work, shown as something to watch.
///
/// <para>This is modelled on <see cref="RipJobService"/>, for the same reason: the work outlives the window
/// that started it. A user who picks "add album to cloud" from a right-click menu has no window at all.
/// Sixteen tracks may be transcoded and sent, with nothing on screen but a badge that changes minutes
/// later. This makes that work visible: a ring on the deck, and a flyout with the count, a bar for the
/// whole run, and the tracks that are being worked on now.</para>
///
/// <para>It owns none of the work. <see cref="CloudSyncService"/> does the uploading and the deleting, and
/// reports through here, so the chip and the engine cannot disagree. Every finished row is also written to
/// the mirror's log book, which is what the cloud settings page reads.</para>
/// </summary>
public sealed class CloudJobService
{
    private readonly LibraryService _library;
    private readonly DispatcherQueue _dispatcher;

    /// <summary>Rows by track, so a report from one of the three upload workers finds its row without a
    /// scan. It holds every track the batch has touched, not only the ones in <see cref="Working"/>,
    /// because a track that is taken up again keeps its row.</summary>
    private readonly Dictionary<long, CloudJobRow> _byTrack = [];

    /// <summary>Which tracks a batch has touched, how far in it is, and when it is over. This is everything
    /// about the batch that is not drawing. See <see cref="CloudJobLedger"/>. Its rules exist because three
    /// report paths overlap.</summary>
    private readonly CloudJobLedger _ledger = new();

    /// <summary>The operation of the batch that is preparing. It chooses the sentence shown before the first
    /// track is taken up.</summary>
    private CloudOp _preparing = CloudOp.Upload;

    public CloudJobService(LibraryService library, DispatcherQueue dispatcher)
    {
        _library = library;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// The tracks that are being worked on now, in the order they started. A track that waits for its turn,
    /// or is over, is not here. The count line says how many tracks are done, so a list of finished
    /// tracks would only repeat it, one row at a time.
    /// </summary>
    public ObservableCollection<CloudJobRow> Working { get; } = [];

    public CloudJobState State => _ledger.Phase switch
    {
        CloudBatchPhase.Running => CloudJobState.Running,
        CloudBatchPhase.Finished => CloudJobState.Finished,
        CloudBatchPhase.Paused => CloudJobState.Paused,
        _ => CloudJobState.Idle,
    };

    /// <summary>0..1 across the whole batch, counted in tracks. A refused track is over, so it counts. This
    /// is the ring on the chip and the bar in the flyout.</summary>
    public double Fraction => _ledger.Fraction;

    /// <summary>True between the batch opening and the first track being taken up. There is nothing to
    /// count yet, so the flyout shows a sentence and no bar.</summary>
    public bool IsPreparing => _ledger.Summary == CloudSummary.Preparing;

    /// <summary>
    /// The line above the bar, and the chip's tooltip: how many tracks went through, out of how many, with
    /// the operation's own word ("uploaded", "downloaded"). It is worked out from the ledger each time it
    /// is read, so it cannot fall behind the counts.
    /// </summary>
    public string CountText => _ledger.Summary switch
    {
        CloudSummary.Silent => "",
        CloudSummary.Preparing => Loc.Get(PreparingKey(_preparing)),

        // Counts are given when there are counts to give. A pause in the gap between the batch opening and
        // the first wave being sized leaves nothing to count, and "0 of 0" is worse than the plain word.
        CloudSummary.Paused => _ledger.Tally.Total > 0
            ? Loc.Format("CloudChip_CountPaused", Count())
            : Loc.Get("CloudChip_Paused"),

        // Space freed is the only result here that the user cannot see for themselves. The tracks are still
        // in the library and still playable. So a batch that deleted local copies says how much space it
        // freed.
        CloudSummary.Freed => Loc.Format("CloudChip_SummaryFreed", _ledger.Total, ByteSize.Human(_ledger.FreedBytes)),

        _ => Count(),
    };

    /// <summary>Raised on the UI thread whenever anything above changes.</summary>
    public event Action? Changed;

    public int Total => _ledger.Total;
    public int Failed => _ledger.Failed;

    /// <summary>The count line's key for the batch's operation. A batch with two operations, or with one
    /// that has no word of its own, uses "done". A total of one takes the singular.</summary>
    internal static string CountKey(CloudOp? op, int total)
    {
        var key = op switch
        {
            CloudOp.Upload => "CloudChip_CountUpload",
            CloudOp.Restore => "CloudChip_CountRestore",
            CloudOp.Remove => "CloudChip_CountRemove",
            _ => "CloudChip_CountDone",
        };
        return total == 1 ? key + "One" : key;
    }

    private static string PreparingKey(CloudOp op) => op switch
    {
        CloudOp.Remove => "CloudChip_SummaryPreparingRemove",
        CloudOp.Restore => "CloudChip_SummaryPreparingRestore",
        CloudOp.Offload => "CloudChip_SummaryPreparingOffload",
        _ => "CloudChip_SummaryPreparingUpload",
    };

    private string Count()
    {
        var (succeeded, total) = _ledger.Tally;
        var culture = CultureInfo.CurrentCulture;
        return Loc.Format(CountKey(_ledger.SharedOp, total),
            succeeded.ToString("N0", culture), total.ToString("N0", culture));
    }

    // ── what the engine calls ──

    /// <summary>
    /// Opens a batch. This is called as soon as the engine knows it has work: before the quota is read, and
    /// before the first file is opened. The user's own action should be visible from that point on. Nothing
    /// should appear only once a track reaches the transcoder.
    /// </summary>
    public void StartBatch(CloudOp kind) => OnUi(() =>
    {
        // A batch that has already finished is not added to. Otherwise the ring would read as half done the
        // moment new work arrived. New work starts a new list.
        if (_ledger.StartBatch())
            ClearRows();

        if (_ledger.Summary == CloudSummary.Preparing)
            _preparing = kind;
        Changed?.Invoke();
    });

    /// <summary>How many tracks the batch will touch in all. This is the rows taken up so far, plus the
    /// tracks still queued behind them. The ring reads against this total, not against the current
    /// wave.</summary>
    public void Expect(int stillQueued) => OnUi(() =>
    {
        _ledger.Expect(stillQueued);
        Changed?.Invoke();
    });

    /// <summary>Closes a batch. The last batch to close decides what the chip is left saying: a count if
    /// anything happened, and nothing at all if the run had no work in it.</summary>
    public void EndBatch() => OnUi(() =>
    {
        if (!_ledger.EndBatch())
            return;   // Another batch is still open. That batch decides what the chip says.

        if (_ledger.Summary == CloudSummary.Silent)
            ClearRows();   // A kick that found nothing due. Not worth a chip.

        Changed?.Invoke();
    });

    /// <summary>Closes a batch the user stopped part-way. The tracks that finished stay counted, because
    /// they really were uploaded. The count line says how far the run got through what it had to do. It
    /// does not call a stopped run a completed one.</summary>
    public void PauseBatch() => OnUi(() =>
    {
        if (!_ledger.PauseBatch())
            return;   // Another batch is still open. That batch decides what the chip says.
        Changed?.Invoke();
    });

    /// <summary>Takes a track up. This is called the moment the engine commits to it, so the row exists
    /// before the first slow step happens to it.</summary>
    public void Begin(long trackId, CloudOp op, string title, string? subtitle) => OnUi(() =>
    {
        // A row exists, so something is happening, so the chip is shown. The engine opens a batch before it
        // touches anything. But if a path is ever added that forgets to, a track being worked on must still
        // be visible, not quietly filling a list nobody can see. The ledger clears itself when it goes from
        // Finished back to working, and the rows have to follow it.
        var wasFinished = _ledger.Phase == CloudBatchPhase.Finished;

        if (_ledger.Begin(trackId, op))
        {
            if (wasFinished) ClearRows();
            _byTrack[trackId] = new CloudJobRow(trackId, op, title, subtitle);
        }
        else if (_byTrack.TryGetValue(trackId, out var existing))
        {
            // The same track is taken up again, for a retry or a removal right after an upload. Its
            // existing row is reused, so the list does not grow a duplicate.
            existing.Stage = CloudStage.Waiting;
            existing.Fraction = 0;
            Place(Working, existing);
        }

        Changed?.Invoke();
    });

    /// <summary>Moves a track to a stage, and optionally sets how far into that stage it is. Unknown tracks
    /// are ignored, because the engine reports more than the chip was asked to watch.</summary>
    public void Stage(long trackId, CloudStage stage, double fraction) => OnUi(() =>
    {
        if (!_byTrack.TryGetValue(trackId, out var row))
            return;
        row.Stage = stage;
        row.Fraction = fraction;
        Place(Working, row);
        Changed?.Invoke();
    });

    /// <summary>Finishes a track and writes it to the log book.</summary>
    public void Complete(long trackId, long bytes = 0) => OnUi(() =>
    {
        if (!_byTrack.TryGetValue(trackId, out var row))
            return;
        _ledger.Complete(trackId, bytes);
        row.Bytes = bytes;
        row.Fraction = 1;
        row.Stage = CloudStage.Done;
        Place(Working, row);
        Log(row, CloudOutcome.Done, null, null);
        Changed?.Invoke();
    });

    /// <summary>Fails a track and writes it to the log book. The track leaves the list, and the failed count
    /// under the bar goes up. That count stays until the chip is cleared, so the failure is still seen.
    /// </summary>
    /// <param name="detail">The sentence the user is shown on the cloud settings page.</param>
    /// <param name="fullError">Everything known about the failure. It is kept in the log book for a report
    /// (<see cref="CloudFailure.InFull(Exception)"/>). This is null only when nothing was thrown.</param>
    public void Fail(long trackId, string? detail, string? fullError) => OnUi(() =>
    {
        if (!_byTrack.TryGetValue(trackId, out var row))
            return;
        _ledger.Fail(trackId);
        row.Fail();
        Place(Working, row);
        Log(row, CloudOutcome.Failed, detail, fullError);
        Changed?.Invoke();
    });

    /// <summary>Drops a track's row without logging it. This is for work that was neither done nor refused.
    /// For example, a pause in the middle of a transfer puts the track back in the queue. A log entry
    /// saying it failed would be a false record of the user's own button press.</summary>
    public void Abandon(long trackId) => OnUi(() =>
    {
        if (!_byTrack.Remove(trackId, out var row))
            return;
        _ledger.Abandon(trackId);
        Working.Remove(row);
        Changed?.Invoke();
    });

    // ── what the chip calls ──

    /// <summary>Clears the chip once its last word has been read. The chip decides when that is. It waits
    /// while its flyout is open, because a panel that empties under the pointer takes away the thing the
    /// user opened it to read.</summary>
    public void Dismiss() => OnUi(() =>
    {
        if (!_ledger.Dismiss())
            return;
        ClearRows();
        Changed?.Invoke();
    });

    // ── internals ──

    /// <summary>
    /// Puts a row in the list, or takes it out, from the stage it is at now. The answer depends only on the
    /// row's own stage, never on which report came before. So reports from the three workers can arrive in
    /// any order and the list still holds exactly the rows that are being worked on.
    /// </summary>
    internal static void Place(IList<CloudJobRow> working, CloudJobRow row)
    {
        var listed = working.Contains(row);
        if (row.IsWorking && !listed)
            working.Add(row);
        else if (!row.IsWorking && listed)
            working.Remove(row);
    }

    /// <summary>The display's half of a clear. The ledger clears its own counts.</summary>
    private void ClearRows()
    {
        Working.Clear();
        _byTrack.Clear();
    }

    /// <summary>Writes one finished row to the mirror's log book. This is best effort. A log that cannot be
    /// written must never bring the upload down with it.</summary>
    private void Log(CloudJobRow row, CloudOutcome outcome, string? detail, string? fullError)
    {
        if (!_library.IsOpen)
            return;
        try
        {
            _library.Db.AddCloudActivity(
                row.Op, row.Title, row.Subtitle.Length == 0 ? null : row.Subtitle,
                outcome, detail, fullError, row.Bytes, DateTime.UtcNow.Ticks);
        }
        catch
        {
            // The log is a courtesy; the work it describes already happened.
        }
    }

    /// <summary>The engine reports from three upload workers at once, off the UI thread. Everything here
    /// touches an ObservableCollection that the chip is bound to, so all of it is marshalled to the UI
    /// thread.</summary>
    private void OnUi(Action work)
    {
        if (_dispatcher.HasThreadAccess)
            work();
        else
            _dispatcher.TryEnqueue(() => work());
    }
}
