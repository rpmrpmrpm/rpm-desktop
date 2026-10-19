using RPM.Core.Cloud;

namespace RPM.Core.Library;

/// <summary>Which copies a delete removes. There is no fourth value and no default. A delete that does not
/// say which copies it removes is a bug. A delete that trashed the local files, left the cloud copy and dropped
/// the row would make the record leave the wall and then come back on the next catalogue pass. So every
/// delete names its reach.
/// </summary>
public enum DeleteReach
{
    /// <summary>The copy in the cloud. Files on this disk are not touched.</summary>
    Cloud,

    /// <summary>The files on this disk, moved to the OS trash. The cloud copy is not touched.</summary>
    Local,

    /// <summary>Both, and the row with them.</summary>
    Everywhere,
}

/// <summary>What one <see cref="DeleteReach"/> does to one row. The last value also says whether the row
/// survives it.</summary>
public enum DeleteOutcome
{
    /// <summary>This row has nothing that the chosen reach can take. For example, a cloud-only track under
    /// <see cref="DeleteReach.Local"/>, or a track that was never in the cloud under
    /// <see cref="DeleteReach.Cloud"/>. It is left alone.</summary>
    Nothing,

    /// <summary>Move the file to the trash. The row stays and becomes <see cref="CloudState.CloudOnly"/>.
    /// The music can still be played, the record stays on the wall, and its mark turns cyan.</summary>
    TrashKeepingCloud,

    /// <summary>Delete the cloud copy. The row stays as local only. The file on this disk is the only copy
    /// from then on.</summary>
    UncloudKeepingFile,

    /// <summary>This is the last copy the row has. Any local file goes to the trash, any cloud copy is
    /// deleted, and the row is dropped. This is the only outcome that removes something from the
    /// library.</summary>
    RemoveEntirely,
}

/// <summary>What a delete does to one row, given the reach the user picked and the state the row is in.
///
/// <para>This rule is shared by three surfaces that must agree: a record's menu, a song's menu and the
/// library table. The interface promises that an album leaves the library when, and only when, its last
/// copy goes. A caller that works this out for itself would be a fourth answer to a question the user has
/// been told has one. So the rule lives here.</para>
/// </summary>
public static class DeletePlan
{
    /// <summary>Whether the account holds the actual bytes for this row. This is narrower than
    /// <see cref="CloudRemovalPlan.IsInTheCloudsHands"/> on purpose. A queued or uploading track has a
    /// place in the line but no object in the cloud. Trashing its file would destroy the only copy there
    /// is, so it must not count as "the cloud has this".
    /// </summary>
    public static bool HasCloudCopy(CloudState state) =>
        state is CloudState.Synced or CloudState.CloudOnly;

    /// <summary>Whether the bytes are on this disk. Only one state says they are not, and the library uses
    /// it for exactly that case.</summary>
    public static bool HasLocalFile(CloudState state) => state != CloudState.CloudOnly;

    public static DeleteOutcome For(DeleteReach reach, CloudState state) => reach switch
    {
        DeleteReach.Local =>
            !HasLocalFile(state) ? DeleteOutcome.Nothing
            : HasCloudCopy(state) ? DeleteOutcome.TrashKeepingCloud
            : DeleteOutcome.RemoveEntirely,

        DeleteReach.Cloud =>
            // This uses the wider test. A track that is only queued has no object to delete, but it has a
            // place in the line. Leaving it there would send it up again within a minute.
            !CloudRemovalPlan.IsInTheCloudsHands(state) ? DeleteOutcome.Nothing
            : HasLocalFile(state) ? DeleteOutcome.UncloudKeepingFile
            : DeleteOutcome.RemoveEntirely,

        _ => DeleteOutcome.RemoveEntirely,
    };

    /// <summary>Whether a folder goes to the OS trash as one folder, rather than file by file.
    ///
    /// <para>This matters more than it seems. A record moved song by song reaches the trash as thirty loose
    /// files at the top level. The folder they came from is left behind. If the listener changes their mind
    /// and puts that folder back, they get the sleeve and none of the music. Trashed as one folder, it is
    /// one entry and one Put Back. That is what "you can restore it from there" promises.
    /// </para>
    ///
    /// <para>This is true only where every row the mirror holds in that folder is going. A record that is
    /// only giving up its local files keeps its rows, its place on the wall and its sleeve. So its folder
    /// is not what is being thrown away.</para>
    /// </summary>
    /// <param name="folderOutcomes">What this delete does to every row the mirror holds under that folder.
    /// Use <see cref="DeleteOutcome.Nothing"/> for the rows it does not touch. Every row counts, not just
    /// the ones selected. A selection of nine songs out of twelve leaves three behind, and taking the
    /// folder would take them too.</param>
    public static bool GoesAsOneRecord(IEnumerable<DeleteOutcome> folderOutcomes)
    {
        ArgumentNullException.ThrowIfNull(folderOutcomes);
        var any = false;
        foreach (var outcome in folderOutcomes)
        {
            if (outcome != DeleteOutcome.RemoveEntirely) return false;
            any = true;
        }
        return any;
    }
}

/// <summary>Which of the three delete entries a menu offers as live, and which it greys.
///
/// <para>All three are always drawn. A menu that loses a line makes the user think they misremembered. A
/// greyed line tells them the thing they need to know: this record is not in the cloud, or none of it is on
/// this disk.</para>
/// </summary>
/// <param name="AnyLocalFile">At least one of the rows still has its bytes on this disk.</param>
/// <param name="AnyInCloud">At least one of the rows is in the cloud, either stored there or queued to go
/// there.</param>
/// <param name="CloudMirrorsEverything">This machine is set to put its whole library in the cloud. Deleting
/// the cloud copy of a track whose FILE is still here clears the row's cloud state. The next sync run then
/// reads it as a local track that belongs in the cloud, and sends it again. So that entry is greyed rather
/// than offered, since it would visibly undo itself. A track with no file here is a different case.
/// Deleting its cloud copy takes the last copy and the row with it, and the syncer has nothing to pick up.
/// That is the second half of deleting a record by hand: local first, then the cloud. This setting must not
/// stop it.</param>
public readonly record struct DeleteOffer(
    bool AnyLocalFile, bool AnyInCloud, bool CloudMirrorsEverything)
{
    /// <summary>Live when there is a file here to move.</summary>
    public bool Local => AnyLocalFile;

    /// <summary>Live when the cloud holds something, and nothing here would be sent straight back up. Only
    /// a file still on this disk can be sent back up. See <see cref="CloudMirrorsEverything"/>.</summary>
    public bool Cloud => AnyInCloud && !(CloudMirrorsEverything && AnyLocalFile);

    /// <summary>Live only when both copies exist. With only one of them, the entry above already is the
    /// whole action. Two live entries doing the same thing is the ambiguity the three entries were split
    /// apart to remove.</summary>
    public bool Everywhere => AnyLocalFile && AnyInCloud;

    /// <summary>Whether the submenu has anything live in it.</summary>
    public bool Any => Local || Cloud || Everywhere;

    /// <summary>Nothing is offered. This is for a borrowed shelf, or a file the library does not
    /// hold.</summary>
    public static readonly DeleteOffer None = new(false, false, false);

    /// <summary>The offer for a set of rows, read directly from their cloud states.</summary>
    public static DeleteOffer For(IEnumerable<CloudState> states, bool cloudMirrorsEverything)
    {
        ArgumentNullException.ThrowIfNull(states);
        var anyLocal = false;
        var anyCloud = false;
        foreach (var state in states)
        {
            anyLocal |= DeletePlan.HasLocalFile(state);
            anyCloud |= CloudRemovalPlan.IsInTheCloudsHands(state);
        }
        return new(anyLocal, anyCloud, cloudMirrorsEverything);
    }
}
