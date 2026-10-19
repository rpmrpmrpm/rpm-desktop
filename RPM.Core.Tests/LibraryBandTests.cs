using RPM.Core.Net;
using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>
/// Which of the four messages the band under the library card shows.
///
/// <para>The messages are about different things and only one band shows them, so the priority
/// matters: a listener whose music has just stopped wants to know why, not which albums another
/// laptop is still lending.</para>
/// </summary>
public class LibraryBandTests
{
    [Fact]
    public void AnAnsweringShelfHasNothingToSay()
    {
        Assert.Equal(
            LibraryBandReading.None,
            LibraryBand.For(playbackLost: false, reach: Reach.Online, viewingPeer: true, peerReachable: true));

        Assert.Equal(
            LibraryBandReading.None,
            LibraryBand.For(playbackLost: false, reach: Reach.Online, viewingPeer: false, peerReachable: true));
    }

    [Fact]
    public void AShelfWhoseComputerIsNotHereSaysSo()
    {
        Assert.Equal(
            LibraryBandReading.PeerAway,
            LibraryBand.For(playbackLost: false, reach: Reach.Online, viewingPeer: true, peerReachable: false));
    }

    /// <summary>The player has priority over the shelf: playback stopped, and that message is shown
    /// even when a borrowed shelf also has a message.</summary>
    [Fact]
    public void MusicThatStoppedOutranksAShelfThatNarrowed()
    {
        Assert.Equal(
            LibraryBandReading.PlaybackLost,
            LibraryBand.For(playbackLost: true, reach: Reach.Offline, viewingPeer: true, peerReachable: false));
    }

    /// <summary>Also on the user's own shelf. A track that is stored only on another laptop stops for
    /// the same reason, and the band is the only place that explains why.</summary>
    [Fact]
    public void MusicThatStoppedIsSaidOnAnyShelf()
    {
        Assert.Equal(
            LibraryBandReading.PlaybackLost,
            LibraryBand.For(playbackLost: true, reach: Reach.Online, viewingPeer: false, peerReachable: true));
    }

    /// <summary>No network at all has priority over the shelf. When this machine has no network, a
    /// peer that does not answer has not gone away, and "your computer is offline" explains
    /// both.</summary>
    [Fact]
    public void NoNetworkOutranksAShelfThatIsNotAnswering()
    {
        Assert.Equal(
            LibraryBandReading.Offline,
            LibraryBand.For(playbackLost: false, reach: Reach.Offline, viewingPeer: true, peerReachable: false));

        Assert.Equal(
            LibraryBandReading.Offline,
            LibraryBand.For(playbackLost: false, reach: Reach.Offline, viewingPeer: false, peerReachable: true));
    }

    /// <summary>A local network with no internet is the opposite case: the shelf's message is more
    /// useful, because that computer really cannot be reached and this one can still reach the
    /// others on the network.</summary>
    [Fact]
    public void AShelfThatIsNotAnsweringOutranksAMissingLine()
    {
        Assert.Equal(
            LibraryBandReading.PeerAway,
            LibraryBand.For(playbackLost: false, reach: Reach.LocalOnly, viewingPeer: true, peerReachable: false));
    }

    /// <summary>When the shelf has no problem, the missing internet is the message.</summary>
    [Fact]
    public void AMissingLineIsSaidOnAnAnsweringShelf()
    {
        Assert.Equal(
            LibraryBandReading.LocalOnly,
            LibraryBand.For(playbackLost: false, reach: Reach.LocalOnly, viewingPeer: true, peerReachable: true));

        Assert.Equal(
            LibraryBandReading.LocalOnly,
            LibraryBand.For(playbackLost: false, reach: Reach.LocalOnly, viewingPeer: false, peerReachable: true));
    }

    /// <summary>The player still has priority over everything. Stopped playback is what the listener
    /// needs explained, whatever the internet connection is doing.</summary>
    [Fact]
    public void MusicThatStoppedOutranksTheLine()
    {
        Assert.Equal(
            LibraryBandReading.PlaybackLost,
            LibraryBand.For(playbackLost: true, reach: Reach.LocalOnly, viewingPeer: false, peerReachable: true));
    }
}
