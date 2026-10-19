using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using RPM.Core.Audio;
using RPM.Core.Diagnostics;
using RPM.Core.Models;
using RPM.Core.Radio;
using RPM.Desktop.Services;
using Microsoft.UI.Dispatching;

namespace RPM.Desktop.Presentation.RPaMp.Models;

/// <summary>
/// The RPaMp brain: a Winamp-style transport, a 10-band EQ and a reorderable playlist, all driven
/// through the shared <see cref="PlaybackService"/>. The app owns its audio pipeline
/// (ffmpeg → the app's EQ → OpenAL), so pause, resume, volume and EQ all act on the running sound
/// and nothing is stopped and restarted. The model moves to the next track when a track ends.
/// </summary>
[RpmBindable]
public sealed partial class RPaMpModel : INotifyPropertyChanged, IDisposable
{
    private readonly PlaybackService _playback;

    /// <summary>The collection that the library pane lists. It is required and not optional: the pane
    /// is part of the player, and a player given no collection would show an empty pane with no
    /// explanation.</summary>
    private readonly LibraryService _library;
    private readonly EqService _eq;

    /// <summary>The tuner, asked whether a station is on. The constructor explains why it is required
    /// and not a hook that can be left unset.</summary>
    private readonly IRadioTuner _tuner;
    private readonly SettingsService? _settings;
    private readonly SessionService? _session;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _positionTimer;
    private readonly DispatcherQueueTimer _shapeDebounce;
    private readonly Random _rng = new();

    private int _currentIndex = -1;

    /// <summary>The number on the slider. It starts from the player's volume and not from a fixed
    /// figure, see the constructor.</summary>
    private int _volume;
    private bool _shuffle;
    private bool _repeat;
    private bool _scanningCast;
    private string? _castError;

    /// <summary>Set while the sliders are written from the service (a curve arriving with a speaker,
    /// or the settings page moving the same knobs). Filling them is then not read as the user grabbing
    /// them, and nothing is written back.</summary>
    private bool _syncingEq;

    /// <param name="tuner">Whether a station is on, asked whenever it matters and not inferred from
    /// the stream. It is required and not an optional hook with a null default, because the transport
    /// is wrong without it: a bar that offers to rewind live radio is a control that does nothing, and
    /// a default of "no station is on" produces exactly that bar. See
    /// <see cref="IsLiveStream"/>.</param>
    public RPaMpModel(PlaybackService playback, EqService eq, DispatcherQueue dispatcher,
                      IRadioTuner tuner, LibraryService library,
                      SettingsService? settings = null, SessionService? session = null)
    {
        _playback = playback;
        _library = library;
        _eq = eq;
        _dispatcher = dispatcher;
        _tuner = tuner;
        _settings = settings;
        _session = session;

        // The screen setting for a television cast, from the settings file. The playback service
        // defaults to "on" and does not know the settings file, so this is where the two are joined.
        // It is safe here because a cast can only be started from a menu bound to this model, so the
        // model exists before any receiver does.
        if (_settings is not null) _playback.CastScreenOn = _settings.CastScreenOn;

        // The cast format the listener last chose. It is kept on the model and is passed to the
        // playback service only when a cast starts or a switch is flipped, so building the model while
        // a cast is running does not restart the cast in another format.
        _castFormatChoice = CastFormatFor(_settings?.CastFormat ?? CastAudioFormat.Mp3, speaker: null, ShowCastSurround);

        // The slider starts at the level the sound is actually at, not at a fixed number. The player
        // is built on first need (a track played from the library, or a transport bar bound), and by
        // then it has a volume: the app's own gain, or the level at which a Bluetooth speaker or a
        // cast room was found. A fixed figure here would open the slider showing something untrue,
        // and the listener would have to move it before the app agrees with itself.
        _volume = playback.Volume;

        Bands = new Common.EqBandList(
            PlaybackService.EqBands.Select((hz, i) => new EqBand(hz, v => OnBandChanged(i, v))));

        // The curve lives in the service. It belongs to the speakers and not to this window, and the
        // settings page turns the same knobs. This skin is one view of it.
        _eq.Changed += OnEqChanged;
        SyncEq();

        PlayPauseCommand = new RelayCommand(_ =>
        {
            if (IsPlaying) Pause();   // actively playing → real pause (track stays loaded)
            else TogglePlay();        // paused → resume; stopped → start
        });
        // Stop empties the queue as well as silencing the player. The queue is built by playing (a
        // track and the rest of its record), so stopping ends it and does not leave a stale running
        // order behind. Playing again builds a new queue.
        StopCommand = new RelayCommand(_ => ClearPlaylist());
        PrevCommand = new RelayCommand(_ => Step(-1, "previous pressed"));
        NextCommand = new RelayCommand(_ => Step(+1, "next pressed"));
        ClearCommand = new RelayCommand(_ => ClearPlaylist());
        RemoveSelectedCommand = new RelayCommand(_ => RemoveCurrentSelection());
        ShuffleCommand = new RelayCommand(_ => Shuffle = !Shuffle);
        RepeatCommand = new RelayCommand(_ => Repeat = !Repeat);
        VolumeDownCommand = new RelayCommand(_ => Volume -= 5);
        VolumeUpCommand = new RelayCommand(_ => Volume += 5);
        EqToggleCommand = new RelayCommand(_ => EqOn = !EqOn);
        EqResetCommand = new RelayCommand(_ => ResetEq());
        LikeCommand = new RelayCommand(_ => Like());
        DislikeCommand = new RelayCommand(_ => TurnDown());

        _positionTimer = dispatcher.CreateTimer();
        _positionTimer.Interval = TimeSpan.FromMilliseconds(400);
        _positionTimer.IsRepeating = true;
        _positionTimer.Tick += (_, _) =>
        {
            RaisePosition();
            RememberPosition();   // where the needle is, kept for the next run
        };

        _shapeDebounce = dispatcher.CreateTimer();
        _shapeDebounce.Interval = TimeSpan.FromMilliseconds(30);   // only merges rapid slider changes
        _shapeDebounce.IsRepeating = false;
        _shapeDebounce.Tick += (_, _) =>
        {
            _playback.SetVolume(_volume);
            RememberVolume();
        };

        _playback.Changed += OnPlaybackChanged;
        _playback.TrackEnded += OnTrackEnded;
        _playback.TrackChained += OnTrackChained;
        _playback.Stopped += OnStopped;
        _playback.CastChanged += OnCastChanged;
        _playback.CastFailed += OnCastFailed;

        // Next and previous pressed on the speaker itself, or spoken to it. They do the same as the
        // buttons in the app and use the same code path: shuffle, repeat and a DJ set all behave as
        // they do here, because only one place decides what follows a track.
        _playback.RemoteNext += () => _dispatcher.TryEnqueue(() => Step(+1, "next from the media keys or a speaker"));
        _playback.RemotePrevious += () => _dispatcher.TryEnqueue(() => Step(-1, "previous from the media keys or a speaker"));

        // The heart at the television. It is written through the library, as the app's own heart is.
        _playback.RemoteFavorite += ToggleCastFavorite;

        // Wherever the level is held for us, the slider follows it: a cast room turned on the device
        // or in the Home app, a Bluetooth speaker turned by its own buttons or by the machine's volume
        // keys, or the level that either one already had when the music arrived. The value is adopted
        // and not assigned, see AdoptVolume, because a number that came from the speaker must not be
        // sent back to it.
        _playback.RemoteVolume += volume => _dispatcher.TryEnqueue(() => AdoptVolume(volume));

        // What a speaker can decode, stored the first time it reports it, see CastDeviceProfile.
        _playback.CastCapabilitiesKnown += (device, caps) =>
            _dispatcher.TryEnqueue(() => RememberCastDevice(device, caps));

        // Each set of speakers keeps its own volume. It is set before the first track, so that
        // starting the app on the endpoint it was closed on opens at that endpoint's volume and not at
        // a number that nobody chose.
        _playback.OutputDeviceChanged += RestoreVolumeFor;
        RestoreVolumeFor(_playback.OutputDeviceName);
        _playback.PlaybackStalled += OnPlaybackStalled;
        Playlist.CollectionChanged += OnPlaylistCollectionChanged;

        // Every change to the running order is stored, so the app opens on the queue it was closed
        // with, see RememberQueue. This is a separate subscription and not a line in the handler
        // above, which only handles drag-reorders.
        Playlist.CollectionChanged += (_, _) => RememberQueue();
    }

    // ── playlist ──

    public ObservableCollection<PlaylistTrack> Playlist { get; } = new();

    private PlaylistTrack? _selected;
    public PlaylistTrack? SelectedTrack
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) return;
            if (_selected is not null) _selected.IsSelected = false;
            _selected = value;
            if (_selected is not null) _selected.IsSelected = true;
            OnChanged();
            OnChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => _selected is not null;

    public bool HasTracks => Playlist.Count > 0;

    /// <summary>Puts tracks at the end of the queue. <paramref name="startPlaying"/> is the difference
    /// between Play and Add: when it is true the first new track starts, and when it is false nothing
    /// about what is playing changes.</summary>
    public void AddTracks(IEnumerable<TrackInfo> tracks, bool startPlaying)
    {
        ForgetSource();
        NoteQueueIsTheirs();
        int firstNew = Playlist.Count;
        foreach (var t in tracks)
            Playlist.Add(new PlaylistTrack(t, radio: null));
        Renumber();
        OnChanged(nameof(HasTracks));
        OnChanged(nameof(TotalTimeText));
        // When asked to play, it plays, whatever was loaded before. Every Play button in the app means
        // "play these", so it must not mean "play, unless something is already playing, and then add
        // quietly". Callers that mean "put these in the queue" pass false, which is the whole
        // difference between the two.
        if (startPlaying && firstNew < Playlist.Count)
            PlayIndex(firstNew, "tracks added with play");
    }

    /// <summary>Queues tracks to play right after the current one, without interrupting it (the "play
    /// next" action). With nothing playing it is a plain append. Inserting after the current index
    /// never shifts it, so the playing track keeps playing.</summary>
    public void InsertNext(IEnumerable<TrackInfo> tracks)
    {
        var items = tracks.Select(t => new PlaylistTrack(t, radio: null)).ToList();
        if (items.Count == 0) return;

        NoteQueueIsTheirs();

        int at = _currentIndex >= 0 && _currentIndex < Playlist.Count ? _currentIndex + 1 : Playlist.Count;
        for (int i = 0; i < items.Count; i++)
            Playlist.Insert(at + i, items[i]);

        // Something else follows the playing track now, so the track that was opened to follow it
        // is no longer the right one.
        _playback.ClearChain();

        Renumber();
        OnChanged(nameof(HasTracks));
        OnChanged(nameof(TotalTimeText));
    }

    public void PlayTrack(PlaylistTrack track)
    {
        var i = Playlist.IndexOf(track);
        if (i >= 0) PlayIndex(i, "a queue row played");
    }

    /// <summary>Plays the track at this position, leaving the queue's order alone.</summary>
    public void PlayAt(int index) => PlayIndex(index, "a queue row played where it stands");

    /// <summary>Grid "play" audition: make this track the now-playing track, replacing the current
    /// one. A track that is already queued is jumped to. A new track replaces the current slot (so
    /// auditioning grid rows does not make the playlist grow), or is appended when nothing is loaded
    /// yet.</summary>
    public void PlayNow(TrackInfo track)
    {
        var existing = Playlist.FirstOrDefault(t => t.FilePath == track.FilePath);
        if (existing is not null) { PlayTrack(existing); return; }

        var item = new PlaylistTrack(track, radio: null);
        if (_currentIndex >= 0 && _currentIndex < Playlist.Count)
        {
            Playlist[_currentIndex] = item;          // replace the current slot in place
            PlayIndex(_currentIndex, "played now in the current row's place");
        }
        else
        {
            Playlist.Add(item);
            Renumber();
            OnChanged(nameof(HasTracks));
            PlayIndex(Playlist.Count - 1, "played now");
        }
        OnChanged(nameof(TotalTimeText));
    }

    /// <summary>
    /// Playing a track by hand: the queue becomes that track and the rest of its album behind it.
    ///
    /// <para>Every manual play rebuilds the queue. Picking a track out of an album means "play the
    /// record from here", not "add one song to whatever was queued". Nothing before the chosen track
    /// survives, and neither does the previous queue.</para>
    /// </summary>
    public void PlayAlbumFrom(IReadOnlyList<TrackInfo> album, int startIndex)
    {
        ArgumentNullException.ThrowIfNull(album);
        if (album.Count == 0 || startIndex < 0 || startIndex >= album.Count)
            return;

        // The queue that this track stood in is gone, and so is any offer to carry on inside it. This
        // is dropped before the list is touched, so the writes that clearing the list sets off do not
        // carry the old position.
        ForgetResume();
        ForgetSource();

        Playlist.Clear();
        for (int i = startIndex; i < album.Count; i++)
            Playlist.Add(new PlaylistTrack(album[i], radio: null));

        _currentIndex = -1;
        Renumber();
        OnChanged(nameof(HasTracks));
        OnChanged(nameof(TotalTimeText));
        PlayIndex(0, "a record played from a track");
    }

    /// <summary>Brings a queued track to the front of the queue and plays it (the "play now" action of
    /// the queue's right-click menu). Everything it jumped over keeps its order behind it, so the
    /// queue shows what is about to play and not where the track used to be.</summary>
    public void MoveToTopAndPlay(int index)
    {
        if (index < 0 || index >= Playlist.Count)
            return;

        if (index > 0)
        {
            var item = Playlist[index];
            Playlist.RemoveAt(index);
            Playlist.Insert(0, item);
        }
        _currentIndex = 0;
        Renumber();
        PlayIndex(0, "a queue row moved to the top and played");
    }

    /// <summary>Takes tracks out of the queue by position (the multi-select removal of the queue
    /// list). Removing the playing track stops playback. Removing anything above it keeps it playing
    /// and only shifts its index.</summary>
    public void RemoveAt(IEnumerable<int> indexes)
    {
        var plan = RPM.Core.Playback.QueueEdit.PlanRemoval(Playlist.Count, _currentIndex, indexes);
        if (plan.Removals.Count == 0)
            return;

        foreach (var i in plan.Removals)   // highest first, so the rest keep their positions
            Playlist.RemoveAt(i);

        _playback.ClearChain();   // the track that was prepared to follow may be the one just removed

        _currentIndex = plan.CurrentIndex;
        if (plan.RemovedCurrent)
            StopPlayback();

        Renumber();
        OnChanged(nameof(HasTracks));
        OnChanged(nameof(TotalTimeText));
        RaiseNowPlaying();
    }

    private void ClearPlaylist()
    {
        StopPlayback();
        Playlist.Clear();
        _currentIndex = -1;
        Renumber();
        OnChanged(nameof(HasTracks));
        OnChanged(nameof(TotalTimeText));
        RaiseNowPlaying();
    }

    private void RemoveCurrentSelection()
    {
        if (_selected is not { } sel) return;
        var i = Playlist.IndexOf(sel);
        if (i >= 0)
            RemoveAt([i]);
    }

    // The playlist ListView reorders the ObservableCollection in place (drag-drop). Recover the
    // current-track pointer from the IsCurrent flag afterwards and renumber the gutter.
    private void OnPlaylistCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Move) return;

        // Dragged by hand during a set: the queue now belongs to the listener, and the DJ's own
        // running order is no longer written over it. This is ignored when the move is this class
        // applying that order.
        NoteQueueIsTheirs();

        // The order changed under the playing track, so the track that was opened to follow it is no
        // longer the one that follows.
        _playback.ClearChain();

        var current = Playlist.FirstOrDefault(t => t.IsCurrent);
        _currentIndex = current is null ? -1 : Playlist.IndexOf(current);
        Renumber();
        OnChanged(nameof(TotalTimeText));
    }

    private void Renumber()
    {
        for (int i = 0; i < Playlist.Count; i++)
        {
            Playlist[i].Index = i + 1;
            Playlist[i].IsCurrent = i == _currentIndex;
        }
    }

    public string TotalTimeText
    {
        get
        {
            var total = TimeSpan.FromSeconds(Playlist.Sum(t => t.DurationSeconds));
            return total.TotalHours >= 1
                ? $"{(int)total.TotalHours}:{total.Minutes:00}:{total.Seconds:00}"
                : $"{total.Minutes}:{total.Seconds:00}";
        }
    }

    // ── transport ──

    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand PrevCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand RemoveSelectedCommand { get; }
    /// <summary>The two keys that say whether what is playing belonged where the list put it. They
    /// are not the heart, which is a different fact in a different column. See RPaMpModel.Verdict
    /// for what they write and where it is acted on.</summary>
    public RelayCommand LikeCommand { get; }

    public RelayCommand DislikeCommand { get; }

    public RelayCommand ShuffleCommand { get; }
    public RelayCommand RepeatCommand { get; }
    public RelayCommand VolumeDownCommand { get; }
    public RelayCommand VolumeUpCommand { get; }
    public RelayCommand EqToggleCommand { get; }
    public RelayCommand EqResetCommand { get; }

    /// <summary>Resumes a paused track, or starts the current or first one. The audio engine handles
    /// pause, so there is nothing to remember and relaunch.</summary>
    private void TogglePlay()
    {
        if (_playback.IsPaused) { _playback.Resume(); return; }
        if (_playback.PlayingPath is not null) return;   // already playing
        if (_currentIndex >= 0) PlayIndex(_currentIndex, "play pressed");
        else if (Playlist.Count > 0) PlayIndex(0, "play pressed");
    }

    private void Pause() => _playback.Pause();

    private void StopPlayback() => _playback.Stop();   // the deck comes off on Stopped, whoever stopped it

    // ── the deck ──

    /// <summary>
    /// The record on the deck: what the player has put on, from the moment it hands the track to the
    /// engine until playback stops.
    ///
    /// <para>This is not the engine's own account of what is sounding. A queue advances on the event
    /// that says the last track ended, so for a few milliseconds between the two the engine is
    /// playing nothing. Anything that follows the engine would read that as "the music stopped" and
    /// tear itself down, just before the next track builds it all again. The artwork and the mini
    /// player follow this property instead, and see one continuous run of records.</para>
    ///
    /// <para>It is also not the queue's cursor (<c>_currentIndex</c>), which survives a stop on
    /// purpose so that pressing play again continues where the listener left off. The deck is empty
    /// as soon as playback ends.</para>
    /// </summary>
    public PlaylistTrack? Deck { get; private set; }

    /// <summary>Raised when a record goes on the deck, or comes off it.</summary>
    public event Action? DeckChanged;

    private void SetDeck(PlaylistTrack? track)
    {
        if (ReferenceEquals(Deck, track))
            return;
        Deck = track;
        DeckChanged?.Invoke();

        // A song radio's row on the deck asks that radio for the songs after it. This is done here
        // because every way that a row goes on the deck passes through here: a start (PlayIndex) and a
        // gapless changeover (OnTrackChained). See RPaMpModel.Radio.
        if (track?.Radio is { } radio)
            TopUpRadio(track, radio);
    }

    /// <param name="why">What moved the queue here, for the <c>queue</c> ring channel. It is required,
    /// so that moves nobody asked for (a track ending, a start that failed) can be told apart from
    /// button presses.</param>
    private void PlayIndex(int index, string why)
    {
        if (index < 0 || index >= Playlist.Count) return;
        _currentIndex = index;
        _setJustEnded = false;   // whatever ends next is this track ending, not the mix
        var t = Playlist[index];
        Said.Line("queue", $"row {index + 1} of {Playlist.Count}, '{Path.GetFileName(t.FilePath)}': {why}");
        Renumber();
        SetDeck(t);

        // Where the needle was when the app was last closed, used only for the track it was in, see
        // RestoreQueue. The record is named as well as its place in the queue, because the place
        // alone does not identify the record: a queue built by hand puts whatever was clicked at the
        // top, and matching on the number alone would put the needle a minute into a song that nobody
        // had been playing. It is spent on the first play either way: the offer is "carry on from
        // where you were", and it ends as soon as the listener plays anything.
        var start = index == _resumeIndex
                 && string.Equals(t.FilePath, _resumePath, StringComparison.Ordinal)
            ? _resumeSeconds
            : 0;
        ForgetResume();

        // Not every track is a file: a track that was offloaded, or that belongs to another of the
        // user's installs, is streamed. StartTrackAsync decides which of the two it is. The deck above
        // already shows the record either way, so nothing on screen waits for a signature.
        _ = StartTrackAsync(t, start);
        RaiseNowPlaying();
        RememberQueue();
    }

    // ── the queue across restarts ──

    /// <summary>Which queue position the stored needle belongs to, which record was in that position,
    /// and how far into it. The first <see cref="PlayIndex"/> spends all three. -1 means there is
    /// nothing to resume.</summary>
    private int _resumeIndex = -1;
    private string? _resumePath;
    private double _resumeSeconds;

    /// <summary>Drops the stored needle. It is called when the needle is spent, and whenever the queue
    /// it pointed into stops existing. A queue rebuilt by hand is the listener saying what to play,
    /// and the offer to carry on from last time does not survive it.</summary>
    private void ForgetResume()
    {
        _resumeIndex = -1;
        _resumePath = null;
        _resumeSeconds = 0;
    }

    /// <summary>The position last written down, so a playing track does not rewrite the note at every
    /// tick of the clock.</summary>
    private double _rememberedSeconds = -1;

    /// <summary>
    /// Puts back the queue the app was closed on, with the needle where it was left, and plays
    /// nothing.
    ///
    /// <para>An app that starts making noise on its own is not trusted to open. So the record is put
    /// on the queue and the needle is where the listener left it, and both wait for play to be
    /// pressed. Whatever is already loaded wins: a restore that arrives after something has started
    /// (a file the app was launched with, a disc that went in) has been overtaken and does
    /// nothing.</para>
    ///
    /// <para>The tracks are handed over already resolved. The caller knows the library, and what
    /// could not be resolved is not in the list. See
    /// <see cref="SessionRestore.Queue{T}"/>.</para>
    /// </summary>
    public void RestoreQueue(IReadOnlyList<TrackInfo> tracks, int index, double seconds)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count == 0 || IsLoaded || Playlist.Count > 0)
            return;

        // Filling the list raises a change for each track, and each change would write a queue that
        // is still half built, with a cursor of -1. The note is made once, at the end, when there is
        // something true to write down.
        _restoringQueue = true;
        try
        {
            // A restored queue is a plain queue: a radio does not outlive the run of the app it was started in.
            foreach (var track in tracks)
                Playlist.Add(new PlaylistTrack(track, radio: null));

            _currentIndex = index >= 0 && index < Playlist.Count ? index : -1;
            _resumeIndex = _currentIndex;
            _resumePath = _currentIndex >= 0 ? Playlist[_currentIndex].FilePath : null;
            _resumeSeconds = _currentIndex >= 0 && double.IsFinite(seconds) && seconds > 1 ? seconds : 0;
        }
        finally
        {
            _restoringQueue = false;
        }
        RememberQueue();

        Renumber();
        OnChanged(nameof(HasTracks));
        OnChanged(nameof(TotalTimeText));
        RaiseNowPlaying();
    }

    /// <summary>Set while the stored queue is being restored, so that the half-filled list is not
    /// written over the note it is being built from.</summary>
    private bool _restoringQueue;

    /// <summary>
    /// Writes the queue down: the running order, which track the needle is on and how far into it.
    ///
    /// <para>The paths are absolute, because that is what the player holds. Turning them back into
    /// records is the reader's job, and only the library can do it (see
    /// <see cref="Presentation.Main.MainPage"/>'s restore). A track whose file has gone, or that
    /// belonged to a library that this machine no longer uses, does not come back.</para>
    /// </summary>
    private void RememberQueue()
    {
        if (_session is not { } session || _restoringQueue)
            return;

        var paths = new List<string>(Math.Min(Playlist.Count, SessionRestore.MaxQueue));
        foreach (var track in Playlist.Take(SessionRestore.MaxQueue))
            paths.Add(track.FilePath);

        var current = _currentIndex >= 0 && _currentIndex < Playlist.Count
            ? Playlist[_currentIndex].FilePath
            : null;

        // A queue that was restored and not yet played holds its resume point and not the engine's
        // zero. Writing the queue again, which the restore itself does because it fills the list,
        // must not lose the position that it was restored for.
        _rememberedSeconds = IsLoaded ? _playback.PositionSeconds
                           : _resumeIndex >= 0 ? _resumeSeconds
                           : 0;
        session.SetQueue(paths, _currentIndex, current, _rememberedSeconds);
    }

    /// <summary>The position of the needle, written down every few seconds while a record plays. This
    /// is often enough that closing the app mid-track returns to the same place, and rare enough that
    /// the queue is not rewritten twice a second for the length of a side.</summary>
    private void RememberPosition()
    {
        if (_session is null || !IsLoaded)
            return;
        var now = _playback.PositionSeconds;
        if (Math.Abs(now - _rememberedSeconds) < 5)
            return;
        RememberQueue();
    }

    /// <param name="why">What asked for the step, passed on to <see cref="PlayIndex"/>.</param>
    private void Step(int direction, string why)
    {
        // In a DJ set these buttons drive the mix and not the queue. Next brings the following record
        // in now, cued, tempo-matched and faded, so that it sounds like an early change and not like a
        // cut. Previous moves the needle back to the start of the record that is playing.
        if (IsDjMixing && DjTransport?.Invoke(direction) == true)
            return;

        if (Playlist.Count == 0) return;
        int next;
        if (_shuffle && Playlist.Count > 1)
        {
            do { next = _rng.Next(Playlist.Count); } while (next == _currentIndex);
        }
        else
        {
            next = _currentIndex + direction;
            if (next < 0) next = Playlist.Count - 1;
            if (next >= Playlist.Count) next = 0;
        }
        PlayIndex(next, why);
    }

}
