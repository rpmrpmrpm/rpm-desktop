namespace RPM.Core.Playback;

/// <summary>
/// The orders behind the Now playing card's shuffle key: one press puts what is still to come in another
/// order, the next press puts it back.
///
/// <para>Only what is still to come is touched, both ways. The rows up to and including the playing one are
/// where the listener has been, and moving them would put songs already heard back in front of them, or take
/// the next song away from a queue they are halfway through. The iPhone's queue shuffles the same rows.</para>
///
/// <para>Some rows are held where they are (the caller says which). The player holds a radio's rows: the
/// mixtape that carries on after an album, or a song radio. A radio picks each song to follow the one before
/// it, and a mixtape is what comes after the list, so shuffling the album must not mix the mixtape into it.
/// The rows that move fill only the slots that moving rows held; a held row keeps its slot.</para>
///
/// <para>Rows are matched by reference, not by value: one record can stand in the queue twice, and each of
/// the two is its own row.</para>
/// </summary>
public static class QueueShuffle
{
    /// <summary>The first row a shuffle may move: the one after the playing row, or the first row when
    /// nothing is playing.</summary>
    public static int FirstToCome(int count, int current) => Math.Clamp(current + 1, 0, count);

    /// <summary>Whether a shuffle could change anything: two or more rows still to come that are not
    /// held.</summary>
    public static bool CanShuffle<T>(IReadOnlyList<T> rows, int current, Func<T, bool>? held = null) where T : class
    {
        ArgumentNullException.ThrowIfNull(rows);
        return Slots(rows, current, held).Count >= 2;
    }

    /// <summary>
    /// <paramref name="rows"/> with the rows after <paramref name="current"/> (all of them when
    /// <paramref name="current"/> is -1) in a random order, except the <paramref name="held"/> ones. The rows up
    /// to and including the playing one stay where they are, and so does every held row. With two or more rows
    /// to move, the order that comes back is never the order that went in.
    /// </summary>
    public static List<T> Shuffled<T>(IReadOnlyList<T> rows, int current, Random random, Func<T, bool>? held = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(random);

        var order = rows.ToList();
        var slots = Slots(rows, current, held);
        if (slots.Count < 2)
            return order;

        var moving = slots.Select(i => rows[i]).ToList();
        for (var i = moving.Count - 1; i > 0; i--)
        {
            var j = random.Next(0, i + 1);
            (moving[i], moving[j]) = (moving[j], moving[i]);
        }

        // A key that was pressed and changed nothing would look broken. A draw comes out in the order it went
        // in once in so many orderings (one time in two with two rows to move), and then the rows are turned
        // round by one instead, which is always another order.
        if (moving.Select((row, k) => ReferenceEquals(row, rows[slots[k]])).All(same => same))
        {
            var first = moving[0];
            moving.RemoveAt(0);
            moving.Add(first);
        }

        for (var k = 0; k < slots.Count; k++)
            order[slots[k]] = moving[k];

        return order;
    }

    /// <summary>
    /// <paramref name="rows"/> with the rows still to come put back in the order they stood in
    /// <paramref name="before"/>, the queue as it was before the shuffle.
    ///
    /// <para>The rows up to and including the playing one stay where they are, so a queue that has played on
    /// since the shuffle keeps what it has played. Held rows stay where they are, as the shuffle left them. A
    /// row that was not in <paramref name="before"/> was added while the queue was shuffled, a "play next" for
    /// one, and it keeps its slot: it was put there on purpose. The rows from <paramref name="before"/> fill the
    /// other slots in their old order.</para>
    /// </summary>
    public static List<T> Restored<T>(IReadOnlyList<T> rows, int current, IReadOnlyList<T> before,
                                      Func<T, bool>? held = null) where T : class
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(before);

        var rank = new Dictionary<T, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < before.Count; i++)
            rank.TryAdd(before[i], i);

        var order = rows.ToList();
        var slots = Slots(rows, current, held).Where(i => rank.ContainsKey(rows[i])).ToList();
        var known = slots.Select(i => rows[i]).ToList();

        known.Sort((a, b) => rank[a].CompareTo(rank[b]));
        for (var k = 0; k < slots.Count; k++)
            order[slots[k]] = known[k];

        return order;
    }

    /// <summary>The places of the rows a shuffle may move: still to come, and not held.</summary>
    private static List<int> Slots<T>(IReadOnlyList<T> rows, int current, Func<T, bool>? held) where T : class
    {
        var slots = new List<int>();
        for (var i = FirstToCome(rows.Count, current); i < rows.Count; i++)
            if (held is null || !held(rows[i]))
                slots.Add(i);
        return slots;
    }
}
