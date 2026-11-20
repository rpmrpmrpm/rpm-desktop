using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// Taking other devices' listens in, a page at a time. What has to hold: a long history is taken over several passes
/// and never read from the start again, even when it was all uploaded in a few minutes, and a row the store wrote
/// late (a slow batch, a lagging index) is still read.
/// </summary>
public class ListensPullTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public ListensPullTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmpull-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static SharedListen Listen(string id) => new(id, "phone", "t1", 1, 200, 240, true, 22, 3);

    /// <summary>A store of rows with stamps, served oldest first in pages of ten "at or after" the stamp asked for,
    /// saying everything is complete up to <paramref name="safe"/>.</summary>
    private sealed class Store(Func<long> safe)
    {
        public List<(long Stamp, string Id)> Rows { get; } = [];
        public List<(long Since, string? Page)> Asked { get; } = [];

        public Task<ListensPage> Read(long since, string? page, CancellationToken ct)
        {
            Asked.Add((since, page));
            var all = Rows.Where(r => r.Stamp >= since).OrderBy(r => r.Stamp).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
            var start = page is null ? 0 : int.Parse(page);
            var rows = all.Skip(start).Take(10).ToList();
            var next = start + rows.Count < all.Count ? (start + rows.Count).ToString() : null;
            return Task.FromResult(new ListensPage(
                [.. rows.Select(r => Listen(r.Id))], rows.Count == 0 ? since : rows.Max(r => r.Stamp), next, safe()));
        }
    }

    /// <summary>Another computer sends five years of history in a few minutes. Each pass reads two pages and the next
    /// carries on where it stopped; no pass reads the start again.</summary>
    [Fact]
    public async Task A_history_uploaded_at_once_is_taken_over_several_passes_and_not_read_again()
    {
        const long now = 1_790_000_000_000;
        var store = new Store(() => now - 300_000);
        for (int i = 0; i < 45; i++)
            store.Rows.Add((now - 60_000 + i, $"l{i:D2}"));

        for (int pass = 0; pass < 3; pass++)
            await ListensPull.RunAsync(_db, "desk", store.Read, pagesPerPass: 2, CancellationToken.None);

        Assert.Equal(45, _db.ListenCount());
        Assert.Equal(5, store.Asked.Count);
        Assert.Single(store.Asked, a => a.Page is null);
    }

    /// <summary>A batch stamped before a later one but readable only after it, as a slow write or a lagging index does,
    /// is still read: the next listing starts from what the store said was complete, not from the newest stamp.</summary>
    [Fact]
    public async Task A_row_stamped_earlier_but_readable_later_is_still_read()
    {
        const long now = 1_790_000_000_000;
        var store = new Store(() => now - 300_000);
        store.Rows.Add((now - 1_000, "fast"));

        await ListensPull.RunAsync(_db, "desk", store.Read, pagesPerPass: 5, CancellationToken.None);
        store.Rows.Add((now - 2_000, "slow"));
        await ListensPull.RunAsync(_db, "desk", store.Read, pagesPerPass: 5, CancellationToken.None);

        Assert.Equal(2, _db.ListenCount());
    }

    /// <summary>Once the store says the old rows are complete, a listing asks from there and does not read them again.</summary>
    [Fact]
    public async Task Rows_older_than_what_is_complete_are_not_asked_for_again()
    {
        long now = 1_790_000_000_000;
        var store = new Store(() => now - 300_000);
        store.Rows.Add((now - 3_600_000, "old"));

        await ListensPull.RunAsync(_db, "desk", store.Read, pagesPerPass: 5, CancellationToken.None);
        now += 3_600_000;
        await ListensPull.RunAsync(_db, "desk", store.Read, pagesPerPass: 5, CancellationToken.None);

        Assert.True(store.Asked[^1].Since > 1_790_000_000_000 - 3_600_000);
    }
}
