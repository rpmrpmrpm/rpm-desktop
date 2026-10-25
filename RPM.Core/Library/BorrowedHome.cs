using RPM.Core.Metadata;

namespace RPM.Core.Library;

/// <summary>One list on a borrowed shelf, as home chooses among them: how the quick selection keys it,
/// and the generated list it was cut as on the owner's computer, or null for a playlist somebody
/// made.</summary>
public readonly record struct BorrowedList(string Key, SmartPlaylistKind? Generated);

/// <summary>One song on a borrowed shelf, as home chooses among them.</summary>
/// <param name="Path">Its library-relative path, which is how the quick selection keys a song.</param>
/// <param name="RpmId">Its id, which is what the owner's favourites are counted by.</param>
/// <param name="Folder">The record it is on.</param>
public readonly record struct BorrowedSong(
    string Path, string RpmId, string Folder, string? Artist, string? Title);

/// <summary>
/// Home on a collection somebody else has shared: the quick selection and Made for you, made from what
/// the collection itself holds.
///
/// <para><b>Why it is not the home of one's own shelf.</b> A borrowed shelf has none of the listener's
/// plays: they are counted on the listener's own tracks, and the listing that carries the records
/// carries no listening (see <see cref="HomeShelf.Basis"/>). So every answer the quick selection gives
/// from what is played most is empty there, and the screen showed no quick selection at all over a
/// collection of thousands. The TV app answered this first (its <c>HomeShelf</c>, the
/// <c>Shelf.Borrowed</c> branches, Chivan 2026-10-18: "fill in the 8 rows of quick select with
/// recommendations" and "show different mood playlists each open of the app"), and this follows its
/// rules.</para>
///
/// <para><b>Drawn again at each launch.</b> Everything here is shuffled by the launch's draw, as the
/// suggestions row is (<see cref="HomeShelf.Suggested"/>): a different home on each opening of the app,
/// and the same one for the whole session, so a screen read again under somebody does not rearrange.
/// Each part has its own salt, so two parts that draw on one launch do not walk the same shuffle.</para>
/// </summary>
public static class BorrowedHome
{
    /// <summary>The moods whose lists Made for you is dealt from, one of each in turn. Classical, the
    /// decades and the rest of what the owner's computer sent come after them.</summary>
    public static IReadOnlyList<SmartPlaylistKind> Moods { get; } =
    [
        SmartPlaylistKind.Calm,
        SmartPlaylistKind.Energetic,
        SmartPlaylistKind.Danceable,
        SmartPlaylistKind.Focus,
        SmartPlaylistKind.Sleep,
    ];

    /// <summary>
    /// Made for you on a borrowed shelf: the owner's generated lists, the moods first.
    /// </summary>
    /// <remarks>
    /// The mood lists the owner's computer sent are each mood whole and each mood cut to genres, so a
    /// row of them in the owner's order is five lists of Calm before the first Energetic one. Here one
    /// list of each mood is dealt in turn, so the row opens on different moods: the moods in an order of
    /// the draw, and each mood's lists in another. The generated lists of other kinds (the decades,
    /// classical) follow, in a draw of their own. Lists somebody made are not in it: they are not made
    /// for anybody here, and the quick selection offers them.
    /// </remarks>
    /// <param name="lists">Every list on the shelf, in any order.</param>
    /// <param name="draw">The launch's draw. The app passes <c>App.Launch</c>.</param>
    /// <param name="count">How many at most: a row's length.</param>
    /// <returns>The lists' keys, in the order the row shows them.</returns>
    public static IReadOnlyList<string> MadeForYou(
        IReadOnlyList<BorrowedList> lists, int draw, int count = HomeShelf.RowLength)
    {
        ArgumentNullException.ThrowIfNull(lists);

        if (count <= 0)
            return [];

        var generated = lists.Where(l => l.Generated is not null && !string.IsNullOrEmpty(l.Key)).ToList();

        var byMood = generated
            .Where(l => Moods.Contains(l.Generated!.Value))
            .GroupBy(l => l.Generated!.Value.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(l => l.Key).ToList(), StringComparer.Ordinal);

        // Sorted before they are shuffled, so the same draw gives the same row whatever order the
        // shelf handed the lists over in.
        var moods = HomeShelf.Shuffled([.. byMood.Keys.Order(StringComparer.Ordinal)], draw, "moods")
            .Select(kind => HomeShelf.Shuffled([.. byMood[kind].Order(StringComparer.Ordinal)], draw, kind))
            .ToList();

        var dealt = new List<string>(count);
        for (var round = 0; dealt.Count < count && moods.Any(m => round < m.Count); round++)
            foreach (var mood in moods)
                if (dealt.Count < count && round < mood.Count)
                    dealt.Add(mood[round]);

        var others = HomeShelf.Shuffled(
            [.. generated.Where(l => !Moods.Contains(l.Generated!.Value)).Select(l => l.Key).Order(StringComparer.Ordinal)],
            draw, "other-lists");

        return [.. dealt.Concat(others).Distinct(StringComparer.Ordinal).Take(count)];
    }

    /// <summary>
    /// The quick selection on a borrowed shelf: recommendations, dealt song, record, list and station in
    /// turn as the played ones are on one's own shelf (<see cref="QuickPicks.Choose"/>).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><b>Songs:</b> the owner's favourite songs, then one song from each of the first records
    /// below. One title by one act is offered once, so a song on a record and on its best-of is not
    /// there twice.</item>
    /// <item><b>Records:</b> the ones holding the owner's favourite songs, then the rest of the
    /// collection; the ones the rows under it already show (<paramref name="shown"/>) last.</item>
    /// <item><b>Lists:</b> Made for you's, in its own order, then every other list on the shelf, the
    /// owner's own playlists among them, in another draw.</item>
    /// <item><b>Stations:</b> the ones this listener hears most, as on their own shelf: a station is
    /// not part of anybody's collection.</item>
    /// </list>
    /// No song radio is offered. The radios on one's own shelf start from the songs played most, and
    /// nothing here has been.
    /// </remarks>
    /// <param name="favourites">The owner's favourite songs that are on this shelf, in any order. Empty
    /// where the owner's favourites cannot be read; the selection is then records, lists and songs from
    /// the records.</param>
    /// <param name="records">The shelf's records, by folder, in any order. A folder whose tracks name
    /// no album is left out by the caller: it is loose songs, not a record.</param>
    /// <param name="shown">The records the rows below already show: Recommended for you and Recently
    /// added.</param>
    /// <param name="songsOf">A record's songs. It is asked for at most <paramref name="count"/>
    /// records, the ones at the front: a collection has thousands.</param>
    /// <param name="madeForYou">Made for you's lists, in its order (<see cref="MadeForYou"/>).</param>
    /// <param name="lists">Every list on the shelf, keyed as the selection keys them.</param>
    /// <param name="stations">The stations this listener hears most, most first, by uuid.</param>
    /// <param name="draw">The launch's draw.</param>
    /// <returns>The selection in the order it is laid out. A song is keyed by its path.</returns>
    public static IReadOnlyList<QuickPick> Quick(
        IReadOnlyList<BorrowedSong> favourites,
        IReadOnlyList<string> records,
        IReadOnlyCollection<string> shown,
        Func<string, IReadOnlyList<BorrowedSong>> songsOf,
        IReadOnlyList<string> madeForYou,
        IReadOnlyList<string> lists,
        IReadOnlyList<string> stations,
        int draw,
        int count = QuickPicks.Count)
    {
        ArgumentNullException.ThrowIfNull(favourites);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(songsOf);
        ArgumentNullException.ThrowIfNull(madeForYou);
        ArgumentNullException.ThrowIfNull(lists);
        ArgumentNullException.ThrowIfNull(stations);

        if (count <= 0)
            return [];

        var loved = new HashSet<string>(favourites.Select(s => s.Folder), StringComparer.OrdinalIgnoreCase);
        var albums = Records(records, loved, shown, draw);

        // The favourites in an order of the draw, sorted by id first so the shelf's own order does not
        // decide it.
        var byId = new Dictionary<string, BorrowedSong>(StringComparer.Ordinal);
        foreach (var song in favourites)
            if (!string.IsNullOrEmpty(song.RpmId) && !string.IsNullOrEmpty(song.Path))
                byId.TryAdd(song.RpmId, song);
        var liked = HomeShelf.Shuffled([.. byId.Keys.Order(StringComparer.Ordinal)], draw, "quick-songs")
            .Select(id => byId[id]);

        // One song from each record at the front, drawn from the record by a salt of its own.
        var fromRecords = albums.Take(count).Select(folder => OneOf(songsOf(folder), folder, draw)).OfType<BorrowedSong>();

        var heard = new HashSet<string>(StringComparer.Ordinal);
        var songs = liked.Concat(fromRecords)
            .Where(s => heard.Add(SimilarAlbums.Fold(s.Artist) + "|" + SimilarAlbums.Fold(s.Title)))
            .Select(s => s.Path)
            .ToList();

        var first = new HashSet<string>(madeForYou, StringComparer.Ordinal);
        var rest = HomeShelf.Shuffled(
            [.. lists.Where(l => !string.IsNullOrEmpty(l) && !first.Contains(l)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            draw, "quick-lists");

        return QuickPicks.Choose(songs, albums, [.. madeForYou, .. rest], stations, count);
    }

    /// <summary>The records in the order the quick selection deals them: the ones holding a favourite
    /// song, then the rest, each in a draw of its own, and the ones already shown below last.</summary>
    internal static IReadOnlyList<string> Records(
        IReadOnlyList<string> records, IReadOnlySet<string> loved, IReadOnlyCollection<string> shown, int draw)
    {
        var sorted = records.Where(f => !string.IsNullOrEmpty(f))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Order(StringComparer.OrdinalIgnoreCase)
                            .ToList();

        var drawn = HomeShelf.Shuffled([.. sorted.Where(loved.Contains)], draw, "quick-loved")
            .Concat(HomeShelf.Shuffled([.. sorted.Where(f => !loved.Contains(f))], draw, "quick-rest"))
            .ToList();

        var below = new HashSet<string>(shown, StringComparer.OrdinalIgnoreCase);
        return [.. drawn.Where(f => !below.Contains(f)), .. drawn.Where(below.Contains)];
    }

    /// <summary>One of a record's songs, chosen by the draw and the record, or null for a record with
    /// none.</summary>
    private static BorrowedSong? OneOf(IReadOnlyList<BorrowedSong> songs, string folder, int draw)
    {
        var byPath = new Dictionary<string, BorrowedSong>(StringComparer.OrdinalIgnoreCase);
        foreach (var song in songs)
            if (!string.IsNullOrEmpty(song.Path))
                byPath.TryAdd(song.Path, song);

        if (byPath.Count == 0)
            return null;

        return byPath[HomeShelf.Shuffled([.. byPath.Keys.Order(StringComparer.OrdinalIgnoreCase)], draw, folder)[0]];
    }
}
