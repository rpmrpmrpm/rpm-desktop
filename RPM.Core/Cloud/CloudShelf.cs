namespace RPM.Core.Cloud;

/// <summary>
/// Whose collection a cloud read is served from: this account's own, or the collection of somebody
/// who has shared theirs.
///
/// <para>
/// It exists because "whose" could be an optional argument on some methods and absent from the
/// rest, and forgetting it fails silently in the worst direction. A listing read without it is the
/// caller's own library under somebody else's name, and a stream signed without it is a 404 for an
/// album that was there a second ago. There is no default: every read that <em>can</em> be
/// somebody else's has to say which it is, so the compiler asks the question at every call site and
/// does not answer it for the callers that forgot.
/// </para>
///
/// <para>The server checks the grant behind <see cref="Owner"/> on every request and not once, so a
/// share that is taken back stops the music and the covers at the next request. Nothing here is
/// trusted: this says what to ask for, not what may be had.</para>
/// </summary>
public readonly record struct CloudShelf
{
    private CloudShelf(string ownerId) => OwnerId = ownerId;

    /// <summary>The caller's own collection. It is also what <c>default</c> is, so a shelf that was
    /// never set reads as the safe answer and not as a half-named stranger.</summary>
    public static CloudShelf Own => new("");

    /// <summary>Somebody else's collection, by their account id.</summary>
    public static CloudShelf Of(string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        return new CloudShelf(ownerId);
    }

    /// <summary>Whose it is, or empty for the caller's own.</summary>
    public string OwnerId { get; }

    public bool IsOwn => string.IsNullOrEmpty(OwnerId);

    /// <summary>What goes on the wire, or null when there is nothing to say: every route serves the
    /// caller's own music when it is not told otherwise.</summary>
    public string? Owner => IsOwn ? null : OwnerId;

    public override string ToString() => IsOwn ? "own" : OwnerId;
}

/// <summary>
/// A shared collection that the store did not hand over as itself.
///
/// <para>
/// Thrown when a listing asked for under one account comes back without saying that it is that
/// account's. In practice this happens with a server that does not support shared collections: an
/// unbound query parameter is not an error, so <c>?owner=</c> is dropped and the caller's OWN rows
/// come back with a 200. Every symptom after that is a client working confidently on the wrong
/// library. This is why it is an exception and not an empty answer: there is nothing to carry on
/// with.
/// </para>
/// </summary>
public sealed class CloudShelfNotServedException(string ownerId)
    : Exception($"The store did not serve the collection shared by {ownerId}.")
{
    /// <summary>Whose collection was asked for.</summary>
    public string OwnerId { get; } = ownerId;
}
