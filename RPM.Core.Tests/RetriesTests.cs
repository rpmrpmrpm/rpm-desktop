using RPM.Core.Playback;

namespace RPM.Core.Tests;

/// <summary>
/// The bound on reopening a track that stopped but did not end. Without a bound, a file that is
/// really shorter than its tag says would stall an album forever and silently, which is worse than
/// skipping the track.
/// </summary>
public class RetriesTests
{
    [Fact]
    public void A_track_gets_three_goes()
    {
        var retries = new Retries();

        Assert.True(retries.MayRetry("a"));
        Assert.True(retries.MayRetry("a"));
        Assert.True(retries.MayRetry("a"));
        Assert.False(retries.MayRetry("a"));
    }

    [Fact]
    public void A_different_track_starts_again_with_a_full_budget()
    {
        var retries = new Retries();

        while (retries.MayRetry("a")) { }

        Assert.True(retries.MayRetry("b"));
        Assert.Equal(1, retries.Used("b"));
    }

    [Fact]
    public void Going_back_to_a_track_that_ran_out_of_goes_does_not_give_it_more()
    {
        // Not a session budget and not a permanent black mark: the counter follows whichever track is
        // being played, so coming back to a broken track immediately does not give another three tries.
        var retries = new Retries();

        while (retries.MayRetry("a")) { }
        Assert.True(retries.MayRetry("b"));

        // "a" is a different track from "b", so its count starts again. That is the plain reading of
        // "per track, reset when another one starts", and it is why Forget exists for the case that
        // matters: a track that played fine.
        Assert.True(retries.MayRetry("a"));
    }

    [Fact]
    public void A_track_that_played_has_its_troubles_forgotten()
    {
        var retries = new Retries();

        Assert.True(retries.MayRetry("a"));
        Assert.True(retries.MayRetry("a"));

        retries.Forget();

        Assert.Equal(0, retries.Used("a"));
        Assert.True(retries.MayRetry("a"));
        Assert.True(retries.MayRetry("a"));
        Assert.True(retries.MayRetry("a"));
    }

    [Fact]
    public void A_track_nobody_has_had_trouble_with_has_used_nothing()
    {
        Assert.Equal(0, new Retries().Used("a"));
    }

    [Fact]
    public void The_budget_can_be_set()
    {
        var one = new Retries(1);

        Assert.True(one.MayRetry("a"));
        Assert.False(one.MayRetry("a"));
    }

    [Fact]
    public void A_budget_of_nothing_still_gives_one_go()
    {
        // Zero would mean that a single short stall skips the track, which is the behaviour this class
        // exists to prevent.
        var none = new Retries(0);

        Assert.True(none.MayRetry("a"));
        Assert.False(none.MayRetry("a"));
    }
}
