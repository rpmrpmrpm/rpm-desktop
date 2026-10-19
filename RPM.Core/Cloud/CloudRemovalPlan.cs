using RPM.Core.Metadata;

namespace RPM.Core.Cloud;

/// <summary>What taking a track out of the cloud comes down to, for one row.</summary>
public enum CloudRemoval
{
    /// <summary>The account holds nothing for this track: a local file that was never offered to the
    /// cloud, or one whose upload already failed for good.</summary>
    Nothing,

    /// <summary>Left in the cloud, and only this caller's claim on it is dropped, because something
    /// else the user has up there (a synced playlist) still plays it.</summary>
    UnpinOnly,

    /// <summary>No object to delete, only a row to clean up: the track was chosen or queued and nothing
    /// has gone up, or it can carry no id and so was never stored under one.</summary>
    ForgetLocally,

    /// <summary>Delete the cloud copy. The file is on this disk either way.</summary>
    DeleteInCloud,

    /// <summary>Fetch the file home first, then delete the cloud copy, because the cloud's copy is the
    /// only one.</summary>
    BringHomeThenDelete,
}

/// <summary>Why a track is being taken out, which decides two things the state alone cannot.</summary>
public enum CloudRemovalIntent
{
    /// <summary>"Take this out of the cloud", from an album's or a song's own menu. The local library
    /// keeps the music, so an offloaded track has to come home before its cloud copy goes, and a track
    /// that a synced playlist still plays is spared.</summary>
    TakeOutOfCloud,

    /// <summary>"Delete this album", from this computer and from the account. Nothing is fetched home,
    /// because downloading an album in order to delete it is not what was asked. A playlist does not
    /// spare a track, because the local rows are going too, and a spared cloud copy would come back as
    /// a cloud album on the next catalogue pass.</summary>
    DeleteEverywhere,
}

/// <summary>
/// Which of the five things a removal does to one track.
///
/// <para>
/// The rule is shared by three callers in the sync service: a song's menu, an album's menu, and the
/// delete that removes both copies. The differences between them are real (see
/// <see cref="CloudRemovalIntent"/>) but small. The parts that must not differ are the dangerous
/// ones: a cloud-only track is never deleted without being fetched home first, and a track with no
/// id has a row to clean up and no object to delete.
/// </para>
/// </summary>
public static class CloudRemovalPlan
{
    /// <summary>The states in which the account holds something for the track: a copy, or a place in
    /// the queue.</summary>
    public static bool IsInTheCloudsHands(CloudState state) =>
        state is CloudState.Queued or CloudState.Uploading or CloudState.Synced or CloudState.CloudOnly;

    /// <param name="heldElsewhere">Whether something else of the user's (a synced playlist) still
    /// plays this track. It only spares the track under <see cref="CloudRemovalIntent.TakeOutOfCloud"/>.</param>
    public static CloudRemoval For(
        CloudRemovalIntent intent, CloudState state, string? rpmId, bool heldElsewhere = false)
    {
        if (intent == CloudRemovalIntent.DeleteEverywhere)
        {
            // A row that the account holds nothing for is not this action's business: the caller deletes
            // the album's local files, and there is no cloud copy to go with them.
            if (!IsInTheCloudsHands(state)) return CloudRemoval.Nothing;

            // Chosen or in flight: the object does not exist yet, so there is a place in the queue to
            // clear and nothing to ask the store for.
            return !RpmTag.IsId(rpmId) || state is CloudState.Queued or CloudState.Uploading
                ? CloudRemoval.ForgetLocally
                : CloudRemoval.DeleteInCloud;
        }

        // Checked before the id and before the state: a track that another synced playlist plays stays
        // up whatever else is true of it, because the user asked for this album to come out and not for
        // the playlist to stop working.
        if (heldElsewhere) return CloudRemoval.UnpinOnly;

        if (!RpmTag.IsId(rpmId)) return CloudRemoval.ForgetLocally;

        return state switch
        {
            CloudState.Synced => CloudRemoval.DeleteInCloud,

            // This is the case that destroys music if it is wrong: this machine gave up its copy, so the
            // store's copy is the only one left and it has to be on this disk before anything is deleted.
            CloudState.CloudOnly => CloudRemoval.BringHomeThenDelete,

            _ => CloudRemoval.ForgetLocally,
        };
    }
}
