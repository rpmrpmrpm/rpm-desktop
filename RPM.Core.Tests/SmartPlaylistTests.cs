using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The generated playlists: the lists the library writes for itself out of what it already knows.
/// Favourites are tested beside the flag they are made of (<see cref="FavoriteTests"/>) and the log
/// they read beside its own writes (<see cref="ListenLogTests"/>); what is here is the catalogue and
/// each kind's rule.
/// </summary>
public class SmartPlaylistTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    /// <summary>A fixed "now", so every window in every rule means the same stretch in every run.</summary>
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 14, 0, 0, TimeSpan.Zero);

    public SmartPlaylistTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmsmart-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private void Add(string relativePath, double seconds = 200, long addedUtcTicks = 0,
                     string? genre = null)
    {
        var slash = relativePath.LastIndexOf('/');
        _db.Upsert(new LibraryTrack
        {
            RelativePath = relativePath,
            Folder = slash < 0 ? "" : relativePath[..slash],
            FileName = slash < 0 ? relativePath : relativePath[(slash + 1)..],
            Extension = Path.GetExtension(relativePath),
            SizeBytes = 5_000_000,
            ModifiedUtcTicks = addedUtcTicks == 0 ? 1 : addedUtcTicks,
            DurationSeconds = seconds,
            Title = Path.GetFileNameWithoutExtension(relativePath),
            Genre = genre,
        });
    }

    /// <summary>Plays a song right through, <paramref name="times"/> times, ending at
    /// <paramref name="when"/>.</summary>
    private void Play(string relativePath, int times, DateTimeOffset when)
    {
        for (var i = 0; i < times; i++)
            _db.NoteListen(ListenEntry.At(when, relativePath, heardSeconds: 200, lengthSeconds: 200)!);
    }

    /// <summary>Starts a song and gives up on it, a listen the log keeps and the totals ignore.</summary>
    private void Skip(string relativePath, int times, DateTimeOffset when)
    {
        for (var i = 0; i < times; i++)
            _db.NoteListen(ListenEntry.At(when, relativePath, heardSeconds: 15, lengthSeconds: 200)!);
    }

    private IEnumerable<string> List(SmartPlaylistKind kind) =>
        _db.QuerySmartPlaylist(SmartPlaylistId.WholeShelf(kind), Now.UtcTicks, advance: true).Select(t => t.RelativePath);

    private int Count(SmartPlaylistKind kind) => _db.SmartPlaylistTotals(SmartPlaylistId.WholeShelf(kind), Now.UtcTicks).Tracks;

    private static DateTimeOffset DaysAgo(int days) => Now.AddDays(-days);

    // ── the catalogue ──

    [Fact]
    public void Every_kind_is_listed_in_the_catalogue()
    {
        // The pinned folder's list plus the ones that live elsewhere. Together they still have to be
        // every kind: that is what catches a kind added to the enum and then listed nowhere. But a
        // decade belongs in the decades folder, and a row called "Decade" with no decade on it is not
        // a list anybody can open. See SmartPlaylists.InTheirOwnFolder.
        Assert.Equal(
            Enum.GetValues<SmartPlaylistKind>().OrderBy(k => k),
            SmartPlaylists.All.Concat(SmartPlaylists.InTheirOwnFolder).OrderBy(k => k));

        Assert.Empty(SmartPlaylists.All.Intersect(SmartPlaylists.InTheirOwnFolder));
    }

    [Fact]
    public void The_favorites_come_first()
    {
        Assert.Equal(SmartPlaylistKind.FavoriteTracks, SmartPlaylists.All[0]);
    }

    /// <summary>Every kind has a rule, a size worth showing at, and an answer on an empty library. The
    /// switches throw instead of falling back, so a kind added without all three fails here and is
    /// not quietly listed as somebody's favourites under a different name.</summary>
    [Fact]
    public void Every_kind_answers_on_an_empty_library()
    {
        foreach (var kind in SmartPlaylists.All)
        {
            Assert.True(SmartPlaylists.LeastForWholeShelf(kind) >= 1);
            Assert.True(SmartPlaylists.LeastForNarrowedRow(kind) >= 1);

            // A narrowed row is never held to MORE than the whole shelf: it is the smaller question.
            Assert.True(
                SmartPlaylists.LeastForNarrowedRow(kind) <= SmartPlaylists.LeastForWholeShelf(kind));
            Assert.Empty(_db.QuerySmartPlaylist(SmartPlaylistId.WholeShelf(kind), Now.UtcTicks, advance: true));
            Assert.Equal((0, 0d, false), _db.SmartPlaylistTotals(SmartPlaylistId.WholeShelf(kind), Now.UtcTicks));
        }
    }

    // ── most played ──

    [Fact]
    public void Most_played_is_the_songs_by_how_often_they_were_played()
    {
        Add("A/One/01.flac");
        Add("A/One/02.flac");
        Add("A/One/03.flac");
        Play("A/One/01.flac", times: 2, DaysAgo(1));
        Play("A/One/02.flac", times: 9, DaysAgo(1));
        Play("A/One/03.flac", times: 5, DaysAgo(1));

        Assert.Equal(["A/One/02.flac", "A/One/03.flac", "A/One/01.flac"], List(SmartPlaylistKind.MostPlayed));
    }

    [Fact]
    public void A_song_never_played_is_not_in_most_played()
    {
        Add("A/One/01.flac");
        Add("A/One/02.flac");
        Play("A/One/02.flac", times: 1, DaysAgo(1));

        Assert.Equal(["A/One/02.flac"], List(SmartPlaylistKind.MostPlayed));
    }

    [Fact]
    public void Songs_played_as_often_are_led_by_the_one_played_most_recently()
    {
        Add("A/One/01.flac");
        Add("A/One/02.flac");
        Play("A/One/01.flac", times: 3, DaysAgo(30));
        Play("A/One/02.flac", times: 3, DaysAgo(1));

        Assert.Equal(["A/One/02.flac", "A/One/01.flac"], List(SmartPlaylistKind.MostPlayed));
    }

    [Fact]
    public void Most_played_stops_at_its_limit()
    {
        for (var i = 0; i < 5; i++)
        {
            var path = $"A/One/{i:00}.flac";
            Add(path);
            Play(path, times: i + 1, DaysAgo(1));
        }

        Assert.Equal(2, _db.QuerySmartPlaylist(SmartPlaylistId.WholeShelf(SmartPlaylistKind.MostPlayed), Now.UtcTicks, limit: 2, advance: true).Count);
    }

    [Fact]
    public void Most_played_totals_count_the_played_songs_only()
    {
        Add("A/One/01.flac", seconds: 120);
        Add("A/One/02.flac", seconds: 240);
        Play("A/One/02.flac", times: 1, DaysAgo(1));

        var (count, seconds, _) = _db.SmartPlaylistTotals(SmartPlaylistId.WholeShelf(SmartPlaylistKind.MostPlayed), Now.UtcTicks);

        Assert.Equal(1, count);
        Assert.Equal(240, seconds);
    }

    // ── the lists the track rows answer ──

    [Fact]
    public void Never_played_is_what_the_collection_holds_and_nobody_has_heard()
    {
        Add("A/One/01.flac");
        Add("A/One/02.flac");
        Play("A/One/01.flac", times: 1, DaysAgo(1));

        Assert.Equal(["A/One/02.flac"], List(SmartPlaylistKind.NeverPlayed));
    }

    [Fact]
    public void Recently_played_is_newest_first()
    {
        Add("A/One/01.flac");
        Add("A/One/02.flac");
        Play("A/One/01.flac", times: 1, DaysAgo(9));
        Play("A/One/02.flac", times: 1, DaysAgo(2));

        Assert.Equal(["A/One/02.flac", "A/One/01.flac"], List(SmartPlaylistKind.RecentlyPlayed));
    }

    [Fact]
    public void Loved_and_forgotten_is_the_favorites_nobody_has_played_in_half_a_year()
    {
        Add("A/One/01.flac");
        Add("A/One/02.flac");
        Add("A/One/03.flac");
        foreach (var path in new[] { "A/One/01.flac", "A/One/02.flac" })
            _db.SetTrackFavorite(path, favorite: true, atUtcTicks: Now.UtcTicks);

        Play("A/One/01.flac", times: 1, DaysAgo(SmartPlaylists.ForgottenDays + 10));
        Play("A/One/02.flac", times: 1, DaysAgo(3));
        Play("A/One/03.flac", times: 1, DaysAgo(SmartPlaylists.ForgottenDays + 10));   // not a favourite

        Assert.Equal(["A/One/01.flac"], List(SmartPlaylistKind.LovedAndForgotten));
    }

    /// <summary>A favourite that has never been played at all has a stamp of zero, so it leads the
    /// list. That is right: it is the strongest version of the same fact.</summary>
    [Fact]
    public void A_favorite_never_played_leads_loved_and_forgotten()
    {
        Add("A/One/01.flac");
        Add("A/One/02.flac");
        _db.SetTrackFavorite("A/One/01.flac", favorite: true, atUtcTicks: Now.UtcTicks);
        _db.SetTrackFavorite("A/One/02.flac", favorite: true, atUtcTicks: Now.UtcTicks);
        Play("A/One/01.flac", times: 1, DaysAgo(SmartPlaylists.ForgottenDays + 10));

        Assert.Equal(["A/One/02.flac", "A/One/01.flac"], List(SmartPlaylistKind.LovedAndForgotten));
    }

    [Fact]
    public void Played_but_not_favorited_needs_both_halves()
    {
        Add("A/One/01.flac");
        Add("A/One/02.flac");
        Add("A/One/03.flac");
        Play("A/One/01.flac", times: SmartPlaylists.PlayedNotLovedPlays, DaysAgo(1));
        Play("A/One/02.flac", times: SmartPlaylists.PlayedNotLovedPlays, DaysAgo(1));
        Play("A/One/03.flac", times: 1, DaysAgo(1));
        _db.SetTrackFavorite("A/One/02.flac", favorite: true, atUtcTicks: Now.UtcTicks);

        Assert.Equal(["A/One/01.flac"], List(SmartPlaylistKind.PlayedNotLoved));
    }

    // ── the lists the log answers ──

    [Fact]
    public void On_repeat_is_what_has_been_played_over_and_over_lately()
    {
        Add("A/One/01.flac");
        Add("A/One/02.flac");
        Play("A/One/01.flac", times: SmartPlaylists.OnRepeatPlays, DaysAgo(3));
        Play("A/One/02.flac", times: 1, DaysAgo(3));

        Assert.Equal(["A/One/01.flac"], List(SmartPlaylistKind.OnRepeat));
    }

    [Fact]
    public void On_repeat_does_not_count_what_happened_before_last_month()
    {
        Add("A/One/01.flac");
        Play("A/One/01.flac", times: 20, DaysAgo(SmartPlaylists.RecentDays + 5));

        Assert.Empty(List(SmartPlaylistKind.OnRepeat));
        Assert.Equal(1, Count(SmartPlaylistKind.MostPlayed));   // all-time still knows about it
    }

    [Fact]
    public void Rediscover_is_what_was_played_a_lot_and_then_went_quiet()
    {
        Add("A/One/01.flac");
        Add("A/One/02.flac");
        Add("A/One/03.flac");
        Play("A/One/01.flac", times: SmartPlaylists.RediscoverPlays, DaysAgo(SmartPlaylists.ForgottenDays + 30));
        Play("A/One/02.flac", times: SmartPlaylists.RediscoverPlays, DaysAgo(3));   // still current
        Play("A/One/03.flac", times: 1, DaysAgo(SmartPlaylists.ForgottenDays + 30));   // never mattered

        Assert.Equal(["A/One/01.flac"], List(SmartPlaylistKind.Rediscover));
    }

    /// <summary>Playing it again is what takes it off the list, and nothing else has to be told.</summary>
    [Fact]
    public void Rediscovering_something_takes_it_off_the_list()
    {
        Add("A/One/01.flac");
        Play("A/One/01.flac", times: SmartPlaylists.RediscoverPlays, DaysAgo(SmartPlaylists.ForgottenDays + 30));
        Assert.Single(List(SmartPlaylistKind.Rediscover));

        Play("A/One/01.flac", times: 1, DaysAgo(1));

        Assert.Empty(List(SmartPlaylistKind.Rediscover));
    }

    [Fact]
    public void Late_night_is_where_the_listening_happens_not_where_it_happened_once()
    {
        Add("A/One/01.flac");
        Add("A/One/02.flac");

        // A night song: heard at half past midnight, three times over.
        var night = new DateTimeOffset(2026, 8, 15, 0, 30, 0, TimeSpan.Zero);
        Play("A/One/01.flac", times: SmartPlaylists.LateNightListens, night);

        // A daytime song that happened to be on once at night.
        var day = new DateTimeOffset(2026, 8, 15, 15, 0, 0, TimeSpan.Zero);
        Play("A/One/02.flac", times: SmartPlaylists.LateNightListens, night);
        Play("A/One/02.flac", times: 20, day);

        Assert.Equal(["A/One/01.flac"], List(SmartPlaylistKind.LateNight));
    }

    [Fact]
    public void New_to_you_is_what_the_log_heard_for_the_first_time_this_month()
    {
        Add("A/One/01.flac");
        Add("A/One/02.flac");
        Play("A/One/01.flac", times: 1, DaysAgo(3));
        Play("A/One/02.flac", times: 1, DaysAgo(SmartPlaylists.RecentDays + 10));
        Play("A/One/02.flac", times: 1, DaysAgo(2));   // played again lately, but not new

        Assert.Equal(["A/One/01.flac"], List(SmartPlaylistKind.FirstListens));
    }

    [Fact]
    public void Often_skipped_needs_the_skips_to_outweigh_the_plays()
    {
        Add("A/One/01.flac");
        Add("A/One/02.flac");
        Skip("A/One/01.flac", times: SmartPlaylists.SkipsBeforeItShows, DaysAgo(3));
        Skip("A/One/02.flac", times: SmartPlaylists.SkipsBeforeItShows, DaysAgo(3));
        Play("A/One/02.flac", times: 4, DaysAgo(3));   // played far more than skipped

        Assert.Equal(["A/One/01.flac"], List(SmartPlaylistKind.KeepSkipping));
    }

    [Fact]
    public void One_bad_evening_is_not_a_habit()
    {
        Add("A/One/01.flac");
        Skip("A/One/01.flac", times: SmartPlaylists.SkipsBeforeItShows - 1, DaysAgo(3));

        Assert.Empty(List(SmartPlaylistKind.KeepSkipping));
    }

    /// <summary>Skips never move the totals, whatever list they end up feeding.</summary>
    [Fact]
    public void Skipping_something_does_not_make_it_played()
    {
        Add("A/One/01.flac");
        Skip("A/One/01.flac", times: 5, DaysAgo(3));

        Assert.Empty(List(SmartPlaylistKind.MostPlayed));
        Assert.Equal(["A/One/01.flac"], List(SmartPlaylistKind.NeverPlayed));
    }

    // ── which lists a heart can move ──

    /// <summary>
    /// <see cref="LibraryDatabase.SmartPlaylistNeedsMarks"/> is checked by turning a heart over and
    /// seeing which lists moved.
    ///
    /// <para>The pane asks this question to decide whether an open list re-reads itself when a song is
    /// loved. Re-reading whatever was open would repaint a playlist with no hearts on it whenever one
    /// song is loved. A wrong answer here fails silently in both directions: too narrow and a list
    /// quietly stops keeping up, too wide and the list flashes for no reason. So it is checked against
    /// the rules and not against a list somebody wrote down.</para>
    /// </summary>
    [Fact]
    public void Only_the_lists_that_read_the_heart_move_when_a_heart_does()
    {
        // Two songs, so that between them every rule about the heart has something to say: one played
        // often and lately, one played a lot and then left alone for longer than "forgotten".
        Add("A/One/01.flac", seconds: 100);
        Add("A/One/02.flac", seconds: 100);
        Add("A/One/03.flac", seconds: 100);
        Play("A/One/01.flac", times: 6, DaysAgo(2));
        Play("A/One/02.flac", times: 6, DaysAgo(SmartPlaylists.ForgottenDays + 20));
        Skip("A/One/03.flac", times: 4, DaysAgo(2));

        // A second RECORD, played more than either of the first. It is here for "Right now": that
        // list holds one song per record and ranks them. On a fixture of one record, a heart cannot
        // change which songs are in it or what order they come in, so the list would sit still while
        // plainly reading the heart, and the check below would call that a rule lying about itself.
        // With a second record the heart moves the first record's song above it, which is the fact
        // being tested.
        Add("B/Two/01.flac", seconds: 100);
        Play("B/Two/01.flac", times: 12, DaysAgo(3));

        var before = SmartPlaylists.All.ToDictionary(k => k, k => List(k).ToList());

        _db.SetTrackFavorite("A/One/01.flac", favorite: true, atUtcTicks: Now.UtcTicks);
        _db.SetTrackFavorite("A/One/02.flac", favorite: true, atUtcTicks: Now.UtcTicks);

        var moved = SmartPlaylists.All
            .Where(k => !List(k).SequenceEqual(before[k]))
            .ToList();

        Assert.All(moved, k => Assert.True(
            LibraryDatabase.SmartPlaylistNeedsMarks(k),
            $"{k} moved when a heart did, and does not say it reads the heart"));

        // …and the other way round, so the answer cannot be right by saying no to everything: every
        // rule that claims to read the heart has to be one that a heart actually moved.
        Assert.Equal(
            SmartPlaylists.All.Where(LibraryDatabase.SmartPlaylistNeedsMarks).OrderBy(k => k),
            moved.OrderBy(k => k));

        // Four of the nineteen: the question is useful because the answer is nearly always no.
        Assert.Equal(4, moved.Count);
    }

    // ── the counts the pane draws its rows from ──

    /// <summary>The totals and the list are the same answer counted two ways. This lets the pane's
    /// subtitle come from a COUNT and not from the rows, and it decides whether the row is drawn at
    /// all.</summary>
    [Fact]
    public void The_totals_agree_with_the_list_for_every_kind()
    {
        Add("A/One/01.flac", seconds: 100);
        Add("A/One/02.flac", seconds: 100);
        Add("A/One/03.flac", seconds: 100);
        _db.SetTrackFavorite("A/One/01.flac", favorite: true, atUtcTicks: Now.UtcTicks);
        Play("A/One/01.flac", times: 6, DaysAgo(2));
        Play("A/One/02.flac", times: 4, DaysAgo(SmartPlaylists.ForgottenDays + 20));
        Skip("A/One/03.flac", times: 4, DaysAgo(2));

        foreach (var kind in SmartPlaylists.All)
        {
            var tracks = _db.QuerySmartPlaylist(SmartPlaylistId.WholeShelf(kind), Now.UtcTicks, advance: true);
            var (count, seconds, _) = _db.SmartPlaylistTotals(SmartPlaylistId.WholeShelf(kind), Now.UtcTicks);

            Assert.Equal(tracks.Count, count);
            Assert.Equal(tracks.Sum(t => t.DurationSeconds), seconds);
        }
    }

    // ── what the home screen offers ──

    /// <summary>Home offers music to put on, not chores. The four it leaves out are maintenance tasks about
    /// a collection and not an hour of listening. A kind appended to <see cref="SmartPlaylists.All"/>
    /// tomorrow must not arrive on that screen by accident, and this case is what prevents
    /// that.</summary>
    [Fact]
    public void The_home_screen_offers_music_and_not_housekeeping()
    {
        Assert.DoesNotContain(SmartPlaylistKind.NeverPlayed, SmartPlaylists.OnTheHomeScreen);
        Assert.DoesNotContain(SmartPlaylistKind.KeepSkipping, SmartPlaylists.OnTheHomeScreen);
        Assert.DoesNotContain(SmartPlaylistKind.PlayedNotLoved, SmartPlaylists.OnTheHomeScreen);

        // Already answered on that screen by a row of records: the same question twice, once in songs
        // and once in sleeves.
        Assert.DoesNotContain(SmartPlaylistKind.RecentlyAdded, SmartPlaylists.OnTheHomeScreen);
    }

    /// <summary>And everything it does offer is a real kind with a rule behind it: a list on the home
    /// screen that is not in <see cref="SmartPlaylists.All"/> would be a card that opens nothing.</summary>
    [Fact]
    public void Every_list_on_the_home_screen_is_one_the_library_generates()
    {
        Assert.All(SmartPlaylists.OnTheHomeScreen, kind => Assert.Contains(kind, SmartPlaylists.All));
        Assert.Equal(
            SmartPlaylists.OnTheHomeScreen.Distinct().Count(),
            SmartPlaylists.OnTheHomeScreen.Count);
    }

    /// <summary>
    /// Every generated list belongs to exactly one folder in the tree.
    ///
    /// <para>The pane draws the moods folder from <see cref="SmartPlaylists.Moods"/> and the pinned
    /// folder from everything else, so a kind on neither list would never be shown and a kind on
    /// both would be drawn twice. Neither failure shows anything on screen except that a playlist is
    /// missing, which is the kind of fault nobody reports.</para>
    /// </summary>
    [Fact]
    public void Every_generated_list_is_in_exactly_one_folder()
    {
        // A kind named here that the app does not offer is a folder promising a row that cannot exist.
        Assert.Empty(SmartPlaylists.Moods.Where(k => !SmartPlaylists.All.Contains(k)));

        // Named twice, it would be drawn twice.
        Assert.Equal(SmartPlaylists.Moods.Count, SmartPlaylists.Moods.Distinct().Count());

        // And neither folder is left with nothing: the pinned one keeps the lists about listening,
        // which is why the moods are a separate group.
        Assert.NotEmpty(SmartPlaylists.All.Where(SmartPlaylists.Moods.Contains));
        Assert.NotEmpty(SmartPlaylists.All.Where(k => !SmartPlaylists.Moods.Contains(k)));
    }

    /// <summary>The moods folder holds the lists cut from how the music sounds: every kind that is
    /// broken out by genre, plus the two that are not cut that way but are chosen for the same
    /// reason, a listening mood. A mood kind left out of the folder is a playlist that vanishes from
    /// the tree.</summary>
    [Fact]
    public void The_moods_folder_holds_every_list_that_is_broken_out_by_genre()
    {
        foreach (var kind in SmartPlaylists.BrokenOutByGenre)
            Assert.Contains(kind, SmartPlaylists.Moods);

        Assert.Contains(SmartPlaylistKind.Sleep, SmartPlaylists.Moods);
        Assert.Contains(SmartPlaylistKind.Classical, SmartPlaylists.Moods);
    }

    // ── the one pass that also says what a list is made of ──

    /// <summary>The card read and the totals read are the same pass asked two ways, so they cannot
    /// disagree about how long a list is or whether it holds an explicit song. If they stop agreeing,
    /// a list's row and its card say different things about it.
    /// </summary>
    [Fact]
    public void The_card_read_agrees_with_the_totals()
    {
        Add("A/One/01.flac", genre: "Rock");
        Add("A/One/02.flac", genre: "Jazz");
        _db.SetTrackFavorite("A/One/01.flac", favorite: true, atUtcTicks: Now.UtcTicks);
        _db.SetTrackFavorite("A/One/02.flac", favorite: true, atUtcTicks: Now.UtcTicks);

        var id = SmartPlaylistId.WholeShelf(SmartPlaylistKind.FavoriteTracks);
        var (tracks, seconds, marked) = _db.SmartPlaylistTotals(id, Now.UtcTicks);
        var card = _db.SmartPlaylistCard(id, Now.UtcTicks);

        Assert.Equal(tracks, card.Tracks);
        Assert.Equal(marked, card.Explicit);
        Assert.True(seconds > 0);
    }

    /// <summary>One genre per song in the list, in the list's own order, raw. Which of them are worth
    /// naming is MoodBlurb's answer and is the same on both screens. A database that decided it would
    /// be that rule written down twice.</summary>
    [Fact]
    public void The_card_read_hands_back_a_genre_for_every_song()
    {
        Add("A/One/01.flac", genre: "Rock");
        Add("A/One/02.flac", genre: "Jazz");
        Add("A/One/03.flac");   // no tag at all, which is an ordinary row
        _db.SetTrackFavorite("A/One/01.flac", favorite: true, atUtcTicks: Now.UtcTicks);
        _db.SetTrackFavorite("A/One/02.flac", favorite: true, atUtcTicks: Now.UtcTicks);
        _db.SetTrackFavorite("A/One/03.flac", favorite: true, atUtcTicks: Now.UtcTicks);

        var card = _db.SmartPlaylistCard(
            SmartPlaylistId.WholeShelf(SmartPlaylistKind.FavoriteTracks), Now.UtcTicks);

        Assert.Equal(card.Tracks, card.Genres.Count);
        Assert.Contains("Rock", card.Genres);
        Assert.Contains("Jazz", card.Genres);
        Assert.Contains(null, card.Genres);
    }

    /// <summary>A list with nothing in it has nothing to describe, and says so with an empty list and
    /// does not throw.</summary>
    [Fact]
    public void An_empty_list_is_made_of_nothing()
    {
        var card = _db.SmartPlaylistCard(
            SmartPlaylistId.WholeShelf(SmartPlaylistKind.FavoriteTracks), Now.UtcTicks);

        Assert.Equal(0, card.Tracks);
        Assert.Empty(card.Genres);
    }
}
