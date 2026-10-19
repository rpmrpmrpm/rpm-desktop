using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The records that "bring my music back" depends on.
///
/// <para>
/// A cloud-only row means two different things, depending on one column. If this install uploaded
/// the track and its file was then removed, the row belongs to this machine and can be downloaded
/// again. If the row came from the catalogue, the track belongs to another of the user's computers
/// and was never here. Bringing files back must offer the first set and never the second. Otherwise
/// a laptop signed in to the same account would try to download the desktop's whole collection.
/// </para>
/// </summary>
public class LibraryDatabaseOffloadTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public LibraryDatabaseOffloadTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"offload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private LibraryTrack Add(string relativePath, long sizeBytes = 5_000_000)
    {
        var slash = relativePath.LastIndexOf('/');
        _db.Upsert(new LibraryTrack
        {
            RelativePath = relativePath,
            Folder = slash < 0 ? "" : relativePath[..slash],
            FileName = slash < 0 ? relativePath : relativePath[(slash + 1)..],
            Extension = Path.GetExtension(relativePath),
            SizeBytes = sizeBytes,
            ModifiedUtcTicks = 1,
        });

        return _db.FindByRelativePath(relativePath)!;
    }

    /// <summary>A track this machine uploaded and whose local file was then removed.</summary>
    private LibraryTrack Offloaded(string relativePath, long sizeBytes = 5_000_000)
    {
        var track = Add(relativePath, sizeBytes);
        _db.SeedSynced(track.Id, "sha-" + track.Id, sizeBytes, 1);
        _db.MarkOffloaded(track.Id);
        return _db.FindByRelativePath(relativePath)!;
    }

    /// <summary>A track this machine uploaded and still has on disk.</summary>
    private LibraryTrack Synced(string relativePath, long sizeBytes = 5_000_000)
    {
        var track = Add(relativePath, sizeBytes);
        _db.SeedSynced(track.Id, "sha-" + track.Id, sizeBytes, 1);
        return _db.FindByRelativePath(relativePath)!;
    }

    private static CloudTrack Cloud(string rpmId, string relativePath, long size = 7_000_000) => new(
        RpmId: rpmId,
        Sha256: rpmId + "-sha",
        RelativePath: relativePath,
        OriginalSize: size,
        Title: "Song",
        Artist: "Band",
        Album: "Record",
        AddedAt: 1000);

    [Fact]
    public void A_track_whose_file_has_gone_is_counted_as_this_machines_to_fetch_back()
    {
        Offloaded("Band/Record/01 One.flac", 4_000_000);

        var gone = _db.OffloadedHereTally();
        Assert.Equal(1, gone.Count);

        // The row's size is kept through the offload. It is the only record of how much space was
        // freed, because the file is not there to measure any more.
        Assert.Equal(4_000_000, gone.Bytes);
    }

    [Fact]
    public void A_row_adopted_from_the_catalog_is_not_this_machines_to_bring_back()
    {
        _db.MirrorCloudLibrary([Cloud("gid-1", "Other/Record/01 Theirs.flac")]);

        // It is cloud-only, it is on the wall, and it plays. But this computer never had the file, so
        // "bring my files back" must not include it.
        Assert.Equal(0, _db.OffloadedHereTally().Count);
        Assert.Empty(_db.QueryOffloadedHere());
    }

    [Fact]
    public void The_two_kinds_of_cloud_only_row_are_told_apart_in_one_library()
    {
        Offloaded("Band/Record/01 Mine.flac", 3_000_000);
        _db.MirrorCloudLibrary([Cloud("gid-1", "Other/Record/01 Theirs.flac")]);

        var mine = _db.QueryOffloadedHere();

        Assert.Single(mine);
        Assert.Equal("Band/Record/01 Mine.flac", mine[0].RelativePath);
        Assert.Equal(3_000_000, _db.OffloadedHereTally().Bytes);
    }

    [Fact]
    public void What_was_never_here_is_counted_apart_from_what_this_machine_gave_up()
    {
        Offloaded("Band/Record/01 Mine.flac", 3_000_000);
        _db.MirrorCloudLibrary([
            Cloud("gid-1", "Other/Record/01 Theirs.flac", 7_000_000),
            Cloud("gid-2", "Other/Record/02 Theirs.flac", 8_000_000),
        ]);

        // The two counts behind the cloud page's two sentences. They must be separate. With one count,
        // a user with a second install would see "every track in your library is on this computer"
        // when this machine had offloaded nothing but the wall showed two tracks it never had.
        Assert.Equal(1, _db.OffloadedHereTally().Count);
        Assert.Equal(2, _db.FromElsewhereTally().Count);
        Assert.Equal(15_000_000, _db.FromElsewhereTally().Bytes);
    }

    [Fact]
    public void A_library_that_has_offloaded_nothing_can_still_be_holding_nothing()
    {
        Add("Band/Record/01 One.flac");
        _db.MirrorCloudLibrary([Cloud("gid-1", "Other/Record/01 Theirs.flac")]);

        Assert.False(_db.OffloadedHereTally().Any);   // none of this machine's own files is missing
        Assert.True(_db.FromElsewhereTally().Any);    // but not every track is on this machine
    }

    [Fact]
    public void An_empty_library_tallies_to_nothing_rather_than_to_null()
    {
        Assert.Equal(TrackTally.Nothing, _db.OffloadedHereTally());
        Assert.Equal(TrackTally.Nothing, _db.FromElsewhereTally());
        Assert.False(TrackTally.Nothing.Any);
    }
}
