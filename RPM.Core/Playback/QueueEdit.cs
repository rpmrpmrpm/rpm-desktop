namespace RPM.Core.Playback;

/// <summary>
/// The index arithmetic behind editing the player's queue while it is playing.
///
/// <para>This is kept apart from the player because queue edits are easy to get wrong. The playing track is
/// tracked by its position. Each removal above it lowers its index by one, so removing several tracks
/// lowers it several times. Removing the playing track itself is a different case: playback must stop, so
/// the position does not slide onto the track that moved into its slot. These errors do not show in the
/// player until the wrong song starts.</para>
/// </summary>
public static class QueueEdit
{
    /// <summary>What removing a set of positions does to the queue.</summary>
    /// <param name="Removals">The positions to remove, highest first. Removing them in this order means
    /// that each removal does not shift the positions still to come.</param>
    /// <param name="CurrentIndex">Where the playing track ends up, or -1 when it was removed.</param>
    /// <param name="RemovedCurrent">Whether the playing track was one of them. If so, the caller should
    /// stop playback, and not start the track that took its place.</param>
    public readonly record struct RemovalPlan(
        IReadOnlyList<int> Removals, int CurrentIndex, bool RemovedCurrent);

    /// <summary>
    /// Plans the removal of <paramref name="remove"/> from a queue of <paramref name="count"/> tracks with
    /// the track at <paramref name="currentIndex"/> playing (-1 for none).
    ///
    /// <para>Out-of-range and duplicate positions are dropped, not rejected. The list view can pass a
    /// selection that is out of date, and a queue edit is not worth failing over.</para>
    /// </summary>
    public static RemovalPlan PlanRemoval(int count, int currentIndex, IEnumerable<int> remove)
    {
        ArgumentNullException.ThrowIfNull(remove);

        var ordered = remove
            .Where(i => i >= 0 && i < count)
            .Distinct()
            .OrderByDescending(i => i)
            .ToList();

        if (ordered.Count == 0)
            return new RemovalPlan(ordered, currentIndex, false);

        bool removedCurrent = currentIndex >= 0 && ordered.Contains(currentIndex);

        // Everything removed from above the playing track pulls it down by one.
        int above = currentIndex < 0 ? 0 : ordered.Count(i => i < currentIndex);
        int newCurrent = removedCurrent || currentIndex < 0 ? -1 : currentIndex - above;

        return new RemovalPlan(ordered, newCurrent, removedCurrent);
    }

    /// <summary>What dragging one track to another position does to the queue.</summary>
    /// <param name="From">Where the track was, clamped into the queue.</param>
    /// <param name="To">Where it lands, clamped into the queue.</param>
    /// <param name="CurrentIndex">Where the playing track ends up. A reorder never stops playback. The
    /// track that is playing carries on, wherever it now sits in the queue.</param>
    public readonly record struct MovePlan(int From, int To, int CurrentIndex)
    {
        /// <summary>Nothing to do. The drag ended where it started, or the queue is too short to reorder.
        /// </summary>
        public bool IsNoOp => From == To;
    }

    /// <summary>
    /// Plans dragging the track at <paramref name="from"/> to <paramref name="to"/> in a queue of
    /// <paramref name="count"/> tracks with <paramref name="currentIndex"/> playing (-1 for none).
    ///
    /// <para>The easy part to get wrong is what happens to other positions. The track is taken out before
    /// it is put back. So everything above the gap moves down first, and then everything from the landing
    /// spot up moves back. If only half of this is applied, the playing marker is one row away from the
    /// track that is actually playing.</para>
    ///
    /// <para>Out-of-range positions are clamped, not rejected, for the same reason that removals drop them.
    /// A list view drags against a queue that may have changed, and a reorder is not worth failing
    /// over.</para>
    /// </summary>
    public static MovePlan PlanMove(int count, int currentIndex, int from, int to)
    {
        if (count <= 1)
            return new MovePlan(0, 0, currentIndex);

        int start = Math.Clamp(from, 0, count - 1);
        int end = Math.Clamp(to, 0, count - 1);

        if (start == end)
            return new MovePlan(start, end, currentIndex);

        return new MovePlan(start, end, Follow(currentIndex, start, end));
    }

    /// <summary>Where a position ends up after the track at <paramref name="from"/> is lifted out and put
    /// back at <paramref name="to"/>. The dragged track itself lands at <paramref name="to"/>.</summary>
    private static int Follow(int index, int from, int to)
    {
        if (index < 0) return -1;
        if (index == from) return to;

        // Out first: everything that sat above the hole drops a place.
        int shifted = index > from ? index - 1 : index;

        // Then back in: everything from the landing spot up makes room.
        return shifted >= to ? shifted + 1 : shifted;
    }
}
