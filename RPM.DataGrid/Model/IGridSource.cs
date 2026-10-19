namespace RPM.DataGrid.Model;

/// <summary>
/// The rows the grid draws.
///
/// <para>Four members and four events. The grid is not a <c>ListView</c>, so there is no
/// <c>ICollectionView</c>, no <c>IObservableVector</c>, no current-item tracking, no incremental
/// loading and no grouping. Nothing in RPM needs those features.</para>
///
/// <para>Everything on this interface is called on the UI thread. A source that reads on a worker
/// raises <see cref="Landed"/> back on the UI thread when the read arrives.</para>
/// </summary>
public interface IGridSource
{
    /// <summary>How many rows there are. Known up front, including for rows never fetched.</summary>
    int Count { get; }

    /// <summary>
    /// The row at an index, or null when it has not been fetched yet. A null row is drawn as an
    /// empty line; it is not an error and it is not a gap in the list.
    /// </summary>
    object? Row(int index);

    /// <summary>
    /// The row at an index if it is already held, and null otherwise, WITHOUT fetching it.
    ///
    /// <para>For a host that asks about rows that have not been shown. Asking through
    /// <see cref="Row"/> would load every page of the list from the store.</para>
    /// </summary>
    object? Peek(int index);

    /// <summary>
    /// Says which rows are about to be drawn, so the source can fetch what it does not hold. The
    /// range is half-open and is whatever the viewport touches, overscan included.
    /// </summary>
    void Prefetch(int first, int end);

    /// <summary>
    /// The list is a different list now: a new sort, a new filter, a rebuild. Indices mean nothing
    /// across this, so the grid clears its selection and goes back to the top.
    /// </summary>
    event Action? Reset;

    /// <summary>
    /// Rows in a half-open range now have real values where they had none. Nothing moved; the grid
    /// repaints if any of them are on screen.
    /// </summary>
    event Action<int, int>? Landed;

    /// <summary>Rows appeared at an index. A group expanding.</summary>
    event Action<int, int>? Inserted;

    /// <summary>Rows went away from an index. A group collapsing.</summary>
    event Action<int, int>? Removed;
}
