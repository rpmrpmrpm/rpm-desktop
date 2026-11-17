using static RPM.Fingerprint.External.MusicBrainzClient;

namespace RPM.Core.Tests;

/// <summary>
/// Which failed MusicBrainz attempts are tried again. A busy service answers a share of requests with
/// 503, and closes a share of connections during the TLS handshake. Both are tried again; a timeout
/// is not.
/// </summary>
public class MusicBrainzRetryTests
{
    private static readonly AttemptEnd[] Refusals = [AttemptEnd.Busy, AttemptEnd.Dropped];

    [Fact]
    public void A_refusal_is_tried_again_after_one_then_two_then_four_seconds()
    {
        foreach (var end in Refusals)
        {
            Assert.Equal(TimeSpan.FromSeconds(1), RetryAfter(end, 0));
            Assert.Equal(TimeSpan.FromSeconds(2), RetryAfter(end, 1));
            Assert.Equal(TimeSpan.FromSeconds(4), RetryAfter(end, 2));
        }
    }

    [Fact]
    public void The_last_attempt_is_final()
    {
        foreach (var end in Refusals)
            Assert.Null(RetryAfter(end, MaxAttempts - 1));
    }

    [Fact]
    public void A_timeout_is_not_tried_again()
    {
        Assert.Null(RetryAfter(AttemptEnd.TimedOut, 0));
    }
}
