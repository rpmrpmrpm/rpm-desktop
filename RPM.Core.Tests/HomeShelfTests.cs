using RPM.Core.Library;
using RPM.Core.Metadata;

namespace RPM.Core.Tests;

/// <summary>
/// The home view's four rows. Each row is a different selection from the same shelf.
///
/// <para>Tested here because the rows are pure logic and the view is not: the contents of that
/// screen are decided by <see cref="HomeShelf"/>, and a row that selected the wrong albums would
/// still look like a normal row.</para>
/// </summary>
public class HomeShelfTests
{
    private static LibraryDatabase.AlbumFactsRow Album(
        string folder, long added = 0, int plays = 0, long lastPlayed = 0) =>
        new(folder, folder, "Somebody", added, plays, lastPlayed);

    private static AlbumTraits Traits(string folder, string artist, string? genre = null, int year = 0) =>
        new(folder, folder, artist, genre is null ? [] : [genre], year);

    // ── what has been played recently ──

    [Fact]
    public void Recently_listened_is_the_newest_listen_first()
    {
        var shelf = new[]
        {
            Album("old", lastPlayed: 10),
            Album("newest", lastPlayed: 30),
            Album("middle", lastPlayed: 20),
        };

        Assert.Equal(["newest", "middle", "old"], HomeShelf.Listened(shelf, 10));
    }

    /// <summary>An album nobody has played is not a recent listen. The row does not fall back to the
    /// rest of the collection, because that would label unheard music as recently played.</summary>
    [Fact]
    public void A_record_never_played_is_not_a_recent_listen()
        => Assert.Empty(HomeShelf.Listened([Album("never"), Album("also-never", added: 99)], 10));

    /// <summary>Two reads of an unchanged shelf give the same row. Ties are broken by folder rather
    /// than by the order the query returned, so the row does not reorder when the user has done
    /// nothing.</summary>
    [Fact]
    public void An_unchanged_shelf_gives_the_same_row_twice()
    {
        var shelf = new[]
        {
            Album("b", lastPlayed: 5), Album("a", lastPlayed: 5), Album("c", lastPlayed: 5),
        };

        Assert.Equal(["a", "b", "c"], HomeShelf.Listened(shelf, 10));
        Assert.Equal(HomeShelf.Listened(shelf, 10), HomeShelf.Listened([.. shelf.Reverse()], 10));
    }

    // ── what has been played most ──

    /// <summary>Counted in TRACKS played: an album played all the way through ranks above one song
    /// played many times. That is what "most played" means for a shelf of albums.</summary>
    [Fact]
    public void Most_played_counts_the_tracks_and_not_the_records()
    {
        var shelf = new[] { Album("one-song-on-repeat", plays: 8), Album("played-through", plays: 12) };

        Assert.Equal(["played-through", "one-song-on-repeat"], HomeShelf.Played(shelf, 10));
    }

    [Fact]
    public void A_record_never_played_is_not_most_played()
        => Assert.Empty(HomeShelf.Played([Album("never", added: 99)], 10));

    // ── what was added last ──

    /// <summary>The only row a newly opened collection can fill, so it does not use plays at
    /// all.</summary>
    [Fact]
    public void Recently_added_answers_for_a_collection_nobody_has_played()
    {
        var shelf = new[] { Album("first", added: 1), Album("last", added: 9) };

        Assert.Equal(["last", "first"], HomeShelf.Added(shelf, 10));
    }

    [Fact]
    public void A_row_is_no_longer_than_it_is_asked_for()
    {
        var shelf = Enumerable.Range(0, 50).Select(i => Album($"a{i}", added: i)).ToList();

        Assert.Equal(5, HomeShelf.Added(shelf, 5).Count);
        Assert.Empty(HomeShelf.Added(shelf, 0));
    }

    // ── what to play next ──

    /// <summary>A suggestion is an album the user has NOT just played. The recently played row is
    /// directly above this one on the same screen, so the same card must not appear in both.</summary>
    [Fact]
    public void Nothing_suggested_is_already_in_the_row_above_it()
    {
        var traits = new[]
        {
            Traits("played/one", "A", "Rock"),
            Traits("played/two", "B", "Rock"),
            Traits("unplayed", "C", "Rock"),
        };

        var row = HomeShelf.Suggested(
            [traits[0]], traits, ["played/one", "played/two"], 10, ADraw);

        Assert.Equal(["unplayed"], row);
    }

    /// <summary>Every seed gets a card near the front, rather than the first seed filling the row.
    /// Users look at the front of a row, so the front should reflect all of their recent
    /// listening.</summary>
    [Fact]
    public void The_front_of_the_row_answers_to_every_seed()
    {
        var traits = new[]
        {
            Traits("rock/seed", "Rock Act", "Rock"),
            Traits("rock/a", "Rock Other", "Rock"),
            Traits("rock/b", "Rock Third", "Rock"),
            Traits("jazz/seed", "Jazz Act", "Jazz"),
            Traits("jazz/a", "Jazz Other", "Jazz"),
            Traits("jazz/b", "Jazz Third", "Jazz"),
        };

        var row = HomeShelf.Suggested(
            [traits[0], traits[3]], traits, ["rock/seed", "jazz/seed"], 4, ADraw);

        // The first two cards are one from each seed, whatever each seed would give on its own.
        Assert.Equal(2, row.Take(2).Select(f => f.Split('/')[0]).Distinct().Count());
    }

    /// <summary>A listener who has played nothing has no listening to base suggestions on, so the
    /// row is empty rather than the collection in some order.</summary>
    [Fact]
    public void Nothing_played_suggests_nothing()
        => Assert.Empty(HomeShelf.Suggested([], [Traits("a", "A")], [], 10, ADraw));

    /// <summary>Every new collection is too small to fill the row. The round-robin must stop rather
    /// than loop looking for a card that does not exist.</summary>
    [Fact]
    public void A_shelf_too_small_to_fill_the_row_still_returns()
    {
        var traits = new[] { Traits("seed", "A", "Rock"), Traits("other", "B", "Rock") };

        var row = HomeShelf.Suggested([traits[0]], traits, ["seed"], 20, ADraw);

        Assert.Equal(["other"], row);
    }

    /// <summary>A fixed draw for the tests that are about the ROW rather than about the draw. Any
    /// number works, as long as it is the same on every run.</summary>
    private const int ADraw = 1;

    /// <summary>The same draw gives the same row, so a screen that is read again while the user is
    /// looking at it does not reorder.</summary>
    [Fact]
    public void The_same_draw_gives_the_same_row()
    {
        var shelf = Shelf(60);

        var once = HomeShelf.Suggested([shelf[0]], shelf, [], 10, 4242);
        var again = HomeShelf.Suggested([shelf[0]], shelf, [], 10, 4242);

        Assert.Equal(once, again);
    }

    /// <summary>A different draw gives a different row. The seeds do not change until something else
    /// is played, so without the draw the row would always show the same albums.</summary>
    [Fact]
    public void Another_draw_gives_another_row()
    {
        var shelf = Shelf(60);

        var rows = new[] { 1, 2, 3, 4, 5 }
            .Select(draw => string.Join(",", HomeShelf.Suggested([shelf[0]], shelf, [], 10, draw)))
            .Distinct()
            .Count();

        Assert.True(rows >= 4, $"five draws over sixty records gave {rows} different rows");
    }

    /// <summary>Every album in the row is one the ranking offered for that seed. The draw shuffles the
    /// pool and never picks from outside it.</summary>
    [Fact]
    public void The_draw_stays_inside_what_the_ranking_offered()
    {
        var shelf = Shelf(60);
        var offered = RPM.Core.Metadata.SimilarAlbums
            .Pick(shelf[0], shelf, 10 * HomeShelf.Pool)
            .Select(s => s.Folder)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in HomeShelf.Suggested([shelf[0]], shelf, [], 10, 7))
            Assert.Contains(folder, offered);
    }

    /// <summary>A shelf large enough to draw from: one artist per album so nothing is merged, and two
    /// genres so the ranking has something to compare.</summary>
    private static AlbumTraits[] Shelf(int count) =>
        [.. Enumerable.Range(0, count)
                      .Select(i => Traits($"rec{i:D2}", $"Act {i}", i % 2 == 0 ? "Rock" : "Jazz"))];

    // ── what the suggestions are based on ──

    /// <summary>A collection with recent listening is based on that listening.</summary>
    [Fact]
    public void A_shelf_that_has_been_played_is_read_off_what_was_played()
        => Assert.Equal(["played"], HomeShelf.Basis(["played"], ["arrived"]));

    /// <summary>A shelf borrowed from another computer has albums but no listening: the listing says
    /// what somebody HAS, never what they played. The newest albums are used instead. Without them
    /// the suggestions have no seeds and the screen shows only one row.</summary>
    [Fact]
    public void A_shelf_with_no_listening_is_read_off_what_arrived_last()
        => Assert.Equal(["arrived"], HomeShelf.Basis([], ["arrived"]));

    /// <summary>A collection with neither (a first run with an empty folder) has no basis.</summary>
    [Fact]
    public void A_shelf_with_neither_is_read_off_nothing()
        => Assert.Empty(HomeShelf.Basis([], []));

    /// <summary>No card appears twice, however many seeds suggest it. Two similar albums played
    /// recently can suggest the same third album, and it is shown once.</summary>
    [Fact]
    public void A_record_is_suggested_once_however_many_seeds_want_it()
    {
        var traits = new[]
        {
            Traits("seed/one", "A", "Rock"),
            Traits("seed/two", "B", "Rock"),
            Traits("third", "C", "Rock"),
        };

        var row = HomeShelf.Suggested(
            [traits[0], traits[1]], traits, ["seed/one", "seed/two"], 10, ADraw);

        Assert.Equal(row.Distinct().Count(), row.Count);
    }

    // ── the row based on what the listener plays ──

    private static PlayedArtist Plays(string name, int plays) =>
        new(name, new ListeningTally(plays, 0, plays * 200.0), plays);

    private static GenreShare Kind(string genre, double share) =>
        new(genre, new ListeningTally((int)(share * 100), 0, share * 1000), share);

    /// <summary>The main rule of the row: an album by an artist the listener plays is offered, and an
    /// album by an artist they never played, in a genre they never play, is not. The row does not fall
    /// back to the rest of the shelf, because then it would be random under a personal heading.
    /// </summary>
    [Fact]
    public void The_taste_row_offers_their_acts_and_not_strangers()
    {
        var shelf = new[]
        {
            Traits("theirs", "Massive Attack", "Electronic"),
            Traits("stranger", "Somebody Else", "Country"),
        };

        Assert.Equal(
            ["theirs"],
            HomeShelf.FromTaste([Plays("Massive Attack", 40)], [Kind("Electronic", 0.8)], shelf, [], [], 10, ADraw));
    }

    /// <summary>A genre they play is enough on its own. Most albums on a shelf are by artists nobody
    /// has played yet, and a row that offered only the artists already in the log would be the same
    /// as the most played row.</summary>
    [Fact]
    public void A_kind_of_music_they_play_is_enough_on_its_own()
    {
        var shelf = new[]
        {
            Traits("unheard/jazz", "An Act Nobody Has Played", "Jazz"),
            Traits("unheard/country", "Another One", "Country"),
        };

        Assert.Equal(
            ["unheard/jazz"],
            HomeShelf.FromTaste([Plays("Someone", 10)], [Kind("Jazz", 0.9)], shelf, [], [], 10, ADraw));
    }

    /// <summary>Albums they have not played get into the row first. This row is for albums they own
    /// and have not played; the played ones are two rows down under "most played".
    ///
    /// <para>The test asks for ONE album from a shelf of eight, so the pool is three: the three
    /// highest scores. The order inside the row comes from the draw and means nothing (see
    /// <see cref="HomeShelf.Pool"/>), so the test checks only which albums the scoring let through.
    /// </para></summary>
    [Fact]
    public void A_record_they_have_not_played_gets_into_the_row_first()
    {
        var shelf = Enumerable.Range(0, 8)
            .Select(i => Traits($"rec{i}", "Massive Attack", "Electronic"))
            .ToArray();

        var heard = shelf.Take(4).Select(a => a.Folder).ToList();

        var row = HomeShelf.FromTaste(
            [Plays("Massive Attack", 40)], [Kind("Electronic", 0.8)], shelf, heard, [], 1, ADraw);

        Assert.DoesNotContain(Assert.Single(row), heard);
    }

    /// <summary>A played album is still offered, only ranked lower. If it were removed, a listener who
    /// has played their whole collection would see an empty row, and an album played once a year ago
    /// is a reasonable suggestion.</summary>
    [Fact]
    public void A_record_they_have_played_is_outranked_rather_than_struck_out()
    {
        var shelf = new[] { Traits("played", "Massive Attack", "Electronic") };

        Assert.Equal(
            ["played"],
            HomeShelf.FromTaste(
                [Plays("Massive Attack", 40)], [Kind("Electronic", 0.8)], shelf, ["played"], [], 10, ADraw));
    }

    /// <summary>Nothing that is already on the screen above it. The two recommendation rows are one
    /// under the other and would otherwise share about half of their albums.</summary>
    [Fact]
    public void The_taste_row_does_not_repeat_what_is_already_on_screen()
    {
        var shelf = new[]
        {
            Traits("above", "Massive Attack", "Electronic"),
            Traits("fresh", "Massive Attack", "Electronic"),
        };

        Assert.Equal(
            ["fresh"],
            HomeShelf.FromTaste(
                [Plays("Massive Attack", 40)], [Kind("Electronic", 0.8)], shelf, [], ["above"], 10, ADraw));
    }

    /// <summary>The artist name is normalised the same way the album ranking does it, so the log's
    /// spelling and the album's tag do not have to match exactly.</summary>
    [Fact]
    public void The_act_is_matched_however_it_is_spelled()
    {
        var shelf = new[] { Traits("theirs", "The xx") };

        Assert.Equal(
            ["theirs"],
            HomeShelf.FromTaste([Plays("xx", 40)], [], shelf, [], [], 10, ADraw));
    }

    /// <summary>A listener who has played nothing has no listening to base the row on, so the row is
    /// empty rather than the collection in an arbitrary order.</summary>
    [Fact]
    public void A_listener_with_no_listening_gets_no_taste_row()
        => Assert.Empty(HomeShelf.FromTaste([], [], Shelf(20), [], [], 10, ADraw));

    /// <summary>The same draw gives the same row, so a screen that is read again while the user is
    /// looking at it does not reorder.</summary>
    [Fact]
    public void The_same_draw_gives_the_same_taste_row()
    {
        var shelf = Shelf(60);
        var acts = shelf.Select(a => Plays(a.Artist, 10)).ToList();

        Assert.Equal(
            HomeShelf.FromTaste(acts, [Kind("Rock", 0.6)], shelf, [], [], 10, 4),
            HomeShelf.FromTaste(acts, [Kind("Rock", 0.6)], shelf, [], [], 10, 4));
    }

    /// <summary>Different draws give different rows. This is the reason the draw exists.
    /// </summary>
    [Fact]
    public void Different_draws_give_different_taste_rows()
    {
        var shelf = Shelf(60);
        var acts = shelf.Select(a => Plays(a.Artist, 10)).ToList();

        var rows = Enumerable.Range(1, 5)
            .Select(draw => string.Join("|", HomeShelf.FromTaste(acts, [Kind("Rock", 0.6)], shelf, [], [], 10, draw)))
            .Distinct()
            .Count();

        Assert.True(rows >= 4, $"five draws gave {rows} different rows");
    }

    // ── what the banner picks ──

    /// <summary>The front of a ranked row does not change, so without a draw the banner would show
    /// the same album on every reload. Different draws pick different albums.</summary>
    [Fact]
    public void Different_draws_reach_for_different_records()
    {
        var ranked = Enumerable.Range(0, 20).Select(i => $"rec{i:D2}").ToList();

        var fronts = Enumerable.Range(1, 6)
            .Select(draw => HomeShelf.Varied(ranked, draw, "banner", HomeShelf.BannerReach)[0])
            .Distinct()
            .Count();

        Assert.True(fronts >= 4, $"six draws reached for {fronts} different records");
    }

    /// <summary>The same draw picks the same album, so the banner does not reorder while the user is
    /// looking at it.</summary>
    [Fact]
    public void The_same_draw_reaches_for_the_same_record()
    {
        var ranked = Enumerable.Range(0, 20).Select(i => $"rec{i:D2}").ToList();

        Assert.Equal(
            HomeShelf.Varied(ranked, 5, "banner", HomeShelf.BannerReach),
            HomeShelf.Varied(ranked, 5, "banner", HomeShelf.BannerReach));
    }

    /// <summary>The draw picks only from the front of the row. "Most played" is true of the third
    /// most played album but not of the hundredth, so this keeps the heading correct.
    /// </summary>
    [Fact]
    public void It_only_reaches_into_the_front_of_the_row()
    {
        var ranked = Enumerable.Range(0, 40).Select(i => $"rec{i:D2}").ToList();
        var front = ranked.Take(HomeShelf.BannerReach).ToHashSet();

        var drawn = HomeShelf.Varied(ranked, ADraw, "banner", HomeShelf.BannerReach);

        Assert.Subset(front, drawn.Take(HomeShelf.BannerReach).ToHashSet());
    }

    /// <summary>The rest of the row is kept in its own order. The banner skips any album another card
    /// has already used, so it needs more items after the front.</summary>
    [Fact]
    public void The_rest_of_the_row_is_left_alone()
    {
        var ranked = Enumerable.Range(0, 20).Select(i => $"rec{i:D2}").ToList();

        var drawn = HomeShelf.Varied(ranked, ADraw, "banner", HomeShelf.BannerReach);

        Assert.Equal(ranked.Count, drawn.Count);
        Assert.Equal(ranked.Skip(HomeShelf.BannerReach), drawn.Skip(HomeShelf.BannerReach));
    }

    /// <summary>A shelf with one played album has one item, and a draw over it is not an error. This
    /// is the normal case soon after a new install.</summary>
    [Fact]
    public void A_row_of_one_is_the_row_of_one()
        => Assert.Equal(["only"], HomeShelf.Varied(["only"], ADraw, "banner", HomeShelf.BannerReach));
}
