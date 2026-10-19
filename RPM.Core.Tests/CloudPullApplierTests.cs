using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Core.Models;

namespace RPM.Core.Tests;

/// <summary>
/// A correction made on another of the user's machines, applied on this one: the file's own tags, the
/// row, and the stamps that stop the two machines pushing their wording back and forth for ever.
///
/// <para>Real files and a real mirror. The tag writer is passed in, so a test can simulate a file
/// that will not open without creating one on disk.</para>
///
/// <para>Nothing here moves a file, and several of these tests check that. If a pull re-filed the
/// record to the store's path, an old layout stored in the cloud would undo this machine's naming
/// template on every run.</para>
/// </summary>
public class CloudPullApplierTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    /// <summary>What the applier was asked to write into the file, in order.</summary>
    private readonly List<(string Path, TrackInfo Info)> _written = [];

    private bool _tagWriteFails;

    public CloudPullApplierTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"pull-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private CloudPullApplier Applier() => new(_db, _root, WriteTags);

    private void WriteTags(string path, TrackInfo info)
    {
        if (_tagWriteFails) throw new IOException("the file is open in something else");
        _written.Add((path, info));

        // A real tag write changes the file's mtime and usually its length. This is why the row's own
        // file facts are read again afterwards.
        File.AppendAllText(path, "tags");
    }

    /// <summary>A row with a file behind it, as the library holds it.</summary>
    private LibraryTrack Row(string relativePath, string rpmId, string title = "Old Title")
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "audio");

        var cut = relativePath.LastIndexOf('/');
        var track = new LibraryTrack
        {
            RelativePath = relativePath,
            Folder = cut < 0 ? "" : relativePath[..cut],
            FileName = cut < 0 ? relativePath : relativePath[(cut + 1)..],
            Extension = Path.GetExtension(relativePath).ToLowerInvariant(),
            SizeBytes = new FileInfo(full).Length,
            ModifiedUtcTicks = File.GetLastWriteTimeUtc(full).Ticks,
            RpmId = rpmId,
            Title = title,
            Artist = "Band",
            Album = "Record",
            CloudState = CloudState.Synced,
        };

        _db.Upsert(track);
        return _db.Query().First(t => t.RelativePath == relativePath);
    }

    /// <summary>A row the catalogue put here, with no file behind it.</summary>
    private LibraryTrack CloudOnlyRow(string relativePath, string rpmId)
    {
        var cut = relativePath.LastIndexOf('/');
        _db.Upsert(new LibraryTrack
        {
            RelativePath = relativePath,
            Folder = cut < 0 ? "" : relativePath[..cut],
            FileName = cut < 0 ? relativePath : relativePath[(cut + 1)..],
            Extension = Path.GetExtension(relativePath).ToLowerInvariant(),
            RpmId = rpmId,
            Title = "Old Title",
            CloudState = CloudState.CloudOnly,
        });
        return _db.Query().First(t => t.RelativePath == relativePath);
    }

    private static CloudTrack Cloud(
        string rpmId, string? relativePath, string title = "New Title", long updatedAt = 5_000)
        => new(
            RpmId: rpmId,
            Sha256: rpmId + "-sha",
            RelativePath: relativePath,
            OriginalSize: 5_000_000,
            Title: title,
            Artist: "Band",
            Album: "Record",
            AddedAt: 1000,
            Tags: new CloudTrackTags(DurationSeconds: 200, TrackNumber: 1, AlbumArtist: "Band"),
            UpdatedAt: updatedAt);

    private LibraryTrack Reread(string relativePath) =>
        _db.Query().First(t => string.Equals(t.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The file is the record. If a row is corrected while its file still has the old tags, the next
    /// sync reads the old tags from the disk and undoes it. The correction would appear and then
    /// disappear a few minutes later.
    /// </summary>
    [Fact]
    public void A_correction_is_written_into_the_file_before_the_row()
    {
        var row = Row("Band/Record/01 - Song.flac", "id-1");

        var outcome = Applier().Apply(new CloudPull(Cloud("id-1", row.RelativePath), row));

        Assert.Equal(PullOutcome.Applied, outcome);
        Assert.Equal("New Title", Assert.Single(_written).Info.Title);
        Assert.Equal("New Title", Reread(row.RelativePath).Title);
    }

    /// <summary>Nothing is stamped, so the same correction is offered again next run rather than being
    /// lost or half-applied.</summary>
    [Fact]
    public void A_file_that_cannot_be_written_keeps_its_row_exactly_as_it_was()
    {
        var row = Row("Band/Record/01 - Song.flac", "id-1");
        _tagWriteFails = true;

        var outcome = Applier().Apply(new CloudPull(Cloud("id-1", row.RelativePath), row));

        Assert.Equal(PullOutcome.TagsRefused, outcome);
        Assert.Equal("Old Title", Reread(row.RelativePath).Title);
    }

    /// <summary>A track that is only in the cloud has no file here, so only the row changes and
    /// nothing is written to disk.</summary>
    [Fact]
    public void A_cloud_only_track_takes_the_correction_into_its_row_alone()
    {
        var row = CloudOnlyRow("Band/Record/02 - Elsewhere.flac", "id-2");

        var outcome = Applier().Apply(new CloudPull(Cloud("id-2", "Band/Record/02 - Renamed.flac"), row));

        Assert.Equal(PullOutcome.Applied, outcome);
        Assert.Empty(_written);

        // The wording is applied and the row keeps its path. The store does not re-file a row with no
        // file behind it, the same as a row with a file.
        var back = Reread(row.RelativePath);
        Assert.Equal("New Title", back.Title);
        Assert.Equal("Band/Record/02 - Elsewhere.flac", back.RelativePath);
    }

    /// <summary>
    /// The row's own facts about the FILE stay this machine's: a size and an mtime describe the thing
    /// on this disk, and the tag write has just changed both.
    /// </summary>
    [Fact]
    public void The_rows_size_and_mtime_describe_the_file_on_this_disk_afterwards()
    {
        var row = Row("Band/Record/01 - Song.flac", "id-1");

        Applier().Apply(new CloudPull(Cloud("id-1", row.RelativePath), row));

        var full = Path.Combine(_root, row.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var back = Reread(row.RelativePath);
        Assert.Equal(new FileInfo(full).Length, back.SizeBytes);
        Assert.Equal(File.GetLastWriteTimeUtc(full).Ticks, back.ModifiedUtcTicks);
        Assert.NotEqual(row.SizeBytes, back.SizeBytes);   // the write really did change the file
    }

    /// <summary>
    /// In step with the store on both clocks. Stamped after the file is written, so the mtime the
    /// write produces is not read as an edit made here and pushed straight back up.
    /// </summary>
    [Fact]
    public void A_correction_taken_in_is_not_pushed_straight_back_out()
    {
        var row = Row("Band/Record/01 - Song.flac", "id-1");
        var cloud = Cloud("id-1", row.RelativePath, updatedAt: 9_876);

        Applier().Apply(new CloudPull(cloud, row));

        var back = Reread(row.RelativePath);
        Assert.Equal(9_876, back.CloudUpdatedAt);

        // This is what the stamps are for. The file's mtime changed when the tags were written, which
        // would otherwise read as an edit made HERE. So the row must say this library has changed
        // nothing, and the planner must not offer the same correction again on the next pass.
        Assert.False(CloudSyncPlan.ChangedHere(back));
        Assert.Empty(CloudSyncPlan.Make([back], [cloud], limit: 10).Pull);
    }

    /// <summary>The mirror refusing the row leaves everything as it was, the file included.</summary>
    [Fact]
    public void A_row_the_mirror_will_not_take_leaves_the_file_where_the_row_says_it_is()
    {
        // A file on disk with no row behind it, so the row write has nothing to update.
        var full = Path.Combine(_root, "Band", "Old Record", "01 - Song.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "audio");
        var row = new LibraryTrack
        {
            RelativePath = "Band/Old Record/01 - Song.flac",
            Folder = "Band/Old Record",
            FileName = "01 - Song.flac",
            Extension = ".flac",
            RpmId = "id-1",
            Title = "Old Title",
        };

        var outcome = Applier().Apply(new CloudPull(Cloud("id-1", "Band/New Record/01 - Song.flac"), row));

        Assert.Equal(PullOutcome.RowRefused, outcome);
        Assert.True(File.Exists(full));
        Assert.False(File.Exists(Path.Combine(_root, "Band", "New Record", "01 - Song.flac")));
    }

    /// <summary>
    /// A record re-filed on another machine is NOT re-filed here, and this is the test that says so.
    ///
    /// <para>Where a file sits is decided by this machine's naming template. If the pull took the
    /// store's path, an old path stored in the cloud would undo a re-file the user asked for, one row
    /// per track, on every run. The wording is still applied; the filing stays this machine's
    /// choice.</para>
    /// </summary>
    [Fact]
    public void A_record_re_filed_elsewhere_is_left_where_this_library_keeps_it()
    {
        var row = Row("Band/Old Record/01 - Song.flac", "id-1");
        const string theirs = "Band/New Record/01 - Song.flac";

        var outcome = Applier().Apply(new CloudPull(Cloud("id-1", theirs), row));

        Assert.Equal(PullOutcome.Applied, outcome);

        var back = Reread(row.RelativePath);
        Assert.Equal("New Title", back.Title);
        Assert.Equal("Band/Old Record", back.Folder);
        Assert.Equal("01 - Song.flac", back.FileName);

        // Nothing on the disk moved, and no row was minted at the store's path.
        Assert.True(File.Exists(Path.Combine(_root, "Band", "Old Record", "01 - Song.flac")));
        Assert.False(File.Exists(Path.Combine(_root, "Band", "New Record", "01 - Song.flac")));
        Assert.DoesNotContain(_db.Query(), t => t.RelativePath == theirs);
    }

    /// <summary>The store saying nothing about where the track lives is not an instruction to move it
    /// to nowhere.</summary>
    [Fact]
    public void A_catalog_row_with_no_path_leaves_the_file_where_it_is()
    {
        var row = Row("Band/Record/01 - Song.flac", "id-1");

        var outcome = Applier().Apply(new CloudPull(Cloud("id-1", relativePath: null), row));

        Assert.Equal(PullOutcome.Applied, outcome);
        Assert.Equal(row.RelativePath, Reread(row.RelativePath).RelativePath);
    }
}
