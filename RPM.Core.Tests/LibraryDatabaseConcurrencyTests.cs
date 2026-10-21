using System.Diagnostics;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The shelf must answer reads while the sync is writing. The mirror reads on its own SQLite
/// connection, separate from the writer, and WAL mode lets those reads run during a write. If reads
/// and writes shared one connection, the album wall's queries would wait behind the sync's batch and
/// the whole shelf would appear at once when the pass finished.
/// </summary>
[Collection(TimingCollection.Name)]
public class LibraryDatabaseConcurrencyTests : IDisposable
{
    private readonly string _root;

    public LibraryDatabaseConcurrencyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmdb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static List<LibraryTrack> Rows(int count) =>
        Enumerable.Range(0, count).Select(i => new LibraryTrack
        {
            RelativePath = $"Artist {i / 20}/album/{i}.mp3",
            Folder = $"Artist {i / 20}/album",
            FileName = $"{i}.mp3",
            Extension = ".mp3",
            SizeBytes = 1000,
            ModifiedUtcTicks = 1,
            DurationSeconds = 200,
            Title = $"track {i}",
            Artist = $"Artist {i / 20}",
            Album = "album",
        }).ToList();

    [Fact]
    public void The_wall_can_ask_while_a_sync_writes()
    {
        using var db = new LibraryDatabase(MirrorLocation.InFolder(_root));
        var rows = Rows(40_000);
        db.UpsertMany(rows);

        var writing = Task.Run(() => db.UpsertMany(rows));   // what a sync pass commits at the end

        // Let the writer take its lock and get well into the batch.
        var spin = Stopwatch.StartNew();
        while (spin.ElapsedMilliseconds < 150 && !writing.IsCompleted)
            Thread.Sleep(10);

        var read = Stopwatch.StartNew();
        var folders = db.QueryFolderTrackCounts(null);       // what the album wall asks
        read.Stop();

        var readFinishedFirst = !writing.IsCompleted;
        writing.Wait();

        Assert.NotEmpty(folders);

        // On a typical machine the query takes about 1 ms alone, and about 1770 ms if it waits behind
        // this write. The limit is far above the first and far below the second, so the test fails on
        // a regression and not on a slow machine.
        Assert.True(read.ElapsedMilliseconds < 500,
                    $"the wall's query waited {read.ElapsedMilliseconds}ms behind the writer");
        Assert.True(readFinishedFirst,
                    "the read only came back after the write finished — it is still queued behind it");
    }

    /// <summary>
    /// The write gate's turn count tells a cache whether this collection may have changed, so it must
    /// increase for a write and stay the same for a read. Sharing uses it to decide when a listing is
    /// out of date: if the count did not increase when a row changed, a borrower would keep an
    /// out-of-date shelf, and neither machine would report it.
    /// </summary>
    [Fact]
    public void The_write_turn_count_steps_for_a_write_and_not_for_a_read()
    {
        using var db = new LibraryDatabase(MirrorLocation.InFolder(_root));

        var start = db.WriteTurns;
        db.UpsertMany(Rows(10));
        var afterWrite = db.WriteTurns;

        Assert.True(afterWrite > start, $"a write left the count at {afterWrite}");

        _ = db.Query();
        _ = db.QueryFolderTrackCounts(null);
        _ = db.Count;

        Assert.Equal(afterWrite, db.WriteTurns);

        db.UpsertMany(Rows(10));
        Assert.True(db.WriteTurns > afterWrite, "a second write did not step the count");
    }

    /// <summary>A read on the other connection must see what the writer has committed. Otherwise the
    /// shelf would show data from before the import that just finished.</summary>
    [Fact]
    public void A_read_sees_what_the_writer_committed()
    {
        using var db = new LibraryDatabase(MirrorLocation.InFolder(_root));
        Assert.Equal(0, db.Count);

        db.UpsertMany(Rows(40));
        Assert.Equal(40, db.Count);

        db.Remove("Artist 0/album/0.mp3");
        Assert.Equal(39, db.Count);
        Assert.Null(db.FindByRelativePath("Artist 0/album/0.mp3"));
    }

    /// <summary>
    /// A generated list must be readable while a sync writes, for the same reason as the wall: the
    /// thread that reads it is the drawing thread.
    ///
    /// <para><b>What this checks.</b> <c>SessionMoods</c> is a PROPERTY and reads a cached field. It
    /// must not take the write gate to do that, because a sync commit holds the gate for its whole
    /// batch. Otherwise a playlist row being drawn, or a peer's catalogue being built, would wait for
    /// the entire import before it could check whether the list already exists. The three
    /// whole-table reads in the property are outside the lock on purpose, and that only helps if
    /// reaching them does not wait behind forty thousand rows.</para>
    /// </summary>
    [Fact]
    public void A_generated_list_can_be_cut_while_a_sync_writes()
    {
        using var db = new LibraryDatabase(MirrorLocation.InFolder(_root));
        var rows = Rows(40_000);
        db.UpsertMany(rows);

        var writing = Task.Run(() => db.UpsertMany(rows));   // what a sync pass commits at the end

        // Let the writer take its lock and get well into the batch.
        var spin = Stopwatch.StartNew();
        while (spin.ElapsedMilliseconds < 150 && !writing.IsCompleted)
            Thread.Sleep(10);

        var cut = Stopwatch.StartNew();
        var moods = db.SessionMoods;                          // what a playlist row asks for
        cut.Stop();

        var cutFinishedFirst = !writing.IsCompleted;
        writing.Wait();

        Assert.NotNull(moods);
        Assert.True(cut.ElapsedMilliseconds < 500,
                    $"the generated list waited {cut.ElapsedMilliseconds}ms behind the writer");
        Assert.True(cutFinishedFirst,
                    "the cut only came back after the write finished — it is queued behind it");
    }

    /// <summary>The same for the full set of published lists, which a peer's catalogue is built from.
    /// It can be built while somebody on this machine is looking at a borrowed shelf.</summary>
    [Fact]
    public void The_published_lists_can_be_built_while_a_sync_writes()
    {
        using var db = new LibraryDatabase(MirrorLocation.InFolder(_root));
        var rows = Rows(40_000);
        db.UpsertMany(rows);

        var writing = Task.Run(() => db.UpsertMany(rows));

        var spin = Stopwatch.StartNew();
        while (spin.ElapsedMilliseconds < 150 && !writing.IsCompleted)
            Thread.Sleep(10);

        var built = Stopwatch.StartNew();
        var lists = db.SessionMoodLists;
        built.Stop();

        var builtFinishedFirst = !writing.IsCompleted;
        writing.Wait();

        Assert.NotNull(lists);
        Assert.True(built.ElapsedMilliseconds < 500,
                    $"the published cut waited {built.ElapsedMilliseconds}ms behind the writer");
        Assert.True(builtFinishedFirst,
                    "the cut only came back after the write finished — it is queued behind it");
    }

    /// <summary>The same for the total that a tree row and a genre page show.</summary>
    [Fact]
    public void A_generated_lists_total_can_be_read_while_a_sync_writes()
    {
        using var db = new LibraryDatabase(MirrorLocation.InFolder(_root));
        var rows = Rows(40_000);
        db.UpsertMany(rows);

        var writing = Task.Run(() => db.UpsertMany(rows));

        var spin = Stopwatch.StartNew();
        while (spin.ElapsedMilliseconds < 150 && !writing.IsCompleted)
            Thread.Sleep(10);

        var asked = Stopwatch.StartNew();
        db.SmartPlaylistTotals(
            SmartPlaylistId.WholeShelf(SmartPlaylistKind.FavoriteTracks), DateTime.UtcNow.Ticks);
        asked.Stop();

        var askedFinishedFirst = !writing.IsCompleted;
        writing.Wait();

        Assert.True(asked.ElapsedMilliseconds < 500,
                    $"the list's total waited {asked.ElapsedMilliseconds}ms behind the writer");
        Assert.True(askedFinishedFirst,
                    "the total only came back after the write finished — it is queued behind it");
    }
}
