using RPM.Core.Library;
using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>
/// The hearts a listener gives to somebody else's records.
///
/// <para>
/// A mark on a borrowed row is not the owner's data and is never sent back to them: it is what the
/// listener at THIS computer thinks of a record on a friend's disk, written on the row in the mirror
/// kept for that computer. The owner's own hearts are not in those rows to be confused with it: a
/// guest's listing carries no favourite in either direction.
/// </para>
///
/// <para>
/// So the mark exists exactly as long as the row does, and that is its only availability rule. A
/// record the friend deletes removes the heart with it. A record only on their laptop becomes
/// unavailable when they leave the network, and is available again when they return. Nothing is
/// stored next to the shelf that could outlive what it is about.
/// </para>
/// </summary>
public class PeerBorrowedHeartTests : IDisposable
{
    private const string Friend = "FRIEND1";

    private readonly string _here;
    private readonly LibraryDatabase _shelf;

    public PeerBorrowedHeartTests()
    {
        _here = Path.Combine(Path.GetTempPath(), $"hearts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_here);
        _shelf = new LibraryDatabase(MirrorLocation.InFolder(_here));
    }

    public void Dispose()
    {
        _shelf.Dispose();
        try { Directory.Delete(_here, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static PeerTrack Track(string rpmId, string path, bool inCloud, string title = "One") => new()
    {
        RpmId = rpmId,
        RelativePath = path,
        Title = title,
        Artist = "Band",
        AlbumArtist = "Band",
        Album = "Record",
        TrackNumber = 1,
        DurationSeconds = 200,
        SizeBytes = 5_000_000,
        InCloud = inCloud,
    };

    private PeerMirrorResult Lend(params PeerTrack[] listing) =>
        _shelf.MirrorPeerLibrary(
            Friend, PeerLibraryMirror.ToCatalogRows(listing),
            cloudOnly: PeerLibraryMirror.CloudBackedIds(listing));

    private static readonly PeerTrack One = Track("gid-1", "Band/Record/01 One.flac", inCloud: true);
    private static readonly PeerTrack Two = Track("gid-2", "Band/Record/02 Two.flac", inCloud: false, title: "Two");

    // ── a song ──

    [Fact]
    public void A_heart_given_to_a_borrowed_song_stays_on_it()
    {
        Lend(One, Two);
        Assert.True(_shelf.SetTrackFavorite("Band/Record/01 One.flac", true, DateTime.UtcNow.Ticks));

        // The listing arrives again, as it does every few minutes, with no favourite (it never has
        // one). That must not be read as the mark being removed.
        Lend(One, Two);

        Assert.True(_shelf.IsTrackFavorite("Band/Record/01 One.flac"));
        Assert.False(_shelf.IsTrackFavorite("Band/Record/02 Two.flac"));
    }

    [Fact]
    public void A_hearted_row_is_not_reported_as_changed_by_every_pass()
    {
        Lend(One, Two);
        _shelf.SetTrackFavorite("Band/Record/01 One.flac", true, DateTime.UtcNow.Ticks);

        // If the mark were part of the comparison, the row would always differ from its own listing:
        // the shelf would report the row changed every five minutes and redraw over an unchanged heart.
        var again = Lend(One, Two);
        Assert.Equal(0, again.Total);
    }

    [Fact]
    public void A_heart_survives_the_owner_retagging_the_song()
    {
        Lend(One, Two);
        _shelf.SetTrackFavorite("Band/Record/01 One.flac", true, DateTime.UtcNow.Ticks);

        var retitled = One with { Title = "One (Remastered)" };
        Assert.Equal(1, Lend(retitled, Two).Updated);

        Assert.True(_shelf.IsTrackFavorite("Band/Record/01 One.flac"));
    }

    [Fact]
    public void A_song_the_friend_deletes_takes_the_heart_with_it()
    {
        Lend(One, Two);
        _shelf.SetTrackFavorite("Band/Record/01 One.flac", true, DateTime.UtcNow.Ticks);

        // Their next listing does not have it. There is nothing left to be marked, and a mark on
        // something this machine never owned should then go.
        Lend(Two);

        Assert.False(_shelf.IsTrackFavorite("Band/Record/01 One.flac"));
        Assert.Empty(_shelf.QuerySmartPlaylist(SmartPlaylistId.WholeShelf(SmartPlaylistKind.FavoriteTracks), DateTime.UtcNow.Ticks, advance: true));
    }

    // ── a record ──

    [Fact]
    public void A_heart_on_a_borrowed_record_is_not_cleared_by_the_owner_editing_it()
    {
        Lend(One);
        _shelf.SetAlbumFavorite("Band/Record", true);

        // Their row, newer than anything here, describing the RECORD. A guest's row has no favourite;
        // if that were taken as part of the row, it would clear this one silently and permanently.
        _shelf.ApplyAlbumMetaEdits(
            [new AlbumMetaEdit(
                AlbumMeta.Default("Band/Record") with { Classical = true }, DateTime.UtcNow.Ticks)],
            ArrivingAlbumMarks.NotTheHeart);

        Assert.True(_shelf.GetAlbumMeta("Band/Record").IsFavorite);
        Assert.True(_shelf.GetAlbumMeta("Band/Record").Classical);   // …and the rest of it did land
        Assert.Contains("Band/Record", _shelf.FavoriteAlbumFolders());
    }

    [Fact]
    public void Between_the_listeners_own_computers_the_heart_does_travel()
    {
        Lend(One);

        // The other case, and the reason the caller states it explicitly: two of one person's
        // machines are one collection in two places, and a favourite that was not shared between
        // them would be missing from the collection.
        _shelf.ApplyAlbumMetaEdits(
            [new AlbumMetaEdit(
                AlbumMeta.Default("Band/Record") with { IsFavorite = true }, DateTime.UtcNow.Ticks)],
            ArrivingAlbumMarks.Whole);

        Assert.True(_shelf.GetAlbumMeta("Band/Record").IsFavorite);
    }

    // ── and the list they make ──

    [Fact]
    public void My_favourites_in_their_library_are_a_list_of_their_songs()
    {
        Lend(One, Two);
        _shelf.SetTrackFavorite("Band/Record/01 One.flac", true, DateTime.UtcNow.Ticks);
        _shelf.SetTrackFavorite("Band/Record/02 Two.flac", true, DateTime.UtcNow.Ticks);

        var loved = _shelf.QuerySmartPlaylist(SmartPlaylistId.WholeShelf(SmartPlaylistKind.FavoriteTracks), DateTime.UtcNow.Ticks, advance: true);
        Assert.Equal(2, loved.Count);
        Assert.Equal(2, _shelf.SmartPlaylistTotals(SmartPlaylistId.WholeShelf(SmartPlaylistKind.FavoriteTracks), DateTime.UtcNow.Ticks).Tracks);
    }

    [Fact]
    public void Once_they_are_gone_it_holds_only_what_can_still_be_played()
    {
        Lend(One, Two);
        _shelf.SetTrackFavorite("Band/Record/01 One.flac", true, DateTime.UtcNow.Ticks);
        _shelf.SetTrackFavorite("Band/Record/02 Two.flac", true, DateTime.UtcNow.Ticks);

        _shelf.CloudRowsOnly = true;

        // The list exists so those songs can be played. Half of it silently not playing is the worst
        // result; the song they moved from their disk to the cloud can still play.
        var loved = _shelf.QuerySmartPlaylist(SmartPlaylistId.WholeShelf(SmartPlaylistKind.FavoriteTracks), DateTime.UtcNow.Ticks, advance: true);
        Assert.Equal(["gid-1"], loved.Select(t => t.RpmId));
        Assert.Equal(1, _shelf.SmartPlaylistTotals(SmartPlaylistId.WholeShelf(SmartPlaylistKind.FavoriteTracks), DateTime.UtcNow.Ticks).Tracks);

        // …and it comes back as soon as they return to the network. Nothing was deleted.
        _shelf.CloudRowsOnly = false;
        Assert.Equal(2, _shelf.QuerySmartPlaylist(SmartPlaylistId.WholeShelf(SmartPlaylistKind.FavoriteTracks), DateTime.UtcNow.Ticks, advance: true).Count);
    }

    // ── and the filter on the wall ──

    [Fact]
    public void Narrowing_the_wall_to_favourites_shows_the_ones_marked_on_that_shelf()
    {
        Lend(One, Two);
        _shelf.SetAlbumFavorite("Band/Record", true);
        _shelf.SetAlbumFavorite("Band/Not here", true);

        // The wall narrows by intersecting the marks with the records it is actually showing (see
        // MainPage.FeedAlbumViews), which drops a folder whose records have gone.
        var marked = _shelf.FavoriteAlbumFolders();
        var showing = _shelf.QueryFolderTrackCounts().Select(f => f.Folder).ToList();

        Assert.Equal(["Band/Record"], showing.Where(marked.Contains));
    }

    [Fact]
    public void And_once_they_are_gone_only_the_records_that_still_play()
    {
        // Two records this time, one of them only on their disk.
        Lend(One, Track("gid-3", "Band/Elsewhere/03 Three.flac", inCloud: false, title: "Three"));
        _shelf.SetAlbumFavorite("Band/Record", true);
        _shelf.SetAlbumFavorite("Band/Elsewhere", true);

        var marked = _shelf.FavoriteAlbumFolders();
        Assert.Equal(2, _shelf.QueryFolderTrackCounts().Count(f => marked.Contains(f.Folder)));

        _shelf.CloudRowsOnly = true;

        // Both are still marked (nothing was deleted, and both return when they do), but only one of
        // them is on the wall to narrow to.
        Assert.Equal(2, _shelf.FavoriteAlbumFolders().Count);
        Assert.Equal(
            ["Band/Record"],
            _shelf.QueryFolderTrackCounts().Select(f => f.Folder).Where(marked.Contains));
    }

    [Fact]
    public void Only_the_hearts_are_generated_over_a_borrowed_shelf()
    {
        // The other lists would be wrong there: "recently added" would date every record to the day
        // the share was set up, and the listening lists would describe the borrower, not the
        // collection. See SmartPlaylists.OnABorrowedShelf.
        Assert.Equal([SmartPlaylistKind.FavoriteTracks], SmartPlaylists.OnABorrowedShelf);
    }
}
