namespace RPM.Core.Library;

/// <summary>One artist backdrop in the saved order: its URL, its position (0 is shown first), the ranker's
/// score when it was ordered (kept for debugging and re-ranking), and whether the user has hidden it from
/// the carousel.</summary>
public sealed record ArtistBackdrop(string Url, int Position, double Score = 0, bool Hidden = false);

/// <summary>Where a picture came from. Art sources return web addresses. A picture the user chose from
/// their own computer is stored under the path it was copied to. That is the only difference between the
/// two kinds.
///
/// <para>It matters because the reconciler keeps a saved picture only while the sources still offer it.
/// That is right for a photo fetched from fanart.tv. It is wrong for a picture the user picked, because no
/// source will ever offer that one, and it would be dropped the next time the reconciler runs. The kind is
/// read from the URL rather than stored in a column. A file path is not a web address, and a migration
/// cannot make that more true than it already is.</para>
/// </summary>
public static class ArtistArtSource
{
    /// <summary>Whether this picture is the user's own, not one from an art source. It is defined by what
    /// a chosen picture is, a file on this computer, rather than by "not a web address". So a URL nobody
    /// expected is still treated as a source's, and it can be dropped when the source drops it.
    /// </summary>
    public static bool IsOwn(string? url)
        => url is not null && url.StartsWith(FileScheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>The prefix of a picture stored from this computer. Every picture is stored as a string,
    /// and this prefix is the only thing that tells the two kinds apart.
    /// </summary>
    public const string FileScheme = "file://";
}

/// <summary>The saved backdrop order for one artist. <see cref="Curated"/> means the user arranged it by
/// hand. The automatic re-ranker must leave it alone, and may only append photos it has not seen before.
/// <see cref="RankVersion"/> is the version of the ranking algorithm that made an automatic order. When it
/// is older than the current version, the order is ranked again. So a change to the algorithm reaches
/// artists that were ranked earlier.
/// </summary>
public sealed record ArtistArtOrder(string ArtistKey, bool Curated, IReadOnlyList<ArtistBackdrop> Backdrops, int RankVersion = 0);

/// <summary>The plan for what to show and what to store. It is worked out by reconciling the fresh
/// candidate list (what the art sources offer right now) with the order saved for the artist.
/// </summary>
/// <param name="DisplayUrls">The carousel, best first, with hidden photos removed.</param>
/// <param name="Merged">The full saved order to store: every candidate, hidden ones kept, positions
/// numbered 0 to n-1. Ignored when <paramref name="NeedsRank"/> is true, because the ranker produces its
/// own order.</param>
/// <param name="NeedsRank">True when the automatic ranker should run. That is when no order was saved, or
/// when the set of available photos changed under an automatic (not curated) order.</param>
/// <param name="Changed">True when <paramref name="Merged"/> differs from what is stored and should be
/// written back, even though the ranker does not run. This happens when a photo appears or disappears under
/// a curated order.</param>
/// <param name="RankUrls">What to pass to the ranker when <paramref name="NeedsRank"/> is true: the
/// sources' own order, with hidden photos removed. This is not <paramref name="DisplayUrls"/>, on purpose.
/// The display leads with the order already saved and puts new photos after it, so nothing jumps on screen.
/// Ranking is a fresh judgement, so it takes the sources at their word. The app puts the user's own
/// fanart.tv photos first in the candidates, because they have the higher resolution. The ranker scores
/// only the first few, and it reads position as the tie-break for likes. If the display order were passed
/// instead, a fanart photo that arrived today would sit behind everything saved yesterday. It would be past
/// the cutoff, never scored, and stuck at the back of the carousel it was fetched to lead.</param>
public readonly record struct ArtistArtPlan(
    IReadOnlyList<string> DisplayUrls,
    IReadOnlyList<ArtistBackdrop> Merged,
    bool NeedsRank,
    bool Changed,
    IReadOnlyList<string> RankUrls);

/// <summary>What the user paging the carousel means for the saved order.
///
/// <para>Arriving at a photo and leaving it there is the whole gesture. There is no "keep this one" button,
/// and there should not be one. The photo the person stopped on is the answer to which photo they want. So
/// the photo on screen moves to the front, and the order becomes <see cref="ArtistArtOrder.Curated"/>. The
/// user has overruled the chooser, and <see cref="ArtistArtReconciler"/> will not re-rank the order
/// again.</para>
/// </summary>
public static class ArtistArtChoice
{
    /// <param name="stored">The order saved for this artist, if any.</param>
    /// <param name="showing">The carousel as it is on screen. This keeps a photo that arrived in this
    /// session and has not been saved yet, so the save does not drop it.</param>
    /// <param name="chosen">The photo the user is looking at.</param>
    public static IReadOnlyList<ArtistBackdrop> Prefer(
        IReadOnlyList<ArtistBackdrop>? stored, IReadOnlyList<string>? showing, string chosen)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chosen);

        var ordered = new List<ArtistBackdrop>(stored ?? []);
        var known = new HashSet<string>(ordered.Select(b => b.Url), StringComparer.OrdinalIgnoreCase);

        foreach (var url in showing ?? [])
            if (!string.IsNullOrWhiteSpace(url) && known.Add(url))
                ordered.Add(new ArtistBackdrop(url, ordered.Count));

        // A photo chosen from outside both lists cannot really happen. It is on screen only because it came
        // from one of them. But it is the user's choice either way. Dropping it would save an order that
        // contradicts what they see.
        var at = ordered.FindIndex(b => string.Equals(b.Url, chosen, StringComparison.OrdinalIgnoreCase));
        var front = at >= 0 ? ordered[at] : new ArtistBackdrop(chosen, 0);
        if (at >= 0)
            ordered.RemoveAt(at);

        // The chosen photo goes to the front and is unhidden. Nothing else moves. The others keep their
        // order, so going back to a photo does not reshuffle the photos around it.
        ordered.Insert(0, front with { Hidden = false });

        return ordered.Select((b, i) => b with { Position = i }).ToList();
    }

    /// <summary>What taking a photo off the carousel means for the saved order: that photo is hidden.
    ///
    /// <para>Hidden, not deleted. The art sources are asked again on every visit, and they would offer the
    /// same photo again and again. A row marked hidden is the only thing that stops them. A photo the user
    /// added from their own computer has the same problem from the other side: no source will ever offer it
    /// again. The hidden row is what stops the chooser from putting it back into the saved order.</para>
    ///
    /// <para>The rest of the order is left exactly as it stands, for the reason <see cref="Prefer"/> leaves
    /// it. Taking one photo away does not say anything about the photos on either side of it.</para>
    /// </summary>
    /// <param name="stored">The order saved for this artist, if any.</param>
    /// <param name="showing">The carousel as it is on screen, including the photo being removed. This way a
    /// photo chosen a moment ago and not yet saved is hidden by a row, not by nothing. Often the save has
    /// not landed yet, and the photo someone removes is the one they have just added.</param>
    /// <param name="removed">The photo the user has taken off.</param>
    public static IReadOnlyList<ArtistBackdrop> Hide(
        IReadOnlyList<ArtistBackdrop>? stored, IReadOnlyList<string>? showing, string removed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(removed);

        var ordered = new List<ArtistBackdrop>(stored ?? []);
        var known = new HashSet<string>(ordered.Select(b => b.Url), StringComparer.OrdinalIgnoreCase);

        foreach (var url in showing ?? [])
            if (!string.IsNullOrWhiteSpace(url) && known.Add(url))
                ordered.Add(new ArtistBackdrop(url, ordered.Count));

        var at = ordered.FindIndex(b => string.Equals(b.Url, removed, StringComparison.OrdinalIgnoreCase));
        if (at >= 0)
            ordered[at] = ordered[at] with { Hidden = true };
        else
            ordered.Add(new ArtistBackdrop(removed, ordered.Count, Hidden: true));

        return ordered.Select((b, i) => b with { Position = i }).ToList();
    }
}

/// <summary>Decides what the strip shows for an artist, from the photos offered now and the order saved
/// before.
///
/// The rules:
/// <list type="bullet">
/// <item>Nothing saved: show the source's order for now and rank in the background.</item>
/// <item>A <b>curated</b> (hand-arranged) order: keep it. New photos it has not seen are added at the end,
/// so they can be reached and edited. The user's order is never reshuffled.</item>
/// <item>An <b>automatic</b> order, same photos as before: reuse it as it is. This cache stops a revisit to
/// an artist from downloading and ranking again every time.</item>
/// <item>An automatic order, but the photos changed: rank again from scratch.</item>
/// </list>
///
/// The method is pure. It reads its inputs and returns a plan, and it writes nothing. The caller passes
/// candidates already in the source's (likes) order, and gets back the display order, plus what, if
/// anything, to write to the store.
///
/// <para>Two orders come back, not one. What is shown now leads with what was saved, so a revisit does not
/// reshuffle in front of the user. What the ranker judges is the sources' order, unchanged. See
/// <see cref="ArtistArtPlan.RankUrls"/> for why this matters for a photo that arrived today.</para>
/// </summary>
public static class ArtistArtReconciler
{
    public static ArtistArtPlan Reconcile(ArtistArtOrder? stored, IReadOnlyList<string> candidates, int currentRankVersion = 0)
    {
        // De-duplicate while keeping the source order. The same URL can come from two art sources, and a
        // carousel that shows one photo twice looks broken.
        var fresh = new List<string>();
        var freshSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in candidates)
            if (!string.IsNullOrWhiteSpace(url) && freshSet.Add(url))
                fresh.Add(url);

        // What the ranker judges: the sources' own order, which leads with the photos the app put in front.
        // Hidden photos stay out. The user removed them from the carousel, and that does not send them to
        // the back of the ranking queue.
        var hiddenUrls = stored is null
            ? null
            : new HashSet<string>(
                stored.Backdrops.Where(b => b.Hidden).Select(b => b.Url), StringComparer.OrdinalIgnoreCase);
        var rankUrls = hiddenUrls is null || hiddenUrls.Count == 0
            ? fresh
            : fresh.Where(u => !hiddenUrls.Contains(u)).ToList();

        if (stored is null || stored.Backdrops.Count == 0)
        {
            // First sight of this artist: show the likes order now, and let the ranker refine it.
            var merged = Number(fresh.Select(u => new ArtistBackdrop(u, 0)));
            return new ArtistArtPlan(Visible(merged), merged, NeedsRank: fresh.Count > 1, Changed: true, rankUrls);
        }

        var storedByUrl = stored.Backdrops
            .GroupBy(b => b.Url, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // Saved photos that the sources still offer, in their saved order. Then photos the source added
        // that were never saved, in source order. A saved photo that the source stops offering is dropped,
        // unless it is the user's own picture. No source would ever offer that one (see ArtistArtSource).
        var kept = stored.Backdrops
            .Where(b => freshSet.Contains(b.Url) || ArtistArtSource.IsOwn(b.Url))
            .OrderBy(b => b.Position)
            .ToList();
        var added = fresh.Where(u => !storedByUrl.ContainsKey(u)).ToList();

        var setChanged = added.Count > 0 || kept.Count != stored.Backdrops.Count;

        if (stored.Curated)
        {
            // The user's order stands. New photos go to the end, unhidden and unscored. Nothing else moves.
            var merged = Number(kept.Concat(added.Select(u => new ArtistBackdrop(u, 0))));
            return new ArtistArtPlan(Visible(merged), merged, NeedsRank: false, Changed: setChanged, rankUrls);
        }

        if (!setChanged)
        {
            // Automatic order, same photos: reuse it as it is. There is no download and no re-rank, which
            // is the reason it was saved. The exception is when the ranking algorithm has changed since it
            // was saved. Then rank again, so a fix to the chooser reaches an artist whose order was cached
            // under the old algorithm. The old order is shown in the meantime. The new ranking replaces it
            // and is stamped with the new version.
            var stale = stored.RankVersion < currentRankVersion && fresh.Count > 1;
            var merged = Number(kept);
            return new ArtistArtPlan(Visible(merged), merged, NeedsRank: stale, Changed: false, rankUrls);
        }

        // Automatic order, but the photo set changed: rank everything again. Meanwhile, show the best guess
        // we have: the saved order for the photos that remain, with new photos after it.
        var interim = Number(kept.Concat(added.Select(u => new ArtistBackdrop(u, 0))));
        return new ArtistArtPlan(Visible(interim), interim, NeedsRank: fresh.Count > 1, Changed: true, rankUrls);
    }

    /// <summary>Renumbers a sequence into consecutive positions from 0.</summary>
    private static IReadOnlyList<ArtistBackdrop> Number(IEnumerable<ArtistBackdrop> items)
        => items.Select((b, i) => b with { Position = i }).ToList();

    /// <summary>The URLs a carousel actually shows: everything not hidden, in order.</summary>
    private static IReadOnlyList<string> Visible(IReadOnlyList<ArtistBackdrop> ordered)
        => ordered.Where(b => !b.Hidden).Select(b => b.Url).ToList();
}
