using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// What each of the three deletes does to one row, and when a row leaves the library at all (the
/// promise the interface makes).
///
/// <para>A delete that trashes the local files, keeps the cloud copy and still drops the row is
/// wrong: the record leaves the wall and comes back by itself on the next catalogue pass, which
/// looks like the app losing music.</para>
/// </summary>
public class DeletePlanTests
{
    /// <summary>Every state a track can be in, so a new state cannot be added without a test that says
    /// what each <see cref="DeleteReach"/> does to it.</summary>
    private static readonly CloudState[] All =
        [.. Enum.GetValues<CloudState>()];

    // ── the promise ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The rule the menu follows: an album or a song leaves the library when its last copy is deleted,
    /// and never otherwise. Every other test in this file is a case of it.
    /// </summary>
    [Fact]
    public void A_row_leaves_the_library_only_when_its_last_copy_goes()
    {
        foreach (var state in All)
        foreach (var reach in Enum.GetValues<DeleteReach>())
        {
            var leaves = DeletePlan.For(reach, state) == DeleteOutcome.RemoveEntirely;

            var losingLocal = reach is DeleteReach.Local or DeleteReach.Everywhere;
            var losingCloud = reach is DeleteReach.Cloud or DeleteReach.Everywhere;
            var keepsSomething =
                (DeletePlan.HasLocalFile(state) && !losingLocal)
                || (DeletePlan.HasCloudCopy(state) && !losingCloud);

            Assert.Equal(!keepsSomething, leaves);
        }
    }

    // ── delete local files ────────────────────────────────────────────────────────────────────────

    /// <summary>The main case. The file is deleted, the row stays, and the row says the music is in
    /// the cloud and nowhere else, which turns the record's mark cyan.</summary>
    [Fact]
    public void Deleting_local_files_of_a_synced_track_keeps_the_row_and_offloads_it()
        => Assert.Equal(
            DeleteOutcome.TrashKeepingCloud, DeletePlan.For(DeleteReach.Local, CloudState.Synced));

    [Fact]
    public void Deleting_local_files_of_a_track_that_is_nowhere_else_takes_the_row_too()
        => Assert.Equal(
            DeleteOutcome.RemoveEntirely, DeletePlan.For(DeleteReach.Local, CloudState.Local));

    /// <summary>
    /// A queued or uploading track has a place in the queue and no object behind it. Reading that as
    /// "the cloud has this" and demoting the row would leave a row claiming a cloud copy that was never
    /// written, for a file that has just been trashed.
    /// </summary>
    [Theory]
    [InlineData(CloudState.Queued)]
    [InlineData(CloudState.Uploading)]
    public void A_track_that_has_not_finished_going_up_is_not_treated_as_being_up(CloudState state)
    {
        Assert.False(DeletePlan.HasCloudCopy(state));
        Assert.Equal(DeleteOutcome.RemoveEntirely, DeletePlan.For(DeleteReach.Local, state));
    }

    /// <summary>Nothing here to move. Not an error and not a removal: the entry is greyed for this
    /// case, and a row that still gets here is left alone, not dropped.</summary>
    [Fact]
    public void Deleting_local_files_of_a_cloud_only_track_does_nothing()
        => Assert.Equal(
            DeleteOutcome.Nothing, DeletePlan.For(DeleteReach.Local, CloudState.CloudOnly));

    // ── delete from cloud ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Deleting_the_cloud_copy_of_a_synced_track_keeps_the_row_and_the_file()
        => Assert.Equal(
            DeleteOutcome.UncloudKeepingFile, DeletePlan.For(DeleteReach.Cloud, CloudState.Synced));

    /// <summary>The case that deletes music, on purpose: there is nothing on this disk, so the copy
    /// being deleted is the only one. The dialog tells the user this.</summary>
    [Fact]
    public void Deleting_the_cloud_copy_of_a_cloud_only_track_takes_the_row_too()
        => Assert.Equal(
            DeleteOutcome.RemoveEntirely, DeletePlan.For(DeleteReach.Cloud, CloudState.CloudOnly));

    /// <summary>Chosen or queued: no object to delete, but a place in the queue that must be cleared,
    /// or the track is uploaded a minute later and the menu item seems to do nothing.</summary>
    [Theory]
    [InlineData(CloudState.Queued)]
    [InlineData(CloudState.Uploading)]
    public void Deleting_the_cloud_copy_of_a_track_still_going_up_is_not_nothing(CloudState state)
        => Assert.Equal(
            DeleteOutcome.UncloudKeepingFile, DeletePlan.For(DeleteReach.Cloud, state));

    [Theory]
    [InlineData(CloudState.Local)]
    [InlineData(CloudState.Error)]
    public void Deleting_the_cloud_copy_of_a_track_the_cloud_never_held_does_nothing(CloudState state)
        => Assert.Equal(DeleteOutcome.Nothing, DeletePlan.For(DeleteReach.Cloud, state));

    // ── delete everywhere ─────────────────────────────────────────────────────────────────────────

    /// <summary>The only reach with no branch in it. Whatever the row is, it is removed.</summary>
    [Fact]
    public void Deleting_everywhere_always_takes_the_row()
    {
        foreach (var state in All)
            Assert.Equal(DeleteOutcome.RemoveEntirely, DeletePlan.For(DeleteReach.Everywhere, state));
    }

    // ── a record goes to the trash as a record ────────────────────────────────────────────────────

    /// <summary>
    /// An album's delete must not go through the per-song path. That path moves the songs into the
    /// trash as loose files and leaves their folder behind, so Put Back on that folder restores the
    /// sleeve and none of the music, and the album does not come back.
    /// </summary>
    [Fact]
    public void A_record_whose_every_row_is_going_goes_as_one_folder()
        => Assert.True(DeletePlan.GoesAsOneRecord(
            Enumerable.Repeat(DeleteOutcome.RemoveEntirely, 32)));

    /// <summary>The folder stays: the record is still in the library, still on the wall, and its sleeve
    /// is still needed. Only the songs move.</summary>
    [Fact]
    public void A_record_only_giving_up_its_local_files_keeps_its_folder()
        => Assert.False(DeletePlan.GoesAsOneRecord(
            Enumerable.Repeat(DeleteOutcome.TrashKeepingCloud, 12)));

    /// <summary>Nine songs of twelve. Taking the folder would take the other three with it.</summary>
    [Fact]
    public void A_selection_that_leaves_rows_behind_never_takes_the_folder()
        => Assert.False(DeletePlan.GoesAsOneRecord(
            [.. Enumerable.Repeat(DeleteOutcome.RemoveEntirely, 9),
             .. Enumerable.Repeat(DeleteOutcome.Nothing, 3)]));

    /// <summary>A folder the mirror holds nothing in is not a record being deleted. If an empty set
    /// read as "all of them are going", an untouched folder would be thrown away.</summary>
    [Fact]
    public void An_empty_folder_is_not_a_record_going_whole()
        => Assert.False(DeletePlan.GoesAsOneRecord([]));

    /// <summary>Follows from the reach: everything a delete-everywhere touches is removed, so a record
    /// it targets is always removed whole.</summary>
    [Fact]
    public void Deleting_a_record_everywhere_always_takes_its_folder()
        => Assert.True(DeletePlan.GoesAsOneRecord(
            new[] { CloudState.Synced, CloudState.CloudOnly, CloudState.Local }
                .Select(s => DeletePlan.For(DeleteReach.Everywhere, s))));

    // ── the two readings the whole thing rests on ─────────────────────────────────────────────────

    [Fact]
    public void Only_a_cloud_only_track_has_no_file_on_this_disk()
    {
        foreach (var state in All)
            Assert.Equal(state != CloudState.CloudOnly, DeletePlan.HasLocalFile(state));
    }

    [Fact]
    public void Only_a_synced_or_offloaded_track_has_bytes_in_the_cloud()
    {
        foreach (var state in All)
            Assert.Equal(
                state is CloudState.Synced or CloudState.CloudOnly, DeletePlan.HasCloudCopy(state));
    }
}
