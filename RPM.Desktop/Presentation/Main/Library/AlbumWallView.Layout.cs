using System.Collections.ObjectModel;
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
    }

    /// <summary>The move has landed. The passengers are spent once, here, at the width the wall is
    /// keeping.</summary>
    public void EndLiveResize()
    {
        if (!_liveResize)
            return;
        _liveResize = false;

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
        RecomputeLayout();

        // The density is settled against the size the wall actually ended at. That is the only size worth
        // decoding for. RefreshCoverDensity does nothing unless the edge really crossed a bucket. So a
        // move that stayed inside one bucket costs nothing here.
        RefreshResidentEdge();
        RefreshCoverDensity();

        // This is last, because it reads the realized rows, and the cache above has just changed which
        // rows those are. CheckAnchorLater queues behind the layout pass requested above, because it runs
        // at low priority.
        CheckAnchorLater();
    }

    private bool _liveResize;

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
        var stride = AlbumGridLayout.StrideFor(available, columns, MaxArt + CardChrome);

        // What the wall may do on this pass. Anything it may not do is spent once at the landing instead.
        // See WallFreeze. Scale is what ships on WinUI. Size and All are for measurements only.
        var frozen = _liveResize ? Freeze : WallFreeze.None;

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
            Regroup();
        }
        else
        {
            _band.RefreshPointers();
        }
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
        /// <summary>The wall re-flows as the pane moves. This is what the Skia head ships with.</summary>
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
        /// <b>What ships on WinUI</b>, with the scale on each row's composition visual (see
        /// <c>AlbumWallLayout.Draw</c>). The repeater's cache is kept for the move. On the 2,501-album
        /// bench library, eight folds of the left pane came back with 6 to 8 writes out of 8, and worst
        /// gaps of 43 to 74 ms (median about 53 ms). With <see cref="None"/>, the same test gave 5 to 6
        /// writes and worst gaps of 60 to 102 ms. The columns still step during the move, and the scale
        /// bridges each step. So the wall always fills its width, and nothing is hidden. The Skia head
        /// uses <see cref="None"/>.
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
    /// <c>all</c> or <c>scale</c>; <c>1</c> means <c>size</c>). If the value is absent or not recognised,
    /// the default depends on the head: <see cref="WallFreeze.Scale"/> on WinUI, and
    /// <see cref="WallFreeze.None"/> on Skia.</summary>
    internal static readonly WallFreeze Freeze =
        RPM.Core.Diagnostics.DevSwitch.Read("RPM_PANE_FREEZE")?.Trim().ToLowerInvariant() switch
        {
            "none" => WallFreeze.None,
            "size" or "1" or "true" => WallFreeze.Size,
            "all" => WallFreeze.All,
            "scale" => WallFreeze.Scale,
#if RPM_WINUI
            _ => WallFreeze.Scale,
#else
            _ => WallFreeze.None,
#endif
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

        var chunks = AlbumGridLayout.Chunk(_cards, _columns);

        // Reflow without a blank frame. Rows and their card collections stay alive across the reflow.
        // Each row's ObservableCollection is diffed to its new chunk, so only the cards that moved are
        // added, removed or replaced. Only the surplus rows at the end are added or trimmed. Nothing is
        // reset, so nothing blanks. This is the same path a window resize takes, which is why a resize
        // never flickers.
        var moved = false;
        for (var i = 0; i < chunks.Count; i++)
        {
            if (i < _rows.Count)
                moved |= SyncRow(_rows[i].Cards, chunks[i]);
            else
            {
                var row = new AlbumRow();
                foreach (var c in chunks[i])
                    row.Cards.Add(c);
                _rows.Add(row);
                moved = true;
            }
        }
        for (var i = _rows.Count - 1; i >= chunks.Count; i--)
        {
            _rows.RemoveAt(i);
            moved = true;
        }

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

    /// <summary>Brings <paramref name="row"/> to match <paramref name="target"/> with the fewest
    /// item-level changes. A card in a slot that differs is replaced in place. The tail is appended, and
    /// the surplus is trimmed. So the inner ItemsControl only re-realizes the containers that actually
    /// moved. Returns whether anything had to change at all.</summary>
    private static bool SyncRow(ObservableCollection<AlbumCard> row, IReadOnlyList<AlbumCard> target)
    {
        var changed = false;
        while (row.Count > target.Count)
        {
            row.RemoveAt(row.Count - 1);
            changed = true;
        }
        for (var j = 0; j < target.Count; j++)
        {
            if (j >= row.Count)
            {
                row.Add(target[j]);
                changed = true;
            }
            else if (!ReferenceEquals(row[j], target[j]))
            {
                row[j] = target[j];
                changed = true;
            }
        }
        return changed;
    }
}
