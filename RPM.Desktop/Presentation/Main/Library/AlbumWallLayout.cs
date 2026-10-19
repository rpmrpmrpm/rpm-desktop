using System.Collections.Specialized;
using RPM.Core.Library;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// The wall's own virtualizing layout. It knows where every row is, rather than estimating it.
///
/// <para>Why an estimate is not enough. <c>StackLayout</c> works out the length of the collection, and
/// where its content starts, from the mean of the row heights it has measured:
/// <c>start = firstRealizedRowTop − firstRealizedIndex × mean</c>. Every row on the wall is the same
/// height except one, the record with its drawer open. That drawer is about 2671 points tall, against
/// about 172 points for a normal row. While the open row is realized, the wall is 4164 points long. When
/// that row scrolls out of the realized range, the same wall measures 1942 points, because the formula
/// drops everything above the topmost row it can still see. A scroller cannot scroll past its own end,
/// so a reader who was 3046 points down is pulled back to 1267, and then to the top. This is a clamp.
/// Scroll anchoring does not prevent it, because anchoring corrects the offset, and the correction is
/// clamped to the same wrong end.</para>
///
/// <para>So the estimate is removed. The arithmetic is in <see cref="AlbumWallRows"/>, where a test can
/// check it. This type talks to the repeater, and it holds exactly two numbers for that:</para>
///
/// <list type="bullet">
/// <item><b>the row height</b>, which every row shares. The card template reserves space for the title
/// and artist lines even on a record that has neither, so the rows match.</item>
/// <item><b>the drawer's height</b>, which one row adds to that.</item>
/// </list>
///
/// <para>Both are learned by measuring. They are kept when the row they came from is recycled away. A
/// mean forgets a row as soon as the row stops being realized. Two remembered numbers do not, so the wall
/// keeps the same length whether the open record is on screen or a thousand rows above it.</para>
///
/// <para><b>Kept is not the same as still true. The drawer's height is the one that goes stale.</b> It
/// changes with the card size, because the drawer holds a sleeve and a two-column tracklist, and both
/// re-flow with the width. It can only be re-learned from a pass that measured its row. So the open row
/// is held and measured on every pass, on screen or off. It is the only row that gets this treatment.</para>
///
/// <para><see cref="BandRow"/> is the one thing the layout cannot see for itself. An unrealized row is not
/// measured, so nothing tells the layout that it is the tall one. The view owns the drawer and reports it
/// through <c>AlbumWallView.HangBand</c> and <c>DropBand</c>. Those are the only two places where a drawer
/// is attached or detached. What the view reports is checked in one call on every pass. See
/// <see cref="ResolveBandRow"/>.</para>
/// </summary>
internal sealed partial class AlbumWallLayout : VirtualizingLayout
{
    /// <summary>The gap between rows. It falls BETWEEN them, so the top of the wall has none.</summary>
    public double Spacing { get; set; }

    /// <summary>
    /// How much bigger the cards are shown than they are laid out, while a pane resizes the wall live.
    /// It is one at rest, which is nearly all the time, and nothing below behaves differently at one.
    /// </summary>
    /// <remarks>
    /// <b>What this is for.</b> During a live resize, writing a new card size on every frame makes every
    /// realized card dirty, and that re-runs its whole subtree. About 1700 elements were affected, 140 of
    /// them text that re-shapes and re-trims because its width changed. The wall's own code took 13.8 ms
    /// of a 113 ms stall. The rest was the framework laying out what that code had dirtied.
    ///
    /// <para>So the cards stay at the size the move began at, and the difference is carried here. Rows are
    /// measured and arranged at that fixed size, and drawn through a <see cref="ScaleTransform"/> of this
    /// amount. That is a render property, and it dirties no layout. A card whose bound sizes never change
    /// is never dirty. A repeated measure against an unchanged constraint returns without traversing the
    /// card.</para>
    ///
    /// <para><b>It does not drift.</b> <c>ColumnsForTarget</c> rounds to the nearest whole column, so the
    /// realized stride stays close to the target by construction. Over a sweep from 1000 to 1900 points at a
    /// 166 target, <c>floor(available/columns)</c> only ranges from 157 to 175. So the scale stays within
    /// ±5.4% of one for a whole move, without the laid-out size ever being reset. The type changes size by
    /// that much and no more.</para>
    ///
    /// <para>Everything vertical is scaled: the row pitch, the spacing and the drawer. So the wall's length
    /// and the realization band are in the same space as the scroller's viewport. Only the arrange rect is
    /// not scaled. A row is placed at its scaled top and arranged at its own unscaled size. The transform
    /// then takes it from there, anchored at its top-left.</para>
    /// </remarks>
    public double Scale
    {
        get => _scale;
        set
        {
            if (_scale == value || double.IsNaN(value) || value <= 0)
                return;
            _scale = value;
            InvalidateMeasure();
        }
    }

    private double _scale = 1;

    /// <summary>
    /// The width a row is laid out at while <see cref="Scale"/> is in force: the column count times the
    /// fixed card stride. Zero means use the width the pass was given. That is the normal case.
    /// </summary>
    /// <remarks>
    /// The view has to state this number. The available width changes on every frame of a live resize, and
    /// a measure against a constraint that changed is a measure that traverses the card. Held constant
    /// between column changes, every row's measure is a cached no-op.
    /// </remarks>
    public double RowWidth { get; set; }

    /// <summary>The row that holds the open drawer, or -1 when no record is open. The view writes this on
    /// every open, close and re-seat.</summary>
    public int BandRow
    {
        get => _bandRow;
        set
        {
            if (_bandRow == value)
                return;
            _bandRow = value;
            if (value < 0)
                _bandHeight = 0;   // nothing is open, so the wall is uniform again and stays that way
            InvalidateMeasure();
        }
    }

    private int _bandRow = -1;

    /// <summary>What the drawer adds to <see cref="BandRow"/>. It is learned from that row, which is measured
    /// on every pass whether or not it is on screen. A mean of all the rows would lose this value. It also
    /// changes whenever the card size changes.</summary>
    private double _bandHeight;

    /// <summary>The height every row without a drawer has. It is learned the same way, and it is re-learned on
    /// every pass that measures a plain row. So the cover-size slider does not need to tell it anything.</summary>
    private double _rowHeight;

    /// <summary>Where <paramref name="index"/>'s top edge sits, from the two remembered heights. This is the
    /// same sum that <see cref="ArrangeOverride"/> places it with, but it is asked before the pass that would
    /// place it.
    ///
    /// <para>This is for the one caller that has to know how far a row is about to move. Attaching or detaching
    /// a drawer moves every row below it. A reader standing on one of those rows has to be moved with it, or the
    /// wall moves out from under them. See <c>AlbumWallView.ReshapeHoldingReader</c>, which reads this on both
    /// sides of the change.</para></summary>
    /// <remarks>In the SCROLLER's space, scale included, because the one caller compares it with
    /// <c>VerticalOffset</c>.</remarks>
    public double TopOf(int index) =>
        AlbumWallRows.RowTop(index, _rowHeight * _scale, Spacing * _scale, _bandRow, _bandHeight * _scale);

    /// <summary>The rows the last pass asked for, so that the rows which leave the realization band can be
    /// handed back. The repeater does not do this itself. A <see cref="VirtualizingLayout"/> owns which
    /// elements it holds, and an element that is never recycled is never reused.</summary>
    private sealed class Held
    {
        public readonly Dictionary<int, UIElement> Rows = new();
    }

    protected override void InitializeForContextCore(VirtualizingLayoutContext context)
        => context.LayoutState = new Held();

    protected override void UninitializeForContextCore(VirtualizingLayoutContext context)
        => context.LayoutState = null;

    /// <summary>The shelf changed. Everything realized belongs to the old collection, and the two remembered
    /// heights were measured from it. The row height is a property of the card, not of the records on it, so
    /// it is kept. The drawer is not kept. The view sets <see cref="BandRow"/> again as part of the same
    /// update.</summary>
    protected override void OnItemsChangedCore(
        VirtualizingLayoutContext context, object source, NotifyCollectionChangedEventArgs args)
    {
        if (context.LayoutState is Held held)
            held.Rows.Clear();
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        // If the held state is missing, keep the new one on the context. A throwaway would lose the realized
        // rows, and rows that nobody holds are rows that nobody recycles.
        if (context.LayoutState is not Held held)
            context.LayoutState = held = new Held();
        var count = context.ItemCount;
        if (count <= 0)
        {
            Recycle(context, held, keep: default);
            return new Size(0, 0);
        }

        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;

        // Rows are laid out at the width the view states while a live resize is scaling them. Otherwise they
        // use the width this pass was given. See RowWidth. A constraint that changes on every pass makes every
        // measure traverse the cards, and avoiding that is the whole saving.
        var room = new Size(RowWidth > 0 ? RowWidth : width, double.PositiveInfinity);

        // The heights as the SCROLLER sees them. Its viewport, its offset and the realization rect are all in
        // scaled space. So the band arithmetic and the length returned must be scaled too. The arrange rect is
        // the one thing that stays unscaled. See ArrangeOverride.
        var k = _scale;
        var pitch = _rowHeight * k;
        var gap = Spacing * k;
        var drawer = _bandHeight * k;

        // Nothing has been measured yet, so there is no pitch to work out the realization band from. One row
        // gives the answer. It must not be the OPEN row. The open row's height is its cards plus a drawer. Used
        // as the shared height, it would make every row on the wall look drawer-tall. The open row also teaches
        // the drawer nothing until the shared height is known. So probing it would leave the pitch at zero. A
        // wall with no pitch puts every row inside the realization band, so the whole collection is inflated in
        // one pass. That is the multi-second hang that the viewport check exists to prevent. The exception is a
        // wall whose only row is the open one. There, that row is the honest answer, since nothing is below it,
        // and its whole height is the height of the wall.
        if (_rowHeight <= 0)
        {
            var probeAt = _bandRow == 0 && count > 1 ? 1 : 0;
            var probe = context.GetOrCreateElementAt(probeAt);
            held.Rows[probeAt] = probe;
            probe.Measure(room);
            if (probeAt == _bandRow)
                _rowHeight = probe.DesiredSize.Height;
            else
                Learn(probeAt, probe.DesiredSize.Height);
        }

        // Still nothing. This happens when a row measures to nothing at all, for example before there is a
        // viewport or before the template has arrived. Hold the one row, instead of working out a band from a
        // pitch of zero.
        if (_rowHeight <= 0)
        {
            Recycle(context, held, keep: (0, 0));
            return new Size(width, 0);
        }

        var band = ResolveBandRow(context, count);
        var (first, last) = AlbumWallRows.RowsIn(
            context.RealizationRect.Top, context.RealizationRect.Bottom,
            count, pitch, gap, band, drawer);
        if (first < 0)
            (first, last) = (0, 0);

        Recycle(context, held, keep: (first, last), alsoKeep: band);

        for (var i = first; i <= last; i++)
        {
            var element = context.GetOrCreateElementAt(i);
            held.Rows[i] = element;
            element.Measure(room);
            Learn(i, element.DesiredSize.Height);
        }

        // The open row, wherever it is. It is held and measured on every pass, not only while it is on screen.
        // It is the only row that gets this treatment.
        //
        // Its height is the one number the wall cannot work out for itself, and Learn can only learn it from a
        // row that was measured. If the open row is left unrealized, it keeps the height it was last measured at.
        // What changes that height is the card size, because the drawer holds a sleeve and a two-column tracklist,
        // and both re-flow with the width. For example: open a record, scroll until its row leaves the realization
        // band, then resize. The row height is re-learned from an ordinary row on the next pass, but the drawer's
        // height is not. So every row below the drawer is placed using two numbers measured at two different card
        // sizes. The reader is one of those rows, and the wall's whole length is wrong by the same amount, so a
        // reader near the end is clamped as well. Nothing corrects this until the reader scrolls back up to the
        // open record.
        //
        // This measures one extra row, and only while a record is open. It is the tall, expensive row, which is the
        // cost. The benefit is that its height decides the placement of every row below it, and there is no other
        // way to know that height. On a pass where nothing changed, it costs nothing extra, because an element that
        // is not dirty answers a repeated constraint without traversing.
        if (band >= 0 && (band < first || band > last))
        {
            var open = context.GetOrCreateElementAt(band);
            held.Rows[band] = open;
            open.Measure(room);
            Learn(band, open.DesiredSize.Height);
        }

        // Read the band again AFTER measuring. A drawer that grew in this pass (it is tweened open a frame at a
        // time) must count in the length returned. Otherwise the wall reports a height it has already outgrown,
        // and the last rows sit under the bottom of the scroller. The band and the heights are read again here,
        // because the loop above may have learned a new pitch or a drawer that grew in this pass.
        return new Size(width, AlbumWallRows.WallHeight(
            count, _rowHeight * k, Spacing * k, ResolveBandRow(context, count), _bandHeight * k));
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        if (context.LayoutState is not Held held)
            return finalSize;

        var count = context.ItemCount;
        var band = ResolveBandRow(context, count);
        var k = _scale;
        var width = RowWidth > 0 ? RowWidth : finalSize.Width;

        foreach (var (index, element) in held.Rows)
        {
            // THE ARRANGE RECT IS THE UNSCALED ONE, and it does not move while a resize runs. The scale, and the
            // distance from the unscaled top to where the row must appear, are both carried by the transform below.
            //
            // This is the main reason a live resize is cheap. Arranging at the scaled top would give every row a
            // different rect on every frame. A changed rect re-arranges the row, its panel, its repeater and every
            // card in it. That is the same traversal the fixed card size exists to stop. Measured: with the cards
            // fixed but the tops still moving, ten folds ran at six frames of eight, with a worst gap of 79 ms. That
            // number is before any of this. An unchanged rect is an arrange that the framework skips.
            var top = AlbumWallRows.RowTop(index, _rowHeight, Spacing, band, _bandHeight);
            var shown = AlbumWallRows.RowTop(index, _rowHeight * k, Spacing * k, band, _bandHeight * k);

            // The row is placed at the height it asked for, not the height the arithmetic expects. The two agree,
            // except in the frame where a drawer changes size. In that frame the element's own answer is the true
            // one. Squashing it to the stale figure would clip the last track of the drawer for a frame at a time
            // while it opens.
            var height = element is FrameworkElement { DesiredSize.Height: > 0 } sized
                ? sized.DesiredSize.Height
                : AlbumWallRows.RowHeight(index, _rowHeight, band, _bandHeight);
            element.Arrange(new Rect(0, top, width, height));
            Draw(element, k, shown - top);
        }
        return finalSize;
    }

    /// <summary>
    /// Draws one row at <see cref="Scale"/>, <paramref name="dy"/> away from where it was arranged. At rest,
    /// it removes the transform.
    /// </summary>
    /// <remarks>
    /// A render transform, not a layout position. This is the point of it. It moves and resizes what is drawn
    /// without making anything dirty. So the rect the row was arranged into can stay where it is for the whole
    /// move, and the framework can skip re-arranging the row.
    ///
    /// <para>A <c>CompositeTransform</c> rather than a group of two. Its scale and translation are four numbers on
    /// one object. A move overwrites them, and allocates nothing per row per frame. It scales about its own
    /// origin, which is (0,0), and translates afterwards. So a point at local <c>y</c> lands at
    /// <c>arrangedTop + k·y + dy</c>. That is why <paramref name="dy"/> is the gap between where the row was put
    /// and where it has to appear.</para>
    ///
    /// <para>It is written on every arrange, not only when the scale changes. Rows are recycled. A recycled
    /// element given to another index would otherwise still carry the transform of the row it used to be. Every
    /// row this layout holds is arranged on every pass, so this is the one place that cannot be missed.</para>
    ///
    /// <para>At rest the transform is removed outright. A scale of one with no shift is still a transform that
    /// the renderer carries, and the wall is at rest almost all of the time.</para>
    /// </remarks>
    private static void Draw(UIElement element, double k, double dy)
    {
        if (element is not FrameworkElement row)
            return;

#if RPM_WINUI
        // On WinUI the scale and the shift are set on the row's composition visual, not on a XAML transform. The
        // compositor applies a visual's matrix to the layer that is already drawn, so nothing is rasterized again at
        // the new scale. A XAML transform makes WinUI re-render every glyph run in the row on every frame. That costs
        // as much as the layout pass that the scale is meant to remove. The matrix scales about the row's top-left,
        // then shifts it by dy, as the CompositeTransform below does.
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(row);
        visual.TransformMatrix = k == 1 && Math.Abs(dy) < 0.01
            ? System.Numerics.Matrix4x4.Identity
            : System.Numerics.Matrix4x4.CreateScale((float)k, (float)k, 1)
              * System.Numerics.Matrix4x4.CreateTranslation(0, (float)dy, 0);
#else

        if (k == 1 && Math.Abs(dy) < 0.01)
        {
            if (row.RenderTransform is CompositeTransform)
                row.RenderTransform = null;
            return;
        }

        if (row.RenderTransform is CompositeTransform drawn)
        {
            drawn.ScaleX = k;
            drawn.ScaleY = k;
            drawn.TranslateY = dy;
            return;
        }

        row.RenderTransformOrigin = default;   // top-left, which is what the placement above assumes
        row.RenderTransform = new CompositeTransform { ScaleX = k, ScaleY = k, TranslateY = dy };
#endif
    }

    /// <summary>A row was measured, so one of the two numbers is now known better. The open row teaches the
    /// drawer's height. Every other row teaches the shared row height.</summary>
    private void Learn(int index, double height)
    {
        if (height <= 0)
            return;
        if (index == _bandRow)
        {
            // Only once there is a row height to subtract from. Otherwise the whole length of the drawer would be
            // recorded as the extra, and every row below it would be placed too far down, twice.
            if (_rowHeight > 0)
                _bandHeight = Math.Max(0, height - _rowHeight);
            return;
        }
        if (WallDiag.On && _rowHeight > 0 && Math.Abs(height - _rowHeight) > 0.5)
            WallDiag.Open($"pitch moved by row {index}: {_rowHeight:0.#} → {height:0.#}"
                + $" (every row below it moves {Math.Abs(height - _rowHeight):0.#} × its index)");
        _rowHeight = height;
    }

    /// <summary>
    /// The open row, or -1 if there is none in this collection. The index is checked rather than believed. A
    /// band index that points past the end of a shelf which has since been narrowed would add the drawer's
    /// height to a wall that does not show it.
    /// </summary>
    /// <remarks>
    /// <para>Nothing open short-circuits on the first test, so an ordinary wall costs one comparison. A row that
    /// still carries its drawer costs one more: a single indexer into the shelf, not a walk. Only a drawer that
    /// has moved since it was named pays for a scan. A drawer moves on a click or a reflow, never on a scroll.</para>
    ///
    /// <para>Why check at all, when the view says where the drawer is? A stale index is not a wrong pixel. It is a
    /// wall that reports a length it does not have. That is the fault this whole type exists to prevent. One
    /// comparison a pass keeps the reported answer from going stale.</para>
    /// </remarks>
    private int ResolveBandRow(VirtualizingLayoutContext context, int count)
    {
        if (_bandRow < 0 || count <= 0)
            return -1;
        if (_bandRow < count && HasBand(context, _bandRow))
            return _bandRow;

        for (var i = 0; i < count; i++)
        {
            if (!HasBand(context, i))
                continue;
            _bandRow = i;
            return i;
        }

        _bandRow = -1;
        _bandHeight = 0;
        return -1;
    }

    private static bool HasBand(VirtualizingLayoutContext context, int index)
        => context.GetItemAt(index) is Models.AlbumRow { Band: not null };

    /// <summary>Hands back every row outside <paramref name="keep"/>. The leavers are collected first, because
    /// recycling while iterating the dictionary would invalidate the iteration.</summary>
    /// <param name="alsoKeep">One index outside the band that is kept anyway, or -1. It is the open row. See the
    /// measure pass for why that row is never handed back.</param>
    private static void Recycle(
        VirtualizingLayoutContext context, Held held, (int First, int Last) keep, int alsoKeep = -1)
    {
        List<int>? leaving = null;
        foreach (var index in held.Rows.Keys)
        {
            if (index >= keep.First && index <= keep.Last && index < context.ItemCount)
                continue;
            if (index == alsoKeep && index < context.ItemCount)
                continue;
            (leaving ??= new List<int>()).Add(index);
        }
        if (leaving is null)
            return;
        foreach (var index in leaving)
        {
            context.RecycleElement(held.Rows[index]);
            held.Rows.Remove(index);
        }
    }
}
