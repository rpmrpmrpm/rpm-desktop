namespace RPM.DataGrid.Model;

/// <summary>
/// A list already in memory, read as grid rows.
///
/// <para>The third source beside <see cref="SqlGridSource"/> and <c>TreeGridSource</c>, for a grid
/// whose rows are all built before it draws them: nothing to page in and nothing to fetch while
/// scrolling. <see cref="Replace"/> is the only way the list changes. It reports a new list, not the
/// moves inside it, so the grid clears its selection and goes back to the top, as it does for a new
/// filter or a new sort.</para>
///
/// <para>A host that must keep a choice across a replace does not keep it in the grid's selection:
/// it reads its own state at paint time through <c>RpmGrid.Washed</c>.</para>
/// </summary>
public sealed class ListGridSource : IGridSource
{
    private IReadOnlyList<object> _rows = [];

    public int Count => _rows.Count;

    public object? Row(int index) => Peek(index);

    public object? Peek(int index) => index < 0 || index >= _rows.Count ? null : _rows[index];

    /// <summary>Nothing to do: every row is already here.</summary>
    public void Prefetch(int first, int end)
    {
    }

    public event Action? Reset;

    // A list in memory has no pages to load and reports no moves: Replace is the only change. These
    // three events exist only because IGridSource requires them.
#pragma warning disable CS0067
    public event Action<int, int>? Landed;

    public event Action<int, int>? Inserted;

    public event Action<int, int>? Removed;
#pragma warning restore CS0067

    /// <summary>The rows, from now on.</summary>
    public void Replace(IReadOnlyList<object> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _rows = rows;
        Reset?.Invoke();
    }
}
