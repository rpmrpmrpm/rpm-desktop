using RPM.Core.Audio;

namespace RPM.Core.Tests;

public class OutputRoutingTests
{
    private static readonly string[] Machine = ["Speakers (Realtek)", "Headphones (USB DAC)", "LG TV"];

    // ── which endpoint ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void No_choice_follows_the_machine()
    {
        Assert.Equal("Speakers (Realtek)", OutputRouting.Choose(null, Machine, "Speakers (Realtek)"));
        Assert.Equal("Speakers (Realtek)", OutputRouting.Choose("", Machine, "Speakers (Realtek)"));
    }

    [Fact]
    public void A_chosen_endpoint_the_machine_has_wins()
        => Assert.Equal("LG TV", OutputRouting.Choose("LG TV", Machine, "Speakers (Realtek)"));

    [Fact]
    public void A_chosen_endpoint_the_machine_has_not_falls_back_to_the_default()
        => Assert.Equal("Speakers (Realtek)",
                        OutputRouting.Choose("Scarlett 2i2", Machine, "Speakers (Realtek)"));

    [Fact]
    public void An_unplugged_choice_is_taken_up_again_the_moment_it_is_listed()
    {
        string[] without = ["Speakers (Realtek)"];
        Assert.Equal("Speakers (Realtek)", OutputRouting.Choose("Scarlett 2i2", without, "Speakers (Realtek)"));

        string[] with = ["Speakers (Realtek)", "Scarlett 2i2"];
        Assert.Equal("Scarlett 2i2", OutputRouting.Choose("Scarlett 2i2", with, "Speakers (Realtek)"));
    }

    [Fact]
    public void A_machine_with_no_endpoints_names_none()
        => Assert.Equal("", OutputRouting.Choose("LG TV", [], null));

    [Fact]
    public void Endpoint_names_are_matched_exactly()
        => Assert.Equal("Speakers (Realtek)", OutputRouting.Choose("lg tv", Machine, "Speakers (Realtek)"));

    // ── reading the machine's own enumeration ──────────────────────────────────────────────────────

    [Fact]
    public void The_first_endpoint_the_machine_lists_is_its_default()
        => Assert.Equal("Speakers (Realtek)", OutputRouting.ChooseFromEnumeration(null, Machine));

    /// <summary>The reason the default is read from the front of a newly enumerated list: when the
    /// machine changes its output, the same list comes back in a different order.</summary>
    [Fact]
    public void The_default_follows_the_machine_reordering_its_list()
    {
        string[] moved = ["Headphones (USB DAC)", "Speakers (Realtek)", "LG TV"];
        Assert.Equal("Headphones (USB DAC)", OutputRouting.ChooseFromEnumeration(null, moved));
    }

    [Fact]
    public void A_chosen_endpoint_outranks_the_machines_default()
        => Assert.Equal("LG TV", OutputRouting.ChooseFromEnumeration("LG TV", Machine));

    [Fact]
    public void A_machine_that_lists_nothing_names_nothing()
    {
        Assert.Equal("", OutputRouting.ChooseFromEnumeration("LG TV", []));
        Assert.Equal("", OutputRouting.ChooseFromEnumeration(null, null));
    }

    // ── whether to move ────────────────────────────────────────────────────────────────────────────

    /// <summary>The Windows case: nothing is disconnected and nothing is added, the listener has only
    /// changed the machine's output, and the sound must follow.</summary>
    [Fact]
    public void The_machine_moving_its_default_is_a_move()
        => Assert.True(OutputRouting.ShouldMove("Headphones (USB DAC)", "Speakers (Realtek)",
                                                connected: true, lost: false));

    [Fact]
    public void Staying_where_we_are_is_not_a_move()
        => Assert.False(OutputRouting.ShouldMove("Speakers (Realtek)", "Speakers (Realtek)",
                                                 connected: true, lost: false));

    [Fact]
    public void A_disconnected_endpoint_is_a_move_even_to_the_same_name()
        => Assert.True(OutputRouting.ShouldMove("Speakers (Realtek)", "Speakers (Realtek)",
                                                connected: false, lost: false));

    /// <summary>The pump's evidence beats the driver's answer: a dead speaker can be reported present
    /// for up to five seconds, and waiting for the driver to agree means waiting in silence.</summary>
    [Fact]
    public void A_device_the_pump_calls_dead_is_a_move_while_the_driver_still_calls_it_present()
        => Assert.True(OutputRouting.ShouldMove("Speakers (Realtek)", "Speakers (Realtek)",
                                                connected: true, lost: true));

    [Fact]
    public void Nowhere_to_go_is_not_a_move()
    {
        Assert.False(OutputRouting.ShouldMove("", "Speakers (Realtek)", connected: false, lost: true));
        Assert.False(OutputRouting.ShouldMove(null, "Speakers (Realtek)", connected: true, lost: false));
    }

    [Fact]
    public void The_first_move_of_a_session_has_nothing_to_compare_against()
        => Assert.True(OutputRouting.ShouldMove("Speakers (Realtek)", "", connected: true, lost: false));

    // ── a stall the pump watched, against a stall it only slept through ──

    [Fact]
    public void A_device_that_hands_nothing_back_while_the_pump_watches_is_presumed_dead()
    {
        // Fifteen blocks due and none delivered, all of it observed: the speaker is gone.
        Assert.True(OutputRouting.ShouldPresumeLost(watchedMs: 800, noProgressMs: 750, playing: true));
    }

    [Fact]
    public void A_pause_the_pump_slept_through_is_not_evidence_about_the_device()
    {
        // A garbage collection can stop every managed thread for 868ms (measured while the grid
        // rebuilt itself). Wall clock time cannot tell that from an unplugged speaker, but the pump
        // performed no waits during it, so it observed nothing and has nothing to report. Using the
        // clock here would hold the track and stop the music ten seconds later.
        Assert.False(OutputRouting.ShouldPresumeLost(watchedMs: 0, noProgressMs: 750, playing: true));
        Assert.False(OutputRouting.ShouldPresumeLost(watchedMs: 200, noProgressMs: 750, playing: true));
    }

    [Fact]
    public void A_paused_device_owes_nothing_however_long_the_silence()
    {
        // Pausing IS the device not returning buffers (that is what stalls the decoder), so silence
        // while paused means nothing.
        Assert.False(OutputRouting.ShouldPresumeLost(watchedMs: 60_000, noProgressMs: 750, playing: false));
    }

    [Fact]
    public void The_threshold_is_a_threshold_and_not_a_range()
    {
        Assert.False(OutputRouting.ShouldPresumeLost(750, 750, playing: true));   // exactly at it, not past
        Assert.True(OutputRouting.ShouldPresumeLost(751, 750, playing: true));
    }
}
