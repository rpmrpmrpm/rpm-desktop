namespace RPM.Core.Ripping;

/// <summary>
/// Talks to the optical drive: lists the drives, reads the TOC of the disc in one, and reads a track's
/// audio as raw CDDA PCM.
///
/// ffmpeg never touches the disc. Its CD demuxer (libcdio) is a GPL-only component. It is not in
/// Homebrew's ffmpeg or in the common LGPL Windows builds. Using it would fail at run time, and it would
/// bring GPL code into a commercial app. We read the sectors ourselves and give ffmpeg only PCM to encode.
/// This keeps one code path per OS, and ffmpeg stays in LGPL use.
/// </summary>
public interface IDiscReader
{
    /// <summary>Optical drives attached to the machine, with or without a disc in them.</summary>
    Task<IReadOnlyList<DiscDrive>> ListDrivesAsync(CancellationToken ct = default);

    /// <summary>Reads the audio CD in the drive. Null when the drive is empty, or when the disc has no
    /// audio tracks.</summary>
    Task<DiscInfo?> ReadDiscAsync(DiscDrive drive, CancellationToken ct = default);

    /// <summary>
    /// Whether an audio CD is in a drive right now. The answer comes from what the OS has already
    /// mounted: no drive handle, no spin-up and no TOC read.
    ///
    /// The app uses this to notice a disc without paying for a read. It is polled every few seconds for
    /// the whole life of the app, and <see cref="ReadDiscAsync"/> at that rate would keep an idle drive awake.
    /// </summary>
    bool AudioDiscPresent();

    /// <summary>Streams one track's audio as 16-bit stereo 44.1 kHz PCM into <paramref name="destination"/>.
    /// <paramref name="progress"/> reports 0..1 through the track.
    ///
    /// Implementations must say what byte order they use, through <see cref="PcmIsBigEndian"/>. macOS
    /// gives CDDA as big-endian AIFF. Windows' raw reads come back little-endian.</summary>
    /// <param name="log">Where blocks the drive would not read are reported, and the policy that says how
    /// hard to try for them. A block that is written off is filled with silence, so the track keeps its
    /// length. A track past its budget throws <see cref="DiscTrackUnreadableException"/>. Null means no
    /// tolerance at all: the first block that will not read fails the track.</param>
    Task ReadTrackPcmAsync(DiscInfo disc, DiscTrack track, Stream destination,
        IProgress<double>? progress = null, CancellationToken ct = default, RipReadLog? log = null);

    /// <summary>Byte order of the PCM <see cref="ReadTrackPcmAsync"/> writes.</summary>
    bool PcmIsBigEndian { get; }

    /// <summary>Ejects the disc. This is best effort. A failure here never fails a rip.</summary>
    Task EjectAsync(DiscDrive drive, CancellationToken ct = default);
}

/// <summary>Thrown when the disc is present but cannot be read. Examples are a scratched sector that will
/// not resolve, or a drive that refuses the raw read.</summary>
public class DiscReadException : Exception
{
    public DiscReadException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// One track the drive cannot deliver: too many blocks were refused, or the drive stopped responding.
///
/// It has its own type because it can be survived, and <see cref="DiscReadException"/> cannot. The rippers
/// skip the track this names and carry on with the rest of the disc. A plain read failure stops the whole
/// rip. A scratch is usually local, so the other tracks are usually fine.
/// </summary>
public sealed class DiscTrackUnreadableException(string message, bool stalled = false)
    : DiscReadException(message)
{
    /// <summary>True when the drive went quiet instead of returning errors. A whole block failed to come
    /// back, so the read was abandoned partway. The abandoned read still owns the handle, so the handle
    /// must not be read from again.</summary>
    public bool Stalled { get; } = stalled;
}
