using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// How often each playlist has been played: the count that the home screen's quick selection uses
/// to rank the lists.
///
/// <para>The key is the part worth testing. A generated list is written under
/// <see cref="LibraryDatabase.PlaylistKey"/> and looked up under the same key again. If the spelling
/// differs between the two, the count never matches and nothing on screen says why: the list simply
/// never appears.</para>
/// </summary>
public class PlaylistPlaysTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public PlaylistPlaysTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"plplays-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static long At(int day) => new DateTime(2026, 9, day, 12, 0, 0, DateTimeKind.Utc).Ticks;

    [Fact]
    public void A_list_nothing_has_played_is_not_offered()
        => Assert.Empty(_db.QueryMostPlayedPlaylists(10));

    [Fact]
    public void The_list_played_most_comes_first()
    {
        _db.NotePlaylistPlayed(PlaylistPlayKind.Kept, "1", At(1));
        _db.NotePlaylistPlayed(PlaylistPlayKind.Kept, "2", At(2));
        _db.NotePlaylistPlayed(PlaylistPlayKind.Kept, "2", At(3));

        Assert.Equal(["2", "1"], _db.QueryMostPlayedPlaylists(10).Select(r => r.Key));
    }

    [Fact]
    public void Plays_of_one_list_add_up()
    {
        _db.NotePlaylistPlayed(PlaylistPlayKind.Kept, "7", At(1));
        _db.NotePlaylistPlayed(PlaylistPlayKind.Kept, "7", At(2));
        _db.NotePlaylistPlayed(PlaylistPlayKind.Kept, "7", At(3));

        var row = Assert.Single(_db.QueryMostPlayedPlaylists(10));
        Assert.Equal(3, row.Plays);
        Assert.Equal(At(3), row.LastPlayedUtcTicks);
    }

    /// <summary>A kept list and a generated one can have the same key text without being the same
    /// list: the kind is part of the key.</summary>
    [Fact]
    public void The_two_kinds_are_two_namespaces()
    {
        _db.NotePlaylistPlayed(PlaylistPlayKind.Kept, "3", At(1));
        _db.NotePlaylistPlayed(PlaylistPlayKind.Generated, "3", At(2));

        var rows = _db.QueryMostPlayedPlaylists(10);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(1, r.Plays));
        Assert.Contains(rows, r => r.Kind == PlaylistPlayKind.Kept);
        Assert.Contains(rows, r => r.Kind == PlaylistPlayKind.Generated);
    }

    /// <summary>The round trip the feature depends on: a generated list written under its key is read
    /// back under exactly that key, so the home screen can match it to the card it drew.</summary>
    [Fact]
    public void A_generated_lists_key_survives_the_round_trip()
    {
        var id = SmartPlaylistId.Within(SmartPlaylistKind.FavoriteTracks, "Rock");
        var key = LibraryDatabase.PlaylistKey(id);

        _db.NotePlaylistPlayed(PlaylistPlayKind.Generated, key, At(1));

        var row = Assert.Single(_db.QueryMostPlayedPlaylists(10));
        Assert.Equal(key, row.Key);
        Assert.Equal(LibraryDatabase.PlaylistKey(id), row.Key);
    }

    /// <summary>Two lists that differ only in the part of the id that is not the kind are two keys. A
    /// key built from the kind alone would merge "Favourites" and "Favourites, rock" into one
    /// count.</summary>
    [Fact]
    public void A_kind_alone_does_not_name_a_generated_list()
    {
        var whole = SmartPlaylistId.WholeShelf(SmartPlaylistKind.FavoriteTracks);
        var rock = SmartPlaylistId.Within(SmartPlaylistKind.FavoriteTracks, "Rock");

        Assert.NotEqual(LibraryDatabase.PlaylistKey(whole), LibraryDatabase.PlaylistKey(rock));

        _db.NotePlaylistPlayed(PlaylistPlayKind.Generated, LibraryDatabase.PlaylistKey(whole), At(1));
        _db.NotePlaylistPlayed(PlaylistPlayKind.Generated, LibraryDatabase.PlaylistKey(rock), At(2));

        Assert.Equal(2, _db.QueryMostPlayedPlaylists(10).Count);
    }

    [Fact]
    public void A_list_with_no_key_is_not_written()
    {
        _db.NotePlaylistPlayed(PlaylistPlayKind.Kept, "", At(1));
        _db.NotePlaylistPlayed(PlaylistPlayKind.Kept, "   ", At(1));

        Assert.Empty(_db.QueryMostPlayedPlaylists(10));
    }

    [Fact]
    public void Nothing_is_asked_for_nothing()
    {
        _db.NotePlaylistPlayed(PlaylistPlayKind.Kept, "1", At(1));
        Assert.Empty(_db.QueryMostPlayedPlaylists(0));
    }

    /// <summary>The counts are this library's, and they come back when it is opened again.</summary>
    [Fact]
    public void What_was_counted_is_there_on_the_next_open()
    {
        _db.NotePlaylistPlayed(PlaylistPlayKind.Kept, "5", At(1));
        _db.Dispose();

        using var reopened = new LibraryDatabase(MirrorLocation.InFolder(_root));
        Assert.Equal(1, Assert.Single(reopened.QueryMostPlayedPlaylists(10)).Plays);
    }
}
