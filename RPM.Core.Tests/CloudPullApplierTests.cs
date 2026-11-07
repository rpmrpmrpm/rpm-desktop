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

    private CloudPullApplier Applier() => new(_db, _root, new PullFiles(WriteTags, ReadTags, LoadArt, WriteAllTags));

    /// <summary>What each file's tags say, as a read of it gives them. A test writes the tags a file starts
    /// with here, and a write with every field as stated replaces them.</summary>
    private readonly Dictionary<string, TrackInfo> _tags = new(StringComparer.Ordinal);

    /// <summary>Each file's embedded front cover.</summary>
    private readonly Dictionary<string, byte[]> _art = new(StringComparer.Ordinal);

    /// <summary>What was written into a file with every field as stated, in order.</summary>
    private readonly List<(string Path, TrackInfo Info)> _writtenWhole = [];

    private TrackInfo ReadTags(string path)
        => _tags.TryGetValue(path, out var info) ? info : new TrackInfo { FilePath = path, Extension = Path.GetExtension(path) };

    private byte[]? LoadArt(string path) => _art.GetValueOrDefault(path);

    private void WriteAllTags(string path, TrackInfo info)
    {
        if (_tagWriteFails) throw new IOException("the file is open in something else");
        _writtenWhole.Add((path, info));
        _tags[path] = info with { PendingArt = null, PendingArtMime = null };
        File.AppendAllText(path, "tags");
    }

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

        var outcome = Applier().Apply(new CloudPull(Cloud("id-1", row.RelativePath), row, false), null);

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

        var outcome = Applier().Apply(new CloudPull(Cloud("id-1", row.RelativePath), row, false), null);

        Assert.Equal(PullOutcome.TagsRefused, outcome);
        Assert.Equal("Old Title", Reread(row.RelativePath).Title);
    }

    /// <summary>A track that is only in the cloud has no file here, so only the row changes and
    /// nothing is written to disk.</summary>
    [Fact]
    public void A_cloud_only_track_takes_the_correction_into_its_row_alone()
    {
        var row = CloudOnlyRow("Band/Record/02 - Elsewhere.flac", "id-2");

        var outcome = Applier().Apply(new CloudPull(Cloud("id-2", "Band/Record/02 - Renamed.flac"), row, false), null);

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

        Applier().Apply(new CloudPull(Cloud("id-1", row.RelativePath), row, false), null);

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

        Applier().Apply(new CloudPull(cloud, row, false), null);

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

        var outcome = Applier().Apply(new CloudPull(Cloud("id-1", "Band/New Record/01 - Song.flac"), row, false), null);

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

        var outcome = Applier().Apply(new CloudPull(Cloud("id-1", theirs), row, false), null);

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

        var outcome = Applier().Apply(new CloudPull(Cloud("id-1", relativePath: null), row, false), null);

        Assert.Equal(PullOutcome.Applied, outcome);
        Assert.Equal(row.RelativePath, Reread(row.RelativePath).RelativePath);
    }

    // ── a pull that read the track's sidecar ──

    private string FullPathOf(LibraryTrack row) => Path.Combine(_root, row.RelativePath.Replace('/', Path.DirectorySeparatorChar));

    private static SidecarStatement Said(string json) => SidecarStatement.Read(json)!;

    private TrackInfo FileSays(LibraryTrack row, Func<TrackInfo, TrackInfo> tags)
    {
        var path = FullPathOf(row);
        var info = tags(new TrackInfo { FilePath = path, Extension = ".flac", Title = "Old Title", Artist = "Band", Album = "Record" });
        _tags[path] = info;
        return info;
    }

    /// <summary>The web app's editors write the fields the row does not hold into the sidecar alone. A
    /// computer holding the file takes them from there.</summary>
    [Fact]
    public void A_field_the_row_does_not_hold_reaches_the_file_from_the_sidecar()
    {
        var row = Row("Band/Record/01 - Song.flac", "id-1");
        FileSays(row, info => info with { Conductor = "Böhm", Publisher = "DG" });

        var outcome = Applier().Apply(
            new CloudPull(Cloud("id-1", row.RelativePath, title: "Old Title"), row, true),
            Said("""{"RelativePath":"x","Extension":".flac","Title":"Old Title","Conductor":"Karajan","Lyrics":"La la"}"""));

        Assert.Equal(PullOutcome.Applied, outcome);
        var (_, written) = Assert.Single(_writtenWhole);
        Assert.Equal("Karajan", written.Conductor);
        Assert.Equal("La la", written.Lyrics);
        Assert.Equal("DG", written.Publisher);     // not stated in the sidecar: the file keeps it
        Assert.Empty(_written);
    }

    /// <summary>A field emptied in the web app is stated as empty in the sidecar, and is emptied in the
    /// file and on the row. A blank on the catalogue row alone could not say this: there it is "not
    /// known".</summary>
    [Fact]
    public void A_field_the_sidecar_states_as_empty_is_cleared_in_the_file_and_the_row()
    {
        var row = Row("Band/Record/01 - Song.flac", "id-1");
        _db.Upsert(Reread(row.RelativePath) with { Genre = "Rock", Year = 1997 });
        row = Reread(row.RelativePath);
        FileSays(row, info => info with { Genre = "Rock", Year = 1997, Conductor = "Karajan" });

        var outcome = Applier().Apply(
            new CloudPull(Cloud("id-1", row.RelativePath, title: "Old Title"), row, true),
            Said("""{"RelativePath":"x","Extension":".flac","Genre":null,"Year":0,"Conductor":null}"""));

        Assert.Equal(PullOutcome.Applied, outcome);
        var (_, written) = Assert.Single(_writtenWhole);
        Assert.Null(written.Genre);
        Assert.Equal(0u, written.Year);
        Assert.Null(written.Conductor);

        var back = Reread(row.RelativePath);
        Assert.Null(back.Genre);
        Assert.Equal(0u, back.Year);
    }

    /// <summary>A sidecar written by an older app leaves out fields it did not know. That is "not known
    /// there", and the file keeps them.</summary>
    [Fact]
    public void A_field_the_sidecar_leaves_out_is_kept()
    {
        var row = Row("Band/Record/01 - Song.flac", "id-1");
        FileSays(row, info => info with { ReleaseCountry = "GB", Conductor = "Karajan" });

        Applier().Apply(
            new CloudPull(Cloud("id-1", row.RelativePath), row, true),
            Said("""{"RelativePath":"x","Extension":".flac","Title":"New Title"}"""));

        var (_, written) = Assert.Single(_writtenWhole);
        Assert.Equal("GB", written.ReleaseCountry);
        Assert.Equal("Karajan", written.Conductor);
        Assert.Equal("New Title", written.Title);
    }

    /// <summary>The catalogue row can be written again after the sidecar, so its values go over the
    /// sidecar's.</summary>
    [Fact]
    public void The_catalogue_rows_values_go_over_the_sidecars()
    {
        var row = Row("Band/Record/01 - Song.flac", "id-1");
        FileSays(row, info => info);

        Applier().Apply(
            new CloudPull(Cloud("id-1", row.RelativePath, title: "Row Title"), row, true),
            Said("""{"RelativePath":"x","Extension":".flac","Title":"Sidecar Title","Conductor":"Karajan"}"""));

        var (_, written) = Assert.Single(_writtenWhole);
        Assert.Equal("Row Title", written.Title);
        Assert.Equal("Karajan", written.Conductor);
        Assert.Equal("Row Title", Reread(row.RelativePath).Title);
    }

    /// <summary>A sidecar that says what the file already says changes nothing on the disk, and the row is
    /// still stamped, so the same pull is not offered again.</summary>
    [Fact]
    public void A_sidecar_that_agrees_with_the_file_writes_nothing_and_is_stamped()
    {
        var row = Row("Band/Record/01 - Song.flac", "id-1");
        FileSays(row, info => info with { Title = "New Title", Conductor = "Karajan", AlbumArtist = "Band", TrackNumber = 1 });

        var outcome = Applier().Apply(
            new CloudPull(Cloud("id-1", row.RelativePath, updatedAt: 7_000), row, true),
            Said("""{"RelativePath":"x","Extension":".flac","Title":"New Title","Conductor":"Karajan"}"""));

        Assert.Equal(PullOutcome.Applied, outcome);
        Assert.Empty(_writtenWhole);
        Assert.Equal(7_000, Reread(row.RelativePath).CloudUpdatedAt);
    }

    [Fact]
    public void The_sidecars_picture_replaces_the_files_and_a_stated_none_removes_it()
    {
        var row = Row("Band/Record/01 - Song.flac", "id-1");
        FileSays(row, info => info with { Title = "New Title", HasArt = true });
        _art[FullPathOf(row)] = [1, 2, 3];

        Applier().Apply(
            new CloudPull(Cloud("id-1", row.RelativePath), row, true),
            Said("""{"RelativePath":"x","Extension":".flac","Art":"CQ==","ArtMime":"image/png"}"""));
        var (_, replaced) = Assert.Single(_writtenWhole);
        Assert.Equal(new byte[] { 9 }, replaced.PendingArt);
        Assert.Equal("image/png", replaced.PendingArtMime);

        _writtenWhole.Clear();
        row = Reread(row.RelativePath);
        Applier().Apply(
            new CloudPull(Cloud("id-1", row.RelativePath), row, true),
            Said("""{"RelativePath":"x","Extension":".flac","Art":null}"""));
        var (_, removed) = Assert.Single(_writtenWhole);
        Assert.Equal([], removed.PendingArt!);
    }

    [Fact]
    public void A_file_that_cannot_be_written_from_the_sidecar_keeps_its_row_and_stamps()
    {
        var row = Row("Band/Record/01 - Song.flac", "id-1");
        FileSays(row, info => info);
        _tagWriteFails = true;

        var outcome = Applier().Apply(
            new CloudPull(Cloud("id-1", row.RelativePath, updatedAt: 7_000), row, true),
            Said("""{"RelativePath":"x","Extension":".flac","Conductor":"Karajan"}"""));

        Assert.Equal(PullOutcome.TagsRefused, outcome);
        var back = Reread(row.RelativePath);
        Assert.Equal("Old Title", back.Title);
        Assert.NotEqual(7_000, back.CloudUpdatedAt);
    }

    [Fact]
    public void A_cloud_only_track_takes_the_sidecars_fields_into_its_row()
    {
        var row = CloudOnlyRow("Band/Record/02 - Elsewhere.flac", "id-2");
        _db.Upsert(Reread(row.RelativePath) with { Genre = "Rock", Composer = "Somebody" });
        row = Reread(row.RelativePath);

        var outcome = Applier().Apply(
            new CloudPull(Cloud("id-2", row.RelativePath), row, true),
            Said("""{"RelativePath":"x","Extension":".flac","Genre":null,"Composers":"Verdi"}"""));

        Assert.Equal(PullOutcome.Applied, outcome);
        Assert.Empty(_writtenWhole);
        var back = Reread(row.RelativePath);
        Assert.Null(back.Genre);
        Assert.Equal("Verdi", back.Composer);
        Assert.Equal("New Title", back.Title);
    }

    /// <summary>The catalogue push sends the album's pressing when the store has none. A catalogue number
    /// emptied in the web app is taken onto the album's row, so it is not sent back up.</summary>
    [Fact]
    public void A_pressing_the_sidecar_states_is_taken_onto_the_album()
    {
        var row = Row("Band/Record/01 - Song.flac", "id-1");
        FileSays(row, info => info with { CatalogNumber = "CAT 1" });
        _db.SetAlbumRelease("Band/Record", "CAT 1", "1997", "5012345678900");

        Applier().Apply(
            new CloudPull(Cloud("id-1", row.RelativePath), row, true),
            Said("""{"RelativePath":"x","Extension":".flac","CatalogNumber":null}"""));

        var album = _db.GetAlbumMeta("Band/Record");
        Assert.Null(album.Catno);
        Assert.Equal("5012345678900", album.Barcode);
        Assert.Equal("1997", album.Released);
    }
}
