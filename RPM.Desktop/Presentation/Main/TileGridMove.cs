using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
#if RPM_WINUI
using System.Numerics;
using Microsoft.UI.Xaml.Controls.Primitives;
#endif

namespace RPM.Desktop.Presentation.Main;

/// <summary>
/// A grid of equal tiles while a pane moves beside it: the artists wall, the genre wall and the playlists'
/// tiles. It does for a grid that is one <see cref="ItemsRepeater"/> with a <see cref="UniformGridLayout"/>
/// what the album wall does for its rows (see <c>AlbumWallView.ExpectLiveWidth</c> and
/// <c>AlbumWallView.Glide.cs</c>).
/// </summary>
/// <remarks>
/// <para><b>During a move</b> the tiles keep the size the move began at, and the grid keeps one column count
/// for the whole move: the larger of the one it has and the one it will land on. The repeater is laid out at
/// that many tiles and drawn scaled to the width it has, about the top of the viewport (<see cref="Draw"/>).
/// Nothing in a tile is measured again on a frame of the move.</para>
///
/// <para><b>One re-deal.</b> The tiles are dealt into the new count once: at the start of a move that makes
/// the grid wider, at the landing of one that makes it narrower. Each tile in view then slides from where it
/// was drawn to its new place, at the size it was drawn at, and a tile that was not in view fades in. The
/// first tile of the top row in view keeps its place on screen, and the scroll offset moves to keep it there.
/// A tile is known by its index: a column change moves tiles but never changes their order.</para>
///
/// <para><b>Elements are recycled.</b> A slide runs on an element, and the repeater hands that element to
/// other tiles. An element that is prepared again or cleared stops its slide.</para>
///
/// <para>The repeater must not be its scroller's direct content. The scroller moves its content through the
/// content's composition visual, so a scale on the content would replace the scroll.</para>
///
/// <para>WinUI only. On the Skia head the grids keep re-flowing on every frame, and these calls do
/// nothing.</para>
/// </remarks>
internal sealed class TileGridMove
{
    /// <summary>A grid as it is laid out: how many tiles go across, a tile's width and height, and the gaps
    /// between tiles across and down.</summary>
    public readonly record struct Shape(int Columns, double Width, double Height, double ColumnGap, double RowGap)
    {
        /// <summary>The width of a full row.</summary>
        public double Laid => (Columns * Width) + (Math.Max(0, Columns - 1) * ColumnGap);

        /// <summary>One row's worth of vertical travel.</summary>
        public double Pitch => Height + RowGap;
    }

    private readonly ScrollViewer _scroller;
    private readonly ItemsRepeater _tiles;
    private readonly Func<Shape?> _shape;

    /// <summary>The move's grid: the tile size it began at, and the column count it keeps. Null when no move
    /// is running.</summary>
    private Shape? _held;

    /// <summary>The scale the grid is drawn at during a move.</summary>
    private double _scale = 1;

    /// <param name="scroller">The scroller the grid is in.</param>
    /// <param name="tiles">The grid. It must not be the scroller's direct content.</param>
    /// <param name="name">What the grid is called on the <c>panes</c> channel.</param>
    /// <param name="shape">The grid as the view has laid it out at rest, or null while it has not.</param>
    public TileGridMove(ScrollViewer scroller, ItemsRepeater tiles, string name, Func<Shape?> shape)
    {
        _scroller = scroller;
        _tiles = tiles;
        _shape = shape;
#if RPM_WINUI
        _name = name;
        tiles.ElementPrepared += (_, e) => StopGlide(e.Element);
        tiles.ElementClearing += (_, e) => StopGlide(e.Element);
        scroller.ViewChanged += (_, e) =>
        {
            if (!e.IsIntermediate)
                _heldOffset = null;
        };
#endif
    }

    /// <summary>Whether a move is holding the grid.</summary>
    public bool Live => _held is not null;

    /// <summary>The column count the move holds, or zero when no move is running.</summary>
    public int Columns => _held?.Columns ?? 0;

    /// <summary>A pane is about to resize the grid, one frame at a time. The grid keeps its tile size and its
    /// column count until <see cref="Expect"/> or <see cref="End"/> changes the count.</summary>
    public void Begin()
    {
#if RPM_WINUI
        if (_held is not null || Library.AlbumWallView.Freeze != Library.AlbumWallView.WallFreeze.Scale
            || !Shown || _shape() is not { Columns: > 0, Width: > 0 } now)
            return;
        _held = now;
        _scale = 1;
#endif
    }

    /// <summary>
    /// The pane says how many columns the grid will have where the move lands. The grid keeps the larger of
    /// that and the count it has, so a grid that will be wider is dealt into its new count now, and grows
    /// into it.
    /// </summary>
    /// <param name="finalColumns">The column count at the width the move lands at.</param>
    /// <param name="available">The width the grid has now.</param>
    public void Expect(int finalColumns, double available)
    {
        if (_held is not { } held || finalColumns <= 0 || available <= 0)
            return;
        var columns = Math.Max(held.Columns, finalColumns);
        if (columns == held.Columns)
            return;

        var next = held with { Columns = columns };
        Rearrange(held, _scale, next, available / next.Laid, () =>
        {
            _held = next;
            _scale = available / next.Laid;
            LayLive(next);
        });
    }

    /// <summary>
    /// Lays the grid out at the move's column count and tile size, and draws it scaled to
    /// <paramref name="available"/>. Returns false when no move is running, and the view then lays the grid
    /// out itself.
    /// </summary>
    public bool Draw(double available)
    {
        if (_held is not { } held || available <= 0)
            return false;
        LayLive(held);
        _scale = available / held.Laid;
#if RPM_WINUI
        ApplyScale();
#endif
        return true;
    }

    /// <summary>
    /// The move has landed. The scale goes, and <paramref name="settle"/> lays the grid out at the width it
    /// keeps. If that is another column count, the tiles slide to their new places.
    /// </summary>
    public void End(Action settle)
    {
        if (_held is not { } held)
        {
            settle();
            return;
        }

        Rearrange(held, _scale, to: null, 1, () =>
        {
            _held = null;
            _scale = 1;
#if RPM_WINUI
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(_tiles).TransformMatrix =
                Matrix4x4.Identity;
#endif
            _tiles.Width = double.NaN;
            _tiles.HorizontalAlignment = HorizontalAlignment.Stretch;
            settle();
        });
    }

    /// <summary>Whether the grid is on screen. A collapsed view keeps the sizes it last had, so the sizes alone
    /// do not say; the grid and everything it is in must be visible as well.</summary>
    private bool Shown
    {
        get
        {
            if (_scroller.ActualWidth <= 0 || _scroller.ActualHeight <= 0 || _tiles.ActualWidth <= 0
                || _tiles.XamlRoot is null)
                return false;
            for (DependencyObject? element = _tiles; element is not null; element = VisualTreeHelper.GetParent(element))
                if (element is UIElement { Visibility: not Visibility.Visible })
                    return false;
            return true;
        }
    }

    /// <summary>The repeater is given exactly the width of <paramref name="shape"/>'s columns. The layout then
    /// fits that many tiles, and nothing in a tile changes. Half a point more than the sum keeps a rounding
    /// error from losing a column.</summary>
    private void LayLive(Shape shape)
    {
        _tiles.HorizontalAlignment = HorizontalAlignment.Left;
        _tiles.Width = shape.Laid + 0.5;
    }

    /// <summary>
    /// Changes the grid's layout with <paramref name="change"/>, keeps the reader's row where it is on screen,
    /// and slides each tile in view from where it was drawn to where it is drawn now.
    /// </summary>
    /// <param name="from">The grid before the change.</param>
    /// <param name="scaleFrom">The scale it is drawn at before the change.</param>
    /// <param name="to">The grid after the change, or null to ask the view once the change is made.</param>
    /// <param name="scaleTo">The scale it is drawn at after the change.</param>
    /// <param name="change">Lays the grid out in its new shape.</param>
    private void Rearrange(Shape from, double scaleFrom, Shape? to, double scaleTo, Action change)
    {
#if RPM_WINUI
        var count = _tiles.ItemsSourceView?.Count ?? 0;
        if (count == 0 || !Shown || from.Pitch <= 0 || from.Columns <= 0)
        {
            change();
            return;
        }

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var gridTop = GridTop();
        var viewFrom = ViewTop();

        // Where each tile in view is drawn now, from the top-left corner of the viewport, and how wide.
        var was = new Dictionary<int, (Vector2 At, float Width)>();
        foreach (var (index, element) in InView(from, viewFrom, scaleFrom, count))
        {
            var slot = LayoutInformation.GetLayoutSlot(element);
            if (slot.Width <= 0)
                continue;
            var (left, size) = Remaining(index, now);
            was[index] = (new Vector2((float)(slot.X * scaleFrom), (float)((slot.Y - viewFrom) * scaleFrom)) + left,
                          (float)(slot.Width * scaleFrom) * size);
        }
        var reader = Reader(from, viewFrom, scaleFrom, count);

        change();

        // A grid that was rebuilt or emptied by the change has other tiles, and nothing slides.
        if ((to ?? _shape()) is not { Columns: > 0 } shape || shape.Pitch <= 0
            || (_tiles.ItemsSourceView?.Count ?? 0) != count)
            return;

        // The reader's row is put where it was on screen.
        var viewTo = viewFrom;
        if (reader is { } place)
        {
            viewTo = Math.Max(0, (place.Index / shape.Columns * shape.Pitch) - (place.Below / scaleTo));
            _stickyOffset = viewTo + gridTop;
            if (Math.Abs(viewTo + gridTop - _scroller.VerticalOffset) >= 0.5)
            {
                _heldOffset = viewTo + gridTop;
                _scroller.ChangeView(null, viewTo + gridTop, null, disableAnimation: true);
            }
        }
        if (_held is not null)
            ApplyScale();

        // Two passes, in this turn. The first applies the offset. The repeater learns what is in view only
        // after a pass, so the second is the one that realizes the tiles at the new place.
        _scroller.UpdateLayout();
        _scroller.UpdateLayout();

        int slid = 0, faded = 0;
        foreach (var (index, element) in InView(shape, viewTo, scaleTo, count))
        {
            var slot = LayoutInformation.GetLayoutSlot(element);
            if (slot.Width <= 0)
                continue;
            var at = new Vector2((float)(slot.X * scaleTo), (float)((slot.Y - viewTo) * scaleTo));
            if (was.TryGetValue(index, out var before))
            {
                var offset = before.At - at;
                var size = before.Width / (float)(slot.Width * scaleTo);
                if (offset.LengthSquared() < 0.25f && Math.Abs(size - 1) < 0.005f)
                {
                    StopGlide(element);
                    _glides.Remove(index);
                    continue;
                }
                // The tile sits inside the grid, which is drawn at the scale. A translation on the tile is in
                // the grid's own units, so it is the distance on screen divided by the scale.
                Slide(element, offset / (float)scaleTo, size);
                _glides[index] = (offset, size, now);
                slid++;
            }
            else
            {
                Fade(element);
                faded++;
            }
        }
        RPM.Core.Diagnostics.Scope.Said("panes",
            $"{_name}: {from.Columns} -> {shape.Columns} columns, {slid} tiles slid, {faded} faded in");
#else
        change();
#endif
    }

#if RPM_WINUI
    /// <summary>How long a tile takes to reach its new place. The album wall's cards take the same
    /// time.</summary>
    private const double GlideMs = 220;

    /// <summary>How long a tile that comes into view takes to fade in.</summary>
    private const double FadeMs = 160;

    /// <summary>What the grid is called on the <c>panes</c> channel.</summary>
    private readonly string _name;

    /// <summary>The scroll offset asked for to keep the reader in place, until the scroller reports it.</summary>
    private double? _heldOffset;

    /// <summary>The reader's place as it was first written down, and the offset it was last held at. While
    /// the offset is still that one, the reader has not scrolled, and the same place is held again. So an
    /// open and a close bring the grid back to exactly what the reader left.</summary>
    private (int Index, double Below)? _sticky;

    private double _stickyOffset = double.NaN;

    /// <summary>The slides that are running, by tile index: how far the tile started from its place, at what
    /// size against its place's, and when.</summary>
    private readonly Dictionary<int, (Vector2 Offset, float Size, long Started)> _glides = new();

    /// <summary>The elements that have been given a slide or a fade, so that only those are ever
    /// stopped.</summary>
    private readonly HashSet<UIElement> _gliding = new();

    /// <summary>Where the top of the viewport is, from the top of the grid. An offset asked for to hold the
    /// reader is the one the next frame is drawn at, even before the scroller reports it.</summary>
    private double ViewTop() => (_heldOffset ?? _scroller.VerticalOffset) - GridTop();

    /// <summary>How far down the scroller's content the grid starts.</summary>
    private double GridTop()
    {
        var top = _scroller.Padding.Top;
        var content = _scroller.Content as UIElement;
        for (UIElement? element = _tiles; element is not null && !ReferenceEquals(element, content);
             element = VisualTreeHelper.GetParent(element) as UIElement)
            top += element.ActualOffset.Y;
        return top;
    }

    /// <summary>Draws the grid at the move's scale, about the top of the viewport, so the row the reader is on
    /// stays where it is.</summary>
    private void ApplyScale()
    {
        var k = (float)_scale;
        var top = (float)ViewTop();
        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(_tiles).TransformMatrix =
            Matrix4x4.CreateTranslation(0, -top, 0)
            * Matrix4x4.CreateScale(k, k, 1)
            * Matrix4x4.CreateTranslation(0, top, 0);
    }

    /// <summary>The realized tiles in the rows that <paramref name="view"/> shows, drawn at
    /// <paramref name="scale"/>.</summary>
    private IEnumerable<(int Index, FrameworkElement Element)> InView(Shape shape, double view, double scale, int count)
    {
        var height = _scroller.ViewportHeight / Math.Max(scale, 0.01);
        var firstRow = Math.Max(0, (int)Math.Floor(view / shape.Pitch));
        var lastRow = (int)Math.Floor((view + height) / shape.Pitch);
        var last = Math.Min(count - 1, ((lastRow + 1) * shape.Columns) - 1);
        for (var index = firstRow * shape.Columns; index <= last; index++)
            if (_tiles.TryGetElement(index) is FrameworkElement element)
                yield return (index, element);
    }

    /// <summary>The reader's place: the first tile of the top row in view, and how far below the top of the
    /// viewport that row is drawn. The top row is the one under the top of the viewport, or the one below it
    /// when less than half of it is still in view.</summary>
    private (int Index, double Below)? Reader(Shape shape, double view, double scale, int count)
    {
        if (_sticky is { } sticky && Math.Abs((_heldOffset ?? _scroller.VerticalOffset) - _stickyOffset) < 1
            && sticky.Index < count)
            return sticky;

        var row = Math.Max(0, (int)Math.Floor(view / shape.Pitch));
        if ((row * shape.Pitch) + (shape.Pitch / 2) < view)
            row++;
        var index = row * shape.Columns;
        if (index >= count)
            return null;
        _sticky = (index, ((row * shape.Pitch) - view) * scale);
        return _sticky;
    }

    /// <summary>How far a tile still is from its place on screen at <paramref name="now"/>, and its size
    /// against its place's. Nothing and one for a tile that is not sliding.</summary>
    private (Vector2 Offset, float Size) Remaining(int index, long now)
    {
        if (!_glides.TryGetValue(index, out var glide))
            return (Vector2.Zero, 1f);
        var t = System.Diagnostics.Stopwatch.GetElapsedTime(glide.Started, now).TotalMilliseconds / GlideMs;
        if (t >= 1)
        {
            _glides.Remove(index);
            return (Vector2.Zero, 1f);
        }
        var left = (float)(1 - PaneGlide.EaseAt(t));
        return (glide.Offset * left, 1 + ((glide.Size - 1) * left));
    }

    /// <summary>Starts <paramref name="element"/> <paramref name="offset"/> away from its place, in the grid's
    /// units, at <paramref name="size"/> times its own size, and slides it home. The size is scaled about the
    /// tile's top-left corner, which is the corner its place is measured to.</summary>
    private void Slide(FrameworkElement element, Vector2 offset, float size)
    {
        StopGlide(element);
        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var ease = PaneGlide.Ease(compositor);

        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0f, new Vector3(offset, 0));
        slide.InsertKeyFrame(1f, Vector3.Zero, ease);
        slide.Duration = TimeSpan.FromMilliseconds(GlideMs);
        visual.StartAnimation("Translation", slide);

        if (Math.Abs(size - 1) >= 0.005f)
        {
            visual.CenterPoint = Vector3.Zero;
            var grow = compositor.CreateVector3KeyFrameAnimation();
            grow.InsertKeyFrame(0f, new Vector3(size, size, 1));
            grow.InsertKeyFrame(1f, Vector3.One, ease);
            grow.Duration = TimeSpan.FromMilliseconds(GlideMs);
            visual.StartAnimation("Scale", grow);
        }
        _gliding.Add(element);
    }

    private void Fade(FrameworkElement element)
    {
        StopGlide(element);
        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, PaneGlide.Ease(compositor));
        fade.Duration = TimeSpan.FromMilliseconds(FadeMs);
        visual.StartAnimation("Opacity", fade);
        _gliding.Add(element);
    }

    /// <summary>Puts an element that was given a slide or a fade back where layout put it, at full opacity.
    /// Does nothing for an element that was never given one: stopping an animation on a property that a
    /// visual does not have throws.</summary>
    private void StopGlide(UIElement element)
    {
        if (!_gliding.Remove(element))
            return;
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Translation");
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        visual.StopAnimation("Scale");
        visual.Scale = Vector3.One;
        visual.StopAnimation("Opacity");
        visual.Opacity = 1;
    }
#endif
}
