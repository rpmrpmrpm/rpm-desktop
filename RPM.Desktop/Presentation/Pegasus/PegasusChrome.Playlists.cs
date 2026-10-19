using RPM.Core.Library;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// Which list the song is going on, the sheet a leftward swipe opens over the drawer.
///
/// <para>The other half of the gesture (see PegasusChrome.Swipe) needs no sheet: there is one queue, and
/// a song pushed right joins it. A list is a choice, and a choice made by the same shove that expressed
/// the intent would file records by accident. So the swipe opens this, the listener says which, and then
/// says so again on the key, two presses, because the second one is the only chance to notice that the
/// first was the wrong list.</para>
///
/// <para>Read at the moment it opens and thrown away when it closes, like everything else in this drawer:
/// the collection is next door in a window somebody may be editing, and a list of lists held over a
/// visualizer would be one more thing to keep true. The write goes through the same database call the
/// library's own "add to playlist" makes, and says so afterwards, see
/// <see cref="LibraryService.NotifyChanged"/>, which is what tells the pane behind this screen.</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>Which line of the open record the sheet is about, or -1 while it is down.</summary>
    private int _sheetTrack = -1;

    /// <summary>The list chosen so far, and what it is called, the name is wanted after the sheet has
    /// gone, for the sentence at the foot of the screen.</summary>
    private long? _sheetPick;
    private string? _sheetPickName;

    /// <summary>The row wearing the choice, so choosing another can put this one out.</summary>
    private Border? _sheetPickRow;

    /// <summary>Whether the sheet is up. The drawer counts itself down to closing after three seconds
    /// with nothing happening, and a panel being READ is not a panel nobody is using, see the idle
    /// count in the tick.</summary>
    private bool _sheetUp;

    /// <summary>Opens the sheet on one song: the lists as they stand, nothing chosen yet.</summary>
    private void AskWhichPlaylist(int index)
    {
        if (index < 0 || index >= _albumTracks.Count)
            return;

        var library = Library;
        if (!library.IsOpen)
            return;

        // Unanswered opens nothing rather than an empty sheet: a refusal has put the problem screen up
        // over the app, and a closed mirror is a library that moved under the swipe.
        if (!library.Read(library.Db, "the playlists for the add sheet", db => db.GetPlaylistNodes())
                   .Answers(out var nodes, out _))
            return;

        // The made lists only. The generated ones (favourites, most played) are a question the
        // collection answers for itself, and nothing can be filed onto an answer. "Now playing" is
        // not here either: that is the queue, and pushing the row the other way is how it gets there.
        List<PlaylistNode> lists = [.. nodes.Where(n => n.Kind == PlaylistNodeKind.Playlist && !n.IsNowPlaying)];

        lists.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));

        var scale = QueueScale;
        _sheetTrack = index;
        _sheetPick = null;
        _sheetPickName = null;
        _sheetPickRow = null;

        PlaylistSheetRows.Children.Clear();
        PlaylistSheetRows.Spacing = QueueRowGap * scale;
        foreach (var node in lists)
            PlaylistSheetRows.Children.Add(SheetRow(node, scale));

        PlaylistSheetScroll.Visibility = lists.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PlaylistSheetEmpty.Visibility = lists.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        PlaylistSheetScroll.ChangeView(null, 0, null, disableAnimation: true);

        SizeSheet(scale);

        _sheetUp = true;
        PlaylistSheet.Visibility = Visibility.Visible;
        PlaylistSheet.IsHitTestVisible = true;
        _sheetFade.To(1, ScreenCrossMs, null);
    }

    /// <summary>One list: a tick where the choice goes, and its name. The same hand-painted row the rest
    /// of the drawer is made of, for the same reason, see LightRow.</summary>
    private Border SheetRow(PlaylistNode node, double scale)
    {
        var grid = new Grid { ColumnSpacing = 8 * scale };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16 * scale) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var tick = new TextBlock
        {
            Text = "",   // check
            FontFamily = (FontFamily)Resources["PegasusFa"],
            FontSize = 11 * scale,
            Opacity = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(tick, 0);

        var name = new TextBlock
        {
            Text = node.Name,
            FontSize = 13.5 * scale,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(name, 1);

        grid.Children.Add(tick);
        grid.Children.Add(name);

        var row = new Border
        {
            Tag = node,
            Child = grid,
            Padding = new Thickness(8 * scale, 7 * scale, 8 * scale, 7 * scale),
            CornerRadius = new CornerRadius(6 * scale),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        row.PointerEntered += DrawerRow_PointerEntered;
        row.PointerExited += DrawerRow_PointerExited;
        row.PointerReleased += SheetRow_Released;
        return row;
    }

    private void SheetRow_Released(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border row || row.Tag is not PlaylistNode node)
            return;

        if (_sheetPickRow is { } last)
            PaintSheetPick(last, chosen: false);

        _sheetPickRow = row;
        _sheetPick = node.Id;
        _sheetPickName = node.Name;
        PaintSheetPick(row, chosen: true);
        PlaylistSheetAdd.IsEnabled = true;
    }

    /// <summary>The chosen list wears its tick and a wash it keeps whether or not the pointer is on it,
    /// the hover comes and goes, and a choice must not look like one of those.</summary>
    private static void PaintSheetPick(Border row, bool chosen)
    {
        row.Background = new SolidColorBrush(chosen
            ? Color.FromArgb(0x2B, 0xFF, 0xFF, 0xFF)
            : Microsoft.UI.Colors.Transparent);

        if (row.Child is Grid grid && grid.Children.Count > 0 && grid.Children[0] is TextBlock tick)
            tick.Opacity = chosen ? 0.9 : 0;
    }

    /// <summary>The sheet at the size the drawer is drawn: it is a card over a panel that grows with the
    /// window, and a sheet that did not grow with it would look too small on a large window.</summary>
    private void SizeSheet(double scale)
    {
        PlaylistSheetTitle.FontSize = 14 * scale;
        PlaylistSheetEmpty.FontSize = 12.5 * scale;

        // How tall the names may stand before they scroll instead. The box itself is sized by what is in
        // it, so this is the only thing holding it down: a collection with forty lists in it would
        // otherwise open a card the height of the window to ask a one line question.
        var room = Math.Max(96 * scale, (Root.ActualHeight * 0.34) - (110 * scale));
        PlaylistSheetScroll.MaxHeight = room;
        PlaylistSheetEmpty.MaxHeight = room;

        foreach (var key in new[] { PlaylistSheetCancel, PlaylistSheetAdd })
        {
            key.FontSize = 12.5 * scale;
            key.Height = 34 * scale;
            key.Padding = new Thickness(14 * scale, 0, 14 * scale, 0);
            key.CornerRadius = new CornerRadius(7 * scale);
        }
    }

    private void PlaylistSheetCancel_Click(object sender, RoutedEventArgs e) => CloseSheet();

    /// <summary>
    /// The confirm: the song goes on the chosen list, and the foot of the screen says which. When the
    /// list already holds the song, the same question every other "add to playlist" asks comes first
    /// (see <see cref="Dialogs.AlreadyInPlaylistPrompt"/>).
    ///
    /// <para>Keyed by the song's rpm id with its library-relative path behind it, the same pair every
    /// other "add to playlist" in the app writes, so a song filed from here is a song the list can still
    /// find after the file has been moved or retagged.</para>
    /// </summary>
    private async void PlaylistSheetAdd_Click(object sender, RoutedEventArgs e)
    {
        var index = _sheetTrack;
        var playlist = _sheetPick;
        var name = _sheetPickName;

        CloseSheet();

        if (playlist is not { } id || index < 0 || index >= _albumTracks.Count)
            return;

        var library = Library;
        if (!library.IsOpen || library.Root is not { } root)
            return;

        // Taken before the question: the drawer can move to another record while the dialog is up.
        var track = _albumTracks[index];
        var db = library.Db;
        (string? RpmId, string RelPath) song = (track.RpmId, Relative(root, track.FilePath));

        // Unanswered writes nothing and says nothing: a refusal has put the problem screen up over the
        // app, and a closed mirror is a library that has gone.
        if (!library.Read(db, "how much of a song a playlist holds", mirror => mirror.CountAlreadyInPlaylist(id, [song]))
                   .Answers(out var held, out _))
            return;

        if (await Dialogs.AlreadyInPlaylistPrompt.AskAsync(XamlRoot, name ?? "", held, 1) is not { } whenAlreadyThere)
            return;

        // The library may have been closed or switched while the question was up. The write goes to the
        // database the count was read from, or nowhere.
        if (!library.IsOpen || !ReferenceEquals(library.Db, db))
            return;

        int added;
        try
        {
            added = db.AddSongsToPlaylist(id, [song], whenAlreadyThere);
        }
        catch
        {
            // The database has gone. Nothing is said: a write that did not happen must not be reported
            // as one that did.
            return;
        }

        // Nought is the list having been deleted while the sheet or the question was up, or a sync having
        // put the song in it a moment after it was counted. Either way nothing was written, so nothing is
        // announced.
        if (added == 0)
            return;

        // The pane in the window behind this screen is looking at the same list, and does not otherwise
        // hear about a write made from over the top of it.
        library.NotifyChanged();

        Announced?.Invoke(string.Format(
            Strings.Loc.Get("Viz_PlaylistAdded"), TitleOf(track), name ?? ""));
    }

    /// <summary>A file where the collection keeps it: relative to the root, forward slashes, which is the
    /// shape every row in the database is written in.</summary>
    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>Takes the sheet down and forgets what it was about.</summary>
    private void CloseSheet()
    {
        if (!_sheetUp)
            return;

        _sheetUp = false;
        _sheetTrack = -1;
        _sheetPick = null;
        _sheetPickName = null;
        _sheetPickRow = null;
        PlaylistSheetAdd.IsEnabled = false;
        PlaylistSheet.IsHitTestVisible = false;
        LightRow(null);   // the rows about to go include whichever one was lit

        _sheetFade.To(0, ScreenCrossMs, () =>
        {
            if (_sheetUp)
                return;   // asked again while it was leaving
            PlaylistSheet.Visibility = Visibility.Collapsed;
            PlaylistSheetRows.Children.Clear();
        });
    }
}
