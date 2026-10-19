using RPM.Core.Diagnostics;
using System.Diagnostics;
using RPM.Core.Cloud;
using RPM.Core.Metadata;
using RPM.Fingerprint;
using RPM.Core.Threading;

namespace RPM.Core.Audio;

/// <summary>
/// Plays RPaMp's audio on a Google Cast device: the <i>real</i> audio, the one that has been
/// through our equaliser, not the original file handed over for the speaker to decode by itself.
///
/// <para>A Cast device is not an output you can write samples to. It is a client: you give it a URL
/// and it fetches the media. So to cast our own pipeline, we publish it. The post-EQ float PCM goes
/// into a small ring, a second ffmpeg encodes that ring to MP3 on the fly, and <see cref="CastServer"/>
/// serves the result as a live, endless HTTP stream that the receiver pulls. Everything above the sink
/// (the EQ, the visualiser, the position, the playlist) works as it does locally, because to them this
/// is just a sink that happens to be slow.</para>
///
/// <para><b>Backpressure runs the whole length of the chain.</b> When the receiver stops pulling, its
/// TCP window closes. The HTTP body write then blocks, the encoder's stdout pipe fills, and ffmpeg
/// stalls. Its stdin pipe fills, the feeder stops draining the ring, and the ring fills. Then
/// <see cref="Enqueue"/> parks and the decoder stops. Nothing is timed and nothing runs ahead. Pausing
/// a Cast device puts the whole chain in this state until the user presses play.</para>
///
/// <para><b>What casting costs.</b> The receiver buffers several seconds before its speaker makes a
/// sound, so a live stream cannot be seeked: seeking (and changing track) tears the stream down and
/// hands the receiver a new one, which is a couple of seconds of silence. And the position can only
/// come from the receiver, since only it knows what is actually coming out.</para>
/// </summary>
public sealed class CastSink : IAudioSink
{
    /// <summary>Audio in flight between the equaliser and the encoder. Small on purpose: it is the
    /// delay between moving an EQ slider and hearing it, and the receiver's own buffer is already
    /// several seconds deep without our help.</summary>
    private const double RingSeconds = 2.0;

    /// <summary>
    /// How far the decoder may run ahead of what the speaker is actually playing.
    ///
    /// <para><b>This is the clock. Without it there is no clock.</b> A natural guess is that the
    /// network paces us: a receiver that stops reading closes its TCP window, and that stalls the
    /// pipeline all the way back to the decoder. It does not. A Cast device has a deep buffer and
    /// pulls as fast as the LAN carries the data. Nothing blocks, so the decoder reaches the end of
    /// the track in seconds. Everything downstream that treats "what we have sent" as "what is being
    /// heard" is then wrong: the position, the visualiser, and the end of the track most of all. The
    /// engine would think the song was over while the speaker was still a verse in. VLC also paces
    /// its output explicitly, for the same reason.</para>
    ///
    /// <para>So the decoder paces itself against the only honest clock available: the receiver's own
    /// reported position. The cost of the lead is latency. An EQ change is not heard until the audio
    /// already in flight has played out. The benefit is a buffer on the speaker side, which carries
    /// the music through a wifi hiccup. Ten seconds is deep enough for that, and short enough that the
    /// EQ still responds quickly.</para>
    /// </summary>
    private const double LeadSeconds = 10.0;

    /// <summary>How long the pipeline may wait with the receiver making no progress before we call
    /// the cast dead. Long enough not to trip on a wifi stall. Not so long that a speaker which has
    /// quietly stopped listening strands the player forever. Paused does not count: a pause is this
    /// same state by design.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>MP3 at its ceiling, when we are not sending FLAC.</summary>
    private const string Mp3Bitrate = "320k";

    /// <summary>
    /// How far the surround stream may run ahead of the speaker before we stop writing.
    ///
    /// <para><b>This is a runaway guard, not a pace.</b> It does not engage in normal running. Two
    /// minutes of AC-3 is ten megabytes, which is more than a media element holds. The receiver stops
    /// reading, and the socket blocks long before this limit is reached.</para>
    ///
    /// <para><b>The transport controls the flow.</b> The receiver buffers what it wants and closes its
    /// window when it is full. Our write waits until the window opens again. This is correct flow
    /// control, it adjusts itself, and it needs no setting.</para>
    ///
    /// <para><b>No pace is applied here.</b> A lead measured against the receiver's reported position
    /// stops sending when the speaker stalls, which is when feeding it matters most. A wall clock
    /// cannot make up a stall, so it always falls behind. The small send buffer on the socket (see
    /// <see cref="CastServer"/>) makes a write block when the receiver stops taking data. That
    /// backpressure is what the design depends on. What is left here is only the bound on how much a
    /// receiver that never pushes back can be sent.</para>
    /// </summary>
    private const double SurroundCeilingSeconds = 120;

    /// <summary>How long the receiver's reported position may stand still before the sink stops
    /// treating it as a measurement. Longer than the gap between statuses and the time one takes to
    /// arrive, and far shorter than <see cref="StallTimeout"/>, which means the speaker has gone.</summary>
    private static readonly TimeSpan ClockStale = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Seconds of music per byte on the surround wire.
    ///
    /// <para>AC-3 at 640k is constant bit rate, so this is exact for the audio. The fragment headers
    /// make the real figure a few tenths of a percent higher. That makes the lead only a few tenths of
    /// a percent longer. The error cannot build up, because the lead is measured against the
    /// receiver's clock and not against our own count.</para>
    /// </summary>
    private const double Ac3BytesPerSecond = 640_000 / 8.0;

    /// <summary>How long the receiver gets to come and fetch the stream before we call it a
    /// failure. It normally connects within a second or two of the LOAD.</summary>
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    private readonly ICastReceiver _receiver;

    /// <summary>Where a cover comes from. Handed in, because where a record's picture is stored is a
    /// question about the collection, not about the audio. See <see cref="SinkArtwork"/>.</summary>
    private readonly SinkArtwork _artwork;
    private readonly CastServer _server;
    private readonly SemaphoreSlim _streamGate = new(1, 1);

    /// <summary>Guards the ratchet in <see cref="FramesPlayed"/>.</summary>
    private readonly object _positionGate = new();

    private PcmRing? _ring;
    private string? _streamId;
    private int _rate = 44100;      // Set by Begin. Until then it is a placeholder for FramesPlayed.
    private int _channels = 2;

    /// <summary>The speaker's own volume control, and the rules for changing it. This is not a gain:
    /// nothing here is applied to the samples. See <see cref="ICastReceiver.SetVolumeAsync"/>.</summary>
    private readonly CastVolume _volume;

    /// <summary>The format this track is being sent in. Pinned at <see cref="Begin"/> so that
    /// flipping <see cref="Format"/> mid-track cannot change the encoder under a stream that the
    /// receiver has already been told the MIME type of.</summary>
    private CastAudioFormat _format;

    // Surround (AC-3 5.1) bypasses the stereo EQ pipeline. It re-encodes the source file directly, so
    // it needs the file itself and the start position, not the ring that the other formats use.
    private bool _surround;

    /// <summary>
    /// The levelling gain for the track being sent, in dB.
    ///
    /// <para>Only the surround path reads it. Every other output takes the samples the pump has
    /// already applied the gain to; a surround stream is re-encoded from the file itself and would
    /// otherwise be the one thing in the app that plays at the wrong level.</para>
    /// </summary>
    private double _gainDb;
    private string? _sourcePath;
    private double _startSeconds;

    /// <summary>
    /// The surround encoder, started at <see cref="Begin"/> rather than when the receiver arrives.
    ///
    /// <para>Spawning ffmpeg, opening the file, seeking to the offset and mixing the first fragment
    /// all have to happen before there is any audio to send. None of them need the receiver. Doing
    /// them while the LOAD is still crossing the network means the answer to the fetch is audio that
    /// already exists. Otherwise the television shows "buffering" before the music starts.</para>
    ///
    /// <para>The encoder runs flat out into its own stdout pipe. That pipe fills after about a second,
    /// and then the encoder stops. Nothing is wasted if the receiver never comes: <see cref="TearDown"/>
    /// kills it.</para>
    /// </summary>
    private Process? _waiting;


    private volatile bool _stopped = true;
    private volatile bool _fetched;
    private volatile bool _paused;

    /// <summary>
    /// True from the moment the speaker is asked to stop until it says it has.
    ///
    /// <para>It tells apart the two things a PLAYING status can mean while we believe we are paused:
    /// someone pressed play in the room, or our own pause has not reached the speaker yet. A status
    /// does not say which. The receiver reports what it is doing, never who asked. So the question is
    /// answered the only way it can be: has it stopped at all since we asked?</para>
    ///
    /// <para>This matters because a stream is loaded in the playing state (see <see cref="Begin"/>: a
    /// receiver that is not playing never fetches the stream). So a track started while the player is
    /// paused always reports PLAYING once before the pause reaches the speaker. If that were read as a
    /// press in the room, music would start at every speaker the listener had sent a paused record
    /// to.</para>
    /// </summary>
    private volatile bool _awaitingPause;
    private long _lastKnownFrames;

    /// <summary>The STOP sent to the receiver on teardown, so that <see cref="Dispose"/> can let it
    /// reach the wire before it closes the connection underneath it.</summary>
    private Task _stopping = Task.CompletedTask;

    /// <summary>True once this sink is finished with. That is also when the connection to the speaker
    /// is closed from this end, so the disconnect that follows is our own and not news.</summary>
    private volatile bool _closed;

    /// <summary>
    /// True from <see cref="Begin"/> until the receiver is actually playing the new stream.
    ///
    /// <para><b>Everything the speaker says during a changeover is about the stream we just took
    /// away.</b> Starting a track tears the old stream down. The receiver reports that as PAUSED, then
    /// IDLE with <c>CANCELLED</c>. That looks the same as someone pressing stop in the room. Without
    /// this flag, next and previous could stop the music instead of changing the track. So while a
    /// changeover is in flight, the speaker's transport reports are not news about the listener's
    /// intentions, and they are ignored.</para>
    /// </summary>
    private volatile bool _changingTrack;


    /// <summary>The favourite button was pressed on the television. The player saves the favourite,
    /// because the sink has no part in the library. The player then tells the receiver what it
    /// saved.</summary>
    public event Action? RemoteFavorite;

    private void OnRemoteFavorite()
    {
        if (_closed) return;
        RemoteFavorite?.Invoke();
    }

    /// <summary>Says whether the record playing is a favourite. Null means the collection does not
    /// hold the record, so the television hides the favourite button instead of showing one that
    /// does not work.</summary>
    public Task SetFavoriteAsync(bool? loved) => _receiver.SetFavoriteAsync(loved);

    /// <summary>Raised on a background thread when the cast fails: the receiver refused the media,
    /// never fetched it, or dropped off the network. The player switches back to the local sink, so
    /// the music does not appear to play to nobody.</summary>
    public event Action<CastTrouble>? Failed;

    /// <summary>Raised for something the listener should know that is not a failure. The music is
    /// still playing. This is kept apart from <see cref="Failed"/>, which ends the cast.</summary>
    public event Action<CastTrouble>? Notice;

    /// <summary>Every <see cref="Failed"/> goes through here, so each one is also written to the ring, as
    /// well as shown on screen. Without that, a cast that failed is gone by the time anyone asks why. The
    /// ring line names the case, not the speaker. The ring's tail is sent with crash reports, and a device
    /// name is the user's own wording.</summary>
    private void RaiseFailed(CastTrouble trouble)
    {
        Said.Line("cast", $"cast failed: {trouble.GetType().Name}");
        Failed?.Invoke(trouble);
    }

    private void RaiseNotice(CastTrouble trouble)
    {
        Said.Line("cast", $"cast notice: {trouble.GetType().Name}");
        Notice?.Invoke(trouble);
    }

    /// <param name="artwork">Where a track's cover comes from. There is no default. The sink looks
    /// in the file at <see cref="SinkTrack.Path"/> and nowhere else. That is right for a record on
    /// this disk, and it gives a blank cover for a borrowed or offloaded one. To use only the file,
    /// pass <see cref="SinkArt.FromTheFileAlone"/>, so the choice is visible at the call site. See
    /// <see cref="SinkArtwork"/>.</param>
    public CastSink(ICastReceiver receiver, SinkArtwork artwork,
                    CastAudioFormat format = CastAudioFormat.Mp3)
    {
        _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
        _artwork = artwork ?? throw new ArgumentNullException(nameof(artwork));
        Format = format;
        _server = new CastServer { OnStream = StreamAsync };
        _server.Start(CastServer.LocalAddressFor(receiver.Address));
        _volume = new CastVolume(level => _receiver.SetVolumeAsync(level));

        _receiver.StatusChanged += OnReceiverStatus;
        _receiver.NextRequested += OnRemoteNext;
        _receiver.PreviousRequested += OnRemotePrevious;
        _receiver.FavoriteRequested += OnRemoteFavorite;
        _receiver.ArtworkRefused += OnArtworkRefused;
        _receiver.Starved += OnStarved;
    }

    /// <summary>
    /// The speaker ran out of audio in the middle of a track.
    ///
    /// <para>This is logged and nothing else is done. The sink already sends as fast as the speaker
    /// reads, so a larger lead would not help. A gap at this point is not something a number on this
    /// side can fix. It shows in the log so that the cause is found in the network, the encoder or the
    /// receiver, not in the pacing.</para>
    /// </summary>
    private void OnStarved() =>
        Said.Line("cast", "the receiver ran dry while being fed as fast as it reads");

    private void OnRemoteNext()
    {
        if (!_closed && !_stopped) RemoteNext?.Invoke();
    }

    private void OnRemotePrevious()
    {
        if (!_closed && !_stopped) RemotePrevious?.Invoke();
    }

    /// <summary>
    /// The receiver could not fetch the cover we published, so it gets the bytes instead. These are the
    /// same bytes the small server holds for this track.
    ///
    /// <para><b>Answered even when there are none.</b> The cover's URL goes out in the LOAD before the
    /// tags are read, so a URL is published for every track. Some tracks have no picture at all. For
    /// those the server returns a 404, and the receiver asks us about it. If we say nothing, the
    /// receiver keeps the previous record's cover on the screen, over the wrong song.</para>
    /// </summary>
    private void OnArtworkRefused()
    {
        if (_closed) return;
        _ = Swallow(AnswerArtworkAsync());
    }

    /// <summary>
    /// Answers the receiver's question about the cover, once there is an answer to give.
    ///
    /// <para>The wait is needed. The cover is read off the thread that starts a track, so for the first
    /// moment of every track there is no answer yet. A receiver that refuses our URL as mixed content
    /// never touches the small server, which is the other place that waits. It reports straight from
    /// the browser's own image load, which fails at once. If we answered before the picture was ready,
    /// we would say "this track has no cover" about one that has a good cover.</para>
    /// </summary>
    private async Task AnswerArtworkAsync()
    {
        await _server.WaitForArtworkAsync(CancellationToken.None);
        if (_closed) return;

        await _receiver.SendArtworkAsync(_server.Artwork);
    }

    /// <summary>
    /// Everything the speaker reports about itself arrives here. This includes changes made by someone
    /// standing next to it, which is the only way we learn about those.
    ///
    /// <para>Silent once torn down: the teardown closes the connection, so a failure raised here would
    /// tell the player to stop casting while it is already stopping, or while it starts the next
    /// speaker.</para>
    /// </summary>
    private void OnReceiverStatus()
    {
        if (_closed) return;

        if (!_receiver.IsConnected)
        {
            RaiseFailed(new CastTrouble.Disconnected(_receiver.Name));
            return;
        }

        AdoptDeviceVolume();

        // The speaker playing again means the changeover is over. From here on, its reports describe
        // the stream it now holds. Waiting only for the fetch would leave the gate shut on a receiver
        // that never fetches the stream, and then every command from the room would be ignored.
        if (_changingTrack && _receiver.Playback is CastPlayback.Playing or CastPlayback.Buffering)
            _changingTrack = false;

        // Mid-changeover the speaker is describing the stream we just replaced. See _changingTrack.
        if (_stopped || _changingTrack) return;

        switch (_receiver.Playback)
        {
            // The speaker has paused. This answers our own pause request, and it can also report a press
            // on the speaker. Either way the wait for our pause is over. Only a press on the speaker is
            // news for the player. Our own pause never reads as a press, because _paused is set before
            // the receiver reports it back. A press on the speaker can come from its touch controls, the
            // Home app, or a voice command such as "Hey Google, pause".
            case CastPlayback.Paused:
                _awaitingPause = false;
                if (!_paused) RemotePaused?.Invoke();
                break;

            // The speaker is playing, but our pause has not been answered yet. This is the LOAD we are
            // still stopping, not a press in the room. Ask again. The first request crossed the network
            // next to the LOAD, and a receiver with no media session yet has nothing to pause.
            case CastPlayback.Playing when _paused && _awaitingPause:
                _ = Swallow(_receiver.PauseAsync());
                break;

            case CastPlayback.Playing when _paused:
                RemoteResumed?.Invoke();
                break;

            // Stopped, not finished. This difference is why idleReason is read. When our stream runs out,
            // the track has ended and the playlist moves on. A stop at the speaker means someone asked for
            // silence, and playing the next song would do the opposite of what they asked for.
            case CastPlayback.Idle when _receiver.Idle is CastIdle.Stopped or CastIdle.Interrupted:
                RemoteStopped?.Invoke();
                break;
        }
    }

    /// <summary>Someone paused it at the speaker: its own controls, the Google Home app, or a voice
    /// command. The player follows, so its own transport shows what is happening in the room.</summary>
    public event Action? RemotePaused;

    /// <summary>Someone started it again at the speaker.</summary>
    public event Action? RemoteResumed;

    /// <summary>Someone stopped it at the speaker, or the speaker was taken over by something else.
    /// This is not the same as the track ending. See <see cref="CastIdle"/>.</summary>
    public event Action? RemoteStopped;

    /// <summary>Next at the speaker. Only RPM's own receiver reports this; on Google's stock one the
    /// press is indistinguishable from a stop and arrives as <see cref="RemoteStopped"/>.</summary>
    public event Action? RemoteNext;

    /// <summary>Previous at the speaker.</summary>
    public event Action? RemotePrevious;

    /// <summary>What the speaker says it can decode, or null on a receiver that cannot be asked.</summary>
    public CastCapabilities? Capabilities => _receiver.Capabilities;

    /// <summary>
    /// The playlist moved on without the stream stopping. Only the screen changes. The receiver keeps
    /// the same endless stream it has been playing. Giving it a new stream would cause the silence that
    /// the gapless changeover exists to avoid.
    /// </summary>
    public void TrackChanged(SinkTrack track)
    {
        if (_stopped || _closed) return;

        // Where in the stream this change lands: everything written so far has still to be played.
        var changeAt = _ring is { } ring ? ring.WrittenFrames / (double)_rate : 0;

                // Runs off this thread. It is called from the pump at the changeover, where the pump holds
                // the tail of a track, and reading the art means opening a file.
        _ = Task.Run(async () =>
        {
            try
            {
                var art = await _artwork(track, CancellationToken.None);
                _server.SupplyArtwork(art?.Bytes, art?.Mime ?? "image/jpeg");

                // The URL names the picture, not the track.
                //
                // It has to change when the cover changes. Otherwise the receiver keeps showing the first
                // cover for the rest of the playlist, because it has already fetched that URL and has no
                // reason to ask again. The URL is built from the picture's contents. A new cover gets a new
                // URL and is fetched. The same cover keeps the same URL, so the receiver serves it from its
                // own cache. The route ignores everything except the prefix, so the name can be any value.
                var artUrl = art is null ? null : $"{_server.BaseUrl}/art/{KeyFor(art.Bytes)}.jpg";

                await _receiver.UpdateNowPlayingAsync(new CastNowPlaying(
                    track.Title, track.Artist, track.Album, artUrl,
                    DurationSeconds: track.DurationSeconds > 0 ? track.DurationSeconds : null,

                    // The new track starts at its beginning. It starts where the audio already written to
                    // the ring ends. That is where the speaker will be in the stream when it hears the
                    // changeover, about <see cref="LeadSeconds"/> from now.
                    TrackAtSeconds: 0,
                    StreamAtSeconds: changeAt));
            }
            catch { /* a failed screen update must not stop the music */ }
        });
    }

    /// <summary>
    /// Names a cover by its contents, so the same picture always gets the same URL and a different
    /// picture gets a different one. This runs once per track, off the pump's thread, so hashing the
    /// cover takes too little time to notice.
    ///
    /// <para>This is not a security check, and it does not need to be unique. If two covers collided,
    /// the television would show one stale picture. Eight bytes of digest is enough.</para>
    /// </summary>
    private static string KeyFor(byte[] artwork) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(artwork), 0, 8)
               .ToLowerInvariant();

    /// <summary>How the audio is encoded on its way out. Takes effect on the next <see cref="Begin"/>.
    /// The caller restarts the stream to apply it in the middle of a track.</summary>
    public CastAudioFormat Format { get; set; }

    private static string ContentTypeFor(CastAudioFormat format) => format switch
    {
        // Matroska is what lets a lossless codec be streamed at all: FLAC has no way to say "length
        // unknown", and Matroska does. Same container VLC muxes into, and the same MIME it sends.
        CastAudioFormat.Flac => "audio/x-matroska",
        // Fragmented MP4 with the codecs parameter is the only AC-3 form that Google documents for the
        // receiver. An audio-only MPEG-TS labelled video/mp2t makes an NVIDIA Shield look for a video
        // track and refuse the LOAD. audio/mp4 is clearly audio.
        CastAudioFormat.Ac3Surround => "audio/mp4; codecs=\"ac-3\"",
        _ => "audio/mpeg",
    };

    /// <summary>The URL's extension. It has no meaning to us, and the route ignores it. Some receivers
    /// sniff the URL instead of trusting the Content-Type. Telling one of those that a Matroska stream
    /// is an <c>.mp3</c> sends it to the wrong demuxer.</summary>
    private static string ExtensionFor(CastAudioFormat format) => format switch
    {
        CastAudioFormat.Flac => "mka",
        CastAudioFormat.Ac3Surround => "mp4",
        _ => "mp3",
    };

    public string DeviceName => _receiver.Name;

    public bool IsAvailable => _receiver.IsConnected && _server.IsRunning;

    /// <summary>
    /// Frames the <i>receiver</i> has played. Taken from its status reports, because nothing on this
    /// side of the network knows what is coming out of the speaker: the audio we handed over is
    /// still in an encoder, a socket, and several seconds of the receiver's buffer.
    ///
    /// <para>Read concurrently by the position timer and the visualiser. It only moves forward, so
    /// reads need a lock. Two readers running at once could otherwise each see a stale maximum and let
    /// the position move backwards.</para>
    /// </summary>
    /// <summary>The receiver's clock is a field on this side, not a device to query. It is safe to read
    /// from the UI thread. That keeps the seek bar moving while the pump is blocked, and during the
    /// drain at the end of a track. See <see cref="IAudioSink.ReportsPositionItself"/>.</summary>
    public bool ReportsPositionItself => true;

    public long FramesPlayed
    {
        get
        {
            var reported = _receiver.PositionSeconds;
            var ring = _ring;

            lock (_positionGate)
            {
                if (reported is double seconds)
                {
                    // Never let it walk backwards: status pushes jitter, and a position that
                    // stutters backwards makes the seek bar and the visualiser twitch.
                    var frames = (long)(seconds * _rate);
                    if (frames > _lastKnownFrames) _lastKnownFrames = frames;
                    return _lastKnownFrames;
                }

                // The receiver hasn't told us yet. Fall back to what has been pulled out of the ring,
                // less the audio we know is still in flight, so the UI moves rather than sitting at
                // zero for the second or two before the receiver's first status arrives.
                if (ring is null) return _lastKnownFrames;
                var estimate = ring.ReadFrames - (long)(RingSeconds * _rate);
                return _lastKnownFrames = Math.Max(_lastKnownFrames, Math.Max(0, estimate));
            }
        }
    }

    public void Begin(SinkTrack track, int sampleRate, int channels)
    {
        // Tear down the previous track's stream, but do NOT tell the receiver to stop: the LOAD
        // below replaces its media anyway, and a fire-and-forget STOP racing that LOAD across the
        // network would land after it and kill the track we are starting. Nothing orders the two.
        TearDown(stopReceiver: false);

        lock (_positionGate) _lastKnownFrames = 0;
        _rate = sampleRate;
        _channels = channels;
        _fetched = false;
        _stopped = false;
        _changingTrack = true;

        var ring = new PcmRing((int)(RingSeconds * sampleRate), channels);
        _ring = ring;

        // A fresh id for every track, and for every seek, because a seek starts a new stream. A receiver
        // that still holds the previous stream's URL gets a 404 instead of the wrong song.
        var id = Guid.NewGuid().ToString("n");
        _streamId = id;

        // What the device says it can decode beats what the listener asked for. Surround is the case
        // that matters. A receiver that will not take AC-3 answers a LOAD with a refusal, and the room
        // gets silence where stereo would have played the music. A receiver that cannot be asked
        // (Google's stock one) reports nothing, and nothing means "try it".
        //
        // The question asked is whether the receiver's player will hold AC-3 now, not whether the box is
        // a Dolby box. Those differ in practice. An amplifier switched off, or an output that has changed
        // to the television's own speakers, turns the decode off while the box stays just as capable.
        // See CastCapabilities.Ac3Playable.
        var format = Format;
        if (format == CastAudioFormat.Ac3Surround && _receiver.Capabilities is { Ac3Playable: false } caps)
        {
            format = CastAudioFormat.Mp3;
            RaiseNotice(caps.Ac3
                // The box can decode Dolby, but its player will not. This usually means the sound is not
                // going to an output that takes Dolby right now. Say so: the user can fix it in the room,
                // and without a message the app looks broken.
                ? new CastTrouble.DolbyOff(_receiver.Name)
                : new CastTrouble.NoSurround(_receiver.Name));
        }

        _format = format;
        _surround = format == CastAudioFormat.Ac3Surround;
        _sourcePath = track.Path;
        _startSeconds = track.StartSeconds;

        _server.StreamContentType = ContentTypeFor(format);
        _server.StreamId = id;

        // The cover is read off this thread, and the LOAD does not wait for it.
        //
        // Reading it means opening the track and pulling a picture out of its tags. That takes time on a
        // big file, and the music should not wait for it. A cover is a courtesy on a television, so it must
        // not delay the music. The URL can be published before the bytes exist, because the server holds a
        // fetch for it until they do (see CastServer.ExpectArtwork).
        //
        // The art comes from whoever built this sink, not from the file here. A record borrowed from
        // another computer has a path that belongs to that computer. A record this machine has offloaded
        // has no file at all. Both can play, and both would cast with no cover. See SinkArtwork.
        _server.ExpectArtwork();
        _ = Task.Run(async () =>
        {
            try
            {
                var art = await _artwork(track, CancellationToken.None);
                _server.SupplyArtwork(art?.Bytes, art?.Mime ?? "image/jpeg");
            }
            catch { _server.SupplyArtwork(null, "image/jpeg"); }
        });

        // Started now, not when the receiver's fetch arrives. Spawning ffmpeg, opening the file, seeking
        // and mixing the first fragment all happen while the LOAD is still crossing the network. So the
        // answer to the fetch is audio that already exists. See _waiting.
        if (_surround) _waiting = StartSurroundEncoder();

        var media = new CastMedia(
            Url: $"{_server.BaseUrl}/stream/{id}.{ExtensionFor(format)}",
            ContentType: ContentTypeFor(format),
            // The whole track's length, even when this stream starts partway in after a seek. The
            // receiver's clock runs within the stream. FramesPlayed's caller adds the seek offset back on,
            // so the user sees the position in the track.
            //
            // Surround is the exception. This is about the box's own AC-3 pipeline, not about which
            // receiver is at the other end. A buffered fragmented-MP4 AC-3 stream never leaves "buffering"
            // on an NVIDIA Shield, but a live one plays and lights up Dolby Digital. So surround goes out
            // with no duration, whichever receiver is listening. The position still tracks, because a
            // receiver reports its clock in a live stream too, and the track's own position is put back
            // together here.
            DurationSeconds: _surround ? null : (track.DurationSeconds > 0 ? track.DurationSeconds : null),
            Title: track.Title,
            Artist: track.Artist,
            Album: track.Album,
            // The URL is published before it is known whether the track has a cover. The other option would
            // be to hold the LOAD until the tags are read. A receiver that fetches a cover this track turns
            // out not to have gets a 404, and it draws the same screen as for any track with no cover.
            ArtworkUrl: $"{_server.BaseUrl}/art/{id}.jpg");

        _ = LoadAsync(media, id, ring, track.StartSeconds,
                      track.DurationSeconds > 0 ? track.DurationSeconds : null);
    }

    /// <summary>Tells the receiver to fetch the stream, and checks that it does. If the LOAD fails, or
    /// succeeds and the stream is then ignored, the sink must be taken down. Otherwise the pump would
    /// wait forever, filling a ring that nobody drains.</summary>
    private async Task LoadAsync(
        CastMedia media, string streamId, PcmRing ring, double startedAt, double? trackSeconds)
    {
        try
        {
            await _receiver.LoadAsync(media);

            // A stream started while the player is paused is stopped as soon as stopping is possible. There
            // is no media session to pause until the LOAD has been taken, and the LOAD has to autoplay, or
            // the receiver never fetches the stream. So the speaker plays only for as long as it takes this
            // request to cross the network.
            if (_paused && _streamId == streamId)
                await Swallow(_receiver.PauseAsync());

            // The screen's clock, for the track that opened this stream. The stream starts at zero. The
            // track starts at the play position, which is not the beginning after a seek.
            //
            // The length here is the track's length, not the LOAD's. Surround is loaded with no duration
            // (see Begin). That is only a fact about the decoder. The television does not need to be told
            // that a song has no end, because the screen's clock runs on the control channel.
            await _receiver.UpdateNowPlayingAsync(new CastNowPlaying(
                media.Title, media.Artist, media.Album, media.ArtworkUrl,
                trackSeconds, TrackAtSeconds: startedAt, StreamAtSeconds: 0));

            // We do not push our volume here. The speaker's own knob is the source of truth (see
            // ICastReceiver.SetVolumeAsync). If the app sent its number when a cast starts, a speaker at
            // 30% could jump to 100% just because the player was on screen. So the player adopts the
            // speaker's level instead, and the slider only changes the speaker once someone moves it.
            AdoptDeviceVolume();

            var waited = TimeSpan.Zero;
            var step = TimeSpan.FromMilliseconds(250);
            while (!_fetched && !_stopped && waited < FetchTimeout)
            {
                await Task.Delay(step);
                waited += step;
            }

            if (!_fetched && !_stopped && ReferenceEquals(_ring, ring))
                Fail(new CastTrouble.NeverFetched(_receiver.Name), ring, streamId);
        }
        catch (Exception ex) when (!_stopped)
        {
            Faults.Note("cast", "loading the stream", ex);
            Fail(new CastTrouble.StreamRefused(_receiver.Name), ring, streamId);
        }
    }

    private void Fail(CastTrouble reason, PcmRing ring, string streamId)
    {
        _changingTrack = false;
        // Closing the ring is what unsticks the pump: its Enqueue is parked in there.
        ring.Close();
        if (_streamId == streamId) _stopped = true;
        RaiseFailed(reason);
    }

    /// <summary>The receiver's position in frames, or null while it has not reported yet. Null is not the
    /// same as zero, and <see cref="Pace"/> must tell them apart.</summary>
    private long? ReceiverFrames =>
        _receiver.PositionSeconds is double seconds ? (long)(seconds * _rate) : null;

    public bool Enqueue(ReadOnlySpan<float> interleaved, int frames)
    {
        var ring = _ring;
        if (ring is null || _stopped) return false;
        if (!Pace(ring)) return false;
        return ring.Write(interleaved, frames);
    }

    /// <summary>
    /// Blocks until the speaker has caught up to within <see cref="LeadSeconds"/>. This is what makes
    /// <see cref="Enqueue"/> act as a clock and not just a queue. A pause on the speaker works like this:
    /// the position stops advancing, the lead reaches its limit, and the decoder stands still until the
    /// music starts again.
    /// </summary>
    /// <returns>False if the sink was torn down (or gave up on the receiver) while waiting.</returns>
    private bool Pace(PcmRing ring)
    {
        var leadFrames = (long)(LeadSeconds * _rate);
        var stalled = Stopwatch.StartNew();
        long lastSeen = -1;

        while (!_stopped)
        {
            // Null (the receiver has not reported yet) counts as zero, not as "no limit". Before the
            // speaker starts playing, we should not send it more than the lead either.
            var played = ReceiverFrames ?? 0;

            // The limit is doubled while the receiver is refilling its buffer. Its clock stops when it
            // stops playing. If the ordinary lead were kept against that frozen number, the buffer that is
            // trying to recover would be starved. The ring keeps a real lead, which the surround path does
            // not, because this lead is also the equaliser's latency. See LeadSeconds.
            var room = _receiver.Playback is CastPlayback.Buffering && !_paused ? leadFrames * 2 : leadFrames;
            if (ring.WrittenFrames - played <= room) return true;
            if (ring.IsClosed) return false;

            if (played != lastSeen)
            {
                lastSeen = played;
                stalled.Restart();     // it is moving, so we are simply ahead of it, which is normal
            }
            else if (!_paused && stalled.Elapsed > StallTimeout)
            {
                // The ring is full, and the speaker has not played a single frame in half a minute. It is
                // not buffering. It is gone. Without this check the pump would wait here forever.
                RaiseFailed(new CastTrouble.StoppedPlaying(_receiver.Name));
                return false;
            }

            Thread.Sleep(20);
        }
        return false;
    }

    /// <summary>
    /// Waits for the speaker to finish playing what it holds. This is the real end of the track, not the
    /// end of the decode. When casting, the two are seconds apart. Telling them apart stops the playlist
    /// from cutting off the last verse.
    /// </summary>
    public void Drain(CancellationToken cancellation = default)
    {
        var ring = _ring;
        if (ring is null) return;

        // No more audio is coming. The feeder drains what is left and closes the encoder's stdin.
        // ffmpeg flushes and exits, and the HTTP body ends. Only then does the receiver start playing
        // the seconds it still holds.
        ring.CompleteWriting();
        ring.WaitDrained(cancellation);

        // Wait for the receiver to make progress, not for a fixed time. A fixed deadline is wrong in both
        // directions. It can cut off the end of a track. Or, when the receiver has a large buffer of its
        // own, it can fire while the speaker is still playing and move the playlist on too early. Progress
        // is the only sign that means "it is still playing".
        var stalled = Stopwatch.StartNew();
        long lastSeen = -1;

        while (!_stopped
               && !cancellation.IsCancellationRequested
               && !_receiver.IsIdle
               && _receiver.IsConnected)
        {
            var played = ReceiverFrames ?? -1;
            if (played != lastSeen)
            {
                lastSeen = played;
                stalled.Restart();
            }
            else if (!_paused && stalled.Elapsed > StallTimeout)
            {
                return;   // It has stopped reporting, so do not leave the playlist stuck on it
            }

            Thread.Sleep(100);
        }
    }

    public void Pause()
    {
        if (_stopped) return;
        _paused = true;

        // The pause stays outstanding until the speaker reports that it has stopped. If the speaker has
        // already stopped, which is the normal case when the player follows a pause made in the room, the
        // pause is not outstanding. See _awaitingPause.
        _awaitingPause = _receiver.Playback is not CastPlayback.Paused;
        _ = Swallow(_receiver.PauseAsync());
        // The receiver's position stops advancing, so the lead reaches its limit and the pump waits. That
        // is the pause. Nothing else needs to hold the audio back.
    }

    public void Resume()
    {
        if (_stopped) return;
        _paused = false;
        _awaitingPause = false;
        _ = Swallow(_receiver.PlayAsync());
    }

    public void Stop() => TearDown(stopReceiver: true);

    /// <summary>Ends the current stream. <paramref name="stopReceiver"/> distinguishes the two
    /// reasons we ever do that: the user stopped the music (tell the speaker), or we are about to
    /// hand the speaker a different track (do not; see <see cref="Begin"/>).</summary>
    private void TearDown(bool stopReceiver)
    {
        _stopped = true;
        _paused = false;
        _awaitingPause = false;
        _streamId = null;
        _server.StreamId = null;

        var ring = _ring;
        _ring = null;
        ring?.Close();          // releases the pump in Enqueue and the feeder in Read

        // An encoder may have been started for a receiver that never came for the stream. Nothing else
        // reads it, and it would hold the file open for as long as the app runs.
        var waiting = Interlocked.Exchange(ref _waiting, null);
        if (waiting is not null)
        {
            try { if (!waiting.HasExited) waiting.Kill(); } catch { }
            waiting.Dispose();
        }

        if (stopReceiver)
            _stopping = Swallow(_receiver.StopMediaAsync());
    }

    /// <summary>
    /// The gain for the surround encoder, which is started at <see cref="Begin"/> and reads it then.
    ///
    /// <para>It is not applied to a stream already running. Restarting an AC-3 encoder in the middle of a
    /// track means a new LOAD on the receiver. That causes a gap and a re-buffer, which is much more
    /// disruptive than finishing the track at its starting level. The next track uses the new gain.</para>
    /// </summary>
    public void SetTrackGain(double gainDb) => _gainDb = double.IsFinite(gainDb) ? gainDb : 0;

    /// <summary>
    /// The listener moved the slider.
    ///
    /// <para>Only sent when there is a stream to send it on. The engine pushes its own level to a sink as
    /// soon as the sink is attached. That push must not reach the speaker. See <see
    /// cref="CastVolume.Set"/>.</para>
    /// </summary>
    public void SetVolume(int volume) => _volume.Set(volume, live: !_stopped);

    /// <summary>Raised when the volume came from the speaker, not from us: its own buttons, the Google Home
    /// app, or the level it was already at when the cast started. The player moves its slider to
    /// match.</summary>
    public event Action<int>? VolumeChanged;

    /// <summary>Takes the speaker's current volume as the player's own, unless we are the ones
    /// changing it, which <see cref="CastVolume.Adopt"/> is the judge of.</summary>
    private void AdoptDeviceVolume()
    {
        if (_volume.Adopt(_receiver.DeviceVolume) is int volume) VolumeChanged?.Invoke(volume);
    }

    /// <summary>
    /// Serves the live stream: a fresh ffmpeg encodes the ring to MP3, and its output goes straight
    /// down the socket. Writing to <paramref name="body"/> is where the pipeline is paced. The write
    /// blocks whenever the receiver's buffer is full.
    /// </summary>
    private async Task StreamAsync(string streamId, Stream body, CancellationToken cancellation)
    {
        var ring = _ring;
        if (ring is null || _streamId != streamId) return;

        // One feeder at a time. A receiver that reconnects supersedes its own earlier fetch, and two
        // threads pulling from the same ring would tear the audio in half.
        if (!await Cancellable.WaitAsync(_streamGate, cancellation))
            return;
        try
        {
            if (_ring is null || _streamId != streamId) return;
            _fetched = true;

            // The receiver has come for the new stream: what it says from here is about this track.
            _changingTrack = false;

            // The surround encoder was started at Begin and has been filling its pipe ever since, so
            // the answer to this fetch is audio that already exists. Anything else starts here.
            var ready = Interlocked.Exchange(ref _waiting, null);
            using var encoder = ready ?? StartEncoder()
                ?? throw new InvalidOperationException("ffmpeg would not start.");

            // Normal formats encode the ring that the engine fills. Surround re-encodes the file itself, so
            // its encoder ignores the ring. The engine still decodes into the ring, because the ring is the
            // transport clock (position, pacing, drain to the track end). So the ring must still be
            // emptied, or it fills and stalls the decode. The drainer reads and discards it.
            var feeder = _surround
                ? new Thread(() => DiscardRing(ring, cancellation)) { IsBackground = true, Name = "RPaMp cast ring drain" }
                : new Thread(() => Feed(encoder, ring, cancellation)) { IsBackground = true, Name = "RPaMp cast encoder feed" };
            feeder.Start();

            try
            {
                // The ring formats are paced by the ring. Enqueue holds the ring to the receiver's clock,
                // and the equaliser's latency depends on that. Surround has no such hold. It goes out as
                // fast as the receiver reads. See PaceOutAsync.
                if (_surround)
                    await PaceOutAsync(encoder.StandardOutput.BaseStream, body, cancellation);
                else
                    await encoder.StandardOutput.BaseStream.CopyToAsync(body, cancellation);

                await body.FlushAsync(cancellation);
            }
            finally
            {
                feeder.Join(TimeSpan.FromSeconds(2));
                try { if (!encoder.HasExited) encoder.Kill(); } catch { }
            }
        }
        catch (OperationCanceledException) { /* superseded or stopped, both are normal */ }
        catch (Exception ex) when (!_stopped)
        {
            Faults.Note("cast", "streaming", ex);
            RaiseFailed(new CastTrouble.StreamBroke(_receiver.Name));
        }
        finally
        {
            _streamGate.Release();
        }
    }

    /// <summary>
    /// Copies the encoder's output to the socket, as fast as the receiver takes it.
    ///
    /// <para><b>The transport controls the flow.</b> The receiver buffers what it wants and stops reading
    /// when its buffer is full. Its TCP window closes. The socket has a small send buffer (see
    /// <see cref="CastServer"/>), so the write below waits within a fraction of a second of that. This is
    /// correct flow control. The receiver decides how much it wants, and nothing needs tuning. The
    /// encoder behind it stops when its stdout pipe fills, so nothing runs ahead upstream either.</para>
    ///
    /// <para>What is left is a bound on how far ahead this may get (<see cref="SurroundCeilingSeconds"/>)
    /// for a receiver that never pushes back at all, and the stall check that ends a cast whose speaker
    /// has stopped listening. In normal running neither applies.</para>
    /// </summary>
    private async Task PaceOutAsync(Stream source, Stream body, CancellationToken cancellation)
    {
        var buffer = new byte[16 * 1024];
        long sent = 0;

        var stalled = Stopwatch.StartNew();
        double lastSeen = double.NaN;

        while (true)
        {
            int n = await source.ReadAsync(buffer, cancellation);
            if (n == 0) return;                  // the encoder reached the end of the file

            // Blocks whenever the receiver stops reading. This block is the flow control.
            await body.WriteAsync(buffer.AsMemory(0, n), cancellation);
            sent += n;

            while (!cancellation.IsCancellationRequested)
            {
                var played = _receiver.PositionSeconds ?? 0;
                if (played != lastSeen)
                {
                    lastSeen = played;
                    stalled.Restart();
                }

                // This is a guard, not a pace. Only a receiver that keeps accepting data without playing
                // gets this far. Two minutes of AC-3 is more than any receiver holds.
                if (sent / Ac3BytesPerSecond - played <= SurroundCeilingSeconds) break;

                if (_paused)
                {
                    if (!await Cancellable.DelayAsync(100, cancellation))
                        return;
                    continue;
                }

                // Standing still for half a minute is not a late status. It means the speaker has stopped
                // listening. Without this check the stream would wait here forever.
                if (stalled.Elapsed > StallTimeout)
                {
                    RaiseFailed(new CastTrouble.StoppedPlaying(_receiver.Name));
                    return;
                }

                // A stopped clock is not a reading, and the arithmetic above depends on the clock. So
                // instead of holding back against a number that is not a measurement, keep sending. The
                // socket is the real backstop, and it blocks when the receiver stops taking data.
                if (stalled.Elapsed > ClockStale) break;

                if (!await Cancellable.DelayAsync(100, cancellation))
                    return;
            }
        }
    }

    /// <summary>Copies the ring to the encoder's stdin, until the track ends or this connection is
    /// superseded. It runs on its own thread by design: <see cref="PcmRing.Read"/> blocks, and that
    /// blocking is intended.</summary>
    private void Feed(Process encoder, PcmRing ring, CancellationToken cancellation)
    {
        var samples = new float[4096 * _channels];
        var bytes = new byte[samples.Length * sizeof(float)];
        var stdin = encoder.StandardInput.BaseStream;

        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                int n = ring.Read(samples, cancellation);
                if (n == 0) break;                      // track finished, or we were torn down

                Buffer.BlockCopy(samples, 0, bytes, 0, n * sizeof(float));
                stdin.Write(bytes, 0, n * sizeof(float));   // blocks once ffmpeg's pipe is full
            }
            stdin.Flush();
        }
        catch { /* the encoder went away, or we were cancelled mid-write */ }
        finally
        {
            // EOF on stdin is what makes ffmpeg flush its last MP3 frames and exit, which ends the
            // HTTP body cleanly. Killing it instead would truncate the tail of the track.
            try { encoder.StandardInput.Close(); } catch { }
        }
    }

    /// <summary>Empties the ring and throws the samples away. Surround does not encode the ring, because it
    /// re-encodes the file. The engine still fills the ring as the transport clock, so something has to
    /// keep draining it. <see cref="PcmRing.Read"/> blocks, which keeps this in step with the
    /// decode.</summary>
    private static void DiscardRing(PcmRing ring, CancellationToken cancellation)
    {
        var scratch = new float[4096 * ring.Channels];
        try
        {
            while (!cancellation.IsCancellationRequested && ring.Read(scratch, cancellation) > 0) { }
        }
        catch { /* torn down or cancelled; the samples are discarded anyway */ }
    }

    private Process? StartEncoder()
    {
        if (_surround)
            return StartSurroundEncoder();

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var psi = new ProcessStartInfo(ToolPaths.Ffmpeg)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("f32le");
        psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add(_rate.ToString(inv));
        // The INPUT is the ring. It has as many channels as the decoded track, for example 6 for a 5.1
        // source.
        psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add(_channels.ToString(inv));
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("-");

        // Downmix the OUTPUT to stereo. The Cast pipeline is stereo in both formats. MP3 cannot carry more
        // than two channels. A 5.1 source makes libmp3lame abort, so nothing plays. The default Cast
        // receiver also does not decode multichannel FLAC. ffmpeg applies the standard 5.1 to 2.0 downmix,
        // so a surround file still casts, folded to stereo.
        psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add("2");

        if (_format == CastAudioFormat.Flac)
        {
            psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("flac");
            // The samples are floats and FLAC is an integer codec, so they are converted to 32-bit integers
            // here. The precision is far below the noise floor of any playback this will reach.
            psi.ArgumentList.Add("-sample_fmt"); psi.ArgumentList.Add("s32");
            // Matroska in live mode allows a stream with no length. Without it, the muxer seeks back to
            // write a duration into the header, and a socket cannot seek. VLC uses the same setting:
            // mux=matroska,options={live=1}.
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("matroska");
            psi.ArgumentList.Add("-live"); psi.ArgumentList.Add("1");
        }
        else
        {
            psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("libmp3lame");
            psi.ArgumentList.Add("-b:a"); psi.ArgumentList.Add(Mp3Bitrate);
            // A Xing/LAME header describes a file's length and seek points. This is a live stream
            // with neither, and writing one makes the receiver believe things that are not true.
            psi.ArgumentList.Add("-write_xing"); psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("-id3v2_version"); psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("mp3");
        }

        // Get each frame onto the wire as it is made; buffering here just deepens a latency that is
        // already dominated by the receiver.
        psi.ArgumentList.Add("-flush_packets"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-");

        try
        {
            var proc = Process.Start(psi);
            if (proc is null) return null;
            _ = proc.StandardError.ReadToEndAsync();   // drain, or a full pipe stalls the encoder
            return proc;
        }
        catch { return null; }
    }

    /// <summary>
    /// Encodes the source file to AC-3 5.1. This is the surround path, and it does not use the stereo ring
    /// at all. It seeks to where playback began. A seek restarts the whole stream, so the receiver's clock
    /// still starts at zero, and the position is put back together on this side.
    /// </summary>
    private Process? StartSurroundEncoder()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (_sourcePath is null)
            return null;

        var psi = new ProcessStartInfo(ToolPaths.Ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");

        // Seek before -i so ffmpeg jumps to the offset instead of decoding up to it.
        if (_startSeconds > 0)
        {
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(_startSeconds.ToString("F3", inv));
        }
        // No ffmpeg -re or -readrate option is used. This encoder runs as fast as it can, and its stdout
        // pipe fills when the socket stops being read. The pace comes from the receiver, through the
        // transport, back to here. A wall clock cannot catch up after a stall, so each stall would be a
        // permanent loss, and the losses would add up to stutter.
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(_sourcePath);

        psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0:a:0");
        psi.ArgumentList.Add("-vn");

        // The levelling gain is applied here, because this is the only path that never sees our PCM. A
        // surround stream is re-encoded straight from the source file, so the gain that the pump applies
        // for every other output has nothing to act on. Without this, a 5.1 record would be the only thing
        // in the app that ignores the setting.
        if (Math.Abs(_gainDb) >= 0.01)
        {
            psi.ArgumentList.Add("-filter:a");
            psi.ArgumentList.Add($"volume={_gainDb.ToString("F2", inv)}dB");
        }

        psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("ac3");
        // 5.1, at AC-3's standard ceiling. A source with fewer channels is up-mixed to 5.1 (the extra
        // channels are simply silent); a wider one folds down to 5.1.
        psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add("6");
        psi.ArgumentList.Add("-b:a"); psi.ArgumentList.Add("640k");

        // Fragmented MP4, streamable to a socket: empty_moov writes no up-front index, delay_moov is
        // required for AC-3 (its parameters aren't known until the first packet), and frag_keyframe
        // emits self-contained fragments so there is never a seek-back.
        psi.ArgumentList.Add("-movflags");
        psi.ArgumentList.Add("+frag_keyframe+empty_moov+default_base_moof+delay_moov");
        // Fragments are a quarter of a second long. The receiver cannot play anything until it holds a
        // whole fragment, so this adds a short delay at the start of every surround track. Halving the
        // length would add about 0.3% in moof headers, which is small next to 640 kbit/s.
        psi.ArgumentList.Add("-frag_duration"); psi.ArgumentList.Add("250000");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("mp4");

        // No -flush_packets here, unlike the stereo encoders. A fragmented MP4 is written in fragments, and
        // a flush per packet would write the stream to the socket in small pieces. That means more system
        // calls and more partial writes, and the receiver would assemble fragments from small pieces just
        // when it decides whether it has enough to start. Quarter-second fragments are already finer than
        // the buffer the receiver wants, so flushing gains no latency and only costs steadiness.
        psi.ArgumentList.Add("-");

        try
        {
            var proc = Process.Start(psi);
            if (proc is null) return null;
            _ = proc.StandardError.ReadToEndAsync();   // drain, or a full pipe stalls the encoder
            return proc;
        }
        catch { return null; }
    }

    private static async Task Swallow(Task task)
    {
        try { await task; } catch { /* the device is gone; StatusChanged will say so */ }
    }

    public void Dispose()
    {
        _closed = true;
        _volume.Close();
        Stop();
        _receiver.StatusChanged -= OnReceiverStatus;
        _receiver.NextRequested -= OnRemoteNext;
        _receiver.PreviousRequested -= OnRemotePrevious;
        _receiver.FavoriteRequested -= OnRemoteFavorite;
        _receiver.ArtworkRefused -= OnArtworkRefused;
        _receiver.Starved -= OnStarved;
        _server.Dispose();
        _streamGate.Dispose();

        // The connection to the speaker belongs to this sink, and it must be closed. A Cast client that is
        // left open keeps sending heartbeat pings to a speaker that nobody is talking to. The ping that
        // finally meets a closed socket is thrown where no handler can catch it (see SharpcasterReceiver).
        // The close waits for the STOP above, so the speaker is told before it is dropped.
        if (_receiver is IDisposable connection)
        {
            var stopping = _stopping;
            _ = Task.Run(async () =>
            {
                await stopping;
                try { connection.Dispose(); } catch { }
            });
        }
    }
}
