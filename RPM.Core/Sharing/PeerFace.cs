namespace RPM.Core.Sharing;

/// <summary>
/// The person behind a shared computer: what they call themselves, and where their picture is.
///
/// <para>This is separate from <see cref="TrustedPeer"/> because it is a different kind of fact. A peer
/// is a key and a machine. It is written down at pairing and stays true while the pairing stands. A face
/// is the account that the machine is signed into. That account is looked up somewhere else, it can
/// change without anybody pairing again, and it is often not there at all. Nothing is trusted because of
/// a face. It is the label on the row, not a permission for the row.</para>
/// </summary>
/// <param name="DisplayName">What they call themselves, or empty when nobody is known.</param>
/// <param name="Avatar">Where their picture is, or null when they have not set one.</param>
/// <param name="IsSupporter">Whether they support RPM. This is shown as a mark beside their name. It is
/// false for everybody whose face could not be read. That is the same answer as "no", on purpose. A
/// badge is a claim about a person. A badge made up because a lookup failed would be a claim that nobody
/// checked.</param>
public sealed record PeerFace(string DisplayName, Uri? Avatar, bool IsSupporter = false)
{
    /// <summary>
    /// Nobody: a computer that is signed out, or one whose owner has never made a profile, or one that
    /// nothing has been looked up for yet.
    ///
    /// <para>The three cases give the same answer on purpose. Each leaves the caller with a machine's name
    /// and no face. A row that has to draw itself now cannot wait to find out which case it is.</para>
    /// </summary>
    public static readonly PeerFace Nobody = new("", null);

    public bool IsKnown => DisplayName.Length > 0;
}
