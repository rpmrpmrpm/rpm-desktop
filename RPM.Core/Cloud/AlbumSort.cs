using System.Text.Json.Serialization;

namespace RPM.Core.Cloud;

/// <summary>The orders that a wall of albums can be put in.</summary>
/// <remarks>Saved BY NAME (the desktop stores the wall's order in settings.json). If it were stored as a
/// number, inserting a new ordering would silently point everybody's saved choice at a different
/// one.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter<AlbumOrdering>))]
public enum AlbumOrdering
{
    /// <summary>By whose record it is, then by its name. This is the order a shelf is in, and it is the
    /// default.</summary>
    Artist,

    /// <summary>By the record's own name, whoever made it.</summary>
    Album,

    /// <summary>Most recently added first. This shows what is new in the library.</summary>
    Added,

    /// <summary>Newest release first. Records with no year go to the end, not to 1900.</summary>
    Year,

    /// <summary>Most played first. Records never played go to the end.</summary>
    Plays,

    /// <summary>Oldest release first, so the wall reads as a history of the collection. Records with no
    /// year go to the end, as with <see cref="Year"/>. This is the TV screens' "By date" (the Android TV
    /// app and Pegasus). The library window offers <see cref="Year"/> instead.</summary>
    Date,
}

/// <summary>What an ordering needs to know about one album, whatever type is holding it.</summary>
/// <param name="AddedAt">The time the album arrived, in whatever unit the head keeps time in: unix seconds
/// from the cloud store, or UTC ticks from the desktop's mirror. Values are only compared, never read as
/// dates. So the unit is the caller's choice, as long as it is the same within one wall.</param>
/// <param name="Year">Zero when no track on the record has a year tag.</param>
public readonly record struct AlbumFacts(
    string Album, string Artist, long AddedAt, int Year, int Plays);

/// <summary>Putting a wall of albums into an order that somebody asked for.
///
/// <para>The rules are kept here, not beside the wall, because each order has a tie to break and a missing
/// value to place. Those are the parts that go wrong without any error. Sorting by year, the untagged half
/// of a library piles up at the front, as if it were from 1900. Sorting by plays, everything never played
/// comes first, in whatever order the list happened to have. Each order falls back to artist and album, so
/// a wall never reshuffles between two runs that agree.
/// </para>
/// </summary>
public static class AlbumSort
{
    /// <param name="factsOf">What the caller knows about each album. This is generic, because the album
    /// type belongs to whichever head draws the wall. The rules are the same for all of them.</param>
    public static IReadOnlyList<T> By<T>(
        IReadOnlyList<T> albums, AlbumOrdering ordering, Func<T, AlbumFacts> factsOf)
    {
        ArgumentNullException.ThrowIfNull(albums);
        ArgumentNullException.ThrowIfNull(factsOf);

        // Read once per album, not once per comparison. For Plays, reading the count means walking the
        // album's tracks, and a comparison sort would do that thousands of times. The facts sit beside the
        // album, not in a dictionary keyed by it. The album type belongs to the caller, and it may be a
        // record type, whose equality compares contents rather than identity.
        var wall = albums.Select(a => (Album: a, Facts: factsOf(a))).ToList();

        var name = StringComparer.CurrentCultureIgnoreCase;

        IOrderedEnumerable<(T Album, AlbumFacts Facts)> ByName(
            IOrderedEnumerable<(T Album, AlbumFacts Facts)> ordered)
            => ordered.ThenBy(a => a.Facts.Artist, name).ThenBy(a => a.Facts.Album, name);

        var sorted = ordering switch
        {
            AlbumOrdering.Album =>
                wall.OrderBy(a => a.Facts.Album, name).ThenBy(a => a.Facts.Artist, name),

            AlbumOrdering.Added => ByName(wall.OrderByDescending(a => a.Facts.AddedAt)),

            // Untagged last, and not merely sorted low. A library with half its albums untagged would
            // otherwise always open on its own unlabelled half.
            AlbumOrdering.Year =>
                ByName(wall.OrderBy(a => a.Facts.Year == 0).ThenByDescending(a => a.Facts.Year)),

            // The same rule for the untagged, the other way round for the rest.
            AlbumOrdering.Date =>
                ByName(wall.OrderBy(a => a.Facts.Year == 0).ThenBy(a => a.Facts.Year)),

            AlbumOrdering.Plays => ByName(wall.OrderByDescending(a => a.Facts.Plays)),

            _ => wall.OrderBy(a => a.Facts.Artist, name).ThenBy(a => a.Facts.Album, name),
        };

        return [.. sorted.Select(a => a.Album)];
    }

    /// <summary>When an album arrived: the newest of its tracks.
    ///
    /// <para>The newest is used, not the oldest. An album is added over the minutes or hours that its
    /// tracks take to upload. A record that is still uploading should not sit below one that finished
    /// yesterday. This also works for a record that was completed later: a missing track added this week
    /// makes the album count as added this week.</para>
    /// </summary>
    public static long AddedOf(IEnumerable<CloudTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        var newest = 0L;
        foreach (var track in tracks)
            if (track.AddedAt > newest) newest = track.AddedAt;

        return newest;
    }

    /// <summary>The record's year: the one that most of its tracks agree on.
    ///
    /// <para>Most, not first or highest. A compilation carries the year of each original recording on the
    /// track that came from it. The first row and the highest year are then a year that the record was not
    /// released in. The tag that the reissue actually carries is on most of the rows. Zero when nothing on
    /// it is tagged.</para>
    /// </summary>
    public static int YearOf(IEnumerable<CloudTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        Dictionary<int, int> seen = [];

        foreach (var track in tracks)
        {
            if (track.Tags is not { Year: > 0 } tags) continue;
            seen[tags.Year] = seen.GetValueOrDefault(tags.Year) + 1;
        }

        return YearFrom(seen.Select(kv => (kv.Key, kv.Value)));
    }

    /// <summary>The same rule, applied to years that are already counted. A database can give these counts
    /// without the caller reading a million track rows to count them again (see
    /// <c>LibraryDatabase.QueryAlbumYearCounts</c>). There is one implementation, so the desktop wall and
    /// the phone wall cannot disagree about what year a record is from.
    /// </summary>
    /// <param name="counts">Each year, and how many of the album's tracks carry it. Years of zero are for
    /// the caller to leave out. An empty sequence is an album with no tagged tracks.</param>
    public static int YearFrom(IEnumerable<(int Year, int Count)> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);

        var best = 0;
        var most = 0;

        foreach (var (year, count) in counts)
        {
            if (year <= 0) continue;

            // The earlier year wins a tie. A reissue tagged half one year and half another is the original
            // record, not the pressing it came out on.
            if (count > most || (count == most && year < best))
            {
                best = year;
                most = count;
            }
        }

        return best;
    }
}
