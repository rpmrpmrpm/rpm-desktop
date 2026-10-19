using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RPM.Core.Audio;
using RPM.Core.Diagnostics;
using Sharpcaster;
using Sharpcaster.Models;
using Sharpcaster.Models.ChromecastStatus;
using Sharpcaster.Models.Media;

// Uno's implicit usings bring in Microsoft.UI.Xaml.Controls, which also has an Image.
using CastImage = Sharpcaster.Models.Media.Image;
using RPM.Core.Threading;

namespace RPM.Desktop.Services.Cast;

/// <summary>
/// Connects to a Cast device and talks to it. This covers everything about casting except finding the
/// device in the first place.
///
/// <para>
/// This file is compiled into both heads. The desktop app includes it in its own project. The phone app
/// includes it from here (see RPM.Mobile.csproj). The CASTV2 conversation is the same on both, and a second
/// copy would mean a second set of bugs. Discovery is the part that really differs. The desktop browses
/// mDNS through Zeroconf. On iOS, Zeroconf needs an entitlement that Apple grants per app, so the phone
/// browses with Bonjour instead. See RPM.Mobile/Services/CastDiscovery.cs.
/// </para>
///
/// <para>What the two apps <em>send</em> is different too. The desktop gives the speaker a URL on its own
/// machine, because the audio being cast is its output after the equaliser, and that audio exists nowhere
/// else (RPM.Core/Audio/CastServer.cs). The phone gives the speaker the cloud URL it would otherwise have
/// played itself. So the speaker fetches from S3 directly, and the phone is not in the path at all.</para>
/// </summary>
public static class CastConnection
{
    /// <summary>Google's stock receiver app. It plays a URL and shows the metadata, and that is all it can
    /// do. It is the fallback, and it is still enough to play music. See <see cref="ConnectAsync"/> for
    /// when it is used.</summary>
    public const string DefaultMediaReceiver = "CC1AD845";

    /// <summary>
    /// RPM's own receiver, hosted at <c>https://rpmrpmrpm.com/cast/receiver.html</c>. The value is empty
    /// until the app id is registered in the Cast Developer Console. It can be set from configuration, so
    /// registering the app is a settings change and not a release.
    ///
    /// <para>The receiver adds three things. Next and previous pressed on the speaker reach the app,
    /// instead of arriving as a stop. The screen keeps up with a gapless playlist. The device says what it
    /// can decode before we encode surround for it.</para>
    /// </summary>
    public static string? ReceiverAppId { get; set; }

    /// <summary>
    /// Connects and launches a receiver. Throws if the device will not accept us at all.
    ///
    /// <para>RPM's own receiver is tried first, and the stock one is the fallback. An unpublished Cast
    /// application only launches on devices registered for it. So during development, every other speaker
    /// in the house refuses the launch the same way a stereo-paired speaker does. The right response is to
    /// play the music through the stock receiver, not to tell the user that the speaker is broken. The user
    /// only loses the speaker's own next button.</para>
    /// </summary>
    public static async Task<SharpcasterReceiver> ConnectAsync(CastDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var control = new CastControlChannel();
        var client = new ChromecastClient(logger: null, control);
        await client.ConnectChromecast(device.Receiver);

        var ours = ReceiverAppId;
        string? refusal = null;

        if (!string.IsNullOrWhiteSpace(ours))
        {
            var (launched, why) = await TryLaunchAsync(client, ours!);
            if (launched)
            {
                Said.Line("cast", $"launched RPM's receiver ({ours})");
                return new SharpcasterReceiver(client, device, control);
            }
            refusal = why;
        }

        if ((await TryLaunchAsync(client, DefaultMediaReceiver)).Launched)
        {
            // Worth saying out loud. The fallback is deliberately invisible to the listener, because the
            // music plays either way. That also makes it invisible to us. "It behaves exactly as it did
            // before" is what a receiver that would not launch looks like.
            Said.Line("cast", string.IsNullOrWhiteSpace(ours)
                ? "stock receiver (no app id configured)"
                : $"fell back to the stock receiver: {refusal}");
            return new SharpcasterReceiver(client, device, control: null);
        }

        try { await client.DisconnectAsync(); } catch { }
        throw new CastRefusedException(device);
    }

    /// <summary>Launches an app. On a refusal, returns what the device said about it. That is how a speaker
    /// in a stereo pair is told apart from an app it has never heard of.</summary>
    private static async Task<(bool Launched, string? Refusal)> TryLaunchAsync(ChromecastClient client, string appId)
    {
        try
        {
            await client.LaunchApplicationAsync(appId, joinExistingApplicationSession: true);
            return (true, null);
        }
        catch (Exception ex) when (IsLaunchRefused(ex))
        {
            return (false, ex.Message);
        }
    }

    /// <summary>The device answered LAUNCH with <c>NOT_ALLOWED</c>. Sharpcaster reports every launch
    /// failure the same way, so the message is all there is to check.</summary>
    private static bool IsLaunchRefused(Exception ex)
        => ex is InvalidOperationException && ex.Message.Contains("could not be started", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The speaker refused to start the receiver app at all. It answers LAUNCH with <c>NOT_ALLOWED</c> for any
/// app, so nothing we send to it would ever be played.
///
/// <para>In practice this means the speaker is in a stereo pair or a speaker group. Google does not let
/// anyone cast to a member of a pair directly, including Google's own apps. The group is the thing you cast
/// to, and the members stop being destinations. The members still advertise themselves on the network as
/// before. That is why they still show up in the list, and then refuse.</para>
/// </summary>
public sealed class CastRefusedException : Exception
{
    public CastRefusedException(CastDevice device)
        : base($"{device.Name} refused to launch any receiver app. It is almost certainly stereo-paired "
             + "or in a speaker group, and Google only allows casting to the group.")
    {
        Device = device;
    }

    public CastDevice Device { get; }
}

/// <summary>A speaker on the network, as offered to the user.</summary>
/// <param name="Id">
/// What identifies the device. <b>Not the address.</b> A speaker group runs on one of its member speakers,
/// at that member's IP address but on a different port. So a whole household can share one address. If the
/// address were the key, the group and its host would be merged into one entry, and the rest would be
/// thrown away. That hides the group, which is the thing the user needs. A speaker that is grouped or
/// stereo-paired cannot be cast to on its own.
/// </param>
/// <param name="IsGroup">True for a speaker group or stereo pair rather than a single speaker.</param>
public sealed partial record CastDevice(
    string Id,
    string Name,
    string Model,
    IPAddress Address,
    bool IsGroup,
    ChromecastReceiver Receiver)
{
    public static CastDevice? From(ChromecastReceiver receiver)
    {
        var host = receiver.DeviceUri?.Host;
        if (host is null || !IPAddress.TryParse(host, out var address)) return null;

        var model = receiver.Model ?? "";
        var name = string.IsNullOrWhiteSpace(receiver.Name) ? host : receiver.Name;

        receiver.ExtraInformation.TryGetValue("id", out var advertised);
        var id = CastDeviceIdentity.For(advertised, address, receiver.Port);

        var isGroup = model.Contains("Group", StringComparison.OrdinalIgnoreCase);

        return new CastDevice(id, name, model, address, isGroup, receiver);
    }

    /// <summary>
    /// Whether this device plainly has no picture. This is read from what the device advertises about
    /// itself, before anything has connected to it.
    ///
    /// <para>The receiver's own <c>display_supported</c> value is the better answer, and it is used
    /// wherever there is one. Often there is none. A custom web receiver does not launch on an audio-only
    /// Cast device at all, so the speakers this question is about are the ones that can never answer it.
    /// The model name is a fact that holds whenever it is asked, and it is known from the moment the device
    /// appears in a scan.</para>
    ///
    /// <para>A "no" here is a real no. An unrecognised model returns false. The caller reads false as "it
    /// might have a screen". That is the safe direction, because it leaves a setting available instead of
    /// taking it away.</para>
    /// </summary>
    public bool HasNoScreen
    {
        get
        {
            // A hub is a speaker with a screen on the front. Its model name says so before it says any of
            // the words below, which is why it is checked first.
            if (Has("Hub")) return false;

            // A group is cast to as one thing, and it is a set of speakers. Google has never made a group
            // of televisions.
            return IsGroup
                || Has("Audio")      // Chromecast Audio
                || Has("Home")       // Google Home, Home Mini, Home Max
                || Has("Nest Mini")
                || Has("Nest Audio")
                || Has("Speaker");

            bool Has(string word) => Model.Contains(word, StringComparison.OrdinalIgnoreCase);
        }
    }
}

/// <summary>
/// <see cref="ICastReceiver"/> over Sharpcaster.
///
/// <para><b>A dead connection must be closed, not only noticed.</b> Sharpcaster keeps the control channel
/// alive with a heartbeat. Its timer handler is an <c>async void</c> that writes to the socket, with no
/// try/catch of its own (Sharpcaster/Channels/HeartbeatChannel.cs, TimerElapsed). When a speaker has gone
/// (unplugged, rebooted, or taken over by another sender), that write throws <c>IOException: Broken
/// pipe</c> on a thread pool thread. Nothing in this process can catch it there. An <c>async void</c>
/// exception is not an unobserved <i>task</i> exception, so the handler in App.xaml.cs never sees it, and
/// the process stops. The receive loop also swallows the read error without raising <c>Disconnected</c>. So
/// a socket that died is never noticed by the library, and the heartbeat keeps pinging it every ten
/// seconds.</para>
///
/// <para>This class therefore treats the first transport-level failure as final. <see cref="Drop"/> stops
/// the polling, tells the listeners, and calls <c>DisconnectAsync</c>. That stops the heartbeat timer and
/// clears the stream that the ping would have been written to. Everything we send goes through <see
/// cref="Send"/>, so there is one place where that decision is made.</para>
/// </summary>
public sealed partial class SharpcasterReceiver : ICastReceiver, IDisposable
{
    /// <summary>
    /// How often we ask the receiver where it has got to.
    ///
    /// <para>Asking is required. A Cast receiver sends a MEDIA_STATUS when its <i>state</i> changes, such
    /// as loading, playing or paused. While it plays, it sends nothing. Nobody sends the position, so we
    /// must keep asking. VLC polls every four seconds (chromecast_ctrl.cpp, getPlaybackTimestamp). We poll
    /// faster, because the seek bar and the visualiser both depend on this number.</para>
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>How often we ask while the receiver is not playing. We still ask, because a paused cast can
    /// quietly lose its socket. The next heartbeat on such a socket would take the process down. Asking is
    /// how we find out in time to close it.</summary>
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(4);

    /// <summary>How many replies may go missing before we call it. One is a slow speaker on a busy network;
    /// two in a row, thirty seconds apart each, is a speaker that has stopped answering.</summary>
    private const int MaxSilentPolls = 2;

    /// <summary>How far we extrapolate past the last status before we admit we do not know the position.
    /// With polling this should not normally be reached. It guards against a receiver that has gone quiet.
    /// A position that carries on regardless would be worse than one that stalls.</summary>
    private static readonly TimeSpan MaxExtrapolation = TimeSpan.FromSeconds(5);

    private readonly ChromecastClient _client;
    private readonly CastDevice _device;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _polling = new();

    private readonly Stopwatch _sinceStatus = new();
    private double _statusPosition;
    private double _statusDuration;
    private PlayerStateType _state = PlayerStateType.Idle;
    private string? _idleReason;
    private double? _deviceVolume;
    private bool _haveStatus;

    /// <summary>
    /// The stream we last asked the receiver to play, and the media session it gave that stream.
    ///
    /// <para><b>Statuses can outlive the media they describe.</b> Changing track while casting sends a LOAD
    /// while the speaker is still playing the previous stream. For a moment its reports are about the old
    /// stream. First it reports that it is playing. Then it reports IDLE with <c>CANCELLED</c>, which
    /// happens when our teardown pulls the body out from under it. That last report looks the same as
    /// someone pressing stop in the room. Acting on it would stop the music that the listener has just
    /// asked to skip. Timing cannot tell them apart. The media session id can.</para>
    /// </summary>
    private string? _expectedContent;
    private long? _session;
    private readonly Stopwatch _sinceLoad = new();
    private bool _sawPlayback;
    private volatile bool _connected = true;

    /// <summary>0 until the connection is given up on, then 1. It is set with an interlocked exchange,
    /// because the poll, a transport failure and <see cref="Dispose"/> can all reach it at the same time.
    /// Closing the client twice would raise the disconnect event twice.</summary>
    private int _dropped;

    /// <summary>The private line to RPM's own receiver, or null when we are on the stock one and there is
    /// nobody at the other end to talk to.</summary>
    private readonly CastControlChannel? _control;

    public SharpcasterReceiver(ChromecastClient client, CastDevice device, CastControlChannel? control = null)
    {
        _client = client;
        _device = device;
        _control = control;

        if (_control is not null)
        {
            _control.NextRequested += () => NextRequested?.Invoke();
            _control.PreviousRequested += () => PreviousRequested?.Invoke();
            _control.ArtworkRefused += () => ArtworkRefused?.Invoke();
            _control.FavoriteRequested += () => FavoriteRequested?.Invoke();
            _control.Starved += () => Starved?.Invoke();
            _control.CapabilitiesReceived += caps =>
            {
                // A hello means a receiver that has just started. A receiver that has just started shows
                // whatever its markup says, not what it was last told. The screen state is sent again here,
                // and not only when it changes, because this is the one moment we know the page at the
                // other end is new. The values come from the current state, not from an earlier event, so
                // they are right whenever they are sent.
                _ = SetScreenAsync(_screenOn);
                _ = SetFavoriteAsync(_favorite);
                CapabilitiesKnown?.Invoke(caps);
            };
            _control.ReceiverErrored += (code, detail) => MediaRejected?.Invoke(code, detail);
        }

        _client.ReceiverChannel.ReceiverStatusChanged += OnReceiverStatus;
        _client.MediaChannel.StatusChanged += OnStatus;
        _client.MediaChannel.LoadFailed += OnLoadFailed;
        _client.MediaChannel.ErrorHappened += OnError;
        _client.Disconnected += OnDisconnected;

        _ = PollAsync(_polling.Token);
    }

    /// <summary>Raised when the receiver rejects the media outright. Without it, a refused LOAD is silent,
    /// and the player would seem to stream to a speaker that is ignoring it.</summary>
    public event Action<CastTrouble>? Error;

    /// <summary>Next at the speaker. Only RPM's own receiver can send this. On the stock receiver the same
    /// press arrives as a stop, and is handled as a stop.</summary>
    public event Action? NextRequested;

    /// <summary>Previous at the speaker.</summary>
    public event Action? PreviousRequested;

    public CastCapabilities? Capabilities => _control?.Capabilities;

    /// <summary>The speaker has said what it can decode. This arrives a moment after connecting. See <see
    /// cref="CastDeviceProfile"/>.</summary>
    public event Action<CastCapabilities>? CapabilitiesKnown;

    /// <summary>
    /// The receiver would not play what it was sent. The code is CAF's own reason.
    ///
    /// <para>Code 104 (<c>MEDIA_SRC_NOT_SUPPORTED</c>) is the one to act on. The player will not hold that
    /// container, whatever the box can decode. Only RPM's own receiver reports it. The stock receiver fails
    /// the load and does not say why.</para>
    /// </summary>
    public event Action<int, string>? MediaRejected;

    /// <summary>The receiver asked for the cover as data. Only RPM's own receiver can do this. The stock
    /// receiver has no way to tell us that it could not load the cover.</summary>
    public event Action? ArtworkRefused;

    /// <summary>The heart button was pressed on the television. Only RPM's own receiver has this
    /// button.</summary>
    public event Action? FavoriteRequested;

    /// <summary>The state of the favourite mark for the record playing, so a receiver that has just started
    /// can be told again. Null until the player has said anything. Null is also the answer for a record
    /// that the collection does not hold.</summary>
    private volatile object? _favoriteBox;

    private bool? _favorite => _favoriteBox as bool?;

    public async Task SetFavoriteAsync(bool? loved, CancellationToken cancellation = default)
    {
        _favoriteBox = loved;
        if (_control is null || _dropped != 0) return;
        await Send(() => _control.SendFavoriteAsync(loved), "show whether this song is loved");
    }

    /// <summary>The receiver ran out of audio in the middle of a track. Only RPM's own receiver can say so.
    /// A stock receiver reports BUFFERING in the same way it does while filling up before it starts. From
    /// here, the two look the same.</summary>
    public event Action? Starved;

    public async Task SendArtworkAsync(byte[]? artwork, CancellationToken cancellation = default)
    {
        // No length check. "There is no cover" is the answer for a track that has none. It is also the
        // message that clears the last cover from the screen. See ICastReceiver.SendArtworkAsync.
        if (_control is null || _dropped != 0) return;
        await Send(() => _control.SendArtworkAsync(artwork), "show the cover");
    }

    /// <summary>Changes the title on the screen during a stream. Nothing is done on the stock receiver,
    /// because it cannot be told anything. So the television keeps showing the track the stream started
    /// with, until the next real LOAD.</summary>
    public async Task UpdateNowPlayingAsync(CastNowPlaying track, CancellationToken cancellation = default)
    {
        if (_control is null || _dropped != 0) return;
        await Send(() => _control.SendMetadataAsync(track), "show what is playing");
    }

    /// <summary>Whether the picture is on. It is kept here so that a receiver that has just started can be
    /// told again. See <see cref="ICastReceiver.SetScreenAsync"/>. The state lives here, not in the
    /// page.</summary>
    private volatile bool _screenOn = true;

    /// <summary>Does nothing on the stock receiver, which cannot be told anything. Its title card is
    /// whatever Google draws, and the listener uses the television's own remote there.</summary>
    public async Task SetScreenAsync(bool on, CancellationToken cancellation = default)
    {
        _screenOn = on;
        if (_control is null || _dropped != 0) return;
        await Send(() => _control.SendScreenAsync(on), on ? "turn the screen on" : "turn the screen off");
    }

    /// <summary>
    /// Asks the receiver for its position, because the receiver does not send it. Asking also keeps a check
    /// on the connection. This is the only thing that uses the socket on a schedule of our own. So it is
    /// where a speaker that has gone is found out, and that must happen before Sharpcaster's heartbeat
    /// writes to the same dead socket. See the class remarks.
    /// </summary>
    private async Task PollAsync(CancellationToken cancellation)
    {
        int silent = 0;

        while (!cancellation.IsCancellationRequested && _connected)
        {
            bool playing;
            lock (_gate) playing = _state is PlayerStateType.Playing or PlayerStateType.Buffering;

            try
            {
                if (!await Cancellable.DelayAsync(playing ? PollInterval : IdlePollInterval, cancellation))
                    return;

                // Nothing has been loaded yet. There is no media session to ask about, and a GET_STATUS
                // that the receiver has no answer for would only time out.
                bool loaded;
                lock (_gate) loaded = _haveStatus || _state is PlayerStateType.Loading;
                if (!loaded) continue;

                // The reply arrives as a MEDIA_STATUS, so OnStatus does the actual updating.
                await _client.MediaChannel.GetMediaStatusAsync();
                silent = 0;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (IsConnectionLost(ex))
            {
                Drop();
                return;
            }
            catch (TimeoutException)
            {
                // The socket is still open, but the speaker is not answering on it. Give it one more round,
                // because a busy network loses a reply now and then. After that, stop trusting the
                // connection.
                if (++silent >= MaxSilentPolls)
                {
                    Drop();
                    return;
                }
            }
            catch { /* a single odd reply is not worth reporting; the next poll will say */ }
        }
    }

    /// <summary>
    /// Sends something to the receiver, and treats a transport failure as the end of the connection rather
    /// than as one failed command. Returns false when the message did not get through.
    /// </summary>
    /// <param name="what">What was being attempted. It reads after "Could not" in the message the user
    /// sees, for example "start playing", "show the cover" or "turn the screen off".</param>
    private async Task<bool> Send(Func<Task> call, string what)
    {
        if (_dropped != 0) return false;

        try
        {
            await call();
            return true;
        }
        catch (Exception ex) when (IsConnectionLost(ex))
        {
            Drop();
            return false;
        }
        catch (Exception ex) when (IsNothingToDo(ex))
        {
            // Not a failure. There is no media session, so the thing asked about is already in the state
            // that was wanted. Stopping a receiver that holds nothing is the normal way a cast ends.
            // Showing that to the user as an error would be noise, because it happens on every teardown.
            return true;
        }
        catch (Exception ex)
        {
            // The connection is fine, but the receiver would not do what was asked. Examples are a refused
            // LOAD or an unsupported codec. This is worth logging, but not worth closing the connection
            // for.
            Faults.Note("cast", what, ex);
            Error?.Invoke(new CastTrouble.CommandFailed(Name));
            return false;
        }
    }

    /// <summary>There was nothing loaded to act on. The receiver says so by refusing the request for want
    /// of a media session, and it means the command had already got what it wanted.</summary>
    private static bool IsNothingToDo(Exception ex)
        => ex.Message.Contains("MediaSessionID is not available", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("INVALID_MEDIA_SESSION", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this is the socket having gone rather than the receiver having said no.
    ///
    /// <para><see cref="TaskCanceledException"/> counts: Sharpcaster completes every request still waiting
    /// for a reply with one when the connection goes, so a command in flight at the moment the speaker
    /// disappears arrives here as a cancellation and means precisely "it is gone".</para>
    /// </summary>
    private static bool IsConnectionLost(Exception ex) => ex switch
    {
        IOException or SocketException or ObjectDisposedException or TaskCanceledException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsConnectionLost),
        { InnerException: { } inner } => IsConnectionLost(inner),
        _ => false,
    };

    /// <summary>
    /// Gives up on the connection, once and for all. This stops the polling, closes the client and reports
    /// that the connection has gone. Closing is the important part. It stops Sharpcaster's heartbeat timer
    /// from pinging a socket that is no longer there. See the class remarks.
    ///
    /// <para>It reports through <see cref="StatusChanged"/> and not through <see cref="Error"/>. A speaker
    /// that has gone is one event, and the user should be told about it once. The sink that listens for it
    /// moves the music back to this machine, and it is the one that speaks.</para>
    /// </summary>
    private void Drop()
    {
        if (Interlocked.Exchange(ref _dropped, 1) != 0) return;

        _connected = false;
        try { _polling.Cancel(); } catch { }

        // Fire and forget, but never unobserved: DisconnectAsync writes a CLOSE to a socket that is quite
        // possibly the reason we are here in the first place.
        _ = CloseAsync();

        StatusChanged?.Invoke();
    }

    private async Task CloseAsync()
    {
        try { await _client.DisconnectAsync(); } catch { /* it was already gone; that was the point */ }
    }

    public IPAddress Address => _device.Address;

    public string Name => _device.Name;

    public bool IsConnected => _connected;

    public event Action? StatusChanged;

    /// <summary>
    /// The receiver reports its position about once a second. That would make the seek bar and the
    /// visualiser move in jumps. So the last reported position is kept, and the time since then, measured
    /// on this machine, is added to it. This runs the receiver's clock forward between its updates.
    /// </summary>
    public double? PositionSeconds
    {
        get
        {
            lock (_gate)
            {
                if (!_haveStatus) return null;
                if (_state != PlayerStateType.Playing) return _statusPosition;

                var elapsed = _sinceStatus.Elapsed;
                if (elapsed > MaxExtrapolation) return _statusPosition + MaxExtrapolation.TotalSeconds;
                return _statusPosition + elapsed.TotalSeconds;
            }
        }
    }

    /// <summary>
    /// What the receiver says the media's length is, or null before it has said.
    ///
    /// <para>Only the phone asks. The desktop knows the length from the tags before it sends anything. What
    /// the desktop serves is a live stream whose length is genuinely unknown. The phone hands over a
    /// finished object on S3, and the receiver parses it. So for a track whose database row has no
    /// duration, this is the only clock there is.</para>
    /// </summary>
    public double? DurationSeconds
    {
        get { lock (_gate) return _statusDuration > 0 ? _statusDuration : null; }
    }

    /// <summary>True once the receiver has played our stream out. This is different from idle, which is
    /// also the state before we have given it anything.</summary>
    public bool IsIdle
    {
        get
        {
            lock (_gate)
            {
                if (!_connected) return true;
                if (!_sawPlayback) return false;
                return _state == PlayerStateType.Idle;
            }
        }
    }

    /// <summary>True while the receiver is actually playing, as opposed to paused, buffering or finished.
    /// The phone's transport reads this; the desktop's comes from its own pipeline.</summary>
    public bool IsPlaying
    {
        get { lock (_gate) return _connected && _state == PlayerStateType.Playing; }
    }

    /// <summary>What the speaker says it is doing. This includes activity that someone else started. See
    /// <see cref="ICastReceiver.Playback"/>.</summary>
    public CastPlayback Playback
    {
        get
        {
            lock (_gate)
            {
                if (!_connected || !_haveStatus) return CastPlayback.Unknown;
                return _state switch
                {
                    PlayerStateType.Playing => CastPlayback.Playing,
                    PlayerStateType.Paused => CastPlayback.Paused,
                    PlayerStateType.Buffering => CastPlayback.Buffering,
                    PlayerStateType.Loading => CastPlayback.Loading,
                    _ => CastPlayback.Idle,
                };
            }
        }
    }

    /// <summary>
    /// Why it is idle, from the <c>idleReason</c> the stock receiver sends with every stop.
    ///
    /// <para>Only after it has actually played something: a receiver is idle before it is given anything
    /// too, and that is not a stop.</para>
    /// </summary>
    public CastIdle Idle
    {
        get
        {
            lock (_gate)
            {
                if (!_sawPlayback || _state != PlayerStateType.Idle) return CastIdle.None;
                return _idleReason?.ToUpperInvariant() switch
                {
                    "FINISHED" => CastIdle.Finished,
                    "CANCELLED" => CastIdle.Stopped,
                    "INTERRUPTED" => CastIdle.Interrupted,
                    "ERROR" => CastIdle.Error,
                    // No reason given at all. Our stream ending is by far the likeliest way to get here,
                    // and calling that a stop would strand the playlist on a finished track.
                    _ => CastIdle.Finished,
                };
            }
        }
    }

    /// <summary>True while the receiver is fetching or buffering, rather than playing. This is the honest
    /// answer to "why is nothing coming out yet".</summary>
    public bool IsBuffering
    {
        get { lock (_gate) return _connected && _state is PlayerStateType.Buffering or PlayerStateType.Loading; }
    }

    public async Task LoadAsync(CastMedia media, CancellationToken cancellation = default)
    {
        lock (_gate)
        {
            // A new stream is a new clock. Without this the position would carry on from the previous track
            // for the second or so before the receiver's first status arrives.
            _haveStatus = false;
            _sawPlayback = false;
            _statusPosition = 0;
            _statusDuration = 0;
            _state = PlayerStateType.Loading;
            _idleReason = null;
            _sinceStatus.Restart();

            // From here, only statuses about THIS stream count. The previous one is still playing at the
            // speaker and is about to end, loudly and in a way that reads like a stop.
            _expectedContent = media.Url;
            _session = null;
            _sinceLoad.Restart();
        }

        var metadata = new MusicTrackMetadata
        {
            MetadataType = MetadataType.Music,
            Title = media.Title,
            SongName = media.Title,
            Artist = media.Artist,
            AlbumName = media.Album,
            Images = media.ArtworkUrl is null
                ? Array.Empty<CastImage>()
                : new[] { new CastImage { Url = media.ArtworkUrl } },
        };

        var payload = new Media
        {
            // contentId is the field the protocol has always used. contentUrl is the newer name. Set both,
            // because receivers in use read one or the other.
            ContentId = media.Url,
            ContentUrl = media.Url,
            ContentType = media.ContentType,
            // Buffered with a duration, even though the body we serve is an endless stream of unknown
            // length. This looks wrong, but it is right. The receiver needs a duration to show a running
            // time at all, and we always know the track's duration. VLC makes the same call for the same
            // reason. It declares BUFFERED whenever the input length is known, and uses LIVE only when it
            // is not. The receiver never seeks within the stream. We do the seeking ourselves, by
            // restarting the stream.
            StreamType = media.DurationSeconds is > 0 ? StreamType.Buffered : StreamType.Live,
            Duration = media.DurationSeconds,
            Metadata = metadata,
        };

        // What we actually put on the wire, so it can be read against what the receiver says it got.
        Said.Line("cast", $"load → {media.ContentType} {payload.StreamType} "
                      + $"{(media.DurationSeconds is { } d ? $"{d:F0}s" : "no duration")} {media.Url}");

        try
        {
            await _client.MediaChannel.LoadAsync(payload, autoPlay: true, activeTrackIds: null);
        }
        catch (Exception ex) when (IsConnectionLost(ex))
        {
            // Close the connection, but say nothing: the caller is the sink, and a LOAD that did not land
            // takes the whole cast down through its own failure path, with its own message.
            Drop();
            throw;
        }
    }

    public async Task PlayAsync(CancellationToken cancellation = default)
        => await Send(_client.MediaChannel.PlayAsync, "start playing");

    public async Task PauseAsync(CancellationToken cancellation = default)
        => await Send(_client.MediaChannel.PauseAsync, "pause");

    public async Task StopMediaAsync(CancellationToken cancellation = default)
        => await Send(_client.MediaChannel.StopAsync, "stop");

    /// <summary>
    /// Turns the speaker's own volume control. This uses <see cref="ReceiverChannel"/>, not the media
    /// channel.
    ///
    /// <para>The volume of the media channel is a stream attenuation. It is applied underneath the device's
    /// own volume. So sending 1.0 there means "as loud as the speaker happens to be set", and every other
    /// value is quieter. The player's slider could then turn the music down, but never up. Other casting
    /// apps all drive the device volume. That is also the volume the speaker's own buttons change, so the
    /// two do not fight each other.</para>
    /// </summary>
    public async Task SetVolumeAsync(double level, CancellationToken cancellation = default)
        => await Send(() => _client.ReceiverChannel.SetVolume(Math.Clamp(level, 0, 1)), "change its volume");

    public double? DeviceVolume
    {
        get { lock (_gate) return _deviceVolume; }
    }

    /// <summary>The speaker's volume changed, by this app, by its own buttons, or by the Home
    /// app.</summary>
    private void OnReceiverStatus(object? sender, ChromecastStatus? status)
    {
        if (status?.Volume?.Level is not double level) return;

        lock (_gate) _deviceVolume = level;
        StatusChanged?.Invoke();
    }

    /// <summary>
    /// Moves the receiver's own playhead.
    ///
    /// <para>This only works when the receiver is fetching a real object that can be seeked. That is the
    /// phone's case: an S3 URL that supports HTTP Range requests. The desktop seeks by restarting its live
    /// stream at the new offset, because the stream has nothing to seek within on the far end.</para>
    /// </summary>
    public async Task SeekAsync(double seconds, CancellationToken cancellation = default)
    {
        if (!await Send(() => _client.MediaChannel.SeekAsync(Math.Max(0, seconds)), "seek")) return;

        lock (_gate)
        {
            // Trust our own seek until the receiver confirms it. Otherwise the position jumps back to where
            // the track was for the second before the next status arrives. The seek bar would then jump
            // back just after the user moved it.
            _statusPosition = Math.Max(0, seconds);
            _haveStatus = true;
            _sinceStatus.Restart();
        }

        StatusChanged?.Invoke();
    }

    /// <summary>How long we wait for a status that names the stream we asked for, before we trust whatever
    /// the receiver sends. A receiver that never echoes the content id would otherwise leave the position
    /// frozen, and no command from the room would be acted on.</summary>
    private static readonly TimeSpan SessionGrace = TimeSpan.FromSeconds(5);

    /// <summary>Whether this status is about the stream we are playing, rather than the one we just
    /// replaced. See <see cref="_expectedContent"/>.</summary>
    private bool IsOurs(MediaStatus status)
    {
        lock (_gate)
        {
            if (_expectedContent is null) return true;      // nothing loaded by us yet

            // The status names its media. That is the only unambiguous answer. Take the session id from it,
            // so that the statuses that follow, which carry no media at all, can be placed.
            if (status.Media?.ContentId is { Length: > 0 } content)
            {
                var ours = string.Equals(content, _expectedContent, StringComparison.Ordinal);
                if (ours) _session = status.MediaSessionId;
                return ours;
            }

            if (_session is long session) return status.MediaSessionId == session;

            // It has not named its media yet and we have no session to compare against. Give the load a
            // moment to be acknowledged, then stop second-guessing the device.
            return !_sinceLoad.IsRunning || _sinceLoad.Elapsed > SessionGrace;
        }
    }

    private void OnStatus(object? sender, MediaStatus? status)
    {
        if (status is null) return;
        if (!IsOurs(status)) return;

        lock (_gate)
        {
            _statusPosition = status.CurrentTime;
            if (status.Media?.Duration is > 0 and var duration) _statusDuration = duration;
            _state = status.PlayerState;
            _idleReason = status.IdleReason;
            _haveStatus = true;
            if (_state is PlayerStateType.Playing or PlayerStateType.Buffering) _sawPlayback = true;
            _sinceStatus.Restart();
        }

        StatusChanged?.Invoke();
    }

    private void OnLoadFailed(object? sender, object? e)
    {
        // The payload is whatever the receiver sent with its refusal. It is usually thin, but it sometimes
        // holds the one detail that matters, and it is safe to print.
        Said.Line("cast", $"LOAD_FAILED: {e}");

        Error?.Invoke(new CastTrouble.StreamRefused(Name));
    }

    private void OnError(object? sender, object? e)
    {
        Said.Line("cast", $"receiver error: {e}");
        Error?.Invoke(new CastTrouble.ReceiverError(Name));
    }

    /// <summary>The library noticed the connection go. It does this only when its own heartbeat times out,
    /// never when the socket simply breaks. Either way the connection is over.</summary>
    private void OnDisconnected(object? sender, EventArgs e) => Drop();

    public void Dispose()
    {
        // Closing the connection is the point of disposing: the heartbeat timer is what keeps writing to
        // the socket after we have stopped caring about it. See the class remarks.
        Drop();

        try { _client.ReceiverChannel.ReceiverStatusChanged -= OnReceiverStatus; } catch { }
        try { _client.MediaChannel.StatusChanged -= OnStatus; } catch { }
        try { _client.MediaChannel.LoadFailed -= OnLoadFailed; } catch { }
        try { _client.MediaChannel.ErrorHappened -= OnError; } catch { }
        try { _client.Disconnected -= OnDisconnected; } catch { }
        try { _polling.Dispose(); } catch { }
    }
}
