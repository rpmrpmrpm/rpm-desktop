using RPM.DataGrid.Model;

namespace RPM.DataGrid.Tests;

/// <summary>
/// Rows fetched a page at a time. Fetches finish inline here, so a continuation runs before the call
/// that caused it returns, unless the test says to hold it.
/// </summary>
public class SqlGridSourceTests
{
    private sealed record Track(int Id, string Title);

    /// <summary>A store of numbered tracks, with a count and a page reader that can be held open.</summary>
    private sealed class Store
    {
        private readonly List<TaskCompletionSource<IReadOnlyList<object>>> _held = [];
        private readonly List<TaskCompletionSource<int>> _heldCounts = [];

        public int Total { get; set; } = 1000;

        public bool Hold { get; set; }

        /// <summary>Holds the count pass open, so a test can look at the source while it does not yet
        /// know how many rows the new question matches.</summary>
        public bool HoldCount { get; set; }

        public int Counts { get; private set; }

        public int Pages { get; private set; }

        public List<(int Offset, int Limit)> Asked { get; } = [];

        public List<SortBy> LastSort { get; private set; } = [];

        public SqlGridSource Build(int pageSize = 10, int maxPages = 4) =>
            new(CountAsync, PageAsync, pageSize, maxPages);

        private Task<int> CountAsync(SqlGridSource.Query query, CancellationToken ct)
        {
            Counts++;
            LastSort = [.. query.Sorts];

            if (!HoldCount)
                return Task.FromResult(Total);

            var pending = new TaskCompletionSource<int>();
            _heldCounts.Add(pending);
            return pending.Task;
        }

        /// <summary>Lets every held count finish.</summary>
        public void DeliverCounts()
        {
            var held = _heldCounts.ToList();
            _heldCounts.Clear();
            foreach (var pending in held)
                pending.SetResult(Total);
        }

        private Task<IReadOnlyList<object>> PageAsync(
            SqlGridSource.Query query, int offset, int limit, CancellationToken ct)
        {
            Pages++;
            Asked.Add((offset, limit));

            var rows = new List<object>();
            for (var i = offset; i < Math.Min(offset + limit, Total); i++)
                rows.Add(new Track(i, $"Track {i}"));

            if (!Hold)
                return Task.FromResult<IReadOnlyList<object>>(rows);

            var pending = new TaskCompletionSource<IReadOnlyList<object>>();
            _held.Add(pending);
            return pending.Task;
        }

        /// <summary>Lets every held page finish, with the rows it was asked for.</summary>
        public void Deliver()
        {
            var held = _held.ToList();
            _held.Clear();

            for (var i = 0; i < held.Count; i++)
            {
                var (offset, limit) = Asked[^ (held.Count - i)];
                var rows = new List<object>();
                for (var r = offset; r < Math.Min(offset + limit, Total); r++)
                    rows.Add(new Track(r, $"Track {r}"));
                held[i].SetResult(rows);
            }
        }
    }

    [Fact]
    public void Before_it_is_asked_it_holds_nothing()
    {
        var store = new Store();
        using var source = store.Build();

        Assert.Equal(0, source.Count);
        Assert.Null(source.Row(0));
        Assert.Equal(0, store.Counts);
    }

    [Fact]
    public void A_refresh_asks_for_the_count_and_reports_it()
    {
        var store = new Store { Total = 1000 };
        using var source = store.Build();

        source.Refresh();

        Assert.Equal(1, store.Counts);
        Assert.Equal(1000, source.Count);
    }

    [Fact]
    public void Reading_a_row_fetches_the_page_it_is_in_and_no_other()
    {
        var store = new Store();
        using var source = store.Build(pageSize: 10);
        source.Refresh();

        store.Hold = true;
        Assert.Null(source.Row(25));

        Assert.Single(store.Asked);
        Assert.Equal((20, 10), store.Asked[0]);
    }

    [Fact]
    public void A_row_whose_page_is_in_flight_is_null_and_arrives_later()
    {
        var store = new Store();
        using var source = store.Build();
        source.Refresh();

        store.Hold = true;
        Assert.Null(source.Row(5));

        store.Deliver();
        Assert.Equal(new Track(5, "Track 5"), source.Row(5));
    }

    [Fact]
    public void Asking_twice_for_a_page_in_flight_fetches_once()
    {
        var store = new Store();
        using var source = store.Build();
        source.Refresh();

        store.Hold = true;
        source.Row(1);
        source.Row(2);
        source.Row(9);

        Assert.Equal(1, store.Pages);
    }

    [Fact]
    public void A_landing_page_says_which_rows_it_filled()
    {
        var store = new Store();
        using var source = store.Build(pageSize: 10);
        source.Refresh();

        var landed = new List<(int, int)>();
        source.Landed += (a, b) => landed.Add((a, b));

        store.Hold = true;
        source.Row(35);
        store.Deliver();

        Assert.Equal([(30, 40)], landed);
    }

    [Fact]
    public void The_last_page_lands_short()
    {
        var store = new Store { Total = 25 };
        using var source = store.Build(pageSize: 10);
        source.Refresh();

        var landed = new List<(int, int)>();
        source.Landed += (a, b) => landed.Add((a, b));

        store.Hold = true;
        source.Row(22);
        store.Deliver();

        Assert.Equal([(20, 25)], landed);
        Assert.Null(source.Row(25));
    }

    [Fact]
    public void Peeking_never_fetches()
    {
        var store = new Store();
        using var source = store.Build();
        source.Refresh();

        Assert.Null(source.Peek(500));
        Assert.Equal(0, store.Pages);
    }

    [Fact]
    public void Prefetching_asks_for_every_page_the_range_touches()
    {
        var store = new Store();
        using var source = store.Build(pageSize: 10);
        source.Refresh();

        store.Hold = true;
        source.Prefetch(5, 25);

        Assert.Equal(3, store.Pages);
        Assert.Equal([(0, 10), (10, 10), (20, 10)], store.Asked);
    }

    [Fact]
    public void Prefetching_past_the_end_is_clamped()
    {
        var store = new Store { Total = 12 };
        using var source = store.Build(pageSize: 10);
        source.Refresh();

        store.Hold = true;
        source.Prefetch(-50, 5000);

        Assert.Equal(2, store.Pages);
    }

    [Fact]
    public void Prefetching_an_empty_query_asks_for_nothing()
    {
        var store = new Store { Total = 0 };
        using var source = store.Build();
        source.Refresh();

        source.Prefetch(0, 40);

        Assert.Equal(0, store.Pages);
    }

    /// <summary>
    /// The point of the class: a million rows are never held. Reading right through the list leaves
    /// the cache at its bound, not at the list's length.
    /// </summary>
    [Fact]
    public void The_cache_stays_bounded_however_far_it_is_read()
    {
        var store = new Store { Total = 100_000 };
        using var source = store.Build(pageSize: 10, maxPages: 4);
        source.Refresh();

        for (var i = 0; i < 100_000; i += 7)
            source.Row(i);

        Assert.Equal(4, source.PagesHeld);
        Assert.Equal(40, source.CachedRows.Count());
    }

    /// <summary>The page dropped is the one nobody has looked at for longest, not the oldest
    /// fetched.</summary>
    [Fact]
    public void The_least_recently_read_page_is_the_one_dropped()
    {
        var store = new Store();
        using var source = store.Build(pageSize: 10, maxPages: 2);
        source.Refresh();

        source.Row(0);        // page 0
        source.Row(10);       // page 1
        source.Row(0);        // page 0 again, so page 1 is now the oldest
        source.Row(20);       // page 2 arrives and something must go

        Assert.NotNull(source.Peek(0));
        Assert.Null(source.Peek(10));
        Assert.NotNull(source.Peek(20));
    }

    [Fact]
    public void A_new_sort_is_a_new_question()
    {
        var store = new Store();
        using var source = store.Build();
        source.Refresh();
        source.Row(0);

        var resets = 0;
        source.Reset += () => resets++;

        source.SetSort([new SortBy("title", Descending: true)]);

        Assert.Equal(1, resets);
        Assert.Equal(0, source.PagesHeld);
        Assert.Equal([new SortBy("title", true)], store.LastSort);
    }

    [Fact]
    public void A_changed_query_drops_what_was_held_and_resets()
    {
        var store = new Store();
        using var source = store.Build();
        source.Refresh();
        source.Row(0);
        Assert.Equal(1, source.PagesHeld);

        var resets = 0;
        source.Reset += () => resets++;

        source.Refresh(queryChanged: true);

        Assert.Equal(1, resets);
        Assert.Equal(0, source.PagesHeld);
    }

    /// <summary>
    /// The same question asked again keeps the reader's place: the pages that were held are read once
    /// more and patched where they sit, and nothing is reset.
    /// </summary>
    [Fact]
    public void The_same_question_asked_again_re_reads_what_was_held()
    {
        var store = new Store();
        using var source = store.Build(pageSize: 10);
        source.Refresh();
        source.Row(0);
        source.Row(50);

        var resets = 0;
        source.Reset += () => resets++;
        var landed = new List<(int, int)>();
        source.Landed += (a, b) => landed.Add((a, b));

        source.Refresh(queryChanged: false);

        Assert.Equal(0, resets);
        Assert.Equal(2, source.PagesHeld);
        Assert.Equal([(0, 10), (50, 60)], landed.OrderBy(l => l.Item1));
    }

    [Fact]
    public void A_shorter_answer_drops_the_pages_past_its_end()
    {
        var store = new Store { Total = 1000 };
        using var source = store.Build(pageSize: 10, maxPages: 8);
        source.Refresh();
        source.Row(0);
        source.Row(500);

        store.Total = 20;
        source.Refresh(queryChanged: false);

        Assert.Equal(20, source.Count);
        Assert.Null(source.Peek(500));
    }

    /// <summary>
    /// A page that was asked for under the old question must not land into the new one. It would put
    /// rows from a different query at indices the reader is looking at.
    /// </summary>
    [Fact]
    public void A_page_in_flight_when_the_question_changes_does_not_land()
    {
        var store = new Store();
        using var source = store.Build(pageSize: 10);
        source.Refresh();

        store.Hold = true;
        source.Row(0);

        source.Refresh(queryChanged: true);

        var landed = 0;
        source.Landed += (_, _) => landed++;

        store.Deliver();

        Assert.Equal(0, landed);
        Assert.Equal(0, source.PagesHeld);
    }

    /// <summary>
    /// Between a new question and its count, the source knows nothing and says so. A page fetched in
    /// that window would be filled from the store's old query and cached as the new one's, which is
    /// how a filtered table ends up showing the rows it just filtered out.
    /// </summary>
    [Fact]
    public void A_changed_query_hands_out_nothing_until_the_new_count_lands()
    {
        var store = new Store { Total = 1000 };
        using var source = store.Build(pageSize: 10);
        source.Refresh();
        source.Row(0);

        store.HoldCount = true;
        source.Refresh(queryChanged: true);

        Assert.Equal(0, source.Count);
        Assert.Null(source.Row(0));
        Assert.Null(source.Peek(0));

        source.Prefetch(0, 40);
        Assert.Equal(1, store.Pages);   // the one before the refresh, and none since

        store.HoldCount = false;
        store.DeliverCounts();

        Assert.Equal(1000, source.Count);

        // The first read starts the page; it finishes inline here, so the second read has the rows
        // of the NEW question rather than the ones the cache was holding before it.
        source.Row(0);
        Assert.Equal(new Track(0, "Track 0"), source.Row(0));
    }

    [Fact]
    public void An_index_outside_the_query_is_nothing()
    {
        var store = new Store { Total = 5 };
        using var source = store.Build();
        source.Refresh();

        Assert.Null(source.Row(-1));
        Assert.Null(source.Row(5));
        Assert.Null(source.Peek(int.MaxValue));
    }

    [Fact]
    public void Disposing_stops_a_page_landing_afterwards()
    {
        var store = new Store();
        var source = store.Build();
        source.Refresh();

        store.Hold = true;
        source.Row(0);

        var landed = 0;
        source.Landed += (_, _) => landed++;

        source.Dispose();
        store.Deliver();

        Assert.Equal(0, landed);
    }
}
