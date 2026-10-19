using System.Diagnostics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using RPM.DataGrid.Model;
using RPM.DataGrid.Paint;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Services.Platform;
using Windows.Foundation;
using Windows.System;

namespace RPM.Desktop.Presentation.Grids;

/// <summary>
/// The pointer and the keyboard.
///
/// <para>One set of handlers on one transparent sheet. Every question they ask is arithmetic
/// (<see cref="GridHitTest"/>). Cell handlers are not used. A cell can be reused for another row
/// between a press and its release, so each gesture would have to work out its row from the cell
/// it hit. Here no cell is hit, so the row always comes from the position.</para>
/// </summary>
public sealed partial class RpmGrid
{
    /// <summary>Two presses closer together than this, on the same spot, are a double-click.</summary>
    private const long DoubleClickMs = 400;

    /// <summary>A press and release that never travelled this far is a click, not a drag.</summary>
    private const double ClickSlop = 3;

    private readonly Stopwatch _clicks = Stopwatch.StartNew();
    private DispatcherTimer? _autoScroll;

    private long _lastClickMs = long.MinValue;
    private Point _lastClickAt;

    private Point _pressedAt;
    private bool _pressed;


    private bool _sweeping;
    private double _sweepPointerY;

    /// <summary>The row whose grip is being held, from the press until the release. -1 for every
    /// press that did not land on a grip, which is most presses.</summary>
    private int _gripRow = -1;

    private double _gripFrom;

    /// <summary>What the drag is carrying and where it may land. Null until the press becomes a
    /// drag.</summary>
    private GridDragBlock? _block;

    /// <summary>The gap the drop is aiming at, as a count of rows above the line.</summary>
    private int _dropSlot = -1;

    /// <summary>The column header being dragged wider, and where the drag started.</summary>
    private GridColumn? _resizing;
    private double _resizeFrom;
    private double _resizeWidth;

    /// <summary>Raised when a row is double-clicked. The host plays it.</summary>
    public event Action<int>? RowActivated;

    /// <summary>Raised when a tick is pressed. The host is the one that knows what a tick means.</summary>
    public event Action<int>? TickPressed;

    /// <summary>Raised when a glyph button is pressed.</summary>
    public event Action<int, string>? ButtonPressed;

    /// <summary>Raised when a tree row's expander is pressed.</summary>
    public event Action<int>? ExpanderPressed;

    /// <summary>Raised when a sortable column header is clicked.</summary>
    public event Action<GridColumn>? HeaderClicked;

    /// <summary>Raised when a column's filter mark is clicked, with where to put the flyout.</summary>
    public event Action<GridColumn, Point>? FilterRequested;

    /// <summary>Raised when a column header is right-clicked, with where to put the menu.</summary>
    public event Action<GridColumn, Point>? HeaderMenuRequested;

    /// <summary>Raised on a right-click, with the row under the pointer (-1 for none) and where.</summary>
    public event Action<int, Point>? ContextRequestedAt;

    /// <summary>Raised when a key the grid does not use is pressed, so the host can act on it.</summary>
    public event Action<VirtualKey, GridModifiers>? KeyPressed;

    /// <summary>
    /// Asked when a press on a grip has travelled far enough to be a drag: which rows is it carrying,
    /// and what range may it land in? Null refuses the drag.
    ///
    /// <para>The host answers, because only the host knows what a run of rows means. The grid handles
    /// the gesture and the drawing.</para>
    /// </summary>
    public Func<int, GridDragBlock?>? RowDragStarting { get; set; }

    /// <summary>Raised when carried rows are dropped in a new place. A drop that moves
    /// nothing does not raise it.</summary>
    public event Action<GridDragBlock, int>? RowsDropped;

    private void AttachInput()
    {
        _input.PointerWheelChanged += OnWheel;
        _input.PointerMoved += OnPointerMoved;
        _input.PointerPressed += OnPointerPressed;
        _input.PointerReleased += OnPointerReleased;
        _input.PointerExited += OnPointerExited;
        _input.PointerCaptureLost += OnPointerCaptureLost;
        _input.KeyDown += OnKeyDown;

        Selection.Changed += Invalidate;
    }

    private void DetachInput()
    {
        _input.PointerWheelChanged -= OnWheel;
        _input.PointerMoved -= OnPointerMoved;
        _input.PointerPressed -= OnPointerPressed;
        _input.PointerReleased -= OnPointerReleased;
        _input.PointerExited -= OnPointerExited;
        _input.PointerCaptureLost -= OnPointerCaptureLost;
        _input.KeyDown -= OnKeyDown;

        Selection.Changed -= Invalidate;

        StopAutoScroll();
    }

    private static GridModifiers Modifiers(VirtualKeyModifiers keys)
    {
        var m = GridModifiers.None;
        if (keys.HasFlag(VirtualKeyModifiers.Control))
            m |= GridModifiers.Control;
        if (keys.HasFlag(VirtualKeyModifiers.Shift))
            m |= GridModifiers.Shift;
        return m;
    }

    private static GridModifiers Held()
    {
        var held = HeldKeys.Now;
        var m = GridModifiers.None;
        if (held.HasFlag(Modifier.Control))
            m |= GridModifiers.Control;
        if (held.HasFlag(Modifier.Shift))
            m |= GridModifiers.Shift;
        return m;
    }

    // ── Wheel ───────────────────────────────────────────────────────

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_frame is not { } frame)
            return;

        var point = e.GetCurrentPoint(_input);
        var properties = point.Properties;

        var sideways = properties.IsHorizontalMouseWheel
            || e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift);

        if (_scroll.Wheel(properties.MouseWheelDelta, sideways, frame.Metrics.RowHeight))
            e.Handled = true;
    }

    // ── Pointer ─────────────────────────────────────────────────────

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_frame is not { } frame)
            return;

        var at = e.GetCurrentPoint(_input).Position;

        if (_resizing is { } column)
        {
            frame.Columns.Resize(column, _resizeWidth + (at.X - _resizeFrom));
            return;
        }

        if (_gripRow >= 0)
        {
            CarryRows(at.Y);
            return;
        }

        if (_sweeping)
        {
            _sweepPointerY = at.Y - frame.RowsTop;
            SweepTo(at);
            return;
        }

        // A press that has travelled far enough is a sweep, not a click.
        if (_pressed && Distance(at, _pressedAt) > frame.Metrics.DragSlop)
        {
            _sweeping = true;
            _sweepPointerY = at.Y - frame.RowsTop;
            StartAutoScroll();
            SweepTo(at);
            return;
        }

        Hover(GridHitTest.At(frame, at.X, at.Y), at);
    }

    private void Hover(GridHit hit, Point at)
    {
        if (_frame is not { } frame)
            return;

        var row = hit.Row;
        var column = hit.Column;
        var header = hit.Part is GridPart.Header or GridPart.HeaderEdge ? hit.Column : -1;

        if (hit.Part is GridPart.Header or GridPart.HeaderEdge or GridPart.Nothing && hit.Row < 0)
            row = -1;

        SetCursor(hit.Part == GridPart.HeaderEdge
            ? InputSystemCursorShape.SizeWestEast
            : InputSystemCursorShape.Arrow);

        if (frame.HoverRow == row && frame.HoverColumn == column && frame.HoverHeader == header)
            return;

        frame.HoverRow = row;
        frame.HoverColumn = column;
        frame.HoverHeader = header;
        Invalidate();

        _ = at;
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_frame is not { } frame || _sweeping)
            return;

        if (frame.HoverRow < 0 && frame.HoverHeader < 0)
            return;

        frame.HoverRow = -1;
        frame.HoverColumn = -1;
        frame.HoverHeader = -1;
        Invalidate();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_frame is not { } frame)
            return;

        _input.Focus(FocusState.Pointer);

        var point = e.GetCurrentPoint(_input);
        var at = point.Position;
        var hit = GridHitTest.At(frame, at.X, at.Y);

        if (point.Properties.IsRightButtonPressed)
        {
            if (hit.Part is GridPart.Header or GridPart.HeaderEdge or GridPart.HeaderFilter)
            {
                HeaderMenuRequested?.Invoke(frame.Columns.Visible[hit.Column], at);
                e.Handled = true;
                return;
            }

            // A right-click on a row that is not in the selection moves the selection to it, the way
            // every list does; on one that is, the selection stands, so a menu opened over a run of
            // rows acts on the run.
            if (hit.OnRow && !Selection.Contains(hit.Row))
                Selection.Click(hit.Row, GridModifiers.None, RowCount);

            ContextRequestedAt?.Invoke(hit.Row, at);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
            return;

        e.Handled = true;

        switch (hit.Part)
        {
            case GridPart.HeaderEdge:
                _resizing = frame.Columns.Visible[hit.Column];
                _resizeFrom = at.X;
                _resizeWidth = _resizing.Width;
                _input.CapturePointer(e.Pointer);
                return;

            case GridPart.Header:
            case GridPart.HeaderFilter:
                // Acted on at release, so a press that turns into a resize or a reorder does not
                // also sort.
                _pressed = true;
                _pressedAt = at;
                return;

            case GridPart.Tick:
                TickPressed?.Invoke(hit.Row);
                return;

            case GridPart.Button:
                frame.PressedRow = hit.Row;
                frame.PressedColumn = hit.Column;
                Invalidate();
                return;

            case GridPart.Expander:
                ExpanderPressed?.Invoke(hit.Row);
                return;

            case GridPart.Grip:
                // Held, not carried: until it travels, this is a press on a handle and nothing more.
                // The selection is deliberately not touched: the rows about to move are the ones
                // already chosen, and a press that reset them would carry the wrong ones.
                _gripRow = hit.Row;
                _gripFrom = at.Y;
                _input.CapturePointer(e.Pointer);
                return;

            case GridPart.Nothing:
                return;
        }

        if (IsDoubleClick(at))
        {
            RowActivated?.Invoke(hit.Row);
            return;
        }

        // A press and a drag on a row sweeps the selection. It never moves rows. Moving rows is done
        // with the grip, because one gesture cannot do both.
        Selection.BeginSweep(hit.Row, Modifiers(e.KeyModifiers), RowCount);

        _pressed = true;
        _pressedAt = at;
        _input.CapturePointer(e.Pointer);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_frame is not { } frame)
            return;

        var at = e.GetCurrentPoint(_input).Position;
        var wasPressed = _pressed;
        var wasSweeping = _sweeping;

        EndGesture(e.Pointer);

        if (frame.PressedRow >= 0)
        {
            var hit = GridHitTest.At(frame, at.X, at.Y);
            if (hit.Part == GridPart.Button && hit.Row == frame.PressedRow && hit.Column == frame.PressedColumn)
                ButtonPressed?.Invoke(hit.Row, frame.Columns.Visible[hit.Column].Id);

            frame.PressedRow = -1;
            frame.PressedColumn = -1;
            Invalidate();
            return;
        }

        if (!wasPressed || wasSweeping)
            return;

        // A header press that never became a resize or a drag is a sort.
        var released = GridHitTest.At(frame, at.X, at.Y);
        if (Distance(at, _pressedAt) > ClickSlop)
            return;

        if (released.Part == GridPart.HeaderFilter)
        {
            FilterRequested?.Invoke(frame.Columns.Visible[released.Column], at);
        }
        else if (released.Part == GridPart.Header)
        {
            var column = frame.Columns.Visible[released.Column];
            if (column.CanSort)
                HeaderClicked?.Invoke(column);
        }
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndGesture(e.Pointer);

    private void EndGesture(Pointer? pointer)
    {
        if (pointer is not null)
            _input.ReleasePointerCapture(pointer);

        DropRows();

        _pressed = false;
        _sweeping = false;
        _resizing = null;

        Selection.EndSweep();
        StopAutoScroll();
    }

    /// <summary>
    /// The pointer has moved while a grip is held. Below the slop it is still a press; past it the
    /// rows leave their places and follow, and the line says where they would land.
    /// </summary>
    private void CarryRows(double y)
    {
        if (_frame is not { } frame)
            return;

        if (_block is null)
        {
            if (Math.Abs(y - _gripFrom) < frame.Metrics.DragSlop)
                return;

            _block = RowDragStarting?.Invoke(_gripRow);
            if (_block is null)
            {
                // The host will not move these rows. Let go rather than holding a gesture that can
                // never end in anything.
                _gripRow = -1;
                return;
            }

            frame.Carried = _block.Carried;
        }

        frame.CarryOffsetY = y - _gripFrom;

        _dropSlot = GridHitTest.DropAt(frame, y, _block.First, _block.Last + 1);
        frame.Drop = new GridDrop(_dropSlot);

        Invalidate();
    }

    /// <summary>The rows are let go. A press that never travelled is not a drop at all.</summary>
    private void DropRows()
    {
        var block = _block;
        var slot = _dropSlot;

        _gripRow = -1;
        _block = null;
        _dropSlot = -1;

        if (_frame is { } frame)
        {
            frame.Carried = null;
            frame.CarryOffsetY = 0;
            frame.Drop = null;
            Invalidate();
        }

        // A drop that puts the rows back where they were does nothing. A reorder makes the host re-read
        // the list, and that would lose the reader's place.
        if (block is not null && slot >= 0 && block.Moves(slot))
            RowsDropped?.Invoke(block, slot);
    }

    private void SweepTo(Point at)
    {
        if (_frame is not { } frame)
            return;

        var listY = at.Y - frame.RowsTop + _scroll.Y;
        var row = Math.Clamp((int)(listY / frame.Rows.RowHeight), 0, Math.Max(0, RowCount - 1));

        Selection.SweepTo(row, RowCount);
    }

    /// <summary>
    /// While a sweep runs, the list keeps scrolling while the pointer is outside it, even when the
    /// pointer does not move. A timer is used rather than a move handler, because a pointer held
    /// still past the edge still means keep going.
    /// </summary>
    private void StartAutoScroll()
    {
        if (_autoScroll is not null)
            return;

        _autoScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _autoScroll.Tick += (_, _) =>
        {
            if (!_sweeping)
            {
                StopAutoScroll();
                return;
            }

            if (_scroll.AutoScrollTick(_sweepPointerY, 16))
                SweepToPointer();
        };

        _autoScroll.Start();
    }

    private void SweepToPointer()
    {
        if (_frame is not { } frame)
            return;

        var listY = _sweepPointerY + _scroll.Y;
        var row = Math.Clamp((int)(listY / frame.Rows.RowHeight), 0, Math.Max(0, RowCount - 1));
        Selection.SweepTo(row, RowCount);
    }

    private void StopAutoScroll()
    {
        _autoScroll?.Stop();
        _autoScroll = null;
    }

    /// <summary>
    /// Two presses close together in time and place.
    ///
    /// <para>This is counted by hand, not with <c>DoubleTapped</c>, which is unreliable under Uno Skia.
    /// <c>WaveformEditor</c> counts its own clicks for the same reason.</para>
    /// </summary>
    private bool IsDoubleClick(Point at)
    {
        var now = _clicks.ElapsedMilliseconds;
        var quick = now - _lastClickMs <= DoubleClickMs && Distance(at, _lastClickAt) <= ClickSlop;

        _lastClickMs = quick ? long.MinValue : now;   // a third click starts again rather than firing twice
        _lastClickAt = at;

        return quick;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt(
        (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private InputSystemCursorShape _cursor = InputSystemCursorShape.Arrow;

    private void SetCursor(InputSystemCursorShape shape)
    {
        if (shape == _cursor)
            return;

        _cursor = shape;
        _input.SetCursor(shape);
    }

    // ── Keyboard ────────────────────────────────────────────────────

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_frame is not { } frame)
            return;

        var count = RowCount;
        if (count == 0)
            return;

        var modifiers = Held();
        var focus = Selection.Focus < 0 ? 0 : Selection.Focus;
        var page = Math.Max(1, (int)(frame.RowsHeight / frame.Rows.RowHeight) - 1);

        int? to = e.Key switch
        {
            VirtualKey.Up => focus - 1,
            VirtualKey.Down => focus + 1,
            VirtualKey.PageUp => focus - page,
            VirtualKey.PageDown => focus + page,
            VirtualKey.Home => 0,
            VirtualKey.End => count - 1,
            _ => null,
        };

        if (to is { } row)
        {
            Selection.KeyTo(row, modifiers, count);
            ShowRow(Selection.Focus);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.A when modifiers.HasFlag(GridModifiers.Control):
                Selection.SelectAll(count);
                e.Handled = true;
                return;

            case VirtualKey.Space when modifiers.HasFlag(GridModifiers.Control):
                Selection.ToggleFocus(count);
                e.Handled = true;
                return;

            case VirtualKey.Enter:
                if (Selection.Focus >= 0)
                    RowActivated?.Invoke(Selection.Focus);
                e.Handled = true;
                return;
        }

        KeyPressed?.Invoke(e.Key, modifiers);
    }
}

/// <summary>
/// The transparent sheet that takes the pointer. A canvas is not a hit-test target on either head, so
/// a transparent sheet is needed to receive pointer input. <c>WaveformEditor</c> and the MixLab
/// canvases do the same.
///
/// <para>A subclass, not a plain border, because <c>ProtectedCursor</c> is protected and cannot be set
/// from outside. The base is <see cref="BorderBase"/>, not <c>Border</c>. Border is sealed on the
/// WinUI head and is not sealed on the Skia head.</para>
/// </summary>
internal sealed partial class GridInputLayer : BorderBase
{
    private InputSystemCursorShape _shape = InputSystemCursorShape.Arrow;

    public GridInputLayer()
    {
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        IsTabStop = true;
        UseSystemFocusVisuals = false;
    }

    public void SetCursor(InputSystemCursorShape shape)
    {
        if (shape == _shape)
            return;

        _shape = shape;
        ProtectedCursor = InputSystemCursor.Create(shape);
    }
}
