using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// Favourites: the per-track flag (schema v32), the favourite albums the shelf can be narrowed to, and
/// the generated playlist that is nothing but a query over the first of them.
/// </summary>
public class FavoriteTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public FavoriteTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmfav-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>The favourites' rule has no time window, so any clock value works.</summary>
    private const long Now = 638_000_000_000_000_000;

    private LibraryTrack Add(string relativePath, double seconds = 200)
    {
        var slash = relativePath.LastIndexOf('/');
        _db.Upsert(new LibraryTrack
        {
            RelativePath = relativePath,
            Folder = slash < 0 ? "" : relativePath[..slash],
            FileName = slash < 0 ? relativePath : relativePath[(slash + 1)..],
            Extension = Path.GetExtension(relativePath),
            SizeBytes = 5_000_000,
            ModifiedUtcTicks = 1,
            DurationSeconds = seconds,
            Title = Path.GetFileNameWithoutExtension(relativePath),
        });
        return _db.FindByRelativePath(relativePath)!;
    }

    // ── one song ──

    [Fact]
    public void A_track_is_not_a_favorite_until_it_is_said()
    {
        Add("Nick Drake/Pink Moon/01 Pink Moon.flac");
        Assert.False(_db.IsTrackFavorite("Nick Drake/Pink Moon/01 Pink Moon.flac"));
    }

    [Fact]
    public void Marking_a_track_stamps_when_it_was_said()
    {
        var path = "Nick Drake/Pink Moon/01 Pink Moon.flac";
        Add(path);

        Assert.True(_db.SetTrackFavorite(path, favorite: true, atUtcTicks: 4242));

        var row = _db.FindByRelativePath(path)!;
        Assert.True(row.IsFavorite);
        Assert.Equal(4242, row.FavoritedUtcTicks);
    }

    [Fact]
    public void Unmarking_a_track_clears_the_flag_and_the_stamp()
    {
        var path = "Nick Drake/Pink Moon/01 Pink Moon.flac";
        Add(path);
        _db.SetTrackFavorite(path, favorite: true, atUtcTicks: 4242);

        _db.SetTrackFavorite(path, favorite: false, atUtcTicks: 9999);

        var row = _db.FindByRelativePath(path)!;
        Assert.False(row.IsFavorite);
        Assert.Equal(0, row.FavoritedUtcTicks);
    }

    [Fact]
    public void A_path_the_library_does_not_hold_marks_nothing()
        => Assert.False(_db.SetTrackFavorite("Nobody/Nothing/01.flac", favorite: true, atUtcTicks: 1));

    /// <summary>A favourite belongs to the row, so reading the file again (a retag, a rescan) must not
    /// remove it. The upsert leaves the column alone on purpose; this test checks that.</summary>
    [Fact]
    public void A_retag_leaves_the_favorite_alone()
    {
        var path = "Nick Drake/Pink Moon/01 Pink Moon.flac";
        var track = Add(path);
        _db.SetTrackFavorite(path, favorite: true, atUtcTicks: 4242);

        _db.Upsert(track with { Title = "Pink Moon (2013 Remaster)", ModifiedUtcTicks = 2 });

        var row = _db.FindByRelativePath(path)!;
        Assert.True(row.IsFavorite);
        Assert.Equal(4242, row.FavoritedUtcTicks);
    }

    [Fact]
    public void Favorites_among_a_tracklist_are_answered_in_one_pass()
    {
        Add("A/One/01.flac");
        Add("A/One/02.flac");
        Add("A/One/03.flac");
        _db.SetTrackFavorite("A/One/02.flac", favorite: true, atUtcTicks: 1);

        var favorites = _db.FavoriteTracksAmong(["A/One/01.flac", "A/One/02.flac"]);

        Assert.Equal(["A/One/02.flac"], favorites);
    }

    [Fact]
    public void Asking_about_no_tracks_answers_with_none()
        => Assert.Empty(_db.FavoriteTracksAmong([]));

    // ── whole records ──

    [Fact]
    public void Favorite_album_folders_are_the_ones_marked()
    {
        _db.SetAlbumFavorite("Nick Drake/Pink Moon", true);
        _db.SetAlbumFavorite("Talk Talk/Laughing Stock", true);
        _db.SetAlbumFavorite("Talk Talk/Laughing Stock", false);

        Assert.Equal(["Nick Drake/Pink Moon"], _db.FavoriteAlbumFolders());
    }

    // ── the generated playlist ──

    [Fact]
    public void The_favorites_playlist_is_the_marked_songs_newest_first()
    {
        Add("A/One/01.flac");
        Add("A/One/02.flac");
        Add("A/One/03.flac");
        _db.SetTrackFavorite("A/One/01.flac", favorite: true, atUtcTicks: 100);
        _db.SetTrackFavorite("A/One/03.flac", favorite: true, atUtcTicks: 300);

        var tracks = _db.QuerySmartPlaylist(SmartPlaylistId.WholeShelf(SmartPlaylistKind.FavoriteTracks), Now, advance: true);

        Assert.Equal(["A/One/03.flac", "A/One/01.flac"], tracks.Select(t => t.RelativePath));
    }

    [Fact]
    public void The_favorites_playlist_stops_at_its_limit()
    {
        for (var i = 0; i < 5; i++)
        {
            var path = $"A/One/{i:00}.flac";
            Add(path);
            _db.SetTrackFavorite(path, favorite: true, atUtcTicks: 100 + i);
        }

        Assert.Equal(2, _db.QuerySmartPlaylist(SmartPlaylistId.WholeShelf(SmartPlaylistKind.FavoriteTracks), Now, limit: 2, advance: true).Count);
    }

    [Fact]
    public void The_favorites_playlist_totals_count_the_marked_songs_only()
    {
        Add("A/One/01.flac", seconds: 120);
        Add("A/One/02.flac", seconds: 240);
        _db.SetTrackFavorite("A/One/02.flac", favorite: true, atUtcTicks: 1);

        var (count, seconds, _) = _db.SmartPlaylistTotals(SmartPlaylistId.WholeShelf(SmartPlaylistKind.FavoriteTracks), Now);

        Assert.Equal(1, count);
        Assert.Equal(240, seconds);
    }

    [Fact]
    public void An_empty_favorites_playlist_totals_nothing()
    {
        Add("A/One/01.flac");
        Assert.Equal((0, 0d, false), _db.SmartPlaylistTotals(SmartPlaylistId.WholeShelf(SmartPlaylistKind.FavoriteTracks), Now));
    }

    // ── the shelf's own running time, per record ──

    [Fact]
    public void Folder_durations_are_read_per_album()
    {
        Add("A/One/01.flac", seconds: 100);
        Add("A/One/02.flac", seconds: 50);
        Add("B/Two/01.flac", seconds: 30);

        var durations = _db.QueryFolderDurations().ToDictionary(d => d.Folder, d => d.Seconds);

        Assert.Equal(150, durations["A/One"]);
        Assert.Equal(30, durations["B/Two"]);
    }
}
