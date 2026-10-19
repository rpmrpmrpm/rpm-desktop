using RPM.Core.Mixing;

namespace RPM.Core.Tests;

/// <summary>
/// The two rolls a skip is made of: the bar going round and round, and the beat roll that shortens
/// as it goes.
/// </summary>
public class RollsTests
{
    private const long Bar = 4410;              // a tenth of a second stands in for a bar
    private const long Beat = Bar / 4;
    private const int Bars = 8;

    [Fact]
    public void The_plain_roll_is_the_bar_and_nothing_else()
    {
        var steps = Rolls.Out(RollStyle.Bar, Bar, Bars);

        Assert.Single(steps);
        Assert.Equal(Bar, steps[0].SliceFrames);
        Assert.Equal(Bar * Bars, steps[0].LengthFrames);
    }

    [Fact]
    public void The_beat_roll_halves_twice_and_still_fills_the_roll()
    {
        var steps = Rolls.Out(RollStyle.Beat, Bar, Bars);

        Assert.Equal(3, steps.Count);

        // Two beats, then one, then half of one: each stage a shorter read of the one before it.
        Assert.Equal(2 * Beat, steps[0].SliceFrames);
        Assert.Equal(Beat, steps[1].SliceFrames);
        Assert.Equal(Beat / 2, steps[2].SliceFrames);

        // Half the roll, then a quarter, then a quarter, and exactly the roll in total, however the
        // division rounded.
        Assert.Equal(Bar * Bars, steps.Sum(step => step.LengthFrames));
        Assert.Equal(Bar * Bars / 2, steps[0].LengthFrames);
        Assert.Equal(Bar * Bars / 4, steps[1].LengthFrames);
    }

    [Fact]
    public void Every_slice_is_a_prefix_of_the_first()
    {
        // What lets the deck capture once and read less of it as the schedule goes on.
        var steps = Rolls.Out(RollStyle.Beat, Bar, Bars);

        foreach (var step in steps)
            Assert.True(step.SliceFrames <= steps[0].SliceFrames,
                $"a slice of {step.SliceFrames} is longer than the {steps[0].SliceFrames} captured");
    }

    [Fact]
    public void A_record_coming_in_on_the_beat_roll_halves_twice_as_well()
    {
        var steps = Rolls.In(RollStyle.Beat, Bar, 4);

        Assert.Equal(3, steps.Count);

        // The first beat, then half of it, then half of that. The kick is on its own by the last bar.
        Assert.Equal(Beat, steps[0].SliceFrames);
        Assert.Equal(Beat / 2, steps[1].SliceFrames);
        Assert.Equal(Beat / 4, steps[2].SliceFrames);

        // Two bars, then one, then one, and exactly the four in total.
        Assert.Equal(Bar * 2, steps[0].LengthFrames);
        Assert.Equal(Bar, steps[1].LengthFrames);
        Assert.Equal(Bar, steps[2].LengthFrames);
        Assert.Equal(Bar * 4, steps.Sum(step => step.LengthFrames));

        // And every slice is still a prefix of the first, which is what lets one capture serve them.
        foreach (var step in steps)
            Assert.True(step.SliceFrames <= steps[0].SliceFrames);
    }

    [Fact]
    public void A_record_coming_in_on_the_plain_roll_arrives_a_bar_at_a_time()
    {
        var steps = Rolls.In(RollStyle.Bar, Bar, 4);

        Assert.Single(steps);
        Assert.Equal(Bar, steps[0].SliceFrames);
        Assert.Equal(Bar * 4, steps[0].LengthFrames);
    }

    [Fact]
    public void A_roll_in_ends_when_the_roll_does()
    {
        // The looping belongs to the transition and stops with it: however much the slices tighten,
        // the schedule adds up to the roll and not a frame more, so nothing is still repeating once
        // the record it arrived over has gone.
        //
        // How much of the record the roll CONSUMES is a separate question and is not settled here: it
        // is the deck's capture length. See A_roll_in_hands_the_record_on_at_a_whole_bar.
        foreach (var style in new[] { RollStyle.Bar, RollStyle.Beat })
            Assert.Equal(Bar * 4, Rolls.In(style, Bar, bars: 4).Sum(step => step.LengthFrames));
    }

    [Fact]
    public void A_bar_too_short_to_cut_into_beats_is_left_whole()
    {
        // Invalid input must give a sane result: a slice of no frames would be a loop of nothing.
        var steps = Rolls.Out(RollStyle.Beat, barFrames: 4, bars: 2);

        Assert.Single(steps);
        Assert.Equal(4, steps[0].SliceFrames);
    }

    [Fact]
    public void Luck_decides_which_roll_a_changeover_gets()
    {
        Assert.Equal(RollStyle.Beat, Rolls.Choose(0));
        Assert.Equal(RollStyle.Beat, Rolls.Choose(Rolls.BeatRollChance - 0.01));
        Assert.Equal(RollStyle.Bar, Rolls.Choose(Rolls.BeatRollChance));
        Assert.Equal(RollStyle.Bar, Rolls.Choose(0.99));
    }
}
