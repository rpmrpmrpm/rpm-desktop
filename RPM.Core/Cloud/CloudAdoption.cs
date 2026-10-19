namespace RPM.Core.Cloud;

/// <summary>
/// Whether a track about to be uploaded should take over an identity that the account already
/// stores the same audio under, instead of sending a second copy.
///
/// <para>
/// The saving is real. Two installs that rip the same CD produce the same canonical audio, and
/// storing it twice means a second catalogue row, a second charge against the same quota, and an
/// album that shows every song twice. But the decision rests on a hash index built from a catalogue
/// listing that can be ten minutes old, and adopting marks the track as being in the cloud without
/// sending a byte. A stale answer would therefore write a row that says "backed up" for music the
/// store does not have.
/// </para>
///
/// <para>
/// Nothing ever corrects such a row. The catalogue pass skips ids that the listing does not contain
/// ("not in the cloud: not this job"), so the library keeps showing the album as safe. The only
/// symptom is the rendition pass encoding the same files and getting a 404 on every run. So the
/// store is asked, now, about the exact id being adopted.
/// </para>
/// </summary>
public static class CloudAdoption
{
    /// <summary>
    /// Whether the identity may be taken over.
    /// </summary>
    /// <param name="knownId">What the hash index says the account already stores this audio under, or
    /// null when it says nothing.</param>
    /// <param name="currentId">The id this track already carries. The same id is not an adoption: it
    /// is this machine's own earlier upload of this very file, so there is nothing to take over.</param>
    /// <param name="storeVouches">Whether the store, asked about <paramref name="knownId"/> just now,
    /// says it can give that track back. "No" means the listing was stale, and the audio is uploaded
    /// as itself.</param>
    public static bool MayAdopt(string? knownId, string? currentId, bool storeVouches)
        => knownId is { Length: > 0 }
           && !string.Equals(knownId, currentId, StringComparison.Ordinal)
           && storeVouches;

    /// <summary>Whether it is worth asking the store at all. The question costs a request, so it is not
    /// asked when the index has nothing to say.</summary>
    public static bool WorthVerifying(string? knownId, string? currentId)
        => knownId is { Length: > 0 } && !string.Equals(knownId, currentId, StringComparison.Ordinal);
}
