namespace RPM.Core.Radio;

/// <summary>
/// The order the dial's stations are listed in.
/// </summary>
/// <remarks>
/// <see cref="Listed"/> is what the directory itself hands back. It is most-played first, which is the
/// order a search is worth reading in and the one the pane opens on. The rest are the ways somebody
/// looks for a station they cannot see: by its name when they know what it is called, by the numbers
/// when they are looking for the biggest or the best-sounding one on the dial.
/// </remarks>
public enum StationOrdering
{
    /// <summary>As the directory ranked them, which is by how often they are played.</summary>
    Listed,

    /// <summary>By name, A to Z.</summary>
    NameUp,

    /// <summary>By name, Z to A.</summary>
    NameDown,

    /// <summary>Most votes first. The votes are the directory's own count of listeners who
    /// voted.</summary>
    Votes,

    /// <summary>Highest bitrate first.</summary>
    Quality,
}

/// <summary>
/// Puts a listing in the order that was asked for.
///
/// <para><b>Over what is listed, like the filter beside it.</b> This orders the page of stations the
/// directory answered with; it does not ask for a different page. What that costs is stated in
/// <see cref="StationSieve"/> and is the same cost here: an A to Z of the dial is an A to Z of the
/// hundred stations in hand, not of the forty thousand the directory holds.</para>
///
/// <para><b>Every order is total.</b> Two stations with the same bitrate, or with no votes each, would
/// otherwise come out in whatever order they arrived. Then the order would change while a listener only
/// scrolled. So every ordering ends in the name, and the name ends in the id, which no two stations
/// share. A list rebuilt from the same answer is the same list.</para>
/// </summary>
public static class StationSort
{
    /// <summary>The stations in the order asked for. The list itself for
    /// <see cref="StationOrdering.Listed"/>, which is the order they arrived in and not worth a
    /// copy.</summary>
    public static IReadOnlyList<RadioStation> By(StationOrdering order, IReadOnlyList<RadioStation> stations)
    {
        ArgumentNullException.ThrowIfNull(stations);

        return order switch
        {
            StationOrdering.NameUp => [.. stations.OrderBy(Name, Names).ThenBy(Id, Ids)],
            StationOrdering.NameDown => [.. stations.OrderByDescending(Name, Names).ThenBy(Id, Ids)],
            StationOrdering.Votes =>
                [.. stations.OrderByDescending(s => s.Votes).ThenBy(Name, Names).ThenBy(Id, Ids)],
            StationOrdering.Quality =>
                [.. stations.OrderByDescending(s => s.BitrateKbps).ThenBy(Name, Names).ThenBy(Id, Ids)],
            _ => stations,
        };
    }

    /// <summary>What a station sorts BY when it sorts by name: the name as the row prints it, which is
    /// the cleaned one. Sorting on the raw name would put a station under the "•" or "[HQ]" that
    /// somebody typed in front of it. See RadioNames.</summary>
    private static string Name(RadioStation station) => station.Display;

    private static string Id(RadioStation station) => station.Uuid;

    /// <summary>The culture's own comparison, because these are names in that culture. For example,
    /// Türkiye sorts under T and Ärzte under Ä, where a reader expects them.</summary>
    private static readonly StringComparer Names = StringComparer.CurrentCultureIgnoreCase;

    /// <summary>The last key, which is never shown. An id is compared as bytes, not as a
    /// name.</summary>
    private static readonly StringComparer Ids = StringComparer.Ordinal;
}
