using RPM.Core.Library;

namespace RPM.Core.Cloud;

/// <summary>Whose shelf a set of cloud marks is drawn on. This decides what the marks mean.
///
/// <para>On the user's own shelf, a cloud mark says how much of this disk the record uses. On a
/// borrowed shelf that answer is always none, so the mark says how the record reaches this computer
/// instead. Two things decide that: whether the store has the record, and whether the computer lending
/// it is switched on. See <see cref="TrackCloudBadge.ForBorrowed"/> and
/// <see cref="Library.AlbumCloudRollup.BadgeOnBorrowedShelf"/>.</para>
/// </summary>
public enum ShelfReading
{
    /// <summary>The user's own collection.</summary>
    Own,

    /// <summary>Somebody else's collection, and their computer is answering.</summary>
    BorrowedFromOnline,

    /// <summary>Somebody else's collection, and their computer is not answering. Only what the store
    /// holds is available.</summary>
    BorrowedFromOffline,
}

/// <summary>The cloud mark that one TRACK line shows, and whether the lines of a record should show
/// marks at all.
///
/// <para>Track lines use the same marks as an album tile, <see cref="AlbumCloudBadge"/>, because the
/// marks mean the same thing. A listener who knows the cloud in the corner of an album tile should not
/// have to learn a second set of marks inside the record. One mark is read differently.
/// <see cref="AlbumCloudBadge.Partial"/> means "some of it is up there" on an album. On one track, which
/// cannot be partly anything, it means "chosen, but not up there yet".</para>
/// </summary>
public static class TrackCloudBadge
{
    /// <param name="selected">The user's own choice. It is true before any bytes have been
    /// uploaded.</param>
    /// <param name="uploadsStopped">Whether the engine has stopped sending anything, for example because
    /// the store is full. A queued track is only on its way while the queue is moving. While the engine
    /// is stopped, the track is chosen and waiting. The mark is then the outline cloud, which does not
    /// pulse. See <see cref="AlbumCloudRollup.BadgeWhen"/>, which says the same thing for the whole
    /// record.</param>
    /// <param name="attempts">How many times the upload has failed. Past
    /// <see cref="CloudRetrySchedule.MaxAttempts"/> the queue has stopped trying with this track, so the
    /// mark must not promise a retry that will not happen.</param>
    /// <param name="partialError">Set when the track is in the cloud but without everything it should
    /// have. It does not outrank the other states: a track that is up there is drawn as up there. It
    /// does tint the mark.</param>
    public static AlbumCloudBadge For(
        CloudState state,
        bool selected,
        bool uploadsStopped = false,
        int attempts = 0,
        string? partialError = null) => state switch
    {
        // Chosen, tried, and given up on. Red, not the outline cloud. The outline cloud means the queue
        // will reach it, and for this track the queue has already tried.
        CloudState.Error when !CloudRetrySchedule.MayTryAgain(attempts) => AlbumCloudBadge.Failed,

        // Up there, but missing something: the head clip that would not encode. This is checked before
        // Synced, because the track is synced too. A plain white cloud would hide the missing part.
        CloudState.Synced when partialError is { Length: > 0 } => AlbumCloudBadge.Incomplete,

        // Stopped: nothing is moving, so the mark shows what the track is now, not what it was going to be.
        CloudState.Queued or CloudState.Uploading when uploadsStopped => AlbumCloudBadge.Partial,

        // On its way right now. This is the one that pulses.
        CloudState.Queued or CloudState.Uploading => AlbumCloudBadge.Working,

        // Up there and nowhere else. This track uses none of this disk.
        CloudState.CloudOnly => AlbumCloudBadge.Offloaded,

        CloudState.Synced => AlbumCloudBadge.Full,

        // Chosen but not up there yet. Either the queue has not reached it, or the last attempt failed
        // and the retry backoff will try again. Both mean the track should be in the cloud and is not
        // yet, which is what the outline cloud shows.
        _ => selected ? AlbumCloudBadge.Partial : AlbumCloudBadge.None,
    };

    /// <summary>One line of a BORROWED record. The mark says how the song reaches this computer, not
    /// whose disk it uses. This is the track version of <see
    /// cref="AlbumCloudRollup.BadgeOnBorrowedShelf"/>, and it is read for the same reason.
    ///
    /// <para>A mirrored peer row has one of two states: cloud-backed, or on that computer's disk and
    /// nowhere else (<c>LibraryDatabase.MirrorPeerLibrary</c>). So there is nothing here about queues or
    /// choices. Neither can happen to somebody else's song from this computer.</para>
    /// </summary>
    /// <param name="ownerReachable">Whether the computer lending this shelf answered.</param>
    public static AlbumCloudBadge ForBorrowed(CloudState state, bool ownerReachable) => state switch
    {
        CloudState.CloudOnly => ownerReachable ? AlbumCloudBadge.Full : AlbumCloudBadge.Offloaded,
        _ => AlbumCloudBadge.PeerLink,
    };

    /// <summary>
    /// Whether a record's track lines should show their marks at all.
    ///
    /// <para>Only when the record disagrees with itself. An album that is entirely in the cloud already
    /// shows that with one cloud on its tile. Repeating it on all sixteen lines would be sixteen marks
    /// saying one thing. That noise would make it harder to spot a record that is half up, which is the
    /// case worth finding. So the lines show marks only when they have something the tile cannot
    /// say.</para>
    /// </summary>
    public static bool WorthShowing(IReadOnlyList<AlbumCloudBadge> badges)
    {
        for (int i = 1; i < badges.Count; i++)
            if (badges[i] != badges[0])
                return true;
        return false;
    }
}
