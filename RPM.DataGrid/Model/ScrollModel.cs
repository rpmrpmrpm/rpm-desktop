namespace RPM.DataGrid.Model;

/// <summary>
/// Where the grid is scrolled to, and what moves it.
///
/// <para>The offsets are two numbers this class owns. There is no <c>ScrollViewer</c> underneath
/// them and nothing else can set them. This is deliberate: a <c>ScrollViewer</c> runs its own
/// layout pass and can change the offset itself. Here an offset is assigned and then drawn, and
/// nothing else moves it.</para>
///
/// <para>There is no inertia here either. A trackpad flick arrives as the platform's own momentum
/// events (many small scrolls per gesture), so the deltas are applied as they come. A second,
/// simulated deceleration on top would keep the list moving after the user's fingers stop, and
/// would swallow the next tap.</para>
/// </summary>
public sealed class ScrollModel
{
    /// <summary>What one detent of a wheel reports. The platform's own unit.</summary>
    public const double DetentUnits = 120;

    private double _x;
    private double _y;
    private double _extentWidth;
    private double _extentHeight;
    private double _viewportWidth;
    private double _viewportHeight;

    /// <summary>How far the strip is scrolled sideways.</summary>
    public double X => _x;

    /// <summary>How far the list is scrolled down.</summary>
    public double Y => _y;

    /// <summary>How wide the whole strip is.</summary>
    public double ExtentWidth => _extentWidth;

    /// <summary>How tall the whole list is.</summary>
    public double ExtentHeight => _extentHeight;

    public double ViewportWidth => _viewportWidth;

    public double ViewportHeight => _viewportHeight;

    /// <summary>The furthest right it can go.</summary>
    public double MaxX => Math.Max(0, _extentWidth - _viewportWidth);

    /// <summary>The furthest down it can go.</summary>
    public double MaxY => Math.Max(0, _extentHeight - _viewportHeight);

    /// <summary>How many rows one detent of a wheel moves. Three is the usual platform
    /// default.</summary>
    public double RowsPerDetent { get; set; } = 3;

    /// <summary>How far one detent moves the strip sideways.</summary>
    public double PixelsPerDetentX { get; set; } = 48;

    /// <summary>Raised whenever an offset changes.</summary>
    public event Action? Scrolled;

    /// <summary>
    /// Says how big the content and the viewport are. The offsets are brought back inside the new
    /// bounds, so a window getting wider does not leave the strip scrolled past its end.
    /// </summary>
    public bool Measure(double extentWidth, double extentHeight, double viewportWidth, double viewportHeight)
    {
        _extentWidth = Clean(extentWidth);
        _extentHeight = Clean(extentHeight);
        _viewportWidth = Clean(viewportWidth);
        _viewportHeight = Clean(viewportHeight);

        return MoveTo(_x, _y);
    }

    /// <summary>Puts the offsets where they are asked for, clamped. Returns whether they moved.</summary>
    public bool MoveTo(double x, double y)
    {
        var nx = Math.Clamp(Clean(x), 0, MaxX);
        var ny = Math.Clamp(Clean(y), 0, MaxY);

        if (Same(nx, _x) && Same(ny, _y))
            return false;

        _x = nx;
        _y = ny;
        Scrolled?.Invoke();
        return true;
    }

    /// <summary>Moves by an amount, clamped.</summary>
    public bool By(double dx, double dy) => MoveTo(_x + Clean(dx), _y + Clean(dy));

    /// <summary>Moves the list down by rows.</summary>
    public bool ByRows(double rows, double rowHeight) => By(0, rows * rowHeight);

    /// <summary>
    /// One wheel event.
    /// </summary>
    /// <param name="delta">The platform's wheel delta. Positive scrolls up and left, which is the
    /// platform's own sign and not a choice made here.</param>
    /// <param name="horizontal">True when the event is a sideways wheel or a shifted one.</param>
    /// <param name="rowHeight">How tall a row is, since a vertical wheel moves in rows.</param>
    public bool Wheel(double delta, bool horizontal, double rowHeight)
    {
        var detents = Clean(delta) / DetentUnits;
        if (detents == 0)
            return false;

        return horizontal
            ? By(-detents * PixelsPerDetentX, 0)
            : By(0, -detents * RowsPerDetent * Math.Max(1, rowHeight));
    }

    /// <summary>Brings a row into view, moving as little as possible.</summary>
    public bool ShowRow(int row, int count, RowStrip rows) =>
        MoveTo(_x, rows.ScrollToShow(row, _y, _viewportHeight, count));

    /// <summary>Brings a column into view, moving as little as possible.</summary>
    public bool ShowColumn(double left, double width)
    {
        if (_viewportWidth <= 0)
            return false;

        if (left < _x)
            return MoveTo(left, _y);

        var right = left + width;
        if (right > _x + _viewportWidth)
            return MoveTo(right - _viewportWidth, _y);

        return false;
    }

    /// <summary>
    /// How fast the list should be scrolling because a drag has taken the pointer outside the
    /// viewport, in pixels a second. Zero while the pointer is inside.
    ///
    /// <para>Kept apart from the tick that applies it so the rate is a testable function of where
    /// the pointer is, rather than something only observable by dragging.</para>
    /// </summary>
    /// <param name="pointerY">Where the pointer is, in the viewport's own coordinates.</param>
    /// <param name="maxRate">The fastest it goes, reached <paramref name="rampOver"/> outside.</param>
    /// <param name="rampOver">How far outside the viewport the rate takes to reach its maximum.</param>
    public double AutoScrollRate(double pointerY, double maxRate = 1200, double rampOver = 80)
    {
        if (_viewportHeight <= 0 || rampOver <= 0)
            return 0;

        var outside = pointerY < 0 ? pointerY
            : pointerY > _viewportHeight ? pointerY - _viewportHeight
            : 0;

        if (outside == 0)
            return 0;

        var ramp = Math.Clamp(Math.Abs(outside) / rampOver, 0, 1);
        return Math.Sign(outside) * ramp * maxRate;
    }

    /// <summary>Applies one tick of an auto-scroll. Returns whether it moved.</summary>
    public bool AutoScrollTick(double pointerY, double elapsedMs, double maxRate = 1200, double rampOver = 80)
    {
        var rate = AutoScrollRate(pointerY, maxRate, rampOver);
        return rate != 0 && By(0, rate * elapsedMs / 1000);
    }

    private static double Clean(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0 : v;

    /// <summary>Less than half a device pixel at any normal scale. Below this nothing on screen
    /// moves, and reporting a change would cause a repaint that changes nothing.</summary>
    private static bool Same(double a, double b) => Math.Abs(a - b) < 0.01;
}
