namespace RPM.Core.Sharing;

/// <summary>What a settings change asks the beacon to do. The answer is one of these, never a combination.
/// Taking sharing down and putting it back up announces on the way, and doing both would announce twice.</summary>
public enum BeaconChange
{
    /// <summary>Nothing. This is the usual answer: the setting that changed is not one that this machine
    /// announces on the network.</summary>
    None = 0,

    /// <summary>Sharing has been switched on or off, or the collection under it has changed. The server and the
    /// beacon both go down and come back, and the beacon goes with them.</summary>
    Restart = 1,

    /// <summary>The server stays up and keeps serving the same collection. But what the beacon says about it is
    /// out of date, because the machine has been renamed.</summary>
    Reannounce = 2,
}

/// <summary>
/// What the network is owed when this machine's settings change.
///
/// <para>
/// Every setting in the app arrives through one event. So this is asked on every drag of the cover-size
/// slider and every flip of the library's view switch. An announcement is costly: the old record is
/// withdrawn and a new one is published over multicast. That runs on the thread of the caller and holds it
/// for a full second. Re-announcing for every setting would make each of those changes cost a second, even
/// though the neighbours see the same computer offering the same music under the same name.
/// </para>
///
/// <para>So the question is asked the other way round: not "did something change?" but "did anything the
/// beacon actually says change?". The beacon says who this machine is, where it is, what to call it, and
/// who is at it. See <see cref="PeerBeacon.Announce"/>. Only the last two come from settings. Who it is
/// belongs to the key. Where it is belongs to the server. The account it is signed into has its own event
/// and its own re-announcement.</para>
/// </summary>
public static class BeaconReaction
{
    /// <param name="wanted">Whether this machine should be serving at all. That is true when the setting is on,
    /// there is a collection open to serve, and there is an account behind both. Sharing is arranged between
    /// accounts, so a machine that is signed out takes its beacon down. See <c>SharingService.MayServe</c>,
    /// where the three are combined.</param>
    /// <param name="serving">Whether it is serving right now.</param>
    /// <param name="announced">The name the beacon currently carries. This is what the neighbours can see.</param>
    /// <param name="name">The name the settings now say.</param>
    /// <param name="announcedFace">The display name the beacon currently carries, or empty.</param>
    /// <param name="face">The display name the settings now say, or empty. This is not a parameter with a
    /// default. A renamed profile whose beacon is never re-announced would show the new name everywhere except
    /// on the one screen where it was changed.</param>
    public static BeaconChange For(
        bool wanted, bool serving, string? announced, string? name, string? announcedFace, string? face)
    {
        // Starting owns the announcement, and so does stopping. Restart puts the beacon up with whatever the
        // settings say at that point. A Reannounce on top of it would announce twice.
        if (wanted != serving)
            return BeaconChange.Restart;

        if (!serving)
            return BeaconChange.None;

        // Ordinal, on purpose. This is not a question about which of two spellings a reader would call the same.
        // It asks whether the string on the wire is the string in the settings.
        return string.Equals(announced ?? "", name ?? "", StringComparison.Ordinal)
               && string.Equals(announcedFace ?? "", face ?? "", StringComparison.Ordinal)
            ? BeaconChange.None
            : BeaconChange.Reannounce;
    }
}
