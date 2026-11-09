using Microsoft.Data.Sqlite;
using RPM.Core.Library;
using RPM.Core.Models;

namespace RPM.Core.Tests;

/// <summary>
/// Plays brought by an import from another app. Each source library is a device of its own
/// ("import:musicbee:..."), so its plays add to the ones made here and a second import replaces them.
/// </summary>
public class ImportedPlaysTests : IDisposable
{
    private const string MusicBee = "import:musicbee:0a1b2c3d4e5f6a7b";
    private const string Plex = "import:plex:machine-1234";

    private readonly string _root;
    private readonly LibraryDatabase _db;

    public ImportedPlaysTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmimportplays-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private LibraryTrack Add(string name, string rpmId)
    {
        _db.Upsert(new LibraryTrack
        {
            RelativePath = $"a/{name}.mp3", Folder = "a", FileName = $"{name}.mp3", Extension = ".mp3",
            SizeBytes = 100, ModifiedUtcTicks = 1, RpmId = rpmId, AddedUtcTicks = 5_000,
        });
        return _db.FindByRelativePath($"a/{name}.mp3")!;
    }

    private void PlayHere(string name, long atUtcTicks) =>
        _db.NoteListen(ListenEntry.At(new DateTimeOffset(new DateTime(atUtcTicks, DateTimeKind.Utc)), $"a/{name}.mp3", 200, 200)!);

    private LibraryTrack Row(string name) => _db.FindByRelativePath($"a/{name}.mp3")!;

    private static LibraryDatabase.TrackPlayRow Imported(string rpmId, string device, int plays, long last) => new(rpmId, device, plays, last, 0);

    [Fact]
    public void Imported_plays_add_to_own_plays()
    {
        Add("one", "id-1");
        PlayHere("one", 638_000_000_000_000_000);

        _db.ApplyImportedPlays(MusicBee, [Imported("id-1", MusicBee, 10, 637_000_000_000_000_000)]);

        Assert.Equal(11, Row("one").Plays);
        Assert.Equal(638_000_000_000_000_000, Row("one").LastPlayedUtcTicks);
    }

    [Fact]
    public void Second_import_replaces_not_adds()
    {
        Add("one", "id-1");
        PlayHere("one", 638_000_000_000_000_000);

        _db.ApplyImportedPlays(MusicBee, [Imported("id-1", MusicBee, 10, 1)]);
        _db.ApplyImportedPlays(MusicBee, [Imported("id-1", MusicBee, 12, 2)]);

        Assert.Equal(13, Row("one").Plays);
    }

    [Fact]
    public void Imported_last_played_moves_the_track_later_only()
    {
        Add("one", "id-1");
        _db.ApplyImportedPlays(MusicBee, [Imported("id-1", MusicBee, 3, 639_000_000_000_000_000)]);

        Assert.Equal(639_000_000_000_000_000, Row("one").LastPlayedUtcTicks);
    }

    [Fact]
    public void Import_rows_are_sent_per_device()
    {
        Add("one", "id-1");
        Add("two", "id-2");
        PlayHere("one", 638_000_000_000_000_000);
        _db.ApplyImportedPlays(MusicBee, [Imported("id-1", MusicBee, 10, 1)]);
        _db.ApplyImportedPlays(Plex, [Imported("id-2", Plex, 4, 1)]);

        var due = _db.ImportPlaysToSend(200);

        Assert.Equal([(MusicBee, "id-1", 10), (Plex, "id-2", 4)], due.Select(r => (r.Device, r.RpmId, r.Plays)).OrderBy(r => r.Device));
        _db.NotePlaysSent(MusicBee, [.. due.Where(r => r.Device == MusicBee)]);
        Assert.Equal([Plex], _db.ImportPlaysToSend(200).Select(r => r.Device));
    }

    [Fact]
    public void Pulled_import_rows_are_not_sent_again()
    {
        Add("one", "id-1");

        // Another computer ran the import and sent the rows; this one pulls them as foreign rows.
        _db.ApplyForeignPlays("this-install", [Imported("id-1", MusicBee, 10, 1)]);

        Assert.Equal(10, Row("one").Plays);
        Assert.Empty(_db.ImportPlaysToSend(200));
    }

    [Fact]
    public void Unsent_reimport_is_not_overwritten_by_an_older_pull()
    {
        Add("one", "id-1");
        _db.ApplyImportedPlays(MusicBee, [Imported("id-1", MusicBee, 10, 1)]);
        _db.NotePlaysSent(MusicBee, _db.ImportPlaysToSend(200));
        _db.ApplyImportedPlays(MusicBee, [Imported("id-1", MusicBee, 12, 2)]);

        // The store still lists the first import's 10, and this machine reads it back.
        _db.ApplyForeignPlays("this-install", [Imported("id-1", MusicBee, 10, 1)]);

        Assert.Equal(12, Row("one").Plays);
        Assert.Equal([12], _db.ImportPlaysToSend(200).Select(r => r.Plays));
    }

    [Fact]
    public void Set_added_earlier_moves_only_earlier()
    {
        var track = Add("one", "id-1");

        _db.SetAddedEarlier(track.Id, 3_000);
        _db.SetAddedEarlier(track.Id, 4_000);
        _db.SetAddedEarlier(track.Id, 0);

        Assert.Equal(3_000, Row("one").AddedUtcTicks);
    }
}
