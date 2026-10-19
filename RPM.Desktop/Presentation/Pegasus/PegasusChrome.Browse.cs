using RPM.Core.Library;
using RPM.Core.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The drawer's second screen: the listener's playlists and the library's records, one step back from the
/// queue.
///
/// <para>The queue answers "what is coming". This screen answers "what else is there". The two are the same
/// question asked a second apart, so they are two screens of one drawer, not two panels. The arrow in the
/// corner is the whole of the navigation: back to here, forward to the queue. Picking something plays it
/// and returns, because once a record has been chosen there is nothing left to ask on this screen.</para>
///
/// <para>Nothing is held between openings. Both lists are read from the library database when the screen is
/// shown, and thrown away when the drawer closes. A full-screen visualizer is not a second library window.
/// A cached shelf would be one more thing to keep current while somebody imports music in the window behind
/// it.</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>
    /// The three screens of the drawer, in the order they stack.
    ///
    /// <para>A stack rather than a set of tabs. Each screen is one step further from what is playing now,
    /// and the arrow in the corner walks the stack. The queue is where the drawer opens. The shelf is one
    /// step back from it, and a record is one step into the shelf.</para>
    /// </summary>
    private enum DrawerScreen
    {
        /// <summary>What is coming.</summary>
        Queue,

        /// <summary>What else there is: the playlists, or the records.</summary>
        Browse,

        /// <summary>One record, and what is on it.</summary>
        Album,
    }

    /// <summary>Which screen the drawer is showing. The queue, always, when it opens.</summary>
    private DrawerScreen _screen = DrawerScreen.Queue;

    /// <summary>…and which of the two lists the browse screen is showing. Playlists first: they are the
    /// shorter list and the one somebody made on purpose.</summary>
    private bool _browseAlbums;

    private Fader _queueListFade = null!;
    private Fader _browseListFade = null!;
    private Fader _albumListFade = null!;
    private Fader _albumBackFade = null!;

    /// <summary>How long one screen takes to become another.</summary>
    private const int ScreenCrossMs = 160;

    /// <summary>The number of the read that filled the list. Every rebuild takes the next number. A read
    /// that comes back from the disk to find that it is no longer current drops what it found. That happens
    /// when the tab is switched, the drawer is closed, or the window is resized while the database is being
    /// asked.</summary>
    private int _browseRead;

    /// <summary>The scale the drawer's hand-built screens were laid out at, for the shelf and the open
    /// record. Like the queue, they keep their sizes from the moment they are built. So a window that has
    /// grown under them needs them built again.</summary>
    private double _drawerBuiltAt;

    /// <summary>How many records the shelf will draw. These rows are built by hand and not virtualized, the
    /// same as the queue. A collection is not a queue, though. Two thousand records would be two thousand
    /// borders laid out in front of a shader. Records over the line are stated at the foot of the list, so
    /// they are not quietly missing.</summary>
    private const int BrowseAlbumCap = 500;

    /// <summary>Everything on a shelf row that is not the name, in points: the sleeve and its margin, the E
    /// and its gap, the row's padding, the list's own margins and the room the scrollbar takes. What is
    /// left of the drawer's width after this is what a name may use. It is scaled with the row, because
    /// every one of those pieces is. The queue's own rows use the same figure (see
    /// QueueRowFurniture).</summary>
    private const double BrowseRowFurniture = 132;

    /// <summary>The name of every row on the shelf, so the drawer can cap them when it is sized. This is
    /// the same list the queue keeps of its own titles, for the same reason. The name is the one part of a
    /// row that must change when the window does.</summary>
    private readonly List<TextBlock> _browseTitles = [];

    /// <summary>What a record's name may take before it trims, at the width the drawer is currently drawn
    /// at. A panel that has not been sized yet answers the width it opens at.</summary>
    private double BrowseTitleMax(double scale) =>
        Math.Max(60, (double.IsNaN(QueuePanel.Width) || QueuePanel.Width <= 0 ? 320 : QueuePanel.Width)
                     - BrowseRowFurniture * scale);

    /// <summary>Where the drawer's rows come from. This is read here rather than passed in. The control is
    /// given a transport (see SetTransport), because the transport is the one thing it must not have a
    /// second opinion about. The collection is a service that anybody may ask.</summary>
    private static LibraryService Library => App.Services.GetRequiredService<LibraryService>();

    // One lit row.
    //
    // Every row in this drawer is a Border whose wash is set from here. It is never a Button wearing the
    // framework's own pointer-over state. That state had two faults, and only one of them was the colour.
    //
    // The colour: a Button's hover comes from a theme brush, and the theme these panels are hosted in is
    // not the near-black they are painted in. So the wash appeared as a pale shape drawn for a light
    // surface, on the darkest card in the app.
    //
    // The ghosting: a pointer held still while a LIST MOVES under it enters row after row and is never told
    // that it has left one. So every row the list moved past stayed lit, and the panel filled up with a
    // trail of them. Hover belongs to the DRAWER, which has only one pointer in it. It is held as one
    // field, so lighting a row also puts out the last one. No row can be left lit by an event that never
    // came.

    /// <summary>The row the pointer is on, anywhere in the drawer, or none.</summary>
    private Border? _litRow;

    /// <summary>Lights one row and puts out whichever was lit before it. Null puts the drawer
    /// out.</summary>
    private void LightRow(Border? row)
    {
        if (ReferenceEquals(_litRow, row))
            return;

        if (_litRow is not null)
            PaintDrawerRow(_litRow, lit: false);
        _litRow = row;
        if (row is not null)
            PaintDrawerRow(row, lit: true);
    }

    /// <summary>One row's wash. A queue row knows something the others do not, whether it is the record
    /// playing, so it keeps its own painter.</summary>
    private void PaintDrawerRow(Border row, bool lit)
    {
        if (row.Tag is PlaylistTrack)
        {
            PaintRow(row, hovered: lit);
            return;
        }

        // The list chosen in the sheet keeps its mark when the pointer leaves it. Hover comes and goes, and
        // a choice that went out with it would leave a panel that had forgotten what it was told.
        if (!lit && ReferenceEquals(row, _sheetPickRow))
        {
            PaintSheetPick(row, chosen: true);
            return;
        }

        row.Background = new SolidColorBrush(lit
            ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF));
    }

    private void DrawerRow_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border row && _dragRow is null)
            LightRow(row);
    }

    private void DrawerRow_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border row && ReferenceEquals(_litRow, row))
            LightRow(null);
    }

    /// <summary>
    /// The list moved under the pointer, so nothing is lit until the pointer says otherwise.
    ///
    /// <para>This is the other half of the fix. A scroll changes which row is under the pointer without the
    /// pointer moving at all. The row that was under it has no way of hearing about that. So the lit row is
    /// put out on the scroll, and lit again by the next pointer move, which is how every list
    /// behaves.</para>
    /// </summary>
    private void Drawer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        LightRow(null);
        UpdateTopFade();   // …and how much of the record is under the head now
    }

    // The three screens.

    /// <summary>
    /// Crosses from whichever screen is up to another.
    ///
    /// <para>All three stay in the tree throughout. Only their opacity changes. The alternative would be to
    /// build the screen on the way in. That puts a layout pass over a hundred rows in front of the shader,
    /// at the exact moment the eye is on the dissolve. That is the one frame in the whole interaction that
    /// cannot afford one.</para>
    /// </summary>
    private void ShowScreen(DrawerScreen to)
    {
        if (_screen == to)
            return;

        var from = _screen;
        _screen = to;
        LightRow(null);

        // A drag cannot survive the queue leaving the screen. The row it is carrying is about to fade out
        // from under the pointer. A reorder finished blind would edit a list the listener did not mean to
        // change.
        if (from == DrawerScreen.Queue)
        {
            CancelDrag();
            EndQueueMove(place: false);   // …and a row the keys were carrying is put back
        }

        Cross(QueueScroll, _queueListFade, DrawerScreen.Queue);
        Cross(BrowseScroll, _browseListFade, DrawerScreen.Browse);
        Cross(AlbumScroll, _albumListFade, DrawerScreen.Album);

        // The record's wash is crossed with the screen it belongs to, but not through Cross. Cross also
        // gives the screen the pointer, and this one must never take it. The picture goes with the fade. A
        // wash left loaded is a full-panel bitmap held for a screen nobody is on.
        AlbumBackdrop.Visibility = Visibility.Visible;
        _albumBackFade.To(to == DrawerScreen.Album ? 1 : 0, ScreenCrossMs, () =>
        {
            if (_screen == DrawerScreen.Album)
                return;
            AlbumBackdrop.Visibility = Visibility.Collapsed;
            AlbumBackdropArt.ImageSource = null;
        });

        ApplyHead();

        // Whatever was dissolving belonged to the screen being left (see PegasusChrome.TopFade).
        UpdateTopFade();

        // The shelf is built again only when it would come back different. Stepping into a record and out
        // of it is the usual case. Rebuilding for that would drop the listener at the top of a list they
        // had scrolled far down, with the record they had just been looking at somewhere off the bottom of
        // it.
        if (to == DrawerScreen.Browse && _browseBuiltFor != BrowseKey)
            BuildBrowse();

        void Cross(FrameworkElement element, Fader fade, DrawerScreen owns)
        {
            var on = _screen == owns;
            element.Visibility = Visibility.Visible;
            element.IsHitTestVisible = on;
            fade.To(on ? 1 : 0, ScreenCrossMs, () =>
            {
                // Only if nobody has come back to it while it was leaving.
                if (_screen != owns)
                    element.Visibility = Visibility.Collapsed;
            });
        }
    }

    /// <summary>
    /// Dresses the head of the drawer for whichever screen is under it: what the key on its right end is
    /// for, and which of the things beside it belong on this screen.
    /// </summary>
    private void ApplyHead()
    {
        // The key on the other end changes what it is for on the record screen. Everywhere else it is the
        // way out of the drawer. In an open record, the way out is not what is wanted. The listener came
        // here looking for music, and everything is one step further out from one record. So it opens the
        // collection instead, full screen over the room (see PegasusChrome.Library). The drawer's own way
        // out is the pointer leaving it, a press on the record, or Escape.
        var toLibrary = _screen == DrawerScreen.Album;
        QueueCloseButton.Content = toLibrary ? "\uf00a" : "\uf00d";   // th-large, times
        IconName.Set(QueueCloseButton,
            Strings.Loc.Get(toLibrary ? "Viz_Library" : "Viz_QueueClose"));

        // All three words are shown on BOTH lists, and none of them on a record screen. What is playing,
        // what you have made and what you own are three answers to one question, so they stand together. A
        // record is not an answer to that question. The head there is about that record, and a row of tabs
        // over it would offer to leave a screen the listener has just arrived at.
        var tabs = _screen == DrawerScreen.Album ? Visibility.Collapsed : Visibility.Visible;
        BrowseQueueTab.Visibility = tabs;
        BrowsePlaylistsTab.Visibility = tabs;
        BrowseAlbumsTab.Visibility = tabs;
        PaintBrowseTabs();
        ApplySearchBox();

        if (_screen != DrawerScreen.Browse)
            BrowseEmpty.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Puts the search field up, or takes it away, the records only.
    ///
    /// <para>The other lists in this drawer are not long enough to need searching. A queue is what somebody
    /// lined up. A playlist is what they made. A record is a dozen lines. A collection is none of those,
    /// which is the whole difference.</para>
    ///
    /// <para>Leaving the shelf clears what was typed. A filter that survived out of sight would quietly
    /// narrow a list by a question asked minutes ago, and the field that says so is not on screen to show
    /// it.</para>
    /// </summary>
    private void ApplySearchBox()
    {
        var show = _screen == DrawerScreen.Browse && _browseAlbums;
        BrowseSearchBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
            return;

        // This is set outright rather than waited for. A collapsed field may or may not be told that it has
        // lost the keyboard, and this flag is what keeps the drawer open. If it stayed set on a field
        // nobody can see, the panel would never close itself again.
        _searchFocused = false;
        _searchTimer?.Stop();

        // The question survives a record being opened out of its answer. Searching for a record, looking at
        // it and stepping back out is one continuous act. A shelf that had forgotten the question by then
        // would make the listener ask it twice. The question is dropped when the shelf itself is left: by
        // the other tab, or when the drawer closes (see ResetToQueue).
        if (!_browseAlbums && BrowseSearch.Text.Length > 0)
            BrowseSearch.Text = "";   // TextChanged sees a hidden field and asks nothing
    }

    /// <summary>
    /// Puts the drawer back on the queue without a dissolve. This is for the moment it is opened, when
    /// there is nothing to cross from.
    ///
    /// <para>Set rather than faded. The panel itself is arriving, and a screen easing in behind a panel
    /// that is also easing in would be two dissolves over one another. The second would look late.</para>
    /// </summary>
    private void ResetToQueue()
    {
        _screen = DrawerScreen.Queue;
        LightRow(null);

        // A question asked over a record the listener has now left. It is set rather than faded, because
        // the panel itself is arriving.
        CloseSheet();
        _sheetFade.Set(0);
        PlaylistSheet.Visibility = Visibility.Collapsed;
        _browseRead++;              // anything still coming back off the disk is for a screen nobody is on
        _albumRead++;
        _browseArt.Clear();
        _browseTitles.Clear();
        BrowseRows.Children.Clear();
        AlbumBody.Children.Clear();
        _albumEntry = null;
        _albumTracks = [];
        _detailPlayKey = null;
        _detailOpener = null;
        _browseBuiltFor = null;

        // A drawer opening is a fresh start, so the shelf is not still narrowed by a question asked the
        // last time it was up. Cleared before the head is dressed below, which is what reads it.
        BrowseSearch.Text = "";

        _queueListFade.Set(1);
        _browseListFade.Set(0);
        _albumListFade.Set(0);
        _albumBackFade.Set(0);
        QueueScroll.Visibility = Visibility.Visible;
        QueueScroll.IsHitTestVisible = true;
        BrowseScroll.Visibility = Visibility.Collapsed;
        BrowseScroll.IsHitTestVisible = false;
        AlbumScroll.Visibility = Visibility.Collapsed;
        AlbumScroll.IsHitTestVisible = false;
        AlbumBackdrop.Visibility = Visibility.Collapsed;
        AlbumBackdropArt.ImageSource = null;   // the last record's colours are not the next one's

        ApplyHead();
    }

    /// <summary>
    /// One of the three words was pressed. Two of them show one list or the other on the shelf. The third
    /// is the queue. The queue is a different screen rather than a different list. So this crosses where it
    /// has to, and reads again only where the answer would come back different.
    /// </summary>
    private void BrowseTab_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag as string;

        if (tag == "queue")
        {
            ShowScreen(DrawerScreen.Queue);   // …which paints the words through ApplyHead
            return;
        }

        var albums = tag == "albums";
        var wasBrowsing = _screen == DrawerScreen.Browse;

        // The word is already lit and was pressed on the shelf it is lit on. Nothing needs crossing to and
        // nothing needs reading again. Pressed from the queue, it is a different case. The list may be the
        // one the shelf showed last, but the shelf is not what is on screen.
        if (_browseAlbums == albums && wasBrowsing)
            return;

        _browseAlbums = albums;
        ApplySearchBox();   // …which also clears the question, before the answer is read below

        if (!wasBrowsing)
        {
            // ShowScreen builds the shelf itself where the key has moved (see its tail). Building here as
            // well would read the collection twice for one press.
            ShowScreen(DrawerScreen.Browse);
            PaintBrowseTabs();
            return;
        }

        BuildBrowse();
    }

    // Asking for one record.

    /// <summary>How long the typing has to stop before the collection is asked. A letter is not a question.
    /// At a normal typing speed, "rumours" would be seven queries and seven rebuilds of the whole shelf, in
    /// front of a shader, to show lists that nobody reads.</summary>
    private static readonly TimeSpan SearchSettle = TimeSpan.FromMilliseconds(220);

    private DispatcherQueueTimer? _searchTimer;

    /// <summary>Whether the field has the keyboard. The drawer closes itself three seconds after the
    /// pointer leaves it. Somebody typing with the pointer parked over the record must not have the panel
    /// closed on them. See the idle count in the tick.</summary>
    private bool _searchFocused;

    /// <summary>Which list is on the shelf, and narrowed by what. This is the question that the rows
    /// currently in the panel answer.</summary>
    private string BrowseKey => $"{(_browseAlbums ? "albums" : "playlists")} {SearchTerm}";

    /// <summary>The question the shelf was last built for. Null for a shelf with no question.</summary>
    private string? _browseBuiltFor;

    /// <summary>What is being asked, or null for the whole shelf.</summary>
    private string? SearchTerm =>
        BrowseSearchBox.Visibility == Visibility.Visible && BrowseSearch.Text.Trim() is { Length: > 0 } term
            ? term
            : null;

    private void BrowseSearch_FocusChanged(object sender, RoutedEventArgs e) =>
        _searchFocused = BrowseSearch.FocusState != FocusState.Unfocused;

    private void BrowseSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        // The field being cleared on the way off the shelf (see ApplySearchBox) is not a question. The list
        // it would rebuild is the one being built already.
        if (BrowseSearchBox.Visibility != Visibility.Visible)
            return;

        _queueSeen = 0;   // typing is attention, wherever the pointer has got to

        if (_searchTimer is null)
        {
            if (DispatcherQueue is null)
                return;
            _searchTimer = DispatcherQueue.CreateTimer();
            _searchTimer.Interval = SearchSettle;
            _searchTimer.IsRepeating = false;
            _searchTimer.Tick += (_, _) =>
            {
                if (_screen == DrawerScreen.Browse && _browseAlbums)
                    BuildBrowse();
            };
        }

        _searchTimer.Stop();
        _searchTimer.Start();
    }

    /// <summary>Which of the three words is lit. The unlit ones are not greyed out so much as quiet. All
    /// three can be pressed, and a disabled-looking word beside a lit one would read as a state rather than
    /// a choice.</summary>
    private void PaintBrowseTabs()
    {
        // Lit from the SCREEN as well as from the list. On the queue, neither of the shelf's two is the
        // answer, whichever one the shelf would show if it were up.
        var browsing = _screen == DrawerScreen.Browse;
        Paint(BrowseQueueTab, _screen == DrawerScreen.Queue);
        Paint(BrowsePlaylistsTab, browsing && !_browseAlbums);
        Paint(BrowseAlbumsTab, browsing && _browseAlbums);

        static void Paint(Button tab, bool on)
        {
            tab.Foreground = new SolidColorBrush(
                Color.FromArgb(on ? (byte)0xFF : (byte)0x8C, 0xFF, 0xFF, 0xFF));
            tab.FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
    }

    // Filling the shelf.

    /// <summary>
    /// Reads the current tab off the library and lays it out.
    ///
    /// <para>The query runs on a thread pool thread, and the rows are built back on the UI thread. That is
    /// the same split the covers use. A shelf of two thousand records is a GROUP BY over the library
    /// database. That is fast but not free. This screen is the one place in the app where a synchronous
    /// read shows up as dropped frames.</para>
    /// </summary>
    private async void BuildBrowse()
    {
        var read = ++_browseRead;
        var scale = DrawerScale;

        PaintBrowseTabs();
        SizeBrowseHead(scale);

        LightRow(null);
        BrowseRows.Children.Clear();
        _browseArt.Clear();
        _browseTitles.Clear();
        BrowseEmpty.Visibility = Visibility.Collapsed;
        BrowseRows.Spacing = QueueRowGap * scale;
        _drawerBuiltAt = scale;
        _browseBuiltFor = BrowseKey;

        var library = Library;
        if (!library.IsOpen || library.Root is not { } root)
        {
            ShowBrowseEmpty("Viz_BrowseNoLibrary", scale);
            return;
        }

        var albums = _browseAlbums;
        var db = library.Db;
        var search = SearchTerm;

        List<BrowseEntry> entries;
        var capped = 0;
        try
        {
            (entries, capped) = await Task.Run(() => albums ? ReadAlbums(db, search) : ReadPlaylists(db));
        }
        catch
        {
            // The library was closed or moved during the read. An empty shelf gives the same result a
            // thrown exception would give. This screen has nowhere to print an error.
            entries = [];
        }

        // The tab was switched, the drawer was closed, or the window was resized while the database was
        // being asked. What came back describes a screen that is no longer on show.
        if (read != _browseRead)
            return;

        if (entries.Count == 0)
        {
            // A shelf with nothing on it and a shelf with nothing matching are different answers. "Add some
            // music" is the wrong answer for somebody who has plenty and mistyped a title.
            ShowBrowseEmpty(
                search is not null ? "Viz_BrowseNoMatch"
                : albums ? "Viz_BrowseNoAlbums"
                : "Viz_BrowseNoPlaylists",
                scale);
            return;
        }

        foreach (var entry in entries)
            BrowseRows.Children.Add(BuildBrowseRow(entry, root, scale));

        // What the cap left out, shown at the foot of the list. It is said plainly. A list that stops at
        // five hundred without a word would look like a collection that has lost fifteen hundred records.
        if (capped > 0)
            BrowseRows.Children.Add(new TextBlock
            {
                Text = string.Format(Strings.Loc.Get("Viz_BrowseMore"), entries.Count),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11 * scale,
                Opacity = 0.55,
                Margin = new Thickness(10 * scale, 12 * scale, 10 * scale, 4 * scale),
            });

        // The rows have no height until the framework has arranged them. Which sleeves to fetch depends on
        // where the rows ended up.
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (read == _browseRead)
                PumpBrowseArt();
        });
    }

    private void ShowBrowseEmpty(string key, double scale)
    {
        BrowseEmpty.Text = Strings.Loc.Get(key);
        BrowseEmpty.FontSize = 13 * scale;
        BrowseEmpty.Visibility = _screen == DrawerScreen.Browse ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>One row of either list, before it is drawn: its name, what is under that, the folder its
    /// sleeve is in, what it plays when pressed, and where a list's cover comes from.</summary>
    /// <param name="Cover">Where a LIST's cover is drawn from: the door its card on home and the playlists
    /// tree draw through (see ListCoverDoor), so the list wears one picture everywhere. Null for a record,
    /// whose sleeve is in its folder. It is stated on every row rather than defaulted. A list built without
    /// one would show no cover, which looks like a list with no picture, not like a fault.</param>
    /// <param name="IsExplicit">Whether the record holds explicit lyrics: the E beside its name. A playlist
    /// has none. It is a list of songs from anywhere, and a mark on the whole of it would warn about
    /// records the listener cannot see from that row.</param>
    private sealed record BrowseEntry(
        string Name, string? Under, string? Folder, string? Glyph, Func<IReadOnlyList<LibraryTrack>> Tracks,
        ListCoverDoor? Cover, bool IsExplicit = false);

    /// <summary>
    /// The playlists, flattened out of the tree that holds them.
    ///
    /// <para>Flat, not a tree, on purpose. A tree needs room for an expander and a way to walk back up, and
    /// this drawer is down the side of the screen. A playlist inside a folder keeps its folder in the line
    /// under its name. That is all the hierarchy was telling the listener.</para>
    ///
    /// <para>The pinned Now playing node is not here. It is the queue, and the queue is the screen this one
    /// steps back from.</para>
    /// </summary>
    private static (List<BrowseEntry> Entries, int Capped) ReadPlaylists(LibraryDatabase db)
    {
        var entries = new List<BrowseEntry>();

        // The generated lists come first, in the same order the panel in the library window pins them. They
        // follow the same rule about which are worth offering. A drawer at the side of the screen is the
        // last place to put a row that opens onto nothing.
        var now = DateTime.UtcNow.Ticks;
        foreach (var kind in SmartPlaylists.All)
        {
            var k = kind;
            var whole = SmartPlaylistId.WholeShelf(k);
            if (db.SmartPlaylistTotals(whole, now).Tracks < SmartPlaylists.LeastToShow(whole))
                continue;

            entries.Add(GeneratedListEntry(db, SmartPlaylistId.WholeShelf(k)));
        }

        var nodes = db.GetPlaylistNodes();
        var byId = nodes.ToDictionary(n => n.Id);

        var made = new List<BrowseEntry>();
        foreach (var node in nodes)
        {
            if (node.Kind != PlaylistNodeKind.Playlist || node.IsNowPlaying)
                continue;

            // The tab reads the listener's own database (library.Db), so these are the listener's own tree.
            made.Add(StoredListEntry(db, node, byId, PlaylistShelf.Own));
        }

        // By name, because the tree's own sibling order means nothing once the tree is gone.
        made.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        entries.AddRange(made);
        return (entries, 0);
    }

    /// <summary>One generated list as a row of this drawer, whole or narrowed (a genre's cut, a decade).
    /// The Playlists tab offers the whole ones; home presses through with any of them.</summary>
    private static BrowseEntry GeneratedListEntry(LibraryDatabase db, SmartPlaylistId id) =>
        new(SmartPlaylistFace.Name(id), null, null, SmartPlaylistFace.Glyph(id.Kind),
            // The clock is read when the row is OPENED, not when the drawer was drawn. A deck can stay open
            // for an evening, and "on repeat" should mean the month before the moment somebody asks for it.
            //
            // Opening the row keeps the list as it stands. Only the "generate a new list" button asks for a
            // different list (see LibraryDatabase.MoodWindow).
            () => db.QuerySmartPlaylist(id, DateTime.UtcNow.Ticks, advance: false),
            new ListCoverDoor.Generated(id));

    /// <summary>One playlist from the tree as a row of this drawer, with the folders it sits in written
    /// under its name.</summary>
    /// <param name="shelf">Whose tree <paramref name="db"/> is: the cover is
    /// looked up by node id in it, and both trees number their lists from one (see PlaylistShelf). No
    /// default, for that reason.</param>
    private static BrowseEntry StoredListEntry(
        LibraryDatabase db, PlaylistNode node, IReadOnlyDictionary<long, PlaylistNode> byId, PlaylistShelf shelf)
    {
        var id = node.Id;
        return new BrowseEntry(
            node.Name, FolderPath(), null, "",   // list-music, the key that opens the drawer
            () => db.FlattenPlaylistTracks(id),
            new ListCoverDoor.Kept(node.Id, node.Name, node.Cover, shelf));

        string? FolderPath()
        {
            var parts = new List<string>();
            var at = node.ParentId;
            // Bounded by a fixed number of hops. A parent link that pointed back at itself would otherwise
            // loop forever.
            for (var hops = 0; at is { } parent && hops < 32; hops++)
            {
                if (!byId.TryGetValue(parent, out var up))
                    break;
                parts.Insert(0, up.Name);
                at = up.ParentId;
            }
            return parts.Count > 0 ? string.Join(" / ", parts) : null;
        }
    }

    /// <summary>
    /// The records, in the order the collection reads, which is the order of the folders. The library's own
    /// shape is Artist/Album/track.
    ///
    /// <para>It is built from the grouping query, so nothing here reads a track. What a record is called
    /// and who it is by are stored in the library database. Its tracks are read when somebody presses
    /// it.</para>
    ///
    /// <para><paramref name="search"/> is passed to the same query the library's own search runs. So a
    /// record found here is a record found there. Because the search narrows the query at the source, the
    /// row cap is never the reason a record cannot be found.</para>
    /// </summary>
    private static (List<BrowseEntry> Entries, int Capped) ReadAlbums(LibraryDatabase db, string? search)
    {
        var facts = db.QueryAlbumFacts(search);

        // Which of them hold explicit lyrics, from the same query the library's own shelves read. It is one
        // DISTINCT query over the library database, not a question per record. It runs here on the thread
        // pool, with the shelf itself.
        var explicitAlbums = db.QueryExplicitAlbums(search).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var entries = new List<BrowseEntry>();

        foreach (var row in facts)
        {
            // The loose tracks at the library root group under the empty folder. They are not a record, and
            // there is nothing to call them.
            if (string.IsNullOrEmpty(row.Folder))
                continue;

            var parts = row.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var name = row.Album ?? (parts.Length > 0 ? parts[^1] : row.Folder);
            var artist = row.AlbumArtist ?? (parts.Length >= 2 ? parts[^2] : null);
            var folder = row.Folder;

            entries.Add(new BrowseEntry(
                name, artist, folder, null, () => db.FolderTracks(folder), Cover: null,
                explicitAlbums.Contains(folder)));
        }

        // Counted after the whole shelf is known. So what is stated at the foot of the list is the number
        // of records actually left out, not the number of rows the query returned.
        if (entries.Count <= BrowseAlbumCap)
            return (entries, 0);

        var over = entries.Count - BrowseAlbumCap;
        entries.RemoveRange(BrowseAlbumCap, over);
        return (entries, over);
    }

    /// <summary>
    /// One row: the sleeve (or the list's own mark), what it is called, and what it is under.
    ///
    /// <para>A Border, like the queue's rows, and for a second reason as well as the first. A Button's own
    /// pointer-over wash is a theme brush drawn for a light surface. It also stays lit on every row that a
    /// moving list drags past a still pointer. Both problems go away if the wash is written from here (see
    /// <see cref="LightRow"/>).</para>
    /// </summary>
    private RemoteLine BuildBrowseRow(BrowseEntry entry, string root, double scale)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // the sleeve
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });    // the words

        // The same square as the queue's sleeves, so the two screens line up down the left edge. A playlist
        // has no cover of its own. It wears its mark in the same square, rather than leaving a hole where
        // every other row has a picture.
        var art = new Image { Stretch = Stretch.UniformToFill };
        var sleeve = new Border
        {
            Width = QueueArt * scale,
            Height = QueueArt * scale,
            CornerRadius = new CornerRadius(3 * scale),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 0, 10 * scale, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = entry.Glyph is { } glyph
                ? new TextBlock
                {
                    Text = glyph,
                    FontFamily = (FontFamily)Resources["PegasusFa"],
                    FontSize = 13 * scale,
                    Opacity = 0.66,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                }
                : art,
        };
        Grid.SetColumn(sleeve, 0);

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        // The name, and the E on a record whose lyrics are explicit. It is the same mark the library's own
        // shelves wear, so a record marked there is marked here. The name's column is AUTO, and the slack
        // is taken by a spacer after the badge, exactly as the queue's own rows do it. If the name were
        // starred, it would fill the row and push the E out to the far edge, away from the title. Sized to
        // its text, the E sits after the last letter. A long name must not push the badge off the drawer.
        // The MaxWidth stops that. The panel's own width sets it here at build, and LayoutQueue sets it
        // again when the window changes size under an open drawer.
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // the name
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // the E
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var title = new TextBlock
        {
            Text = entry.Name,
            FontSize = 14 * scale,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = BrowseTitleMax(scale),
        };
        Grid.SetColumn(title, 0);
        titleRow.Children.Add(title);
        _browseTitles.Add(title);

        // Built only for the records that wear one, rather than built everywhere and hidden. Most rows do
        // not wear one, and a collapsed control is still a control the layout has to walk past.
        if (entry.IsExplicit)
        {
            var badge = new ExplicitBadge
            {
                // Set here even though the row is only built for records that wear one. The badge starts
                // collapsed and shows itself when this property is set. A list binds it and gets this for
                // free. A row built by hand has no binding to do that, so without this line the badge would
                // be built and never shown.
                IsExplicit = true,
                Width = 13 * scale,
                Height = 13 * scale,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6 * scale, 0, 0, 0),
                // The letter is cut out of the fill, so it must match the surface behind the badge. Here
                // that is the drawer's near-black, not the library's dark, which is the default.
                LetterBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x08, 0x08, 0x08)),
            };
            ToolTipService.SetToolTip(badge, Strings.Loc.Get("Library_Explicit"));
            Grid.SetColumn(badge, 1);
            titleRow.Children.Add(badge);
        }
        words.Children.Add(titleRow);
        if (!string.IsNullOrWhiteSpace(entry.Under))
            words.Children.Add(new TextBlock
            {
                Text = entry.Under,
                FontSize = 11.5 * scale,
                Opacity = 0.55,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        Grid.SetColumn(words, 1);

        grid.Children.Add(sleeve);
        grid.Children.Add(words);

        var row = new Border
        {
            Tag = entry,
            Child = grid,
            Padding = new Thickness(10 * scale, 8 * scale, 10 * scale, 8 * scale),
            CornerRadius = new CornerRadius(6 * scale),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        row.PointerEntered += DrawerRow_PointerEntered;
        row.PointerExited += DrawerRow_PointerExited;
        row.PointerReleased += BrowseRow_Released;

        // The sleeve is NOT asked for here. Five hundred rows would be five hundred reads off the disk, all
        // fired at once, for the thirty that anybody can see (see PumpBrowseArt).
        if (entry.Folder is { } albumFolder)
            _browseArt.Add(new BrowseArt(row, art, Path.Combine(root, albumFolder.Replace('/', Path.DirectorySeparatorChar))));

        // In a line the remote can stand on, where OK opens it as a press does. The line takes no pointer
        // input, so the press and the hover stay on the row.
        return new RemoteLine(row, () => OpenShelfEntry(entry), menu: null, turn: null)
        {
            CornerRadius = row.CornerRadius,
        };
    }

    // ── the sleeves, as they come into view ──

    /// <summary>A row waiting for its cover: where it is in the list, what to put the picture in, and which
    /// folder to look in for one.</summary>
    private sealed record BrowseArt(FrameworkElement Row, Image Art, string Folder)
    {
        /// <summary>Whether the disk has been asked yet. Once only, whatever the scrolling does.</summary>
        public bool Asked { get; set; }
    }

    private readonly List<BrowseArt> _browseArt = [];

    private void Browse_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        LightRow(null);
        PumpBrowseArt();
    }

    /// <summary>
    /// Fetches the sleeves for the rows in view, and one screen either side of them.
    ///
    /// <para>The range is worked out from the row pitch, rather than by asking each row where it is. Every
    /// row on this screen is the same height, so the rows on show are an index range. A transform for every
    /// row on every scroll event, over a list this long, would cause a stutter in the room behind the
    /// drawer.</para>
    /// </summary>
    private void PumpBrowseArt()
    {
        if (_browseArt.Count == 0 || _screen != DrawerScreen.Browse)
            return;

        var view = BrowseScroll.ViewportHeight;
        if (view <= 0 || _browseArt[0].Row.ActualHeight <= 0)
            return;

        var pitch = _browseArt[0].Row.ActualHeight + BrowseRows.Spacing;
        var top = BrowseScroll.VerticalOffset;

        var from = Math.Max(0, (int)((top - view) / pitch));
        var to = Math.Min(_browseArt.Count - 1, (int)((top + view * 2) / pitch));

        for (var i = from; i <= to; i++)
        {
            var item = _browseArt[i];
            if (item.Asked)
                continue;
            item.Asked = true;
            ShowCoverAsync(item.Art, item.Folder);
        }
    }

    // What a press on the shelf means.

    /// <summary>
    /// A row was pressed: it opens, whether it is a record or a playlist.
    ///
    /// <para>Neither one plays on the press. Each is a dozen songs behind one name. Choosing one off a
    /// shelf is not the same act as putting the needle on its first track. It is asking what is on it. So
    /// it opens, and plays from wherever the listener then says (see <see cref="ShowDetailAsync"/>).</para>
    /// </summary>
    private void BrowseRow_Released(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is BrowseEntry entry)
            OpenShelfEntry(entry);
    }

    /// <summary>Opens a record or list off the shelf: a press on its row, or OK on it.</summary>
    private void OpenShelfEntry(BrowseEntry entry)
    {
        if (_amp is not null)
            ShowDetailAsync(entry);
    }

    /// <summary>Puts a list on and goes back to what is playing. One door for every press in this drawer
    /// that starts music, so they cannot disagree about what happens to the queue or about where the
    /// listener is left standing.</summary>
    /// <param name="sleeve">The sleeve the screen already has decoded
    /// for what it plays, handed to the player for the moment before the header's own cover (see HandOver),
    /// or <see cref="SleeveInHand.None"/>. Not defaulted: each screen says which it is.</param>
    private void PlayFromDrawer(IReadOnlyList<TrackInfo> tracks, int from, SleeveInHand sleeve)
    {
        if (_amp is null || tracks.Count == 0)
            return;
        _amp.PlayAlbumFrom(tracks, from);

        // Set before the composition comes up, so it comes up with the sleeve already on it.
        HandOver(sleeve);
        ShowScreen(DrawerScreen.Queue);

        // ...and the wall of the collection, where the listener was standing when they found this. It was
        // up because there was nothing to watch (see PegasusChrome.Library). There is something now. A
        // record put on from a screen that stays on the covers would look as if the press did nothing.
        //
        // Not over home: a list opened from home plays, and the listener stays on home, where the mini
        // player shows what came on and is the way to the record (see PegasusChrome.Home).
        if (!HomeInFront)
            HideLibrary();
    }

    // The size of it.

    /// <summary>Builds whichever screen carries hand-sized rows again, after the window has changed size
    /// far enough to want new ones. Never both: only one of them is being looked at.</summary>
    private void RebuildForScale(double scale)
    {
        // Written before either build, not by them, because the album screen's own path does not go through
        // BuildBrowse. A scale that is never marked as built would cause a rebuild on every resize event
        // for the rest of the session.
        _drawerBuiltAt = scale;

        if (_screen == DrawerScreen.Browse)
            BuildBrowse();
        else if (_screen == DrawerScreen.Album && _albumEntry is { } open)
            ShowDetailAsync(open);
    }

    /// <summary>The head of the drawer, which all three screens share. Sized with the rows, off the same
    /// figure, see <see cref="QueueScale"/>.</summary>
    private void SizeBrowseHead(double scale)
    {
        QueueCloseButton.Width = QueueCloseButton.Height = 26 * scale;
        QueueCloseButton.CornerRadius = new CornerRadius(13 * scale);
        QueueCloseButton.FontSize = 11 * scale;

        // The band the record dissolves in, which grows with everything else (see PegasusChrome.TopFade).
        DrawerTopFade.Height = TopFadeDepth * scale;

        BrowseQueueTab.FontSize = BrowsePlaylistsTab.FontSize = BrowseAlbumsTab.FontSize = 12 * scale;
        BrowseQueueTab.Padding = BrowsePlaylistsTab.Padding = BrowseAlbumsTab.Padding =
            new Thickness(8 * scale, 3 * scale, 8 * scale, 3 * scale);
        BrowseQueueTab.CornerRadius = BrowsePlaylistsTab.CornerRadius =
            BrowseAlbumsTab.CornerRadius = new CornerRadius(4 * scale);

        // The field grows with the text in it, and keeps a minimum width. On a narrow drawer it is squeezed
        // between the two words and the way out. A box a few points wide is worse than no box.
        BrowseSearchBox.Height = 28 * scale;
        BrowseSearchBox.MinWidth = 96 * scale;
        BrowseSearchBox.CornerRadius = new CornerRadius(6 * scale);
        BrowseSearchGlyph.FontSize = 11 * scale;
        BrowseSearch.FontSize = 12 * scale;
        BrowseSearch.Height = 18 * scale;
    }
}
