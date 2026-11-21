using RPM.Core.Lyrics;

namespace RPM.Core.Tests;

/// <summary>The steady clock the karaoke words are lit by, as the Android phone app's KaraokeClockTest
/// checks it.</summary>
public class KaraokeClockTests
{
    private const double Near = 1e-9;

    [Fact]
    public void It_follows_an_even_position()
    {
        var clock = new KaraokeClock();
        Assert.Equal(10.0, clock.Seconds(10.0, true, 1.0), Near);
        Assert.Equal(10.5, clock.Seconds(10.5, true, 1.5), Near);
        Assert.Equal(12.0, clock.Seconds(12.0, true, 3.0), Near);
    }

    [Fact]
    public void A_staircase_is_read_as_an_even_movement_that_never_stands_still()
    {
        // The desktop's position, read every frame: it moves in 50 ms steps.
        var clock = new KaraokeClock();
        var last = clock.Seconds(10.0, true, 0);
        for (var frame = 1; frame <= 600; frame++)
        {
            var now = frame / 60.0;
            var reported = 10.0 + (Math.Floor(now / 0.05) * 0.05);
            var shown = clock.Seconds(reported, true, now);
            Assert.True(shown > last, "the clock moves on every frame");
            last = shown;
        }

        // After ten seconds it stands in the middle of the steps.
        Assert.Equal(10.0 + 10.0 - 0.025, last, 0.01);
    }

    [Fact]
    public void A_step_back_is_taken_up_slowly_and_never_goes_back()
    {
        var clock = new KaraokeClock();
        clock.Seconds(10.0, true, 0);
        var last = 10.0;

        // From here the player reports 200 ms less than the wall clock carries it to.
        for (var frame = 1; frame <= 300; frame++)
        {
            var now = frame * 0.016;
            var shown = clock.Seconds(10.0 + now - 0.2, true, now);
            Assert.True(shown >= last);
            last = shown;
        }

        // After almost five seconds the step is taken up.
        Assert.Equal(10.0 + (300 * 0.016) - 0.2, last, 0.005);
    }

    [Fact]
    public void A_step_forward_is_taken_up_slowly()
    {
        var clock = new KaraokeClock();
        clock.Seconds(10.0, true, 0);

        // 200 ms ahead after 100 ms: the clock moves at most a twentieth faster.
        Assert.Equal(10.105, clock.Seconds(10.3, true, 0.1), Near);
    }

    [Fact]
    public void A_seek_and_a_pause_go_straight_there()
    {
        var clock = new KaraokeClock();
        clock.Seconds(10.0, true, 0);
        Assert.Equal(60.0, clock.Seconds(60.0, true, 0.1), Near);
        Assert.Equal(30.0, clock.Seconds(30.0, false, 0.2), Near);
    }
}
