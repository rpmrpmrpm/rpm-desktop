using System.Globalization;
using RPM.Core.Library;

namespace RPM.Core.Cloud;

/// <summary>One stop of a wall's letter wheel: the letter (or decade) shown, and the place in the wall of the
/// first record filed under it.</summary>
public readonly record struct WallLetter(string Letter, int First);

/// <summary>
/// The letters a wall of albums runs through, for a wheel that jumps along it: the TV screens' album carousel
/// (the Android TV app's and Pegasus's) opens one on Up.
///
/// <para>Only an order by name has letters. By album name a record is filed under the first character of its
/// title, and by artist under the first character of its artist, with its accent off: a letter A to Z, or "#"
/// for a digit, a symbol or another script (the TV's initialOf). That is the character the wall sorts it by, so
/// "$uicideboy$" and "*NSYNC" stand under "#" with "!!!", where the wall puts them, at its start. The artists
/// list's own index skips a leading symbol (<see cref="ArtistWall.Initial"/>) because that list is sorted that
/// way too; a wall of records is not, and filing them by that rule put T, N and U between "#" and A. By date
/// the stops are decades instead: 1970, 1980, and "#" for a record with no year. Every other order has none,
/// and the wheel does not open.</para>
///
/// <para>The stops come in the order the wall first reaches them, not in alphabetical order. So the wheel
/// always agrees with the wall it jumps along, whatever rule the wall was sorted by: a letter the sort places
/// in two runs is listed once, at its first run.</para>
/// </summary>
public static class AlbumLetters
{
    /// <summary>Whether a wall in this order has a letter wheel.</summary>
    public static bool HasLetters(AlbumOrdering ordering) =>
        ordering is AlbumOrdering.Album or AlbumOrdering.Artist or AlbumOrdering.Date;

    /// <summary>The stop one record is filed under in <paramref name="ordering"/>; "#" for an order with no
    /// letters.</summary>
    public static string Of(AlbumFacts album, AlbumOrdering ordering) => ordering switch
    {
        AlbumOrdering.Album => FirstLetter(album.Album),
        AlbumOrdering.Artist => FirstLetter(album.Artist),
        AlbumOrdering.Date => album.Year > 0
            ? (album.Year / 10 * 10).ToString(CultureInfo.InvariantCulture)
            : "#",
        _ => "#",
    };

    /// <summary>A name's first character in capitals with its accent off, when that leaves a letter A to Z;
    /// otherwise "#". Only the first character: the wall sorts by the whole name, symbols and all.</summary>
    private static string FirstLetter(string? name)
    {
        var trimmed = (name ?? "").TrimStart();
        if (trimmed.Length == 0)
            return "#";

        var first = char.ToUpperInvariant(trimmed[0]).ToString().Normalize(System.Text.NormalizationForm.FormD)[0];
        return first is >= 'A' and <= 'Z' ? first.ToString() : "#";
    }

    /// <summary>The stops <paramref name="wall"/> runs through in <paramref name="ordering"/>, each with its
    /// first record, in the order the wall reaches them. Empty for an order with no letters, and for an empty
    /// wall.</summary>
    public static IReadOnlyList<WallLetter> For<T>(
        IReadOnlyList<T> wall, AlbumOrdering ordering, Func<T, AlbumFacts> factsOf)
    {
        ArgumentNullException.ThrowIfNull(wall);
        ArgumentNullException.ThrowIfNull(factsOf);

        if (!HasLetters(ordering))
            return [];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stops = new List<WallLetter>();
        for (var i = 0; i < wall.Count; i++)
        {
            var letter = Of(factsOf(wall[i]), ordering);
            if (seen.Add(letter))
                stops.Add(new WallLetter(letter, i));
        }
        return stops;
    }

    /// <summary>
    /// How far an endless wall of <paramref name="count"/> records moves to go from the record at
    /// <paramref name="from"/> to the one at <paramref name="to"/>, by the shorter way round: negative to the
    /// left, positive to the right, never more than half the wall. <paramref name="from"/> may be any whole
    /// number (an endless wall counts past its end); <paramref name="to"/> is a place in the wall.
    /// </summary>
    public static int ShorterWay(int from, int to, int count)
    {
        if (count <= 0)
            return 0;

        var delta = (((to - from) % count) + count) % count;
        return delta > count / 2 ? delta - count : delta;
    }

    /// <summary>The place in a wall of <paramref name="count"/> records of the record an endless count has
    /// reached: <paramref name="slot"/> taken round the wall, so -1 is the last record.</summary>
    public static int Wrap(int slot, int count) => count <= 0 ? 0 : ((slot % count) + count) % count;
}
