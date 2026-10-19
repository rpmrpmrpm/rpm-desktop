using System.Runtime.InteropServices;

using RPM.Desktop.Services.Updates;

namespace RPM.Desktop.Tests;

/// <summary>
/// Which feed a copy of the app reads, and what it does when there is no feed for it.
///
/// <para>The channel is the whole of the update story from the app's side: a build that reads the wrong
/// one offers a reader the other machine's app, and a build that reads none offers nothing. Neither says
/// anything on screen, so the rule is stated here rather than left to the one line that computes it.</para>
/// </summary>
public class UpdateChannelTests
{
    [Fact]
    public void A_mac_on_apple_silicon_reads_the_arm_feed()
        => Assert.Equal("osx-arm64", UpdateChannel.Name(UpdateHead.MacOS, Architecture.Arm64));

    [Fact]
    public void A_mac_on_intel_reads_the_intel_feed()
        => Assert.Equal("osx-x64", UpdateChannel.Name(UpdateHead.MacOS, Architecture.X64));

    [Fact]
    public void Windows_reads_the_x64_feed()
        => Assert.Equal("win-x64", UpdateChannel.Name(UpdateHead.Windows, Architecture.X64));

    /// <summary>A build is published for three pairs and no others. An arm64 Windows runs the x64 app
    /// emulated and reports x64, so this is a pair nothing is installed as. Answering it with the x64
    /// feed anyway would hand somebody a different app's package on the strength of a guess.</summary>
    [Fact]
    public void An_arrangement_nothing_is_published_for_has_no_feed()
    {
        Assert.Null(UpdateChannel.Name(UpdateHead.Windows, Architecture.Arm64));
        Assert.Null(UpdateChannel.Name(UpdateHead.MacOS, Architecture.X86));
    }

    /// <summary>The feed is one prefix per channel under the update host, and it ends in a slash: what
    /// Velopack appends to it is a file name.</summary>
    [Fact]
    public void A_channel_is_a_prefix_under_the_update_host()
        => Assert.Equal("https://updates.rpmrpmrpm.com/osx-arm64/", UpdateChannel.Url("osx-arm64"));

    /// <summary>Linux is built and run from source; nothing is published for it, so nothing is
    /// offered.</summary>
    [Fact]
    public void A_head_with_no_release_channel_asks_for_nothing()
        => Assert.Null(UpdateChannel.Name(UpdateHead.Other, Architecture.X64));
}
