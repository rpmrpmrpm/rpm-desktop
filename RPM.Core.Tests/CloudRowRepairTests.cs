using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// A row that says the cloud has a track the store has never heard of.
///
/// <para>
/// Causes: an id re-stamped after the upload, a track deleted from another install, a library opened
/// under a different account. The rendition catch-up picks rows that are Synced and have no
/// renditions recorded. Without a repair, such a row is encoded, refused with a 404, recorded as
/// nothing, and picked again on the next run, for the life of the installation. The log fills, the
/// CPU is busy, and the record keeps showing as backed up when it is not.
/// </para>
/// </summary>
public class CloudRowRepairTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rowrepair-{Guid.NewGuid():N}");
    private readonly LibraryDatabase _db;

    public CloudRowRepairTests()
    {
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void The_store_denying_a_track_it_should_have_puts_the_row_back_in_the_line()
        => Assert.True(CloudRowRepair.BelongsBackInTheQueue(CloudState.Synced, RenditionOutcome.NotInTheCloud));

    /// <summary>Any failure that may succeed next time leaves the row alone: the track is offered again
    /// on the next run, which is what "best effort" means here.</summary>
    [Theory]
    [InlineData(RenditionOutcome.Made)]
    [InlineData(RenditionOutcome.Failed)]
    public void A_failure_that_might_pass_next_time_changes_no_rows(RenditionOutcome outcome)
        => Assert.False(CloudRowRepair.BelongsBackInTheQueue(CloudState.Synced, outcome));

    /// <summary>
    /// Only a row that CLAIMS to be up there is contradicted by a 404. A track that was never uploaded
    /// has nothing to repair, and one that is only in the cloud must never be silently demoted: its
    /// file is not on this disk, and "Local" would be a row pointing at nothing.
    /// </summary>
    [Theory]
    [InlineData(CloudState.Local)]
    [InlineData(CloudState.Queued)]
    [InlineData(CloudState.Uploading)]
    [InlineData(CloudState.CloudOnly)]
    [InlineData(CloudState.Error)]
    public void Only_a_row_that_says_it_is_synced_is_put_right(CloudState state)
        => Assert.False(CloudRowRepair.BelongsBackInTheQueue(state, RenditionOutcome.NotInTheCloud));

    /// <summary>
    /// A few rows the store does not know is a library with a few bad ids in it. A whole run of them
    /// is different: it means the account is signed in as somebody else, or an API answers 404 to
    /// everything. Then the repair would demote every row in the collection and upload it all again.
    /// The loop cannot tell these two cases apart on its own.
    /// </summary>
    [Fact]
    public void A_run_stops_putting_rows_right_once_it_has_seen_too_many()
    {
        Assert.True(CloudRowRepair.BelongsBackInTheQueue(
            CloudState.Synced, RenditionOutcome.NotInTheCloud, alreadyPutBack: CloudRowRepair.MostPerRun - 1));

        Assert.False(CloudRowRepair.BelongsBackInTheQueue(
            CloudState.Synced, RenditionOutcome.NotInTheCloud, alreadyPutBack: CloudRowRepair.MostPerRun));
    }

    /// <summary>
    /// The catalogue decides what the loop cannot. The store denied this row's id, and the same run's
    /// listing has the same audio under another id. That is a stale name on this machine while the
    /// account clearly works, so it is not evidence against the account, and the row is repaired
    /// whatever the ceiling says.
    /// </summary>
    [Fact]
    public void A_denial_the_catalog_explains_is_a_stale_name_here()
        => Assert.True(CloudRowRepair.ExplainedByTheCatalog("gid-mine", "gid-the-stores"));

    /// <summary>The catalogue does not know this audio either. Nothing is explained, and the run's
    /// ceiling and its account warning both apply.</summary>
    [Fact]
    public void A_denial_the_catalog_knows_nothing_about_explains_nothing()
        => Assert.False(CloudRowRepair.ExplainedByTheCatalog("gid-mine", null));

    /// <summary>The store denied the exact id the listing names. That is not a rename: the two
    /// answers disagree, and re-filing the row under the name it already has would fix nothing.</summary>
    [Fact]
    public void The_name_it_already_has_explains_nothing()
        => Assert.False(CloudRowRepair.ExplainedByTheCatalog("gid-mine", "gid-mine"));

    [Fact]
    public void And_says_so_rather_than_re_uploading_a_collection()
    {
        Assert.False(CloudRowRepair.SomethingIsWrongWithTheAccount(CloudRowRepair.MostPerRun));
        Assert.True(CloudRowRepair.SomethingIsWrongWithTheAccount(CloudRowRepair.MostPerRun + 1));
    }

    /// <summary>Local, not Queued: a track its owner took out of the cloud on purpose must not be put
    /// back in the queue by this.</summary>
    [Fact]
    public void A_row_is_put_back_as_it_was_before_anybody_offered_it()
        => Assert.Equal(CloudState.Local, CloudRowRepair.PutBackAs);

    /// <summary>
    /// The loop itself, over a real mirror: a row that says Synced with no renditions recorded is
    /// offered for rendition on every run.
    /// </summary>
    [Fact]
    public void A_row_the_store_denies_is_offered_for_renditions_for_ever()
    {
        var id = Add("Band/Record/01 - Song.flac", CloudState.Synced);

        // Three runs, and nothing recorded between them because the store keeps refusing.
        for (var run = 0; run < 3; run++)
            Assert.Contains(_db.CloudTracksWithLocalFiles(10), t => t.Id == id);
    }

    /// <summary>The repair: resetting the row takes it out of that query, and the queue picks it up as
    /// a track that has never been uploaded, which is what it is.</summary>
    [Fact]
    public void Putting_the_row_back_ends_the_loop_and_the_track_is_uploaded_instead()
    {
        var id = Add("Band/Record/01 - Song.flac", CloudState.Synced, selected: true);

        _db.SetCloudState(id, CloudRowRepair.PutBackAs);

        Assert.DoesNotContain(_db.CloudTracksWithLocalFiles(10), t => t.Id == id);

        Assert.Equal(1, _db.QueueForUpload(everything: false));
        Assert.Equal(CloudState.Queued, Row(id).CloudState);
    }

    /// <summary>
    /// A track nobody picked stays as it is. The repair says "this is not in the cloud", not "upload
    /// it": in "selected items" mode, a record the user took out of their picks is one they asked not
    /// to have in the cloud.
    /// </summary>
    [Fact]
    public void A_track_nobody_picked_is_not_uploaded_by_being_put_right()
    {
        var id = Add("Band/Record/02 - Other.flac", CloudState.Synced, selected: false);

        _db.SetCloudState(id, CloudRowRepair.PutBackAs);

        Assert.Equal(0, _db.QueueForUpload(everything: false));
        Assert.Equal(CloudState.Local, Row(id).CloudState);
    }

    /// <summary>In "all music" mode it goes up again, because everything does.</summary>
    [Fact]
    public void In_all_music_mode_the_track_goes_up_again()
    {
        var id = Add("Band/Record/03 - Third.flac", CloudState.Synced, selected: false);

        _db.SetCloudState(id, CloudRowRepair.PutBackAs);

        Assert.Equal(1, _db.QueueForUpload(everything: true));
        Assert.Equal(CloudState.Queued, Row(id).CloudState);
    }

    /// <summary>A row that already has renditions is not in the query at all: the catch-up is for the
    /// rows that have never had any.</summary>
    [Fact]
    public void A_track_that_already_has_its_renditions_is_never_offered_again()
    {
        var id = Add("Band/Record/04 - Done.flac", CloudState.Synced);

        _db.NoteRenditions(id, DateTime.UtcNow.Ticks);

        Assert.DoesNotContain(_db.CloudTracksWithLocalFiles(10), t => t.Id == id);
    }

    private long Add(string relativePath, CloudState state, bool selected = false)
    {
        var cut = relativePath.LastIndexOf('/');
        _db.Upsert(new LibraryTrack
        {
            RelativePath = relativePath,
            Folder = cut < 0 ? "" : relativePath[..cut],
            FileName = cut < 0 ? relativePath : relativePath[(cut + 1)..],
            Extension = Path.GetExtension(relativePath).ToLowerInvariant(),
            RpmId = Guid.NewGuid().ToString("D"),
            Title = "Song",
        });

        var row = Row(relativePath);
        if (selected) _db.SetCloudSelected([row.Id], selected: true, pinned: true);
        _db.SetCloudState(row.Id, state);
        return row.Id;
    }

    private LibraryTrack Row(string relativePath) =>
        _db.Query().First(t => string.Equals(t.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));

    private LibraryTrack Row(long id) => _db.Query().First(t => t.Id == id);
}
