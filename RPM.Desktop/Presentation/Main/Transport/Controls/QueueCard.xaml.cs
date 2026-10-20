using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RPM.Desktop.Presentation.RPaMp.Models;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Main.Transport.Controls;

/// <summary>The queue card the deck's queue key opens: every row of the one shared queue, with the playing one
/// lit. Pressing a row plays it where it stands. Its DataContext is the <see cref="RPaMpModel"/>, inherited from
/// the button that opens it as a flyout.</summary>
public sealed partial class QueueCard : UserControl
{
    public QueueCard()
    {
        InitializeComponent();
    }

    private RPaMpModel? Model => DataContext as RPaMpModel;

    /// <summary>Called by the host flyout as it opens. The list is the model's own collection, so rows that
    /// arrive while the card is open (a song radio topping up, a "play next") show at once. The count and the
    /// scroll position are only read here, because the card is closed most of the time.</summary>
    public void Refresh()
    {
        if (Model is not { } model) return;

        if (!ReferenceEquals(Rows.ItemsSource, model.Playlist))
            Rows.ItemsSource = model.Playlist;

        var count = model.Playlist.Count;
        CountText.Text = count == 0 ? "" : Loc.Format("Home_Songs", count);
        EmptyText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Rows.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;

        CapHeight();

        // The playing row is the one you came to see, so the card opens on it, a couple of rows down from the top
        // so what came before it is still in sight.
        var current = model.Playlist.FirstOrDefault(t => t.IsCurrent);
        if (current is not null)
            Rows.ScrollIntoView(current, ScrollIntoViewAlignment.Leading);
    }

    /// <summary>Caps the list to the window, less the deck it opens from and a gap at the top, so a long queue
    /// scrolls inside the card rather than running off the screen.</summary>
    private void CapHeight()
    {
        if (XamlRoot is { } root && root.Size.Height > 0)
            Rows.MaxHeight = Math.Clamp(root.Size.Height - 180, 200, 520);
    }

    private void Rows_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PlaylistTrack track)
            Model?.PlayTrack(track);
    }
}
