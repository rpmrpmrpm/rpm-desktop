using RPM.Core.Library;
using RPM.Desktop.Presentation.Main.Artists.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Main.Artists;

/// <summary>
/// The collection, read by who made it: a wall of 16:9 boxes, one per act. Each box shows the
/// artist's own photograph in grey, with their own colour laid over it and their name across the
/// middle.
///
/// <para>There is one box per ACT, never per credit. A guest on somebody else's record gets a box of
/// their own, and the record is listed under both of them. <see cref="ArtistWall"/> makes that
/// decision before this view sees anything. This view does the layout, the fetching and the index
/// rail down the right edge.</para>
///
/// <para>The control knows nothing about the library. The page passes in the artists and a delegate
/// that turns one artist into a photograph. The album wall is fed in the same way.</para>
/// </summary>
public sealed partial class ArtistWallView : UserControl
{
    /// <summary>The wall's own scroller. The shell uses it to pin the art drawer in step with the
    /// wall, as it does for the album wall.</summary>
    public ScrollViewer ScrollHost => Scroller;

    /// <summary>The box width the wall aims for. It is a target, not a fixed size. Each row is
    /// filled edge to edge, so a box is drawn at this value rounded to a whole number of columns.</summary>
    private const double TargetWidth = 300;
    private const double MinTileWidth = 190;
    private const double Gap = 14;

    /// <summary>What is laid out: the shelf narrowed by the search field. It is a plain list that is
    /// REPLACED as a whole, not an observable list changed in place. Narrowing two thousand artists to
    /// twelve by clearing and re-adding would send two thousand change notices per keystroke. A swap
    /// sends one.</summary>
    private IReadOnlyList<ArtistTile> _tiles = [];

    /// <summary>The whole shelf, not narrowed. The search filters this list, and a cleared field goes
    /// back to it without another call to the library.
    ///
    /// <para>It starts as its own instance, not the shared empty array. That way the first shelf that
    /// is handed in is never the object already held. <see cref="SetArtists"/> tells them apart by
    /// reference.</para></summary>
    private IReadOnlyList<ArtistWallItem> _shelf = new List<ArtistWallItem>();

    private string _query = "";

    private readonly Dictionary<string, ArtistTile> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ArtistTileMetrics _metrics = new();

    /// <summary>Where each letter's first box sits in the list. The rail jumps to these positions. It
    /// is also used to turn a scroll position back into a letter.</summary>
    private readonly Dictionary<char, int> _firstUnder = [];

    private int _columns = 1;
    private char _standingOn = '\0';
    private CancellationTokenSource? _fetching;

    /// <summary>A box was pressed. The page decides what a pressed artist means.</summary>
    public event Action<ArtistWallItem>? ArtistOpened;

    /// <summary>Turns an artist into what the box is drawn from. That is either the photograph, already
    /// in grey, with the colour to wash it in. Or, for the many artists with no photograph, the colour
    /// of their first record. The photograph comes from the shared portrait cache. So a box and the
    /// track pane always show the same picture for an artist.</summary>
    public Func<ArtistWallItem, CancellationToken, Task<ArtistFace>>? PortraitLoader { get; set; }

    public ArtistWallView()
    {
        InitializeComponent();
        Tiles.ElementPrepared += Tile_ElementPrepared;
        Rail.Picked += JumpTo;

        Unloaded += (_, _) =>
        {
            _fetching?.Cancel();
            _wanted.Clear();
        };
    }

    // ── what is on the wall ──

    /// <summary>
    /// The wall's artists, in the order they read.
    ///
    /// <para>Boxes are REUSED by name across rebuilds. This matters for speed and for the picture. A
    /// rebuild runs on every library change and every keystroke in a search. A new box would fetch a
    /// photograph that is already on screen. With a reused box, an artist who stays in the list keeps
    /// the picture they have, and nothing flickers.</para>
    /// </summary>
    /// <param name="read">Whether <paramref name="artists"/> was read from the collection that is open
    /// now. It is false before the first read. Until then the wall shows no message, and the page's
    /// loader is shown instead of the empty-collection message.</param>
    public void SetArtists(IReadOnlyList<ArtistWallItem> artists, bool read)
    {
        var readChanged = read != _read;
        _read = read;

        // The same list again means the same wall. The page calls this on every shelf rebuild. Most of
        // those are keystrokes in the album views' search field. That search does not change the
        // artists (see LibraryAlbumIndex.ArtistShelf), so the list arrives back identical and there is
        // nothing to refold here.
        if (ReferenceEquals(_shelf, artists) && !readChanged)
            return;

        _shelf = artists;
        Rebuild();
    }

    /// <summary>
    /// What has been typed in the wall's search field.
    ///
    /// <para>The search is held here, not passed to the library's own search. This field narrows only
    /// the wall, to the names being looked for. The search field over the album wall narrows the album
    /// views. The two are separate. Typing a name here does not remove that artist's records from the
    /// album wall, and typing a record's name there does not remove names from this wall.</para>
    /// </summary>
    public void SetSearch(string? query)
    {
        var wanted = query?.Trim() ?? "";
        if (string.Equals(wanted, _query, StringComparison.Ordinal))
            return;

        _query = wanted;
        Rebuild();

        // A narrowed wall is a different wall, so the reader goes back to the top. If the view stayed
        // where it was, a search that returns eight names could show an empty page nine screens down.
        Scroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    /// <summary>Whether the wall is showing everybody it has, or a search's worth of them.</summary>
    public bool Searching => _query.Length > 0;

    /// <summary>Whether the list on the wall was read from the collection open now (see
    /// <see cref="SetArtists"/>).</summary>
    private bool _read;

    /// <summary>How many boxes are on the wall. The line under the heading counts these. It is what is
    /// SHOWN, not what the collection holds.</summary>
    public int Shown => _tiles.Count;

    /// <summary>The boxes, the rail's letters and the empty state, from the shelf and the search
    /// together. All three are updated in this one method. So a library change during a search, or a
    /// keystroke against a fixed shelf, cannot leave them disagreeing.</summary>
    private void Rebuild()
    {
        var tiles = new List<ArtistTile>(_shelf.Count);
        var kept = new Dictionary<string, ArtistTile>(StringComparer.OrdinalIgnoreCase);
        var wanted = ArtistWall.SearchKey(_query);

        _firstUnder.Clear();

        foreach (var artist in _shelf)
        {
            // Folded once, against a folded query. The name's own key is worked out here, not inside a
            // per-name comparison. Inside a comparison, two thousand artists would mean two thousand
            // normalizations per keystroke.
            if (wanted.Length > 0 && !ArtistWall.SearchKey(artist.Name).Contains(wanted, StringComparison.Ordinal))
                continue;

            if (_byName.TryGetValue(artist.Name, out var tile))
                tile.Artist = artist;   // the box is kept; what it is about is read afresh
            else
                tile = new ArtistTile(artist, _metrics);

            kept[artist.Name] = tile;
            _firstUnder.TryAdd(artist.Initial, tiles.Count);
            tiles.Add(tile);
        }

        // Boxes are remembered by name across a narrowing, and across a library change. Boxes that are
        // filtered out are kept, not dropped. A box typed back into view brings its photograph with it.
        // So deleting characters from a search does not fetch the wall again one letter at a time.
        foreach (var (name, tile) in kept)
            _byName[name] = tile;

        _tiles = tiles;
        Tiles.ItemsSource = tiles;

        Rail.SetAvailable(new HashSet<char>(_firstUnder.Keys));
        Rail.Visibility = Vis(tiles.Count > 0);
        EmptyHint.Visibility = Vis(tiles.Count == 0 && !Searching && _read);
        NoMatchHint.Visibility = Vis(tiles.Count == 0 && Searching);

        // The wall has changed under the rail, so the standing letter must be read again. The letter
        // that was lit may have no artist under it.
        _standingOn = ' ';
        ReadStandingLetter();
    }

    private static Visibility Vis(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    // ── the pictures ──

    /// <summary>
    /// How many photographs are fetched at once.
    ///
    /// <para><b>Why there is a limit.</b> Without a limit, each realized box starts a fetch. A fast
    /// scroll then starts a fetch for every box it passes. Most of them were cancelled at once, when
    /// their elements were recycled. Each cancellation throws exceptions. Under a debugger those
    /// exceptions are slow and can hold up garbage collection. So fetches are limited. The limit keeps
    /// the number of cancellations small.</para>
    ///
    /// <para>Three fetches at a time. That is enough for a screenful to fill while the user looks at
    /// it. It is few enough that a fast scroll does not leave half-loaded pictures behind the boxes
    /// that asked for them.</para>
    /// </summary>
    private const int Loaders = 3;

    /// <summary>The boxes waiting for a turn, and how many turns are running.
    ///
    /// <para>There is no lock. Every line that touches these runs on the thread that draws.
    /// <see cref="Tile_ElementPrepared"/> is a framework callback on that thread. The awaits below
    /// capture that thread's context and return to it. One ordering would matter: a box queued between
    /// a worker finding the queue empty and that worker counting itself out. It cannot happen, because
    /// there is no await between those two steps.</para>
    /// </summary>
    private readonly Queue<(int Index, ArtistTile Tile)> _wanted = new();

    private int _loading;

    /// <summary>A box has come on screen, or come back. Ask for its photograph, but only once. This is
    /// asked on every realize, not from the template's DataContextChanged. When the repeater gives a
    /// recycled element back to the same box, it rebinds nothing. DataContextChanged would never fire,
    /// and nobody would ask.</summary>
    private void Tile_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Index < 0 || args.Index >= _tiles.Count)
            return;

        var tile = _tiles[args.Index];
        if (tile.Asked)
            return;

        // Marked here, not where the fetch happens. A fast scroll can realize the same box twice before the
        // first turn comes round. Two fetches for one picture would download it twice.
        tile.Asked = true;
        _wanted.Enqueue((args.Index, tile));

        while (_loading < Loaders && _wanted.Count > 0)
        {
            _loading++;
            _ = TurnsAsync();
        }
    }

    /// <summary>One loader: take the next box that is still worth doing, fill it in, repeat until there
    /// is nothing left waiting.</summary>
    private async Task TurnsAsync()
    {
        try
        {
            while (_wanted.Count > 0)
            {
                var (index, tile) = _wanted.Dequeue();

                // Whether this turn is still worth taking depends on the wall NOW, not on how it looked
                // when the box was queued. A fast scroll queues thirty boxes and scrolls past twenty-five
                // of them. Fetching those would deliver a picture that nobody is looking at, to an
                // element that now shows a different artist. The box is marked as not asked, so it is
                // asked for again if it comes back on screen.
                if (!StillWanted(_tiles, index, tile, Tiles.TryGetElement(index) is not null))
                {
                    tile.Asked = false;
                    continue;
                }

                await FetchAsync(tile);
            }
        }
        finally
        {
            _loading--;
        }
    }

    /// <summary>
    /// Whether a queued box is still the box that was queued, and still on screen.
    ///
    /// <para>Both checks are needed. A rebuild runs on every library change and every search keystroke.
    /// It replaces the whole list, so an index queued against the old list can name a different artist
    /// in the new one. The box itself is compared, not only the index. So a rebuild during loading
    /// needs no special handling.</para>
    /// </summary>
    /// <remarks>This method is generic over the box only so that a test can check it. An
    /// <see cref="ArtistTile"/> cannot be built outside a XAML host. A rule that cannot be tested is
    /// easy to change without knowing it. Nothing here depends on the tile. It checks only identity
    /// and bounds.</remarks>
    internal static bool StillWanted<T>(IReadOnlyList<T> tiles, int index, T tile, bool realized)
        where T : class
        => index >= 0
           && index < tiles.Count
           && ReferenceEquals(tiles[index], tile)
           && realized;

    private async Task FetchAsync(ArtistTile tile)
    {
        if (PortraitLoader is not { } load)
            return;

        // Named, so that a cancellation which happens with none of this code on the stack can still be
        // traced to the wall that asked. The renderer opens the picture from a property change, on the
        // thread pool, so nothing on that stack is ours. The parent job is carried in an AsyncLocal. It
        // flows through the assignment below and through every continuation that starts under it. See
        // Scope.Job, and `rpmscope throws`.
        using var job = RPM.Core.Diagnostics.Scope.Job(nameof(FetchAsync), nameof(ArtistWallView));

        // A new token is made after the view has been unloaded and reloaded. A token cancelled on the
        // way out would otherwise refuse every fetch the wall makes from then on.
        if (_fetching is null || _fetching.IsCancellationRequested)
            _fetching = new CancellationTokenSource();
        var token = _fetching.Token;

        try
        {
            var face = await load(tile.Artist, token);
            if (token.IsCancellationRequested)
                return;

            // One call, whatever kind of answer came back. A photograph washes the box. A record paints
            // it. The box does not decide between the two. See ArtistFace.
            tile.Wear(face);

            // A stand-in for a photograph that could not be fetched is not a final answer. The box asks
            // again the next time it comes on screen.
            if (face.StandIn)
                tile.Asked = false;

            // The picture as the source gave it. The grey copy is still made and still on the face, but
            // the wall does not use it. The colour over the picture now fades, so the picture itself is
            // what is wanted. It is decoded at the size it is drawn. See ArtistTileMetrics.DecodeEdge for
            // how that edge is chosen and what leaving it out costs.
            if ((face.Original ?? face.Picture) is { Length: > 0 } picture)
                tile.Picture = await RemoteImage.DecodeAsync(
                    picture, _metrics.DecodeEdge(XamlRoot?.RasterizationScale ?? 1));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A photograph is decoration. A box without one keeps its colour and its name, and those are
            // all it needs.
        }
    }

    // ── the layout, and the rail that reads it ──

    /// <summary>
    /// How many boxes fit across, and how wide each is once the row is filled edge to edge.
    ///
    /// <para>It is worked out here, not left to the layout, because the index rail needs it. A rail
    /// that jumps to a letter must know which ROW that letter's first box is in. The only way to know
    /// that, without asking the repeater about elements it has not realized, is to do the division
    /// here.</para>
    /// </summary>
    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e) => Relayout();

    private void Relayout()
    {
        var available = Scroller.ActualWidth - Scroller.Padding.Left - Scroller.Padding.Right;
        if (available <= 0)
            return;

        // Counted in strides: a box plus the gap after it. One gap is added back, because the last box in
        // a row has no gap after it. The nearest whole number of columns is used, not the floor, so the
        // boxes come as close to the target as a whole number of them allows.
        var columns = AlbumGridLayout.ColumnsForTarget(available + Gap, TargetWidth + Gap);
        columns = Math.Max(1, Math.Min(columns, (int)Math.Floor((available + Gap) / (MinTileWidth + Gap))));
        columns = Math.Max(1, columns);

        var width = Math.Floor((available - ((columns - 1) * Gap)) / columns);
        if (width < 1)
            return;

        _columns = columns;
        _metrics.Set(width);

        // The layout is given the same numbers the boxes are drawn at. So the layout and the rail's
        // arithmetic describe the same wall.
        //
        // The repeater's own minimum width is set at the same time, and the two must be set together.
        // UniformGridLayout counts how many boxes fit across the width it is measured at, then divides
        // by that count. A pass narrower than ONE box gives zero boxes per line, and the measure throws
        // a DivideByZeroException. A narrow pass does happen: minimizing measures the whole tree at a
        // width of nothing. This method has already returned by then, because there is no width to
        // divide, so the last real box width is still in place. The exception is thrown on the
        // dispatcher and the pass is abandoned half way through the tree. The render that answers the
        // window RESIZE, when it comes back from the taskbar, runs inside the window procedure. There, a
        // throw on half-built visuals is not an exception any more. It ends the process. So the box
        // width and the floor under it are always set together.
        Tiles.MinWidth = width;
        TileLayout.MinItemWidth = width;
        TileLayout.MinItemHeight = _metrics.Height;

        ReadStandingLetter();
    }

    /// <summary>One row's worth of vertical travel.</summary>
    private double RowStride => _metrics.Height + Gap;

    private void Scroller_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e) => ReadStandingLetter();

    /// <summary>
    /// Which letter the wall is standing on: the letter of the first box in the row at the top of the
    /// viewport.
    ///
    /// <para>It is worked out from the scroll offset with arithmetic, not from the realized elements.
    /// The repeater only knows the rows it has made. During a fast scroll, the row at the top of the
    /// viewport is often one it has not made. If the rail read the realized elements, it would go blank
    /// exactly while it is being watched.</para>
    /// </summary>
    private void ReadStandingLetter()
    {
        if (_tiles.Count == 0)
            return;

        var row = (int)Math.Floor(Math.Max(0, Scroller.VerticalOffset) / Math.Max(1, RowStride));
        var index = Math.Clamp(row * _columns, 0, _tiles.Count - 1);
        var letter = _tiles[index].Initial;

        if (letter == _standingOn)
            return;

        _standingOn = letter;
        Rail.SetCurrent(letter);
    }

    /// <summary>A letter was picked on the rail. Put its first box at the top of the wall.</summary>
    private void JumpTo(char letter)
    {
        if (!_firstUnder.TryGetValue(letter, out var index))
            return;

        // The rail already lit this letter when it was pressed. The scroll must not change it on the way.
        _standingOn = letter;
        var row = index / Math.Max(1, _columns);
        Scroller.ChangeView(null, row * RowStride, null);
    }

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ArtistTile tile })
            ArtistOpened?.Invoke(tile.Artist);
    }
}
