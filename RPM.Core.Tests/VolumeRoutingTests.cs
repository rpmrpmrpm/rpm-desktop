using RPM.Core.Audio;

namespace RPM.Core.Tests;

public class VolumeRoutingTests
{
    [Fact]
    public void WiredSpeakersKeepOurOwnGain()
    {
        Assert.Equal(VolumeKnob.App, VolumeRouting.Choose(casting: false, hardware: false));
        Assert.Equal(60, VolumeRouting.GainFor(VolumeKnob.App, 60));
    }

    [Fact]
    public void ABluetoothEndpointTakesTheKnobAndOurGainGoesToFull()
    {
        var knob = VolumeRouting.Choose(casting: false, hardware: true);

        Assert.Equal(VolumeKnob.Bluetooth, knob);

        // The speaker is at 60 now, so the samples must not also be at 60: that would be 36.
        Assert.Equal(100, VolumeRouting.GainFor(knob, 60));
    }

    [Fact]
    public void CastingOutranksTheLocalEndpointEvenWhenItIsBluetooth()
    {
        // The Bluetooth speaker is still the machine's output, but no music is going to it.
        Assert.Equal(VolumeKnob.Cast, VolumeRouting.Choose(casting: true, hardware: true));
    }

    [Fact]
    public void CastingKeepsTheNumber()
        // The cast sink forwards it to the receiver instead of applying it: see CastSink.SetVolume.
        => Assert.Equal(35, VolumeRouting.GainFor(VolumeKnob.Cast, 35));

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(140, 100)]
    public void GainIsClampedToTheRange(int volume, int expected)
        => Assert.Equal(expected, VolumeRouting.GainFor(VolumeKnob.App, volume));

    [Fact]
    public void TheFirstReportIsBelieved()
        => Assert.True(VolumeRouting.ShouldAdopt(reported: 55, wanted: -1, sinceWriteMs: 0, echoMs: 500));

    [Fact]
    public void AReportThatAgreesWithUsIsBelieved()
        => Assert.True(VolumeRouting.ShouldAdopt(reported: 40, wanted: 40, sinceWriteMs: 0, echoMs: 500));

    [Fact]
    public void TheLevelWeJustLeftIsNotBelievedBackOverADrag()
        => Assert.False(VolumeRouting.ShouldAdopt(reported: 55, wanted: 40, sinceWriteMs: 20, echoMs: 500));

    [Fact]
    public void OnceWeHaveBeenQuietTheSpeakersOwnButtonsWin()
        => Assert.True(VolumeRouting.ShouldAdopt(reported: 70, wanted: 40, sinceWriteMs: 900, echoMs: 500));
}
