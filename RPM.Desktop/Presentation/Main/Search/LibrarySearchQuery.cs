using RPM.Core.Library;

namespace RPM.Desktop.Presentation.Main.Search;

/// <summary>What one library search found, each kind in its own list.</summary>
internal sealed record LibrarySearchFound(
    IReadOnlyList<ArtistWallItem> Artists,
    IReadOnlyList<SearchRecord> Records,
    IReadOnlyList<SearchSong> Songs,
    IReadOnlyList<SearchList> Playlists,
    IReadOnlyList<GenreWallItem> Genres)
{
    public bool Nothing =>
        Artists.Count == 0 && Records.Count == 0 && Songs.Count == 0 && Playlists.Count == 0 && Genres.Count == 0;
}

/// <summary>
/// The library search itself, with no window: what <see cref="LibrarySearchView"/> runs on a worker for the
/// words in the box.
/// </summary>
/// <remarks>
/// The rules are the phones' (<c>LibraryPage.results</c> on the iPhone): songs and records match their title,
/// artist, record or record artist, ignoring case; artists, genres and playlists match their name folded
/// (<see cref="ArtistWall.SearchKey"/>), so "beyonce" finds Beyoncé and "acdc" finds AC/DC.
/// </remarks>
internal static class LibrarySearchQuery
{
    /// <summary>The most songs one search returns. See the phones' <c>SongSearch.ceiling</c>.</summary>
    public const int SongCeiling = 300;

    /// <summary>The most records one search returns. See the phones' <c>SongSearch.recordCeiling</c>.</summary>
    public const int RecordCeiling = 60;

    /// <summary>
    /// What <paramref name="sources"/> hold that <paramref name="asked"/> names. Null for a search that was
    /// stopped through <paramref name="ct"/> or that failed.
    /// </summary>
    /// <remarks><b>It never throws.</b> The view awaits it on the UI thread, and a throw there (a stopped
    /// search is one on most keystrokes) stops the interface for seconds while a debugger is attached. So
    /// a stop is checked, never thrown, and a failure is caught here, on the worker.</remarks>
    public static LibrarySearchFound? Run(LibrarySearchSources sources, string asked, CancellationToken ct)
    {
        try
        {
            return Search(sources, asked, ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static LibrarySearchFound? Search(LibrarySearchSources sources, string asked, CancellationToken ct)
    {
        var folded = ArtistWall.SearchKey(asked);

        bool Named(string name) => folded.Length > 0 && ArtistWall.SearchKey(name).Contains(folded, StringComparison.Ordinal);

        bool Holds(string? field) => !string.IsNullOrEmpty(field) && field.Contains(asked, StringComparison.OrdinalIgnoreCase);

        var artists = sources.Artists.Where(a => Named(a.Name)).ToList();
        if (ct.IsCancellationRequested)
            return null;

        // A record is found by its own name or who made it, never by one of its songs: a record listed
        // because one track matched looks like a wrong answer. The songs answer that.
        var records = sources.Records.Where(r => Holds(r.Album) || Holds(r.Artist)).Take(RecordCeiling).ToList();
        if (ct.IsCancellationRequested)
            return null;

        var lists = sources.Playlists.Where(p => Named(p.Name)).ToList();
        var genres = sources.Genres.Where(g => Named(g.Name)).ToList();
        if (ct.IsCancellationRequested)
            return null;

        var songs = sources.Songs(asked, SongCeiling);
        if (ct.IsCancellationRequested)
            return null;

        return new LibrarySearchFound(artists, records, songs, lists, genres);
    }
}
