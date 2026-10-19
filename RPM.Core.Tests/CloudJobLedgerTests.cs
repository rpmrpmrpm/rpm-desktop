using RPM.Core.Cloud;

namespace RPM.Core.Tests;

/// <summary>
/// The cloud chip's bookkeeping: which tracks a batch has touched, how far in it is, and when it is
/// over.
///
/// <para>Each rule here covers a reporting path that could leave the ring wrong: three upload
/// workers at once, menus that overlap them, retries, and a pause that is the user's own action
/// rather than a failure.</para>
/// </summary>
public class CloudJobLedgerTests
{
    private static CloudJobLedger Running(CloudOp op = CloudOp.Upload, int expect = 0)
    {
        var ledger = new CloudJobLedger();
        ledger.StartBatch();
        if (expect > 0) ledger.Expect(expect);
        return ledger;
    }

    [Fact]
    public void A_batch_with_nothing_in_it_is_never_worth_a_chip()
    {
        var ledger = Running();

        ledger.EndBatch();

        Assert.Equal(CloudBatchPhase.Idle, ledger.Phase);
        Assert.Equal(CloudSummary.Silent, ledger.Summary);
        Assert.Equal(0, ledger.Total);
    }

    [Fact]
    public void An_open_batch_with_nothing_taken_up_yet_says_it_is_preparing()
    {
        var ledger = Running();

        Assert.Equal(CloudBatchPhase.Running, ledger.Phase);
        Assert.Equal(CloudSummary.Preparing, ledger.Summary);
    }

    [Fact]
    public void A_track_taken_up_and_finished_reads_as_done()
    {
        var ledger = Running();
        Assert.True(ledger.Begin(1, CloudOp.Upload));
        ledger.Complete(1, bytes: 4_000_000);

        Assert.Equal(1, ledger.Finished);
        Assert.Equal(1.0, ledger.Fraction, 6);

        ledger.EndBatch();

        Assert.Equal(CloudBatchPhase.Finished, ledger.Phase);
        Assert.Equal(CloudSummary.Done, ledger.Summary);
    }

    /// <summary>
    /// The ring measures the work, not the wave. The engine takes tracks up twelve at a time, so a
    /// two-hundred-track album counted against the wave would fill the ring, declare itself done, and
    /// start again, sixteen times over.
    /// </summary>
    [Fact]
    public void The_ring_measures_everything_the_batch_expects_rather_than_the_wave_in_hand()
    {
        var ledger = Running();

        for (var id = 1; id <= 12; id++) ledger.Begin(id, CloudOp.Upload);
        ledger.Expect(188);                         // 188 still queued behind these twelve
        for (var id = 1; id <= 12; id++) ledger.Complete(id);

        Assert.Equal((12, 200), ledger.Progress);
        Assert.Equal(0.06, ledger.Fraction, 6);
        Assert.Equal(CloudBatchPhase.Running, ledger.Phase);
    }

    /// <summary>A later wave reporting a shorter queue is the line draining, not the work shrinking.</summary>
    [Fact]
    public void What_the_batch_expects_only_ever_grows()
    {
        var ledger = Running();
        ledger.Begin(1, CloudOp.Upload);
        ledger.Expect(99);
        ledger.Expect(2);

        Assert.Equal(100, ledger.Progress.Total);
    }

    [Fact]
    public void A_batch_that_only_ever_touches_what_it_expected_ends_full()
    {
        var ledger = Running();
        ledger.Expect(2);
        ledger.Begin(1, CloudOp.Upload);
        ledger.Complete(1);
        ledger.Begin(2, CloudOp.Upload);
        ledger.Complete(2);

        Assert.Equal(1.0, ledger.Fraction, 6);
    }

    /// <summary>
    /// Every row being done means only that no worker is mid-file. Between two waves of an upload that
    /// is true for a moment. A chip that read it as an ending would announce twelve tracks done, fade
    /// out, and start again on the thirteenth. The engine says when it has stopped.
    /// </summary>
    [Fact]
    public void A_lull_between_two_waves_is_not_the_end_of_the_batch()
    {
        var ledger = Running();
        ledger.Begin(1, CloudOp.Upload);
        ledger.Complete(1);

        Assert.Equal(CloudBatchPhase.Running, ledger.Phase);

        ledger.Begin(2, CloudOp.Upload);

        Assert.Equal(2, ledger.Total);
        Assert.Equal(CloudBatchPhase.Running, ledger.Phase);
    }

    /// <summary>A removal started from a menu overlaps the upload run, and the first of the two to end
    /// must not clear the chip out from under the other.</summary>
    [Fact]
    public void The_last_batch_out_decides_what_the_chip_is_left_saying()
    {
        var ledger = Running();
        ledger.Begin(1, CloudOp.Upload);
        ledger.StartBatch();                    // a removal, over the top of the upload run
        ledger.Begin(2, CloudOp.Remove);
        ledger.Complete(2);

        Assert.False(ledger.EndBatch());
        Assert.Equal(CloudBatchPhase.Running, ledger.Phase);

        ledger.Complete(1);

        Assert.True(ledger.EndBatch());
        Assert.Equal(CloudBatchPhase.Finished, ledger.Phase);
    }

    /// <summary>The same track taken up again (a retry, or a removal right after an upload) reuses
    /// its row rather than growing a duplicate.</summary>
    [Fact]
    public void A_track_taken_up_twice_keeps_one_row()
    {
        var ledger = Running();
        Assert.True(ledger.Begin(7, CloudOp.Upload));
        ledger.Fail(7);

        Assert.False(ledger.Begin(7, CloudOp.Upload));

        Assert.Equal(1, ledger.Total);
        Assert.Equal(0, ledger.Failed);      // taken up again, so no longer a finished failure
        Assert.Equal(0, ledger.Finished);
    }

    [Fact]
    public void A_failure_stays_on_the_list_because_one_that_vanishes_is_one_nobody_saw()
    {
        var ledger = Running();
        ledger.Begin(1, CloudOp.Upload);
        ledger.Complete(1);
        ledger.Begin(2, CloudOp.Upload);
        ledger.Fail(2);
        ledger.EndBatch();

        Assert.Equal(2, ledger.Total);
        Assert.Equal(1, ledger.Failed);
        Assert.Equal(CloudSummary.Failed, ledger.Summary);
    }

    /// <summary>
    /// A pause mid-flight puts the track back in the line. A log book saying it failed would be wrong
    /// about the user's own button press.
    /// </summary>
    [Fact]
    public void An_abandoned_track_is_neither_done_nor_refused()
    {
        var ledger = Running();
        ledger.Begin(1, CloudOp.Upload);

        Assert.True(ledger.Abandon(1));

        Assert.Equal(0, ledger.Total);
        Assert.Equal(0, ledger.Failed);
        Assert.Equal(CloudBatchPhase.Idle, ledger.Phase);
    }

    [Fact]
    public void Abandoning_one_of_several_leaves_the_batch_running()
    {
        var ledger = Running();
        ledger.Begin(1, CloudOp.Upload);
        ledger.Begin(2, CloudOp.Upload);
        ledger.Abandon(2);

        Assert.Equal(1, ledger.Total);
        Assert.Equal(CloudBatchPhase.Running, ledger.Phase);
    }

    [Fact]
    public void Abandoning_a_track_the_chip_never_saw_changes_nothing()
        => Assert.False(Running().Abandon(404));

    /// <summary>The engine reports more than the chip was asked to watch: an unknown track is ignored
    /// rather than invented.</summary>
    [Fact]
    public void Finishing_a_track_that_was_never_taken_up_is_ignored()
    {
        var ledger = Running();
        ledger.Complete(404, bytes: 999);
        ledger.Fail(405);

        Assert.Equal(0, ledger.Total);
        Assert.Equal(0, ledger.Finished);
    }

    /// <summary>
    /// Space freed is the only outcome the user cannot see for themselves: the tracks are still in the
    /// library and still playable, and the one thing that changed is a number on a disk they are not
    /// looking at.
    /// </summary>
    [Fact]
    public void A_batch_that_freed_room_says_how_much()
    {
        var ledger = Running(CloudOp.Offload);
        ledger.Begin(1, CloudOp.Offload);
        ledger.Complete(1, bytes: 30_000_000);
        ledger.Begin(2, CloudOp.Offload);
        ledger.Complete(2, bytes: 12_000_000);
        ledger.EndBatch();

        Assert.Equal(CloudSummary.Freed, ledger.Summary);
        Assert.Equal(42_000_000, ledger.FreedBytes);
    }

    /// <summary>An upload's bytes went up; they did not come off the disk.</summary>
    [Fact]
    public void An_upload_frees_nothing_however_large_it_was()
    {
        var ledger = Running();
        ledger.Begin(1, CloudOp.Upload);
        ledger.Complete(1, bytes: 900_000_000);
        ledger.EndBatch();

        Assert.Equal(0, ledger.FreedBytes);
        Assert.Equal(CloudSummary.Done, ledger.Summary);
    }

    [Fact]
    public void An_offload_that_was_refused_freed_nothing()
    {
        var ledger = Running(CloudOp.Offload);
        ledger.Begin(1, CloudOp.Offload);
        ledger.Fail(1);
        ledger.EndBatch();

        Assert.Equal(0, ledger.FreedBytes);
        Assert.Equal(CloudSummary.Failed, ledger.Summary);   // a refusal outranks the space it did not free
    }

    /// <summary>New work, new list: a batch that had finished is never added to, or the ring would read
    /// as half done as soon as the next album is picked.</summary>
    [Fact]
    public void Work_arriving_after_a_batch_finished_starts_a_fresh_one()
    {
        var ledger = Running();
        ledger.Begin(1, CloudOp.Upload);
        ledger.Complete(1);
        ledger.EndBatch();

        Assert.True(ledger.StartBatch());   // the caller is told to drop the rows it is showing

        Assert.Equal(0, ledger.Total);
        Assert.Equal(0, ledger.Fraction);
        Assert.Equal(CloudBatchPhase.Running, ledger.Phase);
    }

    /// <summary>The engine opens a batch before it touches anything, but a path that ever forgot to
    /// must still put the track on screen rather than fill a list nobody can see.</summary>
    [Fact]
    public void A_track_reported_with_no_batch_open_still_raises_the_chip()
    {
        var ledger = new CloudJobLedger();

        Assert.True(ledger.Begin(1, CloudOp.Restore));

        Assert.Equal(CloudBatchPhase.Running, ledger.Phase);
        Assert.Equal(1, ledger.Total);
    }

    [Fact]
    public void A_finished_chip_is_cleared_once_its_last_word_has_been_read()
    {
        var ledger = Running();
        ledger.Begin(1, CloudOp.Upload);
        ledger.Complete(1);
        ledger.EndBatch();

        Assert.True(ledger.Dismiss());

        Assert.Equal(CloudBatchPhase.Idle, ledger.Phase);
        Assert.Equal(CloudSummary.Silent, ledger.Summary);
        Assert.Equal(0, ledger.Total);
    }

    /// <summary>The chip holds off while its flyout is open (a panel emptying under the pointer takes away
    /// what the user opened it to read), so a dismissal of running work must do nothing.</summary>
    [Fact]
    public void A_running_batch_cannot_be_dismissed()
    {
        var ledger = Running();
        ledger.Begin(1, CloudOp.Upload);

        Assert.False(ledger.Dismiss());
        Assert.Equal(CloudBatchPhase.Running, ledger.Phase);
        Assert.Equal(1, ledger.Total);
    }

    [Fact]
    public void The_ring_never_reads_past_full()
    {
        var ledger = Running();
        ledger.Expect(1);
        for (var id = 1; id <= 5; id++)
        {
            ledger.Begin(id, CloudOp.Upload);
            ledger.Complete(id);
        }

        Assert.Equal(1.0, ledger.Fraction, 6);
    }

    // ── the pause ──
    //
    // A run the user stopped is not a run that finished, and the two must not end the same way. If
    // pausing a two-hundred-track upload after twelve went through EndBatch, the ring would fill and
    // the batch would be called Done: the chip would show a full ring, "12 tracks done." and a list on
    // which every row reads as finished, over a queue with a hundred and eighty-eight tracks still in
    // it. These pin the difference.

    [Fact]
    public void A_paused_batch_is_not_a_finished_one()
    {
        var ledger = Running();
        for (var id = 1; id <= 12; id++) ledger.Begin(id, CloudOp.Upload);
        ledger.Expect(188);
        for (var id = 1; id <= 12; id++) ledger.Complete(id);

        Assert.True(ledger.PauseBatch());

        Assert.Equal(CloudBatchPhase.Paused, ledger.Phase);
        Assert.Equal(CloudSummary.Paused, ledger.Summary);
    }

    /// <summary>The purpose of the distinction: the ring is left where the work actually got to.</summary>
    [Fact]
    public void Pausing_leaves_the_ring_where_the_work_reached_rather_than_filling_it()
    {
        var ledger = Running();
        for (var id = 1; id <= 12; id++) ledger.Begin(id, CloudOp.Upload);
        ledger.Expect(188);
        for (var id = 1; id <= 12; id++) ledger.Complete(id);

        ledger.PauseBatch();

        Assert.Equal(0.06, ledger.Fraction, 6);
        Assert.Equal((12, 200), ledger.Progress);
    }

    /// <summary>The sentence the chip is left saying counts what was really done against what was
    /// really asked for: not the wave, and not a total quietly redefined as "the part that finished".</summary>
    [Fact]
    public void A_paused_batch_reports_what_it_finished_against_what_it_expected()
    {
        var ledger = Running();
        ledger.Begin(1, CloudOp.Upload);
        ledger.Begin(2, CloudOp.Upload);
        ledger.Expect(58);
        ledger.Complete(1);

        // Track 2 was in flight when the pause landed: back in the line, and off the list.
        ledger.Abandon(2);
        ledger.PauseBatch();

        Assert.Equal((1, 60), ledger.Progress);
        Assert.Equal(1, ledger.Finished);
    }

    /// <summary>
    /// A paused chip stays up even with an empty list, which is the opposite of what EndBatch does with
    /// one, and the difference is not cosmetic. The pause arrives while the tracks in flight are inside
    /// ffmpeg; all of them go back in the line and the list empties. Clearing on that would leave the
    /// engine stopped in a state only the user can lift, having just taken away the button that lifts
    /// it.
    /// </summary>
    [Fact]
    public void A_pause_that_empties_the_list_still_leaves_a_chip_to_start_again_from()
    {
        var ledger = Running();
        ledger.Begin(1, CloudOp.Upload);
        ledger.Expect(59);
        ledger.Abandon(1);                  // in flight when the pause landed: back in the line

        Assert.True(ledger.PauseBatch());

        Assert.Equal(CloudBatchPhase.Paused, ledger.Phase);
        Assert.Equal(CloudSummary.Paused, ledger.Summary);
    }

    /// <summary>An empty list still ends a run that was NOT paused: a kick that found nothing due is
    /// never worth a chip.</summary>
    [Fact]
    public void An_ordinary_batch_with_nothing_in_it_still_leaves_no_chip()
    {
        var ledger = Running();

        Assert.True(ledger.EndBatch());

        Assert.Equal(CloudBatchPhase.Idle, ledger.Phase);
        Assert.Equal(CloudSummary.Silent, ledger.Summary);
    }

    /// <summary>Same rule as EndBatch: the last batch out decides what the chip is left saying, so a
    /// paused upload must not clear a removal that is still running out from under it.</summary>
    [Fact]
    public void Pausing_one_of_two_open_batches_leaves_the_other_in_charge()
    {
        var ledger = Running();
        ledger.StartBatch();               // a removal, overlapping the upload
        ledger.Begin(1, CloudOp.Upload);

        Assert.False(ledger.PauseBatch());
        Assert.Equal(CloudBatchPhase.Running, ledger.Phase);

        Assert.True(ledger.EndBatch());
        Assert.Equal(CloudBatchPhase.Finished, ledger.Phase);
    }

    /// <summary>A paused chip can be put away: the queue is in the database either way, and dismissing
    /// says only that the user has finished reading it.</summary>
    [Fact]
    public void A_paused_chip_can_be_dismissed()
    {
        var ledger = Running();
        ledger.Begin(1, CloudOp.Upload);
        ledger.Complete(1);
        ledger.PauseBatch();

        Assert.True(ledger.Dismiss());

        Assert.Equal(CloudBatchPhase.Idle, ledger.Phase);
        Assert.Equal(CloudSummary.Silent, ledger.Summary);
    }

    /// <summary>Resuming is a new batch over a fresh list. The rows from before the pause are finished
    /// work; carrying them forward would make the new ring read as part-done before it started.</summary>
    [Fact]
    public void Starting_again_after_a_pause_clears_the_old_list()
    {
        var ledger = Running();
        ledger.Begin(1, CloudOp.Upload);
        ledger.Complete(1);
        ledger.PauseBatch();

        Assert.True(ledger.StartBatch());   // true: the caller must drop the rows it is showing

        Assert.Equal(CloudBatchPhase.Running, ledger.Phase);
        Assert.Equal(0, ledger.Total);
        Assert.Equal(0, ledger.Fraction);
    }

    [Fact]
    public void Tracks_are_listed_in_the_order_they_were_taken_up()
    {
        var ledger = Running();
        ledger.Begin(9, CloudOp.Upload);
        ledger.Begin(4, CloudOp.Upload);
        ledger.Begin(7, CloudOp.Upload);
        ledger.Begin(4, CloudOp.Upload);   // the same one again: it keeps its place

        Assert.Equal([9L, 4L, 7L], ledger.Tracks);
    }

    /// <summary>
    /// A record coming down is one batch of twelve, not twelve batches of one, and three of its tracks
    /// are in progress at once. The line under the ring counts the record: the three that are being
    /// fetched have not arrived, and the nine behind them are still to come.
    /// </summary>
    [Fact]
    public void A_record_being_downloaded_counts_against_the_whole_record()
    {
        var ledger = Running(CloudOp.Restore, expect: 12);

        ledger.Begin(1, CloudOp.Restore);
        ledger.Begin(2, CloudOp.Restore);
        ledger.Begin(3, CloudOp.Restore);

        Assert.Equal((0, 12), ledger.Progress);
        Assert.Equal(0, ledger.Fraction, 6);

        ledger.Complete(2);

        Assert.Equal((1, 12), ledger.Progress);
        Assert.Equal(CloudBatchPhase.Running, ledger.Phase);
    }

    /// <summary>
    /// The single-track path called in a loop would open a batch of one on every turn. The chip would
    /// read "0 of 1" over a twelve-track album, and each track's EndBatch would call the whole thing
    /// finished, so the next one would arrive to a cleared list and start the count again. One batch
    /// per record prevents that; see CloudSyncService.RestoreAsync(IReadOnlyList).
    /// </summary>
    [Fact]
    public void A_batch_per_track_never_counts_past_one()
    {
        var ledger = new CloudJobLedger();

        ledger.StartBatch();
        ledger.Expect(1);
        ledger.Begin(1, CloudOp.Restore);

        Assert.Equal((0, 1), ledger.Progress);

        ledger.Complete(1);
        ledger.EndBatch();

        Assert.Equal(CloudBatchPhase.Finished, ledger.Phase);

        // The second track of the same record, one turn of the loop later.
        Assert.True(ledger.StartBatch());   // true: the list the first track left is dropped
        ledger.Expect(1);
        ledger.Begin(2, CloudOp.Restore);

        Assert.Equal((0, 1), ledger.Progress);
    }
}
