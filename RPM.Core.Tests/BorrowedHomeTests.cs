using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// Home on a collection somebody else has shared: its quick selection and its Made for you, which are
/// made from what the collection holds because nothing on it has been played (the TV app's rules, its
/// HomeShelf's borrowed branches).
///
/// <para>Tested here because both are arithmetic over lists, and a selection that quietly stopped
/// mixing, or a row that stopped changing between launches, would still look like a home screen.</para>
/// </summary>
public class BorrowedHomeTests
{
    /// <summary>Forty records of three songs each, every song by the record's own act.</summary>
    private static readonly string[] Shelf = [.. Enumerable.Range(1, 40).Select(a => $"Act {a}/Record {a}")];

    private static IReadOnlyList<BorrowedSong> SongsOf(string folder) =>
        [.. Enumerable.Range(1, 3).Select(t => new BorrowedSong(
            $"{folder}/{t:00}.flac", $"{folder}#{t}", folder, folder.Split('/')[0], $"Song {t}"))];

    private static IReadOnlyList<QuickPick> Quick(
        IReadOnlyList<BorrowedSong>? favourites = null,
        IReadOnlyCollection<string>? shown = null,
        IReadOnlyList<string>? moods = null,
        IReadOnlyList<string>? lists = null,
        IReadOnlyList<string>? stations = null,
        int draw = 7) =>
        BorrowedHome.Quick(
            favourites ?? [], Shelf, shown ?? [], SongsOf, moods ?? [], lists ?? [], stations ?? [], draw);

    // ── the quick selection ──

    [Fact]
    public void A_shelf_with_nothing_played_fills_the_quick_selection()
    {
        var picks = Quick(lists: ["1", "2", "3", "4", "5", "6"]);

        Assert.Equal(QuickPicks.Count, picks.Count);
        Assert.Contains(picks, p => p.Kind == QuickPickKind.Song);
        Assert.Contains(picks, p => p.Kind == QuickPickKind.Album);
        Assert.Contains(picks, p => p.Kind == QuickPickKind.Playlist);
        Assert.DoesNotContain(picks, p => p.Kind == QuickPickKind.Radio);

        // Dealt in turn, as the selection on one's own shelf is.
        Assert.Equal(
            [QuickPickKind.Song, QuickPickKind.Album, QuickPickKind.Playlist, QuickPickKind.Song],
            picks.Take(4).Select(p => p.Kind));
    }

    [Fact]
    public void The_owners_favourite_songs_come_first_and_their_records_lead_the_records()
    {
        var loved = SongsOf("Act 30/Record 30")[1];
        var picks = Quick(favourites: [loved]);

        Assert.Equal(new QuickPick(QuickPickKind.Song, loved.Path), picks[0]);
        Assert.Equal(new QuickPick(QuickPickKind.Album, "Act 30/Record 30"), picks.First(p => p.Kind == QuickPickKind.Album));
    }

    [Fact]
    public void The_records_the_rows_below_show_come_last()
    {
        var shown = Shelf.Take(35).ToList();
        var albums = Quick(shown: shown).Where(p => p.Kind == QuickPickKind.Album).Select(p => p.Key).ToList();

        Assert.All(albums.Take(5), folder => Assert.DoesNotContain(folder, shown));
    }

    [Fact]
    public void A_song_already_offered_is_not_offered_again_from_its_record()
    {
        // The same title by the same act, filed on a best-of as well.
        var loved = new BorrowedSong("Best Of/01.flac", "best#1", "Best Of", "Act 1", "Song 1");
        var songs = Shelf.Select(SongsOf).SelectMany(s => s).Append(loved).ToList();

        var picks = BorrowedHome.Quick(
            [loved], [.. Shelf, "Best Of"], [], f => [.. songs.Where(s => s.Folder == f)], [], [], [], draw: 3);

        var offered = picks.Where(p => p.Kind == QuickPickKind.Song)
                           .Select(p => songs.First(s => s.Path == p.Key))
                           .Select(s => (s.Artist, s.Title))
                           .ToList();
        Assert.Equal(offered.Count, offered.Distinct().Count());
    }

    [Fact]
    public void Made_for_you_leads_the_lists_and_the_rest_follow()
    {
        var picks = Quick(moods: ["m1", "m2"], lists: ["m1", "m2", "p1"]);

        Assert.Equal(
            ["m1", "m2", "p1"],
            picks.Where(p => p.Kind == QuickPickKind.Playlist).Select(p => p.Key));
    }

    [Fact]
    public void The_stations_heard_most_are_dealt_in_as_the_fourth_kind()
    {
        var picks = Quick(lists: ["1"], stations: ["r1", "r2"]);

        Assert.Equal(QuickPickKind.Station, picks[3].Kind);
        Assert.Equal("r1", picks[3].Key);
    }

    [Fact]
    public void One_launch_keeps_its_selection_and_launches_differ()
    {
        var once = Quick(draw: 11);
        var again = Quick(draw: 11);
        var others = Enumerable.Range(1, 6).Select(d => Quick(draw: d)).ToList();

        Assert.Equal(once, again);
        Assert.True(others.Select(p => string.Join(",", p)).Distinct().Count() > 1);
    }

    // ── Made for you ──

    private static readonly string[] Genres = ["", "Jazz", "Rock", "Pop"];

    /// <summary>Each of the five moods whole and cut to three genres, as the owner's computer sends
    /// them, plus two decades, a classical list and two playlists somebody made.</summary>
    private static IReadOnlyList<BorrowedList> Lists()
    {
        var lists = new List<BorrowedList>();
        var id = 0;
        foreach (var mood in BorrowedHome.Moods)
            foreach (var _ in Genres)
                lists.Add(new BorrowedList((--id).ToString(), mood));

        lists.Add(new BorrowedList("-90", SmartPlaylistKind.Decade));
        lists.Add(new BorrowedList("-91", SmartPlaylistKind.Decade));
        lists.Add(new BorrowedList("-92", SmartPlaylistKind.Classical));
        lists.Add(new BorrowedList("5", null));
        lists.Add(new BorrowedList("6", null));
        return lists;
    }

    [Fact]
    public void Made_for_you_opens_on_one_list_of_each_mood()
    {
        var lists = Lists();
        var kindOf = lists.ToDictionary(l => l.Key, l => l.Generated);

        for (var draw = 1; draw <= 8; draw++)
        {
            var row = BorrowedHome.MadeForYou(lists, draw);

            Assert.Equal(HomeShelf.RowLength, row.Count);
            Assert.Equal(BorrowedHome.Moods.ToHashSet(), row.Take(5).Select(k => kindOf[k]!.Value).ToHashSet());
            Assert.DoesNotContain("5", row);
            Assert.DoesNotContain("6", row);
        }
    }

    [Fact]
    public void Made_for_you_is_drawn_again_at_each_launch_and_kept_within_one()
    {
        var lists = Lists();

        Assert.Equal(BorrowedHome.MadeForYou(lists, 4), BorrowedHome.MadeForYou(lists, 4));
        Assert.True(Enumerable.Range(1, 8)
            .Select(d => string.Join(",", BorrowedHome.MadeForYou(lists, d)))
            .Distinct()
            .Count() > 1);
    }

    [Fact]
    public void The_lists_of_other_kinds_follow_the_moods()
    {
        var lists = Lists();
        var row = BorrowedHome.MadeForYou(lists, 2, count: 30);

        Assert.Equal(23, row.Count);
        Assert.Equal(["-90", "-91", "-92"], row.Skip(20).Order());
    }

    [Fact]
    public void A_shelf_whose_owner_sent_no_generated_lists_has_no_made_for_you()
    {
        Assert.Empty(BorrowedHome.MadeForYou([new BorrowedList("1", null)], 1));
    }
}
