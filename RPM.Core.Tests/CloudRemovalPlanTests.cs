using RPM.Core.Cloud;

namespace RPM.Core.Tests;

/// <summary>
/// What a removal does to one track, over the three actions that remove things: a song's menu, a
/// record's menu, and the delete that takes the record off this computer and out of the account.
/// </summary>
public class CloudRemovalPlanTests
{
    private const string Id = "6f1c0d3e-6f1c-4d3e-8f1c-0d3e6f1c0d3e";

    [Theory]
    [InlineData(CloudState.Queued, true)]
    [InlineData(CloudState.Uploading, true)]
    [InlineData(CloudState.Synced, true)]
    [InlineData(CloudState.CloudOnly, true)]
    [InlineData(CloudState.Local, false)]
    [InlineData(CloudState.Error, false)]
    public void The_account_is_holding_something_for_a_track_that_is_up_or_on_its_way(CloudState state, bool held)
        => Assert.Equal(held, CloudRemovalPlan.IsInTheCloudsHands(state));

    /// <summary>
    /// The case that loses music if it is wrong. This machine has given up its copy, so the store has
    /// the only one. Deleting it without first downloading the file deletes the track.
    /// </summary>
    [Fact]
    public void An_offloaded_track_is_fetched_home_before_its_cloud_copy_goes()
        => Assert.Equal(
            CloudRemoval.BringHomeThenDelete,
            CloudRemovalPlan.For(CloudRemovalIntent.TakeOutOfCloud, CloudState.CloudOnly, Id));

    [Fact]
    public void A_track_that_is_in_both_places_only_loses_the_cloud_copy()
        => Assert.Equal(
            CloudRemoval.DeleteInCloud,
            CloudRemovalPlan.For(CloudRemovalIntent.TakeOutOfCloud, CloudState.Synced, Id));

    [Theory]
    [InlineData(CloudState.Queued)]
    [InlineData(CloudState.Uploading)]
    [InlineData(CloudState.Local)]
    [InlineData(CloudState.Error)]
    public void A_track_that_never_got_up_there_has_only_a_row_to_clean(CloudState state)
        => Assert.Equal(
            CloudRemoval.ForgetLocally,
            CloudRemovalPlan.For(CloudRemovalIntent.TakeOutOfCloud, state, Id));

    /// <summary>A file whose format cannot carry an id is never stored under one, whatever the row
    /// says, so there is no object to ask the store about.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_track_with_no_id_has_nothing_in_the_cloud_to_delete(string? rpmId)
    {
        Assert.Equal(
            CloudRemoval.ForgetLocally,
            CloudRemovalPlan.For(CloudRemovalIntent.TakeOutOfCloud, CloudState.Synced, rpmId));
        Assert.Equal(
            CloudRemoval.ForgetLocally,
            CloudRemovalPlan.For(CloudRemovalIntent.DeleteEverywhere, CloudState.CloudOnly, rpmId));
    }

    /// <summary>
    /// The user asked for this album to come out of the cloud, not for their playlist to stop working.
    /// The claim goes; the track stays up.
    /// </summary>
    [Theory]
    [InlineData(CloudState.Synced)]
    [InlineData(CloudState.CloudOnly)]
    [InlineData(CloudState.Queued)]
    public void A_track_a_synced_playlist_still_plays_stays_in_the_cloud(CloudState state)
        => Assert.Equal(
            CloudRemoval.UnpinOnly,
            CloudRemovalPlan.For(CloudRemovalIntent.TakeOutOfCloud, state, Id, heldElsewhere: true));

    /// <summary>
    /// Deleting the record is the exception, on purpose: the local rows are deleted too, so a cloud
    /// copy left in place would come back on the next catalogue pass as a cloud album. That is the
    /// opposite of what the user asked for.
    /// </summary>
    [Fact]
    public void A_playlist_does_not_spare_a_track_from_a_deletion()
        => Assert.Equal(
            CloudRemoval.DeleteInCloud,
            CloudRemovalPlan.For(CloudRemovalIntent.DeleteEverywhere, CloudState.Synced, Id, heldElsewhere: true));

    /// <summary>Downloading a record in order to throw it away is not what "delete this album" means.</summary>
    [Fact]
    public void A_deletion_never_fetches_anything_home_first()
        => Assert.Equal(
            CloudRemoval.DeleteInCloud,
            CloudRemovalPlan.For(CloudRemovalIntent.DeleteEverywhere, CloudState.CloudOnly, Id));

    [Theory]
    [InlineData(CloudState.Queued)]
    [InlineData(CloudState.Uploading)]
    public void A_deletion_of_a_track_still_in_the_line_clears_the_line(CloudState state)
        => Assert.Equal(
            CloudRemoval.ForgetLocally,
            CloudRemovalPlan.For(CloudRemovalIntent.DeleteEverywhere, state, Id));

    [Theory]
    [InlineData(CloudState.Local)]
    [InlineData(CloudState.Error)]
    public void A_deletion_leaves_alone_a_track_the_account_holds_nothing_for(CloudState state)
        => Assert.Equal(
            CloudRemoval.Nothing,
            CloudRemovalPlan.For(CloudRemovalIntent.DeleteEverywhere, state, Id));

    /// <summary>
    /// The property that matters more than any single case: the store is only ever asked to delete
    /// something when the file is on this disk, or is being brought back to it first.
    /// </summary>
    [Fact]
    public void Nothing_is_ever_deleted_in_the_cloud_while_it_is_the_only_copy()
    {
        foreach (var intent in Enum.GetValues<CloudRemovalIntent>())
        foreach (var state in Enum.GetValues<CloudState>())
        foreach (var heldElsewhere in new[] { true, false })
        {
            var plan = CloudRemovalPlan.For(intent, state, Id, heldElsewhere);

            if (state == CloudState.CloudOnly && plan == CloudRemoval.DeleteInCloud)
                Assert.Equal(CloudRemovalIntent.DeleteEverywhere, intent);
        }
    }
}
