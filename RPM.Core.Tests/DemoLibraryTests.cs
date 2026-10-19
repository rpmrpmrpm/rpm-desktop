using RPM.Core.Library;

namespace RPM.Core.Tests;

public class DemoLibraryTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public DemoLibraryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mddemo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Tracks_are_well_formed_mirror_rows()
    {
        var tracks = DemoLibrary.Tracks();

        Assert.NotEmpty(tracks);
        Assert.All(tracks, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.RelativePath));
            Assert.False(string.IsNullOrWhiteSpace(t.Folder));
            Assert.False(string.IsNullOrWhiteSpace(t.FileName));
            Assert.StartsWith(".", t.Extension);
            Assert.EndsWith(t.FileName, t.RelativePath);      // relative path ends with its own file name
            Assert.StartsWith(t.Folder + "/", t.RelativePath); // and sits under its folder
            Assert.False(string.IsNullOrWhiteSpace(t.Title));
            Assert.False(string.IsNullOrWhiteSpace(t.Artist));
            Assert.True(t.DurationSeconds > 0);
            Assert.True(t.TrackNumber > 0);
        });
    }

    [Fact]
    public void Relative_paths_are_unique()
    {
        var paths = DemoLibrary.Tracks().Select(t => t.RelativePath).ToList();
        Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Tracks_are_deterministic()
    {
        // Two calls must match row for row: a gallery seeded twice looks the same, and a test
        // can rely on the data.
        var a = DemoLibrary.Tracks();
        var b = DemoLibrary.Tracks();
        Assert.Equal(a.Count, b.Count);
        Assert.Equal(a.Select(t => t.RelativePath), b.Select(t => t.RelativePath));
        Assert.Equal(a.Select(t => t.ModifiedUtcTicks), b.Select(t => t.ModifiedUtcTicks));
    }

    [Fact]
    public void Seed_fills_the_mirror_and_reads_back_through_the_real_queries()
    {
        DemoLibrary.Seed(_db);

        var expected = DemoLibrary.Tracks();
        Assert.Equal(expected.Count, _db.Count);

        // The tree grid reads folders through this: every album is its own folder with its
        // tracks under it.
        var folders = _db.QueryFolderTrackCounts();
        var albumCount = expected.Select(t => t.Folder).Distinct().Count();
        Assert.Equal(albumCount, folders.Count);
        Assert.Equal(expected.Count, folders.Sum(f => f.TrackCount));

        // And a page of one album comes back in file-name (track) order.
        var firstFolder = folders[0].Folder;
        var page = _db.QueryFolderPage(firstFolder, 0, 100);
        Assert.NotEmpty(page);
        Assert.All(page, t => Assert.Equal(firstFolder, t.Folder));
    }

    [Fact]
    public void Seed_is_idempotent_by_relative_path()
    {
        DemoLibrary.Seed(_db);
        DemoLibrary.Seed(_db);   // upsert-by-path: seeding twice must not double the rows
        Assert.Equal(DemoLibrary.Tracks().Count, _db.Count);
    }

    [Fact]
    public void LargeTracks_yields_the_requested_count()
    {
        Assert.Equal(5000, DemoLibrary.LargeTracks(5000).Count());
    }

    [Fact]
    public void LargeTracks_relative_paths_are_unique_at_scale()
    {
        // The purpose of the stress set: 200k rows and no two share a path (a duplicate would
        // break the mirror's UNIQUE(relative_path) on insert).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in DemoLibrary.LargeTracks(200_000))
            Assert.True(seen.Add(t.RelativePath), $"duplicate path: {t.RelativePath}");
        Assert.Equal(200_000, seen.Count);
    }

    [Fact]
    public void LargeTracks_seed_into_the_mirror_and_group_into_many_folders()
    {
        _db.UpsertMany(DemoLibrary.LargeTracks(6000));
        Assert.Equal(6000, _db.Count);
        // 6000 / 12 per album = 500 album folders. The tree is stressed by the folder count.
        Assert.Equal(500, _db.QueryFolderTrackCounts().Count);
    }
}
