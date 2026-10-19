namespace RPM.DataGrid.Model;

/// <summary>One column the query is ordered by.</summary>
public readonly record struct SortBy(string Column, bool Descending);

/// <summary>
/// Rows fetched a page at a time from a store that holds far more of them than memory does.
///
/// <para>The grid is not a list control, so this needs no <c>ICollectionView</c>,
/// <c>IObservableVector</c> or <c>IList</c> implementation. The grid needs only a count, an
/// indexer, a prefetch and an event.</para>
///
/// <para>A row whose page has not arrived is null, not a placeholder object. The painter draws
/// nothing for it, which is the right look for a row that is still loading. No placeholder objects
/// need to be created or tracked.</para>
///
/// <para>Everything here runs on the UI thread. The two callbacks are awaited, so they may do their
/// work on a worker; their continuations come back here.</para>
/// </summary>
public sealed class SqlGridSource : IGridSource, IDisposable
{
    /// <summary>The query sent to the store. It carries the sort; the host adds its own search and
    /// filters, which this class does not know about.</summary>
    public sealed record Query(IReadOnlyList<SortBy> Sorts);

    /// <summary>How many rows the query matches.</summary>
    public delegate Task<int> CountFn(Query query, CancellationToken ct);

    /// <summary>A window of rows, in the query's order.</summary>
    public delegate Task<IReadOnlyList<object>> PageFn(Query query, int offset, int limit, CancellationToken ct);

    private readonly CountFn _count;
    private readonly PageFn _page;
    private readonly int _pageSize;
    private readonly int _maxPages;

    /// <summary>The pages held, by page number. Each is a full page except possibly the last.</summary>
    private readonly Dictionary<int, object?[]> _pages = [];

    /// <summary>Page numbers in the order they were last read, oldest first. The eviction order.</summary>
    private readonly LinkedList<int> _used = [];

    private readonly Dictionary<int, LinkedListNode<int>> _useNodes = [];

    /// <summary>Pages being fetched right now, so a viewport asking twice does not fetch twice.</summary>
    private readonly HashSet<int> _inFlight = [];

    private CancellationTokenSource? _work;
    private List<SortBy> _sorts = [];
    private int _count_;

    public SqlGridSource(CountFn count, PageFn page, int pageSize = 200, int maxPages = 64)
    {
        ArgumentNullException.ThrowIfNull(count);
        ArgumentNullException.ThrowIfNull(page);

        _count = count;
        _page = page;
        _pageSize = pageSize > 0 ? pageSize : throw new ArgumentOutOfRangeException(nameof(pageSize));
        _maxPages = maxPages > 0 ? maxPages : throw new ArgumentOutOfRangeException(nameof(maxPages));
    }

    public int Count => _count_;

    public event Action? Reset;
    public event Action<int, int>? Landed;

#pragma warning disable CS0067 // A flat query never inserts or removes; it is counted again instead.
    public event Action<int, int>? Inserted;
    public event Action<int, int>? Removed;
#pragma warning restore CS0067

    /// <summary>The sort, as the header last set it.</summary>
    public IReadOnlyList<SortBy> Sorts => _sorts;

    /// <summary>How many pages are held. Diagnostics.</summary>
    public int PagesHeld => _pages.Count;

    /// <summary>The rows actually in memory. At most <c>maxPages</c> pages, however many rows the
    /// query matches.</summary>
    public IEnumerable<object> CachedRows
    {
        get
        {
            foreach (var page in _pages.Values)
                foreach (var row in page)
                    if (row is not null)
                        yield return row;
        }
    }

    /// <summary>
    /// The row at an index, fetching the page it is in if it is not held.
    /// </summary>
    public object? Row(int index)
    {
        if (index < 0 || index >= _count_)
            return null;

        var page = index / _pageSize;
        if (_pages.TryGetValue(page, out var held))
        {
            Touch(page);
            return held[index % _pageSize];
        }

        Fetch(page);
        return null;
    }

    /// <summary>The row at an index if its page is held. Fetches nothing.</summary>
    public object? Peek(int index)
    {
        if (index < 0 || index >= _count_)
            return null;

        return _pages.TryGetValue(index / _pageSize, out var held)
            ? held[index % _pageSize]
            : null;
    }

    public void Prefetch(int first, int end)
    {
        if (_count_ == 0)
            return;

        first = Math.Clamp(first, 0, _count_ - 1);
        end = Math.Clamp(end, first, _count_);
        if (end == first)
            return;

        for (var page = first / _pageSize; page <= (end - 1) / _pageSize; page++)
        {
            if (_pages.ContainsKey(page))
                Touch(page);
            else
                Fetch(page);
        }
    }

    /// <summary>Sets the sort and queries the store again. A different sort is a different query, so
    /// it is a reset.</summary>
    public void SetSort(IReadOnlyList<SortBy> sorts)
    {
        ArgumentNullException.ThrowIfNull(sorts);
        _sorts = [.. sorts];
        Refresh(queryChanged: true);
    }

    /// <summary>
    /// Queries the store again.
    /// </summary>
    /// <param name="queryChanged">
    /// Whether this is a different QUERY. A new search or a new filter puts a different track at
    /// every row, so the grid starts again: the pages are dropped and so is the selection. The same
    /// query run again (after a track is edited, or a few are scanned in) re-fetches the pages that
    /// are held and updates them in place, which keeps the user's scroll position and selection.
    /// </param>
    public void Refresh(bool queryChanged = true)
    {
        var resident = queryChanged ? [] : _pages.Keys.ToList();

        CancelWork();

        if (queryChanged)
        {
            _pages.Clear();
            _used.Clear();
            _useNodes.Clear();

            // Nothing is known until the new count arrives, so the source returns no rows and fetches
            // no pages until then. A page fetched now would be filled from the host's OLD query (the
            // host switches to the new query when it answers the count, not when it is asked), and
            // would then stay in the cache as if it belonged to the new one.
            _count_ = 0;
        }

        var work = _work = new CancellationTokenSource();
        var token = work.Token;
        var query = new Query(_sorts);

        _ = CountThen(query, token, queryChanged, resident);
    }

    private async Task CountThen(Query query, CancellationToken token, bool queryChanged, List<int> resident)
    {
        int total;
        try
        {
            total = await _count(query, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested)
            return;

        _count_ = Math.Max(0, total);

        // Pages past the end of the new count are dropped, whatever kind of refresh this was.
        foreach (var page in _pages.Keys.ToList())
        {
            if (page * _pageSize >= _count_)
                Drop(page);
        }

        if (queryChanged)
        {
            Reset?.Invoke();
            return;
        }

        // The same query run again: the held pages are read once more and updated in place.
        foreach (var page in resident)
        {
            if (page * _pageSize < _count_)
                Fetch(page, force: true);
        }
    }

    private void Fetch(int page, bool force = false)
    {
        if (_inFlight.Contains(page))
            return;
        if (!force && _pages.ContainsKey(page))
            return;

        var offset = page * _pageSize;
        if (offset >= _count_)
            return;

        _inFlight.Add(page);

        var token = _work?.Token ?? CancellationToken.None;
        var query = new Query(_sorts);

        _ = Land(page, query, offset, token);
    }

    private async Task Land(int page, Query query, int offset, CancellationToken token)
    {
        IReadOnlyList<object> rows;
        try
        {
            rows = await _page(query, offset, _pageSize, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            _inFlight.Remove(page);
            return;
        }

        _inFlight.Remove(page);

        // A refresh happened while this fetch was running, so these rows belong to an old query.
        if (token.IsCancellationRequested)
            return;

        var slot = new object?[_pageSize];
        for (var i = 0; i < rows.Count && i < _pageSize; i++)
            slot[i] = rows[i];

        _pages[page] = slot;
        Touch(page);
        Evict();

        var end = Math.Min(offset + Math.Min(rows.Count, _pageSize), _count_);
        if (end > offset)
            Landed?.Invoke(offset, end);
    }

    private void Touch(int page)
    {
        if (_useNodes.TryGetValue(page, out var node))
        {
            _used.Remove(node);
            _used.AddLast(node);
            return;
        }

        _useNodes[page] = _used.AddLast(page);
    }

    private void Evict()
    {
        while (_pages.Count > _maxPages && _used.First is { } oldest)
            Drop(oldest.Value);
    }

    private void Drop(int page)
    {
        _pages.Remove(page);

        if (_useNodes.Remove(page, out var node))
            _used.Remove(node);
    }

    private void CancelWork()
    {
        var work = _work;
        _work = null;
        _inFlight.Clear();

        if (work is null)
            return;

        try
        {
            work.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed, so there is nothing to cancel.
        }

        work.Dispose();
    }

    public void Dispose() => CancelWork();
}
