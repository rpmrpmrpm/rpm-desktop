using RPM.Core.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Shell;

/// <summary>A row that a reorder can pick up and carry. The offset is on the ROW and not on its container.
/// See <see cref="CarriedReorder{T}"/> for what goes wrong when the container is moved instead.</summary>
public interface ICarriedRow
{
    /// <summary>The row's offset from the line the list placed it on. It is bound in the row's container
    /// template, so the offset still applies if that container is reused for another row.</summary>
    TranslateTransform Drop { get; }
}

/// <summary>
/// Drag to reorder a virtualized list. The phone queue uses the same approach
/// (<c>RPM.Mobile/Presentation/PlaylistPage.xaml.cs</c>), and so does the Pegasus drawer.
///
/// <para>
/// The row picked up stays under the pointer, and the running order re-forms around it one place at a
/// time. So the list on the screen during the drag is the list that will be there on release. The
/// collection itself is told once, when the row has landed. A Move is a structural change. Doing one for
/// each boundary crossed would make the list jump under the pointer.
/// </para>
///
/// <para><b>Two rules must hold.</b> First, the offset is stored on the ROW, never on the container. A
/// list recycles containers, so an offset written onto a container stays with it after the row it was
/// meant for has gone. For example, when the list scrolls at an edge: the container holding the carried row
/// is taken away and given to another track, still shifted, and it is then drawn on top of its
/// neighbour. Second, the rows' FACES change places, not their positions in the collection (<see
/// cref="_exchange"/>). So the index of the carried track follows the pointer, and virtualization does
/// not recycle that track.</para>
///
/// <para>The arithmetic is in <see cref="QueueGesture"/>. It is shared with the phone and has tests.</para>
/// </summary>
public sealed class CarriedReorder<T> where T : class, ICarriedRow
{
    private readonly ListView _list;
    private readonly IList<T> _rows;
    private readonly Action<int, int> _exchange;
    private readonly Action<int, int> _commit;
    private readonly Func<T, bool>? _canCarry;
    private readonly Action<int>? _hover;

    /// <param name="exchange">Swaps the faces at the two slots. Nothing structural changes: both rows keep
    /// their places, and what each one shows changes.</param>
    /// <param name="commit">Where the carried row started and where it has landed. Called once, and
    /// only where the two differ.</param>
    /// <param name="canCarry">Whether a row may be picked up at all, for lists where some rows are
    /// furniture rather than entries.</param>
    /// <param name="hover">The slot the pointer is over, whenever it changes, for a list that wants to
    /// mark what is under it. Called with -1 when nothing is.</param>
    public CarriedReorder(ListView list, IList<T> rows,
                          Action<int, int> exchange, Action<int, int> commit,
                          Func<T, bool>? canCarry = null, Action<int>? hover = null)
    {
        _list = list;
        _rows = rows;
        _exchange = exchange;
        _commit = commit;
        _canCarry = canCarry;
        _hover = hover;

        // handledEventsToo is true because a ListViewItem marks its press as handled before the event
        // reaches the list. A handler without this flag would never be called.
        _list.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPressed), true);
        _list.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnMoved), true);
        _list.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnReleased), true);
        _list.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnCaptureLost), true);
    }

    /// <summary>Whether a row is being carried or is still landing. Hosts check this and leave the list
    /// alone while it is true. A list rebuilt under a carried row would leave that row nowhere to
    /// land.</summary>
    public bool IsBusy => _dragging || _dropping;

    /// <summary>How far the pointer must move before a press becomes a drag. Less than this is a press,
    /// and a press selects or plays. The two must be told apart from the same gesture.</summary>
    private const double Slop = 5.0;

    /// <summary>How long the landing takes, and how quickly a displaced row steps aside.</summary>
    private const double DropSeconds = 0.18;
    private const double StepRate = 16.0;

    /// <summary>How close to an end the carried row must get before the list scrolls under it.
    /// <c>MaxScrollStep</c> is the speed at the very edge, in points per frame at 60 frames per second. The
    /// step is scaled by the real frame time.</summary>
    private const double EdgeZone = 64;
    private const double MaxScrollStep = 13;

    private bool _dragging;
    private bool _dropping;

    private int _start = -1;
    private int _at = -1;
    private int _hovered = -1;

    private double _startY;      // Where the press went down, in the list's own content space
    private double _pointerViewY;
    private double _rowHeight;

    private double _dropFrom;
    private double _dropAt;

    private long _lastTick;

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging || _dropping)
            return;

        var row = RowUnder(e.OriginalSource as DependencyObject);
        if (row is null)
            return;

        var at = _rows.IndexOf(row);
        if (at < 0 || (_canCarry is not null && !_canCarry(row)))
            return;

        _start = _at = at;
        _pointerViewY = e.GetCurrentPoint(_list).Position.Y;
        _startY = _pointerViewY + Offset;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_start < 0 || _dropping)
            return;

        _pointerViewY = e.GetCurrentPoint(_list).Position.Y;

        if (_dragging)
            return;

        // Still within Slop. This may still be a press, so nothing is changed yet.
        if (Math.Abs(_pointerViewY + Offset - _startY) < Slop)
            return;

        // Every row has the same height in every list this drives, so a row's position is simple
        // arithmetic. The height is read from a realized container. A list whose rows are not yet
        // arranged gives no height, so nothing can be worked out from it.
        _rowHeight = RowHeight();
        if (_rowHeight <= 0)
        {
            _start = _at = -1;
            return;
        }

        _dragging = true;
        _list.CapturePointer(e.Pointer);

        _lastTick = Environment.TickCount64;
        CompositionTarget.Rendering += OnFrame;
        Services.Shell.FrameWatch.Began("carried-reorder");
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e) => Land();

    private void OnCaptureLost(object sender, PointerRoutedEventArgs e) => Land();

    /// <summary>
    /// One frame of a drag. It moves the carried row under the pointer, re-forms the order around it, and
    /// scrolls the list if the row is held at an end. It runs every frame, not on each pointer event. The
    /// list must keep moving while the pointer is still at an edge, and a still pointer sends no events.
    /// </summary>
    private void OnFrame(object? sender, object e)
    {
        var now = Environment.TickCount64;
        var dt = Math.Min((now - _lastTick) / 1000.0, 0.1);
        _lastTick = now;
        if (dt <= 0)
            return;

        Settle(dt);

        if (_dropping)
        {
            TickDrop(dt);
            return;
        }

        if (!_dragging || _at < 0 || _at >= _rows.Count)
            return;

        // The pointer's position in the CONTENT: its position over the viewport, plus how far the list has
        // scrolled. It is recalculated every frame, not taken from an event. So a pointer held still at an
        // edge keeps moving while the list scrolls under it.
        var travel = _pointerViewY + Offset - _startY;

        // How far the carried row is held from the slot it occupies. This is a loop, not an if, because
        // a list scrolling at an end can cross several boundaries between two frames.
        var y = travel - ((_at - _start) * _rowHeight);

        while (QueueGesture.Step(y, _rowHeight) is var step && step != 0)
        {
            var to = _at + step;
            if (to < 0 || to >= _rows.Count)
                break;

            Carry(to);
            y -= step * _rowHeight;
        }

        _rows[_at].Drop.Y = y;

        Hovered(travel);
        RunListOn(y, dt);
    }

    /// <summary>
    /// Hands the carried face to the next slot along and takes whatever was there.
    ///
    /// <para>The rows do not move: their faces change places. The displaced row is put back where it was
    /// last seen, a row's height away, in the direction it came from. It is then left to slide home from
    /// there. A row stepping aside is easy to follow. A row that jumped into the space the carried
    /// track just left would not be.</para>
    /// </summary>
    private void Carry(int to)
    {
        var from = _at;

        _exchange(from, to);

        // The displaced row now sits at `from`. It is drawn from where it was.
        _rows[from].Drop.Y = (to - from) * _rowHeight;

        // The carried face is now at `to`. Its own offset is written by the frame that called this.
        _rows[to].Drop.Y = 0;

        _at = to;
    }

    /// <summary>Moves every row that is not carried back to its own line, with easing on each
    /// frame.</summary>
    private void Settle(double dt)
    {
        var ease = 1.0 - Math.Exp(-dt * StepRate);

        for (var i = 0; i < _rows.Count; i++)
        {
            if (i == _at)
                continue;

            var drop = _rows[i].Drop;
            if (Math.Abs(drop.Y) < 0.01)
            {
                drop.Y = 0;
                continue;
            }

            drop.Y -= drop.Y * ease;
        }
    }

    /// <summary>Which slot the pointer is over. This changes when the pointer moves. It is not the slot
    /// the carried row holds. A row dropped ONTO another row is over one slot while it holds the
    /// other.</summary>
    private void Hovered(double travel)
    {
        if (_hover is null)
            return;

        var over = (int)Math.Floor((_startY + travel) / _rowHeight);
        if (over < 0 || over >= _rows.Count)
            over = -1;

        if (over == _hovered)
            return;

        _hovered = over;
        _hover(over);
    }

    /// <summary>Scrolls the list when the carried row is held near an end. The closer it gets, the
    /// faster it scrolls, like dragging a file to the edge of a window. Without this, a reorder can
    /// only reach the rows already on screen, which makes long lists hard to reorder.
    /// </summary>
    private void RunListOn(double y, double dt)
    {
        if (Scroller is not { } scroller || scroller.ScrollableHeight <= 0)
            return;

        var top = (_at * _rowHeight) + y - scroller.VerticalOffset;

        // Scaled by the real frame time. If the scroll speed were fixed, a slow frame would make the list
        // fall behind the pointer.
        var step = QueueGesture.EdgeStep(top, _rowHeight, scroller.ViewportHeight,
                                         EdgeZone, MaxScrollStep * dt * 60);
        if (step == 0)
            return;

        var to = Math.Clamp(scroller.VerticalOffset + step, 0, scroller.ScrollableHeight);
        if (Math.Abs(to - scroller.VerticalOffset) < 0.5)
            return;

        scroller.ChangeView(null, to, null, disableAnimation: true);
    }

    /// <summary>Puts the carried row down. It falls into the slot it holds, and the collection is told when
    /// it gets there.</summary>
    private void Land()
    {
        if (!_dragging)
        {
            _start = _at = -1;
            return;
        }

        _dragging = false;
        _dropping = true;
        _dropFrom = _at >= 0 && _at < _rows.Count ? _rows[_at].Drop.Y : 0;
        _dropAt = 0;

        _list.ReleasePointerCaptures();

        if (_hovered >= 0)
        {
            _hovered = -1;
            _hover?.Invoke(-1);
        }
    }

    /// <summary>One frame of the landing. The landing takes a fixed time, not an eased approach, because
    /// something waits for the end of it. An eased approach closes on its target by a fraction each frame,
    /// so it would report itself landed long after the row had visibly stopped.</summary>
    private void TickDrop(double dt)
    {
        _dropAt = Math.Min(_dropAt + dt, DropSeconds);
        var t = _dropAt / DropSeconds;

        if (_at >= 0 && _at < _rows.Count)
            _rows[_at].Drop.Y = _dropFrom * (1 - (t * t * (3 - (2 * t))));

        if (_dropAt < DropSeconds)
            return;

        var from = _start;
        var to = _at;

        Stop();

        if (from != to && from >= 0 && to >= 0)
            _commit(from, to);
    }

    /// <summary>Stops any drag or landing in progress and puts every row back on its own line. A host calls
    /// this when its list has been rebuilt, because the rows a drag was holding no longer exist.</summary>
    public void Cancel()
    {
        Stop();

        foreach (var row in _rows)
            row.Drop.Y = 0;
    }

    private void Stop()
    {
        if (_dragging || _dropping)
            CompositionTarget.Rendering -= OnFrame;
        Services.Shell.FrameWatch.Ended("carried-reorder");

        _dragging = false;
        _dropping = false;
        _start = _at = -1;
        _hovered = -1;

        _list.ReleasePointerCaptures();
    }

    // Where things are

    private double Offset => Scroller?.VerticalOffset ?? 0;

    private ScrollViewer? _scroller;

    private ScrollViewer? Scroller => _scroller ??= FindScroller(_list);

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer found)
                return found;
            if (FindScroller(child) is { } nested)
                return nested;
        }

        return null;
    }

    /// <summary>The height of a row, taken from whichever container the list has realized. All rows
    /// have one height in every list this drives. That is what makes the rest of this class simple
    /// arithmetic.</summary>
    private double RowHeight()
    {
        for (var i = 0; i < _rows.Count; i++)
            if (_list.ContainerFromIndex(i) is FrameworkElement item && item.ActualHeight > 0)
                return item.ActualHeight;

        return 0;
    }

    /// <summary>The row the pointer went down on. It is found by walking up from the element that was
    /// hit, to the first element bound to a row.</summary>
    private T? RowUnder(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: T row })
                return row;

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }
}
