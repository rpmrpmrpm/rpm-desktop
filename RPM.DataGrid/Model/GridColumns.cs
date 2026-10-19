namespace RPM.DataGrid.Model;

/// <summary>Where a column sits in the strip.</summary>
/// <param name="Index">Its index among the visible columns.</param>
/// <param name="X">Its left edge, measured from the start of the strip.</param>
/// <param name="Width">Its width.</param>
public readonly record struct ColumnSlot(int Index, double X, double Width);

/// <summary>
/// The columns in display order, and the arithmetic over them: where each one sits, which ones a
/// viewport can see, and what the pointer is on.
///
/// <para>Offsets are cached and rebuilt when anything changes shape, so a hit test is a binary
/// search rather than a walk. With about fifty columns speed matters less than this: the offsets
/// are computed in one place, so nothing can disagree about where a column starts.</para>
///
/// <para>There are no frozen columns. RPM's two grids do not need them, and the painter would need
/// a second coordinate space to support them. Adding them means changing this file.</para>
/// </summary>
public sealed class GridColumns
{
    private readonly List<GridColumn> _all;

    private readonly List<GridColumn> _visible = [];

    /// <summary>The left edge of each visible column, plus the total width as a final entry, so
    /// <c>_edges[i + 1] - _edges[i]</c> is always column i's width.</summary>
    private double[] _edges = [0];

    private bool _stale = true;

    public GridColumns(IEnumerable<GridColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        _all = [.. columns];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in _all)
        {
            if (!seen.Add(c.Id))
                throw new ArgumentException($"Two columns share the id '{c.Id}'.", nameof(columns));
        }
    }

    /// <summary>Raised whenever the strip's shape changes: a width, a hiding, a reorder.</summary>
    public event Action? Changed;

    /// <summary>Every column, hidden ones included, in display order.</summary>
    public IReadOnlyList<GridColumn> All => _all;

    /// <summary>The columns actually drawn, in display order.</summary>
    public IReadOnlyList<GridColumn> Visible
    {
        get
        {
            Settle();
            return _visible;
        }
    }

    /// <summary>How wide the whole strip is. What the horizontal scrollbar is sized against.</summary>
    public double TotalWidth
    {
        get
        {
            Settle();
            return _edges[^1];
        }
    }

    /// <summary>The column with that id, or null.</summary>
    public GridColumn? ById(string id) => _all.FirstOrDefault(c => c.Id == id);

    /// <summary>Where a visible column's left edge is.</summary>
    public double LeftOf(int visibleIndex)
    {
        Settle();
        return _edges[Math.Clamp(visibleIndex, 0, _edges.Length - 1)];
    }

    /// <summary>Say that a width, a visibility or the order changed. Everything derived is rebuilt
    /// on the next question asked, and <see cref="Changed"/> is raised now.</summary>
    public void Invalidate()
    {
        _stale = true;
        Changed?.Invoke();
    }

    /// <summary>
    /// Sets a column's width, clamped to its minimum, and tells everything that cares. Returns
    /// whether it actually moved, so a drag that changes nothing does not repaint.
    /// </summary>
    public bool Resize(GridColumn column, double width)
    {
        ArgumentNullException.ThrowIfNull(column);

        var clamped = Math.Max(column.MinWidth, double.IsNaN(width) ? column.MinWidth : width);
        if (Math.Abs(column.Width - clamped) < 0.01)
            return false;

        column.Width = clamped;
        Invalidate();
        return true;
    }

    /// <summary>Shows or hides a column. Returns whether it changed.</summary>
    public bool Show(GridColumn column, bool visible)
    {
        ArgumentNullException.ThrowIfNull(column);

        if (column.IsVisible == visible)
            return false;

        column.IsVisible = visible;
        Invalidate();
        return true;
    }

    /// <summary>
    /// Moves a column to a new place in the display order. Both indices are into
    /// <see cref="All"/>, hidden columns included, because that is the order the session stores.
    /// </summary>
    public bool Move(int from, int to)
    {
        if (from < 0 || from >= _all.Count)
            return false;

        to = Math.Clamp(to, 0, _all.Count - 1);
        if (from == to)
            return false;

        var column = _all[from];
        _all.RemoveAt(from);
        _all.Insert(to, column);
        Invalidate();
        return true;
    }

    /// <summary>
    /// Puts the columns in the order given, by id. Ids not named keep their relative order and go
    /// after the named ones; ids named that this strip does not have are ignored. This is how a
    /// stored session order is applied without a missing or an added column breaking it.
    /// </summary>
    public void Order(IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < ids.Count; i++)
            rank.TryAdd(ids[i], i);

        var ordered = _all
            .Select((c, i) => (Column: c, Named: rank.TryGetValue(c.Id, out var r) ? r : int.MaxValue, Was: i))
            .OrderBy(t => t.Named)
            .ThenBy(t => t.Was)
            .Select(t => t.Column)
            .ToList();

        if (ordered.SequenceEqual(_all))
            return;

        _all.Clear();
        _all.AddRange(ordered);
        Invalidate();
    }

    /// <summary>
    /// The visible columns that intersect the viewport, as a half-open range of indices into
    /// <see cref="Visible"/>.
    /// </summary>
    /// <param name="scrollX">How far the strip is scrolled.</param>
    /// <param name="viewportWidth">How much width the strip is drawn into.</param>
    /// <param name="overscan">Extra columns on each side. One is enough to keep a slow drag from
    /// reaching the edge of what has been measured.</param>
    public (int Start, int End) RangeAt(double scrollX, double viewportWidth, int overscan = 1)
    {
        Settle();

        var count = _visible.Count;
        if (count == 0)
            return (0, 0);

        // Nothing has been measured yet. Everything is in range rather than nothing: answering "no
        // columns" before the first layout draws an empty grid that never recovers.
        if (double.IsNaN(viewportWidth) || viewportWidth <= 0)
            return (0, count);

        var from = double.IsNaN(scrollX) ? 0 : Math.Max(0, scrollX);
        var to = from + viewportWidth;

        // A column occupies [left, right). It is touched when its right edge is past the viewport's
        // left and its left edge is before the viewport's right, so the two bounds are not the same
        // search. A column starting exactly where the viewport ends is not on screen, which is why
        // the end uses "first edge at or after" and not "first edge after".
        var start = UpperBound(from) - 1;
        if (start < 0)
            start = 0;

        var end = FirstAtLeast(to);
        if (end > count)
            end = count;

        start = Math.Max(0, start - overscan);
        end = Math.Min(count, end + overscan);

        return start >= end ? (start, start) : (start, end);
    }

    /// <summary>Where each column of a range sits. Offsets are absolute in the strip, not relative
    /// to the range, so a cell drawn from one does not jump when the range moves.</summary>
    public void SlotsInto(( int Start, int End) range, List<ColumnSlot> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        Settle();

        into.Clear();
        var start = Math.Clamp(range.Start, 0, _visible.Count);
        var end = Math.Clamp(range.End, start, _visible.Count);

        for (var i = start; i < end; i++)
            into.Add(new ColumnSlot(i, _edges[i], _edges[i + 1] - _edges[i]));
    }

    /// <summary>
    /// Which visible column an x in strip coordinates falls in, or -1 past the last one.
    /// </summary>
    /// <param name="within">How far into that column the point is.</param>
    public int HitTest(double x, out double within)
    {
        Settle();

        within = 0;
        if (_visible.Count == 0 || double.IsNaN(x) || x < 0 || x >= _edges[^1])
            return -1;

        var i = UpperBound(x) - 1;
        if (i < 0 || i >= _visible.Count)
            return -1;

        within = x - _edges[i];
        return i;
    }

    /// <summary>
    /// Which visible column's right edge is within <paramref name="tolerance"/> of x, for starting a
    /// resize drag. -1 when the pointer is not on an edge, or is on the edge of a column that
    /// cannot be resized.
    /// </summary>
    public int EdgeAt(double x, double tolerance)
    {
        Settle();

        if (double.IsNaN(x))
            return -1;

        for (var i = 0; i < _visible.Count; i++)
        {
            if (!_visible[i].CanResize)
                continue;
            if (Math.Abs(_edges[i + 1] - x) <= tolerance)
                return i;
        }

        return -1;
    }

    /// <summary>The index into <c>_edges</c> of the first edge at or after x.</summary>
    private int FirstAtLeast(double x)
    {
        var lo = 0;
        var hi = _edges.Length;

        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_edges[mid] < x)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    /// <summary>The index into <c>_edges</c> of the first edge strictly greater than x.</summary>
    private int UpperBound(double x)
    {
        var lo = 0;
        var hi = _edges.Length;

        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_edges[mid] <= x)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    private void Settle()
    {
        if (!_stale)
            return;

        _visible.Clear();
        foreach (var c in _all)
            if (c.IsVisible)
                _visible.Add(c);

        if (_edges.Length != _visible.Count + 1)
            _edges = new double[_visible.Count + 1];

        var x = 0d;
        _edges[0] = 0;
        for (var i = 0; i < _visible.Count; i++)
        {
            var w = _visible[i].Width;

            // A width that was never set adds nothing. NaN would make every offset after it NaN, and
            // break every hit test for the rest of the session.
            x += double.IsNaN(w) || w < 0 ? 0 : w;
            _edges[i + 1] = x;
        }

        _stale = false;
    }
}
