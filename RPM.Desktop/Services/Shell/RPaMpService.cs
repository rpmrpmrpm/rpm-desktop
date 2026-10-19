using RPM.Core.Models;
using RPM.Desktop.Presentation.RPaMp;
using Microsoft.UI.Dispatching;

using RPM.Core.Library;

namespace RPM.Desktop.Services.Shell;

/// <summary>
/// Owns the single RPaMp player and its persistent playlist.
///
/// <para>The skin is one of the app's three views (see <see cref="ViewModeService"/>), and it is shown in the
/// app's own window. The shell mounts <see cref="View"/> over itself in the same way as it mounts Pegasus,
/// and takes it down when the view is left. This service does not own a window.</para>
///
/// <para><b>Why it owns no window.</b> A view whose home is a second window raises a question that no view
/// should have: what happens to the first window. Hiding the main window is the only honest answer, and
/// neither way of doing it works on macOS. <c>orderOut:</c> on the app's main window ends the process on
/// Uno's host (cleanly, so there is nothing to catch). <c>miniaturize:</c> leaves the window for the system
/// to restore whenever the app is next activated, which puts the library back on screen under a view that
/// should have replaced it. One window that can show three things has none of these problems.</para>
///
/// <para>There is only one <see cref="RPaMpView"/> and one <see cref="RPaMpModel"/>: the queue, the EQ and
/// the playing track are the same objects whichever view is up, so a record carries on across a change of
/// view.</para>
/// </summary>
public sealed class RPaMpService
{
    private readonly PlaybackService _playback;
    private readonly EqService _eq;
    private readonly DjService _dj;
    private readonly LibraryService _library;
    private readonly DispatcherQueue _dispatcher;
    private RPaMpModel? _model;
    private readonly SettingsService _settings;
    private readonly SessionService _session;

    /// <summary>Asked before a set is started, and only then. The answer is not cached here: support bought
    /// in the browser reaches this service the next time it reads the account, and a set started after that
    /// must not be refused by a copy taken at startup.</summary>
    private readonly Account.SupporterService _supporter;

    /// <summary>Passed to the model, which asks it whether a station is on. It is taken here and not resolved
    /// on demand, so the transport is never built without one.</summary>
    private readonly RadioService _radio;
    private RPaMpView? _view;

    /// <summary>
    /// The user closed the skin (its close button).
    ///
    /// <para>RPaMp is a view (see <see cref="ViewModeService"/>), so this says only "I have been closed". The
    /// service on the other end decides that this means going back to the library, and puts this view away
    /// itself. It does not stop the music. Leaving a view changes what is on screen and not the record: the
    /// same track carries on in the deck of the library, because the two views show one transport.</para>
    /// </summary>
    public event Action? PutAway;

    public RPaMpService(PlaybackService playback, EqService eq, DjService dj, LibraryService library,
        DispatcherQueue dispatcher, SettingsService settings, SessionService session,
        RadioService radio, Account.SupporterService supporter)
    {
        _playback = playback;
        _eq = eq;
        _dj = dj;
        _library = library;
        _dispatcher = dispatcher;
        _settings = settings;
        _session = session;
        _radio = radio;
        _supporter = supporter;

        // The DJ runs on the audio thread, and the player's readout is bound. So everything the DJ reports
        // about the set comes back through the dispatcher.
        _dj.TrackChanged += track => _dispatcher.TryEnqueue(() => _model?.NoteDjTrack(track.Path));
        _dj.SetEnded += () => _dispatcher.TryEnqueue(() =>
        {
            _model?.EndDjSet();
#if DEBUG && RPM_SKIA
            try { Presentation.MixLab.MixLabWindow.Close(); } catch { }
#endif
        });
        _dj.ChangingOverChanged += changing => _dispatcher.TryEnqueue(() => _model?.NoteDjChangeover(changing));

        // Whether the next key has anything to mix into. It is false for a playlist that has never been
        // played, and true again as soon as the first record is read, which is usually during the opening
        // one.
        _dj.NextReadyChanged += ready => _dispatcher.TryEnqueue(() => _model?.NoteDjNextReady(ready));

        // The set's own running order, so the queue shows what is going to be played and not the list it was
        // built from. It is ignored once the listener has arranged the queue.
        _dj.RunningOrderChanged += order => _dispatcher.TryEnqueue(() => _model?.NoteDjOrder(order));

        // The set that cannot be mixed at all: no model here, none available in the cloud, and nothing in the
        // set already read. The queue stays, because those records still play, and the listener is told why
        // they are not mixed.
        _dj.MixUnavailable += () => _dispatcher.TryEnqueue(async () =>
        {
            _model?.EndDjSet();
            await SayMixUnavailableAsync();
        });
    }

    /// <summary>
    /// Tells the listener that the mix is off, once.
    ///
    /// <para>This is a window and not a line somewhere else: a set was asked for and will not happen, and the
    /// listener should not have to find that out from the music being different.</para>
    /// </summary>
    private async Task SayMixUnavailableAsync()
    {
        // The skin when it is shown, otherwise the app's own window: a set can be started from the library
        // grid without the player ever having been opened. With no window at all (a headless run),
        // `PromptDialog` answers without showing anything.
        var root = _view?.XamlRoot ?? App.AppWindow?.Content?.XamlRoot;

        try
        {
            await Presentation.Dialogs.PromptDialog.ShowAsync(
                root,
                Strings.Loc.Get("Dj_UnavailableTitle"),
                Strings.Loc.Get("Dj_UnavailableBody"),
                closeText: Strings.Loc.Get("Common_Ok"));
        }
        catch (Exception ex)
        {
            // A dialog that will not open (another one is already shown, or a window is being closed) must
            // not take the app down from a handler that nobody awaits. The set is off either way, and the log
            // says why.
            RPM.Core.Diagnostics.Said.Line("dj", $"could not say the mix is off: {ex.Message}");
        }
    }

    /// <summary>Adds tracks to the playlist. When <paramref name="play"/> is set, playback starts on the
    /// first added track (unless something is already playing). The player is made ready but is not opened: a
    /// new song plays in the rail, and the user opens the panel when they want it.</summary>
    public void AddTracks(IReadOnlyList<TrackInfo> tracks, bool play)
    {
        if (tracks.Count == 0) return;
        _dispatcher.TryEnqueue(() =>
        {
            var model = ShowCore(expand: false);
            model.AddTracks(tracks, startPlaying: play);
        });
    }

    /// <summary>
    /// Hands a set of tracks to the DJ and lets it mix them: the player shows the set as its queue and
    /// follows whichever record the mix is on, but the audio is one continuous mix and not a file at a time.
    /// It ends by itself when the last record plays out, and anything else that takes the player (a grid
    /// audition, Stop) cuts it short.
    /// </summary>
    public void StartDjSet(IReadOnlyList<TrackInfo> tracks)
    {
        if (tracks.Count == 0) return;
        _dispatcher.TryEnqueue(() =>
        {
            _model?.ForgetSource();
            StartDjCore(tracks);
        });
    }

    /// <summary>Says that the queue that was just filled holds one of the lists that the library writes
    /// itself. It is enqueued like every other order, so it lands after the fill it belongs to: see
    /// <c>RPaMpModel.NoteGenerated</c>, which explains why that order matters.</summary>
    public void NoteGenerated(SmartPlaylistId id) =>
        _dispatcher.TryEnqueue(() => _model?.NoteGenerated(id));

    /// <summary>
    /// "Start DJ here" on one track: the DJ takes that record and the rest of the album after it, and opens
    /// the set on the clicked one. The DJ orders what follows, but where a set begins is the listener's
    /// choice, so the clicked track goes in first. The planner takes the first record offered while nothing
    /// is playing yet.
    ///
    /// <para>The album is read from the folder of the track, the same as <see cref="PlayAlbumFrom"/>: a track
    /// right-clicked in the library grid, on an album page or on the wall starts the same set. A track with
    /// no album behind it is mixed on its own, which is a set of one and ends after it.</para>
    /// </summary>
    public void StartDjHere(TrackInfo track) => _dispatcher.TryEnqueue(() =>
        StartDjCore(RPM.Core.Library.AlbumTracklist.From(AlbumBehind(track), track)));

    private void StartDjCore(IReadOnlyList<TrackInfo> tracks)
    {
        if (tracks.Count == 0) return;

        // This is before anything is shown and before the DJ is given anything. Everything a set costs is on
        // the other side of this line: the crate is read, beat grids and keys are found for records that have
        // none, and a machine that cannot run the beat model sends windows to the cloud to have them found. A
        // gate on the mix coming out would spend all of that and then refuse.
        if (!Account.SupporterReach.For(_supporter.Standing).MayDj)
        {
            _ = Presentation.Dialogs.SupporterDialog.ShowAsync(
                _view?.XamlRoot ?? App.AppWindow?.Content?.XamlRoot,
                Presentation.Dialogs.SupporterDialog.Occasion.Refused(Strings.Loc.Get("Supporters_Dj")));
            return;
        }

        var model = ShowCore(expand: false);
        model.BeginDjSet(tracks);

#if DEBUG && RPM_SKIA
        // The workbench opens with the set, and before it: this is the one moment when it has anything to
        // show, and a tool that has to be remembered and opened by hand is not open on the changeover that
        // someone wanted to look at.
        //
        // Before, because opening it also tells the set to read its first records before playing them
        // (`DjService.HoldForAnalysis`), and a set that is already on its way would not be asked.
        try { Presentation.MixLab.MixLabWindow.OpenOrFocus(_dj, _playback); } catch { }
#endif

        _dj.Start(tracks);
    }

    /// <summary>
    /// "Start radio" on one song: it plays now, and the queue keeps filling with songs that are close in
    /// mood, era and genre to whichever song is playing (see <c>RPaMpModel.Radio</c> and
    /// <see cref="SongRadio"/>). Whatever was queued before is replaced, as with any other play.
    ///
    /// <para>A song that no collection here holds (a file opened from outside the library) has nothing to
    /// pick from, so it plays on its own.</para>
    /// </summary>
    public void StartRadio(TrackInfo track) => _dispatcher.TryEnqueue(() =>
    {
        var model = ShowCore(expand: false);
        if (Presentation.RPaMp.Models.SongRadioRun.Tune(track, _library) is { } radio)
            model.StartRadio(track, radio);
        else
            model.PlayAlbumFrom([track], 0);
    });

    /// <summary>Transport during a DJ set: the next and previous buttons drive the set and not the queue.
    /// False when no set is running, which is the cue for the player to do its ordinary thing.</summary>
    public bool DjSkipToNext() => _dj.SkipToNext();
    public bool DjRestartRecord() => _dj.RestartRecord();

    /// <summary>The scrubber during a set: the bar moves the needle on the record that is playing and not the
    /// engine, which has no file under it to reopen.</summary>
    public bool DjSeekTo(double seconds) => _dj.SeekTo(seconds);
    public bool DjCanSeek => _dj.CanSeek;

    /// <summary>Queues tracks to play right after the current one ("play next") without opening the player or
    /// interrupting what is playing.</summary>
    public void InsertNext(IReadOnlyList<TrackInfo> tracks)
    {
        if (tracks.Count == 0) return;
        _dispatcher.TryEnqueue(() =>
        {
            var model = ShowCore(expand: false);
            model.InsertNext(tracks);
        });
    }

    /// <summary>Grid "play" audition: make this the now-playing track, replacing whatever RPaMp was playing.
    /// It plays in the rail and the panel does not open over the collection.</summary>
    public void PlayNow(TrackInfo track) => _dispatcher.TryEnqueue(() =>
    {
        var model = ShowCore(expand: false);
        model.PlayNow(track);
    });

    /// <summary>
    /// Playing a track directly, from anywhere in the app: the queue becomes that track and the rest of its
    /// record, and whatever was queued before is removed.
    ///
    /// <para>The album is read from the folder of the track and not from the list it was clicked in, so the
    /// library grid, an album page and a wall card all build the same queue from the same track. A track with
    /// no album behind it (a file outside the library) falls back to playing only itself.</para>
    /// </summary>
    public void PlayAlbumFrom(TrackInfo track) => _dispatcher.TryEnqueue(() =>
    {
        var model = ShowCore(expand: false);
        var album = AlbumBehind(track);
        var at = album.FindIndex(t => string.Equals(t.FilePath, track.FilePath, StringComparison.Ordinal));
        if (at < 0)
            model.PlayAlbumFrom([track], 0);
        else
            model.PlayAlbumFrom(album, at);
    });

    /// <summary>The record a track belongs to, in the order it plays: disc 1 whole, then disc 2. Empty when
    /// the track is not in any collection, which is not a failure: it plays alone.</summary>
    /// <remarks>It reads whichever collection holds the track, not only the user's own: see
    /// <see cref="TrackHome"/>. A record played from a friend's shelf has a path that is in nobody's library
    /// here, and without this the album would become a queue of one track that stops at the end of
    /// it.</remarks>
    private List<TrackInfo> AlbumBehind(TrackInfo track)
    {
        if (TrackHome.For(track.FilePath) is not { } home)
            return [];

        // If there is no answer, the track plays on its own. If the user's own mirror refuses the read, the
        // problem screen is already shown over the app (`LibraryService.Read`).
        return _library.Read(home.Shelf, "the record behind a queued track", mirror => mirror.FolderTracks(home.Folder))
            .Match<List<TrackInfo>>(
                rows => [.. RPM.Core.Library.AlbumTracklist
                    .InDiscOrder(rows, t => t.DiscNumber, t => t.TrackNumber)
                    .Select(t => t.ToTrackInfo(home.Root))],
                _ => []);
    }

    /// <summary>Brings a queued track to the front and plays it ("play now" on a row of the queue).</summary>
    public void MoveToTopAndPlay(int queueIndex) => _dispatcher.TryEnqueue(() =>
    {
        var model = ShowCore(expand: false);
        model.MoveToTopAndPlay(queueIndex);
    });

    /// <summary>Plays a track that is already in the queue, where it stands (double-clicking a queued row).
    /// The running order is not changed.</summary>
    public void PlayQueueIndex(int queueIndex) => _dispatcher.TryEnqueue(() =>
    {
        var model = ShowCore(expand: false);
        model.PlayAt(queueIndex);
    });

    /// <summary>Makes these tracks the queue and starts the first one (playing a stored playlist from a track
    /// down). Whatever was queued is replaced, the same as playing by hand anywhere else.</summary>
    public void PlayTracksFrom(IReadOnlyList<TrackInfo> tracks)
    {
        if (tracks.Count == 0) return;
        _dispatcher.TryEnqueue(() =>
        {
            var model = ShowCore(expand: false);
            model.PlayAlbumFrom(tracks, 0);
        });
    }

    /// <summary>Takes tracks out of the queue by position (the removal of several selected rows in the queue
    /// list).</summary>
    public void RemoveFromQueue(IReadOnlyList<int> queueIndexes)
    {
        if (queueIndexes.Count == 0) return;
        _dispatcher.TryEnqueue(() => _model?.RemoveAt(queueIndexes));
    }

    /// <summary>
    /// The one skin, built on first need. The shell mounts it over itself while RPaMp is the view and takes
    /// it down when the view is left. Nothing else parents it.
    /// </summary>
    public RPaMpView View => EnsureView();

    /// <summary>
    /// Makes the player ready and returns its model.
    ///
    /// <para>It does not put anything on the screen. Which view is up is not decided by this service:
    /// starting a track does not mean "and show the skin", and saying so here would be a second opinion about
    /// what the window shows.</para>
    /// </summary>
    private RPaMpModel ShowCore(bool expand = true)
    {
        EnsureView();
        return _model!;   // EnsureView built it
    }

    /// <summary>App teardown: silence whatever is sounding.</summary>
    public void Shutdown() => _playback.Stop();

    /// <summary>The one player model, built on first need. It outlives every move: the skin and the page's
    /// own transport bar are both views onto it.</summary>
    private RPaMpModel EnsureModel()
    {
        if (_model is not null)
            return _model;

        // Settings supplies the volume that each set of speakers was last left at (see
        // `RPaMpModel.RestoreVolumeFor`), and the session supplies the queue itself, which is written down as
        // it changes so that the next run opens on it.
        _model = new RPaMpModel(_playback, _eq, _dispatcher, _radio, _library, _settings, _session)
        {
            // While a set is running, the transport belongs to the DJ (see `RPaMpModel.DjTransport`), and so
            // does the scrubber.
            DjTransport = direction => direction > 0 ? _dj.SkipToNext() : _dj.RestartRecord(),
            DjSeek = seconds => _dj.SeekTo(seconds),
            DjCanSeek = () => _dj.CanSeek,
        };
        return _model;
    }

    /// <summary>The shared player model, for a view that drives it directly: the transport bar of the review
    /// page binds its buttons to it. It ensures the model, so the bar works before the skin is ever
    /// opened.</summary>
    public RPaMpModel Transport => EnsureModel();

    private RPaMpView EnsureView()
    {
        var model = EnsureModel();
        if (_view is not null)
            return _view;

        var view = new RPaMpView
        {
            DataContext = model,
            RequestedTheme = ElementTheme.Dark,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,

            // The skin's own minimise and pin act on a window, and that window is the app's own: there is no
            // second one. Closing is the exception: it means leaving the view and not closing the app, which
            // is why it is reported and not obeyed (see `PutAway`).
            HostWindow = App.AppWindow?.AppWindow,
        };
        view.CloseRequested += () => PutAway?.Invoke();
        return _view = view;
    }
}
