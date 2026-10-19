using RPM.Core.Library;
using RPM.Desktop.Services;
using RPM.DataGrid.Model;

namespace RPM.Desktop.Presentation.Main.Library.Models;

/// <summary>
/// The library data table's datasource: one flat row per track, read from the SQLite mirror. The
/// database sorts and filters the rows, not memory. A collection can hold a million tracks, so
/// nothing here keeps more than the pages on screen.
///
/// <para>Order is settled ONCE per query, not per page. A header click on a column with no index
/// causes a scan and a sort over the whole table. Running that again with <c>ORDER BY ... LIMIT ...
/// OFFSET</c> would repeat the work for every screenful scrolled past. Instead the count pass fetches
/// the matching ids in order (eight bytes per track). Each later page is a primary-key lookup of a
/// window of that list.</para>
///
/// <para>The sort and the filters are both keyed by a column's id. The id IS the mirror's column
/// name. Both are checked against the whitelist in <see cref="TrackTableColumns"/> before they reach
/// any SQL.</para>
/// </summary>
public sealed class LibraryTableSource
{
    private readonly LibraryService _library;
    private readonly PlaybackService _playback;

    /// <summary>The ids the current query matches, in order. This is the sort, stored in memory. It is
    /// replaced whole on each count pass, and pages index into it.</summary>
    private IReadOnlyList<long> _order = [];

    private string? _search;

    public LibraryTableSource(LibraryService library, PlaybackService playback)
    {
        _library = library;
        _playback = playback;
        Source = new SqlGridSource(CountAsync, PageAsync);
    }

    /// <summary>The rows, fetched a page at a time. Assign to the grid.</summary>
    public SqlGridSource Source { get; }

    /// <summary>
    /// What each column's filter is narrowed to, by the column's name in the mirror. A column with
    /// no entry is not filtered.
    /// </summary>
    private readonly Dictionary<string, TextMatch> _filters = new(StringComparer.Ordinal);

    /// <summary>Which columns are filtered, for the headers to say so.</summary>
    public IReadOnlyCollection<string> FilteredColumns => _filters.Keys;

    /// <summary>What a column is filtered to, or null when it is not.</summary>
    public TextMatch? FilterOn(string column) =>
        _filters.TryGetValue(column, out var match) ? match : null;

    /// <summary>
    /// Sets what a column is filtered to and asks the database again. Null takes the filter off; a
    /// <see cref="TextMatch"/> cannot be built with no text, so there is no way to ask for a filter
    /// that matches nothing.
    /// </summary>
    public void SetFilter(string column, TextMatch? match)
    {
        if (match is null)
        {
            if (!_filters.Remove(column))
                return;
        }
        else
        {
            _filters[column] = match;
        }

        Source.Refresh(queryChanged: true);
    }

    /// <summary>
    /// Asks the database again, using the header's current search. This runs for a keystroke and for a
    /// mirror change, but the two are different. A keystroke asks a different question, so every row
    /// gets a different track and the grid is rebuilt. A mirror change (a track edited, a few scanned
    /// in) asks the same question again. The rows on screen are re-fetched and patched where they are,
    /// which keeps their position and selection. The source cannot tell the two apart by itself. It
    /// compares the sort and the filters, but the search text reaches the database through
    /// <see cref="Build"/>, not through the source.
    /// </summary>
    public void Refresh(string? search)
    {
        var text = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var changed = !string.Equals(text, _search, StringComparison.Ordinal);
        _search = text;
        Source.Refresh(queryChanged: changed);
    }

    /// <summary>The rows the source is holding: the pages in memory. There are a few hundred of them,
    /// whatever the query matches.</summary>
    public IEnumerable<LibraryTableRow> RealizedRows => Source.CachedRows.OfType<LibraryTableRow>();

    private async Task<int> CountAsync(SqlGridSource.Query query, CancellationToken ct)
    {
        if (!_library.IsOpen)
        {
            _order = [];
            return 0;
        }

        var request = Build(query);
        var mirror = _library.Shelf;
        var asked = await Task.Run(() => _library.Read(mirror, "the track grid's rows", shelf => shelf.QueryTrackIds(request)));
        if (ct.IsCancellationRequested)
            return _order.Count;

        // No answer means an empty grid. A refusal has already put the problem screen up over the app
        // (see LibraryService.Read). A closed mirror is followed by a Changed event, which counts again.
        _order = asked.Match(ids => ids, _ => []);
        return _order.Count;
    }

    private async Task<IReadOnlyList<object>> PageAsync(
        SqlGridSource.Query query, int offset, int limit, CancellationToken ct)
    {
        if (_library.ShelfRoot is not { } root)
            return [];

        // The order list is the page's index. A refresh that lands between the grid asking and this
        // running can leave the window short or empty. The count that replaced it is already on its way
        // to the grid, so an empty page is the right answer here, not an error.
        var order = _order;
        if (offset >= order.Count)
            return [];
        var window = order.Skip(offset).Take(Math.Min(limit, order.Count - offset)).ToList();

        var mirror = _library.Shelf;
        var asked = await Task.Run(() => _library.Read(mirror, "a page of the track grid", shelf => shelf.QueryTracksByIds(window)));
        if (ct.IsCancellationRequested || !asked.Answers(out var tracks, out _))
            return [];   // unanswered: see CountAsync

        var rows = new List<object>(tracks.Count);
        foreach (var track in tracks)
            rows.Add(new LibraryTableRow(track, root, _playback));
        return rows;
    }

    /// <summary>
    /// The query as it stands: the header's search, the grid's sort, and the column filters.
    /// </summary>
    private TrackTableQuery Build(SqlGridSource.Query query)
    {
        var sorts = new List<TrackTableSort>();
        foreach (var sort in query.Sorts)
        {
            if (TrackTableColumns.Sql(sort.Column) is { } sql)
                sorts.Add(new TrackTableSort(sql, sort.Descending));
        }

        var filters = new List<TrackTableFilter>();
        foreach (var pair in _filters)
        {
            // A column the whitelist does not know is not in the database, so it cannot be filtered.
            // It is skipped.
            if (TrackTableColumns.Sql(pair.Key) is { } sql)
                filters.Add(new TrackTableFilter(sql, pair.Value));
        }

        return new TrackTableQuery(_search, sorts, filters);
    }
}
