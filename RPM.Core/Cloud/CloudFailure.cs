using RPM.Core.Diagnostics;

namespace RPM.Core.Cloud;

/// <summary>What a cloud failure turns out to be, after the tool's message has been read.</summary>
/// <param name="MessageKey">Localisation key for the one sentence that is shown to the user. It is
/// resolved by the head, because <c>RPM.Core</c> has no string table of its own.</param>
/// <param name="Permanent">Whether trying the same bytes again could ever give a different answer.
/// False for a dropped connection, true for a file that the encoder cannot read.</param>
public readonly record struct CloudFailureKind(string MessageKey, bool Permanent);

/// <summary>
/// Reads a failed cloud step and says what to tell the user and whether to try again.
///
/// <para>The two questions (what to tell the user, and whether to retry) need separate answers. The
/// exception message is ffmpeg's stderr, which can be hundreds of lines about channel elements and
/// is no use to someone who asked for their music to be backed up. The retry schedule must also
/// treat a file that cannot be encoded differently from a network that was briefly down. Retrying
/// the first kind for ever (sixteen minutes, five times, on every launch) never helps.</para>
///
/// <para>The match is on ffmpeg's own words and not on the exception type, because the exception is
/// always <c>InvalidOperationException</c> and only the sentence inside it says what happened.
/// Anything unrecognised is <see cref="Unknown"/> and NOT permanent. A wrong guess in that direction
/// costs one retry. A wrong guess in the other direction marks a track as failed for the life of
/// the install because of a bug in this method.</para>
/// </summary>
public static class CloudFailure
{
    /// <summary>More channels than a stereo downmix can be derived for, such as a surround or Atmos
    /// download. ffmpeg will not guess a matrix for thirty channels with no layout, so the head clip
    /// cannot be made from this file at all.</summary>
    public const string Channels = "Cloud_FailChannels";

    /// <summary>The audio would not decode, for example a truncated download or a damaged rip.</summary>
    public const string Unreadable = "Cloud_FailUnreadable";

    /// <summary>ffmpeg refused the file for some other reason of its own.</summary>
    public const string Transcode = "Cloud_FailTranscode";

    /// <summary>Not ffmpeg, and not the fault of this file. This is the default.</summary>
    public const string Unknown = "Cloud_FailUnknown";

    /// <summary>The container cannot carry an rpm id, so the track has no name to be stored under.</summary>
    public const string NoId = "Cloud_FailNoId";

    /// <summary>
    /// What to say about <paramref name="said"/>, and whether it is worth asking again.
    /// </summary>
    public static CloudFailureKind Classify(string? said)
    {
        if (string.IsNullOrWhiteSpace(said))
            return new(Unknown, Permanent: false);

        // Order matters: the channel case also mentions rematrixing and filters, so it is the more
        // specific of the two and must be checked first.
        if (Mentions(said, "Rematrix is needed") ||
            Mentions(said, "not enough information to do it") ||
            Mentions(said, "Invalid channel layout") ||
            Mentions(said, "channel layout is not supported"))
            return new(Channels, Permanent: true);

        if (Mentions(said, "Invalid data found when processing input") ||
            Mentions(said, "Error while decoding") ||
            Mentions(said, "could not find codec parameters") ||
            Mentions(said, "Decoder not found") ||
            Mentions(said, "Unknown decoder"))
            return new(Unreadable, Permanent: true);

        // ffmpeg never reached the audio: the file was locked, missing or being moved, or the temp disk
        // was full. This is checked before the catch-all below, because every canonicalisation failure
        // says "ffmpeg" in its own wrapper, and the catch-all would mark a file that the import is still
        // writing as refused for good.
        if (Mentions(said, "Permission denied") ||
            Mentions(said, "No such file or directory") ||
            Mentions(said, "being used by another process") ||
            Mentions(said, "No space left on device") ||
            Mentions(said, "There is not enough space on the disk") ||
            Mentions(said, "I/O error"))
            return new(Unknown, Permanent: false);

        // Everything else that ffmpeg is known to say. Permanent: it ran, read the file and refused it,
        // so a second run over the same bytes gets the same refusal.
        if (Mentions(said, "ffmpeg") ||
            Mentions(said, "Error reinitializing filters") ||
            Mentions(said, "Error while filtering") ||
            Mentions(said, "Conversion failed"))
            return new(Transcode, Permanent: true);

        // A timeout, a refused connection, or a signed URL that expired. Nothing to do with the file.
        return new(Unknown, Permanent: false);
    }

    private static bool Mentions(string said, string phrase) =>
        said.Contains(phrase, StringComparison.OrdinalIgnoreCase);

    /// <summary>The maximum length of <see cref="InFull(Exception)"/> that is kept.</summary>
    public const int MaxFullError = Faults.MaxFullError;

    /// <summary>Everything known about a cloud failure, for the activity log and for a report. Every
    /// failure gets the same text: see <see cref="Faults.InFull(Exception)"/>.</summary>
    public static string InFull(Exception ex) => Faults.InFull(ex);

    /// <param name="ex">The failure.</param>
    /// <param name="home">The folder written as <c>~</c>. Empty to leave paths alone.</param>
    public static string InFull(Exception ex, string home) => Faults.InFull(ex, home);
}
