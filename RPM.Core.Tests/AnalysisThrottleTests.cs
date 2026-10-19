using RPM.Core.Analysis;

namespace RPM.Core.Tests;

/// <summary>
/// Holds the throttle to the promise it makes: that analysing a library never costs the listener the
/// thing they are doing while it happens.
///
/// <para>Every number in here is one the analyser cannot check for itself at runtime. Too generous
/// and the app is slow to respond and playback skips, which is what this prevents; too strict and a
/// library takes a week. Both failures are silent, which is why they are pinned here.</para>
/// </summary>
public class AnalysisThrottleTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static MachineState Machine(
        int cores = 8, long available = 16 * Gb, bool playing = false, bool mixing = false) =>
        new(cores, available, playing, mixing);

    [Fact]
    public void An_idle_machine_gets_the_cores_it_can_spare_and_no_pause()
    {
        var pace = AnalysisThrottle.ForBackground(Machine(cores: 8));

        Assert.Equal(4, pace.Slots);              // eight cores, two reserved, capped at four
        Assert.Equal(TimeSpan.Zero, pace.Breath);
        Assert.False(pace.Deferred);
    }

    [Fact]
    public void Two_cores_still_analyse_one_track_rather_than_none()
    {
        Assert.Equal(1, AnalysisThrottle.ForBackground(Machine(cores: 2)).Slots);
        Assert.Equal(1, AnalysisThrottle.ForBackground(Machine(cores: 1)).Slots);
    }

    [Fact]
    public void Nothing_ever_runs_more_than_the_ceiling()
    {
        Assert.Equal(AnalysisThrottle.MaxSlots, AnalysisThrottle.ForBackground(Machine(cores: 64)).Slots);
    }

    [Fact]
    public void Music_playing_takes_the_machine_back()
    {
        var pace = AnalysisThrottle.ForBackground(Machine(cores: 16, playing: true));

        Assert.Equal(2, pace.Slots);
        Assert.True(pace.Breath > TimeSpan.Zero);
    }

    /// <summary>A set is two decodes, a resampler and a changeover that has to land on a beat. It
    /// gets the machine; the sweep gets what is left, which is one track at a time.</summary>
    [Fact]
    public void A_mix_takes_it_back_further_still()
    {
        var playing = AnalysisThrottle.ForBackground(Machine(cores: 16, playing: true));
        var mixing = AnalysisThrottle.ForBackground(Machine(cores: 16, playing: true, mixing: true));

        Assert.Equal(1, mixing.Slots);
        Assert.True(mixing.Breath > playing.Breath);
    }

    /// <summary>One track through the beat model peaks near a gigabyte. Two on a machine with two
    /// gigabytes free would make the machine start paging memory to disk.</summary>
    [Fact]
    public void Memory_is_what_binds_on_a_small_machine_not_cores()
    {
        var pace = AnalysisThrottle.ForBackground(Machine(cores: 16, available: 2 * Gb));

        Assert.Equal(1, pace.Slots);
        Assert.False(pace.Deferred);
    }

    [Fact]
    public void A_machine_with_no_memory_to_spare_waits_instead_of_swapping()
    {
        var pace = AnalysisThrottle.ForBackground(Machine(available: 1 * Gb));

        Assert.True(pace.Deferred);
    }

    /// <summary>The collector has not run yet and gives no figure. That is not a reason to stop,
    /// only a reason not to use memory as the limit.</summary>
    [Fact]
    public void An_unknown_amount_of_memory_is_not_read_as_none()
    {
        var pace = AnalysisThrottle.ForBackground(Machine(cores: 8, available: 0));

        Assert.Equal(4, pace.Slots);
        Assert.False(pace.Deferred);
    }

    /// <summary>Something is waiting on a direct request (the next record on a deck). It does not pause
    /// between tracks and it is never deferred, because the alternative to answering late is a
    /// record played without a grid.</summary>
    [Fact]
    public void A_track_something_is_waiting_on_is_not_throttled()
    {
        var pace = AnalysisThrottle.ForDirectAsk(Machine(available: 1 * Gb, playing: true, mixing: true));

        Assert.False(pace.Deferred);
        Assert.Equal(TimeSpan.Zero, pace.Breath);
        Assert.True(pace.Slots >= 1);
    }
}
