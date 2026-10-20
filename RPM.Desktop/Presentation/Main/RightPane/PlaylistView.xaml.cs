using RPM.Core.Library;
using RPM.Desktop.Presentation.RPaMp;
using RPM.Desktop.Presentation.Shell;
using RPM.Desktop.Services;
using RPM.Desktop.Services.Platform;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using RPM.Core.Diagnostics;
using Windows.Foundation;

namespace RPM.Desktop.Presentation.Main.RightPane;

/// <summary>
/// The playlists panel: the user's folder/playlist tree as a flattened, expandable list, with a toolbar
/// to add a playlist or folder and a per-row menu to play, add to Now playing, rename, delete, or remove
/// an item. Playing feeds RPaMp. The model does the work; this is the surface.
/// </summary>
public sealed partial class PlaylistView : UserControl
{
    public PlaylistView()
    {
        InitializeComponent();

        // `handledEventsToo` is true because a ListViewItem marks its own press as handled before
        // this handler would run. Without it the handler is never called.
        Tree.AddHandler(PointerPressedEvent, new PointerEventHandler(Tree_PointerPressed), true);
        Tree.AddHandler(PointerMovedEvent, new PointerEventHandler(Tree_PointerMoved), true);
        Tree.AddHandler(PointerReleasedEvent, new PointerEventHandler(Tree_PointerReleased), true);
        Tree.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(Tree_PointerCaptureLost), true);

        // The handler is on the list, not on the row template, because the template does not cover the
        // whole row. The container's ContentPresenter is inset 22 on each side, and its child is 56 tall in
        // a row of 64. A press near either end or along the top or bottom edge hits the Hover border
        // instead. That border is a sibling of the presenter, so a Tapped event raised there never passes
        // through the template. The row is found from whatever the pointer hit (`TrackRowUnder`), so every
        // point of the row responds.
        DetailList.AddHandler(TappedEvent, new TappedEventHandler(Track_Tapped), true);
        DetailList.AddHandler(ContextRequestedEvent,
            new TypedEventHandler<UIElement, ContextRequestedEventArgs>(Track_ContextRequested), true);

        DataContextChanged += (_, _) => HookModel();
        Loaded += (_, _) =>
        {
            AttachIcons();

            // Active only when the environment names this list. See `PlaylistView.Diag`, and `ListDiag.Which`
            // for why it is a name and not a switch.
            WatchTree();
        };
        Unloaded += (_, _) =>
        {
            if (_pulseModel is { } model)
            {
                model.CloudStateChanged -= SyncBadgePulse;
                model.DetailInvalidated -= TreeChanged;
            }
            _pulseModel = null;
            StopBadgePulse();
        };
    }

    private PlaylistModel? Model => DataContext as PlaylistModel;

    // ── badge pulse while a playlist is uploading or downloading ──

    private PlaylistModel? _pulseModel;
    private bool _pulsing;

    /// <summary>
    /// Attaches to the model that this pane is given. It subscribes to the badge pulse and to the
    /// report that the tree changed. The icon grid needs that report because its tiles are a folder's
    /// children (see <see cref="RebuildTiles"/>). The list does not need it: it is bound to the model's
    /// own collection and follows it by itself.
    /// </summary>
    private void HookModel()
    {
        if (ReferenceEquals(_pulseModel, Model))
            return;

        if (_pulseModel is { } previous)
        {
            previous.CloudStateChanged -= SyncBadgePulse;
            previous.DetailInvalidated -= TreeChanged;
        }

        _pulseModel = Model;
        if (_pulseModel is { } model)
        {
            model.CloudStateChanged += SyncBadgePulse;
            model.DetailInvalidated += TreeChanged;
        }

        SyncBadgePulse();
        TreeChanged();
    }

    /// <summary>The tree changed. Two things follow: the grid's tiles, which are a folder's children,
    /// and the card for lists that are not available yet (its sizes were read by the same pass that
    /// raised this event).</summary>
    private void TreeChanged()
    {
        RebuildTiles();
        ShowLockedLists();
        ReflectReading(_onView);
    }

    /// <summary>Whether the pane is on screen, as the page last reported (see <see cref="ReflectReading"/>).</summary>
    private bool _onView;

    /// <summary>
    /// Shows the tree's loader while the generated lists have not been read from the open collection
    /// (<see cref="PlaylistModel.GeneratedListsRead"/>), and only while the tree is on screen. The page
    /// calls this when it shows or hides screens. It is also called here whenever the tree changes.
    /// </summary>
    /// <param name="onView">Whether the pane is on screen.</param>
    public void ReflectReading(bool onView)
    {
        _onView = onView;
        ListsLoading.Reflect(onView && !_inDetail && _pulseModel is { GeneratedListsRead: false });
    }

    /// <summary>
    /// Pulses the badges only while some playlist is busy. Joining and leaving <see cref="BadgeBreath"/>
    /// is the only reliable way to stop: under Uno a looping storyboard keeps running for an element
    /// that stays in the tree, and stopping one resets Opacity to its base value for one frame.
    /// <see cref="BadgeBreath"/> is also the clock of the album tiles, so a row and the tile for the
    /// same album stay in step.
    /// </summary>
    private void SyncBadgePulse()
    {
        var wanted = _pulseModel?.Visible.Any(i => i.IsCloudBusy) == true;
        if (wanted == _pulsing)
            return;

        if (wanted)
        {
            _pulsing = true;
            BadgeBreath.Join(OnBadgePulseTick);
        }
        else
        {
            StopBadgePulse();
        }
    }

    private void StopBadgePulse()
    {
        if (!_pulsing)
            return;
        _pulsing = false;
        BadgeBreath.Leave(OnBadgePulseTick);

        if (_pulseModel is { } model)
            foreach (var item in model.Visible)
                item.CloudPulse = RPM.Core.Cloud.CloudBadgePulse.Rest;
    }

    private void OnBadgePulseTick(double opacity)
    {
        if (_pulseModel is not { } model)
            return;

        foreach (var item in model.Visible)
            if (item.IsCloudBusy)
                item.CloudPulse = opacity;
    }

    // ── toolbar ──

    private async void NewPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } m) return;
        var name = await PromptNameAsync(Loc.Get("Playlists_NewPlaylist"), Loc.Get("Playlists_DefaultPlaylist"));
        if (name is not null)
            m.NewPlaylist(SelectedFolderId(), name);
    }

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } m) return;
        var name = await PromptNameAsync(Loc.Get("Playlists_NewFolder"), Loc.Get("Playlists_DefaultFolder"));
        if (name is not null)
            m.NewFolder(SelectedFolderId(), name);
    }

    private async void NewMoodPlaylist_Click(object sender, RoutedEventArgs e) => await MoodPlaylistAsync(null);

    /// <summary>Opens the mood playlist window: for a new list when <paramref name="item"/> is null,
    /// otherwise for an existing list with its recipe. The window only reads. This method writes the list
    /// after the window closes.</summary>
    private async Task MoodPlaylistAsync(PlaylistTreeItem? item)
    {
        if (Model is not { } m || m.MoodLibrary is not { } db || XamlRoot is null)
            return;
        var current = item is null ? null : m.MoodRecipeOf(item);
        if (item is not null && current is null)
            return;
        var picture = item is not null && current is { Cover.Source: MoodCoverSource.Picture }
            ? await m.MoodPictureOfAsync(item)
            : null;
        var dialog = new Dialogs.MoodPlaylistDialog(db, item?.Name ?? Loc.Get("MoodMaker_DefaultName"), current, picture)
        {
            XamlRoot = XamlRoot,
        };
        await DialogHost.ShowAsync(dialog);
        if (dialog.Chosen is not { } chosen)
            return;
        if (item is null)
            await m.CreateMoodPlaylistAsync(chosen.Name, chosen.Recipe, chosen.Cover, chosen.Under);
        else
            await m.RefillMoodPlaylistAsync(item, chosen.Name, chosen.Recipe, chosen.Cover, chosen.Under);
    }

    /// <summary>
    /// Where a new playlist or folder is created: under the selected folder, or at the root when
    /// nothing (or a non-folder) is selected.
    ///
    /// <para>In the icon view there is no selection to read. The grid shows the contents of one folder,
    /// so new items are created in that folder, as in a file browser. The folder of generated lists has
    /// no stored node, so nothing can be created inside it.</para>
    /// </summary>
    private long? SelectedFolderId() =>
        _viewMode == PlaylistViewMode.Icons
            ? _openFolder is { Kind: PlaylistRowKind.Folder, NodeId: { } open } ? open : null
            : Tree.SelectedItem is PlaylistTreeItem { IsFolder: true, NodeId: { } id } ? id : null;

    // ── rows ──

    private void Chevron_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is PlaylistTreeItem item)
            item.ToggleExpand();
    }

    /// <summary>A row's container was realized for an item. The container may be new or recycled onto
    /// a different folder. `DataContextChanged` fires in both cases, but `Loaded` does not reliably fire
    /// for a pooled container. This only starts loading a folder's picture. For every other kind of row,
    /// EnsureCoverLoaded does nothing.</summary>
    private void Row_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (args.NewValue is PlaylistTreeItem item)
            item.EnsureCoverLoaded();
    }

    private void Row_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PlaylistTreeItem item || Model is null)
            return;

        // The app's own folders have no menu: there is no stored node to rename, move or delete, and they
        // play nothing themselves. An empty menu is worse than no menu. The same rule applies on a
        // borrowed shelf.
        if (item.IsAppFolder)
        {
            e.Handled = true;
            return;
        }

        var flyout = BuildMenu(item);
        if (e.TryGetPosition(sender, out var pos))
            flyout.ShowAt(sender, new FlyoutShowOptions { Position = pos });
        else
            flyout.ShowAt((FrameworkElement)sender);
        e.Handled = true;
    }

    private MenuFlyout BuildMenu(PlaylistTreeItem item)
    {
        var m = Model!;
        var flyout = new MenuFlyout();

        // Now playing is the queue itself. Adding it to the queue would be circular, so it only plays.
        if (item.Kind == PlaylistRowKind.NowPlaying)
        {
            flyout.Items.Add(Item(Loc.Get("Playlists_Play"), GlyphPlay, () => m.Play(item)));
            return flyout;
        }

        // Every kind except a folder plays itself. A folder plays everything under it.
        flyout.Items.Add(Item(Loc.Get("Playlists_Play"), GlyphPlay, () => m.Play(item)));
        flyout.Items.Add(Item(Loc.Get("Playlists_AddToNowPlaying"), GlyphAddQueue, () => m.AddToNowPlaying(item)));

        // A borrowed tree can only play. The other items in this menu write rows (rename, delete, cloud).
        // Those rows do not belong to this computer: they are in the mirror of another computer, and a
        // change made here would be overwritten the next time that computer syncs.
        if (m.IsBorrowed)
            return flyout;

        // A generated list can only play. There is no stored playlist to rename, move or delete. The
        // library computes the list, so its content changes only when the underlying data changes (for
        // example the hearts on the songs).
        if (item.Kind == PlaylistRowKind.Smart)
            return flyout;

        // Cloud: a playlist can be added, which uploads its tracks and stores their order, or it can be
        // removed. A folder cannot, because it only arranges playlists and plays nothing itself.
        if (item.CanAddToCloud || item.CanRemoveFromCloud)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(item.CanAddToCloud
                // Disabled while the store is full: adding a playlist uploads its tracks, and there is no space
                // for them. Removing stays enabled because it frees space. The record and song menus follow the
                // same rule; see MenuCloudEntries.
                ? Item(Loc.Get("Playlists_AddToCloud"), GlyphCloudUp, () => m.AddToCloud(item),
                       enabled: App.Services.GetRequiredService<CloudSyncService>().CanUpload)
                : Item(Loc.Get("Playlists_RemoveFromCloud"), GlyphCloudOff, () => m.RemoveFromCloud(item)));
        }

        if (item.IsFolder || item.IsPlaylist)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            if (item.IsFolder)
            {
                flyout.Items.Add(Item(Loc.Get("Playlists_NewPlaylist"), GlyphNewPlaylist,
                    async () => { var n = await PromptNameAsync(Loc.Get("Playlists_NewPlaylist"), Loc.Get("Playlists_DefaultPlaylist")); if (n is not null) m.NewPlaylist(item.NodeId, n); }));
                flyout.Items.Add(Item(Loc.Get("Playlists_NewFolder"), GlyphNewFolder,
                    async () => { var n = await PromptNameAsync(Loc.Get("Playlists_NewFolder"), Loc.Get("Playlists_DefaultFolder")); if (n is not null) m.NewFolder(item.NodeId, n); }));
            }
            // A list made in the mood window is renamed and given its cover there. Its name is drawn on its
            // cover frame, so a rename here would leave the old name on the cover, and the cover window would
            // put an unframed picture on it. So only the mood window is offered.
            if (item.IsPlaylist && m.MoodRecipeOf(item) is not null)
            {
                flyout.Items.Add(Item(Loc.Get("MoodMaker_Edit"), GlyphMood, async () => await MoodPlaylistAsync(item)));
            }
            else
            {
                flyout.Items.Add(Item(Loc.Get("Playlists_Rename"), GlyphRename,
                    async () => { var n = await PromptNameAsync(Loc.Get("Playlists_Rename"), item.Name); if (n is not null) m.Rename(item, n); }));
                // Next to Rename because both change how the row looks. Now playing returned above, so the
                // picker is never offered for the one node whose write the database refuses.
                flyout.Items.Add(Item(Loc.Get("Playlists_ChangeCover"), GlyphCover,
                    async () => await ChangeCoverAsync(item)));
            }
            flyout.Items.Add(Item(Loc.Get("Playlists_Delete"), GlyphDelete, () => m.Delete(item)));
        }
        else
        {
            // A song or a nested-playlist reference: it can be taken out of its playlist.
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(Item(Loc.Get("Playlists_Remove"), GlyphRemove, () => m.Delete(item)));
        }

        return flyout;
    }

    // Font Awesome glyphs for the row menus. They are written as escapes because a raw private-use
    // code point does not survive every editor and toolchain between the source and the font.
    private const string GlyphPlay = "\uf04b";          // play
    private const string GlyphAddQueue = "\uf067";      // plus
    private const string GlyphNewPlaylist = "\uf8c9";   // list-music
    private const string GlyphNewFolder = "\uf65e";     // folder-plus
    private const string GlyphRename = "\uf304";        // pencil
    private const string GlyphMood = "\uf0d0";          // wand, same glyph as the mood playlist window
    private const string GlyphDelete = "\uf2ed";        // trash
    private const string GlyphRemove = "\uf068";        // minus
    private const string GlyphCloudUp = "\uf0ee";       // cloud-upload, same glyph as the album wall
    private const string GlyphCloudOff = "\uf381";      // cloud-slash
    private const string GlyphDj = "\uf025";            // headphones, same glyph as the auto-mix button
    private const string GlyphMixtape = "\uf8ab";       // cassette tape, same glyph as Start mixtape in the song menus
    private const string GlyphCover = "\uf03e";         // image, same glyph as the other art pickers
    private const string GlyphRefuseArtist = "\uf506";  // user-slash, for an action that a list is told to stop offering
    private const string GlyphRefuseAlbum = "\uf8d9";   // record-vinyl, used for the album half of the same pair

    /// <summary>A menu row. Every item has a glyph, drawn through FaMenuIcon (Generic.xaml), which sizes
    /// it at 16×16. Uno lays out a MenuFlyoutItem's icon at whatever size the glyph measures, so icons
    /// without a fixed size differ in size and indent from row to row.</summary>
    private static MenuFlyoutItem Item(string text, string glyph, Action onClick, bool enabled = true)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            FontSize = 12,
            MinHeight = 30,
            IsEnabled = enabled,
            Icon = new FontIcon
            {
                Glyph = glyph,
                Style = (Style)Application.Current.Resources["FaMenuIcon"],
            },
        };
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>
    /// Opens the picker and, if the user chose something, renders it once and writes both parts: the
    /// picture, which every surface reads, and the note that says how it was made.
    ///
    /// <para>The render happens here and not in the dialog, because this is the only place that knows
    /// the choice was accepted. A dialog that wrote while previewing would leave the last tile that the
    /// user hovered over as the playlist's cover when they pressed Close.</para>
    ///
    /// <para>The dialog receives the playlist's existing master picture, which the model reads off the
    /// UI thread before the window opens (<see cref="PlaylistModel.AskCoverAsync"/>). This is required:
    /// the dialog's five-argument constructor exists to prevent data loss. Without the picture, opening
    /// the picker on a Picture choice and pressing Save without any change would convert the playlist
    /// to the automatic cover, because the dialog would have no picture of the user's to return.</para>
    /// </summary>
    private async Task ChangeCoverAsync(PlaylistTreeItem item)
    {
        if (Model is not { } model || item.NodeId is not { } id)
            return;

        if (await model.AskCoverAsync(item) is not { } ask)
            return;

        var dialog = new PlaylistCoverDialog(id, item.Name, item.IsFolder, ask.Current, ask.CurrentPicture)
        {
            XamlRoot = XamlRoot,
        };
        await DialogHost.ShowAsync(dialog);

        if (dialog.Chosen is not { } chosen)
            return;   // Closed without a choice: nothing is written.

        await model.SetCoverAsync(item, chosen, dialog.Rendered);
    }

    // ── playlist detail (drill-in: the track list replaces the tree in the pane) ──

    /// <summary>Raised when the pane opens a playlist (true) or returns to the tree (false), so the host
    /// can give the track list the width it needs. See PaneConductor.SetDrilled.</summary>
    public event Action<bool>? DetailModeChanged;

    /// <summary>Raised when the user goes back out of a tracklist that was opened from the home screen.
    /// The view does not show home itself: home covers both panes, and only the page that holds them can
    /// show it. See <see cref="PlaylistFrom.HomeScreen"/>.</summary>
    public event Action? HomeRequested;

    /// <summary>
    /// Where a tracklist was opened from, and so where going back leads.
    ///
    /// <para>This is a type and not a flag, as MainPage's AlbumFrom is: every way into the detail must
    /// state its origin, so a new way in cannot leave a user who came from home on the playlist tree.
    /// Home is the only origin with its own back target. Everywhere else, including a genre's page and
    /// an artist's page, back leads to the tree.</para>
    /// </summary>
    private enum PlaylistFrom
    {
        /// <summary>The tree itself, a genre's page, an artist's page: back leads to the tree.</summary>
        Elsewhere,

        /// <summary>One of the home screen's generated lists. Back leads to home.</summary>
        HomeScreen,
    }

    /// <summary>Where the open tracklist was opened from. Cleared when the detail closes, so it always
    /// describes what is on screen.</summary>
    private PlaylistFrom _detailFrom;

    private PlaylistTreeItem? _detailItem;
    private IReadOnlyList<PlaylistTrackRow> _detailRows = Array.Empty<PlaylistTrackRow>();
    private bool _inDetail;
    private bool _detailIsQueue;
    private long? _detailPlaylistId;

    // ── dragging a row to another place in the tree ──
    //
    // The row follows the pointer as it does in the queue lists, but the tree does not re-form around
    // it. Carried reorder assumes a flat list of peers: the rows move aside because every slot between
    // them is a place the carried row could go. This list is a flattened tree (folders, playlists and the
    // pinned Now playing row, at different depths). Most slots are not a valid place for most rows, and
    // "everything below moves up one" has no meaning across a change of depth. A tree that re-formed
    // would show orders it cannot hold during the whole drag.
    //
    // So the carried row moves and the destination is marked instead: a line in the gap where it would be
    // dropped, or the folder highlighted when it would go inside. TreeDrop decides which, and it has tests.

    private PlaylistTreeItem? _treePressed;
    private PlaylistTreeItem? _treeCarried;
    private PlaylistTreeItem? _treeTarget;
    private TreeDropWhere _treeWhere;
    private bool _treeDragging;
    private double _treeStartY;
    private double _treeRowHeight;
    private ScrollViewer? _treeScroller;

    /// <summary>How far the pointer must move before a press on a row becomes a drag and not the click
    /// that opens the playlist.</summary>
    private const double TreeDragSlop = 5.0;

    private void Tree_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_treeDragging)
            return;

        // A right-click only asks for the menu. If it were handled like another press, the release would
        // open the playlist and the menu would appear over the list that was just left. Touch reports
        // itself as the left button, so a tap still works.
        var point = e.GetCurrentPoint(Tree);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _treePressed = null;
            _treeCarried = null;
            return;
        }

        // The chevron is a separate control with its own gesture: it opens a folder and says nothing
        // about where the folder should be filed. A press that started on it is neither a drag nor a click
        // to open the row.
        var source = e.OriginalSource as DependencyObject;
        _treePressed = HitAButton(source) ? null : RowUnder(source);

        // Nothing is carried out of a borrowed tree. The drop would be refused where it is written
        // (PlaylistModel.Mine) and the row would slide back, which looks like a failed gesture instead of
        // one that was never offered.
        _treeCarried = Model is { IsBorrowed: false } && _treePressed is { CanCarry: true } row ? row : null;
        _treeStartY = point.Position.Y + TreeOffset;
    }

    /// <summary>
    /// Whether the press landed on a control with its own action, on the way up to the row.
    ///
    /// <para>The walk stops at the row's container, because that is where the row's own controls end.
    /// It cannot stop at the first element whose DataContext is the item: a DataContext is inherited, so
    /// every element in the template has it, including the chevron's glyph. The walk would end at the
    /// first element, report "not a button" for the chevron, and the press would count as a press on
    /// the row. Opening a folder would then also open it in detail.</para>
    /// </summary>
    private static bool HitAButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase)
                return true;
            if (source is Microsoft.UI.Xaml.Controls.Primitives.SelectorItem)
                return false;

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void Tree_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_treeCarried is null)
            return;

        var y = e.GetCurrentPoint(Tree).Position.Y + TreeOffset;

        if (!_treeDragging)
        {
            if (Math.Abs(y - _treeStartY) < TreeDragSlop)
                return;

            _treeRowHeight = TreeRowHeight();
            if (_treeRowHeight <= 0)
            {
                _treeCarried = null;
                return;
            }

            _treeDragging = true;
            Tree.CapturePointer(e.Pointer);
        }

        _treeCarried.Drop.Y = y - _treeStartY;
        AimTreeDrop(y);
        e.Handled = true;
    }

    private void Tree_PointerReleased(object sender, PointerRoutedEventArgs e) => DropTreeRow();

    private void Tree_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => DropTreeRow();

    /// <summary>
    /// Works out what the pointer is asking for and shows it on the screen: one of the two marks, never
    /// both, and neither where the tree cannot make the drop.
    /// </summary>
    private void AimTreeDrop(double y)
    {
        if (Model is not { } model || _treeCarried is null)
            return;

        var rows = model.Visible;
        var at = Math.Clamp((int)Math.Floor(y / _treeRowHeight), 0, Math.Max(0, rows.Count - 1));
        if (at >= rows.Count)
        {
            MarkTreeDrop(null, TreeDropWhere.After);
            return;
        }

        var target = rows[at];
        var where = TreeDrop.Where(y - (at * _treeRowHeight), _treeRowHeight, target.CanTakeChildren);

        // A row cannot be filed beside or inside itself, and a folder cannot go into its own branch.
        // This is checked while the drag runs, so no mark appears where the drop would be refused. A line
        // drawn where nothing can land would promise something that the release then breaks.
        if (_treeCarried.Holds(target) || (where != TreeDropWhere.Into && _treeCarried.Holds(target.Parent ?? target)))
        {
            MarkTreeDrop(null, where);
            return;
        }

        MarkTreeDrop(target, where);
    }

    private void MarkTreeDrop(PlaylistTreeItem? target, TreeDropWhere where)
    {
        _treeTarget = target;
        _treeWhere = where;

        if (_treeTarget is { } into && where == TreeDropWhere.Into)
        {
            SetDropInto(into);
            TreeDropLine.Visibility = Visibility.Collapsed;
            return;
        }

        SetDropInto(null);

        if (_treeTarget is not { } beside || Model is not { } model)
        {
            TreeDropLine.Visibility = Visibility.Collapsed;
            return;
        }

        var at = model.Visible.IndexOf(beside);
        if (at < 0)
        {
            TreeDropLine.Visibility = Visibility.Collapsed;
            return;
        }

        // The line is in the gap between rows, not on a row. It sits on the boundary that the drop asks
        // for, and it moves in with the depth the row would land at, so that "into this folder's list" and
        // "beside the folder" look different.
        var line = (at + (where == TreeDropWhere.After ? 1 : 0)) * _treeRowHeight;
        TreeDropLineShift.Y = line - TreeOffset - 1;
        // 16 is where a row's own content starts: the list's 10 of padding plus the row template's 6.
        // This is also the column of the pane's heading. If the list's padding changes, change this.
        TreeDropLine.Margin = new Thickness(16 + (beside.Depth * 16), 0, 16, 0);
        TreeDropLine.Visibility = Visibility.Visible;
    }

    /// <summary>The folder that is highlighted. Only one folder is highlighted at a time.</summary>
    private void SetDropInto(PlaylistTreeItem? item)
    {
        if (Model is not { } model)
            return;

        foreach (var row in model.Visible)
            row.IsDropInto = ReferenceEquals(row, item);
    }

    /// <summary>
    /// The pointer came up. The row returns to its own line in every case. When the drop was valid, the
    /// tree is rebuilt from the database a moment later and the row is drawn in its new place. When it
    /// was not valid, putting the row back is the result.
    /// </summary>
    private void DropTreeRow()
    {
        var carried = _treeCarried;
        var pressed = _treePressed;
        var target = _treeTarget;
        var where = _treeWhere;
        var dragged = _treeDragging;

        _treeCarried = null;
        _treePressed = null;
        _treeTarget = null;
        _treeDragging = false;

        if (carried is not null)
            carried.Drop.Y = 0;

        SetDropInto(null);
        TreeDropLine.Visibility = Visibility.Collapsed;
        Tree.ReleasePointerCaptures();

        // A press that did not move is a click, and a click on a row opens it.
        if (!dragged)
        {
            // Except the app's own folders, which hold lists and not songs: there is nothing to open in
            // detail, so a click on one does what its chevron does. This applies on both shelves; see
            // PlaylistTreeItem.IsAppFolder.
            if (pressed is { IsAppFolder: true })
                pressed.ToggleExpand();
            else if (pressed is not null)
                ShowDetail(pressed, PlaylistFrom.Elsewhere);
            return;
        }

        if (carried is null || target is null || Model is not { } model)
            return;

        model.DropNode(carried, target, where);
    }

    private double TreeOffset => TreeScroller?.VerticalOffset ?? 0;

    private ScrollViewer? TreeScroller => _treeScroller ??= FindScroller(Tree);

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer found)
                return found;
            if (FindScroller(child) is { } nested)
                return nested;
        }

        return null;
    }

    /// <summary>The height of a tree row, read from any realized container. All rows have the same
    /// height, so the rest of this code can use arithmetic and does not need to walk down the list.</summary>
    private double TreeRowHeight()
    {
        for (var i = 0; i < (Model?.Visible.Count ?? 0); i++)
            if (Tree.ContainerFromIndex(i) is FrameworkElement item && item.ActualHeight > 0)
                return item.ActualHeight;

        return 0;
    }

    private static PlaylistTreeItem? RowUnder(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: PlaylistTreeItem row })
                return row;

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    // ── dragging a track to a new place in the list ──
    //
    // This uses the same carried-row reorder as the deck's panel and the Pegasus drawer; see
    // CarriedReorder. Only the place where the new order is written differs: a stored playlist's order
    // is rewritten in the library database, and the Now playing list's order goes to the live queue.

    private CarriedReorder<PlaylistTrackRow>? _detailReorder;
    private List<PlaylistTrackRow> _detailCarried = new();

    /// <summary>
    /// Points the reorder at the list that is now open, or refuses it for lists that cannot be
    /// reordered: a folder's tracks and anything flattened through a nested playlist. Those are a view
    /// of several lists and not a list, and there is no single stored order for a drag to rewrite. A row
    /// moved there would have to be moved in the playlist it really came from, which is not what the
    /// user sees.
    /// </summary>
    private void AttachDetailReorder(PlaylistDetail detail)
    {
        _detailReorder?.Cancel();
        _detailCarried = detail.Tracks as List<PlaylistTrackRow> ?? detail.Tracks.ToList();

        if (detail.PlaylistId is null && !detail.IsQueue)
        {
            _detailReorder = null;
            return;
        }

        _detailReorder = new CarriedReorder<PlaylistTrackRow>(
            DetailList, _detailCarried,
            exchange: (a, b) =>
            {
                var carried = Snapshot(_detailCarried[a]);
                _detailCarried[a].Show(_detailCarried[b]);
                _detailCarried[b].Show(carried);
            },
            commit: CommitDetailOrder);
    }

    /// <summary>A copy of a row's displayed values, kept while two rows exchange them. Without the copy
    /// the second row would take on what the first row has just received, and both would show the same
    /// track.</summary>
    private static PlaylistTrackRow Snapshot(PlaylistTrackRow row)
    {
        var copy = new PlaylistTrackRow();
        copy.Show(row);
        return copy;
    }

    /// <summary>
    /// The row has landed. The order on screen is now the order, so the only remaining step is to
    /// write it where it is stored.
    /// </summary>
    private void CommitDetailOrder(int from, int to)
    {
        if (_detailIsQueue)
        {
            // The queue rows show the live list, so the live list is what moves. Its own notification comes
            // back and rebuilds this list.
            App.Services.GetRequiredService<RPaMpService>().Transport.Playlist.Move(from, to);
            return;
        }

        if (_detailPlaylistId is not { } playlistId || Model is not { } model)
            return;

        var ids = _detailCarried.Select(r => r.ItemId).OfType<long>().ToList();
        if (ids.Count != _detailCarried.Count)
            return;   // a row without an id: these are not all items of one playlist

        model.ReorderDetail(playlistId, ids);
    }

    /// <summary>
    /// Selecting a row does not open it. The release does (see <see cref="DropTreeRow"/>).
    ///
    /// <para>Selection happens on the press, before the pointer has moved far enough to show whether it
    /// is a click or the start of a drag. Opening the playlist at that point would slide the whole tree
    /// away from a pointer that came down to move a playlist. This handler is empty because the
    /// selection is useful on its own: the row is highlighted and the menus read it.</para>
    /// </summary>
    private void Tree_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void ShowDetail(PlaylistTreeItem item, PlaylistFrom from)
    {
        // Read the list as it stands, not as a new one. Opening a list in detail is not a request for a
        // different list. A new reading would change the songs on every visit, and the phone would be sent
        // whatever the last visit produced.
        if (Model is not { } m || m.BuildDetail(item, ListReading.AsItStands) is not { } detail)
            return;   // not a container row: stay in the tree

        _detailFrom = from;

        // Whether a different list is arriving or the open list is being requested again. This is taken
        // before the row is stored, because the stored row is what the header is currently set up for.
        var fill = ReferenceEquals(_detailItem, item) ? DetailFill.Reread : DetailFill.Opened;

        _detailItem = item;
        Populate(detail, fill);

        if (_inDetail)
            return;   // already open (a refresh): content is replaced in place, no slide

        // Open the detail: the tracks slide in from the right and the tree slides out to the left. The
        // host widens the pane at the same time (DetailModeChanged, then the conductor's width animation).
        _inDetail = true;
        ReflectReading(_onView);
        SettleRoom();               // the room draws only while a list is open on a pane that is on screen
        Detail.Visibility = Visibility.Visible;
        Slide(Detail, DetailShift, 34, 0, 0, 1);
        Slide(TreeSide, TreeShift, 0, -34, 1, 0);
        HideAfterSlide(TreeSide, TreeShift, wantDetail: true);
        DetailModeChanged?.Invoke(true);
    }

    /// <summary>
    /// Whether a fill is a list arriving or the open list being read again.
    ///
    /// <para>The caller states it, with no default, because the header's picture depends on it and a
    /// wrong value fails silently. A re-read that cleared the header would blank the cover, the ground
    /// and the faces and read them all again. Re-reads are common: every move in the queue is one for
    /// the Now playing list, and every rebuild of the playlist tree is one for whatever list is open.
    /// See <see cref="Undress"/>.</para>
    /// </summary>
    private enum DetailFill
    {
        /// <summary>A different list. What the header shows belongs to the list being left.</summary>
        Opened,

        /// <summary>The same list, requested again: a queue move, a tree rebuild, a heart turned over. The
        /// header keeps what it has until the picture of this reading is drawn.</summary>
        Reread,
    }

    /// <summary>The list that the room on screen was made for, or null when the open list has no room
    /// yet. See <see cref="WantsRoom"/>.</summary>
    private string? _roomMadeFor;

    /// <summary>
    /// Whether this reading is the one the room is made from.
    ///
    /// <para>Normally only a list that is arriving. Repainting the room is cheap (a palette is written
    /// to a live element), but repainting it on a re-read would restart it: the pose is read from the
    /// colours and the list's name, so the header would jump back to its opening state on every queue
    /// move, tree rebuild, heart and cloud sync. These happen often.</para>
    ///
    /// <para><b>Exception: a list on a borrowed shelf often arrives empty</b> and is filled by the read
    /// that finishes a moment later. Such an opening has no artwork to take colours from, so the room
    /// would be drawn in the fallback grey for a list that then has thirty songs. So an opening with no
    /// picture does not count as having made the room, and the first later reading of the same list
    /// that has a picture makes it. There is one room per list in both cases. The code stores the list
    /// that a room was made for. That is true whenever it is read, and it does not depend on a time
    /// window.</para>
    /// </summary>
    private bool WantsRoom(DetailFill fill, string name, byte[]? cover)
    {
        if (fill == DetailFill.Opened)
        {
            // Still drawn with no picture to take colours from: a list that has no artwork anywhere must
            // still get its header.
            _roomMadeFor = cover is { Length: > 0 } ? name : null;
            return true;
        }

        if (cover is not { Length: > 0 } || string.Equals(_roomMadeFor, name, StringComparison.Ordinal))
            return false;

        _roomMadeFor = name;
        return true;
    }

    /// <summary>Fills the detail header and the track list from a built detail (no slide).</summary>
    private void Populate(PlaylistDetail detail, DetailFill fill)
    {
        _detailRows = detail.Tracks;
        _detailIsQueue = detail.IsQueue;
        _detailPlaylistId = detail.PlaylistId;
        DetailName.Text = detail.Name;
        DetailSubtitle.Text = detail.Subtitle;
        DetailList.ItemsSource = detail.Tracks;
        AttachDetailReorder(detail);

        // The mark on the heading describes the list: if one song has explicit lyrics, the list is
        // marked. It is read from the rows already in hand and not asked again from the library, because
        // the rows are the list and each row already knows.
        DetailExplicit.IsExplicit = detail.Tracks.Any(row => row.IsExplicit);

        ShowSmartActions(detail);

        // Use the face and not the id, so the owner's mood list on a borrowed shelf gets the same
        // sentence as the same list on the owner's own computer. See PlaylistTreeItem.Face.
        ShowFeatured(detail.Tracks, _detailItem?.Face);

        // The header's cover and the ground of the page are cleared first and filled afterwards. What
        // they take depends on the kind of list, and each is a read from disk.
        //
        // Only for a list that is arriving. Everything below takes several hundred milliseconds to return,
        // so on a re-read the cover, the ground and the faces would disappear and come back for a list that
        // has not changed. Lend() would also hand the window a page with no ground in the middle of it, which
        // would take away the backdrop of the whole app. What the reading below does not replace is cleared
        // by the reading itself, because only the reading knows that the new answer has nothing where the
        // old one had something. See FillDetailCoverAsync.
        if (fill == DetailFill.Opened)
            Undress();

        // Open, but nothing is shown yet. This is said before the ground is read and not after: a page
        // that reported itself closed while its picture loads would send the window back to its wallpaper
        // and off it again between two playlists. See PageBackdrop, which explains the difference between
        // the two kinds of nothing.
        //
        // It is also said on a re-read, which is harmless: the ground is unchanged, so the record is equal
        // to the one already filed and PageBackdrop raises nothing.
        Lend();

        _ = FillDetailCoverAsync(detail, _detailItem?.Kind, fill);
    }

    /// <summary>Removes everything the header shows: the square, the ground behind the page, and the
    /// room. These belong to the list being left. If they stayed while the new picture is drawn, the
    /// wrong record would be at the head of the right list.</summary>
    private void Undress()
    {
        DetailCover.Source = null;
        DetailCoverGlyph.Visibility = Visibility.Visible;
        DetailWash.Visibility = Visibility.Collapsed;
        DetailWashArt.ImageSource = null;
        DetailWeight.Visibility = Visibility.Collapsed;

        // The room is hidden with the rest, and SettleRoom takes it off the clock: a collapsed ancestor
        // does not re-measure its children, so a live room under one would keep drawing frames that nobody
        // can see. Keep both lines, in this order.
        DetailRoom.Visibility = Visibility.Collapsed;
        SettleRoom();
    }

    /// <summary>
    /// The two buttons that only a list written by the library gets: ask for another list, and keep
    /// this one.
    ///
    /// <para>Both are hidden for every other list, each for its own reason. A stored playlist is already
    /// kept, so there is nothing to save it as. The queue is not a list that came from a question. The
    /// state is set here on every fill and is not bound, because the answer depends on which list is
    /// open and there is nothing on the rows for a binding to watch.</para>
    /// </summary>
    private void ShowSmartActions(PlaylistDetail detail)
    {
        // Keeping a list means writing a playlist into the tree, and a borrowed shelf's tree is a mirror
        // of another computer. This is the same refusal that PlaylistModel.Mine makes. It is made here so
        // that the button is not offered, instead of being pressed and ignored. An empty list has nothing
        // to keep either.
        DetailSaveButton.Visibility =
            detail.SmartId is not null && detail.Tracks.Count > 0 && Model is { IsBorrowed: false }
                ? Visibility.Visible
                : Visibility.Collapsed;

        // Only where asking again would really return different songs. See
        // LibraryDatabase.SmartPlaylistCanRegenerate, which explains both conditions.
        DetailNewListButton.Visibility =
            detail.SmartId is { } id && Model is { } m && m.CanRegenerate(id)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    /// <summary>How many artists the line under the name names before it counts the rest. Three,
    /// because the line is one trimmed line beside a cover, and a fourth name would push the "and N
    /// more" off the end. That part of the sentence shows that the list is bigger than the names
    /// shown.</summary>
    private const int FeaturedNamed = 3;

    /// <summary>
    /// The line under the name that says who is on the list.
    ///
    /// <para>It is read from the rows in hand and is not asked again from the library: the rows are the
    /// list, and a second reading could disagree with the tracklist shown below it on the same
    /// screen.</para>
    ///
    /// <para>It is hidden when the songs name nobody. This is a normal state and not a fault: an
    /// untagged folder added to a playlist has titles and no credits.</para>
    /// </summary>
    private void ShowFeatured(IReadOnlyList<PlaylistTrackRow> rows, SmartPlaylistId? smart)
    {
        var blurb = Blurb(rows, smart);

        // Ask for exactly the names that the sentence prints. The count at the end does not depend on
        // this: asking for more names only moves artists from the "more" side of the total to the named
        // side.
        var featured = PlaylistFeatures.Of(rows.Select(row => row.Artist), FeaturedNamed);
        if (featured.Named.Count == 0)
        {
            // A mood list whose songs credit nobody still has something to say: what it sounds like does
            // not depend on the tags being filled in.
            DetailFeaturing.Text = blurb ?? "";
            DetailFeaturing.Visibility = blurb is null ? Visibility.Collapsed : Visibility.Visible;
            DetailFeaturing.MaxLines = blurb is null ? 1 : 2;
            return;
        }

        // The text names the first three artists, and every other artist is counted in "more".
        var named = featured.Named.Take(FeaturedNamed).ToList();
        var more = featured.Named.Count + featured.More - named.Count;

        var names = string.Join(", ", named);
        var featuring = more > 0
            ? Loc.Format("Playlists_FeaturingMore", names, more)
            : Loc.Format("Playlists_Featuring", names);

        // The description comes first and the credits after it, on one line: what the list is matters
        // more than who is on it, and a reader who wants the names reads on. Two lines are allowed when
        // there is a description, because one line would cut off the names completely.
        DetailFeaturing.Text = blurb is null ? featuring : $"{blurb} {featuring}";
        DetailFeaturing.MaxLines = blurb is null ? 1 : 2;
        DetailFeaturing.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// The sentence that describes a mood list, or null for every other kind.
    ///
    /// <para><b>Only moods.</b> "Most played" needs no explanation. A mood is different: "Chill" is
    /// only a word, and which part of their own collection it came from is something a listener cannot
    /// see.</para>
    ///
    /// <para>The genres are counted from the rows in hand, as the credits beside them are: the rows are
    /// the list, and a second reading of the library could disagree with the tracklist shown below on
    /// the same screen.</para>
    /// </summary>
    private static string? Blurb(IReadOnlyList<PlaylistTrackRow> rows, SmartPlaylistId? smart)
        => smart is { } id ? MoodBlurbText.For(id, rows.Select(row => row.Genre)) : null;

    /// <summary>How far down a list the header looks for a picture. A list of borrowed records whose
    /// computer is off the network gives no answer for row after row, and the header is one small
    /// square, so checking a thousand songs is not worth it. The glyph is shown in either case.</summary>
    private const int HeaderCoverTries = 12;


    /// <summary>
    /// The picture at the head of an open list. It is one of three things.
    ///
    /// <para><b>A list that a user made</b> gets a cover drawn for it: the records its songs come from,
    /// inset, on a blurred ground of their own colours. See <see cref="PlaylistArt.Made"/>, which
    /// explains the inset: a sleeve that fills the whole square looks like an album, and a playlist is
    /// not one.</para>
    ///
    /// <para><b>A generated list</b> gets its form and its own name across it. It has no records to show
    /// because it is a query and not a set: its content depends on when it is asked, so a quilt of four
    /// sleeves would claim a membership that does not hold.</para>
    ///
    /// <para><b>The queue and a folder</b> use the first track that can supply a picture. The queue is
    /// not a playlist, and a folder is a place and not a list.</para>
    ///
    /// <para>Every branch checks after each await that the rows are still the open ones: opening a
    /// second list while the cover of the first is being drawn is a normal case.</para>
    /// </summary>
    /// <param name="fill">Whether a list was opened or requested again. The room at the head of the list
    /// is painted for the first and never for the second; see <see cref="WantsRoom"/>, where that rule
    /// is kept. The caller states it and there is no default: a re-read that repainted would send the
    /// header's drift back to its opening state, and nothing in a diff would show it.</param>
    private async Task FillDetailCoverAsync(
        PlaylistDetail detail, PlaylistRowKind? kind, DetailFill fill)
    {
        var rows = detail.Tracks;
        try
        {
            var borrowed = Model is { IsBorrowed: true };

            var drawn = kind switch
            {
                // Nothing is read from disk for this one: the picture is what the list is. Its mood decides
                // the family and the colour (see SmartPlaylistFace.Cover), with the name across it.
                PlaylistRowKind.Smart when detail.SmartId is { } smart
                    // The page's ground comes with the square: the same picture, blurred.
                    => await GeneratedCover.PageAsync(smart, CancellationToken.None),

                // Drawn in the same place as every surface that shows a playlist, so the picture at the head of
                // this list and the picture on an artist's shelf are the same picture.
                PlaylistRowKind.Playlist or PlaylistRowKind.Ref when NodeOf(kind) is { } node
                    => await PlaylistCovers.MadeAsync(
                        node, detail.Name, CoverOf(node, borrowed),
                        borrowed ? PlaylistShelf.Borrowed : PlaylistShelf.Own,
                        CancellationToken.None),

                _ => PlaylistArt.Drawn.None,
            };

            if (!ReferenceEquals(_detailRows, rows))
                return;   // another list was opened while the picture was loading

            // The ground first, then the square on it: a page that put its background up after its
            // foreground would show itself being assembled. The artist page uses the same order.
            await ShowWashAsync(drawn.Wash, rows);

            // The room behind the whole header, only for a list that is arriving. It is done here and not
            // in Populate because the colours come from the cover. A room painted earlier would use the
            // colours of the previous list.
            if (WantsRoom(fill, detail.Name, drawn.Cover))
                OpenRoom(drawn.Cover, detail.Name);

            if (drawn.Cover is { Length: > 0 } cover && await ShowCoverAsync(cover, rows))
                return;

            // Either this is the queue or a folder, or the list had no artwork anywhere to draw from. Use
            // the first track that can supply a picture.
            foreach (var row in rows.Take(HeaderCoverTries))
            {
                if (await row.SleeveBytesAsync(ArtScaler.TileEdge) is not { Length: > 0 } thumb)
                    continue;
                if (!ReferenceEquals(_detailRows, rows))
                    return;
                if (await ShowCoverAsync(thumb, rows))
                    return;
            }

            // Nothing could supply a picture, so the header is finished with the glyph. On a list that is
            // arriving the glyph is already there. On a re-read the square still holds the cover of the
            // previous reading, and this is the only place that knows it is gone (the queue was emptied, or
            // the record the picture came from was deleted).
            if (ReferenceEquals(_detailRows, rows))
            {
                DetailCover.Source = null;
                DetailCoverGlyph.Visibility = Visibility.Visible;
            }
        }
        catch { /* decoration only: the header keeps the glyph */ }
    }

    /// <summary>Which playlist node the open row stands for: its own, or the node that a nested
    /// reference points at. Null for anything that is not a stored list. The caller has already
    /// excluded those by kind, but this asks anyway, because the tree can hold a Ref row with no
    /// target.</summary>
    private long? NodeOf(PlaylistRowKind? kind) => kind switch
    {
        PlaylistRowKind.Playlist => _detailItem?.NodeId,
        PlaylistRowKind.Ref => _detailItem?.RefPlaylistId,
        _ => null,
    };

    /// <summary>How the picture of the open row's node is chosen, read from the tree next to its name.
    /// Automatic when the tree no longer has the node (deleted during a draw that is still running),
    /// which is the same answer as an empty value.</summary>
    private static PlaylistCover CoverOf(long node, bool borrowed)
    {
        var library = App.Services.GetRequiredService<LibraryService>();
        var db = borrowed ? library.Shelf : library.Db;
        return db.GetPlaylistNodes().FirstOrDefault(n => n.Id == node)?.Cover ?? new PlaylistCover.Automatic();
    }

    /// <summary>
    /// Puts the list's own ground behind the whole page, or leaves the page as it was when there is
    /// none.
    ///
    /// <para>The queue and a folder never have one: neither is a list that a user made, so no ground is
    /// drawn for it, and the pane keeps its own surface.</para>
    /// </summary>
    private async Task ShowWashAsync(byte[]? wash, IReadOnlyList<PlaylistTrackRow> rows)
    {
        if (wash is not { Length: > 0 })
        {
            // This list has no ground. That is an answer and not a missing value. On a re-read the page
            // still shows the ground of the previous reading, so doing nothing here would leave the queue with
            // the ground of the playlist that was opened before it.
            if (ReferenceEquals(_detailRows, rows))
            {
                DetailWash.Visibility = Visibility.Collapsed;
                DetailWashArt.ImageSource = null;
                DetailWeight.Visibility = Visibility.Collapsed;
                Lend();
            }
            return;
        }

        try
        {
            var image = new BitmapImage();
            var stream = Presentation.Common.BitmapBytes.Stream(wash);
            await image.SetSourceAsync(stream);

            if (!ReferenceEquals(_detailRows, rows))
                return;   // another list was opened while it was decoding

            DetailWashArt.ImageSource = image;
            DetailWash.Visibility = Visibility.Visible;
            DetailWeight.Visibility = Visibility.Visible;
            Lend();
        }
        catch
        {
            // Decoration. The page keeps the pane's own surface.
        }
    }

    /// <summary>
    /// Gives the list's ground to the whole window, so that while a playlist is open and no artwork is
    /// shown over the app, the app uses the colours of that list and not its own wallpaper.
    ///
    /// <para>This is the same arrangement as the artist page, down to the layers (see its Lend). It
    /// reads what the panel is actually showing and not the answer that set it up, so one place decides
    /// and it cannot disagree with what is on screen. There is no flat colour under it: a list's ground
    /// is a picture only, while an artist also has a gradient.</para>
    ///
    /// <para>Null while the panel is bare. That means a page that is open with nothing shown, not a page
    /// that is closed: the window keeps what it showed until this page has something of its own. Only
    /// <see cref="ExitDetail"/> says closed, because leaving is the one thing this cannot infer.</para>
    /// </summary>
    private void Lend()
        => PageBackdrop.Opened(this, DetailWash.Visibility == Visibility.Visible
            ? new PageBackdrop.Ground(
                Colour: null,
                DetailWashArt.ImageSource,
                DetailWash.Opacity,
                Veil: 0,   // the ground is already held back where it was drawn, not here
                DetailWeight.Fill)
            : null);

    /// <summary>Puts one drawn or found picture in the header square. Returns false only when the
    /// picture does not decode, so the caller can try the next one. If the user opened another list
    /// during the decode, it returns true, because there is nothing on screen for the next picture to be
    /// written to.</summary>
    private async Task<bool> ShowCoverAsync(byte[] bytes, IReadOnlyList<PlaylistTrackRow> rows)
    {
        try
        {
            var image = new BitmapImage();
            var stream = Presentation.Common.BitmapBytes.Stream(bytes);
            await image.SetSourceAsync(stream);

            if (!ReferenceEquals(_detailRows, rows))
                return true;   // not ours any more, and there is nothing more to try

            DetailCover.Source = image;
            DetailCoverGlyph.Visibility = Visibility.Collapsed;
            return true;
        }
        catch
        {
            return false;   // a half-written or unreadable picture; the next row may work
        }
    }

    /// <summary>
    /// Opens one playlist's tracklist from outside this view. The artist page's shelf of playlists
    /// uses it.
    ///
    /// <para>It does the same as a click on the row, so a playlist opened from an artist shows exactly
    /// what a playlist opened from the tree shows: one tracklist and one set of actions. The caller has
    /// already asked the conductor for this view; this only selects the list.</para>
    ///
    /// <para>Returns false when the tree has no such playlist (it was deleted, or it is a folder). The
    /// view stays on the tree.</para>
    /// </summary>
    /// <param name="fromHome">Whether the press was on the home screen, which is where backing out of
    /// the list leads. The caller states it and there is no default: home is the only origin with its
    /// own back target.</param>
    public bool OpenPlaylist(long nodeId, bool fromHome)
    {
        if (Model?.RowFor(nodeId) is not { Kind: PlaylistRowKind.Playlist } row)
            return false;

        ShowDetail(row, fromHome ? PlaylistFrom.HomeScreen : PlaylistFrom.Elsewhere);
        return true;
    }



    /// <summary>
    /// Opens a list that the library writes itself, whole or narrowed ("most played rock", "the calm
    /// end of jazz"). The home banner and a genre's page use it.
    ///
    /// <para>It opens the same tracklist as <see cref="OpenPlaylist"/>: a generated list opened from
    /// home shows the same tracklist, with the same actions, as one opened by pressing its row in the
    /// tree. It is named by its ID and not by its kind, because a kind alone cannot name a list that is
    /// cut by a genre, and the banner holds such cuts.</para>
    ///
    /// <para>Returns false when this collection does not offer that list: a borrowed shelf, or a cut
    /// with too little music behind it. The view stays on the tree.</para>
    /// </summary>
    /// <param name="fromHome">Whether the press was on the home screen, which is where backing out of
    /// the list leads. The caller states it and there is no default: home is the only origin with its
    /// own back target.</param>
    public bool OpenSmart(SmartPlaylistId id, bool fromHome)
    {
        if (Model?.SmartRowFor(id) is not { } row)
            return false;

        ShowDetail(row, fromHome ? PlaylistFrom.HomeScreen : PlaylistFrom.Elsewhere);
        return true;
    }
    /// <summary>
    /// The song in the player changed: every row on the open list asks again whether it is the one.
    /// </summary>
    /// <remarks>
    /// <b>No path is passed, on purpose.</b> The row reads the player itself
    /// (<see cref="PlaylistTrackRow.IsPlaying"/>); this call only tells it to ask again. So a call that
    /// arrives twice, late, or for a list that has since been replaced cannot put the mark on the wrong
    /// row. At worst a row re-reads an answer it already had.
    ///
    /// <para>It covers the rows the view holds, not only the rows on screen: the list virtualizes, and
    /// a row scrolled into view later asks on realize and needs nothing from here.</para>
    ///
    /// <para>It is cheap enough to run on every track change: it is one string comparison per row of
    /// one open list, and only the rows whose answer changed raise anything that a binding listens
    /// to.</para>
    /// </remarks>
    public void SetPlaying()
    {
        foreach (var row in _detailRows)
            row.RaisePlaying();
    }

    /// <summary>The tree or queue changed under an open detail (a song added from the library, the queue
    /// moved): re-reads the current playlist's tracks in place. The host calls it on the model's
    /// DetailInvalidated.</summary>
    public void RefreshQueueDetailIfOpen()
    {
        // The queue moved. Only the Now playing detail shows the queue. A stored playlist's rows come
        // from the database and say nothing about what is playing, so re-reading one here would be a whole
        // rebuild for a list that has not changed.
        if (_detailIsQueue)
            RebuildDetail();
    }

    /// <summary>A song's heart turned over. Only a generated list that reads the heart shows that
    /// change. A stored playlist holds the songs that a user put in it and shows no marks, so
    /// re-reading one here would repaint a list that did not change. The host calls it on the model's
    /// MarksInvalidated.</summary>
    public void RefreshMarkedDetailIfOpen()
    {
        if (_detailItem?.SmartKind is { } kind && LibraryDatabase.SmartPlaylistNeedsMarks(kind))
            RebuildDetail();
    }

    /// <summary>
    /// The playlist tree changed under an open detail: a list was made, renamed, deleted, or had songs
    /// added. The host calls it on the model's DetailInvalidated.
    ///
    /// <para><b>A generated list is not re-read here, and this is a correctness rule and not only a
    /// saving.</b> Nothing about the tree is in its answer: it is a question asked of the songs, and
    /// renaming a playlist elsewhere cannot change what "Slow Burn" holds. A rebuild is also not
    /// harmless: the mood lists step their window on every opening (see LibraryDatabase.MoodWindow), so
    /// an unrequested re-read returns a different fifty songs, and the list on screen changes because
    /// something unrelated was renamed. This matters when saving a generated list: the save writes a
    /// playlist, the write reloads the tree, and the list being kept would be replaced by the act of
    /// keeping it.</para>
    ///
    /// <para>The one generated list that is re-read is the list of hearts, and it is re-read when a
    /// heart turns over and not when the tree changes; see <see cref="RefreshMarkedDetailIfOpen"/>,
    /// which asks for the rebuild itself. Those rules have no window (the track rows answer them
    /// whole), so a re-read returns the same songs plus or minus the one just marked, which is the
    /// purpose.</para>
    /// </summary>
    public void RefreshDetailIfOpen()
    {
        if (_detailItem is { Kind: PlaylistRowKind.Smart })
            return;

        RebuildDetail();
    }

    /// <summary>Re-reads the open list and puts it back in place. This is the raw operation: every
    /// caller above has already decided that this list can be changed by what happened.</summary>
    private void RebuildDetail()
    {
        if (!_inDetail || _detailItem is not { } item || Model is not { } m)
            return;

        // Find the row this list was opened from in the tree as it is now, never the row object that was
        // opened. A borrowed shelf's listing is written by deleting every node and inserting them all again
        // (LibraryDatabase.MirrorPlaylistTree), so the id of an open list can be stale and can also be
        // given to a different list. A query with it returns no tracks, and a list of no tracks is a valid
        // empty list, so the songs would show and then disappear shortly after opening on another person's
        // library. See PlaylistModel.Relocate.
        if (m.Relocate(item) is not { } live)
        {
            ExitDetail();   // the playlist is gone: back out to the tree
            return;
        }

        _detailItem = live;

        // The header is kept across a re-read, and a re-read must not step the mood window. Both
        // arguments on this line belong together; see ListReading and DetailFill.
        if (m.BuildDetail(live, ListReading.AsItStands) is { } detail)
            Populate(detail, DetailFill.Reread);
        else
            ExitDetail();   // the playlist is gone: back out
    }

    /// <summary>Goes back out of a tracklist to the tree, and on to home when the list was opened from
    /// home. The origin is read before the detail closes, because closing it clears the origin. Only the
    /// button does this. The host also calls ExitDetail when the pane switches away, and then the user
    /// wants something else.</summary>
    private void DetailBack_Click(object sender, RoutedEventArgs e)
    {
        var home = _detailFrom == PlaylistFrom.HomeScreen;
        ExitDetail();

        if (home)
            HomeRequested?.Invoke();
    }

    /// <summary>Returns to the tree and lets the host give the width back. Idempotent: it is safe to
    /// call when the tree is already showing (the host calls it when the pane switches away or
    /// closes).</summary>
    public void ExitDetail()
    {
        if (!_inDetail)
            return;

        // Back: the tree slides in from the left and the tracks slide out to the right.
        _inDetail = false;
        ReflectReading(_onView);
        TreeSide.Visibility = Visibility.Visible;
        Slide(TreeSide, TreeShift, -34, 0, 0, 1);
        Slide(Detail, DetailShift, 0, 34, 1, 0);
        HideAfterSlide(Detail, DetailShift, wantDetail: false);

        // A generation still on the pool is no longer this screen's business: the ticket changes, so the
        // rows it returns are dropped and not painted onto a tracklist that has gone. The veil and the
        // button are reset here and not left to that arrival, because a stale arrival must not touch
        // either of them.
        _generateTicket++;
        ShowGenerating(false);
        DetailNewListButton.IsEnabled = true;

        // Take the room off the clock too, because the view no longer shows a list. Nothing is
        // cancelled, because nothing runs on a worker: a frame is drawn inside a tick.
        SettleRoom();

        _detailItem = null;
        _detailFrom = PlaylistFrom.Elsewhere;
        _detailRows = Array.Empty<PlaylistTrackRow>();
        Tree.SelectedItem = null;   // so that clicking the same playlist again opens it again

        // The tree is not a page and has no colours of its own, so the window takes back its wallpaper.
        // This is done here and only here: a view left under a fade still holds the ground it showed, and
        // nothing outside the view can tell that this is no longer true.
        PageBackdrop.Closed(this);

        DetailModeChanged?.Invoke(false);
    }

    /// <summary>Slides and fades a view along X. Storyboard.Completed is unreliable on Uno-Skia, so the
    /// caller collapses the outgoing view on a timer (HideAfterSlide).</summary>
    private static void Slide(FrameworkElement el, TranslateTransform shift, double fromX, double toX, double fromO, double toO)
    {
        var sb = new Storyboard();
        var x = new DoubleAnimation
        {
            From = fromX, To = toX,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(x, shift);
        Storyboard.SetTargetProperty(x, "X");
        var o = new DoubleAnimation
        {
            From = fromO, To = toO,
            Duration = new Duration(TimeSpan.FromMilliseconds(190)),
        };
        Storyboard.SetTarget(o, el);
        Storyboard.SetTargetProperty(o, "Opacity");
        sb.Children.Add(x);
        sb.Children.Add(o);
        Services.Shell.Storyboards.Play(sb);
    }

    /// <summary>Collapses the outgoing view after its slide-out has played, but only if the drill state
    /// did not flip back in the meantime (a fast back and forth), so the view that is now on screen is
    /// never hidden.</summary>
    private async void HideAfterSlide(FrameworkElement el, TranslateTransform shift, bool wantDetail)
    {
        await Task.Delay(230);
        if (_inDetail != wantDetail)
            return;
        el.Visibility = Visibility.Collapsed;
        shift.X = 0;
        if (!wantDetail)
            DetailList.ItemsSource = null;   // drop the rows only after the tracks have slid away
    }

    /// <summary>Which of the library's own lists is open, or null for a stored playlist, a folder or
    /// the queue. The transport's verdict keys depend on it; see RPaMpModel.Source, which explains why
    /// they are offered only under a generated list.</summary>
    private SmartPlaylistId? OpenGenerated =>
        _detailItem is { Kind: PlaylistRowKind.Smart } item ? item.SmartId : null;

    private void DetailPlay_Click(object sender, RoutedEventArgs e)
    {
        if (Model is { } m && _detailRows.Count > 0)
            m.PlayRows(_detailRows, shuffle: false, OpenGenerated);
    }

    private void DetailShuffle_Click(object sender, RoutedEventArgs e)
    {
        if (Model is { } m && _detailRows.Count > 0)
            m.PlayRows(_detailRows, shuffle: true, OpenGenerated);
    }

    private void DetailAutoMix_Click(object sender, RoutedEventArgs e)
    {
        if (Model is { } m && _detailRows.Count > 0)
            m.AutoMixRows(_detailRows, OpenGenerated);
    }

    /// <summary>
    /// How long a generation runs before the wait is drawn over the tracks.
    ///
    /// <para>A generated list is one SQLite query and usually answers before the pointer has left the
    /// button. A wait mark shown for one frame looks like a fault and not like work, so nothing is
    /// drawn until the read has taken longer than a press does.</para>
    /// </summary>
    private const int GenerateVeilAfterMs = 180;

    /// <summary>
    /// Which generation the screen is waiting for.
    ///
    /// <para>This is a number and not a flag, because the question is not "is one running" but "is the
    /// one that just finished still the one the user is looking at". Between the click and the return
    /// of the rows, the user can press the button again, go back to the tree, or open a different list,
    /// and the answer to the first press would then arrive and paint over whatever is there now. Every
    /// arrival is checked against this and against the row it was asked about (see
    /// <see cref="StillOurs"/>). A stale arrival touches nothing, including the veil and the button,
    /// because both belong to whoever came after it.</para>
    /// </summary>
    private int _generateTicket;

    /// <summary>
    /// Asks the list for a different list, and shows the wait if it takes long.
    ///
    /// <para>Built off the UI thread through <c>PlaylistModel.BuildSmartDetailAsync</c>: the query is
    /// bounded, but each returned row probes a folder for its artwork, and two hundred directory reads
    /// on the dispatcher would freeze the pane.</para>
    /// </summary>
    private async void DetailNewList_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } m || _detailItem is not { Kind: PlaylistRowKind.Smart } item)
        {
            // The press reached the handler and there was no list to ask again. This is logged because from
            // outside it looks the same as a press that works: nothing moves in either case.
            GenerateLog.LogWarning(
                "[generate] pressed with no generated list open (model: {Model}, row: {Row})",
                Model is not null, _detailItem?.Kind.ToString() ?? "none");
            return;
        }

        var ticket = ++_generateTicket;
        GenerateLog.LogInformation("[generate] {List}: pressed, reading {Ticket}", item.DisplayName, ticket);

        // So a second press cannot start a second read of the same list. The ticket would make that
        // harmless, but the result of the first read would never be shown.
        DetailNewListButton.IsEnabled = false;

        var work = m.BuildSmartDetailAsync(item);

        // The veil goes up only if the read is still running when the delay is over, and only if this is
        // still the generation on screen. A press that the user has already left must not draw a wait over
        // whatever the user went to instead.
        if (await Task.WhenAny(work, Task.Delay(GenerateVeilAfterMs)) != (Task)work && StillOurs(ticket, item))
            ShowGenerating(true);

        var detail = await work;

        // Not ours any more: a newer press owns the button and the veil, or the tracklist has been left
        // and ExitDetail has already reset both. Either way nothing here is this generation's to touch.
        if (!StillOurs(ticket, item))
        {
            GenerateLog.LogInformation(
                "[generate] {List}: reading {Ticket} dropped, the screen is on {Now} (in detail: {In}, same row: {Row})",
                item.DisplayName, ticket, _generateTicket, _inDetail, ReferenceEquals(_detailItem, item));
            return;
        }

        ShowGenerating(false);
        DetailNewListButton.IsEnabled = true;

        // Null means the read did not return a list: the library closed while it was on the pool, or it
        // threw and PlaylistModel.BuildSmartDetailAsync turned the exception into null. The list already
        // on screen stays. Something did happen, though (the window was stepped before the failure), so it
        // is logged and not ignored.
        //
        // On success this is a re-read: the songs differ but the list is the same row, and a generated
        // list's picture is drawn from what the list is and not from what it contains (see
        // SmartPlaylistFace.Cover). The same square is about to be drawn again, and clearing it first would
        // make the header blink on every press of the button.
        if (detail is null)
        {
            // The window was already stepped by the read that failed, so the list that this button was
            // pressed on is a different list now. Only the screen still shows the old one. The reason is
            // logged where it was caught, in PlaylistModel.BuildSmartDetailAsync.
            GenerateLog.LogWarning(
                "[generate] {List}: nothing came back, the list on screen is left standing", item.DisplayName);
        }
        else
        {
            GenerateLog.LogInformation(
                "[generate] {List}: {Rows} songs put up", item.DisplayName, detail.Tracks.Count);
            Populate(detail, DetailFill.Reread);

            // The list on screen is the one the phone should show, so this press sends it: the cut is sent
            // now and not at the next idle moment. Own shelf only: a borrowed list was generated on another
            // person's collection, and that computer publishes it.
            if (Model is { IsBorrowed: false })
                App.Services.GetRequiredService<Services.Cloud.MoodListPushService>().SendNow();
        }
    }

    /// <summary>The log of the press. It has a name and not a type, as the log of PlaylistView.Room
    /// does, so the lines of one generation read as one run in the terminal.</summary>
    private static ILogger GenerateLog =>
        App.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RPM.Generate");

    /// <summary>Whether the generation that just finished is still the one the screen waits for, for
    /// the list the screen still shows. The item is checked as well as the ticket, because leaving and
    /// re-entering the tracklist is not a new generation and does not change the number.</summary>
    private bool StillOurs(int ticket, PlaylistTreeItem item)
        => ticket == _generateTicket && _inDetail && ReferenceEquals(_detailItem, item);

    /// <summary>The wait over the tracks. The spinner shows itself when it is told to turn (see
    /// RecordSpinner.Spinning), so this sets the veil and the record once each.</summary>
    private void ShowGenerating(bool generating)
    {
        DetailBusy.Visibility = generating ? Visibility.Visible : Visibility.Collapsed;
        DetailBusySpinner.Spinning = generating;
    }

    /// <summary>
    /// Keeps the generated list as a playlist of the user's own.
    ///
    /// <para>The rows are taken before the name is asked for, on purpose: what is saved is the list that
    /// was on screen when the button was pressed. If they were read after the dialog closed, they could
    /// be a different list, because the pane can rebuild itself while a dialog is open. A save that
    /// keeps something other than what the user was looking at is worse than no save.</para>
    /// </summary>
    private async void DetailSave_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } m || _detailRows.Count == 0)
            return;

        var rows = _detailRows;
        var name = await PromptNameAsync(Loc.Get("Playlists_SaveAsPlaylist"), DetailName.Text);
        if (name is null)
            return;

        m.SaveAsPlaylist(name, rows.Select(r => r.Info));
    }

    /// <summary>
    /// One press on a row plays it and the rest of the open list after it.
    ///
    /// <para>This is a single press and not a double press. The list also selects on the same press
    /// (this is the ListView's own behaviour, and the context menu and the Delete key act on the
    /// selection). So this handler stays out of the way of the two gestures that build a selection: a
    /// modifier held down means the press is about the selection and not about the music.</para>
    ///
    /// <para>Cmd works as well as Ctrl, because this runs on a Mac and Cmd is the key that a hand
    /// reaches for. The keys are read from the keyboard through <see cref="HeldKeys"/>, not from Uno's
    /// table of key events: on macOS that table keeps Cmd down after a Cmd-Tab, and every row tapped
    /// afterwards would be taken for a Cmd-click and would not play.</para>
    ///
    /// <para>Every tap on a row that does not play logs the reason on the <c>input</c> channel.</para>
    /// </summary>
    private void Track_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (TrackRowUnder(e.OriginalSource) is not { } hit)
        {
            Said.Line("input", "list tap not played: no row under it");
            return;
        }

        if (Model is not { } m)
        {
            Said.Line("input", "row tap not played: the view has no model");
            return;
        }

        var row = hit.Row;

        // Extending or toggling the selection. Nothing plays.
        var held = HeldKeys.Now & (Modifier.Shift | Modifier.Control | Modifier.Command);
        if (held != Modifier.None)
        {
            Said.Line("input", $"row tap not played: {held} held");
            return;
        }

        // A row being carried, or one still settling into its new place. A drag that moved a row far
        // enough does not raise Tapped at all, because the pointer is captured. So this case is the short
        // drag that ended on the row it started on, which is not a press to play either.
        if (_detailReorder is { IsBusy: true })
        {
            Said.Line("input", "row tap not played: a row is being moved");
            return;
        }

        m.PlayRowInList(_detailRows, row, OpenGenerated);
    }

    /// <summary>
    /// The track row that a gesture landed on, and the element it landed on, found by walking out
    /// from whatever the pointer actually hit.
    ///
    /// <para>Every part of a row has the row as its DataContext, including the container's own borders,
    /// so the first element with one is the answer wherever in the row the press was. Outside the rows
    /// there is no answer: the list's padding, its scrollbar and its empty space below the last track
    /// all reach the list's handlers, and none of them is a track.</para>
    /// </summary>
    private static (PlaylistTrackRow Row, FrameworkElement Element)? TrackRowUnder(object? source)
    {
        for (var node = source as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { DataContext: PlaylistTrackRow row } element)
                return (row, element);
        }

        return null;
    }

    private void Track_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (TrackRowUnder(e.OriginalSource) is not { } hit || Model is not { } m)
            return;

        var row = hit.Row;

        // Right-clicking a row that is not in the selection acts on that row alone, as in every list.
        // Otherwise an earlier selection would be removed along with it.
        var target = SelectedRows();
        if (!target.Contains(row))
        {
            target = [row];
            DetailList.SelectedItem = row;
        }

        var flyout = new MenuFlyout();

        // The queue can play a row where it stands. A stored playlist's rows only come out of it. Adding
        // to Now playing is not offered from the queue at all, because the track is already there.
        if (_detailIsQueue && target.Count == 1)
            flyout.Items.Add(Item(Loc.Get("Playlists_PlayNow"), GlyphPlay, () => m.PlayQueueRowNow(row)));

        // Starting the DJ on a row is an action on that row and not on the selection: the DJ gets this
        // track and everything after it in the open list, which is what the queue becomes.
        if (target.Count == 1)
            flyout.Items.Add(Item(Loc.Get("Dj_StartHere"), GlyphDj, () => m.StartDjFromRow(_detailRows, row, OpenGenerated)));

        // A radio starts from one song, so it is offered on one row, like the DJ above.
        if (target.Count == 1)
            flyout.Items.Add(Item(Loc.Get("Track_StartMixtape"), GlyphMixtape, () => m.StartRadioFromRow(row)));

        // Only on a list that the library writes. On the queue or a saved playlist there is no pool to
        // remember the refusal for, and the entry would look like it does nothing: those lists hold exactly
        // what a user put in them, which is what "Remove" below is for.
        //
        // On the row and not on the selection: it is a statement about one action and one record. A menu
        // that refused five of each because five rows happened to be selected would be hard to find again
        // and undo.
        if (OpenGenerated is { } generated && target.Count == 1)
            AddRefusals(flyout, m, generated, row);

        if (target.Any(r => r.CanRemove))
        {
            if (flyout.Items.Count > 0)
                flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(Item(Loc.Get("Playlists_Remove"), GlyphRemove, () => RemoveRows(target)));
        }

        if (flyout.Items.Count == 0)
            return;   // a folder's flattened rows: nothing this menu can do

        if (e.TryGetPosition(hit.Element, out var pos))
            flyout.ShowAt(hit.Element, new FlyoutShowOptions { Position = pos });
        else
            flyout.ShowAt(hit.Element);
        e.Handled = true;
    }

    /// <summary>
    /// "Don't recommend this artist" and "Don't recommend this album", on a row of a generated list.
    ///
    /// <para>Each is worded by what it will do and is not drawn as a tick, in the same way as the track
    /// menu's favourite entry, and for the same reason: a user reading a menu wants to know what
    /// pressing it does, not what state a flag is in. So an action that is already refused is offered
    /// as its reverse and is not offered again.</para>
    ///
    /// <para>Neither entry is shown where there is no subject to name: a song outside this shelf has no
    /// record here, and an untagged song names no artist or album. An entry that stands for nothing is
    /// worse than a missing entry, because the press looks like it worked.</para>
    ///
    /// <para>The list is put back with <see cref="ListReading.AsItStands"/>, so the user sees the
    /// refused row go and everything under it move up, and not the new fifty songs that an opening
    /// would have drawn. The refusal is the change; the window is not.</para>
    /// </summary>
    private void AddRefusals(
        MenuFlyout flyout, PlaylistModel model, SmartPlaylistId generated, PlaylistTrackRow row)
    {
        var said = model.RefusalsFor(generated, row.Info);
        if (said.Artist.Length == 0 && said.Album.Length == 0)
            return;

        if (flyout.Items.Count > 0)
            flyout.Items.Add(new MenuFlyoutSeparator());

        if (said.Artist is { Length: > 0 } artist)
            flyout.Items.Add(Item(
                Loc.Get(said.ArtistRefused ? "Refuse_ArtistUndo" : "Refuse_Artist"), GlyphRefuseArtist,
                () => Refuse(generated, RecommendScope.Artist, artist, !said.ArtistRefused)));

        if (said.Album is { Length: > 0 } album)
            flyout.Items.Add(Item(
                Loc.Get(said.AlbumRefused ? "Refuse_AlbumUndo" : "Refuse_Album"), GlyphRefuseAlbum,
                () => Refuse(generated, RecommendScope.Album, album, !said.AlbumRefused)));
    }

    /// <summary>Writes one refusal and puts the open list back without it.</summary>
    private void Refuse(SmartPlaylistId generated, RecommendScope scope, string subject, bool refused)
    {
        if (Model is not { } m)
            return;

        m.SetRefusal(generated, scope, subject, refused);
        RebuildDetail();
    }

    /// <summary>Delete removes the selected tracks from the open list. Backspace does the same, because
    /// most Mac keyboards have no forward-delete key.</summary>
    private void DetailList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (Windows.System.VirtualKey.Delete or Windows.System.VirtualKey.Back))
            return;

        var rows = SelectedRows();
        if (rows.Count == 0)
            return;

        RemoveRows(rows);
        e.Handled = true;
    }

    /// <summary>The rows selected in the open detail list, in the list's own order.</summary>
    private List<PlaylistTrackRow> SelectedRows() =>
        [.. DetailList.SelectedItems.OfType<PlaylistTrackRow>()];

    /// <summary>Removes rows and puts the list back, keeping the selection sensible: the row that now
    /// sits where the first removed row was is selected, so a run of Delete presses walks down the
    /// list.</summary>
    private void RemoveRows(IReadOnlyList<PlaylistTrackRow> rows)
    {
        if (Model is not { } m || rows.Count == 0)
            return;

        var removable = rows.Where(r => r.CanRemove).ToList();
        if (removable.Count == 0)
            return;

        int at = _detailRows.Count == 0 ? 0 : Math.Max(0, _detailRows.ToList().IndexOf(removable[0]));
        m.RemoveRows(removable);

        // The queue raises its own change (which refreshes the detail), and so does the reload of a
        // stored playlist. Re-reading here as well would be a second rebuild, so only the selection is
        // restored.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (DetailList.Items.Count == 0)
                return;
            DetailList.SelectedIndex = Math.Min(at, DetailList.Items.Count - 1);
        });
    }

    // ── name prompt ──

    private async Task<string?> PromptNameAsync(string title, string initial)
    {
        if (XamlRoot is null)
            return null;

        var box = new TextBox { Text = initial, SelectionStart = 0, SelectionLength = initial.Length };
        var answer = await PromptDialog.ShowAsync(
            XamlRoot, title, box,
            primaryText: Loc.Get("Common_OK"),
            closeText: Loc.Get("Common_Cancel"),
            defaultButton: ContentDialogButton.Primary);
        if (answer != ContentDialogResult.Primary)
            return null;
        var name = box.Text?.Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }
}
