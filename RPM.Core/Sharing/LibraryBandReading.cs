namespace RPM.Core.Sharing;

/// <summary>
/// What the band under the library card has to say, if anything.
///
/// <para>
/// Four kinds of news arrive there, and they are about different things. One is about the shelf: the
/// computer whose records are on screen is not on this network, so the shelf has narrowed itself to what
/// the cloud holds. One is about the player: the music stopped in the middle of a record, and there is no
/// route left to it, neither that computer nor the store. The other two are about this machine's own
/// reach. They are the reason the first two are often true at once: there is no network at all, or a
/// network with nothing beyond it.
/// </para>
///
/// <para>The rule is here, not in the page, so it can be read and tested on its own. The page keeps only
/// the colours and the sentence, which is all a page should own.</para>
/// </summary>
public enum LibraryBandReading
{
    /// <summary>Nothing to say.</summary>
    None,

    /// <summary>Blue: the shelf on screen belongs to a computer that is not answering.</summary>
    PeerAway,

    /// <summary>Red: the music stopped, and nothing on the network or in the cloud will give up the record.</summary>
    PlaybackLost,

    /// <summary>Blue: no network at all. Only what is on this disk can be played.</summary>
    Offline,

    /// <summary>Blue: a network, and nothing beyond it. Only this disk and the libraries shared on this network
    /// can be reached.</summary>
    LocalOnly,
}

public static class LibraryBand
{
    /// <summary>
    /// Which of the four applies, given what the player, this machine's reach, and the library each say.
    ///
    /// <para>The player comes first, on any shelf, including the user's own. A listener standing in front of a
    /// record that has stopped is wondering about the silence, not about which albums a laptop somewhere else
    /// is still sharing.</para>
    ///
    /// <para>Next comes no network at all, because it explains everything below it. A peer that does not
    /// answer when the machine has no network has not gone away. Then the peer: "that computer is not here" is
    /// more useful to someone looking at their shelf than a general network message. Last, the limited network.</para>
    /// </summary>
    /// <param name="playbackLost">The player stopped because there was no connection. Nothing would hand over
    /// the bytes of the record it was playing.</param>
    /// <param name="reach">How far this machine can reach. See <see cref="RPM.Core.Net.Reach"/>.</param>
    /// <param name="viewingPeer">Whether the shelf on screen belongs to another computer.</param>
    /// <param name="peerReachable">Whether that computer is answering.</param>
    public static LibraryBandReading For(
        bool playbackLost, RPM.Core.Net.Reach reach, bool viewingPeer, bool peerReachable) =>
        playbackLost ? LibraryBandReading.PlaybackLost
        : reach == RPM.Core.Net.Reach.Offline ? LibraryBandReading.Offline
        : viewingPeer && !peerReachable ? LibraryBandReading.PeerAway
        : reach == RPM.Core.Net.Reach.LocalOnly ? LibraryBandReading.LocalOnly
        : LibraryBandReading.None;
}
