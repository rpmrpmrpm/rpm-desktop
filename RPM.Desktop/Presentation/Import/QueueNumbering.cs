using RPM.Desktop.Presentation.Main.LeftPane.Models;

namespace RPM.Desktop.Presentation.Import;

/// <summary>
/// Gives rows the numbers their order implies.
///
/// <para>Arranging a record and numbering it are two separate steps. A drag sets the order of the rows
/// but leaves the Track field alone. This method writes that order into the Track field. It takes the
/// rows in the order they are already in and does not sort them. The order on screen is the answer,
/// so sorting again would give a different result.</para>
///
/// <para>Every record starts again at 1. A selection spanning three albums is three numberings, not
/// one running count of ninety.</para>
/// </summary>
public static class QueueNumbering
{
    /// <summary>
    /// Numbers <paramref name="rows"/> from the top down, 1..n. The count starts again at 1 for every
    /// record, and again for every disc inside a record, so a double album has two discs that each start
    /// at 1. Each row also gets its own disc's total. Returns how many rows changed, so a caller can say
    /// the rows were already in order.
    ///
    /// <para>The rows are taken in the order they are given. That is the order shown on screen: by
    /// artist, then record, and inside a record the order the last drag set. Numbering in any other order
    /// would undo that arrangement.</para>
    /// </summary>
    public static int Renumber(IReadOnlyList<TrackRow> rows)
    {
        var changed = 0;

        // One counter per record and disc. A row with no disc number is grouped with every other row
        // that has none. Otherwise a single-disc record would come out as many records of one track each.
        // The grouping keeps the order the rows arrived in, so the numbers follow the screen.
        foreach (var disc in rows.GroupBy(r => (Record(r), Disc(r))))
        {
            var onDisc = disc.ToList();
            var count = onDisc.Count.ToString();
            for (var i = 0; i < onDisc.Count; i++)
            {
                var track = (i + 1).ToString();
                if (onDisc[i].Track != track)
                {
                    onDisc[i].Track = track;
                    changed++;
                }
                onDisc[i].TrackCount = count;
            }
        }

        return changed;
    }

    /// <summary>Which record a row belongs to. This uses the store's own grouping rule, so numbering
    /// restarts exactly where the tree starts a new album.</summary>
    private static (string Artist, string Album) Record(TrackRow row) =>
        RPM.Core.Library.LibraryDatabase.QueueGroupKeys(row.AlbumArtist, row.Artist, row.Album);

    private static uint Disc(TrackRow row) => uint.TryParse(row.Disc, out var d) ? d : 0u;
}
