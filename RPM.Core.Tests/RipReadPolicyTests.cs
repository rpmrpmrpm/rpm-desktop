using System.Diagnostics;
using RPM.Core.Ripping;

namespace RPM.Core.Tests;

/// <summary>
/// The rules that decide when a damaged disc has cost enough to give up on a track: how many times a
/// refused block is asked for again, how long the drive is given to answer at all, and how much of one
/// track may be silence before the track is not worth keeping.
///
/// All of it is exercised through <see cref="DiscBlockReader"/> with a stand-in read, because the real
/// callers are the two platform readers and the DVD sector stream, none of which can fail on demand
/// without a scratched disc in a drive.
/// </summary>
public class RipReadPolicyTests
{
    private static RipReadLog Log(int retries = 2, int maxBadBlocks = 8, double timeoutSeconds = 5) =>
        new(new RipReadPolicy
        {
            Retries = retries,
            MaxBadBlocks = maxBadBlocks,
            BlockTimeout = TimeSpan.FromSeconds(timeoutSeconds),
        });

    [Fact]
    public async Task A_block_that_reads_first_time_is_handed_straight_back()
    {
        var attempts = 0;
        var read = await DiscBlockReader.ReadAsync(_ =>
        {
            attempts++;
            return Task.FromResult(2048);
        }, Log(), "sectors 0..25", CancellationToken.None);

        Assert.Equal(2048, read);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task A_dirty_block_is_asked_for_again_and_often_gives_in()
    {
        // The common case on a fingerprinted disc: the first read fails, the second one works. Nothing
        // is written off, so nothing is filled with silence.
        var attempts = 0;
        var log = Log();

        var read = await DiscBlockReader.ReadAsync(_ =>
        {
            attempts++;
            return attempts < 2
                ? Task.FromException<int>(new IOException("Input/output error"))
                : Task.FromResult(2048);
        }, log, "sectors 0..25", CancellationToken.None);

        Assert.Equal(2048, read);
        Assert.Equal(2, attempts);
        Assert.Equal(0, log.BadBlocks);
    }

    [Fact]
    public async Task A_block_that_never_reads_is_written_off_after_its_retries()
    {
        var attempts = 0;
        var log = Log(retries: 2);

        var read = await DiscBlockReader.ReadAsync(_ =>
        {
            attempts++;
            return Task.FromException<int>(new IOException("Input/output error"));
        }, log, "sectors 0..25", CancellationToken.None);

        // Written off, not thrown: the caller fills its place with silence and the track goes on.
        Assert.Equal(DiscBlockReader.Unreadable, read);
        Assert.Equal(3, attempts); // the first ask plus its two retries
        Assert.Equal(1, log.BadBlocks);
        Assert.Contains("sectors 0..25", log.LastError);
    }

    [Fact]
    public async Task A_track_past_its_budget_of_bad_blocks_is_given_up_on()
    {
        var log = Log(retries: 0, maxBadBlocks: 2);

        // Two lost blocks are a click each and survivable...
        for (var i = 0; i < 2; i++)
        {
            var read = await DiscBlockReader.ReadAsync(
                _ => Task.FromException<int>(new IOException("Input/output error")),
                log, $"sectors {i}", CancellationToken.None);
            Assert.Equal(DiscBlockReader.Unreadable, read);
        }

        // ...the third says this track is not worth ripping, which is the ripper's cue to skip it.
        var ex = await Assert.ThrowsAsync<DiscTrackUnreadableException>(() => DiscBlockReader.ReadAsync(
            _ => Task.FromException<int>(new IOException("Input/output error")),
            log, "sectors 3", CancellationToken.None));

        Assert.False(ex.Stalled);   // it answered, it just answered badly
        Assert.Equal(3, log.BadBlocks);
    }

    [Fact]
    public async Task A_drive_that_stops_answering_is_abandoned_rather_than_waited_on()
    {
        // The case that hangs a rip forever: no error, no data, no end. The read is never cancelled,
        // because a drive wedged in the kernel does not answer a token, so the only way out is to leave it.
        var log = Log(retries: 5, timeoutSeconds: 0.2);
        var clock = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<DiscTrackUnreadableException>(() => DiscBlockReader.ReadAsync(
            _ => new TaskCompletionSource<int>().Task, log, "sectors 0..25", CancellationToken.None));

        clock.Stop();
        Assert.True(ex.Stalled);
        Assert.Contains("stopped responding", ex.Message);

        // Given up on after one timeout, not retried five times over: giving a wedged handle another
        // read is how a rip gets stuck for an hour.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"waited {clock.Elapsed}");
    }

    [Fact]
    public async Task Cancelling_beats_the_clock_and_is_not_reported_as_damage()
    {
        // A skip lands here too, and it must not be recorded as a scratch: the disc did nothing wrong.
        using var cts = new CancellationTokenSource();
        var log = Log(timeoutSeconds: 30);

        var read = DiscBlockReader.ReadAsync(
            async token => { await Task.Delay(Timeout.Infinite, token); return 0; },
            log, "sectors 0..25", cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal(0, log.BadBlocks);
    }

    [Fact]
    public void The_default_policy_is_patient_but_not_endless()
    {
        // Numbers a user never sees but notices: enough retries to get past a fingerprint, enough lost
        // blocks to tolerate a scuff, and a patience measured in seconds and not minutes.
        var policy = RipReadPolicy.Default;

        Assert.True(policy.Retries is >= 1 and <= 5);
        Assert.True(policy.MaxBadBlocks is >= 1 and <= 64);
        Assert.InRange(policy.BlockTimeout, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1));
    }
}
