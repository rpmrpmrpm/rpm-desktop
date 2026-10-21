using RPM.Fingerprint.Catalog;
using RPM.Core.Radio;

namespace RPM.Desktop.Services.Audio;

/// <summary>
/// The app's side of internet radio: the station directory, the user's presets, and tuning in a station.
///
/// <para>A station reaches the speakers through the same path as a mix. It is an <see
/// cref="RPM.Core.Audio.IPcmSource"/> handed to the player. So the equaliser, the visualiser, the volume
/// and casting to a speaker all work on the radio without knowing it is the radio.</para>
/// </summary>
public sealed partial class RadioService : IRadioTuner, IDisposable
{
    private readonly PlaybackService _playback;
    /// <summary>
    /// Directory answers are cached for 30 minutes. This is the time <c>RadioModel.ResultsGoodFor</c>
    /// allows a listing to stay valid. The rest of the cache keeps its data for 30 days.
    ///
    /// <para>"Most played" is the directory's play count over the last 24 hours. Cached for 30 days, the
    /// Dutch list would be a day old and in a different order from the phone's list, which is not cached.
    /// The <c>Vary</c> value puts these answers under a new key, so the rows cached for 30 days are not
    /// read again. When the directory cannot be reached, the last answer is still served (<see
    /// cref="RPM.Core.Net.HttpCacheHandler"/>).</para>
    /// </summary>
    private static readonly RPM.Core.Net.WebCachePolicy DirectoryPolicy =
        new("radio", TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(5)) { Vary = "directory-v2" };

    /// <summary>Only the directory goes through here. The audio has its own connection.</summary>
    private readonly HttpClient _http = AppCache.Client(DirectoryPolicy, RPM.Core.Net.Destination.Unaddressed, TimeSpan.FromSeconds(20));
    private readonly object _gate = new();

    private RadioStreamSource? _tuned;

    private readonly LibraryService _library;

    /// <summary>What the app has learned about how each station formats its track announcements. There is
    /// one instance for the whole app, so each station is learned once and stays known while the app
    /// runs.</summary>
    private readonly IcyOrders _orders;

    /// <summary>The artist names in the collection, folded for comparison. It is built the first time an
    /// announcement needs it, and dropped whenever the library changes. It is a set of a few thousand short
    /// strings, and it is asked about once a song.</summary>
    private HashSet<string>? _artists;

    /// <summary>
    /// Whether the collection has an artist with this name.
    ///
    /// <para>The name is squashed the same way as every other name in the app is compared. So "SHANIA
    /// TWAIN" on a station's readout matches "Shania Twain" on a shelf.</para>
    /// </summary>
    private bool KnowsArtist(string name)
    {
        HashSet<string>? known;
        lock (_gate)
        {
            if (_artists is null)
            {
                if (!_library.IsOpen)
                    return false;   // Library not open yet. The guess stands; the next song asks again

                _artists = new HashSet<string>(
                    _library.Shelf.ArtistNames().Select(a => RPM.Fingerprint.Catalog.NameMatch.Squash(a.Name)),
                    StringComparer.Ordinal);
            }

            known = _artists;
        }

        var key = RPM.Fingerprint.Catalog.NameMatch.Squash(name);
        return key.Length > 0 && known.Contains(key);
    }

    public RadioService(PlaybackService playback, LibraryService library)
    {
        _playback = playback;
        _library = library;
        Directory = new RadioBrowserClient(_http);
        Favorites = new RadioFavorites(Path.Combine(LocalData.Dir, "radio.json"), TimeProvider.System);

        // A --gallery run keeps its listening history beside its temporary library. The home screen lists
        // the most-played stations, and without this the user's own history would be the only real data on
        // the gallery's home screen. This is read in the constructor, which is safe only because App sets
        // up GalleryMode right after Host.Build and before any service is resolved.
        var listensDir = Presentation.Gallery.GalleryMode.Active
                         && Presentation.Gallery.GalleryMode.LibraryPath is { } galleryLibrary
            ? galleryLibrary
            : LocalData.Dir;
        Listens = new RadioListens(Path.Combine(listensDir, "radio-listens.json"));

        // Which half of an announcement is the musician is settled by asking the listener's own collection,
        // and then remembered per station. See IcyOrders. The collection is asked through a delegate rather
        // than handed over, so the rule stays in Core and can be tested.
        _orders = new IcyOrders(KnowsArtist);

        // The library was opened, closed or rescanned, so the set of names may be different now. The set is
        // dropped here, not rebuilt. The next announcement rebuilds it. A rescan is not a reason to read a
        // database that nobody is asking about yet.
        _library.Changed += () => { lock (_gate) _artists = null; };
        _library.Opened += _ => { lock (_gate) _artists = null; };

        // Anything that takes the player elsewhere (a track from the grid, Stop, a DJ set) ends listening
        // to the radio. Otherwise the station would keep streaming into an output that is playing something
        // else.
        _playback.Changed += OnPlaybackChanged;
    }

    public RadioBrowserClient Directory { get; }

    public RadioFavorites Favorites { get; }

    /// <summary>How much each station has been listened to. This is kept apart from the presets. A preset
    /// is a station the user chose, and a listen is a station the user played. The two often disagree. Only
    /// <see cref="RadioCounter"/> adds to it.</summary>
    public RadioListens Listens { get; }

    /// <summary>The station playing, or null.</summary>
    public RadioStation? Current
    {
        get { lock (_gate) return _tuned?.Station; }
    }

    /// <summary>True while the station is unreachable and the source is retrying.</summary>
    public bool IsReconnecting
    {
        get { lock (_gate) return _tuned?.IsReconnecting == true; }
    }

    /// <summary>The track the station says is playing, from its ICY metadata. Null when it does not send
    /// any (HLS stations never do) or has not sent one yet.</summary>
    public string? NowPlayingTitle
    {
        get { lock (_gate) return _tuned?.Title; }
    }

    /// <summary>
    /// The artist and title of the song the station is playing, the way the header shows them: in the
    /// order IcyOrders settled for this station. The artist is null for an advert, a jingle, the station's
    /// own name, or a line that does not name one (see RadioStreamSource.NowPlayingTrack). Both are null
    /// with nothing tuned.
    /// </summary>
    public (string? Artist, string? Title) NowPlayingSong
    {
        get
        {
            RadioStreamSource? tuned;
            lock (_gate) tuned = _tuned;

            // Read outside the lock. Reading the order may ask the collection (KnowsArtist), which takes the
            // same lock.
            return tuned?.NowPlayingTrack is { } now ? (now.Artist, now.Title) : (null, null);
        }
    }

    /// <summary>Raised when the tuned station changes, when it announces another track, and when it drops
    /// or comes back. This can happen on a background thread.</summary>
    public event Action? Changed;

    /// <summary>
    /// Tunes in <paramref name="station"/>, replacing whatever was playing. Returns at once. The connection
    /// is made on the player's own decode thread, so a station that takes five seconds to answer does not
    /// block the interface.
    /// </summary>
    public void Tune(RadioStation station)
    {
        ArgumentNullException.ThrowIfNull(station);

        var source = new RadioStreamSource(station, AudioEngine.Rate, AudioEngine.Channels)
        {
            Orders = _orders,
        };
        source.Changed += () =>
        {
            // The station announcing another song is a track change. Everything that shows what is playing
            // (the header's artwork and its title line above all) only updates when the player says so.
            // Without this call, the header would keep showing the first song of the hour.
            _playback.NotifyNowPlayingChanged();
            Changed?.Invoke();
        };

        lock (_gate) _tuned = source;

        // Duration 0: radio does not end, so the seek bar treats it as nothing to scrub. Use Display rather
        // than the directory's Name. Display is what a cast screen shows. A title set by an admin
        // (StationEdits) is shown there, as it is in the lists.
        if (!_playback.PlaySource(source, durationSeconds: 0, title: station.Display, artist: station.Display))
        {
            // The sound output refused the source, and the player has said why. Clear the tuned station, or
            // the station would show as on with nothing coming out. Only clear it if it is still this
            // source, because another tune may have started in the meantime.
            lock (_gate)
                if (ReferenceEquals(_tuned, source))
                    _tuned = null;
            Changed?.Invoke();
            return;
        }
        Changed?.Invoke();

        // The directory ranks stations by how often they are actually played, and this is the only way it
        // hears about it. Fire and forget, off the tuning path. It never delays the audio, and its failure
        // is nobody's problem.
        _ = Task.Run(() => Directory.ReportPlayedAsync(station.Uuid));
    }

    /// <summary>Switches the radio off. Silent when nothing is tuned.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_tuned is null) return;
            _tuned = null;
        }
        _playback.Stop();       // Disposes the source along with the engine's stream
        Changed?.Invoke();
    }

    /// <summary>True when this station is the one playing.</summary>
    public bool IsTuned(string uuid)
    {
        lock (_gate)
            return _tuned is { } tuned && string.Equals(tuned.Station.Uuid, uuid, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The player moved on to something that is not this station. Clears the tuned station and
    /// raises Changed, so the pane stops showing a station as tuned while a library track is
    /// playing.</summary>
    private void OnPlaybackChanged()
    {
        RadioStreamSource? tuned;
        lock (_gate) tuned = _tuned;
        if (tuned is null || ReferenceEquals(_playback.PlayingSource, tuned))
            return;

        lock (_gate)
        {
            if (!ReferenceEquals(_tuned, tuned))
                return;
            _tuned = null;
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _playback.Changed -= OnPlaybackChanged;
        _http.Dispose();
    }
}
