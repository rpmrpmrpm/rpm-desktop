namespace RPM.Core.Cloud;

/// <summary>
/// One playlist's running order, as the cloud now says it should be.
///
/// <para>
/// A playlist can be reordered on a phone, and the copy that comes back down has to be applied to a
/// node that this library already owns. The pull does not touch such a node in any other case,
/// because a cloud copy of a playlist that this tree holds is normally only its own last push.
/// </para>
///
/// <para>
/// <b>Order and nothing else.</b> Nothing here adds or removes a song. A reorder is the only change
/// that a phone can make, so a cloud copy that names a DIFFERENT set of songs is not a reorder. It
/// means that this library's own copy has changed since that stamp was written, and the ordinary
/// push settles it. Applying it as a reorder would delete or duplicate songs to make the two match,
/// which a sync must never do quietly.
/// </para>
///
/// <para>
/// So the answer is all or nothing: the same songs in a different order, or nothing at all. A
/// playlist may hold the same song twice, and that counts: two copies here and one up there is a
/// different set, not a reorder.
/// </para>
/// </summary>
public static class CloudPlaylistOrder
{
    /// <summary>
    /// The local items, in the order the cloud names them, or null where the order must not be
    /// applied because the sets differ or they are already in step.
    /// </summary>
    /// <param name="local">The playlist's items as this library holds them, in their current order.
    /// An item with no rpm id can never be named by the cloud, so its presence alone makes the sets
    /// differ and the answer null. This is right: the cloud's copy cannot be a reorder of a list that
    /// it is unable to name in full.</param>
    /// <param name="cloudTrackIds">The rpm ids the cloud names, in the order it names them.</param>
    public static IReadOnlyList<long>? Rearrange(
        IReadOnlyList<(long ItemId, string? RpmId)> local,
        IReadOnlyList<string> cloudTrackIds)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(cloudTrackIds);

        if (local.Count == 0 || local.Count != cloudTrackIds.Count) return null;

        // A queue of item ids for each rpm id, in the order this library holds them. They are taken from
        // the front as the cloud names them, so a song held twice keeps both of its items, and the second
        // copy of the name gets the second item.
        var byId = new Dictionary<string, Queue<long>>(StringComparer.Ordinal);

        foreach (var (itemId, rpmId) in local)
        {
            if (rpmId is not { Length: > 0 }) return null;

            if (!byId.TryGetValue(rpmId, out var queue))
                byId[rpmId] = queue = new Queue<long>();

            queue.Enqueue(itemId);
        }

        var ordered = new List<long>(local.Count);

        foreach (var rpmId in cloudTrackIds)
        {
            // Named up there and not held here: a different set, so not a reorder.
            if (!byId.TryGetValue(rpmId, out var queue) || queue.Count == 0) return null;

            ordered.Add(queue.Dequeue());
        }

        // The counts matched at the top and every name was consumed, so nothing can be left. The check
        // is the cheap half of "the sets are the same", and it costs nothing to state it.
        foreach (var queue in byId.Values)
            if (queue.Count != 0)
                return null;

        // Already in step. This is the ordinary case on every pass: the pull runs on a timer and most
        // playlists have not changed. Returning null lets the caller skip the write and the notification.
        var same = true;
        for (var i = 0; i < ordered.Count; i++)
            if (ordered[i] != local[i].ItemId)
            {
                same = false;
                break;
            }

        return same ? null : ordered;
    }
}
