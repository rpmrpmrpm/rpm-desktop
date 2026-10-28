using RPM.Core.Library;
using RPM.Core.Models;
using RPM.Desktop.Presentation.Main.LeftPane;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RPM.Desktop.Presentation.Grids;

namespace RPM.Desktop.Presentation.Main;

/// <summary>
/// Putting the screen back the way it was left, and writing down where it is as the user works.
///
/// <para>What is restored here is the SESSION: the pane that was open, what was in the search box,
/// whether the shelf was narrowed to the favourites, how far down each view was, the table's sort and
/// its column widths, and the queue that was waiting. The user's CHOICES
/// (which view, cover size, ordering, grouping) are settings and are restored by their own owners; see
/// <see cref="SessionService"/> for why the two are kept apart.</para>
///
/// <para><b>Nothing stored is trusted.</b> Between two runs a file is deleted, a library is pointed
/// somewhere else, a record is offloaded, a friend's computer is gone, a column is renamed, a search
/// now matches nothing. Every value is checked against what is actually here and dropped where it is
/// not (see <see cref="SessionRestore"/>). A restore that cannot be honoured leaves the default
/// standing, which is the state a fresh install opens in and is always valid.</para>
/// </summary>
public sealed partial class MainPage
{
    private static SessionService Session => App.Services.GetRequiredService<SessionService>();

    /// <summary>The page is attached on every navigation back to the library, and it is built afresh
    /// each time. The session is put back only once, on the first of them, so the flag is static: it
    /// belongs to the app's launch, not to a page. Coming back to the library from setup or a shoot is
    /// not the app opening. Applying a stored search again at that point would undo whatever the user
    /// had just done.</summary>
    private static bool _sessionRestored;

    /// <summary>Set while the stored state is being written into the page, so the writers below do not
    /// read the restore itself as the user having changed something.</summary>
    private bool _restoringSession;

    /// <summary>Puts the stored session back. Called from <see cref="AttachSources"/>, which is the
    /// first moment the page has both its model and its controls, and before the album views are fed,
    /// so the shelf is built once, already narrowed to whatever the session says.
    /// </summary>
    private void RestoreSession()
    {
        if (_sessionRestored || Model is not { } model)
            return;
        _sessionRestored = true;

        var session = Session;

        _restoringSession = true;
        try
        {
            model.SearchText = SessionRestore.Search(session.LibrarySearch);
            _favoritesOnly = session.FavoritesOnly;   // painted by AttachAlbumGrid's SetFavoritesToggle

            // The queue that Import works on is a temp table: it went with the process that wrote it, so
            // a pane restored onto Import would open on an empty workbench with nothing to explain it.
            // Track info is where the pane opens instead, the same place it opens on a fresh install.
            var tab = SessionRestore.Enum<LeftTab>(session.LeftTab) ?? LeftTab.TrackInfo;
            if (tab == LeftTab.Import)
                tab = LeftTab.TrackInfo;

            // The library opens on the view it was left on. If the stored name no longer means
            // anything, such as after a rail rename or on a first run, Albums is used.
            var reading = SessionRestore.Enum<RightTab>(session.RightTab) ?? RightTab.Albums;

            Panes.Restore(session.LeftPaneOpen, tab, reading,
                          SessionRestore.Split(session.Split.Left, session.Split.Right));
            RestoreTableColumns(session);
        }
        finally
        {
            _restoringSession = false;
        }

        // Home is restored first, at the ordinary priority.
        //
        // Home is what the app opens on, and the rest of the restore runs at low priority. If home
        // ran last, the collection would be drawn and its wall laid out first, then covered by home,
        // and home's own reading would wait behind that work. Running home first lets that reading
        // start at once.
        DispatcherQueue?.TryEnqueue(RestoreHome);

        // And the rest after it, still Low: none of it is on screen while home is, and the queue in
        // particular is a resolve per track against the library that is open now.
        DispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            RestoreQueue(session);
            RestoreAppView(session);
        });
    }

    // ── the library header: the search box and the favourites heart ──

    /// <summary>The search box, written down as it is typed. Debounced in the service, so a fast typist
    /// is one file write.</summary>
    private void RememberSearch()
    {
        if (!_restoringSession && Model is { } model)
            Session.LibrarySearch = model.SearchText;
    }

    /// <summary>Whether the shelf was narrowed to the favourites.</summary>
    private void RememberFavoritesOnly()
    {
        if (!_restoringSession)
            Session.FavoritesOnly = _favoritesOnly;
    }

    // ── the record that was open ──

    /// <summary>
    /// The record that was open is NOT put back, and neither is anything else that was on screen.
    /// </summary>
    /// <remarks>The app opens on home, with nothing behind it. See <see cref="RestoreHome"/>.
    ///
    /// <para>No folder for the open record is stored. A stored value that nothing reads goes wrong
    /// quietly, and a folder written on every opened record would be exactly that.</para>
    ///
    /// <para>What is still restored is not something OPEN: the way the collection is filed, the pane's
    /// tab and split, the search text, the table's columns, and the queue that was waiting. None of
    /// those puts a page on screen, and home covers all of them until the reader leaves it.</para>
    /// </remarks>

    // ── the home screen ──

    /// <summary>
    /// Home, every time the app opens.
    /// </summary>
    /// <remarks><b>Home always opens, whatever was up when the app closed.</b> The app does not reopen
    /// the record or the view that was on screen before.
    ///
    /// <para>No flag for this is stored. The one place that once read a stored flag no longer asks, and
    /// a value nothing reads goes wrong quietly.</para>
    ///
    /// <para>Home is shown after the pane and table restores, so it covers whatever they put up. Home
    /// does not change either pane on the way in. What those restored is what the reader finds when
    /// they leave home. Home's rows are read on the way in, not restored as a pane. They show what has
    /// been played, and a home screen showing last session's listening would be wrong. See ShowHome,
    /// and HomeView.Stale, which is also true on a first run.</para>
    /// </remarks>
    private void RestoreHome()
    {
        // Said because the reveal waits on what this starts: home's reading is the last thing between
        // the splash and the screen, and the gap between the attach ending and this line is the queue
        // ahead of it. Read with: rpmscope log --channel boot
        RPM.Core.Diagnostics.Scope.Said("boot", "home asked");
        ShowHome(true);
    }

    // ── how far down each view was ──

    /// <summary>Which views have already been put back where they were. Per view, because each is
    /// restored the first time it is actually shown. The two that are not on screen at startup have
    /// nothing laid out to scroll.</summary>
    private readonly HashSet<LibraryViewMode> _scrollRestored = new();

    /// <summary>Which views are already reporting their scrolling.</summary>
    private readonly HashSet<LibraryViewMode> _scrollWatched = new();

    private ScrollViewer? ScrollHostFor(LibraryViewMode mode) => mode switch
    {
        LibraryViewMode.Wall => AlbumWall?.ScrollHost,
        LibraryViewMode.List => AlbumList?.ScrollHost,
        _ => null,
    };

    private double StoredScrollFor(LibraryViewMode mode) => mode switch
    {
        LibraryViewMode.Wall => Session.WallScroll,
        LibraryViewMode.List => Session.ListScroll,
        _ => Session.TableScroll,
    };

    private void RememberScroll(LibraryViewMode mode, double offset)
    {
        if (_restoringSession)
            return;
        var session = Session;
        switch (mode)
        {
            case LibraryViewMode.Wall: session.WallScroll = offset; break;
            case LibraryViewMode.List: session.ListScroll = offset; break;
            default: session.TableScroll = offset; break;
        }
    }

    /// <summary>
    /// Takes a view back to where it was left, and starts listening to it.
    ///
    /// <para>Called as each view is shown for the first time, which is the only moment its offset means
    /// anything: a collapsed view has no laid-out content and every offset in it is zero.</para>
    /// </summary>
    private void RestoreScrollFor(LibraryViewMode mode)
    {
        // The first showing carries the stored offset; later ones only make sure the view is being
        // listened to. Both go through the same settle, because a view's scroller is inside a template
        // that may not have been built yet on the frame the view is shown.
        var stored = _scrollRestored.Add(mode) ? StoredScrollFor(mode) : 0;
        _ = SettleScrollAsync(mode, stored);
    }

    /// <summary>The shelf arrives over several frames (the skeleton query lands, the cards are built,
    /// the rows are measured), so where it can be scrolled TO is not known on the frame the view is
    /// shown. This asks again for as long as that reasonably takes and then gives up: a view that never
    /// grows a scrollbar is a shelf that has shrunk since (a search still in force, records deleted),
    /// and sitting at the top of it is the right answer.
    /// </summary>
    private async Task SettleScrollAsync(LibraryViewMode mode, double stored)
    {
        for (var attempt = 0; attempt < 15; attempt++)
        {
            await Task.Delay(120);

            if (_view != mode)
                return;   // the user moved to another view; theirs is the state that counts now

            if (ScrollHostFor(mode) is not { } host)
                continue;   // the scroller lives in a template that has not been built yet

            EnsureScrollWatched(mode);

            if (stored <= 0)
                return;   // nothing to put back; this pass was only here to start listening

            if (host.ScrollableHeight <= 0)
                continue;   // the shelf is still filling in, so there is nowhere to scroll to yet

            var offset = SessionRestore.Scroll(stored, host.ScrollableHeight);
            if (offset > 0)
                host.ChangeView(null, offset, null, disableAnimation: true);
            return;
        }
    }

    /// <summary>Subscribes a view's scroller, once it exists, so the offset is written down as the user
    /// reads. Only the settled offset: a fling raises this on every frame of its deceleration.</summary>
    private void EnsureScrollWatched(LibraryViewMode mode)
    {
        if (_scrollWatched.Contains(mode) || ScrollHostFor(mode) is not { } host)
            return;
        _scrollWatched.Add(mode);
        host.ViewChanged += (_, e) =>
        {
            if (!e.IsIntermediate)
                RememberScroll(mode, host.VerticalOffset);
        };
    }

    // ── the data table: its sort and its column widths ──

    /// <summary>
    /// Puts back the width each column was dragged to and the sort the table was in.
    ///
    /// <para>Keyed by the column's id, which IS its name in the mirror, rather than by its position:
    /// a column added, removed or moved since the file was written costs nothing, because an unknown
    /// key is ignored and a column with no stored width keeps the one its declaration gives it.</para>
    /// </summary>
    private void RestoreTableColumns(SessionService session)
    {
        if (LibraryGridView.Columns is not { } columns)
            return;

        var widths = session.TableColumnWidths;
        var hidden = new HashSet<string>(session.TableHiddenColumns, StringComparer.Ordinal);

        foreach (var column in columns.All)
        {
            if (widths.TryGetValue(column.Id, out var stored) && SessionRestore.ColumnWidth(stored) is { } width)
                column.Width = width;

            if (LibraryGrid.IsOptional(column))
                column.IsVisible = !hidden.Contains(column.Id);
        }

        columns.Invalidate();

        // One event for every shape change (a width dragged, a column hidden, the order moved),
        // rather than a property-changed callback registered per column.
        columns.Changed += RememberTableColumns;

        RestoreTableSort(session);
    }

    private void RestoreTableSort(SessionService session)
    {
        var (stored, descending) = session.TableSort;
        if (SessionRestore.SortColumn(stored, key => TrackTableColumns.Sql(key) is not null) is not { } key)
            return;

        if (LibraryGridView.Columns?.ById(key) is null)
            return;   // the column that sort belonged to is not on this table any more

        SortLibraryBy(key, descending);
    }

    private void RememberTableSort()
    {
        if (_restoringSession)
            return;

        Session.TableSort = (_librarySort, _librarySortDescending);
    }

    private void RememberTableColumns()
    {
        if (_restoringSession || LibraryGridView.Columns is not { } columns)
            return;

        var widths = new Dictionary<string, double>(StringComparer.Ordinal);
        var hidden = new List<string>();

        foreach (var column in columns.All)
        {
            if (column.Width > 0)
                widths[column.Id] = column.Width;

            if (!column.IsVisible)
                hidden.Add(column.Id);
        }

        Session.TableColumnWidths = widths;
        Session.TableHiddenColumns = hidden;
    }

    // ── which of the three views the window was in ──

    /// <summary>
    /// The view the app was left in.
    ///
    /// <para>Only the player is put back. The full-screen view shows something that is PLAYING, and the
    /// app opens playing nothing. A full-screen view of an empty deck is not where anybody left off. So
    /// that view is not restored. The app opens on the library, which is where closing any view goes
    /// anyway.</para>
    /// </summary>
    private static void RestoreAppView(SessionService session)
    {
        if (SessionRestore.Enum<AppView>(session.View) is AppView.RPaMp)
            App.Services.GetRequiredService<ViewModeService>().Switch(AppView.RPaMp);
    }

    private static void RememberAppView(AppView view)
        => Session.View = view.ToString();

    // ── the queue ──

    /// <summary>Puts back the queue that was waiting, with the needle where it was left, and starts
    /// nothing.
    ///
    /// <para>The stored paths are resolved against the library that is open NOW, one at a time: a track
    /// deleted, a library pointed at another folder, or a queue built out of a friend's shelf simply
    /// resolves to nothing and is left out. A track that is in the mirror but not on this disk (one
    /// that was offloaded to the cloud) resolves fine, because the mirror is what answers for it and
    /// the player streams it exactly as it would have done before the app was closed.</para>
    /// </summary>
    private static void RestoreQueue(SessionService session)
    {
        var library = App.Services.GetRequiredService<Services.Library.LibraryService>();

        // Somebody else's shelf is not our queue to put back, and a library that is not open cannot
        // answer for a single path in it.
        if (!library.IsOpen || library.IsPeerView || library.Root is not { } root)
            return;

        var tracks = SessionRestore.Queue(session.Queue, path => Resolve(path));
        if (tracks.Count == 0)
        {
            if (session.Queue.Count > 0)
                session.SetQueue([], -1, null, 0);   // none of it is here any more; stop carrying it
            return;
        }

        var cursor = SessionRestore.Cursor(
            session.QueueIndex, session.QueueSeconds, session.QueueTrack,
            [.. tracks.Select(t => t.FilePath)]);

        var player = App.Services.GetRequiredService<RPaMpService>().Transport;
        player.Shuffle = session.Shuffle;
        player.Repeat = session.Repeat;
        player.RestoreQueue(tracks, cursor.Index, cursor.Seconds, session.QueueRadio);

        TrackInfo? Resolve(string absolute)
        {
            if (string.IsNullOrEmpty(absolute))
                return null;   // a saved entry with no path is a track that does not come back

            var relative = Path.GetRelativePath(root, absolute).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                return null;   // outside the library this machine is pointed at

            // A failed read is a track that does not come back. A refusal has put the problem screen up
            // over the app (LibraryService.Read).
            return library.Read(library.Db, "a saved queue's track", db => db.FindByRelativePath(relative))
                .Match(row => row?.ToTrackInfo(root), _ => null);
        }
    }
}
