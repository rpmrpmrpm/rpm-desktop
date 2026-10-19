using RPM.Core.Library;

using RPM.Desktop.Presentation.Main.RightPane;

namespace RPM.Desktop.Presentation.Main;

/// <summary>The home view's entry point: the one place it is shown and the one place it is hidden.
///
/// <para>Where home sits is decided in <see cref="PaneConductor"/>. Home is a view of the app itself,
/// not another way to read the collection. So it is placed in the library card's view host next to
/// the four views the rail names. While home is shown, no rail tab is lit for it. The left pane is
/// not covered and does not move. It shows what its chevron and its tab say, home or not. Nothing
/// about either pane changes to show home, so hiding home needs no restore step. The views underneath
/// are exactly where the reader left them.</para>
///
/// <para><b>Home is not on the right rail.</b> This is a deliberate choice. The rail's four entries
/// are four ways of filing the same records: by record, by artist, by genre, by list. Home is not a
/// fifth filing. It shows what has been played, which is a different question about the same records.
/// So home has one entry point, the deck's own key. That key shows the rail's mark while
/// home is shown.</para>
/// </summary>
public sealed partial class MainPage
{
    /// <summary>Shows or hides the home view.
    ///
    /// <para><b>Asking for home never hides it.</b> The deck's key does not toggle. Pressing it while
    /// home is shown leaves home shown. The key always shows home, because a key that sometimes opened
    /// home and sometimes an album wall, depending on where the reader was, could not be predicted
    /// before pressing.</para>
    ///
    /// <para>The way out is not lost. The library's rail stands beside home while it is shown, and any
    /// of its four entries hides home. So does a record or a list pressed on home itself. The left rail
    /// is not a way out: what it opens stands beside home rather than in front of it (see
    /// <see cref="PaneConductor.OpenTab"/>).</para>
    ///
    /// <para>Asking for home while it is already shown does nothing, and the data is not read again. A
    /// rebuild would move the rows under the reader, who would think the screen had lost its
    /// place.</para>
    /// </summary>
    private void ShowHome(bool show)
    {
        if (show && Panes.AtHome)
            return;

        // Read the data on the way in only if the library would now answer differently. Home is not
        // reset while it is hidden. It is only covered. So a screen that nothing has changed is still
        // the screen the reader left, with its rows, pictures and scroll position. See HomeView.Stale,
        // which holds the two things that can make that untrue.
        if (show && Home.Stale)
            Home.Load();

        Panes.GoHome(show);
    }

    /// <summary>The one button on the locked home panel. It opens the same window as the Add music button
    /// on the library page, not the setup wizard. The collection already exists. It is just small, so the
    /// setup is not needed.</summary>
    private void Home_AddMusicRequested(object? sender, EventArgs e)
        => AddMusic_Click(this, new Microsoft.UI.Xaml.RoutedEventArgs());

    /// <summary>A record's own page, asked for from the home screen by a press on its cover. Opening it
    /// asks for the Albums view, which hides home. The record remembers that it was opened from here, so
    /// its back button shows home again. Without that it would return to an album wall the reader never
    /// came through. See AlbumFrom.HomeScreen.
    ///
    /// <para>Pressing the card itself does not come here. It opens the record's tracklist under its row
    /// (see HomeView.Records), and home stays shown.</para></summary>
    private void Home_AlbumRequested(object? sender, string folder) =>
        NavigateToAlbumPage(folder, AlbumFrom.HomeScreen);

    /// <summary>A generated list pressed on the home screen. It opens in the playlists area, at the same
    /// tracklist that pressing its row in the tree opens. Asking for that view also hides home.</summary>
    private void Home_PlaylistRequested(object? sender, SmartPlaylistId id)
    {
        // Built first and moved second, as explained on NavigateToAlbumPage. The work that fills the view
        // must not run inside the fade whose end is called back on the same thread.
        EnsurePlaylistPanel();
        PlaylistPanel.OpenSmart(id, fromHome: true);
        Panes.OpenRightTab(RightTab.Playlists);
    }

    /// <summary>A playlist from the shelf, pressed on the home screen. A borrowed shelf shows the
    /// lender's lists, mirrored with their records. It uses the same path as the tree's own rows, so it
    /// opens the same tracklist with the same actions.</summary>
    private void Home_StoredPlaylistRequested(object? sender, long nodeId)
    {
        EnsurePlaylistPanel();
        PlaylistPanel.OpenPlaylist(nodeId, fromHome: true);
        Panes.OpenRightTab(RightTab.Playlists);
    }

    /// <summary>
    /// The radio card pressed on the home screen: the station is tuned in, and nothing else happens.
    /// </summary>
    /// <remarks>This is the one card on this screen whose press starts audio. Every other card opens a
    /// view, which hides home. A radio station has no page to open, only a list it is one row of. So
    /// pressing the card tunes the station.
    ///
    /// <para><b>It does not open the stations pane.</b> A left tab does not change home, so calling
    /// <c>Panes.OpenTab(LeftTab.Radio)</c> here would open the stations pane beside home. The card does
    /// not do that: pressing it only tunes the station. Opening a pane as well would be a second response
    /// to one press. Making the card open the pane would be a deliberate behaviour change.</para>
    ///
    /// <para><b>What shows the press worked.</b> A line along the foot of the window. This app uses
    /// that line when a press has no visible answer of its own (see <see cref="ShowFlash"/> and the
    /// track pane's <c>Announced</c>, which is the same case). It is chrome over the card, not a view
    /// inside it, so home stays shown.</para>
    ///
    /// <para>The line is needed when the track pane is folded. The deck along the foot shows transport,
    /// volume, chips and the menu, but not what is playing. With the pane open beside home, the pane
    /// shows it too, and the line becomes a second answer that costs nothing.</para>
    /// </remarks>
    private void Home_StationRequested(object? sender, RPM.Core.Radio.RadioStation station)
    {
        App.Services.GetRequiredService<RadioService>().Tune(station);
        ShowFlash(Loc.Format("Radio_TunedTo", station.Display));
    }
}
