using RPM.Core.Recording;

namespace RPM.Core.Tests;

public class InputDevicesTests
{
    [Theory]
    [InlineData("MacBook Pro Microphone")]
    [InlineData("Built-in Microphone")]
    [InlineData("External Microphone")]
    [InlineData("Microphone (Realtek High Definition Audio)")]
    [InlineData("USB Mic")]
    [InlineData("Mic 1")]
    [InlineData("FaceTime HD Camera")]
    [InlineData("Jabra Headset")]
    [InlineData("AirPods Pro")]
    public void Microphones_are_recognised(string name) =>
        Assert.Equal(InputDeviceKind.Microphone, InputDevices.Classify(name));

    [Theory]
    [InlineData("Line In (Realtek High Definition Audio)")]
    [InlineData("Line-In")]
    [InlineData("Built-in Line Input")]   // "built-in" must not win over explicit line wording
    [InlineData("Phono Preamp")]
    [InlineData("Pro-Ject Turntable")]
    [InlineData("AUX In")]
    public void Line_inputs_are_recognised(string name) =>
        Assert.Equal(InputDeviceKind.LineIn, InputDevices.Classify(name));

    [Theory]
    [InlineData("USB Audio CODEC")]       // the classic USB turntable — also any USB gadget
    [InlineData("Scarlett Solo USB")]
    [InlineData("")]
    [InlineData(null)]
    public void Unrecognised_names_stay_unknown(string? name) =>
        Assert.Equal(InputDeviceKind.Unknown, InputDevices.Classify(name));

    [Theory]
    [InlineData("Microtek Scanner")]      // "mic" inside a word is not a microphone
    [InlineData("Micron Audio Device")]
    public void Mic_inside_a_word_is_not_a_microphone(string name) =>
        Assert.NotEqual(InputDeviceKind.Microphone, InputDevices.Classify(name));

    [Fact]
    public void Only_line_inputs_monitor_by_default()
    {
        Assert.True(InputDevices.ShouldMonitorByDefault(InputDeviceKind.LineIn));
        Assert.False(InputDevices.ShouldMonitorByDefault(InputDeviceKind.Microphone));
        Assert.False(InputDevices.ShouldMonitorByDefault(InputDeviceKind.Unknown));
    }

    [Fact]
    public void Device_exposes_its_kind()
    {
        Assert.Equal(InputDeviceKind.Microphone, new InputDevice("x", "Built-in Microphone").Kind);
        Assert.Equal(InputDeviceKind.LineIn, new InputDevice("x", "Line In").Kind);
        Assert.Equal(InputDeviceKind.Unknown, new InputDevice("x", "USB Audio CODEC").Kind);
    }
}
