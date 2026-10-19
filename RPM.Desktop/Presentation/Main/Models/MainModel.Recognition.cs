using RPM.Core.Diagnostics;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using RPM.Core.Analysis;
using RPM.Core.Import;
using RPM.Core.Identify;
using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Core.Models;
using RPM.Desktop.Services;
using RPM.Desktop.Services.Account;
using RPM.Desktop.Strings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace RPM.Desktop.Presentation.Main.Models;

/// <summary>Where a recognition run's answers are put, and what the list it was started from does when the
/// run is over.
///
/// <para>The run is the same work on the same bytes, wherever it was started: a fingerprint, a ladder of
/// catalogues, then a name. Only the ending differs. A queue row is a copy from the import store, and it
/// has no filed home yet. So its answer is written back to that store, and the file moves later. A library
/// row is a file that is already on the shelf. So its answer has to reach the tags, and the file has to be
/// filed again. Passing this in, rather than branching on it inside the run, lets one run serve both.
/// Neither list needs to know about the other.</para>
/// </summary>
/// <param name="Write">Saves these rows' new names. This is called as each answer lands. The names are not
/// kept until the end. A long run can be cancelled, or it can lose the network halfway. Names that were
/// already found must survive that.</param>
/// <param name="Settled">Called when the run is over, with how many rows were renamed and the first of
/// them. The queue follows a single renamed row to wherever the tree moved it. That is meaningless for many
/// rows, and for a list that does not regroup itself.</param>
public sealed record RecognitionSink(
    Func<IReadOnlyList<TrackRow>, Task> Write,
    Func<int, TrackRow?, Task> Settled);

/// <summary>How far a run is allowed to reach: the basic ladder alone, or the basic ladder with the
/// advanced layer behind it for whatever the ladder cannot name.
///
/// <para>The choice is not a user option. It belongs to the account. Supporting rpm is what buys the
/// advanced layer. So there is no public constructor. The only ways to make one are to give an answer about
/// the account (<see cref="For"/>), or to ask for the basic ladder by name. A caller that never asked
/// cannot claim "advanced".</para>
/// </summary>
public readonly record struct RecognitionReach
{
    private RecognitionReach(bool advanced) => UsesAdvanced = advanced;

    /// <summary>True when a track that the basic ladder cannot name goes on to the advanced
    /// layer.</summary>
    public bool UsesAdvanced { get; }

    /// <summary>The basic ladder and nothing else: the rpm database, then AcoustID.</summary>
    public static RecognitionReach Basic => new(false);

    /// <summary>What this account gets. Advanced is used when it is needed, and it is never chosen by the
    /// user. A supporter does not tick a box to get what they already pay for.
    ///
    /// <para>An unknown standing (signed out, or offline) reaches for the advanced layer anyway. The server
    /// is the one that refuses. Guessing "no" here would deny a supporter over a dropped request. A refusal
    /// that does come back drops the rest of the run to basic. It does not end the run. See <see
    /// cref="MainModel.RunRecognitionAsync"/>.</para>
    /// </summary>
    /// <param name="support">The account's standing, or null when it could not be read.</param>
    /// <param name="advancedLayerExists">Whether this build has an advanced layer to reach at all.</param>
    public static RecognitionReach For(SupporterService.Status? support, bool advancedLayerExists)
        => new(advancedLayerExists && support is not { IsSupporter: false });

    /// <summary>What the split window's Recognize selected may use, or null when it is not offered at all.
    ///
    /// <para>Supporters only, with the advanced layer behind the basic ladder. A recording of a record is a
    /// version that the basic ladder rarely knows. The rpm database is still small. AcoustID matches a
    /// recording, so it names the exact master that a transfer was made from, or nothing. AudD recognises
    /// the song, and that is what naming the pieces of a transfer needs.</para>
    ///
    /// <para>The basic ladder is kept here, not removed. When the rpm database has grown enough to name
    /// records by itself, returning <see cref="Basic"/> instead of null for an account that is not a
    /// supporter opens Recognize selected to everyone. That is the only line to change.</para>
    ///
    /// <para>Unlike <see cref="For"/>, an unread standing means "not offered". <see cref="For"/> decides
    /// how far a run reaches, and it leaves the refusal to the server. This method decides whether a button
    /// is shown. A button shown to every account whose standing was never read would be shown to everyone.
    /// <see cref="SupporterService.Standing"/> answers from the last thing the server said. So a supporter
    /// who is offline still sees the button.</para>
    /// </summary>
    /// <param name="support">The account's standing as last told, or null when it was never told.</param>
    /// <param name="advancedLayerExists">Whether this build has an advanced layer to reach at all.</param>
    public static RecognitionReach? ForSplit(SupporterService.Status? support, bool advancedLayerExists)
        => advancedLayerExists && support is { IsSupporter: true } ? new(true) : null;
}

/// <summary>Working out what a queued file actually IS: fingerprint it, ask the catalogues, and write the
/// answer onto the row. Recognition is a step that the queue offers, not a step that import takes. An
/// unrecognised row can still be imported, and a recognised row still has to be imported.
/// </summary>
public sealed partial class MainModel
{
    /// <summary>The import queue's ending. The row is a detached copy, so the answer goes back to the
    /// store. The tree is rebuilt when a name has moved a row. A track that sat under "Unknown artist" a
    /// moment ago may belong somewhere else now. A run that only confirmed what the files already said
    /// moves nothing. It leaves the reader exactly where they were.
    /// </summary>
    public RecognitionSink QueueSink
    {
        get
        {
            // One sink per run, so these belong to the run: what it wrote, and whether any of it landed
            // somewhere else in the tree.
            var written = new List<TrackRow>();
            var moved = false;

            return new(
                rows =>
                {
                    moved |= _queueSource.WriteBackMany(rows);
                    written.AddRange(rows);
                    return Task.CompletedTask;
                },
                (renamed, first) =>
                {
                    _dispatcher.TryEnqueue(async () =>
                    {
                        if (!moved)
                        {
                            _queueSource.ApplyEditsInPlace(written);
                            return;
                        }

                        await _queueSource.RebuildAsync(_queueFilter);

                        // Follow the row only when there is A row to follow. One track named is a track
                        // that left the screen without a word, and taking the user to it is the whole
                        // point. A hundred tracks named are a hundred rows scattering across the tree.
                        // There is no single "where it went" to go to. Jumping to whichever row came first
                        // would move the reader away from their place, to show them an arbitrary one of the
                        // hundred. So the batch case moves nothing, and the viewport stays.
                        if (renamed == 1 && first is { QueueId: var id }
                            && _queueSource.LocateRow(id) is var at and >= 0)
                            RevealQueueRow?.Invoke(at);
                    });
                    return Task.CompletedTask;
                });
        }
    }

    // ── recognition ──

    /// <summary>A backend exists. Being signed out does not disable the button. Clicking it opens the login
    /// dialog with the reason.</summary>
    public bool IdentificationAvailable => _identification.IsAvailable;

    private void OnIdentificationAvailabilityChanged() => _dispatcher.TryEnqueue(() =>
        OnChanged(nameof(IdentificationAvailable)));

    /// <summary>True while a run is in flight. A second run is refused, because the rows it would act on
    /// are being written to by the first. So the dialog asks before it offers a button that would do
    /// nothing.</summary>
    public bool IsRecognizing => _recognizing;

    /// <summary>True when the advanced clip layer exists at all. That is the Lambda backend.</summary>
    public bool AdvancedLayerAvailable =>
        _identification.Backend is RPM.Core.Identify.IClipIdentificationBackend;

    /// <summary>Runs recognition over the rows it is handed. If it is handed none, it uses the ticked
    /// rows. It reports what happened to each one: which layer answered, and what it charged. The dialog
    /// decides what to run and how to report it. This method does the work and applies the names.
    /// </summary>
    /// <param name="reach">How far this account may reach. See <see cref="RecognitionReach"/>. The basic
    /// ladder runs first for every track, either way. The advanced layer only sees what the ladder gave up
    /// on.</param>
    /// <param name="outcomeReport">Each track's answer as it lands. This way the dialog's list fills up
    /// while the run is going, and not all at once at the end. The same outcomes come back in the report.
    /// </param>
    /// <param name="sink">Where the answers are put, and what happens when the run ends. See <see
    /// cref="RecognitionSink"/>. It defaults to <see cref="QueueSink"/>, which is where recognition
    /// started, and where most of it is still asked for.</param>
    public async Task<RecognitionReport> RunRecognitionAsync(
        RecognitionReach reach,
        IProgress<RecognitionProgress>? progress = null,
        IReadOnlyList<TrackRow>? explicitRows = null,
        IProgress<RecognitionOutcome>? outcomeReport = null,
        CancellationToken ct = default,
        RecognitionSink? sink = null)
    {
        sink ??= QueueSink;
        if (_identification.Backend is not { } backend || _recognizing)
            return RecognitionReport.Empty;

        var targets = explicitRows?.ToList() ?? CheckedRows();
        if (targets.Count == 0)
            return RecognitionReport.Empty;

        _recognizing = true;
        RaiseRecognizeState();

        var identifier = new TrackIdentifier(backend);
        // The reach as this run holds it. The server can take the advanced layer away during the run. That
        // happens when a supporter's term ends between two tracks, or when the standing was never read in
        // the first place. The rest of the run carries on without it rather than stopping.
        var advanced = reach.UsesAdvanced;
        string? note = null;
        var outcomes = new List<RecognitionOutcome>(targets.Count);
        string? fatal = null;
        TrackRow? movedFirst = null;
        var moved = 0;

        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var row = targets[i];
                var name = Path.GetFileName(row.FilePath);
                progress?.Report(new RecognitionProgress(i, targets.Count, name));

                // Read before the answer is applied. Afterwards there is nothing left to compare to, and
                // "was Track 04, is now Blue Monday" is the line the user is here for.
                var wasArtist = row.Artist;
                var wasTitle = row.Title;

                try
                {
                    // The basic ladder always runs first, however far this run may reach. A track that the
                    // rpm database or AcoustID can name must never go out to AudD.
                    var outcome = await identifier.IdentifyDetailedAsync(row.ToWantedTrackInfo(), ct);

                    // Advanced is not a mode the user picked. So being refused it is not a failure of the
                    // run. The basic answer that this track already has stands, and the rest of the list is
                    // looked up without it.
                    if (advanced && !outcome.Matched)
                    {
                        try
                        {
                            outcome = await identifier.IdentifyPremiumDetailedAsync(row.ToWantedTrackInfo(), ct);
                        }
                        catch (LambdaIdentificationBackend.SupportersOnlyException)
                        {
                            advanced = false;
                            note = Loc.Get("Recognize_AdvancedRefused");
                        }
                    }

                    if (outcome.Matched)
                    {
                        var t = outcome.Track;
                        row.ApplyRecognition(t.Title!, t.Artist!, t.Album, t.Year, t.IdentificationScore);
                        await sink.Write([row]);   // the store, or the file's own tags. See
                                                   // RecognitionSink.
                        movedFirst ??= row;
                        moved++;
                    }

                    Record(new RecognitionOutcome(
                        name, outcome.Matched, outcome.Track.Title, outcome.Track.Artist,
                        outcome.Source, outcome.Score, null, outcome.AudD)
                    {
                        Row = row,
                        PreviousArtist = wasArtist,
                        PreviousTitle = wasTitle,
                        Album = outcome.Track.Album,
                    });
                }
                catch (LambdaIdentificationBackend.IdentificationUnavailableException ex)
                {
                    // The service cannot look ANYTHING up. Grinding through the rest would produce a list
                    // of identical failures, and that would tell the user nothing they do not already know.
                    Faults.Note("recognize", "recognition", ex);
                    fatal = Loc.Get("Recognize_Unavailable");
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad file (unreadable, or the fingerprint tool failed) must not sink the batch.
                    // But it is recorded as a failure, not quietly folded into "not recognised". A run the
                    // user stopped is neither, and it passes to the handler outside the loop. Whatever
                    // threw, no AudD answer was read for this track. So it is not counted as one that went
                    // to AudD.
                    Record(new RecognitionOutcome(name, false, null, null, null, 0,
                        Faults.Note("recognize", "recognizing a track", ex), RPM.Core.Identify.AudDOutcome.NotAsked)
                    {
                        Row = row,
                        PreviousArtist = wasArtist,
                        PreviousTitle = wasTitle,
                    });
                }
            }

            progress?.Report(new RecognitionProgress(targets.Count, targets.Count, ""));
        }
        catch (OperationCanceledException)
        {
            fatal = Loc.Get("Review_RecognizeCancelled");
        }
        finally
        {
            _recognizing = false;
            RaiseRecognizeState();

            // The names went where the sink put them as each one came back. This tells the list they came
            // from that the run is done. What that means is the sink's business. See QueueSink.
            await sink.Settled(moved, movedFirst);
        }

        var report = new RecognitionReport(outcomes, targets.Count, fatal) { Note = note };
        SetStatus(report.Summary);
        return report;

        // Kept in the list AND told to whoever is watching. The dialog can then show an answer the moment
        // it exists, instead of a bar that moves and a list that stays empty.
        void Record(RecognitionOutcome outcome)
        {
            outcomes.Add(outcome);
            outcomeReport?.Report(outcome);
        }
    }

    /// <summary>Puts a line in the queue's status area. It is public because work that the page drives
    /// itself, such as finishing a vinyl session, has nowhere else to report a failure.</summary>
    public void SetStatus([Shown] string s)
    {
        _status = s;
        _dispatcher.TryEnqueue(() => OnChanged(nameof(RecognizeStatus)));
    }

    /// <summary>The last line that <see cref="SetStatus"/> was given.</summary>
    public string RecognizeStatus => _status;

    private void RaiseRecognizeState()
    {
        OnChanged(nameof(IsRecognizing));
        OnChanged(nameof(CanImport));
        OnChanged(nameof(QueueFolderPath));   // recognition changes what is importable, hence the count
    }
}
