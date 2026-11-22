using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Core.Models;
using RPM.Desktop.Strings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// Where the covers land. The layout runs in this order: the target size, how many cards fit in a row,
/// the size each card actually gets, and then how many rows that makes. This file also keeps the
/// repeater's place in the wall while any of these change.
/// </summary>
public sealed partial class AlbumWallView
{
    // ── layout: target size → columns → card size → rows ──

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
        => WallDiag.Time("resize", () => ScrollerResized(e));

    private void ScrollerResized(SizeChangedEventArgs e)
    {
        _availableWidth = e.NewSize.Width;

        // The wall must not exist before its viewport does. The page can arrive collapsed. If rows are
        // handed to the repeater under an unmeasured viewport, the repeater realizes all of them at once.
        // On a 1000-album wall, that made about 900 cards in one pass on first load, which caused a
        // multi-second hang. So rows are held back until the scroller has real extent. The first real
        // SizeChanged feeds them once, with the viewport known. Then the repeater realizes one screenful,
        // as it should.
        if (!_viewportReady && e.NewSize.Width > 1 && e.NewSize.Height > 1)
            _viewportReady = true;

        RecomputeLayout();

        // Not while the pane is moving. The extent changes on every frame of a live resize. A check would
        // read that as a lost place, and it would heal by re-seating every row. See BeginLiveResize.
        if (!_liveResize)
            CheckAnchorLater();
    }

    private bool _viewportReady;

    /// <summary>
    /// Freezes the wall's reflow while the host glides the pane. Without this, every frame of the 240 ms
    /// width animation would re-derive the stride and the columns. It would re-chunk the rows whenever
    /// the count crossed a threshold, and each re-chunk would rebind cards, covers and cloud badges
    /// included. The width keeps arriving through <see cref="Scroller_SizeChanged"/>.
    /// <see cref="ReleaseReflow"/> applies it in one pass at the settled width.
    /// </summary>
    public void HoldReflow() => _reflowHeld = true;

    public void ReleaseReflow()
    {
        if (!_reflowHeld)
            return;
        _reflowHeld = false;
        RecomputeLayout();
        CheckAnchorLater();   // the wall reflowed at a width it was not laid out at during the hold
    }

    private bool _reflowHeld;

    /// <summary>
    /// The pane is resizing the wall LIVE, one frame at a time, and the wall is meant to re-flow as it
    /// goes. The reflow is NOT held. What is held is everything that rides along with it and does not
    /// have to happen until the move is over.
    /// </summary>
    /// <remarks>
    /// Three passengers ride along with the reflow. Each one costs more than the reflow it is attached to:
    ///
    /// <list type="number">
    /// <item><b>The cover density.</b> The art edge sweeps across a decode bucket during the move. The
    /// ladder is 128, 192, 256, 384 and 512. A pane opening moves the art by about fifty points.
    /// <see cref="RefreshCoverDensity"/> answers this by waking the loader for a screenful of records at
    /// once. The loader takes about 5 ms of every 16 ms, and allocates a quarter of a megabyte per cover.
    /// So the whole move runs with a third of each frame, and a growing gen2 heap, sharpening pictures
    /// whose size is still changing.</item>
    /// <item><b>The anchor check.</b> The wall's extent changes on every frame of a live resize.
    /// <see cref="CheckAnchor"/> reads that as a lost place. Its cure is <see cref="Heal"/>: re-seat
    /// ItemsSource, re-realize every row on screen, and bring one into view. In the middle of a move,
    /// that is the largest single thing this control can do.</item>
    /// <item><b>The repeater's cache.</b> Half a viewport on each side means twice the visible rows are
    /// realized. Each realized card is measured again on every frame, because its width changed. The
    /// cache is what makes the NEXT row ready to scroll to. Nobody scrolls during a 240 ms move, so the
    /// cache is given back for the length of the move. Half the rows realized means half the layout work
    /// per frame.</item>
    /// </list>
    ///
    /// <para>The cache is borrowed at the cost of two lumps. The rows outside the viewport are cleared at
    /// the start, and realized again at the end. Both happen outside the move. The first lands before the
    /// pane has moved, and the second on a turn of its own after it has settled. If one of the three must
    /// be dropped, drop this one. It is the only one that trades work for work.
    /// </para>
    /// </remarks>
    public void BeginLiveResize()
    {
        if (_liveResize)
            return;
        _liveResize = true;

        // The cache is not dropped when the wall is scaled. Nothing realized is measured again per frame
        // in that mode, so the cache costs nothing there. Dropping it would build the rows that come into
        // view as the wall narrows (scaled rows are shorter) in the middle of the move. That causes a
        // long gap between frames. So the cache is kept.
        _cacheRows = RowsRepeater.VerticalCacheLength;
        if (Freeze != WallFreeze.Scale)
            RowsRepeater.VerticalCacheLength = 0;

        // The size the cards keep for the length of the move. It is read here, not recomputed. The size
        // must match what is on screen right now. A move redirected mid-flight takes over from wherever
        // the last one left off. Starting from a fresh calculation would cause a jump on the first frame.
        _liveStride = _metrics.Stride;

        // The scale is drawn about the top of the viewport, so the row the reader is on stays put. See
        // AlbumWallLayout.Anchor.
        RowsLayout.Anchor = Scroller.VerticalOffset;
        _liveDeals.Counting = true;

        // The fourth passenger: the cover loader. See CoverStream.Pause.
        _stream?.Pause();
    }

    /// <summary>The move has landed. The passengers are spent once, here, at the width the wall is
    /// keeping.</summary>
    public void EndLiveResize()
    {
        if (!_liveResize)
            return;
        _liveResize = false;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        // The rows on screen that were not dealt during the move are dealt first. Everything below reads them.
        // The rest are dealt a few at a time once the landing is over. See DealStaleRowsLater.
        DealStaleRowsOnScreen();

        // A move that narrowed the wall kept its columns, and the settled pass below deals the cards into
        // fewer. They slide there from where they are drawn now, which is read before the scale goes. See
        // ExpectLiveWidth.
        var heldColumns = _liveColumns;
        _liveColumns = 0;
        if (heldColumns > 0 && _liveStride > 0)
            PrepareGlide(_liveStride * RowsLayout.Scale);

        // The reader's place is written down at every landing, not only one that deals the cards again.
        // Leaving the move's card size for the wall's own changes the row pitch, so the same offset would show
        // the reader's row higher or lower than it was. See HoldReader.
        if (_liveStride > 0)
            _readerHold = CaptureReader(_liveStride);

        RowsRepeater.VerticalCacheLength = _cacheRows;

        // The scale is removed BEFORE the settled pass. That pass then writes the real card size, and the
        // rows are laid out at it. The two answers have the same geometry, so nothing moves on screen.
        // What changes is that the text is drawn at its own size again, not through a scale.
        _liveStride = 0;
        RowsLayout.Scale = 1;
        RowsLayout.RowWidth = 0;

        // The settled width is applied once. A move that was not frozen has already laid the wall out at
        // this width on its last tick, so this call costs nothing. A frozen move (see WallFreeze) has
        // not, and this pass does the whole re-flow. It runs in both cases, not only under the switch.
        // The landing is not guaranteed a SizeChanged. The columns can settle at the width the last tick
        // already wrote.
        _glideLanding = _glideFrom is not null;
        RecomputeLayout();
        _glideLanding = false;
        if (!_glideWaiting)
            _glideFrom = null;   // the columns did not change after all, so nothing slides

        // The columns did not change, so the pass above did not hold the reader. The pitch did change.
        if (_readerHold is { } still)
        {
            _readerHold = null;
            HoldReader(still);
        }
        SettleNow();

        // The density is settled against the size the wall actually ended at. That is the only size worth
        // decoding for. RefreshCoverDensity does nothing unless the edge really crossed a bucket. So a
        // move that stayed inside one bucket costs nothing here.
        RefreshResidentEdge();
        RefreshCoverDensity();

        // This is last, because it reads the realized rows, and the cache above has just changed which
        // rows those are. CheckAnchorLater queues behind the layout pass requested above, because it runs
        // at low priority.
        CheckAnchorLater();

        _stream?.Resume();
        DealStaleRowsLater();

        _liveDeals.Say(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private bool _liveResize;

    /// <summary>
    /// The pane conductor says how much wider (a positive <paramref name="change"/>) or narrower the wall will
    /// be when this live move lands. The wall keeps one column count for the whole move: the larger of the one
    /// it has and the one it will have.
    /// </summary>
    /// <remarks>
    /// <b>Why.</b> A move that lets the column count follow the width changes it two or three times, and each
    /// change deals the records into rows again in the middle of the move. Holding one count, nothing is dealt
    /// during the move: the rows only scale (see <see cref="WallFreeze.Scale"/>), and the cards are dealt once,
    /// sliding to their new places (see AlbumWallView.Glide.cs).
    ///
    /// <para><b>Which end.</b> The larger count is the one held, so the scale never goes above one, and covers
    /// and titles are never drawn larger than they were laid out. A wall that will be wider deals into its new,
    /// larger count straight away, and then grows into it. A wall that will be narrower keeps its count,
    /// shrinks with the move, and deals into the smaller count at the landing.</para>
    /// </remarks>
    public void ExpectLiveWidth(double change)
    {
        _liveColumns = 0;
        if (!_liveResize || Freeze != WallFreeze.Scale || !_viewportReady || _availableWidth <= 0
            || Visibility != Visibility.Visible)
            return;

        var finalColumns = ColumnsAt(_availableWidth + change);
        if (finalColumns <= 0)
            return;
        _liveColumns = Math.Max(_columns, finalColumns);
        if (_liveColumns != _columns)
        {
            // The wall only grows from here, so no row comes into view that is not in view now, and the rows
            // the repeater keeps ready off screen would be built for nothing. See SettleNow for why that
            // matters at this moment. EndLiveResize gives the cache back.
            RowsRepeater.VerticalCacheLength = 0;

            RecomputeLayout();   // deal into the wider count now, at the start of the move
            SettleNow();
        }
    }

    /// <summary>The column count a move holds, or zero when the count follows the width. See
    /// <see cref="ExpectLiveWidth"/>.</summary>
    private int _liveColumns;

    /// <summary>
    /// After a re-deal that moved the scroll offset: applies the offset, lays the wall out and realizes the
    /// rows now in view, all in this turn, and starts the cards' slides on them.
    /// </summary>
    /// <remarks>
    /// Left to the framework, this took three layout passes in three frames when the reader was far down the
    /// wall. The first was laid out before the scroller had applied the new offset, so for a frame the reader
    /// saw the new deal at the old place. The rows at the new place were built over the next two. And a
    /// slide only starts once every row in view has its cards, so it started late, or not at all, and the
    /// cards jumped. Done here, the cost is one pause before the first frame, and every frame after it is
    /// right. <see cref="Heal"/> applies an offset the same way.
    ///
    /// <para>Only called where no layout pass is running: at the start of a move (from the pane conductor's
    /// press) and at its landing.</para>
    /// </remarks>
    private void SettleNow()
    {
        if (_heldOffset is null && !_glideWaiting)
            return;
        // The rows at the new place are realized from the offset the view asked for. See AlbumWallLayout.InView.
        var top = _heldOffset ?? Scroller.VerticalOffset;
        RowsLayout.InView = (top, top + Scroller.ViewportHeight);
        Scroller.UpdateLayout();
        if (_glideWaiting && StartGlides())
            StopWaitingForGlides();
        RowsLayout.InView = null;
    }

    // ── keeping the reader's row in place when the columns change ──

    /// <summary>The reader's place before a column change: the first card of the top row in view, how far
    /// below the top of the viewport that row is laid out (before the scale, which is drawn about the top of
    /// the viewport), and the card stride the wall was laid out at.</summary>
    private readonly record struct ReaderPlace(AlbumCard Card, double ViewY, double LayoutStride);

    /// <summary>A place the landing wrote down before it took the scale away. See
    /// <see cref="EndLiveResize"/>.</summary>
    private ReaderPlace? _readerHold;

    /// <summary>The scroll offset <see cref="HoldReader"/> asked for, until the scroller reports it. Null
    /// otherwise.</summary>
    private double? _heldOffset;

    /// <summary>
    /// The reader's place as it was first written down, and the offset the wall was left at after it was
    /// last held. While the offset is still that one, the reader has not scrolled, and the same place is
    /// held again.
    /// </summary>
    /// <remarks>
    /// Without this, each column change would take a new anchor: the first card of whatever row is on top.
    /// After a change to fewer columns that is usually a card from the row above, so every open and close of
    /// the pane would leave the reader a row higher than before. Kept, an open and a close bring the wall back
    /// to exactly what the reader left.
    /// </remarks>
    private ReaderPlace? _stickyReader;

    private double _stickyOffset = double.NaN;

    /// <summary>Where the reader is now. The top row is the one under the top of the viewport, or the one
    /// below it when less than half of it is still in view. Null on an empty wall.</summary>
    private ReaderPlace? CaptureReader(double layoutStride)
    {
        if (_rows.Count == 0 || RowsLayout.Pitch <= 0)
            return null;
        var top = ViewNow().Top;
        if (_stickyReader is { } sticky && Math.Abs(top - _stickyOffset) < 1 && _cards.Contains(sticky.Card))
            return sticky with { LayoutStride = layoutStride };
        var row = RowsLayout.RowDrawnAt(top, _rows.Count);
        if (row < 0)
            return null;
        if (row + 1 < _rows.Count && RowsLayout.TopOf(row) + RowsLayout.Pitch * RowsLayout.Scale / 2 < top)
            row++;
        if (_rows[row].Cards is not { Count: > 0 } cards)
            return null;
        // Before the scale. A move draws the wall about the top of the viewport, so a row that is partly above
        // it is drawn closer to it than it is laid out. Kept as drawn, an open and a close would bring the reader
        // back a little higher than they left.
        _stickyReader = new ReaderPlace(cards[0], (RowsLayout.TopOf(row) - top) / RowsLayout.Scale, layoutStride);
        return _stickyReader;
    }

    /// <summary>
    /// Scrolls so that the reader's card is drawn as far below the top of the viewport after a column change
    /// as it was before.
    /// </summary>
    /// <remarks>
    /// A column change moves every record to another row: with fewer columns, a record thirty places in is
    /// several rows further down. Left to itself, the wall stays at the same offset, so a reader five rows
    /// down finds other records under them, and the page seems to jump. The top row in view is the anchor:
    /// its first card keeps its place, and the rest of the wall is dealt around it.
    ///
    /// <para>The new row's top is worked out, not measured, so the offset is asked for in the same turn as the
    /// re-deal, as <see cref="ReshapeHoldingReader"/> does for a drawer. During a live move the layout is
    /// laid out at the move's fixed stride, so the row pitch does not change, and the scale is drawn about the
    /// new offset. At a landing the cards take their own stride, and the pitch changes by as much as the
    /// stride does, because a card is a square of art over lines of text of a fixed height.</para>
    ///
    /// <para>Returns false, and does nothing, when the reader's card is no longer on the wall.</para>
    /// </remarks>
    private bool HoldReader(ReaderPlace place)
    {
        var index = _cards.IndexOf(place.Card);
        var pitch = RowsLayout.Pitch;
        if (index < 0 || pitch <= 0 || _columns <= 0)
            return false;
        var row = index / _columns;

        // During a move the row is laid out at the move's pitch and drawn about the new offset, so it is drawn
        // at its laid distance times the scale. At a landing the cards take their own stride.
        var laidPitch = _liveResize ? pitch : pitch + (_metrics.Stride - place.LayoutStride);

        // An open drawer is part of the sum: it pushes every row below it down by its height. It is dealt into
        // the row its record lands in, which is worked out here the same way, because the drawer is moved
        // there after this (see BandConductor.Reseat).
        var bandRow = AttachedBand() is { } open && _cards.IndexOf(open.Card) is var opened and >= 0
            ? opened / _columns
            : -1;
        var spacing = RowsLayout.Spacing;
        var to = AlbumWallRows.RowTop(row, laidPitch - spacing, spacing, bandRow, bandRow >= 0 ? RowsLayout.BandHeight : 0)
                 - place.ViewY;
        to = Math.Max(0, to);
        _stickyOffset = to;

        if (_liveResize)
            RowsLayout.Anchor = to;
        if (Math.Abs(to - Scroller.VerticalOffset) < 0.5)
            return true;
        _heldOffset = to;
        Scroller.ChangeView(null, to, null, disableAnimation: true);
        return true;
    }

    /// <summary>The drawer hanging under a row now, whether it is open, opening or folding. It is the one the
    /// wall's length counts (see <see cref="AlbumWallLayout.BandRow"/>), and the one a re-deal moves.</summary>
    private AlbumBand? AttachedBand()
        => RowsLayout.BandRow is var row and >= 0 && row < _rows.Count ? _rows[row].Band : null;

    /// <summary>True while a re-deal runs whose offset <see cref="HoldReader"/> has already chosen, with the
    /// drawer in its new row. Moving the drawer then does not scroll again (see
    /// <see cref="ReshapeHoldingReader"/>).</summary>
    private bool _readerHeld;

    /// <summary>True for the settled pass at a landing that will slide the cards. See
    /// <see cref="EndLiveResize"/>.</summary>
    private bool _glideLanding;

    /// <summary>How many columns the wall has at a scroller width of <paramref name="width"/>, by the same
    /// arithmetic as <see cref="RecomputeLayout"/>.</summary>
    private int ColumnsAt(double width)
    {
        var available = width - ScrollerPadding;
        return available <= 0
            ? 0
            : AlbumGridLayout.ColumnsForTarget(available, _targetArt + CardChrome, maxStride: MaxArt + CardChrome);
    }

    /// <summary>The card stride the wall is laid out at for the length of a live resize, or zero when no
    /// resize is running. The stride it is shown at is this value times
    /// <see cref="AlbumWallLayout.Scale"/>.
    /// </summary>
    private double _liveStride;

    /// <summary>The repeater's cache length from before a live resize borrowed it. It is given back as it
    /// was found, rather than as a second copy of a number written here.</summary>
    private double _cacheRows;

    // ── keeping the repeater's place in the wall ──
    //
    // See AlbumGridLayout.AnchorLost for what goes wrong, and why the repeater cannot recover from it by
    // itself. The wall checks for this after every pass that could cause it: a reflow, a resize, or
    // coming back from behind an album page. It also checks after any scroll that settles. That is where
    // the user notices it. They scroll up, the wall says it is at the top, and half a row is showing.
    //
    // This check is required. Without it, scrolling down a few screens and coming back leaves the top
    // rows of the wall blank, and nothing brings them back. Fix where the heal goes TO. Do not disable
    // the check.

    /// <summary>Whether the repeater has ever realized a row. Before that, an empty wall is one that has
    /// not been laid out yet. It is not a wall that lost its place.</summary>
    private bool _everRealized;

    /// <summary>
    /// The topmost row the wall was last seen SHOWING, so a heal can put the user back where they were
    /// rather than at the top of the collection.
    ///
    /// <para>This is the first VISIBLE row, not the first realized one. The repeater keeps a cache on
    /// each side of the viewport: two screenfuls, the default, since nothing here sets
    /// VerticalCacheLength. So the lowest index it holds an element for can be up to two screens ABOVE
    /// what the reader sees. A heal to that row would put the reader two screens too high. Anywhere in
    /// the first two screens of the wall, it clamped to row 0. The record the reader clicked was near the
    /// top, so the heal looked like a jump to the top.</para>
    /// </summary>
    private int _topRow;

    private bool _healing;
    private bool _checkQueued;

    /// <summary>The host calls this when the wall is back on screen. That happens when an album page
    /// closes over it, or when the view flips. A control that was collapsed takes its next layout pass
    /// with no viewport to measure. That is exactly the pass that loses the anchor.</summary>
    public void EnsureAnchored() => CheckAnchorLater();

    private void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (WallDiag.On)
        {
            WallDiag.View(Scroller.VerticalOffset, Scroller.ExtentHeight);
            WallDiag.Move(Scroller.VerticalOffset, Scroller.ExtentHeight, Scroller.ViewportHeight);
        }
        if (!e.IsIntermediate)
            _heldOffset = null;   // the offset HoldReader asked for has been applied
        if (!e.IsIntermediate)
            WallDiag.Time("anchor", CheckAnchor);
    }

    /// <summary>Queues the check for after the pending layout has run. Rows are not placed until that
    /// pass. A check run inline would read the state we are leaving.</summary>
    private void CheckAnchorLater()
    {
        if (_checkQueued)
            return;
        _checkQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _checkQueued = false;
            CheckAnchor();
        });
    }

    private void CheckAnchor()
    {
        if (_healing || _reflowHeld || _liveResize || !_viewportReady || _rows.Count == 0)
            return;
        if (Scroller.ActualWidth <= 1 || Scroller.ActualHeight <= 1)
            return;   // no viewport, so the anchor cannot be right or wrong

        var first = FirstRealizedRow();
        var top = FirstRowTop();
        if (!AlbumGridLayout.AnchorLost(_rows.Count, first, Scroller.VerticalOffset, _everRealized, top))
        {
            _lostSeen = 0;
            // Remembered from the VISIBLE row (see _topRow). A blank wall has no visible row to read.
            // That is exactly when the remembered row is needed, so it is written only from a healthy
            // pass.
            if (FirstVisibleRow() is var visible && visible >= 0)
                _topRow = visible;
            return;
        }

        // The loss is confirmed twice before it is acted on. A wall in the middle of a relayout holds no
        // elements for a moment. That reads exactly like a place that is lost for good. The cure re-seats
        // the rows, which empties the repeater, which reads as lost again. Without the second check, the
        // wall would heal in a loop, snapping to the top and climbing back each time. A place that is
        // really lost is still lost on the next pass. A brief blink is not.
        if (++_lostSeen < 2)
        {
            if (WallDiag.On)
                WallDiag.Open($"anchor reads lost (1st) — first={first} top={top:0.#}"
                    + $" offset={Scroller.VerticalOffset:0.#} extent={Scroller.ExtentHeight:0.#} topRow={_topRow}");
            CheckAnchorLater();
            return;
        }
        _lostSeen = 0;

        // Logged, because this is the fault users report as "it will not scroll to the top". The numbers
        // that caused it are worth keeping in the log.
        WallDiag.Healed($"anchor lost — first={first} top={top:0.#} offset={Scroller.VerticalOffset:0.#}"
            + $" extent={Scroller.ExtentHeight:0.#} → to row {(first < 0 ? _topRow : 0)}");

        // A blank wall goes back to the reader's row. A false top means the reader is already asking for
        // row 0.
        Heal(first < 0 ? _topRow : 0);
    }

    /// <summary>How many checks in a row have said the place is lost. See <see
    /// cref="CheckAnchor"/>.</summary>
    private int _lostSeen;

    /// <summary>
    /// Where row 0's top edge sits, relative to the top of the CONTENT. The value is NaN when the
    /// repeater holds no element for row 0.
    ///
    /// <para>A negative value means the row is laid out above the visible area. With the scroller already
    /// at zero, that is a wall the user cannot scroll up to. The repeater realizes a cache of rows on
    /// each side of the viewport. So row 0 being realized does not mean row 0 can be reached. This check
    /// closes that gap.</para>
    ///
    /// <para>The scroller's own top padding is taken off again, because it is not a displacement. The
    /// wall is supposed to start that far down. Measured against the viewport instead, each point of top
    /// padding would look like the anchor being that far out. The anchor check answers a lost anchor by
    /// re-seating ItemsSource, which re-realizes every row on screen and flashes the covers. A top margin
    /// of only four points was enough to trigger this at rest, at the top, and on every
    /// resize.</para></summary>
    private double FirstRowTop()
    {
        if (_rows.Count == 0 || RowsRepeater.TryGetElement(0) is not FrameworkElement row)
            return double.NaN;
        try
        {
            return row.TransformToVisual(Scroller).TransformPoint(default).Y - Scroller.Padding.Top;
        }
        catch (Exception)
        {
            // An element in the middle of being recycled has no place in the tree to transform from. It
            // is not evidence either way. The next settled scroll checks again.
            return double.NaN;
        }
    }

    /// <summary>The lowest row index the repeater holds an element for, or -1 if it holds none. The
    /// answer comes from walking the realized children, which is about one screenful. Every row is not
    /// asked.</summary>
    private int FirstRealizedRow()
    {
        var first = -1;
        var count = VisualTreeHelper.GetChildrenCount(RowsRepeater);
        for (var i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(RowsRepeater, i) is not UIElement child)
                continue;
            var index = RowsRepeater.GetElementIndex(child);
            if (index >= 0 && (first < 0 || index < first))
                first = index;
        }
        return first;
    }

    /// <summary>
    /// The lowest row index the reader can actually SEE. This is the topmost row whose bottom edge is
    /// still below the top of the viewport. The value is -1 when nothing is placed.
    ///
    /// <para>This is not <see cref="FirstRealizedRow"/>. That one answers "what does the repeater hold".
    /// That includes its cache of rows above the viewport, and it is the right question for whether the
    /// anchor is lost. This one answers "where is the reader". It is the right question for two things:
    /// where to put the rows back after a heal, and whose position to hold when the wall changes shape
    /// (<c>AlbumWallView.ReshapeHoldingReader</c>). The two answers are two screenfuls apart.</para>
    /// </summary>
    private int FirstVisibleRow()
    {
        var best = -1;
        var bestTop = double.PositiveInfinity;
        var count = VisualTreeHelper.GetChildrenCount(RowsRepeater);
        for (var i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(RowsRepeater, i) is not FrameworkElement child)
                continue;
            var index = RowsRepeater.GetElementIndex(child);
            if (index < 0)
                continue;
            try
            {
                // Measured against the VIEWPORT this time, padding included. The question is what the
                // reader has in front of them. The scroller's top padding is out of sight, the same as
                // anything else.
                var top = child.TransformToVisual(Scroller).TransformPoint(default).Y;
                if (top + child.ActualHeight > 0 && top < bestTop)
                {
                    bestTop = top;
                    best = index;
                }
            }
            catch (Exception)
            {
                // Mid-recycle: no place in the tree to transform from. Not evidence either way.
            }
        }
        return best;
    }

    /// <summary>Lays the wall out again, and puts <paramref name="row"/> back at the top of the viewport.
    /// Re-seating ItemsSource is what drops the repeater's stale estimate. There is no other way to make
    /// it forget. The rows it clears get their covers back from the thumbnail cache as they are realized
    /// again. That takes a moment, not a rescan.</summary>
    private void Heal(int row)
    {
        _healing = true;
        row = Math.Clamp(row, 0, _rows.Count - 1);

        RowsRepeater.ItemsSource = null;
        RowsRepeater.ItemsSource = _rows;
        Scroller.ChangeView(null, 0, null, disableAnimation: true);
        Scroller.UpdateLayout();

        if (row > 0 && RowsRepeater.GetOrCreateElement(row) is FrameworkElement element)
        {
            element.UpdateLayout();
            element.StartBringIntoView(new BringIntoViewOptions
            {
                VerticalAlignmentRatio = 0,
                AnimationDesired = false,
            });
        }

        // The heal is deaf until it has actually landed. One dispatcher turn is not enough for that. The
        // rows are realized on the pass after the source is re-seated, and the bring-into-view lands on
        // the pass after that. A check run in between would read the heal's own half-finished state as a
        // new lost anchor, and it would heal again. So the wall waits on a short clock, not a turn. The
        // clock clears itself, whatever the layout does.
        _healTimer ??= CreateHealTimer();
        _healTimer.Stop();
        _healTimer.Start();
    }

    private DispatcherQueueTimer? _healTimer;

    private DispatcherQueueTimer CreateHealTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(250);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _healing = false;
            _lostSeen = 0;   // anything seen during the heal came from the heal, not from the wall
        };
        return timer;
    }

    private void SizeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        // Applied synchronously, the same way a window resize is applied. The reflow is a cheap in-place
        // row update, with no collection reset, so driving it straight from the input is flicker-free.
        // Applying it from a timer tick would run the relayout outside the input and layout cycle, and
        // that flickers.
        _targetArt = e.NewValue;
        RecomputeLayout();
        if (!_suppressPersist)
            PersistSizeDebounced();
    }

    /// <summary>
    /// Turns the panel width and the user's target art size into the wall's layout. First comes the
    /// nearest whole column count for that target. Then the card stride that fills the row exactly. Then
    /// the art square inside that stride. The size reaches every card through <see cref="_metrics"/>. The
    /// rows are re-chunked only when the column count changes. A size change inside the same count only
    /// widens the cards in place, and moves the open drawer's pointer with them.
    /// </summary>
    private void RecomputeLayout()
    {
        if (_availableWidth <= 0)
            return;   // not measured yet: keep the default metrics rather than make one card full width

        if (_reflowHeld)
            return;   // a pane glide owns the width. The release runs this once, at the settled width.

        // The reader's place, if the landing wrote it down before it took the scale away. See HoldReader. It is
        // used, and cleared, only by a pass that changes the column count.
        var reader = _readerHold;

        // The display the window is on decides how large the resident tier must be. That saves the wall a
        // read at the bottom of the slider. This is the first moment there is an answer. The scale comes
        // from the XamlRoot, and the wall is fed before it has one.
        //
        // Both this and the density below are held for the length of a live resize, and spent once at its
        // end. See BeginLiveResize for what they cost per frame.
        if (!_liveResize)
            RefreshResidentEdge();

        var available = _availableWidth - ScrollerPadding;
        var targetStride = _targetArt + CardChrome;
        // The ceiling applies to the COUNT, not only to the stretch. Without this, a slider at the top of
        // its range makes the target and the ceiling the same number. The rounding then decides the
        // count. A count rounded down leaves up to half a card of empty panel on the right. See
        // AlbumGridLayout.ColumnsForTarget.
        var columns = AlbumGridLayout.ColumnsForTarget(available, targetStride, maxStride: MaxArt + CardChrome);
        if (_liveResize && _liveColumns > 0)
            columns = _liveColumns;   // one count for the whole move; see ExpectLiveWidth
        var stride = AlbumGridLayout.StrideFor(available, columns, MaxArt + CardChrome);

        // What the wall may do on this pass. Anything it may not do is spent once at the landing instead.
        // See WallFreeze. Scale is what ships on both heads. Size and All are for measurements only.
        var frozen = _liveResize ? Freeze : WallFreeze.None;

        // A column change during a live move: where the cards and the reader are drawn now, before this pass
        // changes the scale. The cards slide from there (see AlbumWallView.Glide.cs), and the reader is kept
        // in place (see HoldReader).
        if (columns != _columns && _rows.Count > 0 && _liveResize && frozen == WallFreeze.Scale && _liveStride > 0)
        {
            PrepareGlide(_liveStride * RowsLayout.Scale);
            reader ??= CaptureReader(_liveStride);
        }

        if (frozen is WallFreeze.Size or WallFreeze.All)
        {
            // A freeze run: the card size is held, and nothing takes up the slack. The wall looks wrong
            // for the length of the move. See WallFreeze. This is a measurement.
        }
        else if (_liveResize && frozen == WallFreeze.Scale && _liveStride > 0)
        {
            // THE CARDS KEEP THE SIZE THE MOVE BEGAN AT. The difference is carried as a scale on each
            // row. Writing a new size on every frame would make every realized card dirty, and each would
            // re-run its whole subtree. See AlbumWallLayout.Scale, which holds the numbers.
            //
            // The row is laid out at the column count times the FIXED stride. That is the constant the
            // measure cache needs. It is drawn at the width the row actually has. The two differ by the
            // scale. So the right-hand edge lands exactly where it does at rest, with no margin to take
            // up.
            RowsLayout.RowWidth = columns * _liveStride;
            RowsLayout.Scale = stride / _liveStride;
        }
        else
        {
            _metrics.Set(stride, stride - CardChrome);
        }

        // The cards are a different size now, so their pictures may be decoded at the wrong size. This is
        // cheap, unless the size actually crossed a bucket. See RefreshCoverDensity.
        if (!_liveResize)
            RefreshCoverDensity();

        if (frozen == WallFreeze.All)
            return;   // _columns is left as it was, so the landing's own pass re-chunks once

        if (columns != _columns || _rows.Count == 0)
        {
            _columns = columns;
            _readerHold = null;
            _readerHeld = reader is { } place && HoldReader(place);
            try
            {
                Regroup();
            }
            finally
            {
                _readerHeld = false;
            }
        }
        else
        {
            _band.RefreshPointers();
        }

        // One row on screen gets its cards on each pass of a live move. See _staleChunks.
        DealOneOnScreen();
    }

    /// <summary>
    /// How much of the wall's re-flow is switched off while the pane resizes it live. This is a
    /// measurement, not a setting. Two of the three answers leave the wall visibly wrong during the move.
    /// </summary>
    /// <remarks>
    /// <b>What it is for.</b> A live resize re-flows the wall on every frame. Each frame has two different
    /// costs:
    ///
    /// <list type="bullet">
    /// <item><b>The card size</b>, written on every frame. Three bound properties change on every
    /// realized card (<c>Button.Width</c>, the art square, and the title's cap). That makes each card
    /// dirty, and it re-runs the whole subtree. That is about 1,700 elements, and about 140 of them are
    /// text that re-shapes and re-trims because its width changed.</item>
    /// <item><b>The column count</b> changes two to four times during a move. Each change re-chunks the
    /// collection, and puts a different record in nearly every slot on screen. So about 70 cards rebind,
    /// each with ten bindings, two text lines and two image sources.</item>
    /// </list>
    ///
    /// <para>Three runs tell them apart. <c>None</c> re-flows every frame. <c>Size</c> freezes the card
    /// size and lets the column count keep changing, so what is left is the re-chunks alone. <c>All</c>
    /// freezes both. The wall then does nothing at all while the pane moves. What is left is everything
    /// that is NOT the wall: the frost's live glass, the collector, the loader, and the panes' own
    /// layout.</para>
    ///
    /// <para>Read the result from the <c>panes</c> channel. It gives one line per live move, with the
    /// frames delivered and the worst gap between them. <c>RPM_WALL_DIAG=1</c> at the same time splits
    /// the wall's own share up further.</para>
    ///
    /// <para><b>What each one looks like on screen</b>, so a run is not mistaken for a fault. <c>Size</c>
    /// leaves the cards at their old size, in rows that are the new width. So the right-hand edge either
    /// shows a gap or cuts a card off. The wall snaps to size at the landing. <c>All</c> leaves the wall
    /// exactly as it was for the length of the move, and does the whole re-flow at the landing.</para>
    /// </remarks>
    internal enum WallFreeze
    {
        /// <summary>The wall re-flows as the pane moves. The cards cannot slide to a new column count in this
        /// mode, because their size changes on every frame.</summary>
        None,

        /// <summary>The card size is held. The column count still changes.</summary>
        Size,

        /// <summary>Neither the card size nor the column count changes until the landing.</summary>
        All,

        /// <summary>
        /// The card size is held, and the difference is carried as a scale on each row. The wall fills
        /// the width, and no margin opens. This is the fixed-stride design, in full.
        /// </summary>
        /// <remarks>
        /// <b>What ships on both heads.</b> On WinUI the scale is on each row's composition visual (see
        /// <c>AlbumWallLayout.Draw</c>). The repeater's cache is kept for the move. On the 2,501-album
        /// bench library, eight folds of the left pane came back with 6 to 8 writes out of 8, and worst
        /// gaps of 43 to 74 ms (median about 53 ms). With <see cref="None"/>, the same test gave 5 to 6
        /// writes and worst gaps of 60 to 102 ms. The columns still step during the move, and the scale
        /// bridges each step. So the wall always fills its width, and nothing is hidden. Those readings are
        /// from WinUI.
        ///
        /// <para>On the Skia head a XAML render transform and the visual's matrix are the same write (Uno's
        /// render transform sets the visual's <c>TransformMatrix</c>), so the row's <c>CompositeTransform</c>
        /// costs no more than the matrix does on WinUI. Skia draws the whole window on every frame in any case.
        /// Scale is also what lets the cards slide to a new column count (see <c>AlbumWallView.Glide.cs</c>),
        /// which <see cref="None"/> cannot do.</para>
        ///
        /// <para><b>Measured on macOS</b> (Uno 6.8.0-dev.60, a 120 Hz panel, a 3,204-album library, ten folds
        /// of the left pane over the wall, no debugger). Scale: 29 to 32 writes per 240 ms move, worst gaps
        /// 16 to 21 ms, one re-deal per move with 12 cards sliding and 23 fading in. <see cref="None"/>: 24
        /// to 30 writes, worst gaps 16 to 29 ms, and three re-deals per move, each of which moves nearly every
        /// card on screen in one frame.</para>
        ///
        /// <para>This machinery (<see cref="AlbumWallLayout.Scale"/>, <c>RowWidth</c> and the constant
        /// arrange rect) is the only thing in the app that resizes a virtualized list without laying it
        /// out. It is correct. Nothing reaches it except this switch.</para>
        ///
        /// <para><b>Where the time goes.</b> Most of the remaining time is rendering, not layout. Scaling
        /// a row by a factor that changes every frame re-rasterizes every glyph run in it. That is about
        /// 70 cards with two text lines each. The lever for this is the number of elements on each
        /// card.</para>
        /// </remarks>
        Scale,
    }

    /// <summary>Which of these this run is, read from <c>RPM_PANE_FREEZE</c> (<c>none</c>, <c>size</c>,
    /// <c>all</c> or <c>scale</c>; <c>1</c> means <c>size</c>). If the value is absent or not recognised, it
    /// is <see cref="WallFreeze.Scale"/>.</summary>
    internal static readonly WallFreeze Freeze =
        RPM.Core.Diagnostics.DevSwitch.Read("RPM_PANE_FREEZE")?.Trim().ToLowerInvariant() switch
        {
            "none" => WallFreeze.None,
            "size" or "1" or "true" => WallFreeze.Size,
            "all" => WallFreeze.All,
            _ => WallFreeze.Scale,
        };

    private void PersistSizeDebounced()
    {
        _persistTimer ??= CreatePersistTimer();
        _persistTimer.Stop();
        _persistTimer.Start();
    }

    private DispatcherQueueTimer CreatePersistTimer()
    {
        // Dragging fires ValueChanged for every pixel. The wall updates live, but the setting is written
        // only once the user settles. That way settings.json is not rewritten on every tick of the drag.
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(400);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => { timer.Stop(); TileSizeChanged?.Invoke(_targetArt); };
        return timer;
    }

    private void Regroup()
    {
        if (!_viewportReady)
            return;   // no viewport yet: see Scroller_SizeChanged. The first real size regroups.

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var chunks = AlbumGridLayout.Chunk(_cards, _columns);

        // During a live move, no row that already exists is dealt here, except the rows in view when the
        // cards are going to slide to their new places. See _staleChunks and AlbumWallView.Glide.cs.
        var defer = (_liveResize || _glideLanding) && SpreadDeal;
        var gliding = (_liveResize || _glideLanding) && _glideFrom is not null;
        var view = gliding ? ViewNow() : default;
        _staleChunks = defer ? chunks : null;
        _staleRows = defer ? new bool[chunks.Count] : null;
        var deferred = 0;

        // Reflow without a blank frame. The rows stay alive across the reflow, and only the surplus rows at the
        // end are added or trimmed. A row whose cards changed is given its new chunk as a whole (AlbumRow.Deal),
        // and a row that holds the same cards is left alone. This is the same path a window resize takes.
        var moved = false;
        for (var i = 0; i < chunks.Count; i++)
        {
            if (i < _rows.Count)
            {
                if (_staleRows is not null && !(gliding && InView(i, view)))
                {
                    _staleRows[i] = true;
                    deferred++;
                    moved = true;
                    continue;
                }
                moved |= _rows[i].Deal(chunks[i]);
            }
            else
            {
                var row = new AlbumRow();
                row.Deal(chunks[i]);
                _rows.Add(row);
                moved = true;
            }
        }
        for (var i = _rows.Count - 1; i >= chunks.Count; i--)
        {
            _rows.RemoveAt(i);
            moved = true;
        }

        if (_liveResize)
            _liveDeals.Add(_columns, chunks.Count - deferred, deferred,
                System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        if (gliding)
            StartGlidesAfterLayout();
        else
            _glideFrom = null;

        // Nothing actually moved. This happens when a feed returns the same albums in the same order.
        // Re-seating would rebind the open drawer's element for no reason. That is a visible blink on a
        // wall that did not change.
        if (!moved)
            return;

        // Put the open drawer back. On a plain resize the card instance is unchanged, so the open drawer
        // is RE-SEATED into its new row and column. It keeps its tint, cover and tracks, with no reload,
        // no default-background flash and no spinner. Only an album feed hands back a fresh card, and
        // that path reloads.
        _band.Reseat();

        if (!_liveResize)
            CheckAnchorLater();   // rows came or went under the repeater's estimate
    }

    // ── rows dealt late, during a live move ──

    /// <summary>
    /// The chunks of the last re-deal, kept while some rows have not been given theirs. Null when every
    /// row is up to date, which is always the case outside a live move.
    /// </summary>
    /// <remarks>
    /// <b>Why.</b> A column change puts a different record in nearly every slot on screen: about five rows
    /// of nine cards. Each card is bound again, its two lines of text are shaped again and its pictures
    /// are swapped. In one pass that took about 30 ms of the drawing thread, twice in every pane move, and
    /// those two frames were the stutter. So during a live move the rows are dealt late:
    ///
    /// <list type="bullet">
    /// <item>A row in view gets its cards one row a pass, top first (<see cref="DealOneOnScreen"/>).
    /// Until its turn it shows its old cards in the new row width, so for a frame or two a row can have
    /// one card too many (cut off at the edge) or one too few. A row in view that is still waiting when
    /// the move lands is dealt at the landing.</item>
    /// <item>A row that is realized during the move gets its cards just before its element is made
    /// (<see cref="DealRowBeforeRealizing"/>, called by <see cref="AlbumWallLayout.Realizing"/>), so a
    /// new row never shows old cards.</item>
    /// <item>Every other row, including the rows the repeater holds just outside the view, gets its cards
    /// after the move has landed, a few at a time (<see cref="DealStaleRowsLater"/>). Nobody sees them
    /// before that.</item>
    /// </list>
    ///
    /// <para>A row that still holds its old cards would give the wrong answer to a search of the rows for a
    /// card. So anything that searches calls <see cref="DealStaleRows"/> first. The open drawer does not
    /// search: it works its row out from its record's place in the wall (see <c>BandConductor.Reseat</c>).</para>
    /// </remarks>
    private List<IReadOnlyList<AlbumCard>>? _staleChunks;

    /// <summary>Which rows still wait for their chunk in <see cref="_staleChunks"/>.</summary>
    private bool[]? _staleRows;

    /// <summary>The layout calls this before it asks for row <paramref name="index"/>'s element, on every
    /// pass and for every row in its range. A row that already has an element is left to
    /// <see cref="DealOneOnScreen"/>. A row about to get one is given its cards first, so that it never
    /// shows the old ones.</summary>
    private void DealRowBeforeRealizing(int index)
    {
        if (_staleRows is null || index < 0 || index >= _rows.Count || _onScreen.Contains(_rows[index]))
            return;
        DealRow(index);
    }

    /// <summary>Gives row <paramref name="index"/> its cards from the last re-deal, if it is still
    /// waiting for them.</summary>
    private void DealRow(int index)
    {
        if (_staleRows is not { } stale || _staleChunks is not { } chunks
            || index < 0 || index >= stale.Length || index >= _rows.Count || !stale[index])
            return;
        stale[index] = false;
        _rows[index].Deal(chunks[index]);
    }

    /// <summary>Gives every waiting row its cards at once. This runs before anything that searches the rows
    /// for a card.</summary>
    private void DealStaleRows()
    {
        if (_staleRows is not { } stale || _staleChunks is not { } chunks)
            return;
        _staleRows = null;
        _staleChunks = null;
        var count = Math.Min(stale.Length, _rows.Count);
        for (var i = 0; i < count; i++)
        {
            if (!stale[i])
                continue;
            if (_liveDeals.Counting && _onScreen.Contains(_rows[i]))
                _liveDeals.LandedOnScreen++;
            _rows[i].Deal(chunks[i]);
        }
    }

    /// <summary>Gives the rows on screen that are still waiting their cards. The landing does this before
    /// anything else, because what is on screen must be right when the move stops.</summary>
    private void DealStaleRowsOnScreen()
    {
        if (_staleRows is not { } stale)
            return;
        var view = ViewNow();
        var count = Math.Min(stale.Length, _rows.Count);
        for (var i = 0; i < count; i++)
        {
            if (!stale[i] || !InView(i, view) || !_onScreen.Contains(_rows[i]))
                continue;
            if (_liveDeals.Counting)
                _liveDeals.LandedOnScreen++;
            DealRow(i);
        }
    }

    /// <summary>
    /// Gives the rows nobody can see their cards, a few at a time, at low priority, after a move has landed.
    /// </summary>
    /// <remarks>
    /// There are a few hundred of them, and dealing them all at once took 5 to 30 ms of the landing frame.
    /// A turn here takes about <see cref="DealSliceMs"/> and queues the next one. A row that is realized in
    /// the meantime is dealt first, by <see cref="DealRowBeforeRealizing"/>. Anything that searches the
    /// rows for a card calls <see cref="DealStaleRows"/> first. A new re-deal or a new move replaces the
    /// rows that are waiting, and then this pass stops.
    /// </remarks>
    private void DealStaleRowsLater()
    {
        if (_staleRows is not { } stale)
            return;
        _dealFrom = 0;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => DealSome(stale));
    }

    private void DealSome(bool[] stale)
    {
        if (!ReferenceEquals(stale, _staleRows) || _liveResize)
            return;   // a newer re-deal owns the rows, or a move has started and its landing will ask again

        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        // The realized rows first: the cache just outside the view. They are the ones a scroll reaches first.
        foreach (var row in _onScreen)
        {
            var index = _rows.IndexOf(row);
            if (index >= 0 && index < stale.Length && stale[index])
                DealRow(index);
        }

        var count = Math.Min(stale.Length, _rows.Count);
        while (_dealFrom < count)
        {
            DealRow(_dealFrom++);
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds > DealSliceMs)
            {
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => DealSome(stale));
                return;
            }
        }
        _staleRows = null;
        _staleChunks = null;
    }

    /// <summary>Where <see cref="DealSome"/> goes on from.</summary>
    private int _dealFrom;

    /// <summary>How long one turn of <see cref="DealSome"/> may take.</summary>
    private const double DealSliceMs = 4;

    /// <summary>
    /// Whether a live move deals its rows late (see <see cref="_staleChunks"/>). <c>RPM_WALL_SPREAD=0</c>
    /// in a Debug build deals every row in the pass that changed the count, so the two can be measured
    /// against each other.
    /// </summary>
    internal static readonly bool SpreadDeal =
        RPM.Core.Diagnostics.DevSwitch.Read("RPM_WALL_SPREAD")?.Trim() is not ("0" or "false");

    /// <summary>Gives the topmost row in view that is still waiting for its cards. See
    /// <see cref="_staleChunks"/>.</summary>
    /// <remarks>Rows the repeater holds just outside the view are left for after the landing. Nobody sees
    /// them during the move, and each row dealt costs a frame about 7 ms.</remarks>
    private void DealOneOnScreen()
    {
        if (_staleRows is not { } stale || _onScreen.Count == 0)
            return;
        var view = ViewNow();
        var best = -1;
        foreach (var row in _onScreen)
        {
            var index = _rows.IndexOf(row);
            if (index >= 0 && index < stale.Length && stale[index] && (best < 0 || index < best) && InView(index, view))
                best = index;
        }
        if (best < 0)
            return;
        DealRow(best);
        _liveDeals.SpreadRows++;
    }

    /// <summary>The part of the wall the scroller shows, in the scroller's own space.</summary>
    private (double Top, double Bottom) ViewNow()
    {
        // An offset that HoldReader asked for is the one the next frame is drawn at, even before the scroller
        // reports it.
        var top = _heldOffset ?? Scroller.VerticalOffset;
        return (top, top + Scroller.ViewportHeight);
    }

    /// <summary>Whether any of row <paramref name="index"/> lies in <paramref name="view"/>. It is asked
    /// with the layout's own arithmetic, scale included, so it is the same answer the next pass draws.</summary>
    private bool InView(int index, (double Top, double Bottom) view)
        => RowsLayout.TopOf(index) < view.Bottom && RowsLayout.TopOf(index + 1) > view.Top;

    /// <summary>What the re-deals of one live move cost. It is said on the <c>panes</c> channel when
    /// the move lands, after the line for the move itself. "Rows dealt" counts the rows made new by the
    /// re-deal, "later" the rows left for <see cref="DealOneOnScreen"/> and the landing.</summary>
    private readonly LiveDeals _liveDeals = new();

    private sealed class LiveDeals
    {
        private readonly System.Text.StringBuilder _line = new();
        private int _count;

        /// <summary>Rows on screen dealt one a pass during the move.</summary>
        public int SpreadRows;

        /// <summary>Rows on screen that were still waiting when the move landed.</summary>
        public int LandedOnScreen;

        /// <summary>True between the start of a move and its report.</summary>
        public bool Counting;

        /// <summary>Cards that slid to a new place, and cards that faded in. See AlbumWallView.Glide.cs.</summary>
        public int Slid, Faded;

        /// <summary>The open drawer slid to its record's new row or column.</summary>
        public bool Drawer;

        public void Add(int columns, int dealt, int deferred, double ms)
        {
            _line.Append(_count++ == 0 ? "" : ", ")
                .Append($"{columns} columns: {dealt} rows dealt, {deferred} later, {ms:F1}ms");
        }

        public void Say(double landingMs)
        {
            // A wall that is not on screen is still told about every move, and it has nothing to say.
            if (_count == 0 && SpreadRows == 0 && LandedOnScreen == 0 && Slid == 0 && Faded == 0 && !Drawer)
            {
                Counting = false;
                return;
            }
            RPM.Core.Diagnostics.Scope.Said("panes",
                $"wall re-dealt {_count}x during the move ({(_count == 0 ? "none" : _line.ToString())}), "
                + $"{SpreadRows} rows on screen dealt a pass at a time, {LandedOnScreen} still waiting at the "
                + $"landing, {Slid} cards slid{(Drawer ? " and the drawer" : "")}, {Faded} faded in, landing {landingMs:F1}ms");
            _line.Clear();
            _count = 0;
            SpreadRows = 0;
            LandedOnScreen = 0;
            Slid = 0;
            Faded = 0;
            Drawer = false;
            Counting = false;
        }
    }
}
