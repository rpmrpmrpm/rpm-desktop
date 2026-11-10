using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RPM.Core.Nas;

namespace RPM.Core.Tests;

public sealed class LibraryMoveTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "rpm-move-" + Guid.NewGuid().ToString("N"));
    private readonly string _from;
    private readonly string _to;
    private readonly string _journal;

    public LibraryMoveTests()
    {
        _from = Path.Combine(_temp, "local");
        _to = Path.Combine(_temp, "nas");
        _journal = Path.Combine(_temp, "moves", "journal.json");
        for (var i = 1; i <= 6; i++)
            Write($"Artist/Album/0{i}.flac", new string((char)('a' + i), 1000 * i));
        Write(".rpm/library.db", "database");
        Write(".rpm/library.json", "{\"id\":\"old\"}");
        Write(".rpm/covers/pl-1.img", "picture");
        Write(".DS_Store", "x");
        Directory.CreateDirectory(_to);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { }
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_from, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static Dictionary<string, string> Tree(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(root, p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));

    private static void Rename(string from, string to) => File.Move(from, to, overwrite: true);

    private static Task<MoveResult> Run(LibraryMove move, string from, string to, string journal, long free = long.MaxValue) =>
        move.RunAsync(from, to, MoveJournal.Open(journal), free, progress: null, CancellationToken.None);

    [Fact]
    public async Task Copies_every_file_and_verifies_each()
    {
        var result = await Run(LibraryMove.Default, _from, _to, _journal);

        Assert.Equal(7, Assert.IsType<MoveResult.Done>(result).Files);
        var copied = Tree(_to);
        Assert.Equal(Tree(_from)["Artist/Album/06.flac".Replace('/', Path.DirectorySeparatorChar)],
                     copied["Artist/Album/06.flac".Replace('/', Path.DirectorySeparatorChar)]);
        Assert.Contains(Path.Combine(".rpm", "covers", "pl-1.img"), copied.Keys);
        Assert.DoesNotContain(Path.Combine(".rpm", "library.db"), copied.Keys);
        Assert.DoesNotContain(Path.Combine(".rpm", "library.json"), copied.Keys);
        Assert.DoesNotContain(".DS_Store", copied.Keys);
        Assert.DoesNotContain(copied.Keys, k => k.EndsWith(".rpmmove", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Never_changes_or_deletes_the_source()
    {
        var before = Tree(_from);

        await Run(LibraryMove.Default, _from, _to, _journal);

        Assert.Equal(before, Tree(_from));
    }

    [Fact]
    public async Task Copy_failure_midway_does_not_switch_and_resumes()
    {
        var calls = 0;
        var failing = new LibraryMove(async (from, to, ct) =>
        {
            if (++calls == 3)
                throw new IOException("the share went away");
            File.Copy(from, to, overwrite: true);
            await Task.CompletedTask;
        }, Rename);

        var first = await Run(failing, _from, _to, _journal);

        // Two files were copied before the third failed. Files go in sorted order, so the playlist
        // picture under .rpm comes first.
        Assert.IsNotType<MoveResult.Done>(first);
        Assert.Equal(2, Directory.EnumerateFiles(_to, "*", SearchOption.AllDirectories).Count());

        var secondCalls = 0;
        var counting = new LibraryMove(async (from, to, ct) =>
        {
            secondCalls++;
            File.Copy(from, to, overwrite: true);
            await Task.CompletedTask;
        }, Rename);
        var second = await Run(counting, _from, _to, _journal);

        Assert.IsType<MoveResult.Done>(second);
        Assert.Equal(5, secondCalls);
    }

    [Fact]
    public async Task A_copy_that_does_not_match_fails_and_leaves_no_file()
    {
        var corrupting = new LibraryMove(async (from, to, ct) =>
        {
            var bytes = File.ReadAllBytes(from);
            bytes[0] ^= 0xFF;
            await File.WriteAllBytesAsync(to, bytes, ct);
        }, Rename);

        var result = await Run(corrupting, _from, _to, _journal);

        Assert.IsType<MoveResult.Failed>(result);
        Assert.Empty(Directory.EnumerateFiles(_to, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Changed_source_file_after_copy_is_copied_again()
    {
        await Run(LibraryMove.Default, _from, _to, _journal);
        Write("Artist/Album/01.flac", "changed after the first run");

        var calls = 0;
        var counting = new LibraryMove(async (from, to, ct) =>
        {
            calls++;
            File.Copy(from, to, overwrite: true);
            await Task.CompletedTask;
        }, Rename);
        await Run(counting, _from, _to, _journal);

        Assert.Equal(1, calls);
        Assert.Equal("changed after the first run", File.ReadAllText(Path.Combine(_to, "Artist", "Album", "01.flac")));
    }

    [Fact]
    public async Task No_room_stops_before_copying()
    {
        var result = await Run(LibraryMove.Default, _from, _to, _journal, free: 100);

        Assert.IsType<MoveResult.NoRoom>(result);
        Assert.Empty(Directory.EnumerateFiles(_to, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_share_that_refuses_the_rename_still_gets_checked_copies()
    {
        var refusing = new LibraryMove(
            async (from, to, ct) => { File.Copy(from, to, overwrite: true); await Task.CompletedTask; },
            (_, _) => throw new IOException("Operation not supported"));

        var result = await Run(refusing, _from, _to, _journal);

        Assert.IsType<MoveResult.Done>(result);
        Assert.Equal(7, Directory.EnumerateFiles(_to, "*", SearchOption.AllDirectories).Count());
        Assert.DoesNotContain(Directory.EnumerateFiles(_to, "*", SearchOption.AllDirectories), f => f.EndsWith(".rpmmove", StringComparison.Ordinal));
    }

    [Fact]
    public void Database_is_carried_with_its_rows_and_never_replaced()
    {
        var source = Path.Combine(_temp, "source.db");
        using (var db = new SqliteConnection($"Data Source={source};Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE plays(id TEXT, n INTEGER); INSERT INTO plays VALUES('RPM1', 42);";
            command.ExecuteNonQuery();
        }

        var target = Path.Combine(_temp, "libraries", "newid", "library.db");
        LibraryMove.CarryDatabase(source, target);

        using (var copy = new SqliteConnection($"Data Source={target};Mode=ReadOnly;Pooling=False"))
        {
            copy.Open();
            using var command = copy.CreateCommand();
            command.CommandText = "SELECT n FROM plays WHERE id='RPM1'";
            Assert.Equal(42L, command.ExecuteScalar());
        }

        var before = File.GetLastWriteTimeUtc(target);
        LibraryMove.CarryDatabase(source, target);
        Assert.Equal(before, File.GetLastWriteTimeUtc(target));
    }
}
