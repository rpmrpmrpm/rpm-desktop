using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

using RPM.Core.Library;
using RPM.Desktop.Services.Shell;
using RPM.Desktop.Presentation.Main.LeftPane.Models;

namespace RPM.Desktop.Presentation.Main.RightPane;

/// <summary>The card under the tree that is about the generated lists that are not there yet.
///
/// <para>rpm writes those lists from the collection. A list that the collection cannot answer is not drawn
/// at all. So a short tree gives no clue about which of two things is true. Either the app can do
/// everything, or this shelf can only answer some of it so far. The card says the second. It names the
/// three closest lists, and says what each one is waiting for.</para>
///
/// <para>What to show is decided by <see cref="LockedLists"/>. That decision is taken on the same worker
/// read that sized the tree. This class only puts it on screen. Nothing here reads the database.</para>
/// </summary>
public sealed partial class PlaylistView
{
    /// <summary>Draws the card, or takes it away.
    /// </summary>
    /// <remarks>This is called from every place that can change either half of the answer. Those places
    /// are: the model landing (where the sizes and the measuring reading arrive), the switch between the
    /// list and the grid, and stepping in and out of a folder. It is cheap enough to run on all of them.
    /// There are at most three rows and no query. The early return below is the usual case once a
    /// collection has filled out.
    /// </remarks>
    private void ShowLockedLists()
    {
        var locked = Model?.Locked ?? [];

        // The setting is read here, and not only by collapsing the card on click. That way a model landing
        // later cannot bring back a card that the user closed.
        var hidden = App.Services.GetRequiredService<SettingsService>().LockedListsHidden;

        if (locked.Count == 0 || hidden || !LockedCardBelongsHere)
        {
            LockedCard.Visibility = Visibility.Collapsed;

            // Stopped, not just hidden. The ring animates on a timer, and every frame of it repaints the
            // whole window. A ring left running behind a collapsed card would repaint the window many times
            // a second, for nothing that anybody can see.
            LockedRing.Live = false;
            return;
        }

        LockedRows.Children.Clear();
        foreach (var list in locked)
            LockedRows.Children.Add(Line(list));

        // The closest of them is the one the rows open with. LockedLists returns them in that order.
        LockedRing.Fraction = locked[0].Gate.Fraction;
        LockedCard.Visibility = Visibility.Visible;
        LockedRing.Live = true;
    }

    /// <summary>The card's close button. It hides the card for good. See <see
    /// cref="SettingsService.LockedListsHidden"/>.</summary>
    private void LockedHide_Click(object sender, RoutedEventArgs e)
    {
        App.Services.GetRequiredService<SettingsService>().LockedListsHidden = true;
        ShowLockedLists();
    }

    /// <summary>Whether this card is about what is on the screen right now.
    /// </summary>
    /// <remarks>The list view draws the whole tree at once, so the card is always about what it shows. The
    /// grid draws one folder. A card about generated playlists that sits under somebody's own "Live albums"
    /// folder is out of place. It belongs at the top of the tree, and inside the folders where the
    /// generated lists live. That is where a user goes to look for exactly this.
    /// </remarks>
    private bool LockedCardBelongsHere =>
        _viewMode != PlaylistViewMode.Icons
        || _openFolder is null
        || _openFolder.IsAppFolder;

    /// <summary>One list and what it is waiting for, as one wrapping line: the name in white, and the
    /// sentence after it in grey. It is one line rather than a name over a sentence. The pane can be narrow
    /// and there are three of these lines. Also, the name and its reason are one statement.
    /// </summary>
    private TextBlock Line(LockedList list)
    {
        var block = new TextBlock
        {
            FontSize = 12,
            LineHeight = 17,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
        };

        block.Inlines.Add(new Run
        {
            Text = SmartPlaylistFace.Name(list.Id) + ": ",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
        });

        block.Inlines.Add(new Run
        {
            Text = LockedListLine.Text(list.Gate, Model?.Measuring ?? default),
        });

        return block;
    }
}
