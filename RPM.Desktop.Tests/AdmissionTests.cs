using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Tests;

/// <summary>
/// Whether a background pass may do its next unit of work.
///
/// <para>The whole startup depends on this rule. It can be tested here, outside the app, because it
/// is a pure function: no window, no dispatcher, no library, no thread. The answer from
/// <see cref="Admission"/> depends only on five readings, so the app's background policy fits on
/// one screen.</para>
///
/// <para>Without this rule, about eleven passes start within two seconds of the library opening,
/// the interface waits about 2.7 s for its own playlist tree behind one of them, and the splash
/// stays up for about 12 s with no animation. With the rule, the splash goes after about 4 s, with
/// its fade, and no UI delay is longer than 250 ms after the eighth second.</para>
/// </summary>
public class AdmissionTests
{
    /// <summary>A drawing thread that is answering on time and has been for a while.</summary>
    private static Verdict Ask(
        Phase reached = Phase.Ready,
        Wave opened = Wave.Last,
        Phase notBefore = Phase.Ready,
        Wave wave = Wave.Last,
        Lane lane = Lane.Network,
        int runningInLane = 0,
        double lateMs = 8,
        double heldForMs = 0,
        int calm = UiPulse.CalmTurns)
        => Admission.Ask(lateMs, heldForMs, calm, reached, opened, notBefore, wave, lane, runningInLane);

    [Fact]
    public void A_free_interface_lets_a_pass_through()
    {
        var verdict = Ask();

        Assert.True(verdict.Go);
        Assert.Equal(Held.None, verdict.Why);
        Assert.Equal(0, verdict.WaitMs);
    }

    [Fact]
    public void Nothing_runs_before_the_phase_it_asked_for()
    {
        // The interface reads as free, but only because there is no interface yet.
        var verdict = Ask(reached: Phase.Opening, notBefore: Phase.Ready);

        Assert.False(verdict.Go);
        Assert.Equal(Held.TooEarly, verdict.Why);
    }

    /// <summary>The splash is the one thing on screen during <see cref="Phase.Opening"/>, so a pass
    /// that named <see cref="Phase.Loading"/> still waits. The phase comparison decides, not the
    /// name.</summary>
    [Fact]
    public void A_pass_due_at_loading_waits_through_opening_and_runs_at_ready()
    {
        Assert.False(Ask(reached: Phase.Opening, notBefore: Phase.Loading).Go);
        Assert.True(Ask(reached: Phase.Loading, notBefore: Phase.Loading, wave: Wave.First, opened: Wave.First).Go);
        Assert.True(Ask(reached: Phase.Ready, notBefore: Phase.Loading, wave: Wave.First, opened: Wave.First).Go);
    }

    [Fact]
    public void A_later_wave_waits_for_its_turn()
    {
        var verdict = Ask(opened: Wave.First, wave: Wave.Last);

        Assert.False(verdict.Go);
        Assert.Equal(Held.WaveShut, verdict.Why);
    }

    /// <summary>The order question is answered without looking at the interface at all. A pass that is
    /// not due yet is not due on a quiet machine either, and a wait computed from a calm reading would
    /// make it ask again every thirty-two milliseconds for the next four seconds.</summary>
    [Fact]
    public void A_pass_that_is_not_due_backs_all_the_way_off()
    {
        Assert.Equal(Admission.CeilingMs, Ask(reached: Phase.Cold, notBefore: Phase.Ready).WaitMs);
        Assert.Equal(Admission.CeilingMs, Ask(opened: Wave.First, wave: Wave.Last).WaitMs);
    }

    /// <summary>An outage IN PROGRESS. This is the reading that matters during a hang, and the last
    /// completed turn cannot give it: while the UI thread is blocked, no turns complete.</summary>
    [Fact]
    public void A_thread_that_is_held_right_now_turns_a_pass_away()
    {
        var verdict = Ask(heldForMs: 900);

        Assert.False(verdict.Go);
        Assert.Equal(Held.UiHeld, verdict.Why);
    }

    [Fact]
    public void A_thread_that_is_answering_but_not_steadily_turns_a_pass_away()
    {
        var verdict = Ask(calm: UiPulse.CalmTurns - 1, lateMs: 120);

        Assert.False(verdict.Go);
        Assert.Equal(Held.UiBusy, verdict.Why);
    }

    /// <summary>How long a pass waits is computed from the interface, not chosen: the 1,800ms here
    /// comes from a 900ms outage.</summary>
    [Fact]
    public void The_busier_the_interface_the_longer_the_pass_stays_away()
    {
        Assert.Equal(1800, Ask(heldForMs: 900).WaitMs);
        Assert.Equal(200, Ask(heldForMs: 100).WaitMs);

        // Limited at both ends: a wait shorter than one pulse turn would get the same reading again,
        // and a wait longer than the ceiling would not notice the interface becoming free for the
        // rest of the wait.
        //
        // The floor is reached through LATENESS, not through a held thread, and that comes from the
        // rule, not from this test: a thread only counts as held past UiPulse.HeldMs, so doubling any
        // held time already clears the floor. The floor matters for the other refusal: a thread that
        // answers a few milliseconds late and is not yet steady.
        Assert.Equal(Admission.FloorMs, Ask(calm: 0, lateMs: 4).WaitMs);
        Assert.Equal(Admission.CeilingMs, Ask(heldForMs: 30_000).WaitMs);
    }

    /// <summary>One at a time in the library lane. The database gate serialises them anyway, so a
    /// second pass there does not go twice as fast; it waits in the same queue as the
    /// window.</summary>
    [Fact]
    public void Only_one_pass_at_a_time_touches_the_library()
    {
        Assert.Equal(1, Admission.Width(Lane.Library));

        Assert.True(Ask(lane: Lane.Library, runningInLane: 0).Go);

        var second = Ask(lane: Lane.Library, runningInLane: 1);
        Assert.False(second.Go);
        Assert.Equal(Held.LaneFull, second.Why);
    }

    /// <summary>A request over the wire holds nothing on this machine while it waits, so several are
    /// allowed. The limit there is about not opening forty connections at once.</summary>
    [Fact]
    public void Several_requests_may_be_in_flight_at_once()
    {
        Assert.True(Admission.Width(Lane.Network) > 1);
        Assert.True(Ask(lane: Lane.Network, runningInLane: Admission.Width(Lane.Network) - 1).Go);
        Assert.False(Ask(lane: Lane.Network, runningInLane: Admission.Width(Lane.Network)).Go);
    }

    /// <summary>The user is listening to music on this machine, so background work never takes all
    /// of it.</summary>
    [Fact]
    public void The_processor_lane_never_takes_the_whole_machine()
    {
        var width = Admission.Width(Lane.Processor);

        Assert.InRange(width, 1, 2);
        Assert.True(width <= Math.Max(Environment.ProcessorCount / 4, 1));
    }

    /// <summary>A full lane is asked again soon: it is the only refusal that has nothing to do with
    /// the interface, so what it waits for can end at any moment.</summary>
    [Fact]
    public void A_full_lane_is_asked_again_promptly()
    {
        Assert.Equal(Admission.FloorMs, Ask(lane: Lane.Library, runningInLane: 1).WaitMs);
    }

    /// <summary>The order the reasons are tested in is deliberate: an outage in progress comes before
    /// an old calm count, and both come before a full lane. A pass told "the lane is full" during
    /// a two-second hang would come back in thirty-two milliseconds and ask again sixty times.</summary>
    [Fact]
    public void A_held_thread_is_reported_before_a_full_lane()
    {
        var verdict = Ask(lane: Lane.Library, runningInLane: 1, heldForMs: 900);

        Assert.Equal(Held.UiHeld, verdict.Why);
    }
}
