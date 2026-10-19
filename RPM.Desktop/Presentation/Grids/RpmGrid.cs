using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using RPM.DataGrid.Model;
using RPM.DataGrid.Paint;
using SkiaSharp;
using Windows.Foundation;

namespace RPM.Desktop.Presentation.Grids;

/// <summary>
/// The data grid for the library table and the import queue. It draws every cell itself, instead of
/// using <c>WinUI.TableView</c>.
///
/// <para>Four elements in the visual tree: the Skia surface, a transparent sheet that takes the
/// pointer, and two scrollbars. There is no <c>ListView</c>, no <c>ScrollViewer</c>, no
/// <c>ItemsRepeater</c> and no container per row. Without row containers nothing is recycled, so a
/// row cannot be left showing the data of another row. A row is an index. A repaint reads the
/// model.</para>
///
/// <para>Derives from <see cref="Microsoft.UI.Xaml.Controls.Grid"/> and builds its children in the
/// constructor, the same way <c>WaveformEditor</c> and the MixLab canvases do. A templated
/// <c>Control</c> would put the parts in a dictionary and then need to find them again.</para>
/// </summary>
public sealed partial class RpmGrid : Microsoft.UI.Xaml.Controls.Grid
{
    private readonly Common.SkiaSurface _surface;
    private readonly GridInputLayer _input = new();
    private readonly ScrollBar _vertical = new() { Orientation = Orientation.Vertical };
    private readonly ScrollBar _horizontal = new() { Orientation = Orientation.Horizontal };

    private readonly RpmGridPainter _painter;
    private readonly ScrollModel _scroll = new();

    private GridFrame? _frame;
    private GridColumns? _columns;
    private IGridSource? _source;

    /// <summary>Whether a repaint has been asked for since the last one was drawn.</summary>
    private bool _dirty;

    /// <summary>Set while the grid is writing the scrollbars, so their own events do not come back
    /// as a user scrolling.</summary>
    private bool _writingBars;

    /// <summary>Runs only while the last paint drew something that moves. See OnBeat.</summary>
    private DispatcherTimer? _beat;

    public RpmGrid()
    {
        var fonts = GridFonts.Shared;
        _painter = new RpmGridPainter(fonts.Shop) { IconFace = fonts.Icon };

        _surface = new Common.SkiaSurface { Draw = Render };

        // A scrollbar outside a ScrollViewer is never told to show itself. A ScrollViewer sets this
        // mode when the pointer is over its content. Without it the bars are laid out and work, but
        // they are never drawn.
        _vertical.IndicatorMode = ScrollingIndicatorMode.MouseIndicator;
        _horizontal.IndicatorMode = ScrollingIndicatorMode.MouseIndicator;

        _vertical.HorizontalAlignment = HorizontalAlignment.Right;
        _vertical.VerticalAlignment = VerticalAlignment.Stretch;
        _vertical.Visibility = Visibility.Collapsed;
        _vertical.Scroll += OnBarScroll;

        _horizontal.HorizontalAlignment = HorizontalAlignment.Stretch;
        _horizontal.VerticalAlignment = VerticalAlignment.Bottom;
        _horizontal.Visibility = Visibility.Collapsed;
        _horizontal.Scroll += OnBarScroll;

        Children.Add(_surface);
        Children.Add(_input);
        Children.Add(_horizontal);
        Children.Add(_vertical);

        _scroll.Scrolled += OnScrolled;
        SizeChanged += (_, _) => Remeasure();

        AttachInput();
    }

    /// <summary>How tall every row is, and every other size the grid draws at.</summary>
    public GridMetrics Metrics
    {
        get => _frame?.Metrics ?? new GridMetrics();
        set
        {
            if (_frame is not { } frame)
                return;

            frame.Metrics = value;
            frame.Rows = new RowStrip(value.RowHeight);
            Remeasure();
        }
    }

    /// <summary>The columns. Set once, then changed in place; the grid listens for its own repaints.</summary>
    public GridColumns? Columns
    {
        get => _columns;
        set
        {
            if (ReferenceEquals(_columns, value))
                return;

            if (_columns is not null)
                _columns.Changed -= OnColumnsChanged;

            _columns = value;

            if (_columns is not null)
                _columns.Changed += OnColumnsChanged;

            Rebuild();
        }
    }

    /// <summary>The rows.</summary>
    public IGridSource? Source
    {
        get => _source;
        set
        {
            if (ReferenceEquals(_source, value))
                return;

            Unhook(_source);
            _source = value;
            Hook(_source);

            if (_frame is not null && value is not null)
                _frame.Source = value;

            Selection.Clear();
            _scroll.MoveTo(0, 0);
            Remeasure();
        }
    }

    /// <summary>Which rows are selected. Lives as long as the grid.</summary>
    public GridSelection Selection { get; } = new();

    /// <summary>
    /// Where an art column's pictures come from. Set before the columns are; see
    /// <see cref="GridFrame.Art"/> for what it may and may not do.
    /// </summary>
    public Func<string, SkiaSharp.SKImage?>? Art
    {
        get => _frame?.Art ?? _art;
        set
        {
            _art = value;
            if (_frame is not null)
                _frame.Art = value;
        }
    }

    private Func<string, SkiaSharp.SKImage?>? _art;

    public Func<int, object?, bool>? Washed
    {
        get => _frame?.Washed;
        set
        {
            _washed = value;
            if (_frame is not null)
                _frame.Washed = value;
            Invalidate();
        }
    }

    private Func<int, object?, bool>? _washed;

    /// <summary>Whether the column header band is drawn.</summary>
    public bool ShowHeader
    {
        get => _frame?.ShowHeader ?? true;
        set
        {
            if (_frame is not { } frame || frame.ShowHeader == value)
                return;

            frame.ShowHeader = value;
            Remeasure();
        }
    }

    /// <summary>The column the list is sorted by, and which way. Set by the host: this grid does not
    /// sort anything itself, it says what the sort is.</summary>
    public void ShowSort(string? columnId, bool descending)
    {
        if (_frame is not { } frame)
            return;

        frame.SortColumn = columnId;
        frame.SortDescending = descending;
        Invalidate();
    }

    /// <summary>Which columns have a filter on them, so their headers can say so.</summary>
    public void ShowFilters(IReadOnlyCollection<string>? columns)
    {
        if (_frame is not { } frame)
            return;

        frame.FilteredColumns = columns;
        Invalidate();
    }

    /// <summary>How many rows there are. Zero when there is no source.</summary>
    public int RowCount => _source?.Count ?? 0;

    /// <summary>Asks for a repaint. Idempotent within a frame, which is why nothing upstream
    /// coalesces its own notifications.</summary>
    public void Invalidate()
    {
        if (_dirty)
            return;

        _dirty = true;
        _surface.Redraw();
    }

    /// <summary>Scrolls down by an amount, clamped. What a host uses to keep a row pinned to a screen
    /// line while something above the grid changes height.</summary>
    public void ScrollBy(double delta) => _scroll.By(0, delta);

    /// <summary>Brings a row into view.</summary>
    public void ShowRow(int row)
    {
        if (_frame is not { } frame)
            return;

        _scroll.ShowRow(row, RowCount, frame.Rows);
    }

    /// <summary>Re-reads the app's colours. Called when the theme changes.</summary>
    public void RefreshTheme()
    {
        if (_frame is not { } frame)
            return;

        frame.Palette = GridTheme.Read();
        Invalidate();
    }

    private void Rebuild()
    {
        if (_columns is not { } columns)
        {
            _frame = null;
            return;
        }

        var was = _frame;

        _frame = new GridFrame
        {
            Columns = columns,
            Source = _source ?? EmptySource.Instance,
            Selection = Selection,
            Palette = GridTheme.Read(),

            // The ground is this element's XAML Background, set where the grid is declared. Not the
            // painter's: a translucent ground drawn on the canvas blends with the pixels already there
            // instead of replacing them, so the previous frame would show through.
            PaintsOwnGround = false,
            Metrics = was?.Metrics ?? new GridMetrics(),
            ShowHeader = was?.ShowHeader ?? true,
        };

        _frame.Rows = new RowStrip(_frame.Metrics.RowHeight);
        _frame.Washed = _washed;
        _frame.Art = _art;
        Remeasure();
    }

    private void OnColumnsChanged() => Remeasure();

    private void Hook(IGridSource? source)
    {
        if (source is null)
            return;

        source.Reset += OnSourceReset;
        source.Landed += OnSourceLanded;
        source.Inserted += OnSourceResized;
        source.Removed += OnSourceResized;
    }

    private void Unhook(IGridSource? source)
    {
        if (source is null)
            return;

        source.Reset -= OnSourceReset;
        source.Landed -= OnSourceLanded;
        source.Inserted -= OnSourceResized;
        source.Removed -= OnSourceResized;
    }

    /// <summary>
    /// The list is a different list. Indices mean nothing across this, so the selection goes and the
    /// grid starts at the top rather than keeping an offset into a list that has been replaced.
    /// </summary>
    private void OnSourceReset()
    {
        Selection.Clear();
        _scroll.MoveTo(_scroll.X, 0);
        Remeasure();
    }

    /// <summary>
    /// A page of rows arrived. Repaint only if some of it is on screen. A page for rows that were
    /// scrolled away from before it arrived would not be seen.
    /// </summary>
    private void OnSourceLanded(int first, int end)
    {
        if (_frame is not { } frame)
            return;

        var (visibleFirst, visibleEnd) = frame.Rows.RangeAt(_scroll.Y, frame.RowsHeight, RowCount, overscan: 1);
        if (end > visibleFirst && first < visibleEnd)
            Invalidate();
    }

    /// <summary>Rows appeared or went away: a group expanded or collapsed.</summary>
    private void OnSourceResized(int first, int end)
    {
        _ = first;
        _ = end;
        Selection.Resize(RowCount);
        Remeasure();
    }

    private void OnScrolled()
    {
        WriteBars();
        Invalidate();
    }

    private void OnBarScroll(object sender, ScrollEventArgs e)
    {
        if (_writingBars)
            return;

        _scroll.MoveTo(
            sender == _horizontal ? e.NewValue : _scroll.X,
            sender == _vertical ? e.NewValue : _scroll.Y);
    }

    /// <summary>
    /// Says how big everything is, and repaints. Called whenever the answer could have changed: the
    /// control resized, a column moved, the source grew, the row height changed.
    /// </summary>
    private void Remeasure()
    {
        if (_frame is not { } frame)
            return;

        frame.Width = ActualWidth;
        frame.Height = ActualHeight;
        frame.ScrollX = _scroll.X;
        frame.ScrollY = _scroll.Y;

        _scroll.Measure(
            frame.Columns.TotalWidth,
            frame.Rows.TotalHeight(RowCount),
            ActualWidth,
            frame.RowsHeight);

        WriteBars();
        Invalidate();
    }

    private void WriteBars()
    {
        _writingBars = true;

        try
        {
            _vertical.Minimum = 0;
            _vertical.Maximum = _scroll.MaxY;
            _vertical.ViewportSize = _scroll.ViewportHeight;
            _vertical.Value = _scroll.Y;
            _vertical.Visibility = _scroll.MaxY > 0.5 ? Visibility.Visible : Visibility.Collapsed;
            _vertical.Margin = new Thickness(0, ShowHeader ? Metrics.HeaderHeight : 0, 0, 0);

            _horizontal.Minimum = 0;
            _horizontal.Maximum = _scroll.MaxX;
            _horizontal.ViewportSize = _scroll.ViewportWidth;
            _horizontal.Value = _scroll.X;
            _horizontal.Visibility = _scroll.MaxX > 0.5 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _writingBars = false;
        }
    }

    /// <summary>
    /// One frame.
    ///
    /// <para>The offsets and the size are read here, not pushed to the painter when they change. The
    /// frame shows the values they have when it is drawn, so the painter never holds values that the
    /// grid has already changed.</para>
    /// </summary>
    private void Render(SKCanvas canvas, Size area)
    {
        _dirty = false;

        if (_frame is not { } frame)
            return;

        frame.Width = area.Width;
        frame.Height = area.Height;
        frame.ScrollX = _scroll.X;
        frame.ScrollY = _scroll.Y;
        frame.Source = _source ?? EmptySource.Instance;
        frame.BarsClock = Controls.PlayingBars.BeatSeconds;

        // Announce the visible range before drawing, so a source that loads data can fetch the whole
        // range at once instead of one row at a time from Row().
        var (first, end) = frame.Rows.RangeAt(frame.ScrollY, frame.RowsHeight, RowCount, overscan: 1);
        _source?.Prefetch(first, end);

        _painter.Paint(canvas, frame);

        // Whether anything on screen is still moving is known only once the frame is drawn, so it is
        // read from the paint. A mark that scrolls off stops the timer on the next frame. A mark that
        // scrolls on starts it.
        if (_painter.DrewAnimation)
            StartBeat();
        else
            StopBeat();
    }

    private void StartBeat()
    {
        if (_beat is not null)
            return;

        Controls.PlayingBars.EnsureClock();

        _beat = new DispatcherTimer { Interval = Controls.PlayingBars.BeatInterval };
        _beat.Tick += OnBeat;
        _beat.Start();
    }

    private void OnBeat(object? sender, object e) => Invalidate();

    private void StopBeat()
    {
        if (_beat is null)
            return;

        _beat.Stop();
        _beat.Tick -= OnBeat;
        _beat = null;
    }

    /// <summary>
    /// Lets go of the source, the columns and the pointer.
    ///
    /// <para>Not called Dispose: a FrameworkElement already has one, and a grid that has been taken
    /// off the page can be put back on it. This says the grid is not listening, not that it is
    /// finished.</para>
    /// </summary>
    public void Detach()
    {
        Unhook(_source);

        if (_columns is not null)
            _columns.Changed -= OnColumnsChanged;

        DetachInput();
        StopBeat();
        _painter.Dispose();
    }

    /// <summary>A source with nothing in it, so the frame never has to hold null.</summary>
    private sealed class EmptySource : IGridSource
    {
        public static readonly EmptySource Instance = new();

        public int Count => 0;

        public object? Row(int index) => null;

        public object? Peek(int index) => null;

        public void Prefetch(int first, int end)
        {
        }

#pragma warning disable CS0067 // Nothing ever changes, so nothing is ever raised.
        public event Action? Reset;
        public event Action<int, int>? Landed;
        public event Action<int, int>? Inserted;
        public event Action<int, int>? Removed;
#pragma warning restore CS0067
    }
}
