namespace RPM.Fingerprint;

/// <summary>The record that the store currently binds one key to.</summary>
/// <param name="RecordingId">The record it is bound to.</param>
/// <param name="Assertions">How many independent bundles have reported this binding. One is a
/// claim; several make it trusted.</param>
public sealed record KeyBinding(RecordingKey Key, string RecordingId, int Assertions);

/// <summary>
/// What to do with one bundle.
/// </summary>
/// <param name="RecordingId">The record the bundle is about, or null when it has nothing usable.</param>
/// <param name="IsNew">True when no key in the bundle was known and a new record is created.</param>
/// <param name="Bind">Keys to attach to <see cref="RecordingId"/>. They are not bound to any
/// record yet.</param>
/// <param name="Affirm">Keys already attached to it, whose assertion count goes up by one.</param>
/// <param name="Disputed">Keys the bundle contradicts. Left exactly as they are.</param>
public sealed record BundleOutcome(
    string? RecordingId,
    bool IsNew,
    IReadOnlyList<RecordingKey> Bind,
    IReadOnlyList<RecordingKey> Affirm,
    IReadOnlyList<RecordingKey> Disputed)
{
    public static readonly BundleOutcome Nothing = new(null, false, [], [], []);

    /// <summary>True when the bundle disagrees with what is already known. This case must never be
    /// resolved by writing.</summary>
    public bool IsConflict => Disputed.Count > 0;
}

/// <summary>
/// The rule for adding to the key graph: given the keys one client saw together and what the store
/// already has for them, what may be written.
///
/// <para>
/// Pure, and kept separate from the table on purpose. In the shared pool, a wrong decision here is
/// not only a bad row but a permanent join between two records that every user then gets. So this
/// rule is kept in one place where it can be read and tested, not as a few <c>if</c>s inside a
/// store.
/// </para>
///
/// <para>
/// The two cases have very different costs. <b>Adding</b> a key to a record wrongly is a small
/// error: one encoding points to the wrong music, one user gets one bad grid, and a later bundle can
/// correct it. <b>Merging</b> two records that already exist wrongly is not: it joins two pieces of
/// music for every user from then on, and no client can see that it happened. So a bundle may add
/// keys freely, but may never merge records by itself.
/// </para>
/// </summary>
public static class RecordingGraph
{
    /// <summary>
    /// Decides what one bundle is allowed to write.
    /// </summary>
    /// <param name="bundle">The keys a client observed together, already parsed and de-duplicated.</param>
    /// <param name="known">What the store has, for each of those keys that it knows.
    /// Keyed by <see cref="RecordingKey.ToString"/>.</param>
    /// <param name="newRecordingId">Called only when a new record is created. Passed in so the
    /// decision can be tested without a clock or a random source.</param>
    public static BundleOutcome Admit(
        IReadOnlyList<RecordingKey> bundle,
        IReadOnlyDictionary<string, KeyBinding> known,
        Func<string> newRecordingId)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(newRecordingId);

        // A single key is not useful. It cannot confirm anything and it cannot start a useful record:
        // it is only a hash that the store already knows or cannot use. A bundle is useful because two
        // keys arrived together.
        if (bundle.Count < 2)
            return BundleOutcome.Nothing;

        var bound = new List<KeyBinding>();
        var unbound = new List<RecordingKey>();
        foreach (var key in bundle)
        {
            if (known.TryGetValue(key.ToString(), out var binding) && !string.IsNullOrEmpty(binding.RecordingId))
                bound.Add(binding);
            else
                unbound.Add(key);
        }

        // No key is known: this is a new record.
        if (bound.Count == 0)
            return new BundleOutcome(newRecordingId(), IsNew: true, Bind: bundle, Affirm: [], Disputed: []);

        var records = bound.Select(b => b.RecordingId).Distinct(StringComparer.Ordinal).ToList();

        // The bundle names two records that the store keeps separate. It may be right (the same master
        // under two identities, which is a join worth making), or it may be one library's wrongly
        // tagged file. This code cannot tell which, so it writes nothing. The conflict is reported and
        // resolved elsewhere, by confirmation from other bundles.
        if (records.Count > 1)
            return new BundleOutcome(null, IsNew: false, Bind: [], Affirm: [], Disputed: [.. bound.Select(b => b.Key)]);

        return new BundleOutcome(
            records[0],
            IsNew: false,
            Bind: unbound,
            Affirm: [.. bound.Select(b => b.Key)],
            Disputed: []);
    }
}
