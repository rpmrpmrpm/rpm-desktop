using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using RPM.Core.Import;
using RPM.Desktop.Presentation.Common;

namespace RPM.Desktop.Services.Art;

/// <summary>The bytes of one record's cover at one size, or null where nothing can answer.</summary>
public delegate Task<byte[]?> CoverRead(string folder, int edge, CancellationToken ct);

/// <summary>When a record's colour is read off its cover.</summary>
public enum TintReading
{
    /// <summary>On the first read of the record. This wall PAINTS the colour, for example the ring around an open
    /// card, or the ground its drawer opens on. It costs a full decode, and sometimes two, on that read.</summary>
    OnFirstRead,

    /// <summary>Behind the wall, after it is up. This is for a wall that paints no colour of its own. It reads
    /// colours only so the pack can carry them to a wall that does. A decode for each record on a screen nobody is
    /// looking at yet should not come before the covers somebody is looking at.</summary>
    BehindTheWall,
}

/// <summary>
/// A wall of covers, streamed: which record's picture to read next, at what size, on whose thread, and what to do
/// with the one it already has.
///
/// <para><b>One engine, two walls.</b> The library window's wall and the visualizer's wall both use this class. It
/// holds the pack, three workers, the frame budget, the job priorities, the off-screen sweep and the first-run pass.
/// Keeping one copy means a fault fixed for one wall is fixed for both.</para>
///
/// <para><b>The order, strictly.</b> A tile with NOTHING on it, then a tile wearing the wrong size, then whatever
/// errand the wall runs alongside its pictures (the window's wall reads a cloud mark), then the collection itself.
/// The first three are a screenful. The last is the whole library, and it is last, so it only ever runs in the
/// gaps.</para>
///
/// <para><b>It is a pull, not a queue.</b> Nothing is ever queued. So there is no list that can go stale, nothing to
/// cancel, and no way to ask for the same picture twice. The workers repeatedly ask one question: what is the most
/// valuable thing not yet done? The answer is computed from the wall's state at that instant. If the user scrolls
/// away mid-flight, the next answer is simply about somewhere else. Every decision here depends on reading the
/// wall's state fresh. So any change must keep each decision valid, whatever has happened since the last one.</para>
/// </summary>
public sealed class CoverStream
{
    /// <summary>What a wall has to tell the stream about itself. Each one is a question asked at the moment it
    /// matters, not an answer given once. A wall re-chunks its rows, changes its tile size, and swaps its collection
    /// underneath. A set of cards captured at realise is stale as soon as the zoom moves.</summary>
    /// <param name="Collection">Every record on the wall, in the order it is shown.</param>
    /// <param name="OnScreen">The records standing in the rows the repeater is holding an element for.</param>
    /// <param name="WantedEdge">The pixel edge a cover should be decoded at for the tile size right now.</param>
    /// <param name="ResidentEdge">The edge the resident tier is cut at on this display.</param>
    /// <param name="PackScope">Which collection's pack this wall reads. See <see cref="ThumbPack.ScopeFor"/>.</param>
    /// <param name="CollectionRoot">Where the records lie, for the sweep's fast path. The picture beside a record is
    /// one path test away. Asking properly is a mirror query per record to discover it was not needed. Null answers
    /// only the thorough way.</param>
    /// <param name="Read">Where a cover comes from at a given size. The two walls read different collections. The
    /// window's shows the shelf, which may be somebody else's. So neither may assume.</param>
    /// <param name="Tints">Whether the colour is read on the first read or behind the wall.</param>
    /// <param name="Hold">What decoded pictures are kept for records nobody is looking at.</param>
    public sealed record Wall(
        Func<IReadOnlyList<ICoverCard>> Collection,
        Func<IEnumerable<ICoverCard>> OnScreen,
        Func<int> WantedEdge,
        Func<int> ResidentEdge,
        Func<string> PackScope,
        Func<string?> CollectionRoot,
        CoverRead Read,
        TintReading Tints,
        CoverHold Hold)
    {
        /// <summary>Something else the wall wants done for a record on screen. It runs behind the picture and ahead
        /// of the collection. The window's wall reads its cloud mark here. A mark that arrives slightly late costs
        /// nothing. A picture that arrives late means the wall is not working.</summary>
        public Func<ICoverCard, Task>? Errand { get; init; }

        /// <summary>Whether this record still wants the errand run.</summary>
        public Func<ICoverCard, bool>? ErrandWanted { get; init; }

        /// <summary>A colour has been read for this record. The wall paints with it however it likes.</summary>
        public Action<ICoverCard, Windows.UI.Color>? Tinted { get; init; }

        /// <summary>Whether the wall IS the screen, with empty frames on it that somebody is waiting for. That is
        /// worth more of a frame than a wall that fills in quietly behind a reader.</summary>
        public Func<bool>? Urgent { get; init; }

        /// <summary>Whether the loader has the frame to itself. Nothing else is drawing, so there is nothing for it
        /// to take the frame from, and no reason to book against a budget at all. The visualizer stands its shader
        /// down while the collection is first read, so that the covers arrive faster.</summary>
        public Func<bool>? HasTheFrame { get; init; }

        /// <summary>The first pass over the collection has begun, or ended. The host acts on this. The visualizer
        /// holds its room still and keeps a cover over the wall while the pass runs.</summary>
        public Action<bool>? Warming { get; init; }

        /// <summary>How far the collection has got into being dressed. It is raised after every batch of the first
        /// pass.</summary>
        public Action<double>? Progress { get; init; }

        /// <summary>What a job cost, by kind. The wall uses this for its scroll diagnostic.</summary>
        public Action<string, double>? Spent { get; init; }
    }

    private readonly Wall _wall;
    private readonly DispatcherQueue _dispatcher;

    public CoverStream(DispatcherQueue dispatcher, Wall wall)
    {
        _dispatcher = dispatcher;
        _wall = wall;
    }

    // ══ the workers ══

    /// <summary>How many workers. Each one takes a dispatcher turn per job, so this also sets how much of a frame
    /// the loader may take. Three fills a screen quickly. It is few enough that the wall is not slow to
    /// scroll.</summary>
    private const int Workers = 3;

    /// <summary>What the loader may take of a frame while the wall is the thing being waited for. It is more than
    /// the usual share, because then the covers ARE the screen.</summary>
    private const double UrgentShareMs = 10;

    private int _workers;
    private readonly FrameBudget _budget = new();
    private readonly HashSet<ICoverCard> _inFlight = new();

    /// <summary>One clock for the pacing. It is static because it is only read for differences. A wall rebuilt during
    /// a session must not restart the window under the workers already running.</summary>
    private static readonly System.Diagnostics.Stopwatch PaceClock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Wakes the loader. It is cheap, and safe to repeat. Call it on anything that could change what is worth
    /// doing: a realise, a feed, a resize, or a cover being replaced.</summary>
    public void Wake()
    {
        while (_workers < Workers)
        {
            _workers++;
            _ = WorkAsync();
        }
    }

    /// <summary>
    /// One worker. It takes a dispatcher turn, asks for the best job, does it, and repeats until nothing is left
    /// worth doing.
    ///
    /// <para>The turn is taken at LOW priority. This is not politeness. It decides between a wall that fills in
    /// behind the user and a wall that never draws. A BitmapImage can only be decoded on the UI thread, so this work
    /// must run there. If the turns were queued back to back, the thread would never be given up. The repeater, which
    /// realises rows on that same thread, would never get a turn. The covers would stream in perfectly onto rows that
    /// are never laid out.</para>
    ///
    /// <para>A turn is not a frame, and that is the other half of it. The dispatcher drains its low queue many times
    /// inside one frame. So three workers that each take a turn can still put nine decodes into one 16 millisecond
    /// frame. A row being realised was measured at 47 milliseconds of UI thread time. So each job is also booked
    /// against the budget. A worker that has used the loader's share of the frame waits for the next one.</para>
    /// </summary>
    private async Task WorkAsync()
    {
        try
        {
            while (true)
            {
                var urgent = _wall.Urgent?.Invoke() == true;
                await YieldToLayoutAsync(urgent);

                if (NextJob() is not { } job)
                {
                    // Nothing left worth doing. If this worker is the last one out and the first pass is over, the
                    // collection is fully dressed. That is the one moment when there is a complete set to pack. It
                    // is also a moment when the loader is idle by definition.
                    if (_workers == 1 && _warmPass is null)
                        WritePackAsync();
                    return;
                }

                _inFlight.Add(job.Card);
                var spent = 0d;
                try
                {
                    if (job.Kind == JobKind.Errand)
                    {
                        await (_wall.Errand?.Invoke(job.Card) ?? Task.CompletedTask);
                    }
                    else
                    {
                        spent = await ReadAsync(job.Card, job.Edge);
                        if (job.Kind == JobKind.Pixels)
                            _decodedHeld += ArtScaler.BytesFor(job.Edge);
                    }
                }
                catch
                {
                    // A locked file, or a mirror that does not answer. The card keeps what it has, and the loader
                    // moves on. CoverMissing and MissedEdge stop the job being asked for again and again.
                }
                finally
                {
                    _inFlight.Remove(job.Card);
                    _wall.Spent?.Invoke(job.Kind == JobKind.Errand ? "badge" : "cover", spent);
                }

                // The cost to the DRAWING thread is the decode and nothing else. That cost is booked against the frame
                // it was taken from. Booking the whole elapsed time would be wrong. A cover read from disk spends most
                // of its job waiting for the pool. Charging that wait to a budget of 5 ms per frame made the loader
                // sit out frames for work that never touched them.
                if (_wall.HasTheFrame?.Invoke() == true)
                    continue;

                var now = PaceClock.Elapsed.TotalMilliseconds;
                var share = urgent ? UrgentShareMs : FrameBudget.ShareMs;
                if (_budget.Spend(now, spent, share) is > 0 and var wait)
                    await Task.Delay(TimeSpan.FromMilliseconds(wait));
            }
        }
        finally
        {
            _workers--;
        }
    }

    internal readonly record struct Job(ICoverCard Card, int Edge, JobKind Kind);

    internal enum JobKind
    {
        /// <summary>Read a record's picture at a size, from the disk, the network, or the bytes in hand.</summary>
        Cover,

        /// <summary>Whatever else the wall wants done for a record on screen.</summary>
        Errand,

        /// <summary>Pixels for a record the user has not reached yet, from bytes already held. This gives a tile that
        /// paints on the frame it is realised, rather than a frame or two later. That is what makes a fast scroll down
        /// a long wall look smooth.</summary>
        Pixels,
    }

    /// <summary>The most valuable job not yet done, or null when the wall is fully dressed. It is answered from the
    /// wall's state at this moment. So a scroll can change the priorities without anything being cancelled or
    /// re-queued.</summary>
    private Job? NextJob() => Pick(
        _wall.OnScreen(),
        _wall.Collection(),
        _wall.WantedEdge(),
        _residentEdge,
        _inFlight.Contains,
        _wall.Errand is not null ? _wall.ErrandWanted : null,
        sweeping: _warmPass is not null,
        holdsMoreDecoded: HoldsMoreDecoded);

    /// <summary>
    /// The order, written out in full as a function of the wall's state, so it can be checked without a wall.
    ///
    /// <para>A tile with NOTHING on it, then a tile wearing the wrong size, then the wall's own errand, then the
    /// collection, then pixels for records nobody has reached yet. The first three are a screenful. The last two are
    /// the whole library. They come last, so they only ever run in the gaps.</para>
    /// </summary>
    /// <param name="busy">Whether a worker is already reading this record, so two workers never take the same job.</param>
    /// <param name="errandWanted">Whether this record still wants the wall's errand run, or null where the wall has
    /// no errand.</param>
    /// <param name="sweeping">Whether the first pass owns the collection at the moment. Offering the same records
    /// here as single jobs would make the two race for them.</param>
    /// <param name="holdsMoreDecoded">Whether this wall keeps pixels ahead of the screen, and has room for more of
    /// them.</param>
    internal static Job? Pick(
        IEnumerable<ICoverCard> onScreen,
        IReadOnlyList<ICoverCard> collection,
        int wantedEdge,
        int residentEdge,
        Func<ICoverCard, bool> busy,
        Func<ICoverCard, bool>? errandWanted,
        bool sweeping,
        bool holdsMoreDecoded)
    {
        // A tile with NOTHING on it first, ahead of one that only has the wrong size. The two do not have the same
        // urgency. An empty frame is a record the user cannot see. A cover one size off is a record they can see.
        // Without this rule, the loader took whichever the row set gave first. So a window resize, which puts every
        // tile on screen one size off at once, was served ahead of the blank tiles that a scroll had just uncovered.
        foreach (var card in onScreen)
            if (card.Cover is null
                && CoverTier.WorthAsking(card.CoverEdge, card.MissedEdge, wantedEdge, card.CoverMissing)
                && !busy(card))
                return new Job(card, wantedEdge, JobKind.Cover);

        foreach (var card in onScreen)
            if (CoverTier.WorthAsking(card.CoverEdge, card.MissedEdge, wantedEdge, card.CoverMissing)
                && !busy(card))
                return new Job(card, wantedEdge, JobKind.Cover);

        if (errandWanted is { } wants)
            foreach (var card in onScreen)
                if (wants(card) && !busy(card))
                    return new Job(card, 0, JobKind.Errand);

        if (sweeping)
            return null;

        // The fallback, for whatever the pass did not reach. This is about BYTES held, not pixels decoded. A record
        // whose picture the off-screen sweep has given back still has its bytes. Offering it here would mean a disk
        // read for something already in memory.
        foreach (var card in collection)
            if (card.ResidentBytes is not { Length: > 0 }
                && CoverTier.WorthAsking(card.CoverEdge, card.MissedEdge, residentEdge, card.CoverMissing)
                && !busy(card))
                return new Job(card, residentEdge, JobKind.Cover);

        // And last of all, pixels for records nobody has reached yet, from bytes already in hand. This applies to a
        // wall that keeps a tier for its whole collection. It runs behind everything, and it is paced against the
        // frame like any other job, because it is work for a part of the wall nobody is looking at.
        if (holdsMoreDecoded)
            foreach (var card in collection)
                if (card.Tiny is null && card.ResidentBytes is { Length: > 0 } && !busy(card))
                    return new Job(card, residentEdge, JobKind.Pixels);

        return null;
    }

    /// <summary>Hands the UI thread back, and takes it again behind everything that draws.</summary>
    private Task YieldToLayoutAsync(bool urgent)
    {
        var turn = new TaskCompletionSource();
        var priority = urgent ? DispatcherQueuePriority.Normal : DispatcherQueuePriority.Low;
        if (!_dispatcher.TryEnqueue(priority, () => turn.TrySetResult()))
            turn.TrySetResult();   // No dispatcher to wait on. The caller's own checks still apply.
        return turn.Task;
    }

    // ══ reading one record ══

    /// <summary>
    /// One record's picture at one size, put straight onto the card.
    ///
    /// <para>The picture is requested at the size it will be DRAWN at, not at one fixed size for every tile. Zoomed
    /// out, that is a 128 px read. Zoomed in, it is 512 or 640. The picture is sharper because the bytes behind it
    /// are bigger, not because a small picture was decoded large.</para>
    /// </summary>
    /// <returns>What the job cost the drawing thread. This is the decode and nothing else.</returns>
    private async Task<double> ReadAsync(ICoverCard card, int edge)
    {
        var spent = 0d;
        var resident = edge <= _residentEdge;
        var wanted = _wall.WantedEdge();

        // What the record already carries is checked first. This is the path that nearly every tile takes once the
        // collection has been swept: a decode of bytes in hand, and nothing else.
        if (resident && card.ResidentBytes is { Length: > 0 } held)
        {
            ApplyResident(card, await Decode(held, edge), edge, wanted);
            return spent;
        }

        // Then the pack. It answers with the colour too. So a collection packed once never touches the disk for each
        // record again, and it never decodes a cover again just to read its tint.
        if (resident && _pack.TryGetValue(card.Folder, out var packed))
        {
            card.ResidentBytes = packed.Bytes;
            card.Tint ??= packed.Tint;
            if (card.Tint is { } packedTint)
                _wall.Tinted?.Invoke(card, packedTint);
            ApplyResident(card, await Decode(packed.Bytes, edge), edge, wanted);
            return spent;
        }

        if (await _wall.Read(card.Folder, edge, CancellationToken.None) is not { Length: > 0 } bytes)
        {
            // Nothing came back. What that means depends on whether this record has ever shown a picture. See
            // CoverTier.Latches.
            if (CoverTier.Latches(HasPicture(card)))
                card.CoverMissing = true;
            else
                card.MissedEdge = edge;
            return spent;
        }

        card.MissedEdge = 0;

        // The tint belongs to the COVER, not to the tile. It does not change when the wall is resized, and it does
        // not change when a card scrolls back. Reading it is a full decode, twice over when the cover has no vivid
        // colour and the dominant colour is asked for too. So it is read once per record and kept.
        if (_wall.Tints == TintReading.OnFirstRead && card.Tint is null
            && await Task.Run(() => CoverColor.Highlight(bytes) ?? CoverColor.Dominant(bytes)) is { } colour)
        {
            card.Tint = colour;
            _wall.Tinted?.Invoke(card, colour);
        }

        var bitmap = await Decode(bytes, edge);

        if (resident)
        {
            card.ResidentBytes = bytes;
            _packDirty = true;
            ApplyResident(card, bitmap, edge, wanted);
            return spent;
        }

        // The sharp tier: its own layer, which fades in over the resident one.
        card.Sharp = bitmap;

        // ...and the ground under it, where there is not one yet. A record first met at a large tile takes this path
        // before the collection pass has read it. For that time, the sharp layer is the only picture it has. A card
        // that holds a picture only in Sharp, with nothing in Cover, looks like a record with no artwork to every
        // surface that asks.
        card.Cover ??= bitmap;
        card.CoverEdge = edge;
        return spent;

        async Task<WriteableBitmap> Decode(byte[] raw, int at)
        {
            var began = PaceClock.Elapsed.TotalMilliseconds;
            try
            {
                return await CoverDecoder.Async(raw, at);
            }
            finally
            {
                spent += PaceClock.Elapsed.TotalMilliseconds - began;
            }
        }
    }

    private static bool HasPicture(ICoverCard card)
        => card.ResidentBytes is { Length: > 0 } || card.Tiny is not null || card.Sharp is not null;

    /// <summary>
    /// Puts the resident picture up as the bottom layer, and records that the card now has a picture at exactly this
    /// size.
    ///
    /// <para>The sharp layer goes too, but only where the resident size is what this zoom asks for. Where the zoom
    /// asks for MORE, the sharp layer is the picture on screen. This read is the small picture arriving underneath it
    /// late. Removing the sharp layer there would blur a tile the user is looking at, and then read it again. See
    /// <see cref="CoverTier.KeepsSharp"/>.</para>
    /// </summary>
    private static void ApplyResident(ICoverCard card, WriteableBitmap bitmap, int edge, int wanted)
    {
        card.Tiny = bitmap;
        card.Cover = bitmap;

        if (CoverTier.KeepsSharp(edge, wanted, card.Sharp is not null))
            return;   // The large picture is still the one this zoom asks for. CoverEdge stays at its own edge.

        card.Sharp = null;
        card.CoverEdge = edge;
    }

    // ══ the resident tier and its pack ══

    private int _residentEdge = ArtScaler.TinyEdge;
    private Dictionary<string, ThumbPack.Entry> _pack = new(StringComparer.OrdinalIgnoreCase);
    private int _packEdge;
    private bool _packDirty;

    /// <summary>
    /// Settles the tier against what the wall now asks for. If that moved it, the pack is read again. A pack is
    /// written per edge, so a different tier is a different file.
    /// </summary>
    /// <param name="dropHeld">Whether everything held is now the wrong size. True for a wall whose tier follows its
    /// tile. Then the bytes are dropped, not kept and scaled up, and the collection is read again at the new size.
    /// False for a wall whose tier is a floor that only moves when the window changes display. There, what is held is
    /// still exactly what it was cut for.</param>
    public void TierMoved(bool dropHeld)
    {
        var edge = _wall.ResidentEdge();
        var moved = edge != _residentEdge;
        _residentEdge = edge;

        // The pack is read whether or not the figure moved. "The tier is what it always was" and "the pack for it
        // has been read" are two different questions. On the first feed of a wall, the answer to the second is no
        // while the answer to the first is yes. ReadPackAsync does nothing when the pack in hand already matches
        // this edge.
        if (!moved)
        {
            ReadPackAsync();
            return;
        }

        _decodedHeld = 0;

        if (dropHeld)
        {
            // The DECODED pictures are left where they are. They are the wrong size, but they are what is on the
            // screen. A wall that blanked itself the moment a window stopped moving would be worse than one a size
            // off for a second. Each one is replaced when its record is read again.
            foreach (var card in _wall.Collection())
            {
                card.ResidentBytes = null;
                card.CoverMissing = false;
                card.MissedEdge = 0;
            }
        }

        ReadPackAsync();

        if (dropHeld && _wall.Collection().Count > 0)
            Warm();

        Wake();
    }

    /// <summary>Reads the pack for the current tier, off the UI thread, then wakes the loader. It is one open and one
    /// sequential read for the whole collection. See <see cref="ThumbPack"/>.</summary>
    private async void ReadPackAsync()
    {
        var edge = _residentEdge;
        if (_packEdge == edge)
            return;
        _packEdge = edge;

        // The held pack is dropped BEFORE the read, and that is not tidiness. What is in hand was cut for the size
        // the wall WAS drawing. The loader reads the pack on every job. If the old pack stayed for the second or two
        // this read takes, it would answer a screenful of tiles with pictures of the old size. Those would then be
        // marked as the new size, and never fetched again.
        _pack = new Dictionary<string, ThumbPack.Entry>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var scope = _wall.PackScope();
            var pack = await Task.Run(() => ThumbPack.Load(edge, scope));
            if (_packEdge != edge)
                return;   // The tier moved again during this read. That later read owns the pack.
            _pack = pack;
        }
        catch
        {
            // No pack means a first run.
        }
        Wake();
    }

    /// <summary>Writes the pack back once the collection is dressed. That is the one moment when there is a complete
    /// set worth writing. It is also a moment when the loader is idle by definition.</summary>
    private async void WritePackAsync()
    {
        if (!_packDirty)
            return;
        _packDirty = false;

        var entries = new Dictionary<string, ThumbPack.Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in _wall.Collection())
            if (card.ResidentBytes is { Length: > 0 } bytes)
                entries[card.Folder] = new ThumbPack.Entry(bytes, card.Tint);

        if (entries.Count == 0)
            return;

        var snapshot = entries;
        var edge = _residentEdge;
        var scope = _wall.PackScope();
        var alsoWanted = KeepPacks;

        try
        {
            await Task.Run(() =>
            {
                ThumbPack.Save(edge, snapshot, scope);

                // ...and the packs for sizes that nothing reads. A wall that has moved to another edge leaves a pack
                // of covers cut at the old size. That is tens of megabytes of a size that nobody uses. Every edge
                // still wanted is named, so the OTHER wall's pack is never the one removed.
                ThumbPack.Prune(scope, [edge, .. alsoWanted()]);
            });
        }
        catch
        {
            // A slower start next time. It is not a fault.
        }
    }

    /// <summary>The edges other walls read, so that the prune does not remove their packs. Set this where the host
    /// knows there is another wall. An empty list is safe. It only means a pack is derived again.</summary>
    public Func<IReadOnlyList<int>> KeepPacks { get; set; } = () => [];

    // ══ dressing the whole collection ══
    //
    // The first pass over a library is a different job from keeping a screenful current. Running it through the same
    // one-record-at-a-time loader is what made opening a wall slow. Two things cost time, and neither is the decode:
    //
    //   A dispatcher turn per record. The loader hands the thread back before every job. That is right when it is
    //   filling in behind a user who is scrolling. It is wasteful when nothing is on screen yet and there are a few
    //   thousand records to go through. Taken in batches, one turn covers sixteen records.
    //
    //   A mirror query per record. Reading a cover properly means checking the folder's own picture, then the first
    //   track's tags, then the network, then the cloud. Each step after the first starts with a query for a track in
    //   that folder. For a collection where most records DO have a cover.jpg beside them, that is one query per
    //   record to find out it was not needed.
    //
    // So the wall reads the cheap way first, over everything. Only after that does it go back for the records that
    // had nothing beside them.

    private Task? _warmPass;

    /// <summary>How many records are read at once. They are file reads on the pool, so they overlap. The number is
    /// what one dispatcher turn's worth of decoding comes to on the way back.</summary>
    private const int WarmBatch = 16;

    /// <summary>Starts the first pass over the collection, or leaves the one already running alone.</summary>
    public void Warm() => _warmPass ??= WarmAsync();

    private async Task WarmAsync()
    {
        _wall.Warming?.Invoke(true);
        try
        {
            // The quick way over everything, then the thorough way over what it missed. Then round again if the tier
            // moved during the pass. A window dragged wider during the sweep changes the size every record is wanted
            // at. The records the sweep has already passed were read at the old size.
            var swept = -1;
            while (swept != _residentEdge)
            {
                swept = _residentEdge;
                await SweepAsync(quick: true);
                await SweepAsync(quick: false);
            }
        }
        catch
        {
            // A collection that went away while it was being read. What arrived is still on the wall.
        }
        finally
        {
            _warmPass = null;
            Report();          // The last batch's figure, before anybody is told it is over.
            _wall.Warming?.Invoke(false);
            Wake();            // Whatever is on screen still wants its sharp covers.
        }

        if (_wall.Tints == TintReading.BehindTheWall)
            await WarmTintsAsync();
        WritePackAsync();
    }

    /// <param name="quick">Reads only the picture lying beside the record. That is one path test, and at worst one
    /// small file. False asks properly. That is a mirror query, and possibly a tag read. It is worth it only for the
    /// records the quick way could not answer.</param>
    private async Task SweepAsync(bool quick)
    {
        var root = _wall.CollectionRoot();
        if (quick && string.IsNullOrEmpty(root))
            return;   // There is nowhere to look beside a record. The thorough pass answers for everything.

        var pending = new List<ICoverCard>(WarmBatch);

        // The WHOLE collection, with no cap. A wall the user was told is their collection has to be their collection
        // all the way down. A sweep stopped at a figure worked out from a memory budget would stop in the middle of a
        // row. Covers would appear up to that point, and empty frames after it, with nothing on screen to say why.
        // What is capped is the PIXELS (see CoverHold). The bytes are tens of kilobytes each, and the pack holds them
        // all anyway.
        foreach (var card in _wall.Collection())
        {
            if (card.ResidentBytes is { Length: > 0 } || card.CoverMissing)
                continue;

            pending.Add(card);
            if (pending.Count < WarmBatch)
                continue;

            await SweepBatchAsync(pending, root, quick);
            pending.Clear();
        }

        if (pending.Count > 0)
            await SweepBatchAsync(pending, root, quick);
    }

    /// <summary>What a batch's read came back with, per record.</summary>
    private readonly record struct SweepRead(byte[]? Bytes, Windows.UI.Color? Tint, bool FromPack);

    private async Task SweepBatchAsync(List<ICoverCard> batch, string? root, bool quick)
    {
        var edge = _residentEdge;
        var cards = batch.ToArray();

        var reads = await Task.WhenAll(cards.Select(card => Task.Run(() =>
        {
            // Each record's read is caught on its OWN, never around the WhenAll. If the catch were around the whole
            // batch, one locked file, one path the filesystem would not answer for, or one picture the cutter could
            // not read would throw out through the sweep. The guard around the whole thing would then swallow it, and
            // every record BELOW it would stay undressed. That gives a wall that loads down to a point and is blank
            // from there on. A record that will not read is one blank tile, never the rest of the wall.
            try
            {
                if (_pack.TryGetValue(card.Folder, out var packed))
                    return new SweepRead(packed.Bytes, packed.Tint, true);

                if (!quick || root is not { Length: > 0 })
                    return default;   // Filled in below, by the thorough reading.

                // The fast path: the picture lying beside the record, cut to size BY PATH, so the scan itself is
                // never opened.
                var directory = Path.Combine(root, card.Folder.Replace('/', Path.DirectorySeparatorChar));
                return FolderArtWriter.FindFolderImage(directory) is { } image
                    ? new SweepRead(ThumbCache.GetFile(image, edge), null, false)
                    : default;
            }
            catch
            {
                return default;   // The thorough pass gets its turn at this one.
            }
        })));

        // The thorough way for the leftovers. It still runs overlapped, and still off this thread. A record is marked
        // as ASKED rather than just empty, because the two mean different things to the tile. Nothing found means the
        // record has no art. A read that threw means nobody has managed to ask about it yet. Only the first of those
        // may be written off.
        var asked = new bool[cards.Length];
        if (!quick)
        {
            var slow = await Task.WhenAll(cards.Select(async (card, i) =>
            {
                if (reads[i].FromPack)
                    return (reads[i].Bytes, Asked: true);
                try
                {
                    return (await _wall.Read(card.Folder, edge, CancellationToken.None), Asked: true);
                }
                catch
                {
                    return ((byte[]?)null, Asked: false);
                }
            }));
            for (var i = 0; i < reads.Length; i++)
            {
                asked[i] = slow[i].Asked;
                if (!reads[i].FromPack)
                    reads[i] = new SweepRead(slow[i].Item1, null, false);
            }
        }

        // The bytes go onto the records. A decode is done ONLY for what the user can see. The sweep's job is the
        // BYTES: tens of kilobytes each, and the pack holds them anyway. Pixels are half a megabyte each. Sixteen of
        // them back to back inside one dispatcher turn is a frame and a half of UI thread time. On a wall being
        // scrolled, that is the sweep making the scroll stutter. Whatever the wall holds decoded AHEAD of the screen
        // is fetched by the loader instead, one job at a time, paced against the frame. See the pixels job in
        // NextJob.
        var visible = new HashSet<ICoverCard>(_wall.OnScreen());
        var wanted = _wall.WantedEdge();

        for (var i = 0; i < cards.Length; i++)
        {
            var card = cards[i];
            var read = reads[i];

            if (read.Bytes is not { Length: > 0 } raw)
            {
                if (!quick && asked[i] && CoverTier.Latches(HasPicture(card)))
                    card.CoverMissing = true;
                continue;
            }

            card.ResidentBytes = raw;
            card.Tint ??= read.Tint;
            if (card.Tint is { } tint)
                _wall.Tinted?.Invoke(card, tint);
            if (!read.FromPack)
                _packDirty = true;

            if (!visible.Contains(card) || (card.CoverEdge == edge && card.Tiny is not null))
                continue;

            try
            {
                ApplyResident(card, await CoverDecoder.Async(raw, edge), edge, wanted);
            }
            catch
            {
                // Not a picture this decoder knows. The frame keeps its quiet square.
            }
        }

        // Anything realised while this batch was out still wants its picture. The workers may well have run out of
        // work and gone. Waking them is safe to repeat, and it costs nothing when there is nothing to do.
        Wake();
        Report();
        await YieldToLayoutAsync(_wall.Urgent?.Invoke() == true);
    }

    /// <summary>How much of the collection is held decoded ahead of being looked at.</summary>
    private long _decodedHeld;

    private bool HoldsMoreDecoded => _decodedHeld < _wall.Hold.DecodedCollection;

    /// <summary>
    /// The colour off every record that has no colour yet, once the wall is up and the user is already reading it.
    /// This is only for a wall that paints none of its own. See <see cref="TintReading"/>.
    /// </summary>
    private async Task WarmTintsAsync()
    {
        var pending = new List<ICoverCard>(WarmBatch);
        foreach (var card in _wall.Collection())
        {
            if (card.Tint is not null || card.ResidentBytes is not { Length: > 0 })
                continue;

            pending.Add(card);
            if (pending.Count < WarmBatch)
                continue;

            await TintBatchAsync(pending);
            pending.Clear();
        }

        if (pending.Count > 0)
            await TintBatchAsync(pending);
    }

    private async Task TintBatchAsync(List<ICoverCard> batch)
    {
        var cards = batch.ToArray();
        var tints = await Task.WhenAll(cards.Select(card => Task.Run(() =>
        {
            try
            {
                return card.ResidentBytes is { Length: > 0 } raw
                    ? CoverColor.Highlight(raw) ?? CoverColor.Dominant(raw)
                    : null;
            }
            catch
            {
                return null;   // The other wall reads its own colour for this record.
            }
        })));

        for (var i = 0; i < cards.Length; i++)
            cards[i].Tint ??= tints[i];

        // Never urgent. This colour is for a wall in another window, gathered behind a screen the user is already
        // using.
        await YieldToLayoutAsync(urgent: false);
    }

    /// <summary>
    /// How far the collection has got into being dressed, as a fraction. It is raised after every batch.
    ///
    /// <para>It is counted over the RECORDS, not over the sweep's own progress. That keeps it honest across both
    /// passes. The quick sweep dresses most of the collection, and the thorough one goes back over what it missed. A
    /// figure taken from either pass alone would reach a hundred percent and then start again. A record is done when
    /// it has a picture, or when it has been asked properly and has none.</para>
    /// </summary>
    private void Report()
    {
        if (_wall.Progress is not { } raise)
            return;

        var cards = _wall.Collection();
        if (cards.Count == 0)
            return;

        var done = 0;
        foreach (var card in cards)
            if (card.ResidentBytes is { Length: > 0 } || card.CoverMissing)
                done++;

        raise(done / (double)cards.Count);
    }

    // ══ giving the big pictures back ══

    /// <summary>When each off-screen record's row was reclaimed. A record realised again is taken off this list
    /// before the sweep sees it. That is the point. A row bouncing on the edge of the realisation window costs
    /// nothing at all.</summary>
    private readonly Dictionary<ICoverCard, long> _offScreen = new();

    private static readonly System.Diagnostics.Stopwatch OffScreenClock = System.Diagnostics.Stopwatch.StartNew();

    private DispatcherQueueTimer? _sweepTimer;

    /// <summary>A record is on screen again. It came back before the sweep reached it, so it still has its picture.
    /// Anything it lacks is put back from what it kept.</summary>
    public void Realized(ICoverCard card)
    {
        _offScreen.Remove(card);
        if (card.Cover is null && card.Tiny is { } tiny)
        {
            card.Cover = tiny;
            card.CoverEdge = _residentEdge;
        }
    }

    /// <summary>A record's row has been reclaimed. It keeps its picture for now. See <see cref="CoverHold"/> for how
    /// long, and how much.</summary>
    public void Reclaimed(ICoverCard card)
    {
        if (card.Sharp is null && !(_wall.Hold.DropsResident && card.Cover is not null))
            return;   // Nothing decoded that is worth giving back.

        _offScreen[card] = OffScreenClock.ElapsedMilliseconds;
        SweepLater();
    }

    private void SweepLater()
    {
        if (_sweepTimer is not null)
            return;

        _sweepTimer = _dispatcher.CreateTimer();
        _sweepTimer.Interval = TimeSpan.FromSeconds(1);
        _sweepTimer.IsRepeating = true;
        _sweepTimer.Tick += (_, _) => SweepOffScreen();
        _sweepTimer.Start();
    }

    private void SweepOffScreen()
    {
        var now = OffScreenClock.ElapsedMilliseconds;
        var entries = _offScreen.Select(p => (Card: p.Key, Since: p.Value,
                                              Edge: p.Key.CoverEdge > 0 ? p.Key.CoverEdge : _residentEdge));
        var dropped = _wall.Hold.ToDrop(entries, now);

        foreach (var card in dropped)
        {
            // The PIXELS go and the bytes stay. Decoded, a cover is half a megabyte. Encoded, it is a few tens of
            // kilobytes. So a record this far from the screen gives back the expensive half. Coming back to it is a
            // decode from memory, not a read from disk.
            card.Sharp = null;
            if (_wall.Hold.DropsResident)
            {
                card.Tiny = null;
                card.Cover = null;
                card.CoverEdge = 0;
            }
            else
            {
                card.CoverEdge = card.Tiny is null ? 0 : _residentEdge;
            }
            _offScreen.Remove(card);
        }

        // Either something is still waiting that the grace period has not reached, or nothing is over the budget.
        // Either way there is no work here until more rows leave. The next row to leave starts the clock again.
        if ((_offScreen.Count > 0 && _wall.Hold.Grace > TimeSpan.Zero) || _sweepTimer is not { } timer)
            return;
        if (dropped.Count > 0)
            return;
        _sweepTimer = null;
        timer.Stop();
    }

    /// <summary>Drops what is held for records that have left the wall. The sweep only looks at what is waiting. A
    /// card that is not in the collection would otherwise sit in that list, holding its pixels until the grace
    /// period ended.</summary>
    /// <param name="stillThere">Whether this record is still one of the wall's.</param>
    public void ForgetGone(Func<ICoverCard, bool> stillThere)
    {
        foreach (var card in _offScreen.Keys.Where(c => !stillThere(c)).ToList())
            _offScreen.Remove(card);
    }

    /// <summary>Drops everything the stream is holding for a collection that is not on show.</summary>
    public void Forget()
    {
        _offScreen.Clear();
        _inFlight.Clear();
        _decodedHeld = 0;
        _sweepTimer?.Stop();
        _sweepTimer = null;
    }
}
