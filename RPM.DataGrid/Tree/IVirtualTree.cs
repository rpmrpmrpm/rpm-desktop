namespace RPM.DataGrid.Tree;

/// <summary>
/// A tree flattened into a list of rows, however it is implemented.
///
/// <para>Lets <see cref="TreeGridSource"/> read the import queue's tree without rebuilding it.
/// <see cref="VirtualTreeModel"/> implements this directly.</para>
/// </summary>
public interface IVirtualTree
{
    /// <summary>How many rows the tree is showing.</summary>
    int Count { get; }

    /// <summary>The row at an index, fetching the page it is in if it is not held. A placeholder
    /// object while a page is in flight.</summary>
    object? GetAt(int index);

    /// <summary>The row at an index if it is already held, without fetching anything.</summary>
    object? PeekAt(int index);

    /// <summary>The structure changed too much to report as index changes.</summary>
    event Action? ResetRaised;

    /// <summary>A run of rows appeared: a group expanded. First index, count.</summary>
    event Action<int, int>? RangeInserted;

    /// <summary>A run of rows went away: a group collapsed. First index, count.</summary>
    event Action<int, int>? RangeRemoved;

    /// <summary>A fetched page filled a placeholder. Index, the row, the placeholder it replaced.</summary>
    event Action<int, object, object>? ItemReplaced;
}
