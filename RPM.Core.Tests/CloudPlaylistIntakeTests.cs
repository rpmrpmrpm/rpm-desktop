using RPM.Core.Cloud;

namespace RPM.Core.Tests;

/// <summary>
/// Which playlists in the cloud a computer takes into its own tree.
///
/// <para>Playlists sync both ways (a phone can write one). A cloud playlist this machine does not
/// have may be one somebody just wrote, or one this machine deleted whose cloud copy is not gone
/// yet. Every test here is about telling those two apart, and about not publishing a shorter
/// playlist than the one the user made.</para>
/// </summary>
public class CloudPlaylistIntakeTests
{
    private const string Made = "11111111-1111-4111-8111-111111111111";
    private const string Known = "22222222-2222-4222-8222-222222222222";
    private const string Deleted = "33333333-3333-4333-8333-333333333333";

    private static CloudPlaylist Playlist(string id, params string[] trackIds) =>
        new(id, "On the train", trackIds, Folder: null);

    private static IReadOnlyList<PlaylistIntakeReading> Read(
        IReadOnlyList<CloudPlaylist> theirs,
        IReadOnlySet<string>? claimed = null,
        IReadOnlySet<string>? letGo = null,
        params string[] have)
        => CloudPlaylistIntake.Read(
            theirs,
            claimed ?? new HashSet<string>(),
            letGo ?? new HashSet<string>(),
            id => have.Contains(id));

    [Fact]
    public void A_playlist_nobody_here_claims_is_taken_in()
    {
        var reading = Read([Playlist(Made, "a", "b")], have: ["a", "b"]).Single();

        Assert.Equal(PlaylistIntake.Take, reading.Verdict);
        Assert.Equal(0, reading.Missing);
    }

    [Fact]
    public void A_playlist_with_no_songs_at_all_is_still_a_playlist()
        => Assert.Equal(PlaylistIntake.Take, Read([Playlist(Made)]).Single().Verdict);

    [Fact]
    public void A_playlist_a_node_here_already_stands_for_is_left_alone()
    {
        var reading = Read([Playlist(Known, "a")], claimed: new HashSet<string> { Known }).Single();

        Assert.Equal(PlaylistIntake.AlreadyHere, reading.Verdict);
    }

    /// <summary>
    /// The main failure this guards against. Deleting a synced playlist deletes the row that stored
    /// its cloud id, so the next listing shows a playlist nothing here claims. Taking that in would
    /// bring the deleted playlist back on every run, for ever.
    /// </summary>
    [Fact]
    public void A_playlist_this_library_deleted_is_never_taken_back_in()
    {
        var reading = Read([Playlist(Deleted, "a")], letGo: new HashSet<string> { Deleted }, have: ["a"])
            .Single();

        Assert.Equal(PlaylistIntake.LetGo, reading.Verdict);
    }

    /// <summary>The "let go" mark is read before the songs, so the contents of a playlist that is not
    /// coming back are not read to find that out.</summary>
    [Fact]
    public void Letting_go_beats_missing_songs()
    {
        var reading = Read([Playlist(Deleted, "a")], letGo: new HashSet<string> { Deleted }).Single();

        Assert.Equal(PlaylistIntake.LetGo, reading.Verdict);
        Assert.Equal(0, reading.Missing);
    }

    /// <summary>
    /// The other failure. A playlist taken in is pushed back up from here, and the push writes the
    /// order this machine can play. Taking in a list that names a song this machine does not have
    /// would silently publish a shorter list to every other head the user owns.
    /// </summary>
    [Fact]
    public void A_playlist_naming_songs_this_library_has_not_got_waits()
    {
        var reading = Read([Playlist(Made, "a", "b", "c")], have: ["a"]).Single();

        Assert.Equal(PlaylistIntake.Incomplete, reading.Verdict);
        Assert.Equal(2, reading.Missing);
    }

    /// <summary>A list that plays one song twice is short by two entries when that song is away, and
    /// the count is what the log line says.</summary>
    [Fact]
    public void A_song_named_twice_is_missing_twice()
        => Assert.Equal(2, Read([Playlist(Made, "a", "a")]).Single().Missing);

    [Fact]
    public void A_claim_is_read_before_the_songs_are()
    {
        var reading = Read([Playlist(Known, "a", "b")], claimed: new HashSet<string> { Known }).Single();

        Assert.Equal(PlaylistIntake.AlreadyHere, reading.Verdict);
        Assert.Equal(0, reading.Missing);
    }

    [Fact]
    public void Every_playlist_in_the_listing_is_answered_for()
    {
        var readings = Read(
            [Playlist(Made, "a"), Playlist(Known, "a"), Playlist(Deleted, "a")],
            claimed: new HashSet<string> { Known },
            letGo: new HashSet<string> { Deleted },
            have: ["a"]);

        Assert.Equal(3, readings.Count);
        Assert.Equal(PlaylistIntake.Take, readings[0].Verdict);
        Assert.Equal(PlaylistIntake.AlreadyHere, readings[1].Verdict);
        Assert.Equal(PlaylistIntake.LetGo, readings[2].Verdict);
    }

    /// <summary>A playlist stored without a folder field means the top of the tree, not a null that
    /// code further on does not check for.</summary>
    [Fact]
    public void A_playlist_written_before_folders_reads_as_the_top_of_the_tree()
    {
        var old = new CloudPlaylist("id", "Old", ["a"], Folder: null);

        Assert.Empty(old.FolderChain);
    }

    [Fact]
    public void A_folder_chain_is_carried_whole()
    {
        var filed = new CloudPlaylist("id", "New", ["a"], Folder: ["Rock", "90s"]);

        Assert.Equal(["Rock", "90s"], filed.FolderChain);
    }
}
