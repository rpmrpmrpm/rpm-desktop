using RPM.Core.Audio;

namespace RPM.Core.Tests;

/// <summary>
/// The sink's device watcher, as a rule. When headphones disconnect while nothing is playing, there
/// is no pump to hand the move to. If the watcher still hands it over, it logs the same device
/// change every few hundred milliseconds for ever. What is checked: with no pump the watcher moves
/// the device itself, a failed move is a stable state, and the short clock is only for a track that
/// is being held.
/// </summary>
public class DeviceWatchTests
{
    private const string Headphones = "OpenAL Soft on Headphones (WH-1000XM3)";
    private const string Steam = "OpenAL Soft on Speakers (Steam Streaming Microphone)";

    [Fact]
    public void Headphones_that_go_while_nothing_plays_are_replaced_by_the_watcher()
    {
        var step = DeviceWatch.Next(MoveCarrier.Watcher, want: Steam, intended: Headphones,
            connected: false, lost: true, unchangedSinceFailure: false);

        Assert.Equal(new WatcherStep.MoveNow(Steam), step);
    }

    [Fact]
    public void Headphones_that_go_during_a_track_are_handed_to_the_pump()
    {
        var step = DeviceWatch.Next(MoveCarrier.Pump, want: Steam, intended: Headphones,
            connected: false, lost: true, unchangedSinceFailure: false);

        Assert.Equal(new WatcherStep.HandToPump(Steam), step);
    }

    [Fact]
    public void A_move_that_failed_is_not_tried_again_until_the_machine_changes()
    {
        var settled = DeviceWatch.Next(MoveCarrier.Watcher, want: Steam, intended: Steam,
            connected: false, lost: true, unchangedSinceFailure: true);
        var changed = DeviceWatch.Next(MoveCarrier.Watcher, want: Steam, intended: Steam,
            connected: false, lost: true, unchangedSinceFailure: false);

        Assert.IsType<WatcherStep.Nothing>(settled);
        Assert.Equal(new WatcherStep.MoveNow(Steam), changed);
    }

    [Fact]
    public void A_device_that_is_where_it_should_be_is_left_alone()
    {
        foreach (var carrier in new[] { MoveCarrier.Pump, MoveCarrier.Watcher })
            Assert.IsType<WatcherStep.Nothing>(DeviceWatch.Next(carrier, want: Steam, intended: Steam,
                connected: true, lost: false, unchangedSinceFailure: false));
    }

    [Fact]
    public void A_machine_with_no_outputs_is_a_wait_not_a_move()
    {
        foreach (var carrier in new[] { MoveCarrier.Pump, MoveCarrier.Watcher })
            Assert.IsType<WatcherStep.Nothing>(DeviceWatch.Next(carrier, want: "", intended: Headphones,
                connected: false, lost: true, unchangedSinceFailure: false));
    }

    [Fact]
    public void The_short_clock_is_only_for_a_held_track()
    {
        Assert.Equal(DeviceWatch.HeldTrackMs, DeviceWatch.SleepMs(MoveCarrier.Pump, lost: true, machineAnnouncesEverything: true));

        // With no pump nothing is waiting: the machine's own device events wake the watcher, and the
        // safety check uses the ordinary interval.
        Assert.Equal(DeviceWatch.AnnouncedMs, DeviceWatch.SleepMs(MoveCarrier.Watcher, lost: true, machineAnnouncesEverything: true));
        Assert.Equal(DeviceWatch.UnannouncedMs, DeviceWatch.SleepMs(MoveCarrier.Watcher, lost: true, machineAnnouncesEverything: false));
        Assert.Equal(DeviceWatch.AnnouncedMs, DeviceWatch.SleepMs(MoveCarrier.Pump, lost: false, machineAnnouncesEverything: true));
    }
}
