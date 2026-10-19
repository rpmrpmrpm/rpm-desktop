using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// Which of the three Delete entries a menu draws enabled, and which it draws greyed. All three are
/// always drawn, because the greyed state is information: a greyed "Delete from cloud" tells the
/// listener the record is not in the cloud.
/// </summary>
public class DeleteOfferTests
{
    [Fact]
    public void A_record_only_on_this_disk_offers_the_local_delete_alone()
    {
        var offer = DeleteOffer.For([CloudState.Local, CloudState.Local], cloudMirrorsEverything: false);

        Assert.True(offer.Local);
        Assert.False(offer.Cloud);
        Assert.False(offer.Everywhere);
    }

    [Fact]
    public void A_record_only_in_the_cloud_offers_the_cloud_delete_alone()
    {
        var offer = DeleteOffer.For([CloudState.CloudOnly], cloudMirrorsEverything: false);

        Assert.False(offer.Local);
        Assert.True(offer.Cloud);
        Assert.False(offer.Everywhere);
    }

    [Fact]
    public void A_record_in_both_places_offers_all_three()
    {
        var offer = DeleteOffer.For([CloudState.Synced], cloudMirrorsEverything: false);

        Assert.True(offer.Local);
        Assert.True(offer.Cloud);
        Assert.True(offer.Everywhere);
    }

    /// <summary>Half a record in the cloud is normal (for example an album added while three of its
    /// tracks were still importing). One track in either place is enough to enable that
    /// entry.</summary>
    [Fact]
    public void One_track_in_either_place_is_enough_to_light_that_entry()
    {
        var offer = DeleteOffer.For(
            [CloudState.CloudOnly, CloudState.Local], cloudMirrorsEverything: false);

        Assert.True(offer.Local);
        Assert.True(offer.Cloud);
        Assert.True(offer.Everywhere);
    }

    /// <summary>A track that is only queued has nothing in the cloud yet, but the cloud does hold
    /// something for it: a place in the queue, which "delete from cloud" clears.</summary>
    [Theory]
    [InlineData(CloudState.Queued)]
    [InlineData(CloudState.Uploading)]
    public void A_track_on_its_way_up_counts_as_being_in_the_clouds_hands(CloudState state)
        => Assert.True(DeleteOffer.For([state], cloudMirrorsEverything: false).Cloud);

    /// <summary>
    /// In "all music" mode there is no per-record exclusion: clearing the cloud state of a row whose
    /// file is still here gives it straight back to the next sync run, which reads it as a local track
    /// that belongs in the cloud. So it is greyed, not offered as an action that would undo itself.
    /// </summary>
    [Fact]
    public void Mirroring_everything_greys_the_cloud_delete_while_the_file_is_here()
    {
        var offer = DeleteOffer.For([CloudState.Synced], cloudMirrorsEverything: true);

        Assert.False(offer.Cloud);
        Assert.True(offer.Local);
    }

    /// <summary>
    /// It is enabled again as soon as there is no file left to send. This is the second step of
    /// deleting a record manually (local files first, then the cloud). If it stayed greyed, the
    /// listener could not finish deleting a record they had removed from their disk.
    ///
    /// <para>Nothing comes back because nothing is left: the cloud copy is the last one, so deleting
    /// it removes the row too, and the syncer never sees a local track to promote.</para>
    /// </summary>
    [Fact]
    public void Mirroring_everything_never_stops_a_cloud_only_record_leaving_the_cloud()
    {
        var offer = DeleteOffer.For([CloudState.CloudOnly], cloudMirrorsEverything: true);

        Assert.True(offer.Cloud);
        Assert.False(offer.Local);
    }

    /// <summary>Local files first, then the cloud, in "all music" mode: both steps must be available,
    /// one after the other, or the two-step delete cannot be done.</summary>
    [Fact]
    public void Deleting_a_record_by_hand_in_two_steps_works_in_every_mode()
    {
        foreach (var mirrorsEverything in new[] { true, false })
        {
            // Step one: on this disk and in the cloud. "Delete local files" is enabled.
            var before = DeleteOffer.For([CloudState.Synced], mirrorsEverything);
            Assert.True(before.Local);

            // Step two: the files are gone and the row is cloud-only. "Delete from cloud" is enabled.
            var after = DeleteOffer.For([CloudState.CloudOnly], mirrorsEverything);
            Assert.True(after.Cloud);
        }
    }

    /// <summary>This never affects "delete everywhere", which removes the rows too and so leaves
    /// nothing for the next sync run to pick up.</summary>
    [Fact]
    public void Mirroring_everything_never_stops_a_record_going_entirely()
        => Assert.True(DeleteOffer.For([CloudState.Synced], cloudMirrorsEverything: true).Everywhere);

    /// <summary>Two enabled entries that do the same thing would be ambiguous: with only one copy, the
    /// entry above already does the whole delete.</summary>
    [Fact]
    public void Delete_everywhere_is_live_only_where_there_are_two_copies_to_take()
    {
        Assert.False(DeleteOffer.For([CloudState.Local], false).Everywhere);
        Assert.False(DeleteOffer.For([CloudState.CloudOnly], false).Everywhere);
        Assert.True(DeleteOffer.For([CloudState.Synced], false).Everywhere);
    }

    [Fact]
    public void Nothing_selected_offers_nothing()
    {
        var offer = DeleteOffer.For([], cloudMirrorsEverything: false);

        Assert.False(offer.Any);
        Assert.False(offer.Local);
        Assert.False(offer.Cloud);
        Assert.False(offer.Everywhere);
    }

    /// <summary>A borrowed shelf: their files, their storage, and no row here to write to. The three
    /// are not greyed: the whole submenu is left out of that menu.</summary>
    [Fact]
    public void The_none_offer_has_nothing_live_in_it()
    {
        Assert.False(DeleteOffer.None.Any);
        Assert.Equal(default, DeleteOffer.None);
    }

    /// <summary>An entry is enabled only when its delete would change at least one of the rows. This
    /// ties the menu to <see cref="DeletePlan"/>.</summary>
    [Fact]
    public void A_live_entry_always_has_work_for_at_least_one_row()
    {
        foreach (var state in Enum.GetValues<CloudState>())
        {
            var offer = DeleteOffer.For([state], cloudMirrorsEverything: false);

            if (offer.Local)
                Assert.NotEqual(DeleteOutcome.Nothing, DeletePlan.For(DeleteReach.Local, state));
            if (offer.Cloud)
                Assert.NotEqual(DeleteOutcome.Nothing, DeletePlan.For(DeleteReach.Cloud, state));
        }
    }
}
