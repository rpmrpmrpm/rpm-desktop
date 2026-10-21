using Microsoft.Extensions.Logging;
using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using RPM.Desktop.Services.Account;
using RPM.Desktop.Services.Sharing;
using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Services.Cloud;

/// <summary>
/// Sends this computer's generated lists to the cloud, so the phone can show the same ones.
///
/// <para><b>The phone shows these lists. It does not compute them.</b> The calculation uses percentiles
/// over a whole collection, a preference table, and a decade window. The calculation changes from time to
/// time. If it were written once here and once in Swift, the two copies would drift apart. The difference
/// would stay hidden until the same list held different records on the two screens. This computer is also
/// the only device that has the whole collection. The phone knows the tracks in the cloud, and nothing
/// about the records that never left this disk. A percentile taken over part of a library is a different
/// cut.</para>
///
/// <para><b>The first cut after the shelf settles, and again whenever the answer changes.</b> What is sent
/// is the lists as they stand in the cloud. So an upload that finishes changes them. A record gains its RPM
/// id when it arrives. Until then it is a song that the phone cannot ask for, and the list does not name
/// it. The library and the cloud mirror are both watched. After either one changes, the service waits for a
/// quiet spell and then takes the cut again.</para>
///
/// <para><b>Also at once when the user presses "generate a new list".</b> That press moves the mood window
/// and puts a different list on the screen. Waiting for a quiet spell would leave the phone showing the
/// list that was replaced. See <see cref="SendNow"/>. It signals the worker that is already running, and
/// does not start a second pusher.</para>
///
/// <para><b>An unchanged cut is not sent.</b> Two builds of an unchanged shelf name the same records in the
/// same order (<see cref="MoodListPush.Same"/>). Sending the same bytes again is a round trip that changes
/// only the stamp. The phone shows the stamp as the age of what it is showing.</para>
///
/// <para><b>Nothing on screen waits for this, and nothing breaks without it.</b> If a push fails, the phone
/// keeps the cut it already has. That cut is stamped, so the phone can say how old it is. This is a better
/// answer than an empty shelf. That is why nothing here retries hard. The next change to the library asks
/// again.</para>
/// </summary>
public sealed partial class MoodListPushService : IDisposable
{
    private readonly RpmCloudClient _cloud;
    private readonly AuthService _auth;
    private readonly Services.Library.LibraryService _library;
    private readonly CloudMirrorService _mirror;
    private readonly ILogger<MoodListPushService> _log;

    private CancellationTokenSource? _stop;
    private Task? _worker;

    /// <summary>The library the running worker is pushing for. A second collection opened in the same
    /// session is a different shelf with different lists, so the worker for the old one must
    /// stop.</summary>
    private LibraryDatabase? _on;

    /// <summary>The cut the store holds, as far as this session knows. A rebuilt cut is compared against
    /// it. Null before the first push for a collection, when everything is worth sending.
    /// </summary>
    private MoodListsPayload? _pushed;

    /// <summary>The lists whose picture is known to be in the cloud, either found there or drawn and sent.
    /// A picture depends on what a list is, not on what it currently contains. So a list that has been
    /// dealt with once stays dealt with. Asking the store again on every push would mean many round trips
    /// for a question that is already answered.</summary>
    private readonly HashSet<string> _pictured = [];

    /// <summary>The wait between one cut and the next. Something changed, and then nothing changed for <see
    /// cref="Quiet"/>. See <see cref="QuietSpell"/>, which stops an upload run from costing one cut per
    /// record.</summary>
    private readonly QuietSpell _settled = new(Quiet);

    public MoodListPushService(
        RpmCloudClient cloud,
        AuthService auth,
        Services.Library.LibraryService library,
        CloudMirrorService mirror,
        ILogger<MoodListPushService> log)
    {
        _cloud = cloud;
        _auth = auth;
        _library = library;
        _mirror = mirror;
        _log = log;

        // Listen to both, because either can happen last. A library opened while signed out has nowhere to
        // send the lists. A sign-in with no library open has nothing to send.
        _library.Opened += _ => Start();
        _auth.Changed += Start;

        // Two things can change which records a list names. A change in the library covers records
        // arriving, leaving, and being retagged. A change in the mirror covers the case that matters most:
        // a track gains its RPM id when it reaches the cloud.
        _library.Changed += _settled.Nudge;
        _mirror.Changed += _settled.Nudge;

        // The worker cuts the lists from the library. It is stopped and waited for before the library is
        // closed (see Shell.PassLife), including a worker that was stopped for an older collection and has
        // not ended yet.
        _library.StopBeforeClose(_life.CloseAsync);
    }

    /// <summary>The workers, stopped and waited for before the library is closed.</summary>
    private readonly Shell.PassLife _life = new();

    private readonly object _starting = new();

    /// <summary>
    /// How long to let the measuring settle before the first cut.
    ///
    /// <para>The mood numbers are measured by a background sweep. On a shelf that has just been opened for
    /// the first time, most of them are not measured yet. A cut taken in the first seconds would cover only
    /// what happened to be measured. One minute is not enough to finish a big library, but it is enough to
    /// get past the worst of it. The rest is covered because the sweep writes its results. That changes the
    /// library, and a changed library gives another cut.</para>
    /// </summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How quiet the library has to be before the cut is taken again.
    ///
    /// <para>An upload run or a scan raises its change hundreds of times, and each cut queries every
    /// offered list. So the worker waits for the run to finish instead of racing it: thirty seconds with
    /// nothing changing, and then one check.</para>
    /// </summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Takes the cut and sends it now, instead of at the next quiet moment.
    ///
    /// <para>The "generate a new list" button causes this. The person who pressed it is looking at the list
    /// it produced. If the phone shows a different list until the next quiet moment, the two screens
    /// disagree about a list that was just requested.</para>
    ///
    /// <para><b>A signal, not a second pusher.</b> It returns at once. The work is done by the one worker
    /// that this service runs for each open collection. That prevents two cuts of the same shelf from being
    /// built and sent at the same time.</para>
    ///
    /// <para>When the user is signed out, or no collection is open, there is nothing to send and nobody to
    /// send it as. <see cref="Start"/> handles that case.</para>
    /// </summary>
    public void SendNow()
    {
        Start();
        _settled.AtOnce();
    }

    /// <summary>Starts the push, or leaves the one in progress alone. Safe to call repeatedly: every event
    /// that reaches it can fire more than once, and most of them fire when nothing has changed.</summary>
    public void Start()
    {
        // Under a lock: the library, the account and the settings page all call this, and two calls must not
        // both find no worker and start two.
        lock (_starting)
        {
            // Signed out, or no collection open: there is nothing to send, and nobody to send it as. A worker
            // still running is for a shelf the app is not showing, so it is stopped.
            if (!_library.IsOpen || !_auth.IsLoggedIn)
            {
                StopWorker();
                return;
            }

            var db = _library.Db;

            if (_worker is { IsCompleted: false } && ReferenceEquals(_on, db))
                return;

            StopWorker();

            _stop = _life.Linked();
            var ct = _stop.Token;

            _on = db;

            // A different collection gives a different cut, and a different account has a different store.
            // What was sent last says nothing about either.
            _pushed = null;
            _pictured.Clear();

            // Refused once the library is about to close; the token is cancelled by that close as well.
            _worker = _life.Run(() => RunAsync(db, ct));
        }
    }

    private void StopWorker()
    {
        try { _stop?.Cancel(); } catch { /* going away */ }
        _stop?.Dispose();
        _stop = null;
        _on = null;
    }

    private async Task RunAsync(LibraryDatabase db, CancellationToken ct)
    {
        try
        {
            // Wait for the measuring to settle before the first cut. The wait is skipped if someone asks
            // for a cut during that minute. A press is answered with whatever has been measured so far,
            // because the list it produced is already on screen.
            var woke = await _settled.SettleAsync(Settle, ct);

            while (!ct.IsCancellationRequested)
            {
                await PushIfItMovedAsync(db, woke, ct);
                woke = await _settled.WaitAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The library closed, the account changed, or the app is closing. Nothing is left half-written,
            // because one cut is sent in one call.
        }
    }

    /// <param name="woke">Why this cut is being taken. A request carries one more fact than a change does.
    /// The press that raised it has already moved the mood window. So the session's saved cut is the shelf
    /// as it was before the press, and it must not be sent as the answer.</param>
    private async Task PushIfItMovedAsync(LibraryDatabase db, Woke woke, CancellationToken ct)
    {
        try
        {
            // The first push uses the session's own cut, which the peer catalogue also uses. Two builds can
            // differ, because the mood window moves, rows change, and the styles sweep finishes. If they
            // differed, a phone and a borrowed shelf would show two different lists with the same name.
            // After the first push the cut is taken again and stored in the session's place, so both read
            // one cut. This runs off the UI thread, because building a cut queries every offered list.
            var cut = _pushed is null && woke != Woke.AtOnce
                ? db.SessionMoodLists
                : db.RecutMoodLists();

            if (cut.Lists.Count == 0)
            {
                // Nothing is measured yet, or nothing is in the cloud to name. Sending an empty cut would
                // replace a good cut, from an earlier launch or from earlier in the session, with nothing.
                _log.LogInformation("No generated lists to send yet.");
                return;
            }

            if (_pushed is not null && MoodListPush.Same(_pushed, cut))
                return;

            // The lists are named and placed here, on the way out, and not where the cut is built. The
            // names and the tree are in the interface layer, and the records are in Core. If the phone
            // wrote the names a second time, the copies would drift apart. So the sender sets each list's
            // name and position, and the phone only renders what it receives. See PublishedMoodLists, which
            // the peer catalogue also uses.
            var dressed = PublishedMoodLists.Dressed(cut);

            var stamped = await _cloud.PutMoodListsAsync(dressed, ct);

            // The push was cancelled while the request was in flight, because a second collection was
            // opened or the account was signed out. Recording the cut now would store a note about this
            // shelf against the next one. Start has already cleared that. The bytes are in the cloud either
            // way.
            ct.ThrowIfCancellationRequested();

            _pushed = cut;
            _log.LogInformation(
                "Sent {Lists} generated lists to the cloud, stamped {Stamp}.", cut.Lists.Count, stamped);

            // Pictures are sent after the lists, because the lists are what the user is waiting to see. If
            // the lists were sent and this step then fails, the phone is still better off than before.
            await SendPicturesAsync(dressed, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The phone keeps the cut it already has. That cut is stamped, so it can show its age. This is
            // better than an empty shelf. The next change to the library sends a new cut.
            _log.LogWarning(ex, "Could not send the generated lists.");
        }
    }

    /// <summary>
    /// The picture that each list shows, drawn here and sent to the cloud.
    ///
    /// <para><b>This computer is the only one that can draw them.</b> A generated list's cover is a drawn
    /// figure. There are eight families, and the colours are chosen for what the list means. The figure is
    /// drawn with Skia, using the app's own palette. The phone has neither. Without this step the phone
    /// showed the cover of whichever record the list opened on. So a list named Adrenaline showed
    /// somebody's album cover, and the shelf lost the thing that made each list easy to recognise: its
    /// colours.</para>
    ///
    /// <para><b>Once per list.</b> A picture depends on what the list is, not on what it currently
    /// contains. The id is made the same way. So a list already in the cloud has the right picture, and
    /// sending it again only costs bytes. This is asked of the store the first time this session meets a
    /// list. It is not remembered across launches, because what the store holds is the fact that matters. A
    /// note kept on this disk would be wrong the first time the user signed in on another computer.</para>
    ///
    /// <para><b>Nothing waits for this, and nothing breaks without it.</b> A list whose picture has not
    /// arrived keeps the picture it had before, and the next push tries again. One failure does not stop
    /// the others, because the pictures do not depend on each other.</para>
    /// </summary>
    private async Task SendPicturesAsync(MoodListsPayload cut, CancellationToken ct)
    {
        var sent = 0;

        foreach (var list in cut.Lists)
        {
            ct.ThrowIfCancellationRequested();

            if (list.Id is not { Length: > 0 } id)
                continue;

            if (_pictured.Contains(id))
                continue;

            if (!Enum.TryParse<SmartPlaylistKind>(list.Kind, ignoreCase: false, out var kind))
                continue;   // This build cannot draw this kind. The list itself was still sent.

            try
            {
                // A picture stored under the current rule is up to date. A picture stored before the rule
                // changed may be a drawn figure, where the list now uses its photograph. Such a picture is
                // replaced once.
                if (await _cloud.GetPlaylistArtStoredAtAsync(CloudShelf.Own, id, ct) is { } storedAt
                    && storedAt >= PicturesFromMs)
                {
                    _pictured.Add(id);
                    continue;
                }

                var listId = new SmartPlaylistId(kind, list.Genre, list.Decade);
                var tint = SmartPlaylistFace.Tint(listId);

                // The picture shown behind the list's frame (GeneratedCover.BehindAsync). It is the
                // photograph shipped for the list, where the list has one. The frame itself is not sent.
                // The phone and the television draw their own frame over the picture.
                var bytes = await Services.Art.GeneratedCoverService.CoverAsync(listId, tint, ct);

                if (bytes is not { Length: > 0 })
                {
                    var (style, seed) = SmartPlaylistFace.Cover(listId);

                    // No mark. An advisory label is a claim about the music, and no one has made such a
                    // claim about a list that the app built itself.
                    var drawn = await Task.Run(() => PlaylistArt.Figure(style, seed, CoverMark.None, tint), ct);
                    bytes = drawn.Cover;
                }

                // The palette has not loaded, or there is no Skia to draw with. In both cases the list
                // keeps its old picture, and the next push tries again.
                if (bytes is not { Length: > 0 })
                    continue;

                await _cloud.PutPlaylistArtAsync(id, bytes, ct);
                _pictured.Add(id);
                sent++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Could not send the picture for {List}.", list.Title ?? list.Kind);
            }
        }

        if (sent > 0)
            _log.LogInformation("Sent {Sent} generated list pictures.", sent);
    }

    /// <summary>The time from which list pictures use the photograph where a list has one. A list's
    /// picture stored before this time is sent again, once. A picture stored after it is left alone.
    /// </summary>
    private static readonly long PicturesFromMs =
        new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    public void Dispose()
    {
        StopWorker();
        _settled.Dispose();
    }
}
