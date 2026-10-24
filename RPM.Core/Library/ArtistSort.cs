using RPM.Core.Cloud;

namespace RPM.Core.Library;

/// <summary>The orders a wall of artists can be put in. These are the same four the phone offers
/// (ArtistSort.swift), so a listener who has sorted artists on one screen finds the same choices on the
/// other.
///
/// <para>When an order is saved, it is saved by name and not as a number. If a new order were inserted in
/// the middle, a number would point a saved choice at its neighbour. The same applies to
/// <see cref="AlbumOrdering"/>.</para>
/// </summary>
public enum ArtistOrdering
{
    /// <summary>Alphabetical, with the "#" bucket last. This is the only order on the desktop artists
    /// wall.</summary>
    Name,

    /// <summary>Most records of their own first.</summary>
    Albums,

    /// <summary>Most songs first.</summary>
    Tracks,

    /// <summary>The artist whose newest record of their own joined the collection most recently comes
    /// first. Artists with no dated record go last.</summary>
    Added,
}

/// <summary>Puts a wall of artists in an order. This is ported from the phone's ArtistSort.swift, rule for
/// rule.
///
/// <para>Every order falls back to <see cref="ArtistOrdering.Name"/>. The name order breaks its own ties
/// with an ordinal comparison. So two builds over one collection never disagree about where anybody
/// stands.</para>
/// </summary>
public static class ArtistSort
{
    /// <summary>The wall in <paramref name="ordering"/>.</summary>
    /// <param name="facts">What is known about each album folder. It is required, not optional. The Added
    /// order reads arrival dates from it. An order that could not date anybody would fall back to name
    /// order without saying so.</param>
    public static IReadOnlyList<ArtistWallItem> By(
        IReadOnlyList<ArtistWallItem> artists,
        ArtistOrdering ordering,
        IReadOnlyDictionary<string, AlbumFacts> facts)
    {
        ArgumentNullException.ThrowIfNull(artists);
        ArgumentNullException.ThrowIfNull(facts);

        // Read once per artist, not once per comparison. Arrival walks every record of theirs.
        var seats = artists
            .Select(a => new Seat(
                a,
                Letter: a.Initial,
                Own: a.Albums.Count + a.Together.Count,
                Arrived: ordering == ArtistOrdering.Added ? ArrivalOf(a, facts) : 0))
            .ToList();

        seats.Sort((one, other) =>
        {
            var by = ordering switch
            {
                ArtistOrdering.Albums => other.Own.CompareTo(one.Own),
                ArtistOrdering.Tracks => other.Artist.Tracks.CompareTo(one.Artist.Tracks),
                ArtistOrdering.Added => other.Arrived.CompareTo(one.Arrived),
                _ => 0,
            };
            return by != 0 ? by : ByName(one, other);
        });

        return seats.Select(s => s.Artist).ToList();
    }

    /// <summary>When an artist arrived: the newest AddedAt among their own records. Only their own records
    /// count. A guest appearance on a compilation added this morning is not the artist arriving. This is
    /// zero when none of their records can be dated, which sorts them last under
    /// <see cref="ArtistOrdering.Added"/>.
    /// </summary>
    public static long ArrivalOf(ArtistWallItem artist, IReadOnlyDictionary<string, AlbumFacts> facts)
    {
        long newest = 0;
        foreach (var folder in artist.Own)
            if (facts.TryGetValue(folder, out var album) && album.AddedAt > newest)
                newest = album.AddedAt;
        return newest;
    }

    /// <summary>One artist with what every order needs read off them already.</summary>
    private readonly record struct Seat(ArtistWallItem Artist, char Letter, int Own, long Arrived)
    {
        /// <summary>Whether the artist is in the "#" bucket, which comes after Z.</summary>
        public bool Last => Letter == '#';
    }

    /// <summary>The "#" bucket last. Then the letter each artist files under (<see cref="ArtistWall.Initial"/>),
    /// A to Z. Then the name in the reader's culture, ignoring case. Then an ordinal comparison, so names
    /// that differ only in case keep one order.
    ///
    /// <para>The letter comes before the name because the name alone does not keep the letters together.
    /// The letter skips a name's leading punctuation and the name comparison does not, so "(hed) p.e."
    /// files under H but would sort ahead of every A. The ladder of letters beside Pegasus's artists list
    /// would then read H, A, B. <see cref="ArtistWall.Build"/> keeps the same rule.</para>
    /// </summary>
    private static int ByName(Seat one, Seat other)
    {
        if (one.Last != other.Last)
            return one.Last ? 1 : -1;
        if (one.Letter != other.Letter)
            return one.Letter.CompareTo(other.Letter);

        var named = StringComparer.CurrentCultureIgnoreCase.Compare(one.Artist.Name, other.Artist.Name);
        return named != 0 ? named : string.CompareOrdinal(one.Artist.Name, other.Artist.Name);
    }
}
