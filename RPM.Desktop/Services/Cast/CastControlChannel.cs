using System.Text.Json;
using System.Text.Json.Serialization;
using SkiaSharp;
using RPM.Core.Audio;
using RPM.Desktop.Services.Art;
using Sharpcaster;
using Sharpcaster.Channels;
using Sharpcaster.Interfaces;

namespace RPM.Desktop.Services.Cast;

/// <summary>
/// The private channel between RPM and its own Cast receiver.
///
/// <para>The standard media channel already reports playing, paused, stopped and the current position. This
/// channel exists for two things that it cannot do. <b>Next and previous</b> pressed on the speaker, or
/// spoken to it, reach the standard receiver as a stop. That looks the same as the user asking for silence.
/// <b>A track change under a stream that is still running</b> cannot be sent as a LOAD without a gap in the
/// music. RPM's changeover is gapless because the receiver never learns that the track ended.</para>
///
/// <para>Both directions use JSON with a <c>type</c> field. The receiver is the page
/// <c>cast/receiver.html</c> in the rpm repo. This file and that page are the only two that must agree on
/// the message format.</para>
/// </summary>
public sealed partial class CastControlChannel : ChromecastChannel
{
    /// <summary>Our own namespace, shared with nobody else. The <c>urn:x-cast:</c> prefix is required by
    /// the platform. What follows it only has to be unique.</summary>
    public const string ControlNamespace = "urn:x-cast:com.rpmrpmrpm.control";

    /// <summary>The protocol version this end speaks. The receiver sends its own version back in its
    /// greeting. A new app talking to a receiver that has not been updated yet is then visible in the
    /// log.</summary>
    public const int Protocol = 1;

    /// <summary>The shape of a control message and how it is sent. The receiver expects camelCase names and
    /// null fields left out. The options are set on the context because a published build uses Native AOT,
    /// which reads its options from generated metadata.</summary>
    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(ControlMessage))]
    private sealed partial class CastControlJson : JsonSerializerContext;

    // useBaseNamespace: false, because the base prefix is for Google's own channels and this one is not.
    public CastControlChannel() : base(ControlNamespace, logger: null, useBaseNamespace: false)
    {
    }

    /// <summary>Next was asked for at the speaker. The player skips to the next track. The stream does not
    /// stop.</summary>
    public event Action? NextRequested;

    /// <summary>Previous was asked for, at the speaker.</summary>
    public event Action? PreviousRequested;

    /// <summary>The heart was pressed on the television. This is a request to switch the favourite mark.
    /// The player writes the change and reports what it wrote.</summary>
    public event Action? FavoriteRequested;

    /// <summary>
    /// The receiver could not load the cover from the URL we gave it, so it asks for the picture itself.
    ///
    /// <para>The app serves artwork from the machine it runs on, over plain HTTP on the local network. The
    /// receiver page is HTTPS, and a browser may refuse that as mixed content. The URL is still worth
    /// trying first. When the browser allows it, it costs nothing and it is the full-resolution
    /// image.</para>
    /// </summary>
    public event Action? ArtworkRefused;

    /// <summary>The receiver refused to play what it was given. The event carries its own message and CAF's
    /// error code. The code tells a codec the receiver cannot decode apart from a stream that never
    /// arrived.</summary>
    public event Action<int, string>? ReceiverErrored;

    /// <summary>The receiver ran out of audio after it had started playing. This is a gap in the music. It
    /// is also the one measurement of whether the lead the sink keeps is deep enough for this room. Raised
    /// once per gap, not once per second.</summary>
    public event Action? Starved;

    /// <summary>What the device said it can decode. Null until it has said something. A stock receiver
    /// stays null, because it has never heard of this channel.</summary>
    public CastCapabilities? Capabilities { get; private set; }

    /// <summary>Raised when <see cref="Capabilities"/> arrives.</summary>
    public event Action<CastCapabilities>? CapabilitiesReceived;

    /// <summary>
    /// Tells the receiver what is playing now, without changing what is playing.
    ///
    /// <para>This is why the screen can keep up with a gapless playlist. The media the receiver holds is
    /// one endless stream, and the tracks inside it change without the stream stopping. A LOAD would tell
    /// the receiver properly, but it would cost a couple of seconds of silence at every changeover.</para>
    /// </summary>
    public Task SendMetadataAsync(CastNowPlaying track)
    {
        // The message goes to the running receiver app, not to the device. The device itself, "receiver-0",
        // does not know about namespaces that belong to the app it hosts. A message sent there is dropped
        // without any error, which is hard to diagnose.
        var transportId = Client?.ChromecastStatus?.Applications?.FirstOrDefault()?.TransportId;
        if (string.IsNullOrEmpty(transportId)) return Task.CompletedTask;

        return SendAsync(JsonSerializer.Serialize(
            new ControlMessage
            {
                Type = "meta",
                Title = track.Title,
                Artist = track.Artist,
                Album = track.Album,
                Art = track.ArtworkUrl,
                Duration = track.DurationSeconds,
                TrackAt = track.TrackAtSeconds,
                StreamAt = track.StreamAtSeconds,
            }, CastControlJson.Default.ControlMessage), transportId!);
    }

    /// <summary>
    /// Sends the cover over the control channel, for a receiver that would not fetch it from a URL.
    ///
    /// <para>The image is shrunk first. A CASTV2 message is not a file transfer, so a 3 MB scan would never
    /// arrive. <see cref="ArtworkWidth"/> is still more than a television shows, and the result is tens of
    /// kilobytes.</para>
    ///
    /// <para>A null <paramref name="artwork"/>, or bytes that are not an image (a normal thing to find in a
    /// tag), is sent as an empty <c>art</c> rather than dropped. The empty value clears the cover from the
    /// screen.</para>
    /// </summary>
    public Task SendArtworkAsync(byte[]? artwork)
    {
        var transportId = Client?.ChromecastStatus?.Applications?.FirstOrDefault()?.TransportId;
        if (string.IsNullOrEmpty(transportId)) return Task.CompletedTask;

        var jpeg = artwork is { Length: > 0 } bytes ? Downscale(bytes) : null;

        return SendAsync(JsonSerializer.Serialize(
            new ControlMessage
            {
                Type = "art",
                Data = jpeg is null ? null : "data:image/jpeg;base64," + Convert.ToBase64String(jpeg),
            }, CastControlJson.Default.ControlMessage), transportId!);
    }

    /// <summary>
    /// Says whether the record now playing is one the listener has marked as a favourite.
    ///
    /// <para>Null is sent as a missing <c>on</c> field. It means the collection does not hold this record.
    /// The television then removes the heart from the row, instead of showing a mark that cannot be
    /// pressed. False and null are different answers, and that difference is why this value is
    /// nullable.</para>
    /// </summary>
    public Task SendFavoriteAsync(bool? loved)
    {
        var transportId = Client?.ChromecastStatus?.Applications?.FirstOrDefault()?.TransportId;
        if (string.IsNullOrEmpty(transportId)) return Task.CompletedTask;

        return SendAsync(JsonSerializer.Serialize(
            new ControlMessage { Type = "favorite", On = loved }, CastControlJson.Default.ControlMessage), transportId!);
    }

    /// <summary>
    /// Turns the television's picture off, or back on. The music is not changed. This is for a screen in a
    /// room nobody is sitting in. It is not a pause.
    ///
    /// <para>This is worth doing because a record is about forty minutes of one still picture. A panel that
    /// shows one still picture for that long can keep some of it on screen. The receiver fades everything
    /// out while the picture is off, so leaving it off costs nothing.</para>
    /// </summary>
    public Task SendScreenAsync(bool on)
    {
        var transportId = Client?.ChromecastStatus?.Applications?.FirstOrDefault()?.TransportId;
        if (string.IsNullOrEmpty(transportId)) return Task.CompletedTask;

        return SendAsync(JsonSerializer.Serialize(
            new ControlMessage { Type = "screen", On = on }, CastControlJson.Default.ControlMessage), transportId!);
    }

    /// <summary>How wide the cover is sent when it has to travel as a message rather than a URL.</summary>
    private const int ArtworkWidth = 640;

    /// <summary>Re-encodes a cover small enough to send. Null when the bytes are not an image we can read,
    /// which is a normal thing to find in a tag.</summary>
    private static byte[]? Downscale(byte[] artwork)
    {
        try
        {
            using var source = SKBitmap.Decode(artwork);
            if (source is null) return null;

            var scale = Math.Min(1.0, ArtworkWidth / (double)Math.Max(source.Width, source.Height));
            var width = Math.Max(1, (int)(source.Width * scale));
            var height = Math.Max(1, (int)(source.Height * scale));

            using var resized = source.Resize(
                new SKImageInfo(width, height), ArtScaler.SamplingFor(1 / scale));
            if (resized is null) return null;

            using var image = SKImage.FromBitmap(resized);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 82);
            return data?.ToArray();
        }
        catch { return null; }
    }

    public override void OnMessageReceived(string messagePayload, string type)
    {
        ControlMessage? message;
        try { message = JsonSerializer.Deserialize(messagePayload, CastControlJson.Default.ControlMessage); }
        catch (JsonException) { return; }   // Not a message this app understands. The receiver may be newer.

        switch (message?.Type)
        {
            case "next":
                RPM.Core.Diagnostics.Said.Line("cast", "next, from the speaker");
                NextRequested?.Invoke();
                break;

            case "prev":
                RPM.Core.Diagnostics.Said.Line("cast", "previous, from the speaker");
                PreviousRequested?.Invoke();
                break;

            case "error":
                // The receiver's own account of what it would not play. This is logged, not shown. The user
                // already sees "would not play the stream". The log is for whoever reads it later. CAF's
                // detailedErrorCode is the useful part: 104 is an unsupported source, 905 is a load that
                // failed outright.
                RPM.Core.Diagnostics.Said.Line("cast",
                    $"receiver error {message.Code}: {message.Reason} {message.Detail}".TrimEnd());
                ReceiverErrored?.Invoke(message.Code ?? 0, $"{message.Reason} {message.Detail}".Trim());
                break;

            case "playing":
                // The time between the LOAD and the first sound. The surround start depends on this number,
                // and this is the only place it can be measured.
                RPM.Core.Diagnostics.Said.Line("cast", message.AfterMs is double wait
                    ? $"receiver playing, {wait / 1000.0:F1}s after the load"
                    : "receiver playing");
                break;

            case "starved":
                // How long it had been playing when it ran out. This shows whether the lead is too shallow
                // for this room, or whether it was one bad moment on the network.
                RPM.Core.Diagnostics.Said.Line("cast",
                    $"receiver ran dry after {(message.AfterMs ?? 0) / 1000.0:F0}s of play");
                Starved?.Invoke();
                break;

            case "load":
                RPM.Core.Diagnostics.Said.Line("cast",
                    $"receiver load: contentType={message.ContentType} streamType={message.StreamType}");
                break;

            case "screenstate":
                // What the television is doing with its own panel, such as standby, or the HDMI input
                // switching to another source. This is logged and nothing else. An audio receiver does not
                // stop because its picture went away. The log shows that the television went to sleep,
                // instead of guessing it.
                RPM.Core.Diagnostics.Said.Line("cast",
                    $"receiver {message.What}: {message.State}");
                break;

            case "artfail":
                ArtworkRefused?.Invoke();
                break;

            case "favorite":
                // A press, not a claim. Our own messages going the other way carry an `on` field. The
                // receiver never sends that field back, so a message with a value in it is one of ours, and
                // is not a press.
                if (message.On is null)
                    FavoriteRequested?.Invoke();
                break;

            case "hello":
                var capabilities = new CastCapabilities(
                    Protocol: message.V ?? 0,
                    Ac3: message.Caps?.Ac3 ?? false,
                    Eac3: message.Caps?.Eac3 ?? false,
                    Flac: message.Caps?.Flac ?? false,
                    HiResAudio: message.Caps?.HiResAudio ?? false,
                    Display: message.Caps?.Display ?? false,

                    // The player's own answer. This is the one that decides whether anything comes out. See
                    // CastCapabilities.Ac3Playable. A receiver too old to send it is taken at the
                    // platform's word, which is how it behaved before there was a second answer to check.
                    Ac3Playable: WillPlay(message.Plays, "mp4-ac3") ?? message.Caps?.Ac3 ?? false,

                    // The same check for lossless, asked with both names of the container. We send
                    // audio/x-matroska, but a device that registered only video/x-matroska has the demuxer
                    // either way. A yes from either name is a yes.
                    FlacPlayable: WillPlayAny(message.Plays, "mka-flac", "mkv-flac")
                                  ?? message.Caps?.Flac ?? false);

                // The answer to "will this device play surround", from the device, before anything is
                // encoded for it. It is logged because the whole surround format decision depends on this
                // one fact, and there is no other way to see it.
                RPM.Core.Diagnostics.Said.Line("cast",
                    $"receiver v{capabilities.Protocol}: ac3={capabilities.Ac3} eac3={capabilities.Eac3} "
                    + $"flac={capabilities.Flac} hiRes={capabilities.HiResAudio} "
                    + $"display={capabilities.Display?.ToString() ?? "unsaid"} "
                    // The two answers the format switch actually uses. They can differ from the platform
                    // answer above.
                    + $"ac3-playable={capabilities.Ac3Playable} flac-playable={capabilities.FlacPlayable}");

                // And what its media element will hold. That is a different question from what the box can
                // decode. See playable() in the receiver.
                if (message.SetupFailures is { Count: > 0 } broken)
                {
                    RPM.Core.Diagnostics.Said.Line("cast",
                        "receiver setup failed: " + string.Join(" | ", broken));
                }

                if (message.Plays is { Count: > 0 } plays)
                {
                    RPM.Core.Diagnostics.Said.Line("cast",
                        "receiver plays: " + string.Join("  ", plays.Select(p => $"{p.Key}={p.Value}")));
                }

                Capabilities = capabilities;
                CapabilitiesReceived?.Invoke(capabilities);
                break;
        }
    }

    private sealed class ControlMessage
    {
        public string? Type { get; set; }
        public int? V { get; set; }
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public string? Art { get; set; }
        public string? Data { get; set; }
        public double? Duration { get; set; }
        public double? TrackAt { get; set; }
        public double? StreamAt { get; set; }
        public int? Code { get; set; }
        public string? Reason { get; set; }
        public string? Detail { get; set; }
        public Dictionary<string, string>? Plays { get; set; }
        public List<string>? SetupFailures { get; set; }
        public double? AfterMs { get; set; }
        public string? ContentType { get; set; }
        public string? StreamType { get; set; }
        public bool? On { get; set; }
        public string? What { get; set; }
        public string? State { get; set; }
        public CapabilityPayload? Caps { get; set; }
    }

    /// <summary>
    /// Reads one of the receiver's <c>plays</c> answers. Returns null when the receiver did not give one.
    ///
    /// <para>Each answer is the word from <c>canPlayType</c>, followed by a suffix for MSE, for example
    /// "probably+mse", "maybe-mse" or "no-mse". Only the first part counts here. The stream is loaded
    /// straight into the media element as a progressive body, so what Media Source Extensions would do with
    /// it never arises. "Probably" and "maybe" both mean yes. A browser says "maybe" about most things,
    /// because it will not commit until it has seen the bytes.</para>
    /// </summary>
    internal static bool? WillPlay(Dictionary<string, string>? plays, string what) =>
        plays is not null && plays.TryGetValue(what, out var answer)
            ? answer.StartsWith("probably", StringComparison.Ordinal)
              || answer.StartsWith("maybe", StringComparison.Ordinal)
            : null;

    /// <summary>Asks the same question several ways and combines the answers. One yes is enough. The result
    /// is "no" only after something has actually answered no. It is written as a loop rather than with
    /// <c>??</c>, because <c>??</c> would stop at the first answer and never reach the second probe. And
    /// the first probe answering "no" is exactly the case this exists for.</summary>
    internal static bool? WillPlayAny(Dictionary<string, string>? plays, params string[] what)
    {
        var asked = false;
        foreach (var one in what)
        {
            if (WillPlay(plays, one) is not { } answer) continue;
            if (answer) return true;
            asked = true;
        }

        return asked ? false : null;
    }

    private sealed class CapabilityPayload
    {
        public bool? Ac3 { get; set; }
        public bool? Eac3 { get; set; }
        public bool? Flac { get; set; }
        public bool? HiResAudio { get; set; }
        public bool? Display { get; set; }
    }
}
