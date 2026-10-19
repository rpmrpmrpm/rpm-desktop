namespace RPM.Core.Sharing;

/// <summary>
/// What a computer is called: the one rule for reading somebody else's name, and the one rule for
/// deciding what this machine puts on the wire about itself.
///
/// <para>
/// Both rules live here, so that every screen uses the same name for a computer. A person who has
/// chosen a name for themselves should see that name in every sentence, not the machine name. A
/// rule that decides what a person reads should not be worked out again at each call site.
/// </para>
///
/// <para>
/// The two halves are in one place on purpose. They are the same rule read from opposite ends.
/// <see cref="Announce"/> decides which of this machine's names travel. <see cref="For"/> decides which
/// of another machine's names wins once it has arrived. If one changes without the other, the far end
/// reads something that nobody meant to send.
/// </para>
/// </summary>
public static class PeerLabel
{
    /// <summary>
    /// What to call another computer, best answer first.
    ///
    /// <para>
    /// The profile comes first because it is the live one. It is fetched from the account that the
    /// machine says it is signed into. So a person who renamed themselves this morning is renamed here.
    /// The broadcast name comes second. It is the name they chose, but only as of the last time their
    /// beacon went out. The machine name comes last. It is a fact about the computer and not about the
    /// person. It is the only one of the three that is always there.
    /// </para>
    ///
    /// <para>The middle rung matters most during pairing. Nothing can be looked up about a machine that is
    /// not paired yet. It broadcasts an account FINGERPRINT, which is for comparing, not for fetching a
    /// profile. Without the middle rung, every sentence in the pairing flow would fall back to the
    /// machine name.</para>
    /// </summary>
    /// <param name="profile">Who the account says they are, or <see cref="PeerFace.Nobody"/>.</param>
    /// <param name="broadcastFace">The display name off their beacon, or empty. Empty is what an older
    /// build sends, and what a machine sends when its owner has named it.</param>
    /// <param name="machineName">What the machine calls itself. Always something.</param>
    public static string For(PeerFace profile, string broadcastFace, string machineName)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.IsKnown)
            return profile.DisplayName;

        return string.IsNullOrWhiteSpace(broadcastFace) ? machineName : broadcastFace;
    }

    /// <summary>
    /// The two names this machine puts in its beacon: what it is called, and who is at it.
    ///
    /// <para>
    /// A typed name is the whole answer, and the display name is not sent beside it. Someone who has gone
    /// into the settings and named this computer has said what they want the neighbours to see. Sending
    /// the profile name as well would make the far end read a name its owner did not choose. The override
    /// would look broken from the only side that can see it. Without a typed name, the machine name is
    /// still sent. It tells apart one person's two laptops, and it is all an older build knows how to read.
    /// </para>
    /// </summary>
    /// <param name="typedName">What the user typed into "This computer is called", or null or blank.</param>
    /// <param name="profileName">The display name on the signed-in account, or null when signed out or
    /// when nothing has been fetched yet.</param>
    /// <param name="machineName">This machine's own name, the fallback that is always there.</param>
    /// <returns>The name for the beacon's <c>n</c>, and the face for its <c>d</c>. The face is empty when
    /// there is none to send. In that case the key is left out of the record altogether.</returns>
    public static (string Name, string Face) Announce(
        string? typedName, string? profileName, string machineName)
    {
        if (!string.IsNullOrWhiteSpace(typedName))
            return (typedName.Trim(), "");

        return (machineName, profileName?.Trim() ?? "");
    }
}
