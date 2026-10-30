namespace RPM.Core.Library;

/// <summary>One place on a series' shelf: a volume the listener has, or a numbered volume they do
/// not.</summary>
/// <param name="Folder">The record's folder, or null for a volume that is not in the library.</param>
/// <param name="Volume">The volume as it is shown: the listener's own spelling for one they have, the
/// number for one they do not, and null for a volume that has no number.</param>
public sealed record SeriesSlot(string? Folder, string? Volume)
{
    public bool IsMissing => Folder is null;
}

/// <summary>A series laid out as a shelf, and how much of it is there.</summary>
/// <param name="Have">How many of the series' records the library holds.</param>
/// <param name="Missing">How many numbered volumes below the highest one held are not there.</param>
public sealed record SeriesShelf(IReadOnlyList<SeriesSlot> Slots, int Have, int Missing)
{
    /// <summary>Past this, the gaps are not drawn. A volume typed as "1991" or as a catalogue number
    /// would otherwise fill the row with two thousand empty sleeves, and that is a typo, not a
    /// collection with holes in it.</summary>
    public const int MostGaps = 500;

    /// <summary>
    /// The records the library holds in one series, in volume order, with an empty place for every
    /// numbered volume that is not there. The gaps run from volume 1 to the highest number held: what
    /// comes after that, nobody here can know. Volumes with no number come last.
    ///
    /// <para>Two records under one number ("3a" and "3b", or two pressings of volume 3) both stand at
    /// that number. A volume numbered 0 or less is shown and starts the count there instead.</para>
    /// </summary>
    public static SeriesShelf Lay(IReadOnlyList<(string Folder, AlbumSeries Series)> held)
    {
        var numbered = held.Where(h => h.Series.Order.Number != long.MaxValue)
            .OrderBy(h => h.Series.Order.Number)
            .ThenBy(h => h.Series.Order.Text, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // A volume spelled without a digit ("IV") still says where it goes, so it comes before a record
        // that names no volume at all.
        var loose = held.Where(h => h.Series.Order.Number == long.MaxValue)
            .OrderBy(h => h.Series.Volume is null)
            .ThenBy(h => h.Series.Order.Text, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.Folder, StringComparer.OrdinalIgnoreCase);

        var slots = new List<SeriesSlot>(held.Count);
        var missing = 0;

        if (numbered.Count > 0)
        {
            var first = Math.Min(1, numbered[0].Series.Order.Number);
            var last = numbered[^1].Series.Order.Number;
            var gaps = last - first + 1 - numbered.Select(n => n.Series.Order.Number).Distinct().Count();
            var fill = gaps <= MostGaps;

            var at = 0;
            for (var number = first; number <= last; number++)
            {
                var here = false;
                while (at < numbered.Count && numbered[at].Series.Order.Number == number)
                {
                    slots.Add(new SeriesSlot(numbered[at].Folder, numbered[at].Series.Volume));
                    at++;
                    here = true;
                }
                if (!here && fill)
                {
                    slots.Add(new SeriesSlot(null, number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    missing++;
                }
                // Without the gaps there is nothing to walk between the volumes held.
                if (!fill && at < numbered.Count)
                    number = numbered[at].Series.Order.Number - 1;
            }
        }

        foreach (var (folder, series) in loose)
            slots.Add(new SeriesSlot(folder, series.Volume));

        return new SeriesShelf(slots, held.Count, missing);
    }
}
