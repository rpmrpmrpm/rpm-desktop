using Microsoft.Data.Sqlite;
using RPM.Core.Import;
using RPM.Core.Library;
using RPM.Core.Models;

namespace RPM.Core.Tests;

/// <summary>
/// Filing a batch of tracks into the library: the step the import queue's import and an import from
/// another app share. It plans by the naming template, copies or moves, writes tags, and records what
/// landed.
/// </summary>
public class LibraryFilerTests : IDisposable
{
    private readonly string _root;
    private readonly string _source;
    private readonly LibraryDatabase _db;

    public LibraryFilerTests()
    {
        var top = Path.Combine(Path.GetTempPath(), $"rpmfiler-{Guid.NewGuid():N}");
        _root = Path.Combine(top, "library");
        _source = Path.Combine(top, "elsewhere");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_source);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private FilingRules Rules(OrganizeMode mode = OrganizeMode.Copy) =>
        new(NamingTemplate.ArtistAlbumNumberedTitle, mode, MergedDiscs: true, _root);

    /// <summary>A second of silence as a WAV file, and the tags the other app gave it.</summary>
    private TrackInfo Wav(string name, uint track, string album = "Album A")
    {
        var path = Path.Combine(_source, name + ".wav");
        const int rate = 8000;
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write("RIFF"u8); w.Write(36 + rate * 2); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(rate * 2); w.Write(new byte[rate * 2]);
        }
        return new TrackInfo
        {
            FilePath = path, Extension = ".wav", Artist = "Artist A", AlbumArtist = "Artist A", Album = album,
            Title = name, TrackNumber = track, Source = MetadataSource.Manual,
        };
    }

    [Fact]
    public void Move_removes_the_emptied_folders_under_the_folder_the_files_came_from()
    {
        var one = Wav("One", 1);
        var album = Path.Combine(_source, "Artist A", "Album A");
        Directory.CreateDirectory(album);
        var nested = Path.Combine(album, "One.wav");
        File.Move(one.FilePath, nested);

        var filed = LibraryFiler.File(_db, [one with { FilePath = nested }], Rules(OrganizeMode.Move), [_source], null, (_, _) => { }, CancellationToken.None);

        Assert.True(filed[0].Result.Success);
        Assert.False(Directory.Exists(Path.Combine(_source, "Artist A")));
        Assert.True(Directory.Exists(_source));
    }

    [Fact]
    public void Copy_leaves_the_source_in_place()
    {
        var one = Wav("One", 1);

        var filed = LibraryFiler.File(_db, [one], Rules(), [], null, (_, _) => { }, CancellationToken.None);

        var result = Assert.Single(filed).Result;
        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(one.FilePath));
        Assert.True(File.Exists(result.DestinationPath));
        Assert.StartsWith(_root, result.DestinationPath);
    }

    [Fact]
    public void Filed_rows_are_upserted_with_tags_from_the_destination()
    {
        var filed = LibraryFiler.File(_db, [Wav("One", 1), Wav("Two", 2)], Rules(), [], null, (_, _) => { }, CancellationToken.None);

        Assert.All(filed, f => Assert.NotNull(f.Row));
        var rows = _db.Query();
        Assert.Equal(["One", "Two"], rows.Select(r => r.Title).Order());
        Assert.All(rows, r => Assert.False(string.IsNullOrEmpty(r.RpmId)));
        Assert.Equal(filed.Select(f => f.Row!.RelativePath).Order(), rows.Select(r => r.RelativePath).Order());
    }

    [Fact]
    public void Same_file_under_two_ids_is_filed_once()
    {
        var one = Wav("One", 1);

        var filed = LibraryFiler.File(_db, [one, one with { Title = "One again" }], Rules(), [], null, (_, _) => { }, CancellationToken.None);

        Assert.Equal(2, filed.Count);
        Assert.Single(_db.Query());
        Assert.Equal(filed[0].Row!.RelativePath, filed[1].Row!.RelativePath);
    }

    [Fact]
    public void Disk_full_stops_and_reports_the_rest()
    {
        var tracks = new[] { Wav("One", 1), Wav("Two", 2), Wav("Three", 3) };
        var calls = 0;

        var filed = LibraryFiler.File(_db, tracks, Rules(), [], null, (_, _) => { }, CancellationToken.None, (s, root, mode) =>
            ++calls == 2
                ? new ApplyResult { SourcePath = s.Track.FilePath, DestinationPath = "x", Success = false, Error = "No space left on device", DiskFull = true }
                : OrganizeApplier.Apply(s, root, mode));

        Assert.Equal(2, calls);
        Assert.Equal([true, false, false], filed.Select(f => f.Result.Success));
        Assert.True(filed[1].Result.DiskFull);
        Assert.True(filed[2].Result.DiskFull);
        Assert.Single(_db.Query());
    }

    [Fact]
    public void Each_file_is_reported_as_it_lands_even_when_the_batch_throws()
    {
        var tracks = new[] { Wav("One", 1), Wav("Two", 2), Wav("Three", 3) };
        var landed = new List<string>();
        var calls = 0;

        Assert.Throws<InvalidOperationException>(() => LibraryFiler.File(_db, tracks, Rules(), [], null,
            (input, _) => landed.Add(input.Title!), CancellationToken.None, (s, root, mode) =>
                ++calls == 3 ? throw new InvalidOperationException("stopped") : OrganizeApplier.Apply(s, root, mode)));

        Assert.Equal(["One", "Two"], landed);
        Assert.Equal(2, _db.Query().Count);
    }

    [Fact]
    public void Progress_reaches_the_total()
    {
        var reports = new List<FilingProgress>();
        LibraryFiler.File(_db, [Wav("One", 1), Wav("Two", 2)], Rules(), [], new SyncProgress<FilingProgress>(reports.Add), (_, _) => { }, CancellationToken.None);

        Assert.Equal((2, 2), (reports[^1].Done, reports[^1].Total));
    }

    [Fact]
    public void Plan_matches_what_file_does()
    {
        var tracks = new[] { Wav("One", 1), Wav("Two", 2) };

        var plan = LibraryFiler.Plan(_db, tracks, NamingTemplate.ArtistAlbumNumberedTitle, merged: true, _root);
        var filed = LibraryFiler.File(_db, tracks, Rules(), [], null, (_, _) => { }, CancellationToken.None);

        Assert.Equal(
            plan.Select(p => Path.Combine(_root, p.ProposedRelativePath.Replace('/', Path.DirectorySeparatorChar))),
            filed.Select(f => f.Result.DestinationPath));
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
