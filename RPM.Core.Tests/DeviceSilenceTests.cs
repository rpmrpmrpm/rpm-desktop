using RPM.Core.Audio;

namespace RPM.Core.Tests;

/// <summary>
/// <see cref="DeviceSilence"/>: silence counts against a device only while it owes audio.
/// </summary>
public class DeviceSilenceTests
{
    private const int Limit = 750;
    private const int Poll = 5;

    private static void Wait(DeviceSilence silence, int ms, bool owed)
    {
        for (int waited = 0; waited < ms; waited += Poll)
            silence.Waited(Poll, owed);
    }

    [Fact]
    public void A_long_pause_is_not_read_as_a_dead_speaker_when_play_is_pressed()
    {
        // Paused for 1.6 s, then play. The pause must not count as silence, or the first check after
        // play reports nothing came back and reopens a healthy speaker.
        var silence = new DeviceSilence();

        Wait(silence, 1595, owed: false);

        Assert.Equal(0, silence.WatchedMs);
        Assert.False(silence.PresumeLost(Limit, owed: true));
    }

    [Fact]
    public void A_device_that_returns_nothing_while_playing_is_presumed_lost()
    {
        var silence = new DeviceSilence();

        Wait(silence, 800, owed: true);

        Assert.True(silence.PresumeLost(Limit, owed: true));
    }

    [Fact]
    public void Silence_either_side_of_a_pause_adds_up()
    {
        // Both periods were watched while the device owed audio. The pause between them was not.
        var silence = new DeviceSilence();

        Wait(silence, 500, owed: true);
        Wait(silence, 5_000, owed: false);
        Wait(silence, 300, owed: true);

        Assert.Equal(800, silence.WatchedMs);
        Assert.True(silence.PresumeLost(Limit, owed: true));
    }

    [Fact]
    public void A_buffer_handed_back_starts_the_count_again()
    {
        var silence = new DeviceSilence();

        Wait(silence, 700, owed: true);
        silence.Heard();
        Wait(silence, 700, owed: true);

        Assert.False(silence.PresumeLost(Limit, owed: true));
    }

    [Fact]
    public void Nothing_is_presumed_while_paused()
    {
        var silence = new DeviceSilence();

        Wait(silence, 800, owed: true);

        Assert.False(silence.PresumeLost(Limit, owed: false));
    }
}
