using System.Diagnostics;

namespace RPM.Core.Radio;

/// <summary>
/// What a station is playing, read without keeping a listen going.
///
/// <para><b>Why this has to open the stream.</b> ICY metadata is not published anywhere. It is carried
/// INSIDE the audio, as a block of text every <c>icy-metaint</c> bytes. There is no directory field for it.
/// The station's own page rarely has one that a machine can read, and broadcasters do not agree on where to
/// put it. So the only way to learn what a station is playing is to connect to it. This connects, reads
/// until the first metadata block has gone past, takes the title and disconnects.</para>
///
/// <para><b>The cost is small.</b> One metaint of audio is read. Stations set it between 8 KB and 16 KB,
/// which is under a second of sound. <see cref="MostBytes"/> is the ceiling for stations that set a larger
/// value or send no metadata at all. <see cref="Patience"/> is the ceiling for stations that accept the
/// connection and then send nothing. Whichever limit comes first, the socket is closed.</para>
///
/// <para><b>It is a real connection to someone's server.</b> The station counts a listener for a second or
/// two. That is the honest cost of the question. It is why the title is asked for ONE station at a time,
/// the card the reader is looking at, and then answered from a cache for a while. It is not asked for every
/// station on a list. See <c>RadioStationItem.OnAirAsync</c>, which holds that rule.</para>
///
/// <para><b>It blocks, like <see cref="IcyConnection"/> underneath it.</b> The socket is opened with
/// <c>HttpClient.Send</c> and read synchronously, so every caller must run on a worker thread. The token is
/// checked between reads. It cannot interrupt a read that is in progress. A read that never returns is
/// bounded by the connect timeout inside <see cref="IcyConnection"/> and by <see cref="Patience"/>
/// here.</para>
/// </summary>
public static class IcyPeek
{
    /// <summary>How much audio is read before giving up. It is large enough for a station that sets a large
    /// <c>icy-metaint</c>, and small enough that a station which sends no metadata costs only a couple of
    /// seconds of one stream.</summary>
    public const int MostBytes = 192 * 1024;

    /// <summary>How long the whole peek may take. This is for a station that accepts a connection and then
    /// sends data very slowly, which no byte count would catch.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(8);

    /// <summary>How much is asked of the socket at a time. One comfortable read, rather than many small
    /// ones. The bytes are thrown away, so the size only changes how often the loop checks the
    /// clock.</summary>
    private const int Chunk = 8 * 1024;

    /// <summary>
    /// The title the station is announcing, or null.
    /// </summary>
    /// <remarks>
    /// Null is a normal answer, and a common one. An HLS station carries no ICY metadata. Many stations
    /// send none. A station that is down answers nothing. None of these is treated as an error here. The
    /// card is drawn without a line.
    ///
    /// <para>The result is the announcement after <see cref="IcyFields"/> has read it. This is the same
    /// string that <see cref="IRadioTuner.NowPlayingTitle"/> gives while the station is tuned. So whatever
    /// judges one, such as <see cref="RadioAds.IsAdBreak"/> or <see cref="IcyTitle.Split"/>, judges the
    /// other the same way. Never return the raw ICY string here, or the card would print the station's raw
    /// fields.</para>
    /// </remarks>
    public static string? Title(RadioStation station, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(station);

        // An HLS stream has no ICY metadata. Its URL is a playlist of segments, and OpenBytes returns null
        // for it. This check is here too, so the common case makes no connection at all.
        if (station.IsHls)
            return null;

        string? announced = null;
        Stream? stream = null;

        try
        {
            stream = IcyConnection.OpenBytes(station, heard => announced = heard.Spoken ?? "");
            if (stream is null)
                return null;

            var clock = Stopwatch.StartNew();
            var buffer = new byte[Chunk];
            var read = 0;

            // The title arrives through the callback as the bytes go by. So this loop reads only enough to
            // move the stream along, until the first metadata block has been passed.
            while (announced is null
                   && read < MostBytes
                   && clock.Elapsed < Patience
                   && !ct.IsCancellationRequested)
            {
                var took = stream.Read(buffer, 0, buffer.Length);
                if (took <= 0)
                    break;   // the station hung up

                read += took;
            }

            return string.IsNullOrWhiteSpace(announced) ? null : announced.Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;   // refused, dropped, or not a stream. The card is drawn without a line
        }
        finally
        {
            // Always. This is the main rule of a peek, as opposed to a listen: the socket is closed as soon
            // as the question is answered.
            stream?.Dispose();
        }
    }
}
