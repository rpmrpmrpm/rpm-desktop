using System.Collections.ObjectModel;
using System.ComponentModel;
using RPM.Core.Cloud;
using RPM.Desktop.Strings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

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

    /// <summary>
    /// The failure's message. It is written to the log book and shown on the cloud settings page, but not
    /// here. The full error output of ffmpeg, several lines per row, would turn a panel meant to say "this
    /// did not work" into a wall of text nobody reads. The row gives the short message, and the page
    /// explains why.
    /// </summary>
    public string Detail { get; private set; } = "";

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
            Raise(nameof(StageBrush));
            Raise(nameof(IsFailed));
            Raise(nameof(RowOpacity));
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

    public bool IsFailed => Stage == CloudStage.Failed;

    /// <summary>A finished row moves back, so the rows still being worked on are read first.</summary>
    public double RowOpacity => Stage is CloudStage.Done ? 0.55 : 1;

    /// <summary>Up, down or gone. It is shown as an icon rather than a word, so the text beside it is free
    /// to show the stage, which is the part that changes.</summary>
    public string OpGlyph => Op switch
    {
        CloudOp.Remove => "\uf1f8",    // bin
        CloudOp.Restore => "\uf063",   // arrow down
        CloudOp.Offload => "\uf0c2",   // cloud: the copy that remains
        _ => "\uf062",                 // arrow up
    };

    /// <summary>The colour of the row's mark. The accent colour while it works, the app's red when it has
    /// failed, and a plain grey once it is over. A finished row needs no attention.</summary>
    public Brush StageBrush => Stage switch
    {
        CloudStage.Failed => Failed,
        CloudStage.Done => Quiet,
        _ => Working,
    };

    private static readonly Brush Working = new SolidColorBrush(Color.FromArgb(0xFF, 0x04, 0xC8, 0xFF));
    private static readonly Brush Failed = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B));
    private static readonly Brush Quiet = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF));

    public string StageText => Stage switch
    {
        CloudStage.Waiting => Loc.Get("CloudChip_StageWaiting"),
        CloudStage.Transcoding => Loc.Get("CloudChip_StageTranscoding"),
        CloudStage.Uploading => Loc.Get("CloudChip_StageUploading"),
        CloudStage.Removing => Loc.Get("CloudChip_StageRemoving"),
        CloudStage.Restoring => Loc.Get("CloudChip_StageRestoring"),
        CloudStage.Verifying => Loc.Get("CloudChip_StageVerifying"),
        // The message names the operation. "Failed" on its own leaves the user to work out what did not
        // happen to the track. A removal that failed is not the same as an upload that failed.
        CloudStage.Failed => Loc.Get(Op switch
        {
            CloudOp.Remove => "CloudChip_StageFailedRemove",
            CloudOp.Restore => "CloudChip_StageFailedRestore",
            // This has its own wording, not "upload failed". The upload worked. What did not happen is that
            // the local file was kept. That is the safe outcome, not a loss.
            CloudOp.Offload => "CloudChip_StageFailedOffload",
            _ => "CloudChip_StageFailedUpload",
        }),
        _ => Loc.Get("CloudChip_StageDone"),
    };

    internal void Fail(string? detail)
    {
        Detail = detail ?? "";
        Fraction = 1;
        Stage = CloudStage.Failed;
        Raise(nameof(StageText));
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
/// later. This makes that work visible: a ring on the deck, and a flyout that names every track and the
/// stage it is at.</para>
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
    /// scan.</summary>
    private readonly Dictionary<long, CloudJobRow> _byTrack = [];

    /// <summary>Which tracks a batch has touched, how far in it is, and when it is over. This is everything
    /// about the batch that is not drawing. See <see cref="CloudJobLedger"/>. Its rules exist because three
    /// report paths overlap.</summary>
    private readonly CloudJobLedger _ledger = new();

    public CloudJobService(LibraryService library, DispatcherQueue dispatcher)
    {
        _library = library;
        _dispatcher = dispatcher;
    }

    /// <summary>Every track this batch has touched, in the order it was taken up. New work is appended, so
    /// the list reads as a running order.</summary>
    public ObservableCollection<CloudJobRow> Rows { get; } = [];

    public CloudJobState State => _ledger.Phase switch
    {
        CloudBatchPhase.Running => CloudJobState.Running,
        CloudBatchPhase.Finished => CloudJobState.Finished,
        CloudBatchPhase.Paused => CloudJobState.Paused,
        _ => CloudJobState.Idle,
    };

    /// <summary>0..1 across the whole batch, counted in tracks. The per-track bars show the detail. The
    /// ring shows how much of what was asked for is done.</summary>
    public double Fraction => _ledger.Fraction;

    /// <summary>The one-line summary for the chip's tooltip and the flyout header. The ledger decides which
    /// sentence applies, and with which numbers. The words are here, because they are translated.</summary>
    public string Summary { get; private set; } = "";

    /// <summary>Raised on the UI thread whenever anything above changes.</summary>
    public event Action? Changed;

    public int Total => _ledger.Total;
    public int Finished => _ledger.Finished;
    public int Failed => _ledger.Failed;

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
            Summary = Loc.Get(kind switch
            {
                CloudOp.Remove => "CloudChip_SummaryPreparingRemove",
                CloudOp.Restore => "CloudChip_SummaryPreparingRestore",
                CloudOp.Offload => "CloudChip_SummaryPreparingOffload",
                _ => "CloudChip_SummaryPreparingUpload",
            });
        Changed?.Invoke();
    });

    /// <summary>How many tracks the batch will touch in all. This is the rows taken up so far, plus the
    /// tracks still queued behind them. The ring reads against this total, not against the current
    /// wave.</summary>
    public void Expect(int stillQueued) => OnUi(() =>
    {
        _ledger.Expect(stillQueued);
        Recount();
    });

    /// <summary>Closes a batch. The last batch to close decides what the chip is left saying: a summary if
    /// anything happened, and nothing at all if the run had no work in it.</summary>
    public void EndBatch() => OnUi(() =>
    {
        if (!_ledger.EndBatch())
            return;   // Another batch is still open. That batch decides what the chip says.

        // Space freed is the only result here that the user cannot see for themselves. The tracks are still
        // in the library and still playable. The one thing that changed is a number on a disk that the user
        // is not looking at. So a batch that deleted local copies reports how much space it freed.
        Summary = _ledger.Summary switch
        {
            CloudSummary.Failed => Loc.Format("CloudChip_SummaryFailed", _ledger.Failed, _ledger.Total),
            CloudSummary.Freed => Loc.Format("CloudChip_SummaryFreed", _ledger.Total, ByteSize.Human(_ledger.FreedBytes)),
            CloudSummary.Done => Loc.Format("CloudChip_SummaryDone", _ledger.Total),
            _ => "",   // A kick that found nothing due. Not worth a chip.
        };

        if (_ledger.Summary == CloudSummary.Silent)
            ClearRows();

        Changed?.Invoke();
    });

    /// <summary>Closes a batch the user stopped part-way. The rows that finished stay finished, because
    /// they really were uploaded. The line above them says how far the run got through what it had to do.
    /// It does not call a stopped run a completed one.</summary>
    public void PauseBatch() => OnUi(() =>
    {
        if (!_ledger.PauseBatch())
            return;   // Another batch is still open. That batch decides what the chip says.

        // Counts are given when there are counts to give. Pausing in the gap between the batch opening and
        // the first wave being sized leaves nothing to count. "0 of 0 tracks done" is worse than the plain
        // word.
        var (finished, total) = _ledger.Progress;
        Summary = total > 0
            ? Loc.Format("CloudChip_SummaryPaused", finished, total)
            : Loc.Get("CloudChip_Paused");

        Changed?.Invoke();
    });

    /// <summary>Takes a track up. This is called the moment the engine commits to it, so the row is on
    /// screen before the first slow step happens to it.</summary>
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

            var row = new CloudJobRow(trackId, op, title, subtitle);
            _byTrack[trackId] = row;
            Rows.Add(row);
        }
        else if (_byTrack.TryGetValue(trackId, out var existing))
        {
            // The same track is taken up again, for a retry or a removal right after an upload. Its
            // existing row is reused, so the list does not grow a duplicate.
            existing.Stage = CloudStage.Waiting;
            existing.Fraction = 0;
        }

        Recount();
    });

    /// <summary>Moves a track to a stage, and optionally sets how far into that stage it is. Unknown tracks
    /// are ignored, because the engine reports more than the chip was asked to watch.</summary>
    public void Stage(long trackId, CloudStage stage, double fraction) => OnUi(() =>
    {
        if (!_byTrack.TryGetValue(trackId, out var row))
            return;
        row.Stage = stage;
        row.Fraction = fraction;
        Recount();
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
        Log(row, CloudOutcome.Done, null, null);
        Recount();
    });

    /// <summary>Fails a track and writes it to the log book. The row stays on the list. A failure that
    /// disappears is a failure the user never sees.</summary>
    /// <param name="detail">The sentence the user is shown.</param>
    /// <param name="fullError">Everything known about the failure. It is kept in the log book for a report
    /// (<see cref="CloudFailure.InFull(Exception)"/>). This is null only when nothing was thrown.</param>
    public void Fail(long trackId, string? detail, string? fullError) => OnUi(() =>
    {
        if (!_byTrack.TryGetValue(trackId, out var row))
            return;
        _ledger.Fail(trackId);
        row.Fail(detail);
        Log(row, CloudOutcome.Failed, detail, fullError);
        Recount();
    });

    /// <summary>Drops a track's row without logging it. This is for work that was neither done nor refused.
    /// For example, a pause in the middle of a transfer puts the track back in the queue. A log entry
    /// saying it failed would be a false record of the user's own button press.</summary>
    public void Abandon(long trackId) => OnUi(() =>
    {
        if (!_byTrack.Remove(trackId, out var row))
            return;
        _ledger.Abandon(trackId);
        Rows.Remove(row);
        if (Rows.Count == 0)
            Summary = "";
        Recount();
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

    /// <summary>The display's half of a clear. The ledger clears its own counts.</summary>
    private void ClearRows()
    {
        Rows.Clear();
        _byTrack.Clear();
        Summary = "";
    }

    /// <summary>
    /// Works out the ring and the line under it again.
    ///
    /// <para>This does NOT decide that the batch is over. Every row being done only means that no worker is
    /// in the middle of a file. Between two waves of an upload, that is true for a moment. A chip that read
    /// it as the end would announce twelve tracks done, fade out, and start again on the thirteenth. The
    /// engine says when it has stopped. See <see cref="EndBatch"/>.</para>
    /// </summary>
    private void Recount()
    {
        if (_ledger.Summary == CloudSummary.Running)
        {
            var (finished, total) = _ledger.Progress;
            Summary = Loc.Format("CloudChip_SummaryRunning", finished, total);
        }

        Changed?.Invoke();
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
