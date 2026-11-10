namespace RPM.Core.Migration;

/// <summary>Where an import puts the files it brings.</summary>
public enum MigrationDestination
{
    /// <summary>Into the library folder on this computer (and to the cloud if cloud upload is on).</summary>
    ThisComputer = 0,

    /// <summary>Only into the cloud: each file is downloaded, uploaded, and its copy on this computer is
    /// deleted once the cloud has confirmed it. Media servers only.</summary>
    CloudOnly = 1,
}

/// <summary>Where one source track is in an import.</summary>
public enum MigrationTrackState
{
    /// <summary>Listed by the source and not handled yet.</summary>
    Found = 0,

    /// <summary>Downloaded for a cloud-only import, and waiting for the cloud to confirm the upload.</summary>
    Downloaded = 1,

    /// <summary>In rpm now.</summary>
    Imported = 2,

    /// <summary>rpm already had the same audio; the details were merged.</summary>
    AlreadyInRpm = 3,

    /// <summary>Not imported, with a reason. Tried again later while attempts remain, unless the reason
    /// is final (for example the file is not there).</summary>
    NotImported = 4,
}

/// <summary>One source track's row in the record. Times are UTC ticks.</summary>
/// <param name="Reason">A <c>Migration_*</c> string key, for rows that are not imported, or that are
/// already in rpm as a lower quality copy.</param>
/// <param name="Detail">The source path, where the reason needs one to be acted on.</param>
/// <param name="TrackId">The library row the track became or was matched to.</param>
public sealed record MigrationTrackRow(
    string SourceId, MigrationTrackState State, int Attempts, long NextTryUtcTicks, string? Reason, string? Detail, long? TrackId);

/// <summary>How far an import is. <paramref name="Found"/> is every track the source listed, and the
/// other four add up to it: <paramref name="NotImported"/> counts only the tracks given up on, and
/// <paramref name="Waiting"/> everything still to be done (including tracks waiting to be tried
/// again).</summary>
public sealed record MigrationCounts(int Found, int Imported, int AlreadyInRpm, int NotImported, int Waiting);

/// <summary>An import that has not finished, so the app can offer to continue it.</summary>
public sealed record MigrationSummary(SourceKind Kind, string LibraryId, string Name, MigrationDestination Destination);

/// <summary>The <c>Migration_*</c> string keys for why a track was not imported.</summary>
public static class MigrationReason
{
    /// <summary>The source lists the file, but it is not on the disk. Final: not tried again.</summary>
    public const string FileNotFound = "Migration_ReasonFileNotFound";

    /// <summary>The file could not be read or downloaded. Tried again later.</summary>
    public const string ReadFailed = "Migration_ReasonReadFailed";

    /// <summary>The disk ran out of space while copying.</summary>
    public const string DiskFull = "Migration_ReasonDiskFull";

    /// <summary>The cloud storage of the plan is full.</summary>
    public const string CloudFull = "Migration_ReasonCloudFull";

    /// <summary>The file could not be put into the library (for example another file is already at its
    /// path). The detail holds the reason the file system gave.</summary>
    public const string CouldNotCopy = "Migration_ReasonCouldNotCopy";

    /// <summary>The track was removed from rpm while the import was waiting for the cloud.</summary>
    public const string Removed = "Migration_ReasonRemoved";

    /// <summary>On an already-in-rpm row: rpm's copy is lossy and the source's is lossless.</summary>
    public const string LowerQualityInRpm = "Migration_ReasonLowerQualityInRpm";
}
