namespace RPM.Fingerprint.Chroma;

/// <summary>
/// Where a matcher gets the fingerprints it needs to score, which is not the same set as the ones
/// it indexed.
///
/// <para>Stage 1 answers out of the index alone and names about a hundred and fifty candidates.
/// Stage 2 is the only part that opens a fingerprint, and it opens those. Holding the whole store in
/// memory to serve that would need about 400 MB of blobs in a function that has 1,769 MB. So the store
/// is asked for the candidates instead. Implementations that already have everything in hand answer
/// without going anywhere.</para>
/// </summary>
public interface IChromaFingerprints
{
    /// <summary>
    /// The fingerprints of these recordings. Asked once per lookup, with every candidate at once,
    /// so an implementation that fetches can do it in one round trip rather than a hundred and
    /// fifty. Recordings it cannot answer for are simply absent from the result.
    /// </summary>
    ValueTask<IReadOnlyDictionary<long, ChromaFingerprint>> GetAsync(
        IReadOnlyList<long> songIds, CancellationToken ct = default);

    /// <summary>
    /// Takes a fingerprint the caller has just filed into the index, so a lookup in the same breath
    /// can score it. Without this a song stored back mid-batch is a candidate whose fingerprint
    /// cannot be opened until the durable write lands, and it would lose to recordings that are
    /// merely older.
    /// </summary>
    void Remember(long songId, ChromaFingerprint fingerprint);
}

/// <summary>Fingerprints already in hand: a dictionary, answered without going anywhere. What the
/// tests, the POC and any host small enough to hold its whole store use.</summary>
public sealed class ResidentFingerprints : IChromaFingerprints
{
    private readonly Dictionary<long, ChromaFingerprint> _held;

    public ResidentFingerprints(IReadOnlyDictionary<long, ChromaFingerprint> held)
        => _held = held.ToDictionary(kv => kv.Key, kv => kv.Value);

    public ValueTask<IReadOnlyDictionary<long, ChromaFingerprint>> GetAsync(
        IReadOnlyList<long> songIds, CancellationToken ct = default)
    {
        var found = new Dictionary<long, ChromaFingerprint>(songIds.Count);
        foreach (long id in songIds)
            if (_held.TryGetValue(id, out var fp)) found[id] = fp;
        return ValueTask.FromResult<IReadOnlyDictionary<long, ChromaFingerprint>>(found);
    }

    public void Remember(long songId, ChromaFingerprint fingerprint) => _held[songId] = fingerprint;
}
