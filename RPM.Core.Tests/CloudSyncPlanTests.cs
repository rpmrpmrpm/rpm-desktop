using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// Who is right about a track's metadata when a user has more than one computer.
///
/// <para>The stamps are the main subject. Each side is compared only against itself: this machine's
/// edit against this machine's last agreement, and the store's write against the store's stamp as
/// this machine last accepted it. So nothing here depends on two computers agreeing about the
/// time.</para>
/// </summary>
public class CloudSyncPlanTests
{
    private const string Id = "11111111-2222-3333-4444-555555555555";

    /// <param name="modified">The file's mtime. A retag rewrites the file, so a change here means
    /// "edited here" for a track that has a file.</param>
    /// <param name="synced">When this machine last agreed with the store.</param>
    /// <param name="edited">A row edited with no file to write (a cloud-only track).</param>
    /// <param name="accepted">The store's stamp as this machine last took it.</param>
    private static LibraryTrack Mine(
        string? title = "Brimful of Asha",
        string? artist = "Cornershop",
        string relativePath = "Cornershop/7th Time/02 - Brimful of Asha.flac",
        long modified = 100,
        long synced = 100,
        long edited = 0,
        long accepted = 500,
        CloudState state = CloudState.Synced) => new()
        {
            Id = 1,
            RelativePath = relativePath,
            Folder = "Cornershop/7th Time",
            FileName = "02 - Brimful of Asha.flac",
            Extension = ".flac",
            RpmId = Id,
            Title = title,
            Artist = artist,
            Album = "When I Was Born for the 7th Time",
            AlbumArtist = "Cornershop",
            DurationSeconds = 318,
            TrackNumber = 2,
            DiscNumber = 1,
            Year = 1997,
            Channels = 2,
            SampleRate = 44100,
            BitsPerSample = 16,
            CloudState = state,
            ModifiedUtcTicks = modified,
            CloudSyncedUtcTicks = synced,
            MetadataEditedUtcTicks = edited,
            CloudUpdatedAt = accepted,
        };

    private static CloudTrack Theirs(
        string? title = "Brimful of Asha",
        string? artist = "Cornershop",
        string? relativePath = "Cornershop/7th Time/02 - Brimful of Asha.flac",
        long updatedAt = 500) => new(
            Id, "sha", relativePath, 1000, title, artist, "When I Was Born for the 7th Time", 0,
            new CloudTrackTags(318, 2, 0, 1, 0, 1997, null, "Cornershop", ".flac", 2, 44100, 16),
            updatedAt);

    // ── nothing has moved ──

    [Fact]
    public void Two_sides_that_agree_do_nothing_at_all()
    {
        var plan = CloudSyncPlan.Make([Mine()], [Theirs()]);

        Assert.Empty(plan.Push);
        Assert.Empty(plan.Pull);
    }

    // ── the store has changed, this machine has not ──

    [Fact]
    public void A_correction_made_on_another_machine_comes_down()
    {
        // The store's row has been written since this machine last accepted it, and this machine has
        // edited nothing.
        var plan = CloudSyncPlan.Make(
            [Mine(title: "Brimful of Ashe")],
            [Theirs(title: "Brimful of Asha", updatedAt: 900)]);

        var pull = Assert.Single(plan.Pull);
        Assert.Empty(plan.Push);
        Assert.Equal("Brimful of Asha", pull.Cloud.Title);
    }

    /// <summary>
    /// A record filed somewhere else on another machine is neither pulled nor pushed over.
    ///
    /// <para>Where a file sits is decided by this machine's naming template, so the store's path only
    /// records where the file was when it was uploaded; it is not an instruction. Reading it as a
    /// disagreement would undo a re-file the user asked for on the next run and then push the row
    /// back: one write per track, per run, for ever.</para>
    /// </summary>
    [Fact]
    public void A_record_filed_elsewhere_is_neither_taken_in_nor_argued_with()
    {
        var plan = CloudSyncPlan.Make(
            [Mine()],
            [Theirs(relativePath: "Cornershop/When I Was Born for the 7th Time/02 - Brimful.flac", updatedAt: 900)]);

        Assert.Empty(plan.Pull);
        Assert.Empty(plan.Push);
    }

    /// <summary>And the path is not applied even when the row is being pulled for something else.</summary>
    [Fact]
    public void A_correction_taken_in_does_not_move_the_row()
    {
        var plan = CloudSyncPlan.Make(
            [Mine(title: "Brimful of Ashe")],
            [Theirs(
                title: "Brimful of Asha",
                relativePath: "Cornershop/When I Was Born for the 7th Time/02 - Brimful.flac",
                updatedAt: 900)]);

        var pull = Assert.Single(plan.Pull);
        var applied = CloudSyncPlan.Apply(pull.Row, pull.Cloud);

        Assert.Equal("Brimful of Asha", applied.Title);
        Assert.Equal(pull.Row.RelativePath, applied.RelativePath);
        Assert.Equal(pull.Row.Folder, applied.Folder);
        Assert.Equal(pull.Row.FileName, applied.FileName);
        Assert.Equal(900, applied.CloudUpdatedAt);
    }

    [Fact]
    public void A_store_that_moved_but_says_the_same_thing_is_not_a_correction()
    {
        // This machine's own push changed the stamp. Reading that back as somebody else's edit would
        // rewrite the file's tags on every run for ever.
        var plan = CloudSyncPlan.Make([Mine()], [Theirs(updatedAt: 900)]);

        Assert.Empty(plan.Pull);
        Assert.Empty(plan.Push);
    }

    [Fact]
    public void A_row_the_store_has_never_stamped_is_left_to_the_push()
    {
        // Written by a build that kept no stamp. Nothing can be concluded from it.
        var plan = CloudSyncPlan.Make([Mine(title: "Mine")], [Theirs(title: "Theirs", updatedAt: 0)]);

        Assert.Empty(plan.Pull);
        Assert.Single(plan.Push);
    }

    // ── this machine's word ──

    [Fact]
    public void An_edit_made_here_wins_a_race_with_one_made_there()
    {
        // Both sides changed since they last agreed. The machine the user is using wins, for the whole
        // row, so the two are in step at once and do not trade edits.
        var plan = CloudSyncPlan.Make(
            [Mine(title: "Edited here", modified: 900, synced: 100)],
            [Theirs(title: "Edited there", updatedAt: 900)]);

        Assert.Empty(plan.Pull);
        var push = Assert.Single(plan.Push);
        Assert.Equal("Edited here", push.Title);
    }

    [Fact]
    public void An_edit_to_a_track_with_no_file_counts_as_an_edit()
    {
        // A cloud-only row has no file whose mtime could move, so its own stamp is the only evidence
        // that the user changed it. Without this the store's older wording would come straight back
        // down over the correction.
        var plan = CloudSyncPlan.Make(
            [Mine(title: "Corrected", state: CloudState.CloudOnly, edited: 900, synced: 100)],
            [Theirs(title: "Old", updatedAt: 900)]);

        Assert.Empty(plan.Pull);
        Assert.Equal("Corrected", Assert.Single(plan.Push).Title);
    }

    [Fact]
    public void A_library_that_drifted_still_repairs_the_copy()
    {
        // Neither stamp changed and they still disagree (an older build, or a row written without
        // stamps). The library repairs the cloud copy.
        var plan = CloudSyncPlan.Make([Mine(artist: "Cornershop")], [Theirs(artist: "Cornershp")]);

        Assert.Empty(plan.Pull);
        Assert.Equal("Cornershop", Assert.Single(plan.Push).Artist);
    }

    // ── what a pull does and does not take ──

    [Fact]
    public void A_pull_takes_what_the_store_knows_and_keeps_what_it_does_not()
    {
        var row = Mine();
        var thin = new CloudTrack(Id, "sha", null, 1000, "New title", null, null, 0, null, 900);

        var applied = CloudSyncPlan.Apply(row, thin);

        Assert.Equal("New title", applied.Title);
        // Blank on that side is "not known there", never an instruction to erase.
        Assert.Equal("Cornershop", applied.Artist);
        Assert.Equal("When I Was Born for the 7th Time", applied.Album);
        Assert.Equal(2u, applied.TrackNumber);
        Assert.Equal(row.RelativePath, applied.RelativePath);
    }

    [Fact]
    public void A_pull_leaves_what_the_row_knows_about_the_file_alone()
    {
        var applied = CloudSyncPlan.Apply(Mine(), Theirs(title: "New", updatedAt: 900));

        // The size, the mtime, the plays and the date added describe this machine's copy and its
        // history. No other install can change them.
        Assert.Equal(100, applied.ModifiedUtcTicks);
        Assert.Equal(CloudState.Synced, applied.CloudState);
        Assert.Equal(2, applied.Channels);
    }

    [Fact]
    public void The_sidecar_of_a_track_being_pulled_is_not_sent_back()
    {
        // The file is about to be written FROM the store. Sending its old tags up in the same run would
        // conflict with that write.
        var plan = CloudSyncPlan.Make(
            [Mine(title: "Old", modified: 900, synced: 1000, accepted: 100)],
            [Theirs(title: "New", updatedAt: 900)]);

        Assert.Single(plan.Pull);
        Assert.Empty(plan.Sidecars);
    }

    [Fact]
    public void A_retagged_file_still_sends_its_sidecar()
    {
        var plan = CloudSyncPlan.Make([Mine(title: "New", modified: 900, synced: 100)], [Theirs()]);

        Assert.Equal(Id, Assert.Single(plan.Sidecars));
    }

    // ── the advisory flag, set on another machine ──

    private static CloudTrack Flagged(int advisory, long updatedAt = 900)
    {
        var theirs = Theirs(updatedAt: updatedAt);
        return theirs with { Tags = theirs.Tags! with { Advisory = advisory } };
    }

    [Fact]
    public void An_explicit_flag_set_on_another_machine_comes_down()
    {
        var plan = CloudSyncPlan.Make([Mine(accepted: 100)], [Flagged(1)]);

        var pull = Assert.Single(plan.Pull);
        Assert.Equal(RPM.Core.Models.ContentAdvisory.Explicit, CloudSyncPlan.Apply(pull.Row, pull.Cloud).Advisory);
    }

    [Fact]
    public void A_catalog_that_states_no_flag_does_not_clear_the_one_here()
    {
        var row = Mine(accepted: 100) with { Advisory = RPM.Core.Models.ContentAdvisory.Explicit };

        Assert.False(CloudSyncPlan.Differs(row, Flagged(0)));
        Assert.Equal(RPM.Core.Models.ContentAdvisory.Explicit, CloudSyncPlan.Apply(row, Flagged(0)).Advisory);
        // A number no client writes is read as no flag at all.
        Assert.Equal(RPM.Core.Models.ContentAdvisory.Explicit, CloudSyncPlan.Apply(row, Flagged(7)).Advisory);
    }

    // ── sidecars still to send after the catalogue row is up ──

    [Theory]
    [InlineData(CloudState.Synced, 900, 0, 100, true)]       // file written after it was sent
    [InlineData(CloudState.Synced, 100, 900, 100, false)]    // a Synced row's evidence is its file
    [InlineData(CloudState.CloudOnly, 100, 900, 100, true)]  // edited here with no file
    [InlineData(CloudState.CloudOnly, 900, 0, 100, false)]   // no file, so its mtime says nothing
    [InlineData(CloudState.CloudOnly, 100, 900, 1000, false)]
    [InlineData(CloudState.Local, 900, 900, 100, false)]
    public void A_sidecar_is_owed_by_the_evidence_of_the_rows_own_kind(
        CloudState state, long modified, long edited, long synced, bool owed)
        => Assert.Equal(owed, CloudSyncPlan.OwesSidecar(
            Mine(state: state, modified: modified, edited: edited, synced: synced)));

    [Fact]
    public void A_cloud_only_edit_being_pushed_has_its_sidecar_patched()
    {
        var plan = CloudSyncPlan.Make(
            [Mine(title: "Corrected", state: CloudState.CloudOnly, edited: 900, synced: 100)],
            [Theirs(title: "Old")]);

        Assert.Single(plan.Push);
        Assert.Equal(Id, Assert.Single(plan.CloudOnlySidecars));
        Assert.Empty(plan.Sidecars);
    }

    [Fact]
    public void A_cloud_only_sidecar_still_owed_after_an_earlier_push_is_patched()
    {
        // The catalogue row went up on an earlier run and the sidecar pass did not reach this row. The
        // catalogue agrees now, so nothing is pushed, and the row's own stamp keeps it listed.
        var plan = CloudSyncPlan.Make(
            [Mine(state: CloudState.CloudOnly, edited: 900, synced: 100)],
            [Theirs()]);

        Assert.Empty(plan.Push);
        Assert.Equal(Id, Assert.Single(plan.CloudOnlySidecars));
    }

    [Fact]
    public void A_cloud_only_edit_that_did_not_fit_in_the_push_waits_for_its_sidecar()
    {
        // Patching the sidecar now would stamp the row as sent while its catalogue row is still behind,
        // and a correction from another machine could then be pulled over the edit.
        var plan = CloudSyncPlan.Make(
            [Mine(title: "Corrected", state: CloudState.CloudOnly, edited: 900, synced: 100)],
            [Theirs(title: "Old")],
            limit: 0);

        Assert.Empty(plan.Push);
        Assert.Empty(plan.CloudOnlySidecars);
    }

    [Fact]
    public void A_cloud_only_row_nobody_edited_has_no_sidecar_fetched()
    {
        var plan = CloudSyncPlan.Make([Mine(state: CloudState.CloudOnly, modified: 900)], [Theirs()]);

        Assert.Empty(plan.CloudOnlySidecars);
        Assert.Empty(plan.Sidecars);
    }

    [Fact]
    public void A_track_the_cloud_does_not_hold_is_neither_pushed_nor_pulled()
    {
        var plan = CloudSyncPlan.Make([Mine(modified: 900, synced: 100)], []);

        Assert.Empty(plan.Push);
        Assert.Empty(plan.Pull);
        Assert.Empty(plan.Sidecars);
    }
}
