using Velopack;

namespace RPM.Desktop.Services.Updates;

/// <summary>
/// The delta packages that turn the local full package into the newest release, or null when the full
/// package has to be downloaded instead.
/// </summary>
/// <remarks>
/// <para>A delta's entry in the feed names the version it produces, not the version it was made from.
/// Velopack's own rule takes every delta newer than the local package. So if the feed has a gap, a delta
/// made from 0.1.9 is applied to 0.1.6. The patch then fails on a checksum, and Velopack falls back to the
/// full package. But that only happens after it has downloaded and patched for nothing. And the
/// patch fails on a checksum only because every delta happens to patch a file that differs between the
/// two bases.</para>
///
/// <para>Both release scripts make each delta from the newest full release that is already in the feed, and
/// they only ever add entries to the feed (scripts/release_delta.py). So the delta for a version was made
/// from the full release listed just before it. A chain is whole when the local version is listed as a full
/// release, and every full release after it, up to the target, has its delta. Anything less is answered
/// with null, which means the full download.</para>
/// </remarks>
public static class DeltaChain
{
    /// <summary>The deltas from <paramref name="local"/> to <paramref name="target"/>, oldest first.
    /// Returns null when the feed does not hold a whole chain between them.</summary>
    public static VelopackAsset[]? From(IReadOnlyCollection<VelopackAsset> feed, VelopackAsset local, VelopackAsset target)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(target);

        // A local package that the feed does not list may come from a release that was never published to
        // this channel. Nothing says which delta was made from it.
        if (!feed.Any(a => a.Type == VelopackAssetType.Full && a.Version == local.Version))
            return null;

        var steps = feed
            .Where(a => a.Type == VelopackAssetType.Full && a.Version > local.Version && a.Version <= target.Version)
            .Select(a => a.Version)
            .Distinct()
            .OrderBy(v => v)
            .ToList();
        if (steps.Count == 0)
            return null;

        var chain = new VelopackAsset[steps.Count];
        for (var i = 0; i < steps.Count; i++)
        {
            if (feed.FirstOrDefault(a => a.Type == VelopackAssetType.Delta && a.Version == steps[i]) is not { } delta)
                return null;
            chain[i] = delta;
        }
        return chain;
    }
}
