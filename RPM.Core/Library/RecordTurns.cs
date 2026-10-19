namespace RPM.Core.Library;

/// <summary>
/// One song from each record before any record gets a second turn.
///
/// <para><b>A ranked list of a collection is really a list of its records, not of its songs.</b>
/// Whatever the ranking measures, the songs on one album tend to score alike. So they arrive together,
/// and the top of the list becomes an album with gaps in it. A chart can have sixteen of its fifty
/// songs from one album. Taking turns, one song per record in each round, prevents this.</para>
///
/// <para><b>A turn rather than a cap.</b> A cap throws away the album's other songs. That is right for
/// a mood list drawn from a deep pool. It is wrong for a thin chart. A collection with only seven
/// records from the 1960s would give a seven-song list with one song each. The row would then fall
/// below <see cref="SmartPlaylists.LeastToShow"/> and not be drawn. Taking turns keeps the list at full
/// length and moves the repeats to the bottom, where a long list is not read anyway.</para>
///
/// <para><b>The order within a record, and the order between records, both come from the ranking.</b>
/// This method reorders and never drops songs. The record whose best song ranks highest goes first, and
/// each record's songs keep their ranked order. A caller that wants songs dropped should use
/// <see cref="MoodPool.Window"/>, which is the cap.</para>
/// </summary>
public static class RecordTurns
{
    /// <summary>
    /// <paramref name="ranked"/> reordered so the records take turns.
    /// </summary>
    /// <param name="ranked">The songs, best first. Read once.</param>
    /// <param name="folderOf">Which record each song is on. A song whose record cannot be named gets a
    /// turn of its own, rather than sharing one with every other unnamed song. The artist caps use the
    /// same rule for an empty credit.</param>
    public static IReadOnlyList<T> Taken<T>(IEnumerable<T> ranked, Func<T, string> folderOf)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(folderOf);

        // Each record's songs are in ranked order, and the records are in the order of their best song.
        // GroupBy keeps both orders, so one pass is enough.
        var records = ranked
            .GroupBy(folderOf, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.ToList())
            .ToList();

        var total = records.Sum(r => r.Count);
        var taken = new List<T>(total);

        for (var round = 0; taken.Count < total; round++)
        {
            var anyLeft = false;
            foreach (var record in records)
            {
                if (record.Count <= round)
                    continue;
                taken.Add(record[round]);
                anyLeft = true;
            }

            // No record had a song at this depth, so all records are used up. This guards the loop in case
            // the count is wrong.
            if (!anyLeft)
                break;
        }

        return taken;
    }
}
