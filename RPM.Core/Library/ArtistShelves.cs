using RPM.Core.Cloud;

namespace RPM.Core.Library;

/// <summary>
/// An artist's records sorted onto the shelves their page shows: their own records, the ones they made
/// with somebody, EPs, live records, mixtapes, soundtracks, compilations, singles, and somebody else's
/// records they appear on. Each list holds album folders, newest record first.
///
/// <para>The rule is the same as on the desktop artist page (ArtistDetailView.ShelvesAsync) and on
/// the phone (ArtistShelves.swift). It is in Core so that Pegasus reads the same shelves without a
/// view:</para>
/// <list type="bullet">
/// <item>A record goes on the shelf of its release type, as its files carry it (<see cref="ReleaseType"/>),
/// and only where they carry none on what its folder's name suggests (<see cref="AlbumKind"/>). See
/// <see cref="ShelfOf"/>, which both artist pages call.</item>
/// <item>One record, one shelf. When a name answers more than once the order decides: single, then
/// live, then soundtrack, then compilation.</item>
/// <item>The kind shelves are cut from both their own records and the joint ones.</item>
/// <item>Appearances are not split by kind: the name on somebody else's sleeve says nothing about this
/// artist.</item>
/// <item>Each shelf is newest year first, undated records last, then by title, then by folder.</item>
/// </list>
/// </summary>
public sealed record ArtistShelves(
    IReadOnlyList<string> Records,
    IReadOnlyList<string> Together,
    IReadOnlyList<string> Eps,
    IReadOnlyList<string> Live,
    IReadOnlyList<string> Mixtapes,
    IReadOnlyList<string> Soundtracks,
    IReadOnlyList<string> Compilations,
    IReadOnlyList<string> Singles,
    IReadOnlyList<string> Appearances)
{
    /// <summary>Whether there is nothing on any shelf.</summary>
    public bool IsEmpty =>
        Records.Count == 0 && Together.Count == 0 && Eps.Count == 0 && Live.Count == 0 && Mixtapes.Count == 0
        && Soundtracks.Count == 0 && Compilations.Count == 0 && Singles.Count == 0 && Appearances.Count == 0;

    /// <summary>The artist's own records in the order the page shows them, shelf by shelf. Appearances
    /// are not included: only some of the songs on those are the artist's.</summary>
    public IEnumerable<string> OwnInPageOrder =>
        Records.Concat(Together).Concat(Eps).Concat(Live).Concat(Mixtapes).Concat(Soundtracks)
            .Concat(Compilations).Concat(Singles);

    /// <summary>Sorts one artist's records onto the shelves.</summary>
    /// <param name="facts">What is known about each album folder; a folder missing from it counts as
    /// undated. Required: the year order reads it.</param>
    /// <param name="types">The release type each record's files carry
    /// (<see cref="LibraryDatabase.QueryReleaseTypesByFolder"/>). Required: a record absent from it is shelved by its
    /// name, so an empty map is the stated answer for "no record has a type", never a missing argument.</param>
    public static ArtistShelves Of(
        ArtistWallItem artist, IReadOnlyDictionary<string, AlbumFacts> facts,
        IReadOnlyDictionary<string, ReleaseType> types)
    {
        ArgumentNullException.ThrowIfNull(artist);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(types);

        var own = artist.Own.ToList();
        IReadOnlyList<string> Kind(ReleaseType type) => Newest(own.Where(f => ShelfOf(f, types) == type), facts);

        return new ArtistShelves(
            Records: Newest(artist.Albums.Where(f => ShelfOf(f, types) == ReleaseType.Album), facts),
            Together: Newest(artist.Together.Where(f => ShelfOf(f, types) == ReleaseType.Album), facts),
            Eps: Kind(ReleaseType.Ep),
            Live: Kind(ReleaseType.Live),
            Mixtapes: Kind(ReleaseType.Mixtape),
            Soundtracks: Kind(ReleaseType.Soundtrack),
            Compilations: Kind(ReleaseType.Compilation),
            Singles: Kind(ReleaseType.Single),
            Appearances: Newest(artist.Appearances, facts));
    }

    /// <summary>
    /// The shelf one record goes on: its files' release type where they carry one, else what its folder's name
    /// suggests, else the albums. The one rule both artist pages use (ArtistDetailView and Pegasus), so they cannot
    /// come to disagree about a record.
    /// </summary>
    public static ReleaseType ShelfOf(string folder, IReadOnlyDictionary<string, ReleaseType> types)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(types);
        return ReleaseTypes.ShelfOf(types.TryGetValue(folder, out var set) ? set : null, NameOf(folder));
    }

    /// <summary>The folder's own name, which is what the desktop page classifies a record by.</summary>
    private static string NameOf(string folder)
    {
        var parts = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : folder;
    }

    /// <summary>Newest year first, undated last, then by title in the reader's culture, then by
    /// folder.</summary>
    private static IReadOnlyList<string> Newest(
        IEnumerable<string> folders, IReadOnlyDictionary<string, AlbumFacts> facts)
        => folders
            .Select(f => (
                Folder: f,
                Year: facts.TryGetValue(f, out var a) ? a.Year : 0,
                Title: TitleAsides.NameOnly(NameOf(f))))
            .OrderByDescending(r => r.Year > 0)
            .ThenByDescending(r => r.Year)
            .ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Folder, StringComparer.Ordinal)
            .Select(r => r.Folder)
            .ToList();
}
