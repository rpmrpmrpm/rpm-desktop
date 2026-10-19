namespace RPM.Core.Cloud;

/// <summary>
/// Where a track's bytes live. The library is local-first: a row is <see cref="Local"/> until
/// the user opts it into the cloud, and rpm never takes the local file away. A track becomes
/// <see cref="CloudOnly"/> because the user deleted the file themselves, or because another of
/// their machines uploaded it and this one has never had it.
///
/// <para>
/// <see cref="CloudOnly"/> is the one state that breaks the mirror's basic assumption that a row has
/// a file. Its file is gone by the user's own doing, so <see cref="Library.LibrarySynchronizer"/>
/// must not read it as vanished and drop the row: the recording is still in the store, and the row
/// is the only thing that knows about it.
/// </para>
/// </summary>
public enum CloudState
{
    /// <summary>On disk, not in the cloud. This is the default, and where every import starts.</summary>
    Local = 0,

    /// <summary>Chosen for the cloud, not uploaded yet.</summary>
    Queued = 1,

    /// <summary>Upload in progress.</summary>
    Uploading = 2,

    /// <summary>On disk AND in the cloud, verified.</summary>
    Synced = 3,

    /// <summary>In the cloud only: there is no local file. It plays by streaming, and can be
    /// downloaded back from the track's or the album's own menu.</summary>
    CloudOnly = 4,

    /// <summary>The last upload failed. Carries the reason. The queue retries with backoff.</summary>
    Error = 5,
}
