using RPM.Desktop.Presentation.Main.Genres.Models;
using RPM.Desktop.Services.Art;

using Microsoft.UI.Dispatching;

namespace RPM.Desktop.Presentation.Main.Genres;

/// <summary>
/// The one way a genre card gets its photograph, and the reason the wall stays smooth while it does.
///
/// <para><b>Why this exists.</b> The obvious approach starts one fetch from the realize callback for each
/// card, as soon as the repeater hands it an element: <c>foreach (var card in row.Cards) _ =
/// FetchAsync(card)</c>. Each fetch ends in a <c>BitmapImage</c>, which can only be decoded on the thread
/// that draws. Nothing limits how many of them land in one frame. A screenful of cards realized together
/// queues a screenful of decodes at normal priority. The repeater realizes rows on that same thread, so it
/// gets no turn while they run. On a large library this kept the UI thread busy for about two thirds of a
/// second, and none of the wall's own timed code ran in that time.</para>
///
/// <para><b>What it does instead.</b> It works the way the album wall's loader works. A fixed number of
/// workers each take their turn at the LOWEST dispatcher priority, so layout and input go first. Each
/// worker records what it spent on the drawing thread against <see cref="FrameBudget"/>. The pictures
/// therefore take a share of a frame and give the rest back. See <c>CoverStream</c>, which describes the
/// same approach in more detail.</para>
///
/// <para><b>The card is finished before this runs.</b> Its colour, its name and its lettering come from the
/// card itself. The photograph is added afterwards. Nothing here can hold any of that up, so the wall still
/// reads when there is no network.</para>
///
/// <para><b>Events that can arrive out of order, and why each one is safe.</b></para>
/// <list type="bullet">
/// <item>A card is realized while a worker is already holding it. The card is marked
/// <see cref="GenreTile.Asked"/> as soon as a worker takes it, before any await. So the second look skips
/// it.</item>
/// <item>A card is recycled between being taken and its picture arriving. The picture is set on the CARD,
/// not on an element. Whether it is shown is checked on the card at every realize (see
/// GenreWallView.RowPrepared). The result is the same whichever order the two happen in.</item>
/// <item>The wall is rebuilt while a worker is running. Cards are kept by genre across a rebuild, so the
/// card being filled in is still on the wall. A card that is no longer on the wall drops out of
/// <see cref="Wall.Everything"/> and is never asked about again.</item>
/// <item>The view is let go in the middle of a fetch. <see cref="Stop"/> cancels the token the workers wait
/// on. A card that was taken but not finished is released. The WORK is not cancelled, because it is shared
/// and cached (see <see cref="GenreFaces"/>). The next pass gets its answer from the cache.</item>
/// <item>Two walls exist at once: the shell's and one in a panel. Each has its own stream and its own
/// cards. They meet only at <see cref="GenreFaces"/>, which is keyed by genre and gives both the same
/// task.</item>
/// </list>
/// </summary>
internal sealed class GenreFaceStream
{
    /// <summary>Everything the stream needs from the wall. It is ASKED for each time, not kept: a wall is
    /// re-paired, resized and re-fed while the workers run. A list taken when a worker started would hold
    /// cards that are no longer on the wall.</summary>
    /// <param name="OnScreen">The cards the repeater is holding an element for, now.</param>
    /// <param name="Everything">Every card on the wall, in reading order.</param>
    /// <param name="Ready">Whether there is anything to read WITH. This is stated, not assumed. The wall is
    /// laid out before the page gives it its delegate. A worker that ran in that gap would mark every card
    /// as asked and fill none of them, which would leave thirty-six cards with no photograph.</param>
    /// <param name="Read">A genre's photograph as bytes, or null where none of its acts has one.</param>
    /// <param name="Colour">Reads the card's colour from those bytes and puts it on the card. It runs on
    /// the pool, and it is deliberately outside the time that <see cref="FillAsync"/> measures. It is a
    /// decode that never touches the drawing thread. Counting it against the frame budget would make the
    /// loader wait for frames that never paid for it.</param>
    /// <param name="Show">Puts those bytes on that card and shows it. This is the wall's delegate, not this
    /// type's. What it costs is a decode on the drawing thread, which is the thing this type paces. A
    /// loader that did the decoding itself could only be measured in a real window.</param>
    internal sealed record Wall(
        Func<IEnumerable<GenreTile>> OnScreen,
        Func<IEnumerable<GenreTile>> Everything,
        Func<bool> Ready,
        Func<GenreTile, CancellationToken, Task<byte[]?>> Read,
        Func<GenreTile, byte[], Task> Colour,
        Func<GenreTile, byte[]?, Task> Show)
    {
        /// <summary>How much drawing-thread time a job used, for the wall's scroll diagnostic.
        /// Optional.</summary>
        public Action<string, double>? Spent { get; init; }
    }

    /// <summary>
    /// How many cards are filled in at once.
    ///
    /// <para>Two. <see cref="GenreFaces"/> already allows only two at a time. A third worker would wait on
    /// that limit instead of decoding, and a turn spent waiting is a turn the layout could have used. The
    /// album wall runs three, because a cover is a disk read. A genre's face needs two catalogue lookups
    /// and two neural nets, so the limit in <see cref="GenreFaces"/> is the real ceiling.</para>
    /// </summary>
    private const int Workers = 2;

    /// <summary>The queue whose turns the workers take. It is null when there is none, which is the case
    /// for a wall built without a window, as in the tests of the ordering below. Then there is nothing to
    /// pace against and the order stays the same. That order is the part the tests check.</summary>
    private readonly DispatcherQueue? _dispatcher;
    private readonly Wall _wall;
    private readonly FrameBudget _budget = new();

    /// <summary>The clock the pacing is measured on. Only this wall uses it. The budget is this wall's
    /// share of a frame, and the album wall's loader has its own clock.</summary>
    private readonly System.Diagnostics.Stopwatch _pace = System.Diagnostics.Stopwatch.StartNew();

    private int _workers;
    private CancellationTokenSource _life = new();

    public GenreFaceStream(DispatcherQueue? dispatcher, Wall wall)
    {
        _dispatcher = dispatcher;
        _wall = wall;
    }

    /// <summary>Wakes the workers. This is cheap, and calling it many times is safe. Call it whenever
    /// something may have changed what is worth doing: a realize, a feed, a re-pair or a resize.</summary>
    public void Wake()
    {
        // A view that was let go and is shown again. The token that stopped the last set of workers would
        // refuse every fetch this wall makes from now on, so a new token is made.
        if (_life.IsCancellationRequested)
            _life = new CancellationTokenSource();

        var token = _life.Token;

        // The number to start is decided ONCE, before any worker starts. A worker whose turn does not
        // actually wait runs its whole loop inline. That happens when there is no dispatcher to wait on.
        // Such a worker has already counted itself out before this reads the count again. A check that read
        // the count again would start another worker, and another, without end. A test that hit this case
        // never returned.
        //
        // The increment is still interlocked and still checked. The count is written by whichever thread a
        // worker finishes on, and read by whichever thread is waking it.
        var wanted = Workers - Volatile.Read(ref _workers);
        for (var i = 0; i < wanted; i++)
        {
            if (Interlocked.Increment(ref _workers) > Workers)
            {
                Interlocked.Decrement(ref _workers);
                return;
            }

            _ = WorkAsync(token);
        }
    }

    /// <summary>Called when the view is let go. The workers stop, but the work they started does not. That
    /// work is shared and cached per genre. A wall that is shown again is answered from the cache and does
    /// not ask again.</summary>
    public void Stop() => _life.Cancel();

    /// <summary>
    /// One worker: take a turn, take the best card, fill it in, repeat until there is nothing left.
    ///
    /// <para>The turn is at LOW priority. That is what keeps the wall scrollable. A BitmapImage can only be
    /// decoded on the drawing thread, so the work has to run there. If it were queued at normal priority,
    /// it would never give the thread up, and the repeater would never get to realize a row. A turn is not
    /// a frame. The dispatcher drains its low queue many times in one frame, so the decode is also recorded
    /// against the budget. A worker that has used its share of the frame waits for the next one.</para>
    /// </summary>
    private async Task WorkAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await YieldToLayoutAsync();
                if (ct.IsCancellationRequested)
                    return;

                if (!_wall.Ready() || NextCard() is not { } card)
                    return;

                // Marked BEFORE any await. The other worker, and any realize that happens between the two,
                // read this flag and skip the card. So its picture is never fetched twice.
                card.Asked = true;

                var spent = 0d;
                try
                {
                    spent = await FillAsync(card, ct);
                }
                catch (OperationCanceledException)
                {
                    // The wall was let go while this card was waiting. The card is unmarked, so the next
                    // pass takes it. By then the answer is in the cache.
                    card.Asked = false;
                    return;
                }
                catch
                {
                    // A picture is decoration. The card keeps its colour and its name, which are all it
                    // needs. It is not asked again.
                }
                finally
                {
                    _wall.Spent?.Invoke("face", spent);
                }

                if (_budget.Spend(_pace.Elapsed.TotalMilliseconds, spent) is > 0 and var wait)
                    await Task.Delay(TimeSpan.FromMilliseconds(wait), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The wait between frames, ended early when the view is let go.
        }
        finally
        {
            Interlocked.Decrement(ref _workers);
        }
    }

    /// <summary>Puts one card's photograph on the card. Returns what it cost the DRAWING thread, which is
    /// the showing and nothing else. The fetch and the models run on the pool. Counting those against the
    /// frame budget would make the loader wait for frames they never used.</summary>
    private async Task<double> FillAsync(GenreTile card, CancellationToken ct)
    {
        // There is no picture for this shelf. The card waited for an answer, so it keeps its own colour and
        // shows that, instead of staying a blank plate for the rest of the session.
        if (await _wall.Read(card, ct) is not { Length: > 0 } bytes)
        {
            var settle = System.Diagnostics.Stopwatch.StartNew();
            await _wall.Show(card, null);
            return settle.Elapsed.TotalMilliseconds;
        }

        ct.ThrowIfCancellationRequested();

        // The colour first, and off the clock: see Wall.Colour.
        await _wall.Colour(card, bytes);
        ct.ThrowIfCancellationRequested();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await _wall.Show(card, bytes);
        return clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// The card worth filling in next: one on screen if there is one, otherwise the next one anywhere.
    ///
    /// <para>On-screen cards come first, because somebody is waiting for their pictures. The rest come
    /// after. A card scrolled to with its photograph already loaded paints in the frame it is realized in.
    /// A wall of thirty-six genres is a fixed amount of quiet work, not an endless sweep.</para>
    ///
    /// <para>The list is read from the wall on each call, not kept in a queue. A wall is re-paired and
    /// re-fed while the workers run. A queue built when the first worker started would name cards that are
    /// no longer on the wall.</para>
    /// </summary>
    private GenreTile? NextCard()
    {
        foreach (var card in _wall.OnScreen())
            if (!card.Asked)
                return card;

        foreach (var card in _wall.Everything())
            if (!card.Asked)
                return card;

        return null;
    }

    /// <summary>Hands the thread back and waits to be given it again, behind layout and input.</summary>
    private Task YieldToLayoutAsync()
    {
        var turn = new TaskCompletionSource();
        if (_dispatcher?.TryEnqueue(DispatcherQueuePriority.Low, () => turn.TrySetResult()) != true)
            turn.TrySetResult();   // no dispatcher to wait on; the caller's own checks still apply
        return turn.Task;
    }
}
