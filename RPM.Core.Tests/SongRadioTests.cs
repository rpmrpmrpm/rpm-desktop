using RPM.Core.Analysis;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// What a song radio picks next: songs close in mood, era and genre, never one already queued or played, and
/// spaced so one act or one record does not fill the queue. See <see cref="SongRadio"/>.
/// </summary>
public class SongRadioTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<SmartPlaylistKind>> NoMoods =
        new Dictionary<string, IReadOnlySet<SmartPlaylistKind>>();

    private static readonly IReadOnlySet<string> Nothing = new HashSet<string>();

    /// <summary>A row with its own record and its own act unless told otherwise, so the spacing rules stay out of
    /// tests that are not about them.</summary>
    private static RadioRow Row(
        string path, int year = 1975, double? sound = 0.5, string? artist = null, string? folder = null,
        string? title = null, bool offered = true, double? bpm = null, int plays = 0, string? genreTag = null,
        string? subgenre = null) =>
        new(path, folder ?? path, artist ?? "act " + path, title ?? "song " + path, year,
            sound, sound, sound, offered, bpm, plays, genreTag, subgenre);

    private static RadioCrate Crate(
        IEnumerable<RadioRow> rows, IReadOnlyDictionary<string, string>? genres = null,
        IReadOnlyDictionary<string, IReadOnlySet<SmartPlaylistKind>>? moods = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? styles = null)
    {
        var list = rows.ToList();
        return RadioCrate.Build(
            list,
            genres ?? list.Select(r => r.Folder).Distinct().ToDictionary(folder => folder, _ => "Rock"),
            moods ?? NoMoods,
            styles);
    }

    private static Dictionary<string, string> AllOn(string shelf, params string[] folders) =>
        folders.ToDictionary(folder => folder, _ => shelf);

    private static IReadOnlyList<string> Pick(
        RadioCrate crate, string seed, int count, IReadOnlyList<string>? history = null,
        IReadOnlySet<string>? inQueue = null, string? origin = null, int random = 7) =>
        SongRadio.Pick(crate, seed, origin ?? seed, inQueue ?? Nothing, history ?? [], count, new Random(random));

    // ── what is close ──

    [Fact]
    public void A_song_from_the_same_genre_era_and_mood_is_closer_than_one_that_differs_in_any_of_them()
    {
        var crate = Crate(
            [
                Row("seed", 1975, 0.10), Row("close", 1976, 0.12),
                Row("loud", 1976, 0.95), Row("jazz", 1976, 0.12), Row("later", 2015, 0.12),
            ],
            new Dictionary<string, string>
            {
                ["seed"] = "Rock", ["close"] = "Rock", ["loud"] = "Rock", ["jazz"] = "Jazz", ["later"] = "Rock",
            });
        var seed = crate.Find("seed")!;

        var close = SongRadio.Apart(crate.Find("close")!, seed);
        Assert.True(close < SongRadio.Apart(crate.Find("loud")!, seed));
        Assert.True(close < SongRadio.Apart(crate.Find("jazz")!, seed));
        Assert.True(close < SongRadio.Apart(crate.Find("later")!, seed));
    }

    [Fact]
    public void A_related_genre_is_closer_than_an_unrelated_one()
    {
        var crate = Crate(
            [Row("seed"), Row("metal"), Row("classical")],
            new Dictionary<string, string> { ["seed"] = "Rock", ["metal"] = "Metal", ["classical"] = "Classical" });
        var seed = crate.Find("seed")!;

        Assert.True(SongRadio.Apart(crate.Find("metal")!, seed) < SongRadio.Apart(crate.Find("classical")!, seed));
    }

    [Fact]
    public void Two_songs_in_the_same_mood_list_are_closer_than_two_that_share_none()
    {
        var calm = new HashSet<SmartPlaylistKind> { SmartPlaylistKind.Calm };
        var energetic = new HashSet<SmartPlaylistKind> { SmartPlaylistKind.Energetic };
        var crate = Crate(
            [Row("seed"), Row("calm"), Row("energetic")],
            moods: new Dictionary<string, IReadOnlySet<SmartPlaylistKind>>
            {
                ["seed"] = calm, ["calm"] = calm, ["energetic"] = energetic,
            });
        var seed = crate.Find("seed")!;

        Assert.True(SongRadio.MoodApart(crate.Find("calm")!, seed) < SongRadio.MoodApart(crate.Find("energetic")!, seed));
    }

    [Fact]
    public void An_unmeasured_song_is_judged_by_genre_and_era_and_counts_as_neither_close_nor_far_in_mood()
    {
        var crate = Crate(
            [Row("seed", sound: null), Row("same", sound: null), Row("other", sound: 0.9)],
            new Dictionary<string, string> { ["seed"] = "Rock", ["same"] = "Rock", ["other"] = "Jazz" });
        var seed = crate.Find("seed")!;

        Assert.Equal(SongRadio.Unknown, SongRadio.MoodApart(crate.Find("same")!, seed));
        Assert.True(SongRadio.Apart(crate.Find("same")!, seed) < SongRadio.Apart(crate.Find("other")!, seed));
    }

    [Fact]
    public void An_undated_song_counts_as_neither_close_nor_far_in_era()
    {
        Assert.Equal(SongRadio.Unknown, SongRadio.EraApart(0, 1975));
        Assert.Equal(0, SongRadio.EraApart(1975, 1975));
        Assert.Equal(1, SongRadio.EraApart(1960, 2020));
    }

    [Fact]
    public void A_doubled_or_halved_tempo_counts_as_the_same_tempo()
    {
        Assert.Equal(0, SongRadio.TempoApart(100, 200), 9);
        Assert.Equal(0, SongRadio.TempoApart(140, 70), 9);
        Assert.Equal(1, SongRadio.TempoApart(100, 141));
    }

    [Fact]
    public void A_missing_tempo_counts_as_neither_close_nor_far()
    {
        Assert.Equal(SongRadio.Unknown, SongRadio.TempoApart(null, 120));
        Assert.Equal(SongRadio.Unknown, SongRadio.TempoApart(120, 0));
    }

    [Fact]
    public void Tempo_tips_two_songs_that_are_otherwise_alike_but_does_not_outweigh_genre()
    {
        var crate = Crate(
            [
                Row("seed", bpm: 120), Row("same tempo", bpm: 121), Row("other tempo", bpm: 95),
                Row("other genre, same tempo", bpm: 120),
            ],
            new Dictionary<string, string>
            {
                ["seed"] = "Rock", ["same tempo"] = "Rock", ["other tempo"] = "Rock",
                ["other genre, same tempo"] = "Classical",
            });
        var seed = crate.Find("seed")!;

        Assert.True(SongRadio.Apart(crate.Find("same tempo")!, seed) < SongRadio.Apart(crate.Find("other tempo")!, seed));
        Assert.True(SongRadio.Apart(crate.Find("other tempo")!, seed)
                    < SongRadio.Apart(crate.Find("other genre, same tempo")!, seed));
    }

    [Fact]
    public void Between_two_songs_equally_close_to_the_one_playing_the_one_nearer_the_start_of_the_radio_wins()
    {
        // Inside EraSpan of the origin, or both would sit at the cap and tie.
        var crate = Crate([Row("origin", 1980), Row("playing", 1990), Row("earlier", 1986), Row("later", 1994)]);
        var playing = crate.Find("playing")!;
        var origin = crate.Find("origin")!;

        Assert.Equal(
            SongRadio.Apart(crate.Find("earlier")!, playing), SongRadio.Apart(crate.Find("later")!, playing), 9);
        Assert.True(
            SongRadio.Score(crate.Find("earlier")!, playing, origin) < SongRadio.Score(crate.Find("later")!, playing, origin));
    }

    [Fact]
    public void A_far_song_is_not_picked_while_a_shortlist_of_close_ones_is_left()
    {
        var close = Enumerable.Range(0, SongRadio.Shortlist).Select(i => Row($"close{i}", 1975, 0.5)).ToList();
        var crate = Crate(
            [Row("seed", 1975, 0.5), .. close, Row("far", 2020, 0.99)],
            new Dictionary<string, string>(close.ToDictionary(r => r.Folder, _ => "Rock"))
            {
                ["seed"] = "Rock", ["far"] = "Classical",
            });

        for (var random = 0; random < 20; random++)
            Assert.DoesNotContain("far", Pick(crate, "seed", 1, random: random));
    }

    // ── styles ──

    [Fact]
    public void On_one_shelf_a_song_of_the_same_style_comes_before_an_unfiled_one_and_that_before_another_style()
    {
        var crate = Crate(
            [
                Row("seed", subgenre: "Trip Hop"), Row("trip hop", subgenre: "Trip Hop"), Row("unfiled"),
                Row("electro", subgenre: "Electro"),
            ],
            AllOn("Electronic / Dance", "seed", "trip hop", "unfiled", "electro"));
        var seed = crate.Find("seed")!;

        var same = SongRadio.Apart(crate.Find("trip hop")!, seed);
        var unfiled = SongRadio.Apart(crate.Find("unfiled")!, seed);
        Assert.True(same < unfiled);
        Assert.True(unfiled < SongRadio.Apart(crate.Find("electro")!, seed));
    }

    [Fact]
    public void Kin_styles_are_closer_than_styles_with_nothing_in_common()
    {
        var crate = Crate(
            [Row("lo-fi", subgenre: "Lo-fi Beats"), Row("downtempo", subgenre: "Downtempo"), Row("electro", subgenre: "Electro")],
            AllOn("Electronic / Dance", "lo-fi", "downtempo", "electro"));
        var seed = crate.Find("lo-fi")!;

        Assert.Equal(SongRadio.KinStyle, SongRadio.GenreApart(crate.Find("downtempo")!, seed, new HashSet<string>()));
        Assert.Equal(SongRadio.OtherStyle, SongRadio.GenreApart(crate.Find("electro")!, seed, new HashSet<string>()));
    }

    [Fact]
    public void Styles_with_nothing_in_common_on_one_shelf_are_further_apart_than_two_related_shelves()
    {
        Assert.True(SongRadio.OtherStyle > SongRadio.RelatedGenre);
    }

    [Fact]
    public void A_radio_on_trip_hop_does_not_pick_electro_while_trip_hop_is_left()
    {
        var chill = Enumerable.Range(0, SongRadio.Shortlist + 3).Select(i => Row($"chill{i}", subgenre: "Trip Hop")).ToList();
        var club = Enumerable.Range(0, SongRadio.Shortlist).Select(i => Row($"club{i}", subgenre: "Electro")).ToList();
        var crate = Crate(
            [Row("seed", subgenre: "Trip Hop"), .. chill, .. club],
            AllOn("Electronic / Dance", ["seed", .. chill.Select(r => r.Folder), .. club.Select(r => r.Folder)]));

        for (var random = 0; random < 20; random++)
            Assert.All(Pick(crate, "seed", 3, random: random), path => Assert.StartsWith("chill", path));
    }

    [Fact]
    public void Styles_are_read_from_every_entry_of_a_listing_tag_and_from_the_record()
    {
        var crate = Crate(
            [Row("a", genreTag: "classical, romantic classical, classical period"), Row("b", folder: "rec")],
            AllOn("Classical", "a", "rec"),
            styles: new Dictionary<string, IReadOnlyList<string>> { ["rec"] = ["Baroque", "Not A Style RPM Knows"] });

        Assert.Equal(["Classical Period", "Romantic Classical"], crate.Find("a")!.Styles.Order());
        Assert.Equal(["Baroque"], crate.Find("b")!.Styles);
    }

    [Fact]
    public void A_record_on_no_shelf_takes_the_shelf_its_style_hangs_off()
    {
        var crate = Crate([Row("a", subgenre: "Trip Hop")], new Dictionary<string, string>());

        Assert.Equal("Electronic / Dance", crate.Find("a")!.Genre);
    }

    [Fact]
    public void Every_style_in_a_family_is_a_style_rpm_knows()
    {
        var known = RPM.Core.Metadata.Subgenres.All.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

        Assert.All(SongRadio.StyleFamilies.SelectMany(f => f), style => Assert.Contains(style, known));
    }

    // ── classical ──

    [Fact]
    public void Between_two_classical_records_the_recording_year_is_not_an_era()
    {
        var crate = Crate(
            [Row("mozart", 1987), Row("bach", 2000), Row("pop", 1987)],
            new Dictionary<string, string> { ["mozart"] = "Classical", ["bach"] = "Classical", ["pop"] = "Pop" });
        var seed = crate.Find("mozart")!;

        Assert.Equal(SongRadio.Apart(seed, seed), SongRadio.Apart(crate.Find("bach")!, seed), 9);
        Assert.True(SongRadio.Apart(crate.Find("bach")!, seed) < SongRadio.Apart(crate.Find("pop")!, seed));
    }

    [Fact]
    public void A_classical_radio_picks_classical_records_and_keeps_to_the_period()
    {
        var baroque = Enumerable.Range(0, SongRadio.Shortlist + 4).Select(i => Row($"baroque{i}", 1990 + i, subgenre: "Baroque")).ToList();
        var romantic = Enumerable.Range(0, SongRadio.Shortlist).Select(i => Row($"romantic{i}", 1990 + i, subgenre: "Romantic Classical")).ToList();
        var pop = Enumerable.Range(0, SongRadio.Shortlist).Select(i => Row($"pop{i}", 1987)).ToList();
        var genres = AllOn("Classical", ["seed", .. baroque.Select(r => r.Folder), .. romantic.Select(r => r.Folder)]);
        foreach (var row in pop)
            genres[row.Folder] = "Pop";
        var crate = Crate([Row("seed", 1987, subgenre: "Baroque"), .. baroque, .. romantic, .. pop], genres);

        for (var random = 0; random < 20; random++)
            Assert.All(Pick(crate, "seed", 4, random: random), path => Assert.StartsWith("baroque", path));
    }

    // ── favourites ──

    /// <summary>Ten songs closest, then the favourite, then a crowd further off in era. Enough of a crowd that the
    /// favourite is inside <see cref="SongRadio.FavouriteFit"/> of the ranking without being in the closest
    /// shortlist, so only its turn can bring it.</summary>
    private static RadioCrate FavouriteCrate() => Crate(
    [
        Row("seed", 1975, 0.5),
        .. Enumerable.Range(0, 10).Select(i => Row($"near{i}", 1975, 0.5)),
        Row("favourite", 1977, 0.5, plays: 50),
        .. Enumerable.Range(0, 300).Select(i => Row($"crowd{i}", 1995, 0.5)),
    ]);

    [Fact]
    public void Every_fifth_song_is_a_favourite_that_fits()
    {
        var crate = FavouriteCrate();

        // The radio has queued the seed and four songs, so the next is the fifth after the seed.
        IReadOnlyList<string> history = ["seed", "near0", "near1", "near2", "near3"];
        for (var random = 0; random < 10; random++)
            Assert.Equal("favourite", Pick(crate, "near3", 1, history: history, origin: "seed", random: random)[0]);
    }

    [Fact]
    public void A_favourite_waits_for_its_turn()
    {
        var crate = FavouriteCrate();

        IReadOnlyList<string> history = ["seed", "near0"];
        var picked = Pick(crate, "near0", 4, history: history, origin: "seed", random: 1);

        Assert.DoesNotContain("favourite", picked.Take(3));
        Assert.Equal("favourite", picked[3]);
    }

    [Fact]
    public void A_favourite_that_does_not_fit_is_not_played()
    {
        var close = Enumerable.Range(0, 40).Select(i => Row($"close{i}", 1975, 0.5)).ToList();
        var genres = AllOn("Rock", ["seed", .. close.Select(r => r.Folder)]);
        genres["favourite"] = "Classical";
        var crate = Crate([Row("seed", 1975, 0.5), .. close, Row("favourite", 2020, 0.99, plays: 50)], genres);

        IReadOnlyList<string> history = ["seed", "close0", "close1", "close2", "close3"];
        for (var random = 0; random < 10; random++)
            Assert.DoesNotContain("favourite", Pick(crate, "close3", 6, history: history, origin: "seed", random: random));
    }

    [Fact]
    public void A_song_played_once_or_twice_is_not_a_favourite()
    {
        var crate = Crate([Row("tried", plays: RadioCrate.FavouriteLeastPlays - 1), Row("loved", plays: RadioCrate.FavouriteLeastPlays)]);

        Assert.False(crate.IsFavourite(crate.Find("tried")!));
        Assert.True(crate.IsFavourite(crate.Find("loved")!));
    }

    // ── what is never picked ──

    [Fact]
    public void A_song_in_the_queue_or_already_queued_by_the_radio_is_not_picked()
    {
        var crate = Crate([Row("seed"), Row("queued"), Row("played"), Row("fresh")]);

        var picked = Pick(crate, "seed", 3, history: ["played"], inQueue: new HashSet<string> { "seed", "queued" });

        Assert.Equal(["fresh"], picked);
    }

    [Fact]
    public void A_song_that_is_not_offered_is_never_picked_but_can_start_a_radio()
    {
        var crate = Crate([Row("interlude", offered: false), Row("song"), Row("other interlude", offered: false)]);

        Assert.Equal(["song"], Pick(crate, "interlude", 5));
    }

    [Fact]
    public void The_same_song_on_another_record_is_not_picked()
    {
        var crate = Crate(
            [
                Row("album/1", artist: "Band", folder: "album", title: "Song"),
                Row("remaster/1", artist: "Band", folder: "remaster", title: "Song (2011 Remaster)"),
                Row("hits/7", artist: "Band", folder: "hits", title: "Song"),
                Row("other", artist: "Band", folder: "other", title: "Another Song"),
            ]);

        Assert.Equal(["other"], Pick(crate, "album/1", 5));
    }

    [Fact]
    public void Two_copies_of_one_song_are_not_picked_together()
    {
        var crate = Crate(
            [
                Row("seed"),
                Row("a/1", artist: "Band", folder: "a", title: "Song"),
                Row("b/1", artist: "Band", folder: "b", title: "Song (Live)"),
            ]);

        Assert.Single(Pick(crate, "seed", 5));
    }

    [Fact]
    public void A_radio_whose_seed_and_origin_are_both_unknown_picks_nothing()
    {
        var crate = Crate([Row("a"), Row("b")]);

        Assert.Empty(Pick(crate, "gone", 5, origin: "also gone"));
    }

    [Fact]
    public void A_seed_that_has_left_the_crate_is_replaced_by_the_song_the_radio_started_from()
    {
        var crate = Crate([Row("origin"), Row("next")]);

        Assert.Equal(["next"], Pick(crate, "gone", 5, origin: "origin"));
    }

    // ── spacing ──

    [Fact]
    public void An_act_does_not_come_back_until_others_have_played()
    {
        var same = Enumerable.Range(0, 6).Select(i => Row($"same{i}", 1975, 0.5, artist: "Band"));
        var others = Enumerable.Range(0, 6).Select(i => Row($"other{i}", 1980, 0.6));
        var crate = Crate([Row("seed", 1975, 0.5, artist: "Band"), .. same, .. others]);

        var picked = Pick(crate, "seed", SongRadio.ArtistGap);

        Assert.All(picked, path => Assert.StartsWith("other", path));
    }

    [Fact]
    public void A_record_does_not_come_back_until_others_have_played()
    {
        var album = Enumerable.Range(0, 6).Select(i => Row($"album/{i}", 1975, 0.5, folder: "album"));
        var others = Enumerable.Range(0, 6).Select(i => Row($"other{i}", 1985, 0.6));
        var crate = Crate([Row("album/seed", 1975, 0.5, folder: "album"), .. album, .. others]);

        var picked = Pick(crate, "album/seed", 6);

        Assert.All(picked, path => Assert.StartsWith("other", path));
    }

    [Fact]
    public void Spacing_gives_way_when_one_act_is_all_there_is()
    {
        var crate = Crate(Enumerable.Range(0, 5).Select(i => Row($"song{i}", artist: "Band", folder: "album")));

        Assert.Equal(4, Pick(crate, "song0", 4).Count);
    }

    // ── a radio that has played everything ──

    [Fact]
    public void When_every_song_has_been_queued_the_older_half_of_the_history_comes_back()
    {
        var crate = Crate(Enumerable.Range(0, 9).Select(i => Row($"s{i}")));
        IReadOnlyList<string> history = ["s0", "s1", "s2", "s3", "s4", "s5", "s6", "s7", "s8"];

        var picked = Pick(crate, "s8", 2, history: history, inQueue: new HashSet<string> { "s8" });

        Assert.Equal(2, picked.Count);
        Assert.All(picked, path => Assert.Contains(path, (string[])["s0", "s1", "s2", "s3"]));
    }

    [Fact]
    public void One_random_seed_gives_one_answer()
    {
        var crate = Crate(Enumerable.Range(0, 30).Select(i => Row($"s{i}", 1960 + i, i / 30.0)));

        Assert.Equal(Pick(crate, "s10", 8, random: 3), Pick(crate, "s10", 8, random: 3));
    }

    // ── the crate ──

    [Fact]
    public void Readings_become_places_among_the_measured_songs_and_ties_share_one()
    {
        var crate = Crate(
            [
                new RadioRow("low", "low", "", "", 0, 1, 1, 1, true, null),
                new RadioRow("tie1", "tie1", "", "", 0, 2, 2, 2, true, null),
                new RadioRow("tie2", "tie2", "", "", 0, 2, 2, 2, true, null),
                new RadioRow("high", "high", "", "", 0, 9, 9, 9, true, null),
                new RadioRow("silent", "silent", "", "", 0, 0, 0, 0, true, null),
                new RadioRow("unmeasured", "unmeasured", "", "", 0, null, null, null, true, null),
            ]);

        Assert.Equal(0, crate.Find("low")!.Sound!.Value.Brightness);
        Assert.Equal(0.5, crate.Find("tie1")!.Sound!.Value.Brightness);
        Assert.Equal(0.5, crate.Find("tie2")!.Sound!.Value.Brightness);
        Assert.Equal(1, crate.Find("high")!.Sound!.Value.Brightness);
        Assert.Null(crate.Find("silent")!.Sound);
        Assert.Null(crate.Find("unmeasured")!.Sound);
    }

    [Fact]
    public void The_crate_offers_songs_and_holds_back_interludes_and_unmeasured_songs_have_no_sound()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rpmradio-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var db = new LibraryDatabase(MirrorLocation.InFolder(root));
            db.Upsert(Track("a/song.flac", "a", 240, "Band", "Song", 1977));
            db.Upsert(Track("a/intro.flac", "a", 30, "Band", "Intro", 1977));
            db.Upsert(Track("b/other.flac", "b", 200, "Other", "Other", 0));
            db.SaveTrackMood("a/song.flac", 100, 1, new MoodReading(0.3, 4, 12));

            var crate = db.ReadRadioCrate();

            var song = crate.Find("a/song.flac")!;
            Assert.True(song.Offered);
            Assert.NotNull(song.Sound);
            Assert.Equal("Band", song.Artist);
            Assert.Equal(1977, song.Year);

            Assert.False(crate.Find("a/intro.flac")!.Offered);

            var other = crate.Find("b/other.flac")!;
            Assert.True(other.Offered);
            Assert.Null(other.Sound);
            Assert.Equal(0, other.Year);
            Assert.Null(other.Bpm);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        static LibraryTrack Track(string path, string folder, double seconds, string artist, string title, uint year) => new()
        {
            RelativePath = path,
            Folder = folder,
            FileName = Path.GetFileName(path),
            Extension = ".flac",
            SizeBytes = 100,
            ModifiedUtcTicks = 1,
            DurationSeconds = seconds,
            Artist = artist,
            Title = title,
            Year = year,
        };
    }

    [Fact]
    public void The_crate_takes_the_tempo_from_the_tag_where_the_analyser_has_nothing_confident()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rpmradio-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var db = new LibraryDatabase(MirrorLocation.InFolder(root));
            db.Upsert(Track("tagged.flac", 98));
            db.Upsert(Track("untagged.flac", 0));

            var crate = db.ReadRadioCrate();

            Assert.Equal(98, crate.Find("tagged.flac")!.Bpm);
            Assert.Null(crate.Find("untagged.flac")!.Bpm);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        static LibraryTrack Track(string path, double tagBpm) => new()
        {
            RelativePath = path,
            Folder = "",
            FileName = path,
            Extension = ".flac",
            SizeBytes = 100,
            ModifiedUtcTicks = 1,
            DurationSeconds = 200,
            TagBpm = tagBpm,
        };
    }
}
