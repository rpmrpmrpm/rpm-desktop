namespace RPM.Core.Cloud;

/// <summary>What this machine last published for a playlist, or the fact that it does not know. The last
/// published list is the common ancestor that a three-way merge is measured against.
///
/// <para>
/// <b>This is a type, not a nullable list, because the two answers behave in completely different ways.</b>
/// Mixing them up is the main way this goes wrong. With an ancestor, an id that is here but not up
/// there is a removal that somebody made, and it must be honoured. Without one, the same two lists
/// say nothing about removals. The id could be an addition made here that has not gone up yet.
/// Treating "I do not know" as an empty list turns every song in the cloud into news and every song
/// here into an addition. That merges to a union, which is harmless. Treating it as a KNOWN empty list
/// turns every song into a removal, which empties the playlist on a first sync.
/// </para>
/// </summary>
public readonly record struct PlaylistBase
{
    private PlaylistBase(IReadOnlyList<string>? ids) => Ids = ids;

    /// <summary>This machine has never published this playlist, or published it before it started
    /// recording what it sent. Nothing can be assumed to have been removed.</summary>
    public static readonly PlaylistBase Unknown = new(null);

    /// <summary>The ids this machine last wrote to the store, in the order it wrote them.</summary>
    public static PlaylistBase Published(IReadOnlyList<string> ids)
        => new(ids ?? throw new ArgumentNullException(nameof(ids)));

    internal IReadOnlyList<string>? Ids { get; }

    public bool IsKnown => Ids is not null;

    /// <summary>Whether a list is exactly what was published: the same songs in the same order.
    ///
    /// <para>This check is kept here, not at the caller, and the ids stay hidden behind the type. A
    /// pusher asks this question to decide whether to write at all. For a playlist with no record of
    /// what was published, the answer is false. That makes an unrecorded playlist go up, rather than
    /// sit there silently.</para>
    /// </summary>
    public bool Matches(IReadOnlyList<string> now)
    {
        ArgumentNullException.ThrowIfNull(now);

        if (Ids is not { } was || was.Count != now.Count) return false;

        for (var i = 0; i < was.Count; i++)
            if (!string.Equals(was[i], now[i], StringComparison.Ordinal))
                return false;

        return true;
    }
}

/// <summary>
/// Merging one playlist's running order across two heads that both changed it.
///
/// <para>A playlist can be edited on a phone and on a computer, and the two do not take turns. The cloud
/// copy is what every other head can see. The local copy is what this head has. The list this machine
/// last published is what the two have in common. With all three, each change made on one side can be
/// told apart from each change made on the other side. That is the only way to apply both without one of
/// them quietly undoing the other.
/// </para>
///
/// <para>
/// <b>Counted, not set-based.</b> A playlist may hold the same song twice on purpose, and the second
/// copy is something somebody put there. So the arithmetic works on counts, not on membership. A song's
/// count ends up as: what was published, plus what was added, minus what was taken away. "Added" is
/// the LARGER of the two sides' additions, not their sum. Two heads that both put the same song in have
/// made one change, not two. See <see cref="Targets"/>.
/// </para>
///
/// <para>
/// <b>The order is the published one.</b> If both sides reordered the same songs, there is no answer
/// that is right, because one side has to lose. The cloud's order is the one that every other head is
/// already looking at, so it stands. Songs that this side added are appended after it, in the order
/// this side has them. An addition has no place in an order that has never seen it.
/// </para>
///
/// <para>Renames and refiling are not handled here. A playlist's name and the folder it sits in can only
/// be changed on a computer. No phone screen offers either, so they travel one way and there is nothing
/// to reconcile. If that changes, this is where the merge for it belongs.
/// </para>
/// </summary>
public static class PlaylistMerge
{
    /// <summary>The running order that both sides should end up with.
    /// </summary>
    /// <param name="ancestor">What this machine last published. See <see cref="PlaylistBase"/>.</param>
    /// <param name="mine">The order this machine holds now.</param>
    /// <param name="theirs">The order the store holds now.</param>
    public static IReadOnlyList<string> Songs(
        PlaylistBase ancestor, IReadOnlyList<string> mine, IReadOnlyList<string> theirs)
    {
        ArgumentNullException.ThrowIfNull(mine);
        ArgumentNullException.ThrowIfNull(theirs);

        // Nothing to reconcile: one side is exactly what was published, so every difference belongs to
        // the other side and is taken whole. This is the usual case on every pass, because most
        // playlists have not moved. It is answered exactly here, not by the arithmetic below. The
        // arithmetic would give the same songs in the published order and quietly undo a reorder made
        // on this side.
        if (ancestor.Ids is { } published)
        {
            if (Same(published, mine)) return theirs;
            if (Same(published, theirs)) return mine;
        }

        var want = Targets(ancestor, mine, theirs);

        var merged = new List<string>(Math.Max(mine.Count, theirs.Count));
        var taken = new Dictionary<string, int>(StringComparer.Ordinal);

        // The published order comes first.
        foreach (var id in theirs) Take(id, want, taken, merged);

        // Then whatever this side is still short of, in the order this side has it. This is what an
        // addition made here looks like once the other head's list has been laid down.
        foreach (var id in mine) Take(id, want, taken, merged);

        return merged;
    }

    /// <summary>
    /// How many of each id the merged list should hold.
    ///
    /// <para>For each id: what was published, plus what was added, minus what was taken away. Here
    /// "added" is the LARGER of the two sides' additions, not their sum. Removals work the same way.
    /// <b>That distinction is the difference between arithmetic and meaning.</b> Two heads that both put
    /// a song in have made one change, not two. Summing them would put the song in the playlist twice.
    /// Nobody meant that, and it is a common way for a playlist to end up with the same song twice. The
    /// same reasoning, mirrored, keeps a song that both heads deleted deleted, and not deleted twice.
    /// </para>
    ///
    /// <para>If a change was made on one side only, the larger number belongs to that side. So this
    /// reduces to exactly "honour the change". That is the case that happens most often.</para>
    /// </summary>
    private static Dictionary<string, int> Targets(
        PlaylistBase ancestor, IReadOnlyList<string> mine, IReadOnlyList<string> theirs)
    {
        // No ancestor: nothing can be shown to have been removed, so nothing is treated as removed.
        // Reading the published list as empty gives that result from the same arithmetic. Every song on
        // both sides is then an addition, and additions are never lost.
        var was = ancestor.Ids is { } published ? Count(published) : [];
        var known = ancestor.IsKnown;

        var here = Count(mine);
        var there = Count(theirs);

        var want = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var id in here.Keys.Concat(there.Keys).Concat(was.Keys))
        {
            if (want.ContainsKey(id)) continue;

            var before = was.GetValueOrDefault(id);
            var ours = here.GetValueOrDefault(id);
            var theirsCount = there.GetValueOrDefault(id);

            var added = Math.Max(ours - before, theirsCount - before);

            // Without an ancestor there are no removals. See above. With one, a song that is gone from
            // one side is gone. A song removed from both sides counts as one removal, not two.
            var removed = known ? Math.Max(before - ours, before - theirsCount) : 0;

            want[id] = Math.Max(0, before + Math.Max(0, added) - Math.Max(0, removed));
        }

        return want;
    }

    /// <summary>Adds one id to the merged list if the merge still wants another copy of it.</summary>
    private static void Take(
        string id, Dictionary<string, int> want, Dictionary<string, int> taken, List<string> into)
    {
        var already = taken.GetValueOrDefault(id);
        if (already >= want.GetValueOrDefault(id)) return;

        taken[id] = already + 1;
        into.Add(id);
    }

    private static Dictionary<string, int> Count(IReadOnlyList<string> ids)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in ids) counts[id] = counts.GetValueOrDefault(id) + 1;
        return counts;
    }

    private static bool Same(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;

        for (var i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                return false;

        return true;
    }
}
