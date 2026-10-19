using RPM.Core.Library;
using RPM.Core.Models;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using RPM.Desktop.Services.Library;

namespace RPM.Desktop.Tests;

/// <summary>
/// The import queue's chrome (the header's ticked count, the status line, the tag panel and the Import
/// key) is re-read from the store whenever <see cref="QueueTreeSource.Changed"/> is raised. Removing every
/// row with the remove key must raise it too. Otherwise the four parts keep describing rows that are gone,
/// until some unrelated click asks again. This test checks that replacing the rows on display raises the
/// event after the store has the change, whichever path changed them.
/// </summary>
public class QueueChromeFollowsTheRowsTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "rpm-queue-chrome-tests", Guid.NewGuid().ToString("n"));

    private readonly LibraryService _library;
    private readonly QueueTreeSource _queue;

    public QueueChromeFollowsTheRowsTests()
    {
        Directory.CreateDirectory(_dir);
        _library = new LibraryService(TestSessions.SignedOut());
        _library.Open(_dir);

        // Neither is reached by inserting, removing or rebuilding: playback is for the rows' play glyph
        // when a page is fetched, and the dispatcher is held for the grid.
        _queue = new QueueTreeSource(_library, playback: null!, dispatcher: null!,
            unknown: ("Unknown artist", "Unknown album"));
    }

    public void Dispose()
    {
        _queue.Dispose();
        _library.Dispose();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static QueueTrack Row(int n)
    {
        var info = new TrackInfo
        {
            FilePath = $"D:/New music/track0{n}.mp3",
            Extension = ".mp3",
            DurationSeconds = 200,
            TrackNumber = (uint)n,
        };
        return new QueueTrack
        {
            Source = "New music",
            Folder = "New music",
            FileName = $"track0{n}.mp3",
            IsChecked = true,
            Current = info,
            Wanted = info,
        };
    }

    private async Task<IReadOnlyList<long>> FiveTickedRows()
    {
        await _queue.InsertAsync([Row(1), Row(2), Row(3), Row(4), Row(5)]);
        return [.. _library.Db.Queue(QueueFilter.None).CheckedRows().Select(r => r.Id)];
    }

    [Fact]
    public async Task Removing_every_row_tells_the_chrome_and_it_reads_an_empty_queue()
    {
        var ids = await FiveTickedRows();
        Assert.Equal(5, ids.Count);

        // What a chrome pass would read, at the moment it is asked for.
        var seen = new List<(int Total, int Ticked)>();
        _queue.Changed += () => seen.Add((_queue.TotalCount, _queue.CheckedCount));

        await _queue.RemoveAsync(ids);

        Assert.NotEmpty(seen);
        Assert.Equal((0, 0), seen[^1]);
    }

    [Fact]
    public async Task Removing_some_rows_tells_the_chrome_the_count_that_is_left()
    {
        var ids = await FiveTickedRows();

        var seen = new List<int>();
        _queue.Changed += () => seen.Add(_queue.CheckedCount);

        await _queue.RemoveAsync([ids[0], ids[1]]);

        Assert.Equal(3, seen[^1]);
    }

    [Fact]
    public async Task Clearing_the_queue_tells_the_chrome()
    {
        await FiveTickedRows();

        var seen = new List<int>();
        _queue.Changed += () => seen.Add(_queue.TotalCount);

        await _queue.ClearAsync();

        Assert.Equal(0, seen[^1]);
    }

    [Fact]
    public async Task Adding_rows_tells_the_chrome()
    {
        var seen = new List<int>();
        _queue.Changed += () => seen.Add(_queue.TotalCount);

        await _queue.InsertAsync([Row(1), Row(2)]);

        Assert.Equal(2, seen[^1]);
    }
}
