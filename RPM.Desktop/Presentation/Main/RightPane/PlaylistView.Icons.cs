using System.Collections.ObjectModel;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Main.RightPane;

/// <summary>The second way of reading the same tree: the covers of one folder's contents, in a grid. This
/// is how a file browser shows a folder of files. The list beside it draws the whole tree at once, and it
/// shows what is inside what with an indent. This draws one level, and it shows the nesting by being inside
/// the folder.
///
/// <para>The tree itself is not changed by any of this. Which folder is open is the surface's own state. No
/// row is marked, nothing is expanded, and switching back to the list finds it exactly as it was left. The
/// cost of this is the check in <see cref="RebuildTiles"/>. State that the surface holds about a tree it
/// does not own must be checked against the tree again, every time the tree changes. See <see
/// cref="PlaylistIcons.StillOpen"/>.</para>
/// </summary>
public sealed partial class PlaylistView
{
    private PlaylistViewMode _viewMode = PlaylistViewMode.List;

    /// <summary>The folder the grid is inside, or null at the top of the tree. Nothing that draws reads
    /// this directly. <see cref="RebuildTiles"/> passes it through <see cref="PlaylistIcons.StillOpen"/>
    /// first, because a folder can be deleted while it is the one on screen.</summary>
    private PlaylistTreeItem? _openFolder;

    /// <summary>What the grid is showing. It has its own collection, not the model's <c>Visible</c>. That
    /// one is the tree flattened by expansion. It is the list's source, and it says nothing about what is
    /// inside the folder in front of you.</summary>
    private readonly ObservableCollection<PlaylistTreeItem> _tiles = new();

    private bool _iconsAttached;

    /// <summary>Points the grid at its collection, and puts the pane back in whichever view it was left
    /// in.
    ///
    /// <para>This runs on Loaded, not in the constructor, and it is guarded. The stored view is read
    /// through the service provider. Loaded can run again for a control that is re-parented.</para>
    /// </summary>
    private void AttachIcons()
    {
        if (_iconsAttached)
            return;

        _iconsAttached = true;
        Tiles.ElementPrepared += Tile_ElementPrepared;
        Tiles.ItemsSource = _tiles;

        // The remembered cover size is seeded before the first tile is laid out. So the grid is never seen
        // at one size and then resized into another. This is quiet. The slider's own handler writes the
        // setting back, and a seed is not a user's choice.
        var settings = App.Services.GetRequiredService<SettingsService>();
        _tileTargetArt = Math.Clamp(
            settings.PlaylistTileSize, Library.AlbumWallView.MinArt, Library.AlbumWallView.MaxArt);
        _seedingTileSize = true;
        TileSizeSlider.Value = _tileTargetArt;
        _seedingTileSize = false;
        TileSizeSlider.ValueChanged += TileSize_ValueChanged;

        ApplyViewMode(settings.PlaylistView);
    }

    // ── the switch ──

    private void ViewIcons_Click(object sender, RoutedEventArgs e) => SwitchView(PlaylistViewMode.Icons);

    private void ViewList_Click(object sender, RoutedEventArgs e) => SwitchView(PlaylistViewMode.List);

    private void SwitchView(PlaylistViewMode mode)
    {
        if (_viewMode == mode)
            return;

        App.Services.GetRequiredService<SettingsService>().PlaylistView = mode;
        ApplyViewMode(mode);
    }

    private void ApplyViewMode(PlaylistViewMode mode)
    {
        _viewMode = mode;
        SelectedSegment.Paint(ViewIconsButton, mode == PlaylistViewMode.Icons);
        SelectedSegment.Paint(ViewListButton, mode == PlaylistViewMode.List);

        var icons = mode == PlaylistViewMode.Icons;
        Tree.Visibility = icons ? Visibility.Collapsed : Visibility.Visible;
        IconsSide.Visibility = icons ? Visibility.Visible : Visibility.Collapsed;

        // The size slider belongs to the grid, so it goes with the grid. On the list there is nothing for
        // it to size. The library's header follows the same rule, where the slider's whole slot slides away
        // when the wall does.
        TileSizeCluster.Visibility = icons ? Visibility.Visible : Visibility.Collapsed;

        // Whatever a half-finished drag was aiming at is not on screen any more. The list's own pointer
        // handlers cannot fire while it is collapsed, so nothing else would clear this.
        TreeDropLine.Visibility = Visibility.Collapsed;

        if (icons)
            RebuildTiles();

        // The card is about the shelf, not about either view. But whether it belongs on screen depends on
        // which view is up, and on which folder is open.
        ShowLockedLists();
    }

    // ── what the grid is showing ──

    /// <summary>Puts what is inside the open folder onto the grid.
    ///
    /// <para>This runs on every change to the tree, as well as on every step in and out of a folder. For
    /// the grid these are the same event. The tiles are the folder's children, and an import, a rename, a
    /// delete or a shelf swap all rewrite those children. That is also why the open folder is checked again
    /// here, and not trusted. See <see cref="PlaylistIcons.StillOpen"/>. That check is what leaves somebody
    /// who deletes the folder they are in standing in the folder that held it. Otherwise they would be left
    /// in front of an empty grid that never fills.</para>
    ///
    /// <para>The collection is only rewritten when it would actually differ. The comparison is row by row,
    /// and by reference. Rows survive a reload, so a usual tree change hands back the same objects in the
    /// same order. Clearing and refilling for that would make every tile in the folder lose its container,
    /// its picture and its place on the screen for a frame. That would happen several times a second during
    /// an import.</para>
    /// </summary>
    internal void RebuildTiles()
    {
        if (_viewMode != PlaylistViewMode.Icons || Model is not { } model)
            return;

        _openFolder = PlaylistIcons.StillOpen(
            _openFolder, model.Roots, item => item.Parent, item => item.Children);

        // The folder's rows, plus the lists nested inside any of them. See PlaylistIcons.TilesFor for why a
        // grid cannot leave those behind a tile.
        IReadOnlyList<PlaylistTreeItem> wanted = PlaylistIcons.TilesFor(
            _openFolder is { } open ? open.Children : model.Roots, item => item.Kind, item => item.Children);

        // Every tile on the grid shares one measurement, so the slider writes a size once and all the tiles
        // follow. It is written on every rebuild, not once. The rows belong to the TREE, and a folder
        // opened for the first time hands over rows that have never been tiles.
        foreach (var item in wanted)
            item.Metrics = _tileMetrics;

        if (!SameTiles(wanted))
        {
            _tiles.Clear();
            foreach (var item in wanted)
                _tiles.Add(item);
        }

        WritePath();

        // Only inside a folder that somebody made. At the top of the tree an empty grid means there is no
        // library open yet. "This folder is empty" would then be a sentence about a folder that nobody has.
        // The pane's own subheading says that instead.
        IconsEmpty.Visibility = _openFolder is not null && _tiles.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private bool SameTiles(IReadOnlyList<PlaylistTreeItem> wanted)
    {
        if (_tiles.Count != wanted.Count)
            return false;

        for (var i = 0; i < wanted.Count; i++)
            if (!ReferenceEquals(_tiles[i], wanted[i]))
                return false;

        return true;
    }

    /// <summary>Steps into a folder, or back out of one.
    ///
    /// <para><b>Nothing slides.</b> A folder change does not slide the grid in from the side. A slide like
    /// that reads as a jerk, because the view is rebuilt and laid out during the animation. The drill into
    /// a tracklist swaps two views that are already laid out. This one rebuilds the view that it is
    /// animating, in this same handler, before the animation's first frame. The shift is applied at once.
    /// Then the tiles are realised, measured, and their covers are requested. So the grid stands 18 points
    /// to the side for as long as that pass takes. The eased animation would then start after the jump
    /// has already been seen. A folder change is the one transition where the content and its position cannot both
    /// be new in the same frame. So only the content is new. Which folder is open is shown by the path
    /// above the tiles, as a file browser shows it.</para>
    /// </summary>
    private void OpenFolder(PlaylistTreeItem? folder)
    {
        _openFolder = folder;
        RebuildTiles();

        // Which folder is open decides whether the card about the lists that are not there yet belongs on
        // screen. See PlaylistView.Locked.
        ShowLockedLists();
    }

    // ── the path above the tiles ──

    /// <summary>Writes where the grid is standing: a way back out, the top of the tree, and every folder
    /// between the top and here. Each of those folders is a way back to itself. At the top, the whole path
    /// is down, because the pane's own heading already says where you are.
    /// </summary>
    private void WritePath()
    {
        IconPath.Children.Clear();

        var path = PlaylistIcons.PathTo(_openFolder, item => item.Parent);
        if (path.Count == 0)
        {
            IconPath.Visibility = Visibility.Collapsed;
            return;
        }

        IconPath.Visibility = Visibility.Visible;
        IconPath.Children.Add(PathBack());
        IconPath.Children.Add(PathStep(Loc.Get("Playlists_Title"), null));

        for (var i = 0; i < path.Count; i++)
        {
            IconPath.Children.Add(PathArrow());

            // The folder you are in is where you are, not somewhere to go. It uses the same box as the
            // steps beside it (see PathHere), and it is not a button to press.
            IconPath.Children.Add(i == path.Count - 1
                ? PathHere(path[i].DisplayName)
                : PathStep(path[i].DisplayName, path[i]));
        }
    }

    /// <summary>The small button that the whole path is built from. It is the app's own dense button, the
    /// one that the dialogs put on a field row. So a way back out of a folder looks like something to
    /// press, and not like a word that looks like a link.</summary>
    private static Style PathButtonStyle => (Style)Application.Current.Resources["DenseButtonStyle"];

    /// <summary>The widest that a folder's name is set before it is trimmed. It is the same for every step.
    /// The path is one line. A step that could grow wider than the ones beside it would push the way back
    /// out off its own edge.</summary>
    private const double StepWidest = 160;

    /// <summary>The way out of the folder, at the head of the path. It goes one step up, never all the way
    /// to the top. The top is the first step of the path beside it, and it is a press of its own.</summary>
    private Button PathBack()
    {
        var button = new Button
        {
            Style = PathButtonStyle,
            // Tighter than the style's own padding. This button holds a chevron, not a word. The room that
            // a word needs on either side would look like an empty box around an arrow.
            Padding = new Thickness(8, 0, 8, 0),
            Content = new FontIcon
            {
                Style = (Style)Application.Current.Resources["FaIcon"],
                Glyph = "\uf053",   // chevron-left
                FontSize = 11,
            },
        };
        button.Click += (_, _) => OpenFolder(_openFolder?.Parent);
        IconName.Set(button, Loc.Get("Album_Back"));
        return button;
    }

    /// <summary>One folder on the way here, and the way back to it. Null is the top of the tree.</summary>
    private Button PathStep(string name, PlaylistTreeItem? folder)
    {
        var button = new Button
        {
            Style = PathButtonStyle,
            Content = new TextBlock
            {
                Text = name,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = StepWidest,
            },
        };
        button.Click += (_, _) => OpenFolder(folder);
        ToolTipService.SetToolTip(button, name);
        return button;
    }

    /// <summary>The folder the grid is in: the last step of the line. It uses the same button as every
    /// step before it, so that one folder has one width wherever it stands in the path. A name set as bare
    /// text would be the same name in a narrower box, and the path would change shape as you walked into
    /// it.
    ///
    /// <para>It does not answer a press, and it does not light up under the pointer. It is where you
    /// already are. A way to where you already are is not a way anywhere.</para>
    /// </summary>
    private static Button PathHere(string name)
    {
        var button = new Button
        {
            Style = PathButtonStyle,
            IsHitTestVisible = false,
            IsTabStop = false,
            Content = new TextBlock
            {
                Text = name,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = StepWidest,
            },
        };
        ToolTipService.SetToolTip(button, name);
        return button;
    }

    private static FontIcon PathArrow() => new()
    {
        Style = (Style)Application.Current.Resources["FaIcon"],
        Glyph = "\uf054",   // chevron-right
        FontSize = 8,
        Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
        VerticalAlignment = VerticalAlignment.Center,
    };

    // ── the tiles ──

    /// <summary>What a tile does when it is pressed. A folder opens. Everything else drills into its
    /// tracks. This is the same drill that the list's own rows make, so the two views arrive at the same
    /// page.
    /// </summary>
    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PlaylistTreeItem item)
            return;

        if (PlaylistIcons.OpensAsFolder(item.Kind))
            OpenFolder(item);
        else
            ShowDetail(item, PlaylistFrom.Elsewhere);   // a tile in this view, so back is this view
    }

    /// <summary>A tile element has been realised onto a row. The element can be new, or it can be recycled
    /// off another row.
    ///
    /// <para>Two things happen here, and both must happen on every realise.</para>
    ///
    /// <para>The element is given the row as its DataContext. <see cref="Tile_Click"/> and the context menu
    /// read the row from the element that they were raised on. A repeater whose ItemTemplate uses x:Bind
    /// does not set a DataContext on WinUI. Instead, it processes the compiled bindings, and the element
    /// keeps the DataContext it inherits, which is the pane's model. So the pictures and names were drawn,
    /// but nothing answered a press. Uno sets the DataContext, which is why the same template works
    /// there.</para>
    ///
    /// <para>The row's picture is also requested (<see cref="PlaylistTreeItem.EnsureTileLoaded"/>). This
    /// happens on the realise, and not on the element's DataContextChanged. The genre wall's rows work the
    /// same way. A repeater that hands a recycled element back to the SAME row rebinds nothing. So nobody
    /// would ever ask for the picture.</para>
    /// </summary>
    private void Tile_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Index < 0 || args.Index >= _tiles.Count || args.Element is not FrameworkElement element)
            return;

        var item = _tiles[args.Index];
        element.DataContext = item;
        item.EnsureTileLoaded(TileDecodeEdge);
    }

    /// <summary>The pixels that a tile's square is decoded at: the size it is drawn at right now, at this
    /// display's scale. It is asked for each tile, and not worked out once. A window dragged to a second
    /// screen gives a different answer, and nothing announces that. Also, the square moves while the slider
    /// moves. So a tile realised at the small end of the slider and one realised at the big end need
    /// different pictures.</summary>
    private int TileDecodeEdge =>
        ArtScaler.DecodeEdgeFor(_tileMetrics.Art, XamlRoot?.RasterizationScale ?? 1);

    // ══ how big a tile is ══
    //
    // The album wall's arithmetic, asked about this scroller. AlbumGridLayout.ColumnsForTarget picks the
    // nearest whole number of columns for the size the reader asked for. StrideFor shares the width out
    // between them, so the row fills edge to edge, with no dead gutter. Nothing here is copied. Both halves
    // belong to the wall, so the two grids cannot drift apart.
    //
    // This method is the same as AlbumWallView.RecomputeLayout, except that the wall also has a band to
    // re-point afterwards.

    /// <summary>The one live measurement that every tile on the grid binds to. There is one instance for
    /// the life of the view. The tiles hold it. The view writes its numbers into it, and the numbers are
    /// what change.</summary>
    private readonly Library.Models.AlbumTileMetrics _tileMetrics = new();

    /// <summary>The art edge the reader has asked for, which is the slider's value. What is DRAWN is this
    /// value snapped to a whole number of columns. See <see cref="FitTiles"/>.</summary>
    private double _tileTargetArt = 150;

    /// <summary>The scroller's last measured width. It is kept, because the slider can move without a
    /// resize, and a resize can happen without the slider. Each of those needs the other's
    /// number.</summary>
    private double _tilesWidth;

    /// <summary>Set while the slider is being seeded from the setting. So a restore is not written back as
    /// a choice.</summary>
    private bool _seedingTileSize;

    /// <summary>The chrome a tile wears around its square: the button's 8 of padding on each side. That
    /// padding is the gutter between covers. It is the album wall's own number, for the same reason. The
    /// stride is the art plus this value, so the square fills the padding box exactly.</summary>
    private const double TileChrome = 16;

    /// <summary>What a tile spends under its square: seven of air, the caption's room, and the ten that the
    /// button pads its foot with. The caption's 38 is the wall's two lines: 16, then 7, then 15. The tile
    /// keeps that room whether the name uses one line or two. That is what holds each row on one
    /// line.</summary>
    private const double TileCaption = 7 + 38 + 10;

    /// <summary>The scroller's left and right padding together. It is taken off before the columns are
    /// counted, so the last tile in a row is not pushed under the scrollbar.</summary>
    private const double TilesScrollerPadding = 32;

    private void TilesScroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0)
            return;

        _tilesWidth = e.NewSize.Width;
        FitTiles();
    }

    private void TileSize_ValueChanged(
        object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        // Applied straight from the input, as the wall applies its own. The reflow is a layout property
        // that is written twice. Running it from a timer tick instead would land it outside the input
        // cycle, and that flickers.
        _tileTargetArt = e.NewValue;
        FitTiles();

        if (!_seedingTileSize)
            PersistTileSizeDebounced();
    }

    /// <summary>Turns the scroller's width and the reader's target size into the grid's realised layout:
    /// the nearest whole column count, the stride that fills the row exactly, and the square inside it.
    ///
    /// <para>The stride goes onto the layout as its minimum item width. The tiles are laid out at exactly
    /// that width, because each one gets the same width from its binding. That is why the layout's
    /// ItemsStretch is None. The row is already full, and a stretch on top of it would widen the tiles past
    /// the stride that the columns were counted for.</para>
    /// </summary>
    private void FitTiles()
    {
        if (_tilesWidth <= 0)
            return;   // not measured yet: the tiles keep the opening size, rather than one worked out from
                      // nothing

        var available = _tilesWidth - TilesScrollerPadding;
        var columns = RPM.Core.Library.AlbumGridLayout.ColumnsForTarget(
            available, _tileTargetArt + TileChrome);
        var stride = RPM.Core.Library.AlbumGridLayout.StrideFor(
            available, columns, Library.AlbumWallView.MaxArt + TileChrome);

        _tileMetrics.Set(stride, stride - TileChrome);

        TilesLayout.MinItemWidth = stride;
        TilesLayout.MinItemHeight = stride - TileChrome + TileCaption;
    }

    private DispatcherQueueTimer? _tileSizeTimer;

    private void PersistTileSizeDebounced()
    {
        // A drag fires ValueChanged for each pixel. The grid follows live, and the setting is written once
        // the reader settles. So settings.json is not rewritten on every tick of the drag. This is the
        // wall's own rule, and it uses the same 400 ms.
        _tileSizeTimer ??= CreateTileSizeTimer();
        _tileSizeTimer.Stop();
        _tileSizeTimer.Start();
    }

    private DispatcherQueueTimer CreateTileSizeTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(400);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            App.Services.GetRequiredService<SettingsService>().PlaylistTileSize = _tileTargetArt;
        };
        return timer;
    }
}
