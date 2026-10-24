using RPM.Core.Cloud;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The three questions asked of the wall: what am I looking for, what order is it in, and only the ones I
/// love.
///
/// <para>The same three the library window's header carries, and deliberately the same answers: the search
/// is the mirror's own (one indexed query, narrowed in SQL rather than by walking the wall), the orders are
/// <see cref="AlbumSort"/>'s, shared with that window and with the phone, and the heart reads the same
/// <c>album_meta</c> rows the sleeve sticker does. Nothing here decides anything the rest of the app has
/// already decided somewhere else.</para>
///
/// <para>All three land the same way: they change one field and ask the wall to fill again. The fill reuses
/// the cards it already has (see BuildLibraryCards), so narrowing a collection and widening it back is a
/// re-chunk and a sort rather than a re-read, which is what lets the search answer on every keystroke.
/// </para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>What the listener has typed, or empty. Held rather than read off the box: the box is
    /// debounced, and the fill can run from a sort or a heart while a keystroke is still
    /// settling.</summary>
    private string _librarySearch = "";

    /// <summary>The order the wall is in: the one chosen last time, kept across launches as the Android TV
    /// app keeps its own (see <see cref="SavedLibraryOrdering"/>). Artist on a first visit, which is the
    /// order a shelf is in and the order the window's wall opens on.</summary>
    private AlbumOrdering _libraryOrdering = SavedLibraryOrdering();

    /// <summary>The orders the sort key offers, in the menu's order. The window's Year order is not one of
    /// them: the TV screens sort by date the other way round, oldest first.</summary>
    private static readonly AlbumOrdering[] LibraryOrderings =
        [AlbumOrdering.Album, AlbumOrdering.Artist, AlbumOrdering.Added, AlbumOrdering.Date, AlbumOrdering.Plays];

    /// <summary>The order saved by the last choice on the sort key, in settings.json under its own name
    /// (<see cref="SettingsService.PegasusAlbumOrdering"/>), not the window's. An order this wall does not
    /// offer is read as Artist.</summary>
    private static AlbumOrdering SavedLibraryOrdering()
    {
        var saved = App.Services.GetRequiredService<SettingsService>().PegasusAlbumOrdering;
        return Array.IndexOf(LibraryOrderings, saved) >= 0 ? saved : AlbumOrdering.Artist;
    }

    /// <summary>Whether the wall is narrowed to the records the listener has loved.</summary>
    private bool _libraryFavoritesOnly;

    /// <summary>Whether the search field has the keys. The host's own shortcuts, space to play, the
    /// number keys to cycle compositions, must not fire into a name being typed.</summary>
    private bool _librarySearchFocused;

    // ── the search ──

    /// <summary>
    /// A keystroke. Not acted on at once: otherwise a question is asked of the mirror per keystroke, and
    /// typing five letters into a large collection would start five reads for four answers nobody will
    /// see. A short wait after the LAST keystroke suits a normal typing speed and is still quick.
    /// </summary>
    private void LibrarySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _librarySearch = LibrarySearch.Text ?? "";

        _librarySearchSettle ??= CreateSearchSettleTimer();
        _librarySearchSettle.Stop();
        _librarySearchSettle.Start();
    }

    /// <summary>How long the field must be still before the question is asked.</summary>
    private static readonly TimeSpan LibrarySearchSettle = TimeSpan.FromMilliseconds(220);

    private DispatcherQueueTimer? _librarySearchSettle;

    private DispatcherQueueTimer CreateSearchSettleTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = LibrarySearchSettle;
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            RefillLibrary();
        };
        return timer;
    }

    private void LibrarySearch_FocusChanged(object sender, RoutedEventArgs e)
        => _librarySearchFocused = LibrarySearch.FocusState != FocusState.Unfocused;

    /// <summary>Whether a field on the collection has the keys, so the host can leave the keyboard alone
    /// while somebody is typing into it. The drawer's own shelf answers the same question.</summary>
    public bool IsLibrarySearchFocused => (WallUp && !RecordUp && _librarySearchFocused) || ArtistsSearchHasKeys;

    /// <summary>Takes the keys back off the search field, leaving what was typed. What Escape does while
    /// somebody is in the box, the way out of the field rather than out of the screen.</summary>
    public void DropLibrarySearchFocus()
    {
        if (ArtistsSearchHasKeys)
        {
            DropArtistsSearchFocus();
            return;
        }
        if (!_librarySearchFocused)
            return;
        // Moved to the wall rather than merely cleared: focus taken off an element with nowhere to go
        // lands back on it, and the field would keep the keys it was just asked to give up.
        LibraryWall.Focus(FocusState.Programmatic);
        _librarySearchFocused = false;
    }

    // ── the order ──

    /// <summary>
    /// The order the wall is in: by the record's name, by whose record it is, by when it arrived, by when
    /// it came out (oldest first), or by how much it has been listened to. The choice is saved, so the wall
    /// opens in it on the next launch.
    ///
    /// <para>A menu rather than a row of chips, exactly as in the window and on the phone: it is a choice
    /// made rarely, and four chips across the top of a wall of album art would always be in the way.</para>
    /// </summary>
    private void LibrarySort_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };

        // At the wall's text scale, read when the menu opens: the scale the mini player and the tiles are
        // drawn at (see PegasusTileMetrics.Scale).
        var scale = QueueScale;

        // A glyph each, and the one in force wears a tick. The icons are what makes the list readable at
        // a glance: four words in a column look alike. The same glyphs the window's own menu uses.
        Add(AlbumOrdering.Album, "AlbumSort_Album", "");     // the record itself
        Add(AlbumOrdering.Artist, "AlbumSort_Artist", "");   // a person
        Add(AlbumOrdering.Added, "AlbumSort_Added", "");     // a date, with something new on it
        Add(AlbumOrdering.Date, "AlbumSort_Date", "\uf073");   // a calendar
        Add(AlbumOrdering.Plays, "AlbumSort_Plays", "");     // headphones

        menu.ShowAt(LibrarySortButton);

        void Add(AlbumOrdering ordering, string key, string glyph)
        {
            var chosen = ordering == _libraryOrdering;
            var item = new MenuFlyoutItem
            {
                Text = Strings.Loc.Get(key),
                FontSize = 12 * scale,
                MinHeight = 26 * scale,
                Padding = new Thickness(11 * scale, 3 * scale, 11 * scale, 4 * scale),
                // The one in force shows a tick instead of its own glyph, a checkable item would make
                // the whole menu reserve a check gutter left of every icon.
                Icon = new FontIcon
                {
                    FontFamily = (FontFamily)Resources["PegasusFa"],
                    FontSize = 12 * scale,
                    Glyph = chosen ? "" : glyph,
                },
            };
            item.Click += (_, _) =>
            {
                if (_libraryOrdering == ordering)
                    return;   // the order it is already in is not a change to re-chunk the wall for
                _libraryOrdering = ordering;
                App.Services.GetRequiredService<SettingsService>().PegasusAlbumOrdering = ordering;
                RefillLibrary();
            };
            menu.Items.Add(item);
        }
    }

    // ── the heart ──

    private void LibraryFavorites_Click(object sender, RoutedEventArgs e)
    {
        _libraryFavoritesOnly = !_libraryFavoritesOnly;
        PaintLibraryFavorites();
        RefillLibrary();
    }

    /// <summary>Fills the heart in, or hollows it out, the same pair of weights the sleeve sticker and a
    /// song's own line are drawn with, so one mark means one thing across the app.</summary>
    private void PaintLibraryFavorites()
    {
        LibraryFavoritesIcon.FontFamily =
            (FontFamily)Resources[_libraryFavoritesOnly ? "PegasusFa" : "PegasusFaRegular"];
        LibraryFavoritesIcon.Foreground = (Brush)Application.Current.Resources[
            _libraryFavoritesOnly ? "FavoriteMarkBrush" : "FavoriteMarkIdleBrush"];
    }

    /// <summary>Asks the wall again with whatever the three tools currently say. Everything above lands
    /// here; nothing above knows how the wall is built.</summary>
    private void RefillLibrary()
    {
        if (!WallUp)
            return;   // nothing is looking at the wall; ShowLibrary fills it with these same three answers
        FillLibraryAsync();
    }

    // ── the ring ──
    //
    // Shown only when the collection actually takes a moment. A search is one indexed query and usually
    // lands inside a frame, so a ring switched on at the start of every read would flash on every
    // keystroke, which looks like the screen struggling and not like the screen working.

    /// <summary>How long a read may take before the listener is told it is happening.</summary>
    private static readonly TimeSpan LibraryBusyDelay = TimeSpan.FromMilliseconds(200);

    private DispatcherQueueTimer? _busyTimer;
    private bool _busyUp;

    /// <summary>Starts the clock. If the read answers first, <see cref="HideLibraryBusy"/> stops it and
    /// nothing is ever shown.</summary>
    private void ShowLibraryBusyLater()
    {
        _busyTimer ??= CreateBusyTimer();
        _busyTimer.Stop();
        _busyTimer.Start();
    }

    private DispatcherQueueTimer CreateBusyTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = LibraryBusyDelay;
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!WallUp)
                return;
            _busyUp = true;
            LibraryBusy.Visibility = Visibility.Visible;
            LibraryBusyRecord.Spinning = true;
            _libraryBusyFade.To(1, LibraryBusyFadeMs, null);
        };
        return timer;
    }

    /// <summary>The answer is in. The ring fades rather than switching off: a spinner that vanishes in
    /// the frame the covers arrive looks like a glitch, and one that fades looks like the wall taking
    /// its place.</summary>
    private void HideLibraryBusy()
    {
        _busyTimer?.Stop();
        if (!_busyUp)
            return;
        _busyUp = false;

        _libraryBusyFade.To(0, LibraryBusyFadeMs, () =>
        {
            if (_busyUp)
                return;
            LibraryBusy.Visibility = Visibility.Collapsed;
            LibraryBusyRecord.Spinning = false;   // a turning record is a timer, collapsed or not
        });
    }

    private const int LibraryBusyFadeMs = 220;
}
