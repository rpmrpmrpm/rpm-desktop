using RPM.Desktop.Presentation.Main.LeftPane.Models;

namespace RPM.Desktop.Presentation.Import;

/// <summary>
/// The record that a queue selection covers in full, or null for any other selection.
///
/// <para>This is needed because most of what the tag panel writes belongs to a FILE, so any set of rows
/// can answer for it. The disc label does not. It belongs to the record and is written once into the
/// folder its files go to. Part of an album cannot answer for the album, and tracks from three albums
/// cannot answer for any of them. So when this is null, the panel does not offer the well at all,
/// instead of writing one answer across music the user was not looking at.</para>
///
/// <para>The label the record already has comes with it, because the two are one question. The label is
/// held in the store against the record, since a queued record has no folder for a disc.png yet. Only a
/// whole record has a stored answer to show.</para>
/// </summary>
/// <param name="DiscArt">The label the record already carries, or null where it carries none.</param>
/// <param name="Series">The series the record has been named a volume of in the queue, or null. Like the
/// disc label it belongs to the record, so only a whole record is offered it.</param>
public sealed record QueueRecordSelection(byte[]? DiscArt, RPM.Core.Library.AlbumSeries? Series = null)
{
    /// <summary>
    /// The one record every row is on, or null where they are not all on one.
    ///
    /// <para>This uses the store's own grouping rule:
    /// <see cref="RPM.Core.Library.LibraryDatabase.QueueGroupKeys"/>. So "the same record" means here
    /// what it means in the tree where the ticks were made. The match is case-insensitive, because that
    /// is how the store groups records.</para>
    /// </summary>
    public static (string Artist, string Album)? RecordOf(IReadOnlyList<TrackRow> rows)
    {
        if (rows.Count == 0)
            return null;

        var first = Key(rows[0]);
        foreach (var row in rows)
        {
            var key = Key(row);
            if (!string.Equals(key.Artist, first.Artist, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(key.Album, first.Album, StringComparison.OrdinalIgnoreCase))
                return null;
        }
        return first;
    }

    private static (string Artist, string Album) Key(TrackRow row) =>
        RPM.Core.Library.LibraryDatabase.QueueGroupKeys(row.AlbumArtist, row.Artist, row.Album);
}
