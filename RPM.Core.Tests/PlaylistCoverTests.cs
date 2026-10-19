using Microsoft.Data.Sqlite;
using RPM.Core.Library;
using Xunit;

namespace RPM.Core.Tests;

public class PlaylistCoverTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public PlaylistCoverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mdpc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void Automatic_is_the_empty_wire_value()
    {
        Assert.Equal("", new PlaylistCover.Automatic().ToWire());
        Assert.IsType<PlaylistCover.Automatic>(PlaylistCover.FromWire(""));
        Assert.IsType<PlaylistCover.Automatic>(PlaylistCover.FromWire(null));
    }

    [Fact]
    public void Records_round_trips_its_folders_in_order()
    {
        var made = new PlaylistCover.Records(["/a", "/b", "/c", "/d"]);
        var read = Assert.IsType<PlaylistCover.Records>(PlaylistCover.FromWire(made.ToWire()));
        Assert.Equal(new[] { "/a", "/b", "/c", "/d" }, read.Folders);
    }

    [Fact]
    public void Figure_round_trips_style_and_seed()
    {
        var read = Assert.IsType<PlaylistCover.Figure>(
            PlaylistCover.FromWire(new PlaylistCover.Figure(2, 0x9F3A1C0B, CoverMark.None).ToWire()));
        Assert.Equal(2, read.Style);
        Assert.Equal(0x9F3A1C0Bu, read.Seed);
    }

    // The mark is a fourth part on the wire, and only when there is one. Both halves matter: a marked
    // cover has to come back marked, and an unmarked one has to come back written exactly as every
    // build before the mark existed wrote it. Otherwise, the first time anybody opens this library on an
    // older build, every generated cover in it reads as unreadable and falls back to automatic.
    [Fact]
    public void The_advisory_mark_survives_the_wire_and_costs_nothing_when_it_is_off()
    {
        Assert.Equal("fig:2:99", new PlaylistCover.Figure(2, 99u, CoverMark.None).ToWire());
        Assert.Equal("fig:2:99:x", new PlaylistCover.Figure(2, 99u, CoverMark.Explicit).ToWire());

        Assert.Equal(
            new PlaylistCover.Figure(2, 99u, CoverMark.Explicit),
            PlaylistCover.FromWire("fig:2:99:x"));

        // What an older build wrote: three parts, and it means no mark rather than an unknown one.
        Assert.Equal(new PlaylistCover.Figure(2, 99u, CoverMark.None), PlaylistCover.FromWire("fig:2:99"));

        // And a fourth part this build has never heard of folds to the automatic cover, which is the
        // whole file's rule for a value out of the future.
        Assert.IsType<PlaylistCover.Automatic>(PlaylistCover.FromWire("fig:2:99:zz"));
    }

    [Fact]
    public void Picture_round_trips()
        => Assert.IsType<PlaylistCover.Picture>(PlaylistCover.FromWire(new PlaylistCover.Picture().ToWire()));

    // A borrowed shelf's copy of one of the owner's generated lists. All three parts of the id come
    // back, because "calm", "calm rock" and "calm in the seventies" are three different pictures.
    [Theory]
    [InlineData(SmartPlaylistKind.Calm, null, 0)]
    [InlineData(SmartPlaylistKind.Calm, "Rock", 0)]
    [InlineData(SmartPlaylistKind.Decade, null, 1970)]
    [InlineData(SmartPlaylistKind.Energetic, "Electronic / Dance", 0)]
    public void A_generated_list_round_trips_all_of_its_id(SmartPlaylistKind kind, string? genre, int decade)
    {
        var cover = new PlaylistCover.Generated(new SmartPlaylistId(kind, genre, decade));
        Assert.Equal(cover, PlaylistCover.FromWire(cover.ToWire()));
    }

    // A borrowed node whose owner chose its picture: the owner's node id and the picture's stamp both
    // come back, because together they are what the picture is asked for and kept by.
    [Fact]
    public void A_picture_the_owner_chose_round_trips_their_node_and_its_stamp()
    {
        var cover = new PlaylistCover.ChosenByOwner(42, "0A1B2C3D4E5F6071");
        Assert.Equal(cover, PlaylistCover.FromWire(cover.ToWire()));
    }

    // The genre is the last part and is read back whole, so a colon inside one does not cut it short.
    [Fact]
    public void A_genre_with_a_colon_in_it_comes_back_whole()
    {
        var cover = new PlaylistCover.Generated(new SmartPlaylistId(SmartPlaylistKind.Calm, "Odd: genre", 0));
        Assert.Equal(cover, PlaylistCover.FromWire(cover.ToWire()));
    }

    // Stored by the kind's name, so reordering the enum does not turn one list into another.
    [Fact]
    public void A_generated_list_is_stored_by_the_name_of_its_kind()
        => Assert.Equal(
            "gen:Calm:0:Rock",
            new PlaylistCover.Generated(SmartPlaylistId.Within(SmartPlaylistKind.Calm, "Rock")).ToWire());

    // A value this build does not understand is a cover it cannot draw, and the cover it CAN draw for
    // any playlist is the automatic one. Never an exception: this is read on the tree's own load path.
    [Theory]
    [InlineData("sparkle:9")]
    [InlineData("fig:notanumber:7")]
    [InlineData("fig:2")]
    [InlineData("records:")]
    [InlineData("gen:Windswept:0:")]
    [InlineData("gen:Calm:notanumber:")]
    [InlineData("gen:Calm")]
    [InlineData("owner:notanumber:AB")]
    [InlineData("owner:42:")]
    [InlineData("owner:42")]
    public void An_unreadable_value_reads_as_automatic(string wire)
        => Assert.IsType<PlaylistCover.Automatic>(PlaylistCover.FromWire(wire));

    [Fact]
    public void A_playlist_starts_automatic_and_remembers_what_it_is_set_to()
    {
        var id = _db.CreatePlaylist(null, "Rainy");

        Assert.IsType<PlaylistCover.Automatic>(
            _db.GetPlaylistNodes().First(n => n.Id == id).Cover);

        _db.SetPlaylistCover(id, new PlaylistCover.Figure(3, 12345u, CoverMark.None));

        var read = Assert.IsType<PlaylistCover.Figure>(
            _db.GetPlaylistNodes().First(n => n.Id == id).Cover);
        Assert.Equal(3, read.Style);
        Assert.Equal(12345u, read.Seed);
    }

    // The Now-playing node is the player's queue, not a playlist somebody made. This is the same refusal
    // RenamePlaylistNode and MovePlaylistNode already make. The database is the one place a caller
    // cannot forget to check first, which is the whole reason the guard belongs here rather than only
    // in whatever window offers the choice.
    [Fact]
    public void Now_playing_keeps_no_cover_a_caller_tries_to_give_it()
    {
        var queue = _db.GetPlaylistNodes().Single(n => n.IsNowPlaying).Id;

        _db.SetPlaylistCover(queue, new PlaylistCover.Figure(1, 999u, CoverMark.None));

        Assert.IsType<PlaylistCover.Automatic>(
            _db.GetPlaylistNodes().First(n => n.Id == queue).Cover);
    }

    // SetPlaylistCover must update the stamp like every other pl_node writer.
    // PlaylistNode does not carry modified_utc_ticks and the public surface never reads it, so this
    // reads the column directly, the same way LibraryDatabaseTests already opens a raw connection onto
    // the same file when a test needs to see something the model does not expose.
    [Fact]
    public void Setting_a_cover_moves_the_modified_stamp()
    {
        var id = _db.CreatePlaylist(null, "Rainy");
        var before = ModifiedTicks(id);

        _db.SetPlaylistCover(id, new PlaylistCover.Figure(3, 12345u, CoverMark.None));

        Assert.True(ModifiedTicks(id) > before);
    }

    private long ModifiedTicks(long nodeId)
    {
        using var connection = new SqliteConnection($"Data Source={MirrorLocation.InFolder(_root).DatabasePath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT modified_utc_ticks FROM pl_node WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", nodeId);
        return (long)cmd.ExecuteScalar()!;
    }
}
