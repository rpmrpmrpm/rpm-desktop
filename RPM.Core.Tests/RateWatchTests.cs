using RPM.Core.Audio;
using RPM.Fingerprint;

namespace RPM.Core.Tests;

/// <summary>
/// Turning "this connection could not keep up" into "ask for less next time".
///
/// <para>The clock is the whole behaviour, so every test here moves it by hand. A rung given up has
/// to come back on its own, and it has to come back one at a time. A watcher that jumped straight
/// back to the top would fail again at the same place, every five minutes, for the whole journey.</para>
/// </summary>
public class RateWatchTests
{
    private static readonly AudioLadder.RateOffer Whole = AudioLadder.RateOffer.Whole;

    /// <summary>A clock the test moves.</summary>
    private sealed class Hand
    {
        public DateTime Now { get; private set; } = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

        public void Pass(TimeSpan by) => Now += by;

        public Func<DateTime> Read => () => Now;
    }

    [Fact]
    public void Asks_for_what_was_chosen_until_something_goes_wrong()
    {
        var watch = new RateWatch(new Hand().Read);

        Assert.Equal(AudioLadder.Original, watch.Ask(AudioLadder.Original, Whole));
        Assert.Equal(256, watch.Ask(256, Whole));
    }

    [Fact]
    public void One_stumble_asks_for_the_next_rung_down()
    {
        var watch = new RateWatch(new Hand().Read);

        watch.Stumbled();

        // The original is the top rung, so the rung below it is 320. That is a real drop in what has
        // to arrive per second, which is why the ladder starts there.
        Assert.Equal(320, watch.Ask(AudioLadder.Original, Whole));
    }

    [Fact]
    public void Stumbling_again_keeps_going_down()
    {
        var watch = new RateWatch(new Hand().Read);

        watch.Stumbled();
        watch.Stumbled();
        watch.Stumbled();

        Assert.Equal(192, watch.Ask(AudioLadder.Original, Whole));
    }

    [Fact]
    public void Never_goes_below_the_bottom_of_the_ladder()
    {
        var watch = new RateWatch(new Hand().Read);

        for (var i = 0; i < 20; i++) watch.Stumbled();

        Assert.Equal(96, watch.Ask(AudioLadder.Original, Whole));
    }

    [Fact]
    public void A_free_account_has_nowhere_to_step_down_to()
    {
        var watch = new RateWatch(new Hand().Read);

        watch.Stumbled();
        watch.Stumbled();

        // One rung on offer. The failure was real but was not about the rate, and answering with a
        // number that is not on the ladder would be worse than answering with the only one there is.
        Assert.Equal(AudioLadder.FreeKilobits, watch.Ask(AudioLadder.FreeKilobits, AudioLadder.RateOffer.Free));
    }

    [Fact]
    public void The_listeners_own_setting_stays_the_ceiling()
    {
        var watch = new RateWatch(new Hand().Read);

        // Nothing has gone wrong, so nothing is taken away, including from somebody who has chosen a
        // low rate for a data plan.
        Assert.Equal(128, watch.Ask(128, Whole));

        watch.Stumbled();
        Assert.Equal(96, watch.Ask(128, Whole));
    }

    [Fact]
    public void Gives_a_rung_back_after_a_quiet_stretch()
    {
        var hand = new Hand();
        var watch = new RateWatch(hand.Read);

        watch.Stumbled();
        watch.Stumbled();
        Assert.Equal(256, watch.Ask(AudioLadder.Original, Whole));

        hand.Pass(RateWatch.Patience - TimeSpan.FromSeconds(1));
        Assert.Equal(256, watch.Ask(AudioLadder.Original, Whole));   // not yet

        hand.Pass(TimeSpan.FromSeconds(2));
        Assert.Equal(320, watch.Ask(AudioLadder.Original, Whole));   // one rung, not all of them
    }

    [Fact]
    public void A_long_quiet_stretch_walks_all_the_way_back_up()
    {
        var hand = new Hand();
        var watch = new RateWatch(hand.Read);

        watch.Stumbled();
        watch.Stumbled();
        watch.Stumbled();

        hand.Pass(TimeSpan.FromHours(1));

        Assert.Equal(AudioLadder.Original, watch.Ask(AudioLadder.Original, Whole));
    }

    [Fact]
    public void A_stumble_during_the_climb_starts_the_wait_again()
    {
        var hand = new Hand();
        var watch = new RateWatch(hand.Read);

        watch.Stumbled();
        watch.Stumbled();

        hand.Pass(RateWatch.Patience);
        Assert.Equal(320, watch.Ask(AudioLadder.Original, Whole));

        watch.Stumbled();
        Assert.Equal(256, watch.Ask(AudioLadder.Original, Whole));

        // And the clock for the NEXT rung back runs from the new stumble, not from the old one.
        hand.Pass(RateWatch.Patience - TimeSpan.FromSeconds(1));
        Assert.Equal(256, watch.Ask(AudioLadder.Original, Whole));
    }

    [Fact]
    public void Moving_to_another_connection_forgets_what_this_one_taught()
    {
        var watch = new RateWatch(new Hand().Read);

        watch.Stumbled();
        watch.Stumbled();

        watch.Forget();

        // Arriving on a good connection after a bad train journey must not keep the rate held down for
        // no visible reason.
        Assert.Equal(AudioLadder.Original, watch.Ask(AudioLadder.Original, Whole));
    }
}
