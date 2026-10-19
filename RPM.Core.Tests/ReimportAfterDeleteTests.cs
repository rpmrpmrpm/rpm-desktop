using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// Deleting a record from the library and importing it again.
///
/// <para>A record that is also in the cloud leaves rows behind when it is deleted here. Its cloud
/// copy stays, so the next catalogue pass ADOPTS it back: a row at the same library path saying
/// "cloud only, no file here". Importing the files again upserts onto that row. The upsert leaves
/// the cloud columns alone, so without special handling the track would stay adopted and cloud-only
/// forever: the files are filed, the shelf still says they are not here, and the import looks as
/// if it did nothing.</para>
/// </summary>
public class ReimportAfterDeleteTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public ReimportAfterDeleteTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"reimport-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose() { _db.Dispose(); try { Directory.Delete(_root, true); } catch { } }

    private static CloudTrack Cloud(string rpmId, string relativePath) => new(
        RpmId: rpmId,
        Sha256: rpmId + "-sha",
        RelativePath: relativePath,
        OriginalSize: 5_000_000,
        Title: "Apocalypse Please",
        Artist: "Muse",
        Album: "Absolution",
        AddedAt: 1000,
        Tags: new CloudTrackTags(DurationSeconds: 200, TrackNumber: 1, AlbumArtist: "Muse"));

    /// <summary>A freshly imported file, as the import mirrors it: read from the disk, knowing nothing
    /// about the cloud.</summary>
    private static LibraryTrack Imported(string relativePath, string? rpmId) => new()
    {
        RelativePath = relativePath,
        Folder = relativePath[..relativePath.LastIndexOf('/')],
        FileName = relativePath[(relativePath.LastIndexOf('/') + 1)..],
        Extension = ".flac",
        SizeBytes = 40_000_000,
        ModifiedUtcTicks = 99,
        DurationSeconds = 200,
        Title = "Apocalypse Please",
        Artist = "Muse",
        AlbumArtist = "Muse",
        Album = "Absolution",
        TrackNumber = 1,
        RpmId = rpmId,
    };

    [Fact]
    public void The_file_coming_back_takes_the_adopted_row_over()
    {
        const string path = "Muse/Absolution/01.flac";
        Assert.Equal(1, _db.MirrorCloudLibrary([Cloud("rpm-1", path)]).Adopted);
        Assert.Equal(1, _db.CountAdopted());

        // The import files the same track back onto the disk, at the path the catalogue claims.
        _db.UpsertMany([Imported(path, "rpm-1")]);

        var row = Assert.Single(_db.Query());
        Assert.False(row.IsOffloaded);                       // there is a file here now
        Assert.Equal(CloudState.Synced, row.CloudState);     // and a copy up there: both
        Assert.Equal(0, _db.CountAdopted());                 // the row is this library's own again
        Assert.Equal(40_000_000, row.SizeBytes);             // and it describes the real file
    }

    [Fact]
    public void A_different_file_at_that_path_is_local_and_nobodys_cloud_copy()
    {
        // Re-imported as a COPY, so the file has a fresh id: the same music, but not the object the
        // catalogue is holding. It is on this disk and that is all this library can say.
        const string path = "Muse/Absolution/01.flac";
        _db.MirrorCloudLibrary([Cloud("rpm-1", path)]);

        _db.UpsertMany([Imported(path, "rpm-2")]);

        var row = Assert.Single(_db.Query());
        Assert.Equal(CloudState.Local, row.CloudState);
        Assert.Equal(0, _db.CountAdopted());
        Assert.Equal("rpm-2", row.RpmId);
    }

    [Fact]
    public void An_ordinary_synced_track_keeps_its_state_through_a_retag()
    {
        // The upsert rule that this must not break: a caller holding a freshly read file knows
        // nothing about the cloud, so it may not reset a synced track.
        const string path = "Muse/Absolution/01.flac";
        _db.UpsertMany([Imported(path, "rpm-1")]);
        var row = Assert.Single(_db.Query());
        _db.SetCloudState(row.Id, CloudState.Synced);

        _db.UpsertMany([Imported(path, "rpm-1") with { Title = "Apocalypse, Please" }]);

        Assert.Equal(CloudState.Synced, Assert.Single(_db.Query()).CloudState);
    }
}
