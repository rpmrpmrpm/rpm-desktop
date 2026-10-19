using RPM.Core.Mixing;

namespace RPM.Core.Tests;

/// <summary>
/// What the faders do across a changeover: the shape of the sweep, and whether it is the shape
/// everything downstream expects.
///
/// <para>The levels are linear amplitude, the unit the deck faders take, and the sweep through
/// them is deliberately NOT linear. Two uncorrelated records at half amplitude each sum to about
/// seven tenths of one, so a straight line from full to silence sags in the middle, which is the
/// one place a changeover should be at its strongest. Sine and cosine legs hold the sum of the
/// POWERS flat instead, and that is the property tested here: not that the line is straight, but
/// that the power is level and that the curve never steps.</para>
/// </summary>
public class VolumeSweepTests
{
    /// <summary>How finely the sweep is walked. Far finer than a block, so a step that a listener
    /// would hear as a click cannot fall between two samples of it.</summary>
    private const int Steps = 20_000;

    private static IEnumerable<double> Sweep()
    {
        for (int i = 0; i <= Steps; i++)
            yield return (double)i / Steps;
    }

    // ── the crossfade ────────────────────────────────────────────────────────

    /// <summary>
    /// The ends are the ends: the record playing is alone at the start, and gone by
    /// <see cref="Crossfader.OutBy"/>, which is before the changeover finishes, on purpose.
    /// </summary>
    [Fact]
    public void The_sweep_starts_on_one_record_and_ends_on_the_other()
    {
        var start = Crossfader.At(0);
        Assert.Equal(1, start.Outgoing, 9);
        Assert.Equal(0, start.Incoming, 9);

        var crossed = Crossfader.At(Crossfader.OutBy);
        Assert.Equal(0, crossed.Outgoing, 9);
        Assert.Equal(1, crossed.Incoming, 9);

        // And it stays there for the last quarter rather than coming back.
        var end = Crossfader.At(1);
        Assert.Equal(0, end.Outgoing, 9);
        Assert.Equal(1, end.Incoming, 9);
    }

    /// <summary>
    /// Neither leg ever doubles back. A fader that rises again half way through a changeover brings
    /// back, for a moment, a record that has already faded out. That is heard as a mistake even when
    /// nobody can say what it was.
    /// </summary>
    [Fact]
    public void Neither_leg_ever_turns_back_on_itself()
    {
        double lastOut = double.MaxValue, lastIn = double.MinValue;

        foreach (double t in Sweep())
        {
            var (outgoing, incoming) = Crossfader.At(t);

            Assert.True(outgoing <= lastOut + 1e-12, $"the outgoing fader rose at t={t:F5}");
            Assert.True(incoming >= lastIn - 1e-12, $"the incoming fader fell at t={t:F5}");

            lastOut = outgoing;
            lastIn = incoming;
        }
    }

    /// <summary>
    /// Both legs stay inside the range a fader has. Deck.Fader clamps, so a curve that goes outside
    /// it is not heard as an overshoot but as the curve flattening off where it was meant to be
    /// moving, which is far harder to see in a waveform.
    /// </summary>
    [Fact]
    public void Neither_leg_ever_leaves_the_range_a_fader_has()
    {
        foreach (double t in Sweep())
        {
            var (outgoing, incoming) = Crossfader.At(t);
            Assert.InRange(outgoing, 0, 1);
            Assert.InRange(incoming, 0, 1);
        }
    }

    /// <summary>
    /// The point of the shape: two uncorrelated records summing to one record's worth of power, the
    /// whole way across. A straight line does not do this: a linear fade is 3 dB down in the middle,
    /// which is the sag the sine and cosine legs exist to remove.
    /// </summary>
    [Fact]
    public void The_two_records_sum_to_one_records_power_the_whole_way_across()
    {
        double worst = 1, worstAt = 0;

        foreach (double t in Sweep())
        {
            var (outgoing, incoming) = Crossfader.At(t);
            double power = outgoing * outgoing + incoming * incoming;

            if (Math.Abs(power - 1) > Math.Abs(worst - 1))
            {
                worst = power;
                worstAt = t;
            }
        }

        Assert.True(
            Math.Abs(worst - 1) <= 1e-9,
            $"the mix summed to {worst:F6} of one record's power at t={worstAt:F5}, " +
            $"{10 * Math.Log10(worst):+0.00;-0.00} dB");
    }

    /// <summary>
    /// It is a sweep, not a staircase. Read at twenty thousand points across the changeover, no two
    /// neighbouring points are more than a tiny step apart, so however finely the audio thread
    /// samples the curve, it never finds a jump in it.
    /// </summary>
    [Fact]
    public void The_sweep_has_no_step_in_it_at_any_resolution()
    {
        var last = Crossfader.At(0);

        // The largest move the curve can make between two samples of it, which is at the steepest
        // point of a quarter sine: (pi/2)/OutBy per unit of t, times the step.
        double most = Math.PI / 2 / Crossfader.OutBy / Steps * 1.05;

        foreach (double t in Sweep())
        {
            var now = Crossfader.At(t);

            Assert.True(
                Math.Abs(now.Outgoing - last.Outgoing) <= most,
                $"the outgoing fader jumped {Math.Abs(now.Outgoing - last.Outgoing):F6} at t={t:F5}");
            Assert.True(
                Math.Abs(now.Incoming - last.Incoming) <= most,
                $"the incoming fader jumped {Math.Abs(now.Incoming - last.Incoming):F6} at t={t:F5}");

            last = now;
        }
    }

    /// <summary>
    /// Half way through the crossing the two records are level with each other, which is where what
    /// the room is listening to changes over. <see cref="Crossfader.Balance"/> is what the mix lab
    /// draws that moment at, so it has to be the moment and not near it.
    /// </summary>
    [Fact]
    public void The_balance_point_is_where_the_two_records_are_actually_level()
    {
        var (outgoing, incoming) = Crossfader.At(Crossfader.Balance);
        Assert.Equal(outgoing, incoming, 9);
    }

    // ── the drawn arc ────────────────────────────────────────────────────────

    /// <summary>
    /// The arc a hand starts from is the sound the app would have made anyway. FadeArc.EqualPower
    /// says so: it is straight lines between nine handles, where the crossfader is a true cosine. So
    /// this tests how much is lost between the two.
    ///
    /// <para>Half a dB, which is under what anybody hears on a moving fader. If a handle is ever
    /// added or removed, this shows whether the shape survived it.</para>
    /// </summary>
    [Fact]
    public void The_arc_a_hand_starts_from_is_the_crossfade_it_replaces()
    {
        var arc = FadeArc.EqualPower();
        double worst = 0, worstAt = 0;

        foreach (double t in Sweep())
        {
            var drawn = arc.LevelsAt(t);
            var made = Crossfader.At(t);

            double off = Math.Max(
                Math.Abs(drawn.Outgoing - made.Outgoing),
                Math.Abs(drawn.Incoming - made.Incoming));

            if (off > worst)
            {
                worst = off;
                worstAt = t;
            }
        }

        // In amplitude, at the level where a difference would matter most.
        Assert.True(
            worst <= 0.06,
            $"the drawn arc is {worst:F4} from the crossfade it stands in for, at t={worstAt:F5}");
    }

    /// <summary>
    /// The drawn arc holds the power flat too, which is the property it was laid onto handles to
    /// keep. The tolerance is looser than the crossfader's own because straight lines between handles
    /// cannot be a cosine exactly, but it is well inside the third of a dB nobody hears.
    /// </summary>
    [Fact]
    public void The_drawn_arc_keeps_the_power_flat_as_well()
    {
        var arc = FadeArc.EqualPower();
        double worst = 1, worstAt = 0;

        foreach (double t in Sweep())
        {
            var (outgoing, incoming) = arc.LevelsAt(t);
            double power = outgoing * outgoing + incoming * incoming;

            if (Math.Abs(power - 1) > Math.Abs(worst - 1))
            {
                worst = power;
                worstAt = t;
            }
        }

        double db = 10 * Math.Log10(worst);
        Assert.True(
            Math.Abs(db) <= 0.35,
            $"the drawn arc summed to {db:+0.00;-0.00} dB of one record's power at t={worstAt:F5}");
    }
}
