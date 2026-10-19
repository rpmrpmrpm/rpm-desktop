namespace RPM.Core.Playback;

/// <summary>
/// Where one go at a track may read its bytes from.
///
/// <para>
/// A record streamed over the network can stop arriving in the middle of a song, for example when a laptop
/// is closed or the Wi-Fi drops. The player then restarts the track from the cloud copy, at the second
/// where it stopped. That fallback works. The problem came next: the computer answered again ten seconds
/// later, the next stumble found it on the network, and the same record moved back and forth between the
/// two sources for about a minute. The listener heard a gap at each switch.
/// </para>
///
/// <para>
/// So the route is decided once per track, not once per go, and it is carried forward, not recalculated. A
/// track whose computer has dropped out plays the rest of itself from the cloud, whatever that computer
/// does afterwards. The next track starts again at <see cref="Anywhere"/>. The network is the faster and
/// free route, and a laptop that was away for a moment is asked again.
/// </para>
///
/// <para>The route is a value and not a flag on the player. The go that reopens a broken stream may still
/// be running after the listener has started something else, and the route belongs to the track it was
/// decided for.</para>
/// </summary>
public readonly record struct TrackRoute
{
    private TrackRoute(bool peerAllowed) => PeerAllowed = peerAllowed;

    /// <summary>The disk, then a computer on this network, then the cloud. Every track starts here,
    /// including one that fell back to the cloud on its previous play.</summary>
    public static TrackRoute Anywhere => new(peerAllowed: true);

    /// <summary>Whether another computer on this network may serve these bytes.</summary>
    public bool PeerAllowed { get; }

    /// <summary>
    /// The route the rest of this track takes after its bytes stopped arriving.
    ///
    /// <para>The network is ruled out for the rest of the track, whether or not this failure came from it.
    /// A cloud stream that breaks after the network has already failed is not a reason to try the network
    /// again.</para>
    /// </summary>
    public TrackRoute AfterAStumble() => new(peerAllowed: false);
}
