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

    // ── deleted elsewhere ──

    private const long ListedAt = 1_800_000_000_000;
    private static readonly long LongBefore = ListedAt - (long)TimeSpan.FromHours(1).TotalMilliseconds;

    private static Library.PlaylistCloudState Published(long node, long stamp) =>
        new(node, stamp, PlaylistBase.Unknown, SentMeta: "x");

    [Fact]
    public void A_playlist_published_from_here_and_missing_from_the_listing_was_deleted_elsewhere()
    {
        var gone = CloudPlaylistIntake.GoneElsewhere(
            [Playlist(Known, "a")],
            new Dictionary<string, Library.PlaylistCloudState>
            {
                [Known] = Published(1, LongBefore),
                [Deleted] = Published(2, LongBefore),
            },
            synced: new HashSet<long> { 1, 2 },
            ListedAt);

        Assert.Equal([2L], gone);
    }

    /// <summary>Published while the listing was on its way: not in it yet, and not gone.</summary>
    [Fact]
    public void A_playlist_published_just_before_the_listing_is_not_judged_by_it()
    {
        var gone = CloudPlaylistIntake.GoneElsewhere(
            [Playlist(Known, "a")],
            new Dictionary<string, Library.PlaylistCloudState> { [Deleted] = Published(2, ListedAt - 1_000) },
            synced: new HashSet<long> { 2 },
            ListedAt);

        Assert.Empty(gone);
    }

    [Fact]
    public void A_playlist_never_stamped_or_no_longer_synced_is_not_judged()
    {
        var gone = CloudPlaylistIntake.GoneElsewhere(
            [Playlist(Known, "a")],
            new Dictionary<string, Library.PlaylistCloudState>
            {
                [Deleted] = Published(2, 0),
                [Made] = Published(3, LongBefore),
            },
            synced: new HashSet<long> { 2 },
            ListedAt);

        Assert.Empty(gone);
    }

    /// <summary>An empty answer is also what a fault on the store's side gives. Letting every playlist go
    /// on it would stop them all syncing.</summary>
    [Fact]
    public void An_empty_listing_decides_nothing()
    {
        var gone = CloudPlaylistIntake.GoneElsewhere(
            [],
            new Dictionary<string, Library.PlaylistCloudState> { [Deleted] = Published(2, LongBefore) },
            synced: new HashSet<long> { 2 },
            ListedAt);

        Assert.Empty(gone);
    }

    // ── renamed elsewhere ──

    private static readonly IReadOnlyList<string> Mixes = ["Mixes"];

    private static string Meta(string name, IReadOnlyList<string> folder) =>
        Library.PlaylistCloudState.MetaOf(name, folder);

    private static CloudPlaylist Named(string name, IReadOnlyList<string> folder) =>
        new(Known, name, ["a"], folder);

    [Fact]
    public void A_rename_made_elsewhere_is_taken_when_nothing_changed_here()
    {
        var name = CloudPlaylistIntake.NameToTake(
            Meta("Sunday", Mixes), Named("Sunday morning", Mixes), "Sunday", Mixes);

        Assert.Equal("Sunday morning", name);
    }

    /// <summary>Renamed on both sides: this machine keeps its own, and its push sends it.</summary>
    [Fact]
    public void A_rename_here_wins_over_a_rename_elsewhere()
    {
        var name = CloudPlaylistIntake.NameToTake(
            Meta("Sunday", Mixes), Named("Sunday morning", Mixes), "Sunday night", Mixes);

        Assert.Null(name);
    }

    [Fact]
    public void The_cloud_still_holding_what_was_agreed_gives_nothing_to_take()
    {
        Assert.Null(CloudPlaylistIntake.NameToTake(
            Meta("Sunday", Mixes), Named("Sunday", Mixes), "Sunday", Mixes));
    }

    /// <summary>A move to another folder made on another computer is not a rename, and folders are not
    /// taken in.</summary>
    [Fact]
    public void A_folder_move_elsewhere_is_not_a_rename()
    {
        Assert.Null(CloudPlaylistIntake.NameToTake(
            Meta("Sunday", Mixes), Named("Sunday", ["Elsewhere"]), "Sunday", Mixes));
    }

    [Fact]
    public void Nothing_recorded_means_nothing_is_taken()
    {
        Assert.Null(CloudPlaylistIntake.NameToTake(null, Named("Sunday morning", Mixes), "Sunday", Mixes));
    }
}
