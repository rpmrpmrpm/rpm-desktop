using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>What the delete confirmation is about. The reach does not decide this part of the
/// wording.</summary>
public enum DeleteSubject
{
    /// <summary>One record, named.</summary>
    Album,

    /// <summary>One song, named.</summary>
    Track,

    /// <summary>A selection of songs, counted by number. Naming the first of nine would tell the listener
    /// that one thing is deleted, when nine are.</summary>
    Tracks,
}

/// <summary>What the delete dialog says.</summary>
/// <param name="BodyKey">The string the body is read from. There is one per reach per subject. There is
/// also a second one per reach, for the case where the copy being deleted is the last one. A single
/// sentence that hides that difference would let a listener delete their only copy while believing it is
/// still in the cloud.</param>
/// <param name="ActionKey">The string the title and the confirm button are both read from. These are the
/// same three words as the menu entry that opened the dialog, so the dialog is clearly the answer to the
/// entry that was clicked.</param>
/// <param name="LeavesLibrary">Whether anything will be removed from the library. This decides which of
/// the two bodies each reach uses.</param>
public readonly record struct DeleteWording(string BodyKey, string ActionKey, bool LeavesLibrary);

/// <summary>
/// Which question the delete dialog asks: the reach the listener picked, combined with where the files of
/// the selected rows actually are.
///
/// <para>This is kept out of the page because it has the most branches. It is shared by the record's
/// menu, the song's menu and the library table, so all three always describe the same action in the same
/// way.</para>
/// </summary>
public static class DeletePrompt
{
    /// <param name="states">The cloud state of every row about to be deleted. Never empty: a delete with
    /// nothing to delete is stopped before a dialog is built.</param>
    public static DeleteWording For(DeleteReach reach, DeleteSubject subject, IEnumerable<CloudState> states)
    {
        ArgumentNullException.ThrowIfNull(states);

        // If any copy will not survive this delete, that decides the wording for the whole set. Take a
        // selection where eleven keep their cloud copy and one does not. That delete loses a song. The
        // gentler sentence would be true for the eleven and false for the twelfth.
        var leaves = reach == DeleteReach.Everywhere
            || states.Any(s => DeletePlan.For(reach, s) == DeleteOutcome.RemoveEntirely);

        var prefix = subject switch
        {
            DeleteSubject.Album => "AlbumDelete_",
            DeleteSubject.Track => "TrackDelete_",
            _ => "TrackDelete_Many",
        };

        var body = reach switch
        {
            DeleteReach.Local => leaves ? "LocalLast" : "Local",
            DeleteReach.Cloud => leaves ? "CloudLast" : "Cloud",
            _ => "Everywhere",
        };

        return new(prefix + body, ActionKey(reach), leaves);
    }

    /// <summary>The three words for one reach. The menu entry, the dialog title and the confirm button
    /// are all read from this, so they name the same action.</summary>
    public static string ActionKey(DeleteReach reach) => reach switch
    {
        DeleteReach.Local => "Delete_Local",
        DeleteReach.Cloud => "Delete_Cloud",
        _ => "Delete_Everywhere",
    };
}
