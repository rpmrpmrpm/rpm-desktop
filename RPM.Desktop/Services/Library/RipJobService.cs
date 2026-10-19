using RPM.Core.Diagnostics;
using System.Collections.ObjectModel;
using RPM.Core.Models;
using RPM.Core.Ripping;
using RPM.Desktop.Presentation.Dialogs;
using RPM.Desktop.Strings;
using Microsoft.UI.Dispatching;

namespace RPM.Desktop.Services.Library;

/// <summary>The stage of a rip. A clean finish is not shown. It hands its folder to the import queue, and
/// the queue gives the feedback. A cancel is not shown either, because it deleted everything it made. The
/// other three stages each have something to show.</summary>
public enum RipJobState
{
    Idle,

    /// <summary>A disc turned up in the drive by itself, and the user has been asked what to do with it.
    /// Nothing is happening to the disc yet. This is the question, not the work.</summary>
    Offered,

    Running,

    /// <summary>Finished, but the disc cost the album something: a track was skipped, or damage was patched
    /// over with silence. It stays on the deck until the user has read it. A rip that drops track nine
    /// without saying so would be misleading.</summary>
    Damaged,

    Failed,
}

/// <summary>The one rip in flight. The app owns it, not the popup that started it. A disc takes minutes.
/// The dialog is only a window onto this job, and closing the dialog leaves the drive spinning.
///
/// Everything the rip step needs to keep (progress, the tracks that have landed, which one is being
/// auditioned, the cancel token) lives here. So the dialog and the deck's rip chip are two views of the
/// same job, not two copies. Only one job runs at a time, because there is one optical drive.
/// </summary>
public sealed class RipJobService
{
    private readonly PlaybackService _playback;
    private readonly RipperService _ripper;
    private readonly DispatcherQueue _dispatcher;

    private CancellationTokenSource? _cts;
    private RipControl? _control;
    private RippedTrackRow? _playingRow;
    private string? _completed;

    public RipJobService(RipperService ripper, PlaybackService playback, DispatcherQueue dispatcher)
    {
        _ripper = ripper;
        _playback = playback;
        _dispatcher = dispatcher;

        // The preview button uses the shared player, so it must follow the engine. A track reaching its
        // end, or something else taking the player, both come here and are treated as "not ours".
        _playback.Changed += OnPlaybackChanged;
        _playback.TrackEnded += OnPlaybackChanged;
    }

    /// <summary>The tracks that have finished ripping, in the order they landed. They can be played in
    /// place, because they are still in staging. Every view of the job shares this list.</summary>
    public ObservableCollection<RippedTrackRow> Tracks { get; } = [];

    public RipJobState State { get; private set; } = RipJobState.Idle;

    public bool IsRunning => State == RipJobState.Running;

    /// <summary>Whether the rip can still be steered. The stop and skip buttons depend on this.</summary>
    public bool CanSteer => IsRunning && _control is not null;

    /// <summary>0 to 1 across the whole disc, not the current track.</summary>
    public double Fraction { get; private set; }

    /// <summary>What the rip is doing right now, ready to show.</summary>
    public string Status { get; private set; } = "";

    /// <summary>The failure message, when there is one.</summary>
    public string Detail { get; private set; } = "";

    /// <summary>The disc being ripped, as the user named it: the album name, or the disc's own volume
    /// name.</summary>
    public string Title { get; private set; } = "";

    /// <summary>Which kind of disc is in the drive. This decides which window reopens the rip, since a CD
    /// and a DVD are two different views of the same job.</summary>
    public RipSource Source { get; private set; } = RipSource.Cd;

    /// <summary>Raised on the UI thread whenever anything above changes.</summary>
    public event Action? Changed;

    /// <summary>Raised when a rip finishes and its staging folder is ready for the import queue. Take the
    /// folder with <see cref="TakeCompleted"/>. A finish that happens while nothing is listening is then
    /// still picked up by the next view that asks.</summary>
    public event Action? Finished;

    /// <summary>True from when a rip finishes until its folder is handed to the import queue. It tells a
    /// rip that ended well apart from one that was cancelled. Both of them end in the Idle state.</summary>
    public bool HasCompleted => _completed is not null;

    /// <summary>The staging folder of a rip that finished and has not been imported yet. It is cleared when
    /// it is handed out, so two listeners cannot import it twice.</summary>
    public string? TakeCompleted()
    {
        var folder = _completed;
        _completed = null;
        return folder;
    }

    /// <summary>Shows the question. A disc appeared in the drive by itself, and the deck's chip asks
    /// whether to rip it. Nothing is read from the disc to get here. The offer costs the user a glance, and
    /// the drive is woken only if they say yes.
    ///
    /// <para>Only allowed from Idle. A rip in progress owns the drive. A chip that still shows a failure
    /// holds something the user has not read yet.</para>
    /// </summary>
    public void Offer(string title)
    {
        if (State != RipJobState.Idle)
            return;

        Title = title;
        Source = RipSource.Cd;
        State = RipJobState.Offered;
        Fraction = 0;
        Status = "";
        Detail = "";
        Raise();
    }

    /// <summary>Removes the question. This happens when the user says no, or says yes and the disc window
    /// has taken the disc over from here.</summary>
    public void CloseOffer()
    {
        if (State != RipJobState.Offered)
            return;

        Reset();
        Raise();
    }

    /// <summary>Starts a rip. The caller provides the plan, and knows whether this is a CD or a DVD title.
    /// This method owns everything that must outlive the caller.
    /// </summary>
    /// <returns>False when a rip is already running. There is one drive, so there is one job.</returns>
    public bool Start(string title, RipSource source, DiscDrive? drive,
        Func<RipHooks, CancellationToken, Task<RipResult>> run)
    {
        if (IsRunning)
            return false;

        // A new rip starts with an empty list. A retry after a failure must not show the tracks from the
        // last run, because their staging files were deleted with the failed folder.
        StopPreview();
        Tracks.Clear();

        Title = title;
        Source = source;
        State = RipJobState.Running;
        Fraction = 0;
        Detail = "";
        Status = Loc.Get("AddMusic_RipStarting");
        Raise();

        _cts = new CancellationTokenSource();
        _control = new RipControl();
        _ = RunAsync(drive, run, _control, _cts.Token);
        return true;
    }

    /// <summary>Throws the rip away. The ripper deletes the whole staging folder on cancel, so the tracks
    /// that had already landed are deleted too, and nothing reaches the library.
    ///
    /// The destructive one of the three. <see cref="Stop"/> is the other choice. A disc that fails on track
    /// nine is still worth eight tracks, so the button that ends a rip should not be the one that deletes
    /// them.
    /// </summary>
    public void Cancel() => _cts?.Cancel();

    /// <summary>Ends the rip here and keeps what it has. The tracks already on disk go to the import queue
    /// as a short album. Only the track being read is thrown away. A stop before a single track has
    /// finished has nothing to keep, so it ends exactly as a cancel does.</summary>
    public void Stop() => _control?.Stop();

    /// <summary>Gives up on the track being read and moves to the next one. This is the way past a track
    /// that the drive will never finish. A scratched track can be re-seeked forever and does not answer
    /// cancellation, so the read is abandoned instead of waited for.</summary>
    public void SkipTrack() => _control?.SkipTrack();

    /// <summary>Clears a finished rip's last message from the chip. The message is either a failure or the
    /// tally of what a damaged disc cost. This is done once the user has read it.</summary>
    public void Dismiss()
    {
        if (State is not (RipJobState.Failed or RipJobState.Damaged))
            return;

        // A damaged rip's rows point at staging files that the importer has already moved. So the list is
        // cleared with the tally, and it does not stay behind as dead play buttons.
        StopPreview();
        Tracks.Clear();
        Reset();
        Raise();
    }

    private async Task RunAsync(DiscDrive? drive, Func<RipHooks, CancellationToken, Task<RipResult>> run,
        RipControl control, CancellationToken ct)
    {
        var progress = new Progress<RipProgress>(p => _dispatcher.TryEnqueue(() =>
        {
            Fraction = p.Overall;
            Status = Loc.Format("AddMusic_Ripping", p.TrackNow, p.TrackTotal, p.CurrentTrack);
            Raise();
        }));

        // Each finished track is added to the list as soon as it lands. It can be played while the rest of
        // the disc is still ripping.
        var trackDone = new Progress<TrackInfo>(t => _dispatcher.TryEnqueue(() =>
        {
            Tracks.Add(new RippedTrackRow(
                (int)t.TrackNumber,
                string.IsNullOrWhiteSpace(t.Title)
                    ? Loc.Format("AddMusic_TrackN", (int)t.TrackNumber)
                    : t.Title,
                Format(TimeSpan.FromSeconds(t.DurationSeconds)),
                t.FilePath,
                t.DurationSeconds));
            Raise();
        }));

        // A track that the rip gave up on takes its place in the same list, greyed out and not playable.
        // The album is deliberately one track short. The gap is the only honest way to show this. Closing
        // the gap silently would leave the user counting rows to find out what was lost.
        var trackSkipped = new Progress<RipTrackOutcome>(outcome => _dispatcher.TryEnqueue(() =>
        {
            Tracks.Add(new RippedTrackRow(
                outcome.Number,
                string.IsNullOrWhiteSpace(outcome.Title)
                    ? Loc.Format("AddMusic_TrackN", outcome.Number)
                    : outcome.Title,
                Loc.Get("Rip_Skipped"),
                "",
                0)
            {
                Skip = outcome.Result,
            });
            Raise();
        }));

        var hooks = new RipHooks
        {
            Progress = progress,
            TrackDone = trackDone,
            TrackSkipped = trackSkipped,
            Control = control,
        };

        try
        {
            var result = await run(hooks, ct);

            if (drive is not null)
                await _ripper.EjectAsync(drive);

            _dispatcher.TryEnqueue(() =>
            {
                Fraction = 1;

                // Damage that did not cost a whole track can still cause clicks. So the rows that carry it
                // are marked before the album leaves for the library.
                foreach (var outcome in result.TrackOutcomes.Where(o => !o.IsSkipped && o.RepairedBlocks > 0))
                {
                    var row = Tracks.FirstOrDefault(r => r.Number == outcome.Number && !r.IsSkipped);
                    if (row is not null)
                        row.RepairedBlocks = outcome.RepairedBlocks;
                }

                var repaired = result.TrackOutcomes.Count(o => !o.IsSkipped && o.RepairedBlocks > 0);
                if (result.SkippedCount > 0 || repaired > 0 || result.Stopped)
                {
                    // The disc cost the album something. The chip stays on the deck with the tally and the
                    // rows behind it until the user has read them. The import queue that takes the folder
                    // from here shows only what arrived, never what did not.
                    State = RipJobState.Damaged;
                    Status = Loc.Format("Rip_DoneShort", result.Tracks.Count, result.TrackTotal);
                    Detail = DamageDetail(result.SkippedCount, repaired);
                }
                else
                {
                    Reset();
                }

                // This does not stop a playing preview. The import moves the file on the same volume, so an
                // open file handle follows the file, and a track being auditioned keeps playing.
                _completed = result.StagingFolder;
                Raise();
                Finished?.Invoke();
            });
        }
        catch (OperationCanceledException)
        {
            // The folder, and every staging file in the list, was deleted on cancel. So nothing here can be
            // played now.
            _dispatcher.TryEnqueue(() =>
            {
                StopPreview();
                Tracks.Clear();
                Reset();
                Raise();
            });
        }
        catch (Exception ex)
        {
            Faults.Note("rip", "ripping the disc", ex);
            var detail = FailureKey(ex);

            // The rip is all or nothing. A failure deletes the whole staging folder, and the tracks that
            // had already landed go with it. So none of them can be played now.
            _dispatcher.TryEnqueue(() =>
            {
                StopPreview();
                Tracks.Clear();
                State = RipJobState.Failed;
                Status = Loc.Get("AddMusic_RipFailed");
                Detail = Loc.Get(detail);
                Raise();
            });
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _control = null;
        }
    }

    /// <summary>The line under "The rip failed.", as a string key. A copy-protected DVD gets the fix for
    /// this platform, and a disc that cannot be read gets its own message. Anything else uses the <see
    /// cref="Fault"/> sentence. The exception itself is written to the diagnostic ring under "rip".
    /// </summary>
    internal static string FailureKey(Exception ex) => ex switch
    {
        DvdEncryptedException => OperatingSystem.IsWindows()
            ? "AddMusic_DvdNeedsLibDvdCssWindows"
            : "AddMusic_DvdNeedsLibDvdCss",
        DiscReadException => "AddMusic_RipUnreadable",
        _ => Faults.Read(ex).StringKey() ?? "Fault_Unknown",
    };

    /// <summary>The one-line tally under a short rip: what was lost, and what was patched.</summary>
    private static string DamageDetail(int skipped, int repaired)
    {
        var parts = new List<string>(2);
        if (skipped > 0)
            parts.Add(Loc.Format("Rip_TracksSkipped", skipped));
        if (repaired > 0)
            parts.Add(Loc.Format("Rip_TracksRepaired", repaired));
        return string.Join(Loc.Get("Common_ListSeparator"), parts);
    }

    private void Reset()
    {
        State = RipJobState.Idle;
        Status = "";
        Detail = "";
    }

    private void Raise() => Changed?.Invoke();

    // ── Previewing a ripped track ────────────────────────────────────────────────────────────
    //
    // The ripped file is still in staging, waiting for the import queue. A preview plays it through the
    // shared player, the same one that RPaMp drives. It does not go through RPaMp's playlist. The file is
    // temporary, so it is played in place and never queued, and the player never deletes or moves it. One
    // row plays at a time, and its button acts as a stop button while it plays. The player owns playback,
    // so starting a preview replaces whatever it was playing.

    /// <summary>Plays the row, or stops it when it is the one playing. A skipped row has no file, so there
    /// is nothing to preview.</summary>
    public void TogglePreview(RippedTrackRow row)
    {
        if (row.IsSkipped)
            return;

        if (row.IsPlaying)
        {
            _playback.Stop();
            ClearPlaying();
            Raise();
            return;
        }

        ClearPlaying();   // The previous row's button goes back to being a play button.
        _playback.Play(row.FilePath, row.Seconds, startSeconds: 0, title: row.Title);
        row.IsPlaying = true;
        _playingRow = row;
        Raise();
    }

    /// <summary>Stops a preview, but only when the sound coming out is ours. A track from an unrelated
    /// source that is playing through the shared player is left alone.</summary>
    public void StopPreview()
    {
        if (_playingRow is null)
            return;
        if (_playback.PlayingPath == _playingRow.FilePath)
            _playback.Stop();
        ClearPlaying();
    }

    /// <summary>The engine raises Changed and TrackEnded off the UI thread. When the sound is not the file
    /// this class started (it reached its end, or something else took the player), the preview button goes
    /// back to play.</summary>
    private void OnPlaybackChanged() => _dispatcher.TryEnqueue(() =>
    {
        if (_playingRow is null)
            return;
        bool stillOurs = _playback.PlayingPath == _playingRow.FilePath && !_playback.IsPaused;
        if (!stillOurs)
        {
            ClearPlaying();
            Raise();
        }
    });

    private void ClearPlaying()
    {
        if (_playingRow is not null)
            _playingRow.IsPlaying = false;
        _playingRow = null;
    }

    private static string Format(TimeSpan duration) => duration.TotalHours >= 1
        ? duration.ToString(@"h\:mm\:ss")
        : duration.ToString(@"m\:ss");
}
