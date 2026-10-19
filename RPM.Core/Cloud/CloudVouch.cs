namespace RPM.Core.Cloud;

/// <summary>
/// Whether the store has just answered for a track. This is the question asked before rpm records
/// that a recording is safely in the cloud.
///
/// <para>
/// Nothing here trusts a local row. A catalogue listing says that a track was linked to this
/// account once. It does not say that the store still holds the two objects a restore needs, and the
/// listing can be minutes old. A row that claims a track is backed up when the store has nothing
/// under that id is the one mistake that matters here, because nothing afterwards corrects it.
/// </para>
/// </summary>
public static class CloudVouch
{
    /// <summary>
    /// Whether the store just said, of this exact track, that it can give it back.
    ///
    /// <para>There are three ways to be told no: no answer at all (an old API, or a machine that went
    /// offline; an empty answer vouches for nothing), an answer that says not ok, and an answer about a
    /// different recording. The last one is the subtle one. If the checksum that the store has filed
    /// under this id is not the checksum this library holds, the two disagree about which recording the
    /// id names, and the file on this disk is the one the user can actually hear.</para>
    /// </summary>
    /// <param name="verdict">What the store said about this id, or null when it said nothing.</param>
    /// <param name="localHash">The canonical hash this library has for the track. Empty is not a
    /// mismatch: a row from before hashes were recorded has nothing to disagree with.</param>
    public static bool Vouches(CloudVerification? verdict, string? localHash)
    {
        if (verdict is not { Ok: true }) return false;

        return localHash is not { Length: > 0 } mine
            || verdict.Sha256 is not { Length: > 0 } theirs
            || string.Equals(mine, theirs, StringComparison.OrdinalIgnoreCase);
    }
}
