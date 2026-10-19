using RPM.Core.Playback;

namespace RPM.Core.Tests;

/// <summary>
/// Where one attempt at a track may look for its bytes, and for how long that answer holds.
///
/// <para>The failure it prevents: a record borrowed over the network drops out, the player puts it
/// back on from the cloud copy, then the other computer answers again and the next dropout puts it
/// straight back on the network. The record crosses over several times in a minute and the
/// listener hears every seam. A decision made once per track cannot prevent that. One made per
/// attempt can.</para>
/// </summary>
public class TrackRouteTests
{
    [Fact]
    public void A_track_starts_able_to_reach_the_computer_next_door()
    {
        Assert.True(TrackRoute.Anywhere.PeerAllowed);
    }

    [Fact]
    public void A_stumble_closes_the_network_for_the_rest_of_the_track()
    {
        Assert.False(TrackRoute.Anywhere.AfterAStumble().PeerAllowed);
    }

    /// <summary>The one that matters: the second dropout must not hand the network back. That is the
    /// back-and-forth switching, written as a test.</summary>
    [Fact]
    public void A_second_stumble_does_not_re_open_it()
    {
        Assert.False(TrackRoute.Anywhere.AfterAStumble().AfterAStumble().PeerAllowed);
    }

    /// <summary>A value, not a switch on the player: narrowing the route of a record that is dying
    /// says nothing about the record that follows it. The next track is opened at
    /// <see cref="TrackRoute.Anywhere"/> and gets the network again.</summary>
    [Fact]
    public void Narrowing_one_track_leaves_the_next_one_alone()
    {
        var dying = TrackRoute.Anywhere;
        _ = dying.AfterAStumble();

        Assert.True(dying.PeerAllowed);
        Assert.True(TrackRoute.Anywhere.PeerAllowed);
    }

    /// <summary>Which way a forgotten route fails. The narrow way, deliberately: a route that
    /// defaulted open would bring the seam back silently. One that defaults closed sends the track to
    /// the cloud, and where there is no cloud copy it stops the track with the band up, where
    /// somebody can see and report it.</summary>
    [Fact]
    public void A_route_nobody_stated_does_not_reach_the_network()
    {
        Assert.False(default(TrackRoute).PeerAllowed);
    }
}
