using System.Net;

namespace RPM.Core.Audio;

/// <summary>A connected Cast device, as much of one as <see cref="CastSink"/> needs: point it at a URL,
/// tell it to play or pause, ask it where it has got to.
///
/// <para>It is an interface so the sink can be tested without a speaker, and so the Cast library stays
/// behind one door (<see cref="CastService"/>) instead of leaking into the audio pipeline.</para>
/// </summary>
public interface ICastReceiver
{
    /// <summary>The device's address on the network. The media server uses it to work out which of this
    /// machine's addresses the device can reach.</summary>
    IPAddress Address { get; }

    string Name { get; }

    /// <summary>True while we are still connected to it.</summary>
    bool IsConnected { get; }

    /// <summary>Where the receiver says it has got to in the stream, in seconds, or null when it has not
    /// said (yet, or at all).
    ///
    /// <para>This is the only accurate clock when casting. What we have <i>sent</i> is not what is heard:
    /// an encoder, a socket and several seconds of the receiver's own buffer sit in between. The receiver
    /// pushes a status about once a second. Implementations interpolate between those with a stopwatch, so
    /// the position and the visualiser move smoothly instead of jumping.</para>
    /// </summary>
    double? PositionSeconds { get; }

    /// <summary>True once the receiver has finished playing the stream out.</summary>
    bool IsIdle { get; }

    /// <summary>What the receiver says it is doing. This is not always what we told it to do.
    ///
    /// <para>A Cast device can also be controlled from its touch surface, the Google Home app and by voice
    /// commands. All of these arrive here as a pushed MEDIA_STATUS, and this is the only way to learn about
    /// them.</para>
    /// </summary>
    CastPlayback Playback { get; }

    /// <summary>Why the receiver went idle. This tells a track that ended apart from a listener who stopped
    /// it. Only meaningful while <see cref="Playback"/> is <see cref="CastPlayback.Idle"/>.</summary>
    CastIdle Idle { get; }

    /// <summary>Gives the receiver a URL and tells it to fetch the media.</summary>
    Task LoadAsync(CastMedia media, CancellationToken cancellation = default);

    Task PlayAsync(CancellationToken cancellation = default);

    Task PauseAsync(CancellationToken cancellation = default);

    /// <summary>Stops the media. The receiver app stays open, so the next track does not wait for it to
    /// start again.</summary>
    Task StopMediaAsync(CancellationToken cancellation = default);

    /// <summary>Sets the speaker's own volume, 0..1. This is the same control as the speaker's buttons and
    /// the Google Home app, and every other casting app uses it.
    ///
    /// <para>It is not the stream volume. The stream volume is applied underneath the device's own volume:
    /// at 1.0 the speaker plays at its current setting, and every lower value is quieter. So the player's
    /// slider could only ever turn the music down. Turning it up has to happen on the device.</para>
    /// </summary>
    Task SetVolumeAsync(double level, CancellationToken cancellation = default);

    /// <summary>The speaker's own volume, 0..1, or null before it has said. Read when a cast starts, so the
    /// player takes the volume of the room instead of imposing its own.</summary>
    double? DeviceVolume { get; }

    /// <summary>Raised when the receiver reports a new status, and when it disconnects.</summary>
    event Action? StatusChanged;

    // ── what only RPM's own receiver can do ────────────────────────────────────────────────────
    //
    // The three members below are silent on Google's stock receiver, which has no way to report a skip, no
    // way to be retitled mid-stream, and nothing to say about what it can decode. They are on the interface
    // rather than behind a cast because the sink's job does not change: it forwards what the speaker says,
    // and a speaker that says nothing is a speaker that says nothing.

    /// <summary>Next was pressed on the speaker, or said to it. The <i>player</i> skips. The stream carries
    /// on, which keeps the changeover gapless.</summary>
    event Action? NextRequested;

    /// <summary>Previous was pressed on the speaker, or said to it.</summary>
    event Action? PreviousRequested;

    /// <summary>What this device can decode, once it has said. Null when the receiver cannot be asked. That
    /// is a different answer from "it can decode nothing".</summary>
    CastCapabilities? Capabilities { get; }

    /// <summary>The receiver would not fetch the cover from the URL we gave it. The cover is served from
    /// this machine over plain HTTP, and the receiver page is HTTPS, which a browser may refuse. The
    /// receiver asks for the picture itself instead.</summary>
    event Action? ArtworkRefused;

    /// <summary>The receiver ran out of audio after it had started playing, so it is buffering again. For
    /// the listener this is a gap in the music.
    ///
    /// <para>Only RPM's own receiver can report this. A stock receiver sends the same BUFFERING status
    /// while it fills up before it starts, and the two cannot be told apart here. This is the one reliable
    /// measure of whether the lead we keep is deep enough for the room's network. See
    /// <see cref="CastSink"/>.</para>
    /// </summary>
    event Action? Starved;

    /// <summary>Sends the cover as data, not as a URL, shrunk on the way. Answers
    /// <see cref="ArtworkRefused"/>.
    ///
    /// <para><b>Null is an answer and must be sent.</b> It means "this track has no cover". The screen
    /// needs to hear it, or the previous record's cover stays up over a different song. The receiver does
    /// not clear the cover on its own, because it cannot tell a refused cover from a missing one, and those
    /// need opposite handling.</para>
    /// </summary>
    Task SendArtworkAsync(byte[]? artwork, CancellationToken cancellation = default);

    /// <summary>Changes what is on screen without touching what is playing. See
    /// <see cref="IAudioSink.TrackChanged"/> for why these must be separate.</summary>
    Task UpdateNowPlayingAsync(CastNowPlaying track, CancellationToken cancellation = default);

    /// <summary>The heart was pressed on the television. This is a request to flip the mark, not a change
    /// that has been made. The app owns the flag. It writes the flag through the same path its own heart
    /// button uses, then reports the result with <see cref="SetFavoriteAsync"/>.
    ///
    /// <para>Only RPM's own receiver can send this. A stock receiver has no heart to press.</para>
    /// </summary>
    event Action? FavoriteRequested;

    /// <summary>Whether the playing record is one the listener has loved, so the television can draw the
    /// mark filled or empty.
    ///
    /// <para>Null is a different answer from false. Null means the collection does not hold this record at
    /// all. For example, a file played from outside the collection has no row to keep the mark on. The
    /// television then hides the heart, rather than show a key that cannot be pressed.</para>
    ///
    /// <para>Sent again whenever a receiver introduces itself, for the same reason as
    /// <see cref="SetScreenAsync"/>.</para>
    /// </summary>
    Task SetFavoriteAsync(bool? loved, CancellationToken cancellation = default);

    /// <summary>Turns the television's picture off or on, without touching what is playing.
    ///
    /// <para>A record can be forty minutes of one still picture. A panel that shows one still picture for
    /// that long can keep a faint image of it. This is for a television nobody is watching. The music
    /// carries on exactly as before.</para>
    ///
    /// <para>The receiver is a web page and does not keep this state itself. When the page reloads (the app
    /// restarts, or the receiver is relaunched), it shows whatever it was last told to show. So an
    /// implementation sends the state again whenever a receiver introduces itself, not only when the
    /// listener asks for it.</para>
    /// </summary>
    Task SetScreenAsync(bool on, CancellationToken cancellation = default);
}

/// <summary>What the device at the other end can decode, in its own words. RPM's own receiver sends this
/// when it connects.
///
/// <para>This matters more than it seems. Without it, surround is a setting the listener has to test: send
/// AC-3 and find out from the room whether the speaker passes Dolby through, folds it to stereo, or refuses
/// it. A receiver of ours answers before anything is encoded.</para>
/// </summary>
/// <param name="Ac3">What the <i>Cast platform</i> says: this box is capable of Dolby Digital.</param>
/// <param name="Ac3Playable">What the <i>media element</i> says: it will accept an AC-3 stream <b>right
/// now</b>.
///
/// <para><b>These are two different questions, and the gap between them is where surround goes wrong.</b> A
/// box's capability is a fact about the box, and it does not change. Whether its player accepts AC-3
/// depends on where the sound goes, because the decode is only enabled when something downstream can pass
/// Dolby on. An amplifier that is switched off, or an output that has switched to the television's own
/// speakers, makes this false while <see cref="Ac3"/> stays true. On the same hardware, the value has
/// flipped between two runs. The listener experiences it as surround stopping working.</para>
///
/// <para>Gate on this one. The stream is loaded progressively into that element, so its answer decides
/// whether anything comes out.</para>
/// </param>
/// <param name="Flac">What the <i>Cast platform</i> says: this box can decode FLAC in Matroska.</param>
/// <param name="FlacPlayable">What the <i>media element</i> says: it will hold FLAC in a live Matroska
/// stream.
///
/// <para>These are the same two questions as <see cref="Ac3"/> and <see cref="Ac3Playable"/>. Lossless
/// checked only the first, so a Shield, a box that plays Matroska all day, was told it could not play
/// lossless. The platform answers <c>canDisplayType</c> from a list of MIME strings it publishes, and
/// <c>audio/x-matroska</c> is not on that list for any device. The player the stream is handed to is the
/// part that has the demuxer.</para>
///
/// <para>Gate on this one. The value is never null. A receiver too old to send the player's answer gets the
/// platform's answer here.</para>
/// </param>
/// <param name="Display">Whether the device has a screen. <b>Null is a different answer from false.</b>
/// The device did not say, and "not told" is not the same as "has no screen", so the picture settings stay
/// available. False means a speaker, with no screen to turn off.
/// </param>
public sealed record CastCapabilities(
    int Protocol,
    bool Ac3,
    bool Eac3,
    bool Flac,
    bool HiResAudio,
    bool? Display,
    bool Ac3Playable,
    bool FlacPlayable);

/// <summary>What the receiver is doing, as it reports it.</summary>
public enum CastPlayback
{
    /// <summary>It has not said yet.</summary>
    Unknown = 0,

    /// <summary>It has been given media and is fetching it.</summary>
    Loading,

    /// <summary>It has the media and is filling its buffer.</summary>
    Buffering,

    Playing,

    Paused,

    /// <summary>Nothing is loaded. See <see cref="ICastReceiver.Idle"/> for why.</summary>
    Idle,
}

/// <summary>Why a receiver is idle. The stock receiver reports this on every stop, and the reason matters.
/// The end of our stream means the track finished and the playlist moves on. A stop on the speaker means
/// the listener wants silence. Treating the second as the first would skip to the next song when the
/// listener asked for silence.
/// </summary>
public enum CastIdle
{
    /// <summary>Not idle, or idle before it was ever given anything to play.</summary>
    None = 0,

    /// <summary>The media played out to its end. <c>FINISHED</c>.</summary>
    Finished,

    /// <summary>Stopped from somewhere else: the speaker's own controls, the Google Home app, or a voice
    /// command. <c>CANCELLED</c>.</summary>
    Stopped,

    /// <summary>Another sender took the speaker, or its own media replaced ours.
    /// <c>INTERRUPTED</c>.</summary>
    Interrupted,

    /// <summary>The receiver gave up on the media. <c>ERROR</c>.</summary>
    Error,
}

/// <summary>What the screen should show, and when it should start showing it.
///
/// <para><b>The clock anchor is the important part.</b> A track changes inside a stream that does not stop.
/// The change is decoded about ten seconds before the speaker plays it. So a screen that changes its title
/// when the decoder moves on would announce the next song over the end of the current one.
/// <see cref="StreamAtSeconds"/> gives the point in the stream where the change happens. The receiver knows
/// where it is in that stream, so it holds the new title until it reaches that point.</para>
///
/// <para>The anchor also gives the true position. The receiver's clock counts the <i>stream</i>, which runs
/// across every track in it. So the elapsed time is <see cref="TrackAtSeconds"/> plus how far the stream
/// has come since the anchor. Without the anchor, the receiver could only show a number that keeps climbing
/// past the end of each song.</para>
/// </summary>
/// <param name="TrackAtSeconds">Where in the track the anchor falls: zero for a track that starts at its
/// beginning, or the seek offset for one that does not.</param>
/// <param name="StreamAtSeconds">Where in the stream the anchor falls: zero for the track that began the
/// stream, or the changeover point for a track chained onto it.</param>
public readonly record struct CastNowPlaying(
    string? Title,
    string? Artist,
    string? Album,
    string? ArtworkUrl,
    double? DurationSeconds,
    double TrackAtSeconds,
    double StreamAtSeconds);

/// <summary>What to tell the receiver to fetch, and what to put on the television while it plays.</summary>
/// <param name="Url">Where the audio lives: a URL on <see cref="CastServer"/>.</param>
/// <param name="ContentType">MIME type of the stream, e.g. <c>audio/mpeg</c>.</param>
/// <param name="DurationSeconds">The <i>track's</i> total length. We always know it from the tags, so the
/// receiver can show a real running time instead of an open-ended live stream. Null only when it is truly
/// unknown.
///
/// <para>This is the length of the whole track, not of the bytes we are about to send. After a seek the
/// stream carries only the rest of the track, but the duration still describes the whole track. That looks
/// wrong and is not. The receiver's clock counts within the stream, and we add the seek offset back before
/// anyone sees the position. VLC does the same.</para>
/// </param>
/// <param name="Title">Track title.</param>
/// <param name="Artist">May be null.</param>
/// <param name="Album">May be null.</param>
/// <param name="ArtworkUrl">Cover art, also served by <see cref="CastServer"/>. May be null.</param>
public readonly record struct CastMedia(
    string Url,
    string ContentType,
    double? DurationSeconds,
    string Title,
    string? Artist,
    string? Album,
    string? ArtworkUrl);

/// <summary>How the post-EQ audio is encoded for the trip across the network. Stored in the settings file
/// by name.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<CastAudioFormat>))]
public enum CastAudioFormat
{
    /// <summary>MP3 at 320 kbps, in a bare MP3 stream. This adds one lossy encoding on top of the source.
    /// It is the only live audio format Google documents for the Default Media Receiver, so it is the safe
    /// default. VLC supports it fully.</summary>
    Mp3 = 0,

    /// <summary>FLAC in a live Matroska stream (<c>audio/x-matroska</c>). The post-EQ signal reaches the
    /// speaker unchanged, at 24-bit.
    ///
    /// <para>Matroska makes this possible. It holds a stream of unknown length, which a bare FLAC file
    /// cannot. VLC uses the same container (<c>mux=matroska,options={live=1}</c>), but VLC only puts lossy
    /// codecs in it. Its codec list does not cover Cast devices that can decode FLAC. Google lists FLAC as
    /// supported up to 96 kHz / 24-bit, but publishes no MIME string for it. Test this format on your own
    /// speaker before you rely on it.</para>
    /// </summary>
    Flac = 1,

    /// <summary>AC-3 (Dolby Digital) 5.1 in a live MPEG-TS stream (<c>video/mp2t</c>). Use it for a
    /// multichannel source sent to a device that can pass Dolby through to an AV receiver, for example a
    /// Shield feeding a surround amplifier.
    ///
    /// <para>This format is not the live post-EQ signal. The EQ pipeline is stereo, so surround skips it.
    /// The original file is encoded straight to AC-3 5.1. That keeps all six channels intact to the
    /// speaker.</para>
    ///
    /// <para>Google does not document whether the Default Media Receiver passes this through as 5.1 or
    /// downmixes it to stereo for a progressive stream. Test it on the real hardware and check that the
    /// amplifier shows "Dolby Digital".</para>
    /// </summary>
    Ac3Surround = 2,
}
