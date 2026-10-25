using RPM.Core.Viz;

namespace RPM.Core.Tests;

/// <summary>
/// The screensaver of the full-screen player (<see cref="Screensaver"/>): when the screens go, and which
/// presses bring them back without doing anything else. The Android TV app keeps the same rule.
/// </summary>
public class ScreensaverTests
{
    private const long Minute = 60_000;

    [Fact]
    public void The_choices_step_round_from_off_to_half_an_hour()
    {
        var seen = new List<int>();
        var minutes = 0;
        for (var i = 0; i < Screensaver.Choices.Count; i++)
        {
            seen.Add(minutes);
            minutes = Screensaver.Next(minutes);
        }

        Assert.Equal([0, 1, 2, 5, 10, 15, 30], seen);
        Assert.Equal(0, minutes);
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(0, 0)]
    [InlineData(3, 2)]
    [InlineData(-1, 2)]
    public void A_stored_figure_that_is_not_a_choice_is_the_default(int stored, int expected)
        => Assert.Equal(expected, Screensaver.Minutes(stored));

    [Fact]
    public void The_time_runs_from_the_last_key()
    {
        var saver = new Screensaver(now: 0);
        Assert.Equal(2 * Minute, saver.DueIn(2, now: 0));

        saver.KeyDown(key: 40, now: Minute);
        Assert.Equal(2 * Minute, saver.DueIn(2, now: Minute));
        Assert.Equal(0, saver.DueIn(2, now: 3 * Minute));

        saver.Used(now: 3 * Minute + 500);
        Assert.Equal(2 * Minute - 500, saver.DueIn(2, now: 3 * Minute + 1000));
    }

    [Fact]
    public void Off_is_never_due()
        => Assert.Null(new Screensaver(now: 0).DueIn(0, now: 100 * Minute));

    [Fact]
    public void The_waking_press_goes_no_further_down_to_its_release()
    {
        var saver = new Screensaver(now: 0);
        Assert.False(saver.KeyDown(key: 40, now: 1));
        Assert.False(saver.KeyUp(key: 40));

        saver.PutAway();
        Assert.True(saver.Shown);

        Assert.True(saver.KeyDown(key: 13, now: 10));    // brings the screens back
        Assert.False(saver.Shown);
        Assert.True(saver.KeyDown(key: 13, now: 20));    // its repeat
        Assert.False(saver.KeyDown(key: 40, now: 30));   // another key acts
        Assert.False(saver.KeyUp(key: 40));
        Assert.True(saver.KeyUp(key: 13));                // its release
        Assert.False(saver.KeyDown(key: 13, now: 40));   // the next press acts
    }

    [Fact]
    public void The_mouse_brings_the_screens_back_too()
    {
        var saver = new Screensaver(now: 0);
        Assert.False(saver.Used(now: 1));

        saver.PutAway();
        Assert.True(saver.Used(now: 2));
        Assert.False(saver.Shown);
        Assert.False(saver.KeyDown(key: 13, now: 3));
    }
}
