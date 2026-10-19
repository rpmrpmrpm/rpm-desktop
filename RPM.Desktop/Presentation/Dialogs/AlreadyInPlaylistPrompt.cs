using RPM.Core.Library;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>Which question is asked before songs go on a playlist, from how many of them it already
/// holds.</summary>
public enum AlreadyInPlaylistQuestion
{
    /// <summary>None of them are there. Nothing is asked.</summary>
    None,

    /// <summary>One song, already there: add it again, or cancel.</summary>
    Track,

    /// <summary>Several songs, all already there: add them again, or cancel.</summary>
    AllTracks,

    /// <summary>Several songs, some already there: add only the new ones, add all, or cancel.</summary>
    SomeTracks,
}

/// <summary>
/// The confirmation shown when a song is added to a playlist that already holds it. Every desktop
/// "add to playlist" (track menu, library table, album menu, the full-screen player's sheet) asks
/// through here, so they all ask the same question in the same words. The phone asks the same question
/// for one song in <c>PlaylistPickSheet</c>, with the same strings.
/// </summary>
public static class AlreadyInPlaylistPrompt
{
    public static AlreadyInPlaylistQuestion Question(int held, int total) =>
        held <= 0 ? AlreadyInPlaylistQuestion.None
        : total <= 1 ? AlreadyInPlaylistQuestion.Track
        : held >= total ? AlreadyInPlaylistQuestion.AllTracks
        : AlreadyInPlaylistQuestion.SomeTracks;

    /// <summary>What the button pressed means for the write. Null is "add nothing": cancel, Escape,
    /// or no window to ask in.</summary>
    public static AlreadyInPlaylist? Answer(AlreadyInPlaylistQuestion question, ContentDialogResult pressed) =>
        (question, pressed) switch
        {
            (AlreadyInPlaylistQuestion.None, _) => AlreadyInPlaylist.LeaveIt,
            (AlreadyInPlaylistQuestion.SomeTracks, ContentDialogResult.Primary) => AlreadyInPlaylist.LeaveIt,
            (AlreadyInPlaylistQuestion.SomeTracks, ContentDialogResult.Secondary) => AlreadyInPlaylist.AddAnother,
            (_, ContentDialogResult.Primary) => AlreadyInPlaylist.AddAnother,
            _ => null,
        };

    /// <summary>
    /// Asks, when there is anything to ask, and says what the write should do.
    /// </summary>
    /// <param name="playlist">The playlist's name, for the title.</param>
    /// <param name="held">How many of the songs being added the playlist already holds.</param>
    /// <param name="total">How many songs are being added.</param>
    /// <returns>What to pass to the write, or null to write nothing. Returns
    /// <see cref="AlreadyInPlaylist.LeaveIt"/> without showing anything when none of the songs are
    /// there.</returns>
    public static async Task<AlreadyInPlaylist?> AskAsync(XamlRoot? root, string playlist, int held, int total)
    {
        var question = Question(held, total);
        if (question == AlreadyInPlaylistQuestion.None)
            return AlreadyInPlaylist.LeaveIt;

        var title = Loc.Format("Rpm_PlaylistPick_AlreadyIn", playlist);

        var pressed = question switch
        {
            // Enter adds only the new songs. That is the add the user asked for, without the copies.
            AlreadyInPlaylistQuestion.SomeTracks => await PromptDialog.ShowAsync(
                root, title,
                held == 1 ? Loc.Format("Playlists_AlreadyInOneOf", total)
                          : Loc.Format("Playlists_AlreadyInSomeOf", held, total),
                primaryText: Loc.Get("Playlists_AddNewOnes"),
                secondaryText: Loc.Get("Playlists_AddAll"),
                closeText: Loc.Get("Common_Cancel"),
                defaultButton: ContentDialogButton.Primary),

            // Enter cancels: a second copy is only added by pressing the button that says so.
            _ => await PromptDialog.ShowAsync(
                root, title,
                Loc.Get(question == AlreadyInPlaylistQuestion.Track
                    ? "Rpm_PlaylistPick_AlreadyInBody" : "Playlists_AlreadyInAll"),
                primaryText: Loc.Get("Rpm_PlaylistPick_AddAgain"),
                closeText: Loc.Get("Common_Cancel"),
                defaultButton: ContentDialogButton.Close),
        };

        return Answer(question, pressed);
    }
}
