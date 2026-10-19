using RPM.DataGrid.Model;

namespace RPM.DataGrid.Tree;

/// <summary>
/// A <see cref="VirtualTreeModel"/> read as a flat list of rows.
///
/// <para>This is the whole adapter: the grid only asks for a count and an index, so no
/// <c>ICollectionView</c>, <c>IObservableVector</c> or <c>IList</c> is needed.</para>
///
/// <para>A page that arrives raises <see cref="Landed"/> once per row, and a page has two hundred
/// rows. That is deliberate and it is not two hundred repaints: on a landing the grid marks itself
/// dirty and asks the surface to redraw, and asking twice before a frame is drawn is the same as
/// asking once. Merging the runs here would need a second, worse frame boundary, and it would have
/// to guess when the batch had ended.</para>
/// </summary>
public sealed class TreeGridSource : IGridSource, IDisposable
{
    private readonly IVirtualTree _model;

    public TreeGridSource(IVirtualTree model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;

        _model.ResetRaised += OnReset;
        _model.RangeInserted += OnInserted;
        _model.RangeRemoved += OnRemoved;
        _model.ItemReplaced += OnReplaced;
    }

    /// <summary>The tree underneath, for work the grid itself does not do: expanding a group,
    /// finding a row again after a rebuild, restoring an order.</summary>
    public IVirtualTree Model => _model;

    public int Count => _model.Count;

    public event Action? Reset;
    public event Action<int, int>? Landed;
    public event Action<int, int>? Inserted;
    public event Action<int, int>? Removed;

    /// <summary>
    /// The row at an index. Asking for it fetches the page it is in, which is how this tree loads:
    /// a row is requested when the viewport reaches it.
    /// </summary>
    public object? Row(int index) =>
        index < 0 || index >= _model.Count ? null : _model.GetAt(index);

    /// <summary>The row at an index if its page is already held. Fetches nothing.</summary>
    public object? Peek(int index) =>
        index < 0 || index >= _model.Count ? null : _model.PeekAt(index);

    /// <summary>
    /// Says which rows are about to be drawn. The model fetches a page when a row inside it is
    /// read, so asking for each row in the range is the request.
    /// </summary>
    public void Prefetch(int first, int end)
    {
        var count = _model.Count;
        if (count == 0)
            return;

        first = Math.Clamp(first, 0, count - 1);
        end = Math.Clamp(end, first, count);

        for (var i = first; i < end; i++)
            _model.GetAt(i);
    }

    private void OnReset() => Reset?.Invoke();

    private void OnInserted(int first, int count) => Inserted?.Invoke(first, first + count);

    private void OnRemoved(int first, int count) => Removed?.Invoke(first, first + count);

    private void OnReplaced(int index, object row, object was)
    {
        _ = row;
        _ = was;
        Landed?.Invoke(index, index + 1);
    }

    public void Dispose()
    {
        _model.ResetRaised -= OnReset;
        _model.RangeInserted -= OnInserted;
        _model.RangeRemoved -= OnRemoved;
        _model.ItemReplaced -= OnReplaced;
    }
}
