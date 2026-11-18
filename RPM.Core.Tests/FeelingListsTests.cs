using RPM.Core.Analysis;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The six lists the sound model and the tags make: Melancholy, Happy, Angry, Romantic, Zen and Festive.
/// Which songs are in them, in what order, and what they take away from the other lists.
/// </summary>
public class FeelingListsTests
{
    private static MeasuredMood Track(
        string folder, double brightness, double onsets, double dynamics, int n, string artist = "") =>
        new($"{folder}/{n}.flac", folder, brightness, onsets, dynamics, false, artist, 0,
            LyricValence: null, Loudness: null);

    private static readonly Dictionary<string, CatalogueFacts> NoCatalogue = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A genre's worth of songs whose sound rises evenly, one record every two songs, as
    /// <c>MoodShelfTests</c> builds them.</summary>
    private static List<MeasuredMood> Ramp(string folder, int count)
    {
        var rows = new List<MeasuredMood>(count);
        for (int i = 0; i < count; i++)
        {
            double at = (double)i / (count - 1);
            rows.Add(Track($"{folder}-{i / 2}", at, at, at, i, artist: $"{folder} act {i / 2}"));
        }
        return rows;
    }

    private static Dictionary<string, string> FiledAs(IEnumerable<MeasuredMood> rows, string genre)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
            map[r.Folder] = genre;
        return map;
    }

    private static Dictionary<string, string> Merged(params Dictionary<string, string>[] maps)
    {
        var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var map in maps)
            foreach (var (k, v) in map)
                all[k] = v;
        return all;
    }

    /// <summary>Scores where only <paramref name="feeling"/> is set.</summary>
    private static FeelingScores Only(Feeling feeling, double score) => feeling switch
    {
        Feeling.Melancholy => new(score, 0, 0, 0, 0, 0),
        Feeling.Happy => new(0, score, 0, 0, 0, 0),
        Feeling.Angry => new(0, 0, score, 0, 0, 0),
        Feeling.Romantic => new(0, 0, 0, score, 0, 0),
        Feeling.Zen => new(0, 0, 0, 0, score, 0),
        Feeling.Festive => new(0, 0, 0, 0, 0, score),
    };

    private static ShelfHearing Heard(
        IReadOnlyDictionary<string, FeelingScores> feelings,
        IReadOnlySet<string>? holiday = null, IReadOnlySet<string>? zen = null, bool season = false,
        bool melancholyShown = true, IReadOnlySet<string>? newYear = null, bool newYearsEve = false) =>
        new(feelings,
            holiday ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            newYear ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            zen ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            season,
            newYearsEve,
            melancholyShown);

    // ── the four feelings ──

    /// <summary>
    /// A feeling is cut within each genre, so every genre has an end in it. The list over the whole
    /// collection is ordered by the score itself, so the genre whose songs score higher leads it: the
    /// angriest jazz does not stand beside the angriest metal.
    /// </summary>
    [Fact]
    public void A_feeling_is_cut_within_each_genre_and_ordered_by_its_score()
    {
        // Two genres whose ends both sound angry over the whole collection, metal more so, and a large
        // genre that is not angry at all, which keeps both ends near the top of everything.
        var metal = Ramp("metal", 50);
        var jazz = Ramp("jazz", 50);
        var pop = Ramp("pop", 300);
        var feelings = new Dictionary<string, FeelingScores>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 50; i++)
        {
            feelings[metal[i].RelativePath] = Only(Feeling.Angry, 0.5 + i / 100.0);
            feelings[jazz[i].RelativePath] = Only(Feeling.Angry, 0.42 + i / 100.0);
        }
        for (int i = 0; i < pop.Count; i++)
            feelings[pop[i].RelativePath] = Only(Feeling.Angry, -0.5 + i / 1000.0);

        var shelf = MoodShelf.Build(
            [.. metal, .. jazz, .. pop],
            Merged(FiledAs(metal, "Metal"), FiledAs(jazz, "Jazz"), FiledAs(pop, "Pop")),
            NoCatalogue, Likes.None, Heard(feelings));

        var angry = shelf.Angry.WholeList.Songs;

        // Both genres have an end in it, the top 15% of each.
        Assert.Contains(jazz[^1].RelativePath, angry);
        Assert.Contains(metal[^1].RelativePath, angry);
        Assert.DoesNotContain(jazz[0].RelativePath, angry);
        Assert.NotEmpty(shelf.Angry.Within(["Jazz"], []).Songs);

        // Every metal song in it comes before every jazz song, because metal scored higher.
        Assert.Equal(metal[^1].RelativePath, angry[0]);
        var lastMetal = angry.ToList().FindLastIndex(p => p.StartsWith("metal", StringComparison.Ordinal));
        var firstJazz = angry.ToList().FindIndex(p => p.StartsWith("jazz", StringComparison.Ordinal));
        Assert.True(lastMetal < firstJazz);

        // The angriest pop is not angry over the whole collection, so pop is not in the list at all.
        Assert.DoesNotContain(angry, p => p.StartsWith("pop", StringComparison.Ordinal));
    }

    /// <summary>
    /// A genre whose end of a feeling does not sound like the feeling over the whole collection holds no
    /// songs in it: no row, and nothing in the list over the whole collection. In the blind check of
    /// 2026-11-18 the angriest jazz was 0 of 10 angry.
    /// </summary>
    [Fact]
    public void A_genre_whose_end_is_not_the_feeling_is_not_in_the_list()
    {
        var metal = Ramp("metal", 100);
        var jazz = Ramp("jazz", 50);
        var feelings = new Dictionary<string, FeelingScores>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < metal.Count; i++)
            feelings[metal[i].RelativePath] = Only(Feeling.Angry, 0.3 + i / 200.0);
        for (int i = 0; i < jazz.Count; i++)
            feelings[jazz[i].RelativePath] = Only(Feeling.Angry, -0.4 + i / 100.0);

        var shelf = MoodShelf.Build(
            [.. metal, .. jazz], Merged(FiledAs(metal, "Metal"), FiledAs(jazz, "Jazz")),
            NoCatalogue, Likes.None, Heard(feelings));

        Assert.DoesNotContain(shelf.Angry.WholeList.Songs, p => p.StartsWith("jazz", StringComparison.Ordinal));
        Assert.Empty(shelf.Angry.Within(["Jazz"], []).Songs);
        Assert.Empty(shelf.Angry.GenresWorthListing(1).Where(g => g == "Jazz"));
        Assert.NotEmpty(shelf.Angry.Within(["Metal"], []).Songs);
    }

    /// <summary>Each feeling's hip-hop row draws both hip-hop shelves, as the blind check heard them, and
    /// the rap shelf is not a row of its own.</summary>
    [Fact]
    public void Each_feelings_hip_hop_row_holds_both_shelves()
    {
        foreach (var kind in new[] { SmartPlaylistKind.Melancholy, SmartPlaylistKind.Happy, SmartPlaylistKind.Angry, SmartPlaylistKind.Romantic })
        {
            Assert.Contains(new MoodCell(kind, "Rap"), MoodSets.CellsFor(kind, "Hip-Hop / Rap"));
            Assert.True(MoodSets.IsAbsorbed(kind, "Rap"));
            Assert.True(MoodSets.IsOffered(kind, "Hip-Hop / Rap"));
        }
    }

    /// <summary>A song the sound pass has not reached is in none of the four lists, whatever it
    /// measures. The meter's lists do not wait for it.</summary>
    [Fact]
    public void A_song_nobody_has_heard_is_in_no_feeling_list()
    {
        var rows = Ramp("rock", 100);

        var shelf = MoodShelf.Build(rows, FiledAs(rows, "Rock"), NoCatalogue, Likes.None, ShelfHearing.Nothing);

        Assert.Empty(shelf.Melancholy.WholeList.Songs);
        Assert.Empty(shelf.Happy.WholeList.Songs);
        Assert.Empty(shelf.Angry.WholeList.Songs);
        Assert.Empty(shelf.Romantic.WholeList.Songs);
        Assert.NotEmpty(shelf.Calm.WholeList.Songs);
    }

    /// <summary>
    /// Melancholy takes the saddest part of its cut out of Chill: the saddest 5% of the genre. A calm
    /// song that is only a little sad stays in Chill, and is in Melancholy as well.
    /// </summary>
    [Fact]
    public void Melancholy_takes_its_saddest_songs_out_of_chill()
    {
        var rows = Ramp("rock", 100);
        var filed = FiledAs(rows, "Rock");

        // Every other song is sad, the calmest sad songs most of all. Melancholy's cut (the top 15%) is
        // the fifteen even songs 0 to 28; its saddest part (the top 5%) is the five even songs 0 to 8.
        // The calm band is songs 0 to 14.
        var feelings = new Dictionary<string, FeelingScores>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rows.Count; i++)
            feelings[rows[i].RelativePath] = Only(Feeling.Melancholy, i % 2 == 0 ? 1 - i / 100.0 : 0);

        var before = MoodShelf.Build(rows, filed, NoCatalogue, Likes.None, ShelfHearing.Nothing);
        var after = MoodShelf.Build(rows, filed, NoCatalogue, Likes.None, Heard(feelings));

        // The saddest calm song leaves Chill for Melancholy.
        Assert.Contains(rows[0].RelativePath, before.Calm.WholeList.Songs);
        Assert.DoesNotContain(rows[0].RelativePath, after.Calm.WholeList.Songs);
        Assert.Contains(rows[0].RelativePath, after.Melancholy.WholeList.Songs);

        // A calm song in Melancholy's cut but not in its saddest part is in both lists.
        Assert.Contains(rows[12].RelativePath, after.Calm.WholeList.Songs);
        Assert.Contains(rows[12].RelativePath, after.Melancholy.WholeList.Songs);

        // A calm song that is not sad stays in Chill only.
        Assert.Contains(rows[1].RelativePath, after.Calm.WholeList.Songs);
        Assert.DoesNotContain(rows[1].RelativePath, after.Melancholy.WholeList.Songs);
    }

    /// <summary>While Melancholy is not on screen, Chill keeps every song it had. A song must not leave a
    /// list for one that nobody can open.</summary>
    [Fact]
    public void Chill_keeps_its_sad_songs_until_melancholy_is_shown()
    {
        var rows = Ramp("rock", 100);
        var filed = FiledAs(rows, "Rock");
        var feelings = new Dictionary<string, FeelingScores>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rows.Count; i++)
            feelings[rows[i].RelativePath] = Only(Feeling.Melancholy, 1 - i / 100.0);

        var before = MoodShelf.Build(rows, filed, NoCatalogue, Likes.None, ShelfHearing.Nothing);
        var hidden = MoodShelf.Build(rows, filed, NoCatalogue, Likes.None, Heard(feelings, melancholyShown: false));

        Assert.Equal(before.Calm.WholeList.Songs, hidden.Calm.WholeList.Songs);
        Assert.Contains(rows[0].RelativePath, hidden.Melancholy.WholeList.Songs);
    }

    /// <summary>Melancholy is on screen, in the moods folder, so the session takes the saddest songs out of
    /// Chill.</summary>
    [Fact]
    public void Melancholy_is_shown()
        => Assert.Contains(SmartPlaylistKind.Melancholy, SmartPlaylists.Moods);

    /// <summary>Angry never holds Soul, and Romantic never holds Club: not a row of their own, and not
    /// in the list over the whole collection either.</summary>
    [Fact]
    public void A_list_never_holds_the_genres_it_refuses()
    {
        var soul = Ramp("soul", 50);
        var club = Ramp("club", 50);
        var rock = Ramp("rock", 50);
        var feelings = new Dictionary<string, FeelingScores>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in (List<MeasuredMood>)[.. soul, .. club, .. rock])
            feelings[row.RelativePath] = new FeelingScores(0, 0, row.Onsets, row.Onsets, 0, 0);

        var shelf = MoodShelf.Build(
            [.. soul, .. club, .. rock],
            Merged(FiledAs(soul, "Soul"), FiledAs(club, "Club"), FiledAs(rock, "Rock")),
            NoCatalogue, Likes.None, Heard(feelings));

        Assert.DoesNotContain(shelf.Angry.WholeList.Songs, p => p.StartsWith("soul", StringComparison.Ordinal));
        Assert.Empty(shelf.Angry.Within(["Soul"], []).Songs);
        Assert.Contains(shelf.Angry.WholeList.Songs, p => p.StartsWith("club", StringComparison.Ordinal));

        Assert.DoesNotContain(shelf.Romantic.WholeList.Songs, p => p.StartsWith("club", StringComparison.Ordinal));
        Assert.Contains(shelf.Romantic.WholeList.Songs, p => p.StartsWith("soul", StringComparison.Ordinal));
    }

    // ── holiday music ──

    /// <summary>Out of season, a holiday song is in no list at all, the calm ones included. In season it
    /// may be anywhere a song of its sound may be.</summary>
    [Fact]
    public void Holiday_music_waits_for_its_season()
    {
        var rows = Ramp("rock", 100);
        var filed = FiledAs(rows, "Rock");
        var carol = rows[0].RelativePath;
        var holiday = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { carol };
        var feelings = new Dictionary<string, FeelingScores>(StringComparer.OrdinalIgnoreCase);

        var outOfSeason = MoodShelf.Build(rows, filed, NoCatalogue, Likes.None, Heard(feelings, holiday));
        var inSeason = MoodShelf.Build(rows, filed, NoCatalogue, Likes.None, Heard(feelings, holiday, season: true));

        Assert.DoesNotContain(carol, outOfSeason.Calm.WholeList.Songs);
        Assert.Contains(carol, inSeason.Calm.WholeList.Songs);
    }

    /// <summary>
    /// New Year's Eve is the songs about the new year first, then a party: what Feel Good holds and the
    /// meter calls danceable. No Christmas song, however happy, and nothing outside its days.
    /// </summary>
    [Fact]
    public void New_years_eve_is_the_new_years_songs_and_a_party()
    {
        // A rock shelf whose busiest, evenest songs are danceable (MoodBandsTests: busy over a level that
        // holds still), and whose happiest songs are the same ones.
        var rows = new List<MeasuredMood>();
        for (int i = 0; i < 100; i++)
        {
            double at = i / 99.0;
            rows.Add(Track($"rock-{i / 2}", at, at, 1 - at, i, artist: $"act {i / 2}"));
        }
        var filed = FiledAs(rows, "Rock");
        var feelings = new Dictionary<string, FeelingScores>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rows.Count; i++)
            feelings[rows[i].RelativePath] = Only(Feeling.Happy, i / 100.0);

        var auldLangSyne = rows[10].RelativePath;      // a quiet song about the new year
        var carol = rows[99].RelativePath;             // the happiest and most danceable, and a Christmas song
        var holiday = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { auldLangSyne, carol };
        var newYear = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { auldLangSyne };

        var night = MoodShelf.Build(rows, filed, NoCatalogue, Likes.None,
            Heard(feelings, holiday, season: true, newYear: newYear, newYearsEve: true));
        var july = MoodShelf.Build(rows, filed, NoCatalogue, Likes.None,
            Heard(feelings, holiday, season: false, newYear: newYear, newYearsEve: false));

        var list = night.NewYearsEve.WholeList.Songs;
        Assert.Equal(auldLangSyne, list[0]);
        Assert.DoesNotContain(carol, list);
        Assert.Contains(rows[97].RelativePath, list);
        Assert.All(list.Skip(1), p => Assert.Contains(p, night.Happy.WholeList.Songs));

        Assert.Empty(july.NewYearsEve.WholeList.Songs);
    }

    [Theory]
    [InlineData("Happy New Year", "Arrival", true)]
    [InlineData("Auld Lang Syne", "", true)]
    [InlineData("Track 3", "New Year's Eve Party Hits", true)]
    [InlineData("Oudejaarsavond", "", true)]
    [InlineData("Silent Night", "", false)]
    [InlineData("Last Christmas", "", false)]
    [InlineData("Yesterday", "Help!", false)]
    public void The_new_years_songs_are_found_by_their_words(string title, string album, bool newYear)
    {
        Assert.Equal(newYear, HolidayMusic.IsNewYear(title, album, null));

        // Every New Year's song is holiday music too, so it keeps out of the other lists in July.
        if (newYear)
            Assert.True(HolidayMusic.IsHoliday(title, album, null));
    }

    [Theory]
    [InlineData(12, 25, false)]
    [InlineData(12, 26, true)]
    [InlineData(12, 31, true)]
    [InlineData(1, 1, true)]
    [InlineData(1, 2, false)]
    [InlineData(6, 30, false)]
    public void New_years_eve_runs_from_boxing_day_to_new_years_day(int month, int day, bool offered)
        => Assert.Equal(offered, HolidayMusic.IsNewYearsEveTime(new DateTime(2026, month, day, 20, 0, 0)));

    [Theory]
    [InlineData("Silent Night", "Greatest Hits", "Pop", true)]
    [InlineData("Track 4", "Merry Christmas", "Pop", true)]
    [InlineData("Track 4", "Album", "Holiday", true)]
    [InlineData("Kerstmis in de stad", "", "", true)]
    [InlineData("Feliz Navidad", null, null, true)]
    [InlineData("Happy New Year", "Arrival", "Pop", true)]
    [InlineData("Holiday Christmas Medley", "", "", true)]
    [InlineData("Dreadlock Holiday", "Bloody Tourists", "Rock", false)]
    [InlineData("Holiday", "Madonna", "Pop", false)]
    [InlineData("Bachelor Boy", "Summer Holiday", "Pop", false)]
    [InlineData("Christmassy", "", "", false)]
    [InlineData("Santa Baby", "", "", true)]
    [InlineData("Santa Claus Is Coming To Town", "", "", true)]
    [InlineData("Santa Barbara Improv", "", "", false)]
    [InlineData("Santa Monica Dream", "", "", false)]
    [InlineData("Rush (Tom Santa Remix)", "Rush (remixes)", "Pop", false)]
    [InlineData("NOËL BLANC", "", "", true)]
    [InlineData(null, null, null, false)]
    public void Holiday_music_is_found_by_its_words(string? title, string? album, string? genre, bool holiday)
    {
        Assert.Equal(holiday, HolidayMusic.IsHoliday(title, album, genre));

        // The database narrows by the hints first, so every holiday song must hold one of them.
        if (holiday)
            Assert.Contains(HolidayMusic.Hints, hint =>
                $"{title} | {album} | {genre}".Contains(hint, StringComparison.Ordinal)
                || $"{title} | {album} | {genre}".ToLowerInvariant().Contains(hint, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(11, 21, false)]
    [InlineData(11, 22, true)]
    [InlineData(12, 25, true)]
    [InlineData(1, 6, true)]
    [InlineData(1, 7, false)]
    [InlineData(7, 1, false)]
    public void The_season_runs_from_late_november_to_twelfth_night(int month, int day, bool season)
        => Assert.Equal(season, HolidayMusic.InSeason(new DateTime(2026, month, day, 12, 0, 0)));

    // ── Zen ──

    /// <summary>
    /// Zen is the still songs of the ambient records and nothing else: not a calm song on another
    /// record, and not a loud song on an ambient one. It takes up to five songs from one record.
    /// </summary>
    [Fact]
    public void Zen_is_the_still_songs_of_the_ambient_records()
    {
        var shelf = Ramp("rock", 100);
        var ambient = Enumerable.Range(0, 8).Select(n => Track("enya", 0.0, 0.0, 0.0, n, "Enya"))
            .Append(Track("enya", 1.0, 1.0, 0.0, 8, "Enya"))
            .ToList();
        var other = Enumerable.Range(0, 3).Select(n => Track("quiet", 0.0, 0.0, 0.0, n, "Quiet")).ToList();
        List<MeasuredMood> rows = [.. shelf, .. ambient, .. other];
        var zen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "enya" };

        var built = MoodShelf.Build(
            rows, FiledAs(rows, "Electronic / Dance"), NoCatalogue, Likes.None,
            Heard(new Dictionary<string, FeelingScores>(), zen: zen));

        var pool = built.Zen.WholeList;
        Assert.Equal(8, pool.Count);
        Assert.All(pool.Songs, p => Assert.StartsWith("enya/", p));
        Assert.DoesNotContain("enya/8.flac", pool.Songs);

        // Five songs of the one record, not one.
        Assert.Equal(5, pool.Window(SmartPlaylists.MostSongs(SmartPlaylistKind.Zen), 0).Count);
    }

    /// <summary>A record is Zen when the tag most of its tracks carry, or the first style the catalogues
    /// give it, is calm ambient music. Ambient as one style among several is not enough, and dark ambient
    /// is not calm.</summary>
    [Fact]
    public void A_zen_record_is_named_by_its_tag_or_its_first_style()
    {
        IReadOnlyList<(string Folder, string Genre, int Count)> counts =
        [
            ("enya", "New Age", 10),
            ("mixed", "Ambient", 2), ("mixed", "Rock", 8),
            ("dark", "Dark Ambient", 9),
            ("burial", "Electronic", 9),
            ("elements", "Electronic", 12),
        ];
        var facts = new Dictionary<string, CatalogueFacts>(StringComparer.OrdinalIgnoreCase)
        {
            ["burial"] = new("Electronic / Dance", ["Dubstep", "Ambient"]),
            ["elements"] = new("Electronic / Dance", ["Ambient", "Downtempo"]),
        };

        var folders = MoodBands.ZenFolders(counts, facts);

        Assert.Equal(new[] { "elements", "enya" }, folders.Order(StringComparer.Ordinal));
    }

    // ── the record cap ──

    [Fact]
    public void Zen_takes_five_songs_from_a_record_and_the_rest_take_one()
    {
        foreach (var kind in Enum.GetValues<SmartPlaylistKind>())
            Assert.Equal(
                kind is SmartPlaylistKind.Zen ? 5 : SmartPlaylists.MostFromOneRecord,
                SmartPlaylists.MostFromOneRecordFor(kind));
    }

    // ── the scores ──

    /// <summary>The six scores at once are the six scores one at a time.</summary>
    [Fact]
    public void Every_score_at_once_is_each_score_alone()
    {
        var random = new Random(7);
        var v = new float[SoundEmbedding.Dimensions];
        for (int i = 0; i < v.Length; i++)
            v[i] = (float)(random.NextDouble() * 2 - 1);
        var embedding = SoundEmbedding.Average([v])!;

        var all = SoundPrompts.ScoreAll(embedding);

        // Six places: ScoreAll works in single precision, and two records' scores differ by thousandths.
        foreach (var feeling in Enum.GetValues<Feeling>())
            Assert.Equal(SoundPrompts.Score(embedding, feeling), all.For(feeling), 6);
    }
}
