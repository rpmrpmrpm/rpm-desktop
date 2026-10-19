using RPM.Core.Cloud;

namespace RPM.Core.Tests;

/// <summary>
/// When a track that failed to upload is tried again. The queue is the library itself, so a failed
/// track stays in the queue. Without a wait and a ceiling, one unreadable file keeps the queue busy
/// and the rest of the collection waits behind it.
/// </summary>
public class CloudRetryScheduleTests
{
    private static readonly DateTime Now = new(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    public void The_wait_doubles_with_every_failure(int attempts, int minutes)
        => Assert.Equal(TimeSpan.FromMinutes(minutes), CloudRetrySchedule.Backoff(attempts));

    /// <summary>The doubling stops. A track that has failed a dozen times is tried every sixteen
    /// minutes, not once every eight hours. Without the ceiling, the track would in practice never be
    /// tried again.</summary>
    [Theory]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(64)]
    public void The_wait_stops_growing_at_sixteen_minutes(int attempts)
        => Assert.Equal(TimeSpan.FromMinutes(CloudRetrySchedule.MaxBackoffMinutes), CloudRetrySchedule.Backoff(attempts));

    [Fact]
    public void A_negative_count_is_treated_as_a_first_attempt()
        => Assert.Equal(TimeSpan.FromMinutes(1), CloudRetrySchedule.Backoff(-3));

    [Fact]
    public void The_next_attempt_is_the_wait_from_now()
        => Assert.Equal((Now + TimeSpan.FromMinutes(4)).Ticks, CloudRetrySchedule.NextAttemptTicks(2, Now));

    [Theory]
    [InlineData(0, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    public void Five_failures_and_the_track_is_left_alone(int attempts, bool mayTry)
        => Assert.Equal(mayTry, CloudRetrySchedule.MayTryAgain(attempts));

    /// <summary>
    /// The failure that waiting cannot fix: a file whose format cannot hold an rpm id will never be
    /// able to hold one. Retrying it every sixteen minutes for the life of the installation would put
    /// the same error in the log for ever.
    /// </summary>
    [Fact]
    public void A_file_that_can_never_be_synced_is_not_tried_again_in_this_lifetime()
    {
        var never = CloudRetrySchedule.NeverAgainTicks(Now);

        Assert.True(never > Now.AddYears(50).Ticks);
        Assert.True(never > CloudRetrySchedule.NextAttemptTicks(CloudRetrySchedule.MaxAttempts, Now));
    }

    /// <summary>Five attempts spread over the backoff take most of an hour, which is long enough for a
    /// network that is only down for a while to come back.</summary>
    [Fact]
    public void Giving_up_takes_long_enough_for_a_dropped_connection_to_return()
    {
        var total = TimeSpan.Zero;
        for (var attempt = 0; attempt < CloudRetrySchedule.MaxAttempts; attempt++)
            total += CloudRetrySchedule.Backoff(attempt);

        Assert.True(total >= TimeSpan.FromMinutes(30), $"a track is abandoned after only {total}");
    }

    // ── a refusal has to happen twice ──

    private static readonly CloudFailureKind Refused = new(CloudFailure.Transcode, Permanent: true);
    private const string Sentence = "Audio could not be converted";

    /// <summary>A refusal can happen while the import is still writing the files, and the same files
    /// convert later. So one refusal is not final.</summary>
    [Fact]
    public void A_first_refusal_is_tried_again()
        => Assert.Equal(
            CloudRetrySchedule.NextAttemptTicks(0, Now),
            CloudRetrySchedule.AfterFailure(Refused, Sentence, previousError: null, attempts: 0, Now));

    [Fact]
    public void The_same_refusal_twice_is_final()
        => Assert.Equal(
            CloudRetrySchedule.NeverAgainTicks(Now),
            CloudRetrySchedule.AfterFailure(Refused, Sentence, previousError: Sentence, attempts: 1, Now));

    /// <summary>A dropped connection last time and a refusal now is still only one refusal.</summary>
    [Fact]
    public void A_refusal_after_a_different_failure_is_tried_again()
        => Assert.Equal(
            CloudRetrySchedule.NextAttemptTicks(1, Now),
            CloudRetrySchedule.AfterFailure(Refused, Sentence, previousError: "Upload failed", attempts: 1, Now));

    [Fact]
    public void A_failure_that_is_not_permanent_is_never_final()
    {
        var network = new CloudFailureKind(CloudFailure.Unknown, Permanent: false);

        Assert.Equal(
            CloudRetrySchedule.NextAttemptTicks(1, Now),
            CloudRetrySchedule.AfterFailure(network, "Upload failed", previousError: "Upload failed", attempts: 1, Now));
    }
}
