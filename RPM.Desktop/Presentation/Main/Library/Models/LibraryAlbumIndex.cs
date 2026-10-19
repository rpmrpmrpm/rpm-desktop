using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Core.Threading;
using RPM.Desktop.Services;

namespace RPM.Desktop.Presentation.Main.Library.Models;

/// <summary>
/// What the album views are built from: the library's albums, in order, and what is known about each
/// one. It is a single pass of indexed GROUP BYs over the mirror. The number of tracks can reach a
/// million, but the number of folders is only the size of the collection, so it is cheap to know the
/// whole shelf where it would not be cheap to know every track.
///
/// <para>Nothing here holds a track. The album views page a record's own tracks when it comes on
/// screen, and the data table asks the database directly (<see cref="LibraryTableSource"/>).</para>
/// </summary>
public sealed partial class LibraryAlbumIndex : IDisposable
{
    /// <summary>One folder in the skeleton while it is being built: its own direct track count and the
    /// folders under it. This is enough to say which folders are albums and in what order they read.</summary>
    private sealed record Node(string Folder, int TrackCount)
    {
        public List<Node> Children { get; } = new();
    }

    private readonly LibraryService _library;
    private CancellationTokenSource? _rebuildCts;

    /// <summary>The artists' pass cancels its previous run and nothing else. It is separate from
    /// <see cref="_rebuildCts"/> on purpose: a keystroke in the album views' search field starts a
    /// searched pass, and that must neither re-read the artists nor cancel a read of them half way.</summary>
    private CancellationTokenSource? _artistsCts;

    public LibraryAlbumIndex(LibraryService library) => _library = library;

    /// <summary>The collection that each half of the index was last read from. It is null until the
    /// first read. It is compared with the collection that is open now and is not kept as a flag, so
    /// a reopened library or a switched borrowed shelf reads as "not read yet" and nobody has to clear
    /// anything.</summary>
    private LibraryDatabase? _albumsFrom, _artistsFrom;

    /// <summary>Whether <see cref="Albums"/> was read from the collection that is open now. A closed
    /// library has nothing to read and counts as read.</summary>
    public bool AlbumsRead => !_library.IsOpen || ReferenceEquals(_albumsFrom, _library.Shelf);

    /// <summary>Whether <see cref="Artists"/> was read from the collection that is open now. See
    /// <see cref="AlbumsRead"/>.</summary>
    public bool ArtistsRead => !_library.IsOpen || ReferenceEquals(_artistsFrom, _library.Shelf);

    /// <summary>
    /// The albums, by relative folder, in the order the collection reads. A folder that holds tracks
    /// directly is an album, because the library's own shape is Artist/Album/track.
    /// </summary>
    public IReadOnlyList<string> Albums { get; private set; } = [];

    /// <summary>
    /// What the interface knows about each album, by folder: what it is called, when it joined the
    /// library, what year it is from and how much it has been played. These are all the values that
    /// the views show and can order by.
    ///
    /// <para>All of it comes from the mirror and none from the path. A folder name is a sanitised
    /// spelling of a tag (a filesystem does not accept every character in a band's name), it is fixed
    /// at import, and it says nothing about dates or plays.</para>
    /// </summary>
    public IReadOnlyDictionary<string, AlbumFacts> AlbumFacts { get; private set; }
        = new Dictionary<string, AlbumFacts>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What each album folder's genre is, where the record agrees with itself: the genre that most of
    /// its tracks carry. A folder with nothing tagged is absent.
    ///
    /// <para>This is beside <see cref="AlbumFacts"/> and not in it. The facts struct belongs to Core
    /// and is shared with the phone, and only one desktop view asks for the genre. It is still read
    /// on the same pass, because it is one more indexed GROUP BY over the table that is already being
    /// grouped.</para>
    /// </summary>
    public IReadOnlyDictionary<string, string> AlbumGenres { get; private set; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How many tracks each album folder holds directly. It is known from the same GROUP BY that the
    /// skeleton is built from. This lets a view that lists a record's tracks reserve the room for them
    /// before it reads them from the disk. Without it, every block on screen would grow when its
    /// tracklist arrived, and a list of them would shift under the pointer while it is scrolled.
    /// </summary>
    public IReadOnlyDictionary<string, int> TrackCounts { get; private set; }
        = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How many lines each album's tracklist holds beside its tracks: a divider for each disc of a
    /// multi-disc set, and a header for each work on a record marked classical. They are counted in
    /// track lines (a divider and a work header are each two of them, see the album list's template),
    /// so the reservation above can be calculated in one unit. A record with only tracks is absent.
    /// </summary>
    public IReadOnlyDictionary<string, int> HeaderLines { get; private set; }
        = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How long each album folder plays for. It is read on the same pass as the counts, because a
    /// shelf that can be narrowed to the favourites must be able to state what those records add up
    /// to. The total of the whole collection answers a different question from the one on screen.
    /// </summary>
    public IReadOnlyDictionary<string, double> FolderSeconds { get; private set; }
        = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which album folders hold at least one explicit track. This puts the E beside a record's title.
    /// It is beside <see cref="AlbumFacts"/> for the same reason as the genres: the facts struct
    /// belongs to Core and is shared with the phone, and this is read on the same pass anyway.
    /// </summary>
    public IReadOnlySet<string> ExplicitAlbums { get; private set; }
        = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The same collection read by who made it: one entry for each act, with the records that are
    /// theirs, the records they only appear on, and the captions that the artist's page gives each
    /// of those.
    ///
    /// <para>It is always whole. Nothing of the album views' search is in it, and that is why it is a
    /// value of its own and not four more fields in the searched part above. The artists wall has a
    /// search field of its own. The two fields are two searches over one collection, not one narrowing
    /// laid over both. Typing a record's name over the wall of covers must not remove names from the
    /// wall of artists behind it.</para>
    /// </summary>
    /// <param name="AlbumReleaseTypes">The release type that each record's files carry, where they
    /// carry one. The artist page shelves a record by it before it falls back to the title. See
    /// <see cref="ArtistShelves"/>.</param>
    /// <param name="AlbumGenres">What each record is filed under, where it agrees with itself. It is
    /// here and not beside the searched map above because the genres wall reads it. That wall is a way
    /// into the collection, as the artists wall is. If it were narrowed by the album views' field, it
    /// would shrink to the two cards that the matched records happened to be on.</param>
    public sealed record ArtistShelf(
        IReadOnlyList<ArtistWallItem> Acts,
        IReadOnlyDictionary<string, AlbumFacts> AlbumFacts,
        IReadOnlyDictionary<string, int> TrackCounts,
        IReadOnlySet<string> ExplicitAlbums,
        IReadOnlyDictionary<string, string> AlbumGenres,
        IReadOnlyDictionary<string, RPM.Core.Library.ReleaseType> AlbumReleaseTypes)
    {
        /// <summary>No library open: no acts, and nothing known about any record.</summary>
        public static ArtistShelf Empty { get; } = new(
            [],
            new Dictionary<string, AlbumFacts>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, RPM.Core.Library.ReleaseType>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The artists' side of the collection, as <see cref="RebuildArtistsAsync"/> last read
    /// it.</summary>
    public ArtistShelf Artists { get; private set; } = ArtistShelf.Empty;

    /// <summary>What the artists' side is read at: the whole collection. It is a named value and not a
    /// missing argument, because the searched passes below take the same parameter, and the difference
    /// between them is the point.</summary>
    private const string? WholeCollection = null;

    /// <summary>How many songs the shelf holds and how long they play, at the search it was read at.
    /// The library panel's subtitle states them. The count is the skeleton's own sum and not a second
    /// query. Only the running time costs a SUM.</summary>
    public int TotalTracks { get; private set; }

    public double TotalSeconds { get; private set; }

    /// <summary>
    /// Re-reads the shelf at the given search. It runs the GROUP BYs off the UI thread. A newer
    /// rebuild (the next keystroke or the next library change) cancels this one.
    /// </summary>
    public async Task RebuildAsync(string? search)
    {
        _rebuildCts?.Cancel();
        var cts = _rebuildCts = new CancellationTokenSource();
        var query = string.IsNullOrWhiteSpace(search) ? null : search.Trim();

        if (!_library.IsOpen)
        {
            AlbumFacts = new Dictionary<string, AlbumFacts>(StringComparer.OrdinalIgnoreCase);
            AlbumGenres = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            TrackCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            HeaderLines = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            FolderSeconds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            ExplicitAlbums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            TotalTracks = 0;
            TotalSeconds = 0;
            Albums = [];
            return;
        }

        List<Node> roots;
        int rootTracks;
        Dictionary<string, AlbumFacts> facts;
        Dictionary<string, string> genres;
        HashSet<string> explicitAlbums;
        double seconds;
        Dictionary<string, int> trackCounts = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> headerLines = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, double> folderSeconds = new(StringComparer.OrdinalIgnoreCase);
        var mirror = _library.Shelf;
        var read = await Cancellable.RunAsync(
            () => _library.Read(mirror, "the album wall's index", shelf =>
            {
                var counts = shelf.QueryFolderTrackCounts(query);
                foreach (var (folder, count) in counts)
                    trackCounts[folder] = count;
                foreach (var (folder, folderTime) in shelf.QueryFolderDurations(query))
                    folderSeconds[folder] = folderTime;
                foreach (var row in shelf.QueryAlbumHeaderLines(query))
                {
                    // One disc draws no divider (a record is one disc), so a set's dividers are its
                    // disc count and a single-disc record has none. Each divider takes two track
                    // lines, which is what the template gives it.
                    var lines = (row.Discs > 1 ? row.Discs : 0) + row.Works;
                    if (lines > 0)
                        headerLines[row.Folder] = lines * 2;
                }
                // Read on the same pass as the skeleton: the same indexed GROUP BYs over the same
                // table. Otherwise the views would go back to the database a second time to find
                // out about the folders they were just given.
                return (BuildSkeleton(counts),
                        ReadAlbumFacts(shelf, query),
                        ReadAlbumGenres(shelf, query),
                        new HashSet<string>(
                            shelf.QueryExplicitAlbums(query), StringComparer.OrdinalIgnoreCase),
                        shelf.QueryTotalDurationSeconds(query));
            }), cts.Token);

        if (read is not { Ran: true, Value: { } asked } || cts.IsCancellationRequested)
            return;

        // When the read is unanswered, the shelf stays as it was last read. A refusal has put the
        // problem screen up over the app (LibraryService.Read). A closed mirror is followed by a
        // Changed that rebuilds this.
        if (!asked.Answers(out var built, out _))
            return;

        ((roots, rootTracks), facts, genres, explicitAlbums, seconds) = built;

        AlbumFacts = facts;
        AlbumGenres = genres;
        ExplicitAlbums = explicitAlbums;
        TrackCounts = trackCounts;
        HeaderLines = headerLines;
        FolderSeconds = folderSeconds;
        // Every track, once: the counts are per folder and grouped by it, so their sum is the table,
        // including the library root's own loose tracks, which are the "" folder in the same list.
        TotalTracks = trackCounts.Values.Sum();
        TotalSeconds = seconds;
        Albums = AlbumOrder.Build(
            roots,
            folderOf: n => n.Folder,
            directTrackCountOf: n => n.TrackCount,
            childrenOf: n => n.Children,
            rootTracks);
        _albumsFrom = mirror;
    }

    /// <summary>
    /// Re-reads the artists' side of the collection, whole (see <see cref="ArtistShelf"/>).
    ///
    /// <para>It is a separate pass. It runs when the collection changes (an import, a delete or a
    /// library opened) and never on a keystroke, because a search cannot change who is in the
    /// collection. So the wall of names costs nothing for each keystroke, and a keystroke cannot cancel
    /// it half read and leave the wall showing an older collection than the covers behind it.</para>
    /// </summary>
    public async Task RebuildArtistsAsync()
    {
        _artistsCts?.Cancel();
        var cts = _artistsCts = new CancellationTokenSource();

        if (!_library.IsOpen)
        {
            Artists = ArtistShelf.Empty;
            return;
        }

        var mirror = _library.Shelf;
        var built = await Cancellable.RunAsync(
            () => _library.Read(mirror, "the artists wall", ReadArtistShelf), cts.Token);

        if (built is not { Ran: true, Value: { } asked } || cts.IsCancellationRequested)
            return;

        // When the read is unanswered, the wall stays as it was last read. See RebuildAsync.
        if (!asked.Answers(out var shelf, out _))
            return;

        Artists = shelf;
        _artistsFrom = mirror;
    }

    /// <summary>
    /// The artists' side of one mirror, whole: the acts, every record's facts, track counts, explicit
    /// records and genres. It runs inside <see cref="LibraryService.Read{T}"/> on a worker. The main
    /// window's artists and genres walls (on the collection on screen) and the Pegasus walls (on the
    /// listener's own collection) share it, so both read one collection in the same way.
    /// </summary>
    internal static ArtistShelf ReadArtistShelf(LibraryDatabase db)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, count) in db.QueryFolderTrackCounts(WholeCollection))
            counts[folder] = count;

        // Who is on each record, split into the acts that each credit names. See ArtistWall for why a
        // credit is not a name.
        return new ArtistShelf(
            ArtistWall.Build(db.QueryArtistCredits(WholeCollection)),
            ReadAlbumFacts(db, WholeCollection),
            counts,
            new HashSet<string>(db.QueryExplicitAlbums(WholeCollection), StringComparer.OrdinalIgnoreCase),
            ReadAlbumGenres(db, WholeCollection),
            db.QueryReleaseTypesByFolder());
    }

    /// <summary>
    /// One album's facts, with the folder standing in wherever the tags say nothing. An untagged
    /// record still sorts and still reads as something and not as a blank.
    /// </summary>
    private static Dictionary<string, AlbumFacts> ReadAlbumFacts(LibraryDatabase db, string? search)
    {
        var years = new Dictionary<string, List<(int Year, int Count)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, year, count) in db.QueryAlbumYearCounts(search))
        {
            if (!years.TryGetValue(folder, out var list))
                years[folder] = list = [];
            list.Add((year, count));
        }

        var facts = new Dictionary<string, AlbumFacts>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in db.QueryAlbumFacts(search))
        {
            var parts = row.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
            facts[row.Folder] = new AlbumFacts(
                Album: row.Album ?? (parts.Length > 0 ? parts[^1] : ""),
                Artist: row.AlbumArtist ?? (parts.Length >= 2 ? parts[^2] : ""),
                AddedAt: row.AddedAt,
                // The rule is AlbumSort's, so the desktop views and the phone cannot disagree about
                // what year a record is from.
                Year: years.TryGetValue(row.Folder, out var counted) ? AlbumSort.YearFrom(counted) : 0,
                Plays: row.Plays);
        }
        return facts;
    }

    /// <summary>
    /// Each album's genre, where it has one: the genre that most of its tracks carry. When two tie,
    /// the alphabetically first one wins. A record that is half Jazz and half Blues must land in one
    /// definite place, or it would move between two rebuilds that read the same rows.
    /// </summary>
    private static Dictionary<string, string> ReadAlbumGenres(LibraryDatabase db, string? search)
    {
        var best = new Dictionary<string, (string Genre, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, genre, count) in db.QueryAlbumGenreCounts(search))
        {
            if (best.TryGetValue(folder, out var standing)
                && (standing.Count > count
                    || (standing.Count == count
                        && string.Compare(standing.Genre, genre, StringComparison.CurrentCultureIgnoreCase) <= 0)))
                continue;
            best[folder] = (genre, count);
        }

        var genres = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, standing) in best)
            genres[folder] = standing.Genre;
        return genres;
    }

    /// <summary>Folder chain to nodes. Direct-track counts land on their folder. Children (subfolders)
    /// are sorted by name. Runs on a worker thread, so it uses plain objects only.</summary>
    private static (List<Node> Roots, int RootTracks) BuildSkeleton(
        IReadOnlyList<(string Folder, int TrackCount)> counts)
    {
        var roots = new List<Node>();
        var nodes = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        var rootTracks = 0;

        Node GetFolder(string path, int trackCount)
        {
            if (nodes.TryGetValue(path, out var existing))
                return existing;
            var slash = path.LastIndexOf('/');
            var node = new Node(path, trackCount);
            nodes[path] = node;
            if (slash < 0)
                roots.Add(node);
            else
                GetFolder(path[..slash], 0).Children.Add(node);
            return node;
        }

        foreach (var (folder, trackCount) in counts)
        {
            if (folder.Length == 0)
                rootTracks = trackCount;
            else
                GetFolder(folder, trackCount);
        }

        foreach (var node in nodes.Values)
            node.Children.Sort(static (a, b) => Compare(a, b));
        roots.Sort(static (a, b) => Compare(a, b));

        return (roots, rootTracks);

        static int Compare(Node a, Node b)
            => string.Compare(a.Folder, b.Folder, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _rebuildCts?.Cancel();
        _artistsCts?.Cancel();
    }
}
