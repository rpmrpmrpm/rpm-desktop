using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RPM.DataGrid.Model;
using RPM.DataGrid.Paint;
using Microsoft.UI.Xaml.Media.Imaging;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Grids;
using RPM.Desktop.Presentation.Main.Transport.Controls;
using RPM.Desktop.Presentation.RPaMp.Models;
using RPM.Core.Sharing;
using RPM.Desktop.Services.Art;
using RPM.Desktop.Services.Library;
using RPM.Desktop.Services.Sharing;
using RPM.Desktop.Strings;
using System.Linq;
using Windows.Foundation;

namespace RPM.Desktop.Presentation.RPaMp;

/// <summary>
/// The library pane's four grids: what they show, and what a click in one of them does.
///
/// <para>The tree, the two name panes and the song list are all <see cref="RpmGrid"/>, the grid that the
/// library table and the import queue are drawn with. What each grid shows comes from
/// <see cref="RPaMpModel"/>'s <see cref="ListGridSource"/>s. These are replaced in full each time the list
/// is narrowed.</para>
///
/// <para><b>A pick is not kept in a grid's selection.</b> Replacing a source also clears the grid's
/// selection. So three of the four grids ask the model which row is picked each time they draw a row
/// (<c>RpmGrid.Washed</c>). They are not told in advance. The song list is the exception. Its selection
/// is its own, and nothing outside it depends on what is selected.</para>
/// </summary>
public sealed partial class RPaMpView
{
    /// <summary>The model the grids are built on. The grids are built once, not again for the same
    /// model.</summary>
    private RPaMpModel? _libModel;

    /// <summary>The song list's columns, kept so the header menu can hide and show them.</summary>
    private GridColumns? _libTrackColumns;

    /// <summary>Builds the four grids over the model. Does nothing twice.</summary>
    private void AttachLibraryGrids(RPaMpModel model)
    {
        if (ReferenceEquals(_libModel, model))
            return;

        _libModel = model;

        // The tree. Set Columns before Source, because the grid builds its frame when the columns arrive.
        // The other settings are applied to that frame.
        LibNavGrid.Columns = AmpGrids.Nav();
        LibNavGrid.Source = model.LibNavRows;
        LibNavGrid.Metrics = new GridMetrics { RowHeight = 22, HeaderHeight = 22 };
        LibNavGrid.ShowHeader = false;
        LibNavGrid.Washed = (_, row) => ReferenceEquals(row, _libModel?.LibPick);
        LibNavGrid.Selection.SelectionChanged += NavPicked;
        LibNavGrid.ExpanderPressed += NavExpanderPressed;

        LibArtistGrid.Columns = AmpGrids.Artists();
        LibArtistGrid.Source = model.LibArtistRows;
        LibArtistGrid.Metrics = new GridMetrics { RowHeight = 20, HeaderHeight = 20 };
        LibArtistGrid.Washed = (_, row) =>
            row is AmpBrowseRow pane && Same(pane.Name, _libModel?.LibArtist);
        LibArtistGrid.Selection.SelectionChanged += ArtistPicked;

        LibAlbumGrid.Columns = AmpGrids.Albums();
        LibAlbumGrid.Source = model.LibAlbumRows;
        LibAlbumGrid.Metrics = new GridMetrics { RowHeight = 20, HeaderHeight = 20 };
        LibAlbumGrid.Washed = (_, row) =>
            row is AmpBrowseRow pane && Same(pane.Name, _libModel?.LibAlbum);
        LibAlbumGrid.Selection.SelectionChanged += AlbumPicked;

        _libTrackColumns = AmpGrids.Tracks(row => model.IsLibraryRowPlaying(row));
        LibTrackGrid.Columns = _libTrackColumns;
        LibTrackGrid.Source = model.LibTrackRows;
        LibTrackGrid.Metrics = new GridMetrics { RowHeight = 20, HeaderHeight = 20 };
        LibTrackGrid.Selection.SelectionChanged += ShowLibraryArt;
        LibTrackGrid.RowActivated += TrackActivated;
        LibTrackGrid.ContextRequestedAt += TrackContextRequested;
        LibTrackGrid.HeaderClicked += TrackHeaderClicked;
        LibTrackGrid.HeaderMenuRequested += TrackHeaderMenu;

        model.LibraryRefreshed += LibraryRefreshed;

        ShowCollectionName();

        // The mark in the first column is read from the row as it is drawn. So when a track starts playing
        // somewhere else in the app, only one repaint is needed, not a search for the row.
        model.DeckChanged += () => LibTrackGrid.Invalidate();
    }

    // ── which collection the pane is showing ──

    /// <summary>The sharing service, for the switcher. It is found the same way as in other controls. It
    /// is null where there is no host, and then the button offers only the user's own collection.</summary>
    private SharingService? _sharing;

    private LibraryService? _libraryService;

    /// <summary>Who the other computers belong to. The switcher then shows people's names instead of
    /// machine names, where a profile is known. It is the same table the deck's share button reads.</summary>
    private PeerFaces? _peerFaces;

    /// <summary>Whether the user has an account, which is needed to open another person's shelf. The
    /// deck's share button reads the same value, so both switches offer the same places.</summary>
    private Services.Account.AccountGate? _accountGate;

    private void EnsureLibraryServices()
    {
        _sharing ??= App.Services?.GetService<SharingService>();
        _libraryService ??= App.Services?.GetService<LibraryService>();
        _peerFaces ??= App.Services?.GetService<PeerFaces>();
        _accountGate ??= App.Services?.GetService<Services.Account.AccountGate>();
    }

    /// <summary>
    /// The collections the pane can switch to: this computer's own, and every shelf shared by someone else.
    /// </summary>
    /// <remarks>
    /// <see cref="SharedLibraryRow.Build"/> is the deck's own list and is used here unchanged, so both
    /// switches offer the same collections. It returns an empty list when there is nothing to switch to.
    /// That suits the deck button, but an open menu needs something to show. So the user's own row is
    /// added back, and the menu always shows what is on screen.
    /// </remarks>
    private List<SharedLibraryRow> Collections()
    {
        EnsureLibraryServices();

        var own = Loc.Get("Share_MyLibrary");
        if (_sharing is not { } sharing)
            return [new SharedLibraryRow("", own, "", IsOpen: true, PeerFace.Nobody)];

        _peerFaces?.Learn(sharing.Peers);

        Func<string, PeerFace> faceOf =
            _peerFaces is { } faces ? faces.Face : static _ => PeerFace.Nobody;

        var rows = SharedLibraryRow.Build(
            sharing.Peers,
            _accountGate is { IsOpen: true } ? ShelfAccess.Open : ShelfAccess.NeedsAccount,
            PeerLibraryStore.Exists,
            sharing.IsReachable,
            faceOf,
            _libraryService?.ViewingPeer,
            PeerFace.Nobody,
            own,
            Loc.Get("Settings_ShareOnline"),
            Loc.Get("Settings_ShareOffline"));

        return rows.Count > 0
            ? rows
            : [new SharedLibraryRow("", own, "", IsOpen: true, PeerFace.Nobody)];
    }

    /// <summary>
    /// Fills the switcher's list as it opens.
    /// </summary>
    /// <remarks>
    /// The list is built each time the menu opens, not kept up to date. It has six rows at most.
    /// One fact in them is whether another computer answered just now. That is only worth checking
    /// while the menu is open.
    /// </remarks>
    private void LibrarySwitch_Opening(object? sender, object e)
    {
        LibraryPickerList.Picked -= LibraryRowPicked;
        LibraryPickerList.Picked += LibraryRowPicked;
        LibraryPickerList.Show(Collections());
    }

    private void LibraryRowPicked(string deviceId)
    {
        LibrarySwitchFlyout.Hide();
        OpenCollection(deviceId);
    }

    /// <summary>
    /// Steps the pane into another collection, or back into the user's own.
    /// </summary>
    /// <remarks>
    /// Another person's collection needs an account. Part of a shared shelf is records that the owner put
    /// in the cloud, and the URL that plays each one is issued to whoever asks for it. Going back to the
    /// user's own collection never needs an account. It is the way out, and a way out that asks for an
    /// account would trap the user on a shelf they cannot use. The deck's switch uses the same rule.
    /// </remarks>
    private async void OpenCollection(string deviceId)
    {
        EnsureLibraryServices();
        if (_sharing is not { } sharing)
            return;

        if (string.Equals(deviceId, _libraryService?.ViewingPeer ?? "", StringComparison.OrdinalIgnoreCase))
            return;

        if (deviceId.Length == 0)
        {
            sharing.CloseLibrary();
        }
        else
        {
            if (!await Dialogs.SignInGate.AllowAsync(XamlRoot, Loc.Get("Login_ForSharing")))
                return;

            if (!sharing.OpenLibrary(deviceId))
                return;
        }

        // The search box is the one piece of state that the reload does not own, because its words are on
        // screen. It is cleared here, because the words narrowed the collection that was open.
        LibSearchBox.Text = string.Empty;

        _libModel?.ReloadLibrary();
        ShowCollectionName();
    }

    /// <summary>Puts the name of the collection on screen on the switcher.</summary>
    private void ShowCollectionName()
    {
        foreach (var row in Collections())
        {
            if (!row.IsOpen)
                continue;

            LibrarySwitchLabel.Text = row.Name;
            return;
        }
    }

    /// <summary>The rows were replaced. Show the sort again and redraw the picks.</summary>
    private void LibraryRefreshed()
    {
        if (_libModel is not { } model)
            return;

        LibTrackGrid.ShowSort(model.LibSort?.Column, model.LibSort?.Descending ?? false);

        // The tree's rows stay the same when a different list is picked, so nothing tells the tree to redraw.
        // Redraw it here.
        LibNavGrid.Invalidate();
        LibArtistGrid.Invalidate();
        LibAlbumGrid.Invalidate();

        ShowLibraryArt();
    }

    /// <summary>
    /// A row of one of the three picking grids was clicked.
    /// </summary>
    /// <remarks>
    /// <b>An EMPTY selection does not mean the user picked nothing.</b> Each pick replaces the rows of the
    /// grids beside it. A replaced list clears that grid's selection, and clearing it raises this same
    /// event again, with the focus still where it was. If that focus were read as a pick, the artist pane
    /// would select the first name in a list it has just been given. An empty selection is what tells the
    /// two cases apart. This holds whenever it is checked, not only during one dispatcher turn.
    /// </remarks>
    private static object? Picked(RpmGrid grid, ListGridSource rows) =>
        grid.Selection.IsEmpty ? null : rows.Peek(grid.Selection.Focus);

    // ── the cover under the tree ──

    /// <summary>The folder of the cover that is on screen, or being loaded. A read that finishes late is
    /// harmless: its result is dropped unless this still names the folder it was asked for.</summary>
    private string? _libArtFolder;

    private CancellationTokenSource? _libArtCancel;

    /// <summary>The size, in physical pixels, that the cover is loaded at. The card is 168 points square,
    /// and the screen may draw two pixels per point.</summary>
    private const int LibArtEdge = 336;

    /// <summary>
    /// Shows the cover of the song picked in the list, and the placeholder when none is.
    /// </summary>
    /// <remarks>
    /// The read runs off the UI thread, and the list can change while it runs. What keeps this safe is
    /// not the order in which the answers arrive. The folder that was asked for is stored here. Image
    /// bytes for a folder that is no longer on screen are dropped. A row moving, the list being sorted
    /// again, and a different collection being opened are all the same question, asked again.
    /// </remarks>
    private async void ShowLibraryArt()
    {
        if (_libModel is not { } model)
            return;

        // Nothing picked is a valid answer, so no other row is guessed. The card then shows the empty
        // placeholder.
        var row = LibTrackGrid.Selection.IsEmpty
            ? null
            : model.LibTrackRows.Peek(LibTrackGrid.Selection.Focus) as LibraryRow;

        var folder = row?.Folder is { Length: > 0 } named ? named : null;
        if (folder == _libArtFolder)
            return;

        _libArtFolder = folder;

        _libArtCancel?.Cancel();
        _libArtCancel?.Dispose();
        _libArtCancel = null;

        if (folder is null)
        {
            ShowNoCover(null);
            return;
        }

        var cancel = new CancellationTokenSource();
        _libArtCancel = cancel;

        try
        {
            var bytes = await AlbumCovers.LoadForSquareAsync(folder, cancel.Token, LibArtEdge);

            // Is this still the record on screen? The stored folder is checked, not the cancellation token.
            // A read that finished just before the cancellation can still hold a valid picture, but of the
            // wrong album.
            if (_libArtFolder != folder)
                return;

            if (bytes is not { Length: > 0 })
            {
                ShowNoCover(folder);
                return;
            }

            var picture = await RemoteImage.DecodeAsync(bytes);
            if (_libArtFolder != folder)
                return;

            LibArt.Source = picture;
            LibArtFigure.Background = null;
            LibArtMark.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            // A newer record is being read, and that read will draw its own cover.
        }
        catch
        {
            // A cover that cannot be decoded is treated as no cover.
            if (_libArtFolder == folder)
                ShowNoCover(folder);
        }
    }

    /// <summary>
    /// The square shown for a record with no artwork: a picture made from its own folder, with the record
    /// mark on top.
    /// </summary>
    /// <remarks>
    /// The app's own placeholder (<see cref="Converters.SleeveFigureConverter"/>) is used, and no second
    /// one is made here. The album wall, the list, the drawer and the album page all draw this same
    /// placeholder, so one record looks the same wherever it is shown. This does not say whether the record
    /// has a cover. That is still the answer from the loader.
    ///
    /// <para>With no folder, as for an empty list, the mark is drawn on the bare card. There is no record
    /// to make a picture of.</para>
    /// </remarks>
    private void ShowNoCover(string? folder)
    {
        LibArt.Source = null;
        LibArtFigure.Background = folder is { Length: > 0 }
            ? Converters.SleeveFigureConverter.For(folder)
            : null;
        LibArtMark.Visibility = Visibility.Visible;
    }

    /// <summary>A row of the tree was clicked: it opens a list, or it opens and closes.</summary>
    private void NavPicked()
    {
        if (_libModel is { } model && Picked(LibNavGrid, model.LibNavRows) is AmpNode node)
            model.PickNode(node);
    }

    /// <summary>The arrow on a heading. It always opens or closes the heading. It is not a pick, so a
    /// repeat press still counts.</summary>
    private void NavExpanderPressed(int row)
    {
        if (_libModel is { } model && model.LibNavRows.Peek(row) is AmpNode node)
            model.PickNode(node);
    }

    private void ArtistPicked()
    {
        if (_libModel is { } model && Picked(LibArtistGrid, model.LibArtistRows) is AmpBrowseRow row)
            model.PickArtist(row);
    }

    private void AlbumPicked()
    {
        if (_libModel is { } model && Picked(LibAlbumGrid, model.LibAlbumRows) is AmpBrowseRow row)
            model.PickAlbum(row);
    }

    /// <summary>Double-click plays the song, and the rest of what is listed queues behind it.</summary>
    private void TrackActivated(int row)
    {
        if (_libModel is { } model && model.LibTrackRows.Peek(row) is LibraryRow track)
            model.PlayFromLibrary(track);
    }

    /// <summary>
    /// A right-click in the song list clears the selection. It does not pick a row.
    /// </summary>
    /// <remarks>
    /// The two buttons at the foot act on the selection. When nothing is selected, they act on the whole
    /// list. Without a way to clear the selection, the first click in the list would leave the buttons
    /// stuck on one song.
    ///
    /// <para>This applies only to this pane. In the library table and the import queue, a right-click opens
    /// a menu that acts on the selection. Clearing it there would leave every menu acting on nothing. So the
    /// grid moves the selection to the row under the pointer, as every list does, and this pane undoes that.
    /// The grid itself is not changed for the other lists.</para>
    /// </remarks>
    private void TrackContextRequested(int row, Point at)
    {
        LibTrackGrid.Selection.Clear();
        LibTrackGrid.Invalidate();
    }

    private void TrackHeaderClicked(GridColumn column) => _libModel?.SortLibraryBy(column.Id);

    /// <summary>The header's menu: the sort, and which columns the list shows.</summary>
    private void TrackHeaderMenu(GridColumn column, Point at)
    {
        if (_libModel is not { } model)
            return;

        GridHeaderMenu.Show(LibTrackGrid, column, at, new GridHeaderMenu.Actions(
            SortBy: (id, descending) => model.SortLibraryTo(id, descending),
            SortedColumn: model.LibSort?.Column,
            SortedDescending: model.LibSort?.Descending ?? false,

            // This list has no column filters. The search box above it is the only way to narrow the list.
            // A per-column filter would be a second answer to the same question.
            Filter: null,
            ClearFilter: null,
            IsFiltered: _ => false,
            IsOptional: IsOptionalTrackColumn));
    }

    /// <summary>Whether a song column can be hidden. The playing mark cannot be hidden, because it has no
    /// header that the menu could show again. It is how the list marks the song that is playing.</summary>
    private static bool IsOptionalTrackColumn(GridColumn column) => column.Id != "playing";

    /// <summary>The columns button above the list. It opens the header menu from the toolbar, where Winamp
    /// keeps it.</summary>
    private void LibColumns_Click(object sender, RoutedEventArgs e)
    {
        if (_libTrackColumns?.All.FirstOrDefault(c => c.Header.Length > 0) is { } column)
            TrackHeaderMenu(column, new Point(0, 0));
    }

    private void LibSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_libModel is { } model)
            model.LibSearch = LibSearchBox.Text;
    }

    private void LibClearSearch_Click(object sender, RoutedEventArgs e) => LibSearchBox.Text = string.Empty;

    /// <summary>The songs picked become the queue and start playing.</summary>
    private void LibPlay_Click(object sender, RoutedEventArgs e) => ActOnChosen(enqueue: false);

    /// <summary>The songs picked go on the end of the queue, and what is playing keeps playing.</summary>
    private void LibAdd_Click(object sender, RoutedEventArgs e) => ActOnChosen(enqueue: true);

    /// <summary>
    /// What the two buttons at the foot act on: the songs picked in the list, or every song listed
    /// when nothing is picked.
    /// </summary>
    /// <remarks>
    /// Decided here and not in the model, because the selection belongs to the grid. The model is given the
    /// rows, so it never has to guess which case applies. "Everything" is passed as a list of every index,
    /// not as an empty list that the model would have to interpret.
    /// </remarks>
    private void ActOnChosen(bool enqueue)
    {
        if (_libModel is not { } model)
            return;

        IReadOnlyList<int> rows = LibTrackGrid.Selection.IsEmpty
            ? [.. Enumerable.Range(0, model.LibTrackCount)]
            : [.. LibTrackGrid.Selection.Rows()];

        model.PlayRows(rows, enqueue);
    }

    private static bool Same(string? a, string? b) =>
        string.Equals(a, b, StringComparison.CurrentCultureIgnoreCase);
}
