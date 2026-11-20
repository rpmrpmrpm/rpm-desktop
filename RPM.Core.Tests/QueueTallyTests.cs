using System.Reflection;
using RPM.Core.Library;
using RPM.Core.Models;

namespace RPM.Core.Tests;

/// <summary>
/// The import queue's counts are kept by the write gate: its last step inside each hold counts the queue
/// again when the hold changed it. So a count is answered without taking the gate, and it is never older
/// than the rows.
/// </summary>
public class QueueTallyTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public QueueTallyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mdtally-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static QueueTrack Row(string fileName, bool @checked = false)
    {
        var current = new TrackInfo
        {
            FilePath = $"/music/src/A/{fileName}",
            Extension = Path.GetExtension(fileName),
            DurationSeconds = 200,
            Artist = "Muse",
            Title = fileName,
            Album = "Absolution",
        };
        return new QueueTrack
        {
            Source = "src",
            Folder = "src/A",
            FileName = fileName,
            IsChecked = @checked,
            Current = current,
            Wanted = current,
        };
    }

    [Fact]
    public void A_count_is_answered_while_another_thread_holds_the_write_gate()
    {
        _db.QueueInsertMany([Row("1.mp3", @checked: true), Row("2.mp3")]);

        var gate = (Gate)typeof(LibraryDatabase)
            .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(_db)!;

        using var holding = new ManualResetEventSlim();
        using var letGo = new ManualResetEventSlim();
        var writer = Task.Run(() =>
        {
            using (gate.Take())
            {
                holding.Set();
                letGo.Wait(TimeSpan.FromSeconds(10));
            }
        });
        Assert.True(holding.Wait(TimeSpan.FromSeconds(5)), "the writer never took the gate");

        // Asked of the database, this would wait for the writer above.
        var asked = Task.Run(() => (_db.QueueCount(), _db.QueueCheckedCount()));
        var answered = asked.Wait(TimeSpan.FromSeconds(2));
        letGo.Set();
        writer.Wait(TimeSpan.FromSeconds(10));

        Assert.True(answered, "a count waited for the write gate");
        Assert.Equal((2, 1), asked.Result);
    }

    [Fact]
    public void Every_kind_of_change_moves_the_counts()
    {
        Assert.Equal(0, _db.QueueCount());

        _db.QueueInsertMany([Row("1.mp3"), Row("2.mp3"), Row("3.mp3")]);
        Assert.Equal(3, _db.QueueCount());
        Assert.Equal(0, _db.QueueCheckedCount());

        var rows = _db.WholeQueue.GroupPage("Muse", "Absolution", 0, 10);
        _db.QueueSetChecked(rows[0].Id, true);
        Assert.Equal(1, _db.QueueCheckedCount());
        Assert.Equal(1, _db.QueueImportableCount());

        _db.QueueRemove([rows[0].Id]);
        Assert.Equal(2, _db.QueueCount());
        Assert.Equal(0, _db.QueueCheckedCount());

        _db.QueueClear();
        Assert.Equal(0, _db.QueueCount());
    }
}
