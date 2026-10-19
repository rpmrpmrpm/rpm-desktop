using RPM.Core.Cloud;
using RPM.Desktop.Presentation.Main.LeftPane.Models;

namespace RPM.Desktop.Services.Sharing;

/// <summary>
/// A cut as it goes out to anybody: with the names this app calls its lists, the tree it files them
/// in, and an id each of them answers to.
///
/// <para><b>Done here, not at each publisher, so the two publishers cannot disagree.</b> The cloud push
/// and the peer catalogue both send <see cref="RPM.Core.Library.LibraryDatabase.SessionMoodLists"/>.
/// That is records and nothing else. The database holds the music and the interface holds the words.
/// If each far end arranged the lists itself, they would arrive in different shapes. The borrowed shelf
/// would put the eras and the mood flavours flat in one folder, and the phone would work out its own
/// shape from the kind and the genre. A cut is a shelf as somebody arranged it, and the arrangement is
/// part of what is shared.</para>
///
/// <para><b>Only what the database cannot know is added:</b> the name
/// (<see cref="SmartPlaylistFace.Name(string, string?, int)"/>), the place
/// (<see cref="SmartPlaylistFace.FolderChain"/>) and the id
/// (<see cref="MoodListPush.IdOf"/>). Which records are in a list is not changed here.</para>
///
/// <para>This is applied at the boundary, not inside the cut. So one cut stays one cut for a session.
/// The same records go to the phone and to a borrower, and they are dressed the same way.</para>
/// </summary>
public static class PublishedMoodLists
{
    /// <summary>The cut, ready to send.</summary>
    public static MoodListsPayload Dressed(MoodListsPayload cut)
    {
        ArgumentNullException.ThrowIfNull(cut);

        return cut with
        {
            Lists = [.. cut.Lists.Select(list => list with
            {
                Title = SmartPlaylistFace.Name(list.Kind, list.Genre, list.Decade),
                Folder = SmartPlaylistFace.FolderChain(list.Kind, list.Genre, list.Decade),
                Id = MoodListPush.IdOf(list.Kind, list.Genre, list.Decade).ToString("D"),
            })],
        };
    }
}
