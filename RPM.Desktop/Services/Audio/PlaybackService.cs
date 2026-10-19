using RPM.Core.Audio;
using RPM.Core.Cloud;
using RPM.Core.Diagnostics;
using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Core.Playback;

namespace RPM.Desktop.Services.Audio;

/// <summary>
/// Audio playback for the app. The inline review preview and RPaMp both use this class.
///
/// It owns an <see cref="AudioEngine"/>. ffmpeg decodes, our <see cref="Equalizer"/> filters, and a sink
/// plays. Owning the whole pipeline gives real pause and resume, live volume and live EQ (nothing
/// restarts), an accurate position, and a visualiser fed by the exact samples being played.
///
/// <para>Because the pipeline is ours, so is the destination. <see cref="CastToAsync"/> swaps the sink for
/// a Google speaker, and the <i>same post-EQ audio</i> goes out over the network. Handing the receiver the
/// original file to decode would bypass the equaliser and the visualiser.</para>
/// </summary>
public sealed partial class PlaybackService : IDisposable
{
    private readonly AudioEngine _engine = new();
    private readonly CastService _cast = new();
    private readonly object _gate = new();

    /// <summary>CAF's <c>MEDIA_SRC_NOT_SUPPORTED</c>: the receiver's player will not hold the source it was
    /// given, whatever the device can decode.</summary>
    private const int MediaSourceNotSupported = 104;

    private CastDevice? _castDevice;
    private CastAudioFormat _castFormat = CastAudioFormat.Mp3;
    private ICastReceiver? _castReceiver;
    private bool _castScreenOn = true;
    private bool? _castFavorite;

    /// <summary>The volume the listener sees, from 0 to 100. This is the number the slider shows. It is not
    /// always the gain the pipeline runs at. With a hardware volume control, the gain stays at full scale
    /// and this number is applied to the speaker instead. See <see cref="ApplyVolume"/>.</summary>
    private int _volume = 100;

    /// <summary>The centre frequencies of the 10 equaliser bands, in Hz.</summary>
    public static int[] EqBands => Equalizer.Bands;

    /// <summary>Raised (possibly on a background thread) whenever playback starts, pauses or
    /// stops.</summary>
    public event Action? Changed;

    /// <summary>Raised (background thread) only when a track finishes on its own, not on a user Stop or a
    /// seek. This lets a playlist advance by itself.</summary>
    public event Action? TrackEnded;

    /// <summary>Raised (background thread) when the music is stopped rather than played to the end. See
    /// <see cref="AudioEngine.Stopped"/>. The queue ends its "now playing" state here. If it did not, that
    /// state could end in the gap between two tracks.</summary>
    public event Action? Stopped;

    /// <summary>Raised (background thread) when a cast drops: the speaker went away, refused the stream, or
    /// never came to fetch it. Playback has already fallen back to this computer by the time this fires;
    /// the trouble is for telling the user why. Also raised for a notice (<see
    /// cref="CastTrouble.IsNotice"/>), when the cast carries on in stereo.</summary>
    public event Action<CastTrouble>? CastFailed;

    /// <summary>Raised when the cast destination changes, including when it fails back to local.</summary>
    public event Action? CastChanged;

    /// <summary>Next was pressed at the speaker, or spoken to it. The owner of the playlist answers this
    /// event. This service does not know what follows the playing track. Only RPM's own receiver can report
    /// this.</summary>
    public event Action? RemoteNext;

    /// <summary>Previous, at the speaker.</summary>
    public event Action? RemotePrevious;

    /// <summary>The heart was pressed on the television. The library owns the favourite mark, so this event
    /// is passed up to whoever owns the library. See RPaMpModel, which writes the mark through the same
    /// path as the app's own heart button, and then reports what it wrote.</summary>
    public event Action? RemoteFavorite;

    /// <summary>The volume changed at the speakers rather than here. That is a cast speaker's own buttons
    /// or the Google Home app, a Bluetooth speaker's buttons, the keyboard's volume keys, or simply the
    /// level the speakers were already at when the music arrived. The player's slider follows it, because
    /// wherever the change comes from, that control and this one are the same control.</summary>
    public event Action<int>? RemoteVolume;

    /// <summary>What the speaker being cast to says it can decode, or null when it cannot be asked. That is
    /// the answer for Google's stock receiver and for no cast at all.</summary>
    public CastCapabilities? CastCapabilities => (_engine.Sink as CastSink)?.Capabilities;

    /// <summary>A speaker has said what it can decode. Whoever keeps settings writes it down, so the next
    /// cast to it can choose a format before connecting rather than after.</summary>
    public event Action<CastDevice, CastCapabilities>? CastCapabilitiesKnown;

    /// <summary>Turns a track whose bytes are not on this disk into something ffmpeg can open: the
    /// presigned URL, and the opening seconds to start on while it opens. Null when there is no
    /// cloud.</summary>
    private readonly CloudStreamService? _stream;

    /// <summary>
    /// Turns a track held by another of the user's computers into a URL on the local network.
    ///
    /// <para>This is set after construction, because sharing needs an open library and the player is built
    /// before one is open. It stays null in a build without sharing. A null value reads as "no computer has
    /// it", which is the same answer as for a file that is not there.</para>
    /// </summary>
    public Sharing.SharingService? Sharing { get; set; }

    /// <summary>This account's own album art in the cloud store. It is the last place that is checked for a
    /// cover when the record being cast has no file on this disk. It is set together with <see
    /// cref="Sharing"/>.</summary>
    public Cloud.CloudArtService? CloudArt { get; set; }

    /// <summary>Raised when playback was paused because the sound had nowhere to go for long enough.
    /// Waiting stopped being the kinder answer. The speakers went away, and nothing replaced them. This is
    /// for telling the user why the music stopped.</summary>
    public event Action? PlaybackStalled;

    /// <summary>
    /// Raised when a play was refused because this computer's sound output is not working, with the reason
    /// the local sink found. For the shell, which shows what to try.
    ///
    /// <para>Raised on the thread that asked to play. It is not raised for a cast speaker, which has its
    /// own messages (<see cref="CastFailed"/>). It is also not raised when the reason is null. A null
    /// reason means the device is being rebuilt, which takes a few milliseconds. The next press
    /// plays.</para>
    /// </summary>
    public event Action<OutputTrouble>? OutputRefused;

    /// <summary>
    /// Raised when the music stopped because the bytes stopped arriving and nowhere else would hand them
    /// over: the computer serving the record left the network, and there is no cloud copy to fall back to.
    /// <see cref="SourceLost"/> holds the state; this says it changed.
    /// </summary>
    public event Action? SourceLostChanged;

    /// <summary>
    /// Whether playback is stopped because the connection is lost, rather than because somebody stopped it.
    /// The library's warning band reads this. It is cleared the moment anything starts playing again,
    /// including the track that could not play. It is a report about now, not a fault to dismiss.
    /// </summary>
    public bool SourceLost { get; private set; }

    /// <summary>How many tries one track gets when it re-opens itself, before the player gives up on it.
    /// See <see cref="RPM.Core.Playback.Retries"/>. The phone uses the same rule.</summary>
    private readonly RPM.Core.Playback.Retries _retries = new();

    /// <summary>
    /// A track the player is holding, with three things about it that are only true together: what it is,
    /// where its bytes are being read from, and which routes to it are still open.
    ///
    /// <para>These are one value and not three fields. The fields are written on one thread and read on
    /// another. The gapless changeover promotes the next track on the pump thread, while the queue may
    /// clear it on the UI thread. As separate fields, the two writes could pair a track with another
    /// track's source. Or the track could end up with no source at all. That reads as "not a network
    /// stream", so a connection that died would look like a record that simply ended. A whole value is
    /// replaced at once, so a reader sees one arrangement or the other, never a mix.</para>
    ///
    /// <para><see cref="Route"/> is the part that narrows during a track's life. See <see
    /// cref="TrackRoute"/>. It narrows by being replaced, like everything else here.</para>
    /// </summary>
    private sealed record Loaded(PlayRequest Track, string Source, bool FromPeer, TrackRoute Route);

    /// <summary>
    /// The track being played. It is kept so that a stream that dies can be opened again. The engine cannot
    /// say what it was playing once it has stopped.
    ///
    /// <para>Read and written with <see cref="Volatile"/>. Swapping the whole value stops a reader from
    /// seeing a mixed state. The barrier stops a reader from seeing an old value, or one whose fields have
    /// not reached memory yet. The write happens on a worker thread and the read on the pump thread, which
    /// runs on another core.</para>
    ///
    /// <para>No lock, on purpose. The pump has fifty milliseconds to fill the sink. A lock here would make
    /// the pump wait for the UI thread. See the note on AudioEngine.TryChain, which calls into this.</para>
    /// </summary>
    private Loaded? _playing;

    /// <summary>The successor that the gapless changeover has already opened. It becomes <see
    /// cref="_playing"/> when the changeover happens. It is written by whoever armed it, and read by the
    /// pump.</summary>
    private Loaded? _next;

    /// <summary>
    /// How close to the declared end counts as having reached it.
    ///
    /// <para>Wide enough to cover a tag whose duration is a few seconds out, which is most of them. It is
    /// far narrower than the gap that a stream that died leaves behind.</para>
    /// </summary>
    private const double EndSlackSeconds = 5;

    /// <summary>Less than this heard before a clean end means the stream delivered no audio.</summary>
    private const double NothingHeardSeconds = 1;

    /// <summary>How long to wait before opening a broken stream again. This is long enough for a laptop's
    /// Wi-Fi to come back, and for the sharing service to mark the other computer as gone. It is short
    /// enough that a hiccup stays a hiccup, and does not become a silence.</summary>
    private static readonly TimeSpan ReopenPause = TimeSpan.FromSeconds(1.5);

    /// <summary>Raised with the name of the endpoint that the sound is now coming out of. This happens when
    /// a speaker connects, when the system's output changes, or when a cast starts or stops. For a cast the
    /// name is empty, because the audio is not on this computer's own device at all. The equaliser listens
    /// to this event.</summary>
    public event Action<string>? OutputDeviceChanged;

    public PlaybackService(CloudStreamService? stream = null)
    {
        _stream = stream;
        _volume = _engine.Volume;

        // The volume changed at the speakers. That is the keyboard's volume keys, the menu bar, or the
        // buttons on a Bluetooth speaker, which send the level back over AVRCP. This uses the same path as
        // the buttons on a cast speaker. From the player's side both are the same event: the volume
        // changed, and we did not change it.
        SystemVolume.Changed += level =>
        {
            _volume = level;
            RemoteVolume?.Invoke(level);
        };

        WatchSink(_engine.Sink);
        _engine.Changed += () => { UpdateSleepBlock(); Changed?.Invoke(); };
        _engine.TrackEnded += OnEngineTrackEnded;
        _engine.Stopped += () => Stopped?.Invoke();
        _engine.TrackChained += OnEngineTrackChained;

        // The decoder is a child pipe and dies with us, but stop cleanly on a normal quit anyway.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Stop(); } catch { } };
    }

    /// <summary>
    /// The source of the levelling gain. Null leaves every record at the level it was mastered at. Tests
    /// and the gallery get this.
    ///
    /// <para>This is a property and not a constructor argument, because the resolver needs the library and
    /// the settings. This service is built before either one is open.</para>
    /// </summary>
    public NormalizationService? Normalization { get; set; }

    /// <summary>
    /// Whether what is playing is a record played straight through, as against a queue somebody built from
    /// several. The first case uses album gain and the second uses track gain. See <see
    /// cref="RPM.Core.Analysis.Normalization.Resolve"/>.
    ///
    /// <para>This is set by whoever owns the queue, because only the queue knows what surrounds the track.
    /// It is true by default. A single track played on its own counts as its own record, and the album
    /// reading is the better choice when nothing else is known.</para>
    /// </summary>
    public bool InAlbumOrder { get; set; } = true;

    /// <summary>The gain a track would play at, in dB. Zero when nothing is to be done.</summary>
    private double GainFor(string path) =>
        Normalization?.GainFor(path, InAlbumOrder) ?? 0;

    public string? PlayingPath => _engine.PlayingPath;

    /// <summary>The cloud id of the record playing, or null where it has none (a radio stream, a set, a
    /// file never uploaded).</summary>
    public string? PlayingRpmId => PlayingPath is { } path ? RpmIdFor(path) : null;

    /// <summary>The record playing, as the collection knows it, or null when what is playing is not a
    /// record: a mix before its first record starts playing, a radio stream. What anything that shows or
    /// opens the playing track should ask for. See <see cref="AudioEngine.PlayingRecord"/>. <see
    /// cref="PlayingPath"/> returns a stream's own label, and it is for matching only.</summary>
    public string? PlayingRecord => _engine.PlayingRecord;

    /// <summary>Duration of the playing file, as supplied by the caller (from the tags).</summary>
    public double DurationSeconds => _engine.DurationSeconds;

    /// <summary>Play position reported by the sink: the sound device locally, or the receiver when casting.
    /// Either way it is the position of what is actually coming out, not a wall clock.</summary>
    public double PositionSeconds => _engine.PositionSeconds;

    /// <summary>How much of the current track has actually been played out in this stretch of it. See <see
    /// cref="AudioEngine.HeardSeconds"/>. The play counter adds up a listen from this value.</summary>
    public double HeardSeconds => _engine.HeardSeconds;

    /// <summary>Which run of the pump <see cref="HeardSeconds"/> belongs to. See <see
    /// cref="AudioEngine.PlaySession"/>.</summary>
    public int PlaySession => _engine.PlaySession;

    /// <summary>Where that run was started from, in the track's seconds: zero from the top, the target
    /// after a seek.</summary>
    public double StartOffsetSeconds => _engine.StartOffsetSeconds;

    /// <summary>True while paused (the track is still loaded and will resume where it stopped).</summary>
    public bool IsPaused => _engine.IsPaused;

    /// <summary>How far ahead of the speakers the source has already been decoded. See <see
    /// cref="AudioEngine.OutputLagSeconds"/>. Anything that draws a playhead against a beat grid has to
    /// subtract this.</summary>
    public double OutputLagSeconds => _engine.OutputLagSeconds;

    /// <summary>Live volume/EQ are always available with our own pipeline.</summary>
    public bool CanShape => _engine.IsAvailable;

    /// <summary>
    /// Seeking also works when casting. A live stream allows only one way to seek. The decoder restarts at
    /// the new offset, and the receiver is given a fresh stream. This costs a second or two of silence
    /// while the receiver buffers again. VLC works the same way. The position stays correct across a seek,
    /// because it is calculated and not measured. It is the seek offset, plus how far the receiver says it
    /// has got through the current stream.
    /// </summary>
    public bool CanSeek => _engine.IsAvailable && _engine.CanSeekCurrent;

    /// <summary>
    /// How the audio is encoded on its way to a speaker. Changing it while casting restarts the stream at
    /// the current position, since the receiver has already been told the old MIME type.
    /// </summary>
    public CastAudioFormat CastFormat
    {
        get { lock (_gate) return _castFormat; }
        set
        {
            lock (_gate)
            {
                if (_castFormat == value) return;
                _castFormat = value;
            }

            if (_engine.Sink is CastSink sink)
            {
                sink.Format = value;
                // Restart the stream from the current position. This uses the same code as a seek, so the
                // queue and the EQ are not touched. The listener hears a short re-buffer, not a full
                // restart.
                if (PlayingPath is not null) _engine.SeekTo(PositionSeconds);
            }

            CastChanged?.Invoke();
        }
    }

    /// <summary>
    /// Says whether the listener has marked the record playing as a favourite. The television uses this to
    /// draw the mark. Null means the collection does not hold the record, and that removes the heart from
    /// the row.
    ///
    /// <para>This value is kept here as well as sent. A receiver page that reloads starts with no
    /// knowledge, so the connection sends this value again when the receiver says hello.</para>
    /// </summary>
    public bool? CastFavorite
    {
        get { lock (_gate) return _castFavorite; }
        set
        {
            ICastReceiver? receiver;
            lock (_gate)
            {
                if (_castFavorite == value) return;
                _castFavorite = value;
                receiver = _castReceiver;
            }

            if (receiver is not null) _ = receiver.SetFavoriteAsync(value);
        }
    }

    /// <summary>
    /// Whether the television at the far end shows anything. The music does not depend on it either way.
    /// This is the screen in a room nobody is sitting in. A still picture left up for the length of an
    /// album is how a panel gets a permanent mark.
    ///
    /// <para>This is kept here rather than in the receiver, because it outlives one. A speaker that drops
    /// and is joined again, or a receiver page that reloads, comes back showing whatever its markup says.
    /// The connection sends this value again each time a receiver introduces itself.</para>
    /// </summary>
    public bool CastScreenOn
    {
        get { lock (_gate) return _castScreenOn; }
        set
        {
            ICastReceiver? receiver;
            lock (_gate)
            {
                if (_castScreenOn == value) return;
                _castScreenOn = value;
                receiver = _castReceiver;
            }

            if (receiver is not null) _ = receiver.SetScreenAsync(value);
            CastChanged?.Invoke();
        }
    }

    /// <summary>The output volume, from 0 to 100. This is the listener's number, whichever control holds
    /// it. On wired speakers it is the sink's gain. When casting, it is the receiver's stream volume. On a
    /// Bluetooth speaker it is the speaker's own volume. See <see cref="VolumeKnob"/>.</summary>
    public int Volume => _volume;

    /// <summary>Which control holds the volume. This is where the level is actually held. It is the only
    /// thing the interface needs to know about this.</summary>
    public VolumeKnob VolumeHeldBy => VolumeRouting.Choose(IsCasting, SystemVolume.IsHardware);

    /// <summary>True when the number is held on the speakers themselves, not on our own gain. This is also
    /// when it is not ours alone. A machine's output volume belongs to the whole machine, and a speaker in
    /// a room belongs to the room.</summary>
    public bool IsHardwareVolume => VolumeHeldBy != VolumeKnob.App;

    /// <summary>The speaker we are casting to, or null when playing on this computer.</summary>
    public CastDevice? CastDevice { get { lock (_gate) return _castDevice; } }

    public bool IsCasting => CastDevice is not null;

    public bool IsPlaying(string path) => _engine.PlayingPath == path;

    // ── casting ──

    /// <summary>Finds the Cast devices on the network. Empty when there are none. On macOS, when the user
    /// has not granted Local Network access yet.</summary>
    public Task<IReadOnlyList<CastDevice>> DiscoverCastDevicesAsync(TimeSpan? timeout = null)
        => _cast.DiscoverAsync(timeout);

    /// <summary>
    /// What to tell the user about a connection to <paramref name="device"/> that failed. A speaker in a
    /// stereo pair refuses every app, and that is fixable by choosing the group; one that does not answer
    /// at all is off or on another network. The exception goes to the ring.
    /// </summary>
    private static CastTrouble ConnectTrouble(CastDevice device, Exception ex)
    {
        if (ex is CastRefusedException)
        {
            Said.Line("cast", "connecting failed: the speaker refused every receiver app (a stereo pair or group)");
            return new CastTrouble.InPair(device.Name);
        }

        return Faults.Note("cast", "connecting", ex) is Fault.Offline or Fault.Timeout
            ? new CastTrouble.Unreachable(device.Name)
            : new CastTrouble.ConnectFailed(device.Name);
    }

    /// <summary>
    /// Moves playback to <paramref name="device"/>, picking up where it left off. Returns false if the
    /// speaker would not have us, in which case nothing changes and the music stays here.
    /// </summary>
    /// <param name="format">The format to send in. Required, so a cast never starts in whatever format the
    /// previous cast was left in. Called once the connection is up rather than when the cast is asked for,
    /// so a format chosen while connecting is the one used.</param>
    public async Task<bool> CastToAsync(CastDevice device, Func<CastAudioFormat> format)
    {
        ArgumentNullException.ThrowIfNull(device);

        ICastReceiver receiver;
        try { receiver = await _cast.ConnectAsync(device); }
        catch (Exception ex)
        {
            CastFailed?.Invoke(ConnectTrouble(device, ex));
            return false;
        }

        // A receiver that refuses the media reports it once, and then ignores us. Without this report, the
        // player would seem to stream to a speaker that is not listening.
        if (receiver is SharpcasterReceiver reporting)
        {
            reporting.Error += reason => CastFailed?.Invoke(reason);
            reporting.CapabilitiesKnown += caps => CastCapabilitiesKnown?.Invoke(device, caps);

            // The receiver refused the container outright. Code 104 means that the player will not hold
            // that source. For surround this is a dead end, not a hiccup. The speaker can decode Dolby, but
            // it cannot be given it this way. So the music comes back in stereo, instead of the room
            // getting nothing at all.
            reporting.MediaRejected += (code, detail) =>
            {
                if (code != MediaSourceNotSupported) return;
                if (CastFormat != CastAudioFormat.Ac3Surround) return;

                CastFormat = CastAudioFormat.Mp3;
                Said.Line("cast", $"surround stream rejected ({code}: {detail}); sending stereo");
                CastFailed?.Invoke(new CastTrouble.NoSurround(device.Name));
            };
        }

        var sending = format();
        var sink = new CastSink(receiver, CastCoverAsync, sending);

        // A speaker is not only ours while we are casting to it. It has a touch surface, it is in the
        // Google Home app, and it answers to "Hey Google, pause". Whatever happens to the music in the
        // room, the player's own transport has to show it. Otherwise the app would show that it is playing
        // while the room is silent, and pressing pause would do nothing that anyone can hear.
        sink.RemotePaused += () => Pause();
        sink.RemoteResumed += () => Resume();

        // Stopped at the speaker, or the speaker taken over by something else. Stop rather than skip. The
        // receiver also goes idle at the end of every track. Its idleReason tells the two apart. Without
        // that, a stop would be answered with the next song.
        sink.RemoteStopped += () => Stop();

        // A notice is worth telling the user about, but it must not stop the cast. The cast is still working.
        sink.Notice += reason => CastFailed?.Invoke(reason);

        // Next and previous at the speaker only arrive from RPM's own receiver. Google's stock receiver
        // cannot tell a skip from a stop, and reports both as a stop. The skip happens on this side, in the
        // same stream, which keeps it gapless.
        sink.RemoteNext += () => RemoteNext?.Invoke();
        sink.RemotePrevious += () => RemotePrevious?.Invoke();
        sink.RemoteFavorite += () => RemoteFavorite?.Invoke();

        // Casting hands the volume control to the speaker. The slider drives the device's own volume, so it
        // must show where that control really is. This includes the level the room was already set to.
        sink.VolumeChanged += volume =>
        {
            _volume = volume;
            _engine.SetVolume(volume);
            RemoteVolume?.Invoke(volume);
        };

        sink.Failed += reason =>
        {
            // The speaker is no longer a working destination. Move the music back to this computer, instead
            // of leaving the player to look as if it is playing to an empty room.
            CastFailed?.Invoke(reason);
            StopCasting();
        };

        bool screen;
        bool? loved;
        lock (_gate)
        {
            _castDevice = device;
            _castReceiver = receiver;
            _castFormat = sending;
            screen = _castScreenOn;
            loved = _castFavorite;
        }

        // The state is sent now, as well as on the receiver's hello. Both are needed. The stock receiver
        // never says hello, and a rejoin to a receiver that is already running does not start a new page to
        // say hello from.
        _ = receiver.SetScreenAsync(screen);
        _ = receiver.SetFavoriteAsync(loved);

        _engine.SetSink(sink);
        WatchSink(sink);
        CastChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// The cover to put on the television, wherever this record happens to live.
    ///
    /// <para>The cover is found wherever this record lives. Reading it from the file next to the track is
    /// right for only one kind of record. A record borrowed from another computer carries that computer's
    /// path, so there is no file here and no folder beside it. A record this machine has offloaded has a
    /// row and a cover in the store, and no file at all. Both kinds play, and both would cast without a
    /// cover.</para>
    ///
    /// <para>So: this disk first, then the computer that has it, then this account's store. In that order
    /// because each is slower and less certain than the one before it, and because the first one that
    /// answers is the picture the app is already showing.</para>
    /// </summary>
    private async Task<AlbumCover?> CastCoverAsync(SinkTrack track, CancellationToken ct)
    {
        // The album folder's picture, then the track's tags. SinkArt owns that order and why.
        if (await SinkArt.FromTheFileAlone(track, ct) is { Bytes.Length: > 0 } local)
            return local;

        // Nothing is on this disk, so the question is which record this is. The answer is not in a
        // SinkTrack, which holds a path and three names. It is asked of what is loaded, by path, using the
        // same string that the request which loaded it carried. The answer is looked up, not caught at one
        // moment, so it is true on the fifth ask as on the first.
        if (RpmIdFor(track.Path) is not { Length: > 0 } rpmId)
            return null;

        try
        {
            // The computer that has it. Its answer is much better than anything that could be worked out
            // here. It looks in the folder, in the file, in the album's other tracks, and finally in its
            // own cloud. See SharingService.CoverFor.
            if (Sharing is { } sharing &&
                await sharing.CoverForAsync(rpmId, ct) is { Length: > 0 } borrowed)
                return new AlbumCover(borrowed, AlbumArt.SniffMime(borrowed), AlbumCover.SourceElsewhere);

            // If that fails, use this account's store. That is where the cover went when this machine's
            // files for the record left.
            if (CloudArt is { } art &&
                await art.CoverAsync(track.Artist, track.Artist, track.Album, rpmId, ct)
                    is { Length: > 0 } stored)
                return new AlbumCover(stored, AlbumArt.SniffMime(stored), AlbumCover.SourceElsewhere);
        }
        catch
        {
            // Decoration, over a network. A cover that could not be fetched is no cover.
        }

        return null;
    }

    /// <summary>
    /// Which record is at <paramref name="path"/>. Only the two records that this service holds open are
    /// checked. They are the one playing and the one chained behind it. Null for anything else. That
    /// includes a DJ set and a radio stream, because neither has a row that can be asked about over the
    /// network.
    /// </summary>
    private string? RpmIdFor(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        if (Volatile.Read(ref _playing) is { Track: { RpmId: { Length: > 0 } playing } request }
            && request.Path == path)
            return playing;

        if (Volatile.Read(ref _next) is { Track: { RpmId: { Length: > 0 } next } chained }
            && chained.Path == path)
            return next;

        return null;
    }

    /// <summary>Brings playback back to this computer's sound device, from wherever it was.</summary>
    public void StopCasting()
    {
        lock (_gate)
        {
            if (_castDevice is null) return;
            _castDevice = null;
            _castReceiver = null;
        }
        var local = new OpenAlSink();
        _engine.SetSink(local);
        WatchSink(local);
        CastChanged?.Invoke();
    }

    /// <summary>Sends the music to a particular set of speakers, by the sound driver's name for them. A
    /// null name means the machine's default.
    ///
    /// <para>This takes effect live. A track that is playing through the old speakers finishes through the
    /// new ones, without a restart and without losing its place. While casting, this only decides where the
    /// music goes when it comes back to this computer.</para></summary>
    public void SetOutputDevice(string? device)
    {
        OpenAlSink.PreferredDevice = device;
        if (_engine.Sink is OpenAlSink local) local.FollowNow();
    }

    /// <summary>The speakers the sound is actually coming out of, by the driver's name for them, or empty
    /// while the music is going somewhere that is not this computer.</summary>
    public string OutputDeviceName => _engine.Sink is OpenAlSink local ? local.CurrentDevice : "";

    /// <summary>Follows a local sink's own moves between endpoints, and announces the swap itself for a
    /// sink that has just been fitted. Every place the sink changes goes through here.</summary>
    private void WatchSink(IAudioSink sink)
    {
        if (sink is OpenAlSink local)
        {
            local.DeviceChanged += AnnounceOutput;

            // The sound has had nowhere to go for ten seconds. Keeping the track open for longer is worse
            // than stopping it. So the track pauses where it got to. Pressing play once there are speakers
            // again carries on from there, and does not start from the top.
            local.Stalled += () =>
            {
                if (IsPaused || PlayingPath is null) return;

                Pause();
                PlaybackStalled?.Invoke();
            };
        }

        AnnounceOutput(OutputDeviceName);
    }

    /// <summary>
    /// Keeps the machine awake for as long as a record is actually playing, and lets go the moment one is
    /// not.
    ///
    /// <para>This is needed for casting. A local sink keeps the machine up by itself, because an open audio
    /// device is activity that the operating system already counts. A cast closes that device and sends
    /// bytes to a socket instead. On battery, that looks like an idle machine, so the laptop sleeps in the
    /// middle of a song. The block is also held for local playback. The cost is nothing, and "playing" is
    /// the true reason on every route out, not only on some of them.</para>
    ///
    /// <para>When paused, the block is not held. The music has stopped, so letting the machine sleep is the
    /// right answer, even when the speaker is still connected. The machine wakes when the lid is
    /// opened.</para>
    /// </summary>
    private void UpdateSleepBlock()
        => SleepBlocker.Hold(PlayingPath is not null && !IsPaused);

    /// <summary>The equaliser only: the pre-amp and the ten band gains, applied live. Volume is set
    /// separately.</summary>
    public void ApplyEqualizer(double preampDb, IReadOnlyList<double> bandGainsDb)
        => _engine.SetEqualizer(preampDb, bandGainsDb);

    /// <summary>Output volume, 0..100, live.</summary>
    public void SetVolume(int volume)
    {
        _volume = Math.Clamp(volume, 0, 100);
        ApplyVolume();
    }

    /// <summary>
    /// Puts the listener's number on whichever control is holding it, and sets our own gain to match that
    /// decision.
    ///
    /// <para>The order matters. The hardware volume is set FIRST. Our own gain is only opened to full after
    /// the hardware has actually taken the level. A write that fails, for example because the speaker
    /// dropped between the check and the write, must leave the app attenuating as before. It must not jump
    /// the room to full volume.</para>
    /// </summary>
    private void ApplyVolume()
    {
        var knob = VolumeHeldBy;

        if (knob == VolumeKnob.Bluetooth && !SystemVolume.TryWrite(_volume))
            knob = VolumeKnob.App;

        _engine.SetVolume(VolumeRouting.GainFor(knob, _volume));
    }

    /// <summary>
    /// Reads the level back from the speakers that hold it, and takes the value found as the app's own
    /// number.
    ///
    /// <para>The endpoint's notification is the usual way this news arrives. Anything that moves the level
    /// is reported the moment it happens. That includes the volume keys, the tray, another app, and the
    /// buttons on the speaker. This method is for the reports that never arrive. Those are the levels that
    /// were already set before anything here was listening to that endpoint. Examples are the speaker the
    /// app was launched next to, an endpoint swapped in underneath us, and a speaker that reconnected as a
    /// new one. Asking costs one property read.</para>
    ///
    /// <para>The value is reported even when it matches the number already held. The point of asking is the
    /// listener at the other end of <see cref="RemoteVolume"/>. A slider that opened at a guess has no
    /// other way to hear the true level. False means that nothing out there holds a level, and our own gain
    /// is the control. In that case the number is already the one we think it is.</para>
    /// </summary>
    public bool SyncVolumeFromSpeakers()
    {
        // Casting is handled elsewhere. The receiver pushes its own level over the session (see
        // CastSink.VolumeChanged). This computer's output device is not in the path, so there is
        // nothing to read.
        if (IsCasting || !SystemVolume.TryRead(out var level)) return false;

        _volume = level;
        _engine.SetVolume(VolumeRouting.GainFor(VolumeKnob.Bluetooth, level));
        RemoteVolume?.Invoke(level);
        return true;
    }

    /// <summary>
    /// The sound is coming out of somewhere else now. This first works out where the volume control went. A
    /// Bluetooth speaker has a control of its own. A wired speaker does not. A cast takes this machine's
    /// output out of the path entirely.
    ///
    /// <para>The three steps are in this order, for a reason each. The volume control is settled BEFORE the
    /// news, because whoever hears the news asks which control we are on, to decide whether to restore a
    /// remembered level. The adoption comes AFTER it. Where the speakers hold the level, their number has
    /// to win. RPM takes what it finds, rather than imposing the number it was last set to, which is the
    /// courtesy casting has always shown a room and is doubly owed to an output volume the whole machine
    /// shares.</para>
    /// </summary>
    private void AnnounceOutput(string device)
    {
        var hardware = SystemVolume.Retarget(IsCasting ? "" : OutputDeviceName);

        OutputDeviceChanged?.Invoke(device);

        if (hardware && SyncVolumeFromSpeakers()) return;

        // Back onto our own gain from a control that was holding the volume. The endpoint that had the
        // control has gone. So the number has to be applied again, or the sound stays at whatever full
        // scale it was left at.
        ApplyVolume();
    }

    // ── transport ──

    /// <summary>Plays the file from the start, or stops it when it is the one already playing.</summary>
    public void Toggle(string path, double durationSeconds)
    {
        if (IsPlaying(path)) Stop();
        else Play(path, durationSeconds, startSeconds: 0);
    }

    /// <summary>Plays <paramref name="path"/> from <paramref name="startSeconds"/>, optionally stopping
    /// after <paramref name="playLengthSeconds"/> (clip preview). The tags come along because a Cast
    /// receiver puts them on the television; local playback ignores them.</summary>
    /// <returns>False when the sound output refused it, which has already been reported through <see
    /// cref="OutputRefused"/>.</returns>
    public bool Play(
        string path,
        double durationSeconds,
        double startSeconds,
        double? playLengthSeconds = null,
        string? title = null,
        string? artist = null,
        string? album = null,
        string? source = null)
    {
        // Whatever the player was told to put on is what is on now. StartAsync writes its own track back
        // straight afterwards. Everything else, such as a clip from the split editor or a rip preview,
        // leaves this empty. That stops a preview that failed from being read as a failure of the queue's
        // own record.
        Forget();
        if (_engine.Play(path, durationSeconds, startSeconds, playLengthSeconds, title, artist, album, source))
            return true;

        NoteOutputRefused();
        return false;
    }

    /// <summary>The sound output would not take a track. This asks the local sink to try its device again,
    /// because the listener may just have fixed the problem. It then reports the reason through <see
    /// cref="OutputRefused"/>.</summary>
    private void NoteOutputRefused()
    {
        if (_engine.Sink is not OpenAlSink local)
            return;

        local.TryAgain();

        if (local.Trouble is { } trouble)
            OutputRefused?.Invoke(trouble);
    }

    /// <summary>Lets go of the track the player is holding for a re-open. This is called whenever something
    /// other than the queue takes over the pipeline.</summary>
    private void Forget()
    {
        Volatile.Write(ref _playing, null);
        Volatile.Write(ref _next, null);
    }

    /// <summary>
    /// What the player needs to put a record on, wherever the record lives: where it belongs on this disk,
    /// how long it runs, and the id the cloud knows it by.
    /// </summary>
    /// <param name="Path">The track's identity. Anything that asks "is this the track that is playing?"
    /// compares against it, streamed or not. It is the one thing about a track that stays the same.</param>
    /// <param name="RpmId">Null for a track that has never been in the cloud. Such a track can only be
    /// played from the disk.</param>
    public sealed record PlayRequest(
        string Path,
        double DurationSeconds,
        string? RpmId = null,
        string? Title = null,
        string? Artist = null,
        string? Album = null);

    /// <summary>
    /// Plays a track wherever it lives. A track on this disk plays from the disk. A track that is only in
    /// the cloud streams, and the listener is not told which. This happens for a track that was offloaded
    /// from here, or was uploaded by another of the user's installs and never held here at all. That is the
    /// whole point of offloading, and of signing in on a second computer.
    ///
    /// <para>
    /// <b>One pipeline, on purpose.</b> A streamed track goes through <see cref="Play"/> exactly as a file
    /// does. ffmpeg opens a presigned URL as happily as a path, with ranges and all. So a streamed track
    /// keeps everything that depends on ordinary playback: the record on the deck, the artwork, the ticker,
    /// the visualiser, a working scrubber, and the Cast handoff. A separate path that started from a short
    /// head clip would save a few hundred milliseconds, but it would lose all of those features.
    /// </para>
    ///
    /// <para>What makes it quick is the look-ahead rather than a clip: the URL of the next few tracks is
    /// signed before they are wanted, so starting one is a decoder opening, the same as a file.</para>
    /// </summary>
    /// <returns>False when there is nothing to play: no file, no computer on this network holding it, and
    /// no cloud copy to stream.</returns>
    public Task<bool> StartAsync(
        PlayRequest track, double startSeconds = 0, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(track);

        // A start that the listener asked for. Trouble with the last record is not carried over. This track
        // gets its full three tries of its own.
        _retries.Forget();

        // It also gets every route to itself, including the computer that dropped the last record. A
        // computer that is marked as gone during one song is marked as gone for that song only.
        return StartCoreAsync(track, startSeconds, TrackRoute.Anywhere, ct);
    }

    private async Task<bool> StartCoreAsync(
        PlayRequest track, double startSeconds, TrackRoute route, CancellationToken ct)
    {
        // Set the gain before the track starts, not after. The engine applies the gain when a track begins.
        // Setting it first stops the opening moment of every record from playing at the previous record's
        // level.
        _engine.SetTrackGain(GainFor(track.Path));

        var found = await FindAsync(track, route, ct);
        if (found.Source is not { } source)
            return false;

        // The sound output refused it. The track was found, but it is not playing. This returns true
        // anyway, because false means there was nothing to play. The queue would then skip to the next
        // record, and in the end show the "no connection" warning. The output's own message has already
        // been raised.
        if (!Play(track.Path, track.DurationSeconds, startSeconds,
                  title: track.Title, artist: track.Artist, album: track.Album, source: source))
            return true;

        // This is set after the start, not before. Play lets go of whatever the player was holding, and
        // this is the record it is holding now. Nothing can read it in between. The engine opens the
        // decoder on a worker thread, and the track cannot run out before it has begun.
        Volatile.Write(ref _playing, new Loaded(track, source, found.FromPeer, route));

        NoteSourceFound();
        return true;
    }

    /// <summary>
    /// Where a track's bytes are to be read from: this disk when the file is here, a presigned URL when the
    /// record only exists in the cloud, and null when it is neither and so cannot be played at all.
    ///
    /// <para>The one place that rule lives, and everything that opens a track asks it. The player starting
    /// one, the look-ahead opening the next, and the DJ putting a record on a deck. That is what makes "the
    /// DJ can mix whatever the player can play" true by construction rather than by two lists of conditions
    /// agreeing with each other.</para>
    /// </summary>
    /// <param name="route">Which routes this attempt may take. It is stated, not defaulted. A track that is
    /// being put on for the first time may go anywhere. A track that is being picked up again, after its
    /// bytes stopped arriving, may not go back to the computer that stopped serving it.</param>
    public async Task<string?> SourceForAsync(
        PlayRequest track, TrackRoute route, CancellationToken ct = default)
        => (await FindAsync(track, route, ct)).Source;

    /// <summary>The same answer, with one extra fact that the player needs when a stream dies. That fact is
    /// whether the bytes came from another computer on this network. It decides whether the record should
    /// be looked for in the cloud, or whether there is nowhere else to look.</summary>
    private async Task<(string? Source, bool FromPeer)> FindAsync(
        PlayRequest track, TrackRoute route, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (File.Exists(track.Path))
            return (track.Path, false);

        if (!RpmTag.IsId(track.RpmId))
            return (null, false);

        // Another of the user's own computers is checked before the cloud. It is on the same network, so it
        // is quicker and costs nobody anything. For a record that was never uploaded, it is the only place
        // where the bytes are.
        //
        // The route is handed down, not acted on here, because this door leads to two places. One is that
        // computer. The other is the store's copy of the record, which is the only copy that can be heard
        // while that computer is switched off. A track that the computer has already dropped mid-song must
        // be barred from the first place, but not from the second. The second place is the fallback.
        if (Sharing is { } sharing &&
            await sharing.UrlForAsync(track.RpmId!, route, ct) is { Length: > 0 } near)
            return (near, RPM.Core.Sharing.SharingProtocol.IsPeerTrackUrl(near));

        if (_stream is null)
            return (null, false);

        return (await _stream.UrlAsync(track.RpmId!, ct), false);
    }

    /// <summary>
    /// Opens the track that follows and hands it to the engine, so that when the current one plays out the
    /// pump simply reads on: no stop, no device re-open, no silence between two sides of a record that were
    /// never meant to have one.
    ///
    /// <para>Works the same for a file and for a stream. The successor is opened ahead of time either way,
    /// which is also what takes the connection's latency off the changeover.</para>
    /// </summary>
    /// <returns>False when the track could not be opened; the queue then advances the ordinary way when the
    /// current one ends.</returns>
    public async Task<bool> ChainAsync(PlayRequest track, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(track);

        // This is the next track, so it is a fresh start. Whatever the record before it could not reach,
        // this track asks the network again first. This is how a computer that came back gets picked up
        // again.
        if ((await FindAsync(track, TrackRoute.Anywhere, ct)).Source is not { } source)
            return false;

        if (ct.IsCancellationRequested || Open(source, track.Path) is not { } pcm)
            return false;

        // Remembered for the changeover. When the engine reads on into this track, it becomes the track
        // that has to be opened again if its stream breaks.
        Volatile.Write(ref _next, new Loaded(
            track, source, RPM.Core.Sharing.SharingProtocol.IsPeerTrackUrl(source),
            // Opened with every route open. The track keeps that when it is promoted. Whatever the record
            // before it could not reach is not this track's concern.
            TrackRoute.Anywhere));

        _engine.QueueNext(
            pcm, track.DurationSeconds, track.Title, track.Artist,
            path: track.Path, seekVia: source, album: track.Album,
            // The gain is carried with the successor, and not set now. The changeover happens on the pump,
            // seconds from here. Setting it at this moment would change the gain of the record that is
            // still playing.
            gainDb: GainFor(track.Path));

        return true;
    }

    /// <summary>A decoder on a file or a URL, or null when it would not start.</summary>
    private static IPcmSource? Open(string source, string label)
    {
        try
        {
            var pcm = new FfmpegPcmSource(new FfmpegPcmSource.Options
            {
                Source = source,
                SampleRate = AudioEngine.Rate,
                Channels = AudioEngine.Channels,
                Label = label,
                InputArgs = FfmpegPcmSource.ReconnectArgs(source),
            });

            if (!pcm.Ended) return pcm;

            pcm.Dispose();
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Whether a successor is already queued behind the playing track.</summary>
    public bool HasChain => _engine.HasQueuedNext;

    /// <summary>
    /// Whether the player currently has this file open. This covers the track that is playing, and the
    /// track that the gapless changeover has already opened behind it.
    ///
    /// <para>
    /// Asked before anything rewrites a file that the listener may be using. Tag edits, a split, or an
    /// analysis written back all rewrite the file in place. Doing that during a song either fails outright
    /// or quietly breaks the next seek. So no file should be rewritten while it is being listened to.
    /// </para>
    /// </summary>
    public bool HoldsOpen(string? path) =>
        RPM.Core.Cloud.OpenFiles.HeldOpen(path, _engine.PlayingPath, _engine.QueuedNextPath);

    /// <summary>Lets go of the queued successor. The queue was edited, so the track that was prepared may
    /// no longer be the one that follows.</summary>
    public void ClearChain()
    {
        Volatile.Write(ref _next, null);
        _engine.ClearQueuedNext();
    }

    /// <summary>Raised on a background thread when the queued successor took over. The device was not
    /// touched, so the queue advances its own cursor on this event. No track ended, so <see
    /// cref="TrackEnded"/> is not raised.</summary>
    public event Action? TrackChained;

    // ── a stream that broke ──

    /// <summary>
    /// The engine has run out of audio. Whether that is the end of a record or the end of a connection is
    /// the whole question here. Only the source knows. See <see cref="AudioEngine.EndedAs"/>.
    ///
    /// <para>Treating both as "the track finished" is wrong. If a computer on the network has left, the
    /// queue could run through every track in silence. Each track would end the moment it started, and
    /// nothing on screen would say why.</para>
    /// </summary>
    private void OnEngineTrackEnded()
    {
        // Read once. Everything below is about one arrangement of the deck. The changeover can put another
        // arrangement there between any two lines of this method.
        var playing = Volatile.Read(ref _playing);

        if (playing?.Track is not { RpmId: { } id } track || !FfmpegPcmSource.IsNetwork(playing.Source))
        {
            // A file on this disk. The line records how the decoder ended and how much was heard, so an end
            // before any audio (which the queue reads as "next track") can be told from a track that played out.
            Said.Line("play",
                $"'{System.IO.Path.GetFileName(playing?.Track.Path ?? "a source outside the queue")}' ended: " +
                $"{_engine.EndedAs}, {Math.Max(0, _engine.HeardSeconds):0.#}s heard from " +
                $"{Math.Max(0, _engine.StartOffsetSeconds):0.#}s" +
                (_engine.EndFault is { } fault ? $", {fault}" : ""));
            TrackEnded?.Invoke();
            return;
        }

        // Where it got to. The position reads zero at this point, because the engine has already let the
        // track go. That is why this value is built from the two numbers that survive.
        var heard = Math.Max(0, _engine.HeardSeconds);
        var reached = Math.Max(0, _engine.StartOffsetSeconds + heard);
        var failed = _engine.EndedAs is PcmState.Failed;
        var end = ReadEnd(failed, track.DurationSeconds, reached, heard);

        Said.Line("play",
            $"'{track.Title ?? track.Path}' from {(playing.FromPeer ? "a computer on this network" : "the cloud")} " +
            $"ended at {reached:0.#}s of {track.DurationSeconds:0.#}s: {end}, " +
            (failed ? _engine.EndFault ?? "failed, no reason given"
                    : heard < NothingHeardSeconds ? "ended before any audio arrived" : "ended cleanly"));

        if (end is TrackEnd.PlayedOut)
        {
            TrackEnded?.Invoke();
            return;
        }

        _ = ReopenAsync(
            track, id, reached, _engine.PlaySession, playing.FromPeer, playing.Route.AfterAStumble());
    }

    /// <summary>What a network stream running out means.</summary>
    internal enum TrackEnd { PlayedOut, Broke }

    /// <summary>
    /// Reads the end of a streamed track. A stream that reached its declared end has played out, whatever
    /// ffmpeg reported on the way. Without this floor, a stream that complained once about something
    /// harmless, and then finished, would be opened again at its own last second, up to three times.
    ///
    /// <para>Short of the end, a failure is a break. A clean end with no audio heard is also a break.
    /// ffmpeg exits with zero and prints nothing to stderr when the server answers but sends no body. If
    /// that were read as a finish, the track would be skipped in silence and the queue would move
    /// on.</para>
    /// </summary>
    internal static TrackEnd ReadEnd(bool failed, double durationSeconds, double reachedSeconds, double heardSeconds)
    {
        if (durationSeconds > 0 && reachedSeconds >= durationSeconds - EndSlackSeconds)
            return TrackEnd.PlayedOut;

        if (failed) return TrackEnd.Broke;

        return heardSeconds < NothingHeardSeconds ? TrackEnd.Broke : TrackEnd.PlayedOut;
    }

    /// <summary>The gapless changeover happened: what was prepared is what is playing now.</summary>
    private void OnEngineTrackChained()
    {
        // Promote the successor before letting go of the old one, in that order. For the instant when both
        // name the track, the deck reads as the track that is actually playing. In the other order there is
        // a moment when the successor is gone and the deck still names the record it replaced. A stream
        // that broke in that moment would be re-opened as the wrong track.
        //
        // Let go of the successor only if it is still the one that was promoted. The queue can clear the
        // successor and arm a different one at any point, including this instant. Clearing the slot blindly
        // would throw away that new arming, while the engine holds what it opened. The deck would then name
        // a record that has finished playing.
        if (Volatile.Read(ref _next) is { } next)
        {
            Volatile.Write(ref _playing, next);
            Interlocked.CompareExchange(ref _next, null, next);
        }

        // A record that played itself out has no troubles left. Its retry count is cleared.
        _retries.Forget();
        TrackChained?.Invoke();
    }

    /// <summary>
    /// Puts the record back on where it stopped, by whatever route will have it now.
    ///
    /// <para>The computer that was serving it is marked as gone first. This avoids spending three more
    /// seconds to learn what the last failure already showed. If the record has a cloud copy, that copy is
    /// the next thing <see cref="FindAsync"/> tries. That is the fallback: the same record, from the store,
    /// from the second where it stopped.</para>
    ///
    /// <para>The narrowed route stays in place. <paramref name="route"/> is the narrowed one, and every
    /// further attempt at this track uses it. So the rest of the record plays from the store, whatever that
    /// computer does in the meantime. The track after it starts again from <see
    /// cref="TrackRoute.Anywhere"/>, and asks the network first, as every track does.</para>
    ///
    /// <para>When there is no other route, it stops rather than pretending. Three tries in the same place
    /// is enough, and a player that is not playing should say so. That is the warning in the library, see
    /// <c>Share_PlaybackLost</c>. The answer is the same whether the store had no copy of this record or
    /// could not be reached either.</para>
    /// </summary>
    private async Task ReopenAsync(
        PlayRequest track, string rpmId, double reached, int session, bool fromPeer, TrackRoute route)
    {
        // This is said once, here, so that nothing else has to find it out again. The shelf narrows itself
        // to what the cloud holds, on the same word.
        if (fromPeer)
        {
            Sharing?.NoteUnreachable(rpmId);

            // The successor goes too, when it was opened from the same computer. The look-ahead opened it
            // over the connection that has just died. The gapless changeover would then hand the pump a
            // decoder that fails on its first read. That track would appear to start and end in the same
            // instant. Dropped, the queue starts the next track when this one is done. That start gets the
            // network again, like any other.
            if (Volatile.Read(ref _next) is { FromPeer: true })
                ClearChain();
        }

        if (!_retries.MayRetry(rpmId))
        {
            Said.Line("play", $"gave up on {rpmId} at {reached:0.#}s");
            NoteSourceLost();
            return;
        }

        await Task.Delay(ReopenPause).ConfigureAwait(false);

        // The listener moved on while this was waiting, to another record or to stop. The session number
        // changes on every Play and every Stop, and on nothing else.
        if (_engine.PlaySession != session)
            return;

        bool started;
        try
        {
            started = await StartCoreAsync(track, reached, route, CancellationToken.None);
        }
        catch (OperationCanceledException) { return; }
        catch { started = false; }

        if (!started)
            NoteSourceLost();
    }

    /// <summary>The music stopped because the connection was lost. This stops the pump, because nothing is
    /// coming out of it. It then reports the state, which is what the library's warning band
    /// displays.</summary>
    public void NoteSourceLost()
    {
        Stop();

        if (SourceLost) return;
        SourceLost = true;
        SourceLostChanged?.Invoke();
    }

    /// <summary>Something is playing again. The report was about a moment, and the moment passed.</summary>
    private void NoteSourceFound()
    {
        if (!SourceLost) return;
        SourceLost = false;
        SourceLostChanged?.Invoke();
    }

    /// <summary>Signs the URLs and fetches the opening seconds of tracks that are about to be wanted, so
    /// neither the start of the next record nor the gapless changeover waits on a round trip. Cheap to call
    /// with tracks that are on the disk: they are simply skipped.</summary>
    public Task WarmAsync(IReadOnlyList<string> rpmIds, CancellationToken ct = default)
        => _stream is null ? Task.CompletedTask : _stream.WarmAsync(rpmIds, ct);

    /// <summary>
    /// Plays audio that this service did not open itself, such as the DJ's two-deck mix. It goes through
    /// the engine's pump. So the equaliser, the visualiser, the volume and the sink (a speaker included)
    /// all work on a mix exactly as they do on a file.
    /// </summary>
    /// <param name="source">Taken over: the engine disposes it when playback stops.</param>
    /// <returns>False when the sound output refused it: the source has been disposed, the refusal has been
    /// reported through <see cref="OutputRefused"/>, and the caller's own idea of what is playing (a
    /// station tuned, a set running) has to be put back.</returns>
    public bool PlaySource(IPcmSource source, double durationSeconds, [Shown] string? title = null, string? artist = null)
    {
        Forget();
        if (_engine.PlaySource(source, durationSeconds, title, artist))
            return true;

        NoteOutputRefused();
        return false;
    }

    /// <summary>The stream currently feeding the sink, for a caller checking whether its own source is
    /// still the one playing. <see cref="PlayingPath"/> moves from track to track during a mix and cannot
    /// answer that question.</summary>
    public object? PlayingSource => _engine.PlayingSource;

    /// <summary>What a multi-track source says it is playing. This is the record in a mix, or the song that
    /// a radio station is announcing. It includes the cover, where there is no file to read one from. Null
    /// for a plain track, whose own tags say everything about it.</summary>
    public RPM.Core.Audio.NowPlayingTrack? NowPlaying => _engine.NowPlaying;

    /// <summary>Tells everything that displays "what is playing" to look again. This is for a source that
    /// plays a succession of tracks and has just moved to the next one.</summary>
    public void NotifyNowPlayingChanged() => _engine.NotifyNowPlayingChanged();

    /// <summary>How much audio may sit between the decoder and the speakers, in seconds. Null gives the
    /// depths that a player wants. See <see cref="AudioEngine.WorkAhead"/>. This is the delay between doing
    /// something to what is playing and hearing it. Only a workbench needs that.</summary>
    public void WorkAhead(double? seconds) => _engine.WorkAhead(seconds);

    /// <summary>Pauses for real. The sink stops, and the track stays loaded.</summary>
    public void Pause() => _engine.Pause();

    public void Resume() => _engine.Resume();

    /// <summary>Jumps to the given second (restarts the decoder there, flushes the sink).</summary>
    public void SeekTo(double seconds)
    {
        if (PlayingPath is null) return;
        _engine.SeekTo(seconds);
    }

    public void Stop()
    {
        Forget();
        _engine.Stop();
    }

    /// <summary>The mono samples that are being played now, for the visualiser. They are in sync, because
    /// they are the very samples the sink is playing, wherever the sink happens to be.</summary>
    public bool CopyPlayFrame(float[] dest) => _engine.CopyPlayFrame(dest);

    public void Dispose()
    {
        SleepBlocker.Hold(false);
        _engine.Dispose();
    }
}
