using RPM.Desktop.Services.Art;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// What dresses the collection over the visualizer: which picture each tile is wearing, at what size, and
/// in what order the wall is allowed to go and get them.
///
/// <para>The loader is the same one the library window's wall runs: <see cref="CoverStream"/>. It is one
/// collection drawn twice, on one thread, and two different answers to "what should we fetch next"
/// competing for that thread would make the wall stutter in front of a shader. The loader serves blank
/// tiles ahead of tiles that are merely a size out, and asks again for a sleeve that is not square.</para>
///
/// <para>What is left here is only what belongs to THIS wall. Eight fixed columns rather than a size
/// slider, so its tier follows the tile instead of standing at a floor, which is why it throws away what
/// it holds when the window settles at a new size, and why it holds ONE picture rather than a small one
/// under a large. No cloud marks to fetch. Its own collection, always the user's, where the window's shelf
/// may be somebody else's. And a room that stands still while the first pass runs, which is what
/// <see cref="LibraryWarming"/> is for.</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>The rows the repeater is holding an element for. Rows and not cards: what is IN a row is
    /// read when the question is asked, so nothing goes stale under a relayout.</summary>
    private readonly HashSet<PegasusAlbumRow> _onScreenRows = new();

    /// <summary>The records standing in those rows, read now rather than remembered.</summary>
    private IEnumerable<ICoverCard> CardsOnScreen()
    {
        foreach (var row in _onScreenRows)
            foreach (var card in row.Cards)
                yield return card;
    }

    private CoverStream? _libraryArt;

    /// <summary>
    /// The stream, built the first time it is wanted.
    ///
    /// <para>The tier here is the TILE's own size, not a floor under it. This wall has no slider: its tile
    /// is an eighth of the window and nothing else, which on an ordinary desktop is very nearly three
    /// hundred points. If the tier were held at the window's floor of 128 pixels, every tile on the
    /// screen would be a small picture stretched across three hundred points, with a second read and a
    /// second decode arriving late to cover it. One tier at the tile's own size means a realized row
    /// paints the picture the wall wanted, with nothing to fetch over the top of it.</para>
    /// </summary>
    private CoverStream LibraryArt => _libraryArt ??= new CoverStream(DispatcherQueue, new CoverStream.Wall(
        Collection: () => _libraryCards,
        OnScreen: CardsOnScreen,
        WantedEdge: () => Math.Max(ArtScaler.TinyEdge, CardDecodeEdge()),
        ResidentEdge: () => Math.Max(ArtScaler.TinyEdge, CardDecodeEdge()),

        // Named after the COLLECTION, not the wall, so this and the library window's wall share one pack
        // file whenever they are drawing the same records at the same size, and the first-run cost is paid
        // once between them.
        PackScope: () => ThumbPack.ScopeFor(Library.Root),

        // The user's own collection, because that is what this wall lists, the window next door may well
        // be browsing somebody else's.
        CollectionRoot: () => Library.Root,
        Read: (folder, edge, ct) => AlbumCovers.LoadForSquareAsync(folder, ct, edge, ownCollection: true),

        // Nothing on this screen paints a record's colour. They are read for the PACK, to carry them to the
        // wall in the library window, which would otherwise decode every cover in the collection to find
        // one. That saving is for a screen nobody is looking at yet, so the colours are gathered
        // behind the covers somebody IS looking at.
        Tints: TintReading.BehindTheWall,
        Hold: CoverHold.OnePicture)
    {
        Urgent = AnyBlankOnScreen,
        HasTheFrame = () => _warming,
        // Only a pass that has something to fetch holds the room. A search, a sort or the heart hands back
        // records the wall is ALREADY holding pictures for. Raising the flag for those would put the
        // host's "reading your collection" cover (a full-screen wait with a percentage) over a
        // collection that is complete, for a filter that has nothing to do.
        Warming = on => SetWarming(on && AnyUndressed()),
        Progress = fraction => LibraryWarmProgress?.Invoke(fraction),
    });

    /// <summary>Wakes the loader. Cheap and idempotent, call it on anything that could have changed what
    /// is worth doing: a realize, a fill, a resize.</summary>
    private void LoadLibraryArt() => LibraryArt.Wake();

    /// <summary>Reads the pack for the collection about to be dressed.</summary>
    private void ReadPackAsync() => LibraryArt.TierMoved(dropHeld: true);

    /// <summary>Starts the first pass over the collection, or leaves the one already running
    /// alone.</summary>
    private void WarmResidentLater() => LibraryArt.Warm();

    /// <summary>Whether any tile on screen has nothing on it at all. An empty frame is a record the
    /// listener cannot see, and it is worth more of a frame than one merely a size out.</summary>
    private bool AnyBlankOnScreen()
    {
        foreach (var card in CardsOnScreen())
            if (card.Cover is null && !card.CoverMissing)
                return true;
        return false;
    }

    /// <summary>
    /// Raised while the collection is being dressed for the first time, and again when it is done.
    ///
    /// <para>The HOST acts on it, because what it asks for is not this screen's to give: hold the room
    /// still and keep the cover over the wall until the covers are in. Reading a few thousand records off
    /// the disk is the one genuinely slow thing this screen does, and it is slowest on exactly the machine
    /// least able to spare the cores, the room's shader wants the same ones. Standing the room down for
    /// the length of it is not a courtesy to the loader. It means the wall arrives finished and the
    /// listener does not watch it being assembled.</para>
    /// </summary>
    public event Action<bool>? LibraryWarming;

    /// <summary>How far the collection has got into being dressed, as a fraction, raised after every
    /// batch, the percentage under the host's ring.</summary>
    public event Action<double>? LibraryWarmProgress;

    /// <summary>Whether the resident tier is still filling. Only the FIRST pass over a collection raises
    /// it, once every record has its picture there is nothing left to hold the room for, and a record
    /// arriving later (a resize crossing a bucket) is a tile sharpening, not a wall being built.</summary>
    private bool _warming;

    private void SetWarming(bool on)
    {
        if (_warming == on)
            return;
        _warming = on;
        LibraryWarming?.Invoke(on);
    }

    // ── the tier follows the tile, once the window has stopped moving ──
    //
    // Acting on a change is EXPENSIVE: every record's bytes are thrown away and the whole collection is
    // read again at the new size. That is fine once, when a window has settled at a size, and very costly
    // while one is being dragged: an eighth of the window moves with every pixel of the drag, so a wall
    // that acted on each one would restart the sweep dozens of times before the listener let go of the
    // edge.
    //
    // Except the first, which lands at once: before the collection has been read there is nothing to throw
    // away, and waiting would only hold the wall's own opening.

    /// <summary>How long the window must hold still before the tier follows it.</summary>
    private static readonly TimeSpan EdgeSettle = TimeSpan.FromMilliseconds(450);

    private DispatcherQueueTimer? _edgeSettle;

    private void RefreshResidentEdge()
    {
        if (_libraryCards.Count == 0)
        {
            LibraryArt.TierMoved(dropHeld: true);
            return;
        }

        _edgeSettle ??= CreateEdgeSettleTimer();
        _edgeSettle.Stop();
        _edgeSettle.Start();
    }

    private DispatcherQueueTimer CreateEdgeSettleTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = EdgeSettle;
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            LibraryArt.TierMoved(dropHeld: true);
        };
        return timer;
    }

    /// <summary>A cover's bytes as pixels at the size a square wants them. The record screen decodes its
    /// own label art through here, so it and the wall answer the question the same way.</summary>
    private static Task<WriteableBitmap> DecodeArtAsync(byte[] bytes, int edge) => CoverDecoder.Async(bytes, edge);

    // ── the sharp layer fading in over the resident one ──
    //
    // The tween is Common.SharpFade: ONE clock for every tile fading anywhere, ticking at the same
    // rate as the badges and the playing mark, because on Skia a write repaints the whole window, so
    // what a fade costs is its writes and not its arithmetic.
    //
    // Do not drive a fade with a per-tile loop such as `while(true) { await Task.Yield(); }`. Yield does
    // not wait for a frame: it posts the continuation straight back to the dispatcher, which runs it and
    // posts it again. A 200ms fade would then write its opacity some twenty-seven THOUSAND times instead
    // of four, and every write asks for a full-window repaint, in front of a running shader, on the one
    // screen in the app that is already drawing every frame.

    /// <summary>
    /// The tile has been handed another record, or has given its sharp picture back.
    ///
    /// <para>The element is recycled, so it arrives carrying whatever opacity it was left at. Left alone, a
    /// tile that was fully sharp would show the NEXT record's small picture through a layer still claiming
    /// to be opaque, and the fade would never run again because it was already at one.</para>
    ///
    /// <para>This is also where the layer is put on the fade's watch, the same as the library window's
    /// wall, and for the same reason. See <see cref="Common.SharpFade.Watch"/>.</para>
    /// </summary>
    private void LibrarySharp_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is Image layer)
            Common.SharpFade.Watch(layer);

        if ((args.NewValue as PegasusAlbumCard)?.Sharp is null)
        {
            Common.SharpFade.Stop(sender);
            sender.Opacity = 0;
        }
    }

    /// <summary>Drops everything the wall is holding for a collection it is no longer showing.</summary>
    private void ForgetLibraryArt()
    {
        _onScreenRows.Clear();
        _libraryArt?.Forget();
    }
}
