using RPM.Core.Playback;

namespace RPM.Core.Tests;

/// <summary>
/// Who owns the player when somebody changes their mind during a load. Getting this wrong does not
/// throw either: it plays the track before the one that was tapped, under the name of the one that
/// was.
/// </summary>
public class StartGateTests
{
    [Fact]
    public void The_first_attempt_owns_the_player()
    {
        var gate = new StartGate();

        using var first = gate.Begin();

        Assert.True(first.IsLive);
        Assert.False(first.Token.IsCancellationRequested);
    }

    [Fact]
    public void A_later_attempt_takes_the_player_from_the_one_still_loading()
    {
        var gate = new StartGate();

        using var first = gate.Begin();
        using var second = gate.Begin();

        Assert.False(first.IsLive);
        Assert.True(second.IsLive);
    }

    /// <summary>A superseded attempt is not merely ignored: the download that the first attempt is
    /// waiting on stops and does not run to completion on somebody's data plan.</summary>
    [Fact]
    public void The_attempt_that_loses_the_player_is_cancelled()
    {
        var gate = new StartGate();

        using var first = gate.Begin();
        using var second = gate.Begin();

        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(second.Token.IsCancellationRequested);
    }

    /// <summary>The gate is asked again at the moment the source would be handed over, because
    /// cancellation does not cover everything: a read that is already past its token check returns
    /// normally.</summary>
    [Fact]
    public void Ownership_is_still_answerable_after_the_attempt_has_finished_waiting()
    {
        var gate = new StartGate();

        using var first = gate.Begin();
        var generation = first.Generation;

        using var second = gate.Begin();

        Assert.False(gate.IsLive(generation));
        Assert.True(gate.IsLive(second.Generation));
    }

    /// <summary>Stopping, or handing the player to something that does not come through the gate at
    /// all (a radio station, a DJ set).</summary>
    [Fact]
    public void Retiring_leaves_nobody_owning_the_player()
    {
        var gate = new StartGate();

        using var attempt = gate.Begin();
        gate.Retire();

        Assert.False(attempt.IsLive);
        Assert.True(attempt.Token.IsCancellationRequested);
    }

    [Fact]
    public void Retiring_twice_is_harmless()
    {
        var gate = new StartGate();

        using var attempt = gate.Begin();
        gate.Retire();
        gate.Retire();

        Assert.False(attempt.IsLive);
    }

    /// <summary>Every attempt gets its own number, so one that finished long ago can never be mistaken
    /// for the one running now.</summary>
    [Fact]
    public void Generations_never_repeat()
    {
        var gate = new StartGate();

        var seen = new HashSet<int>();
        for (var i = 0; i < 10; i++)
        {
            using var attempt = gate.Begin();
            Assert.True(seen.Add(attempt.Generation));
        }
    }

    /// <summary>The caller's own token still ends the attempt (a screen left, a service disposed)
    /// without anything else having claimed the player.</summary>
    [Fact]
    public void The_callers_own_cancellation_ends_the_attempt()
    {
        var gate = new StartGate();
        using var outer = new CancellationTokenSource();

        using var attempt = gate.Begin(outer.Token);
        outer.Cancel();

        Assert.True(attempt.Token.IsCancellationRequested);

        // And it is still the live one: nobody took the player, the attempt simply gave up. The
        // service reads this to decide whether it may clear the spinner it put up.
        Assert.True(attempt.IsLive);
    }

    /// <summary>An attempt that ends while it still owns the player leaves the gate usable: the next
    /// tap must not fail trying to cancel a token source that was already released.</summary>
    [Fact]
    public void Disposing_the_live_attempt_does_not_break_the_next_one()
    {
        var gate = new StartGate();

        gate.Begin().Dispose();

        using var next = gate.Begin();

        Assert.True(next.IsLive);
        Assert.False(next.Token.IsCancellationRequested);
    }

    [Fact]
    public void Retiring_after_the_live_attempt_was_disposed_does_not_throw()
    {
        var gate = new StartGate();

        gate.Begin().Dispose();

        gate.Retire();
    }
}
