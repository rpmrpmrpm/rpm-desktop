using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The roll-ups over the listening log: what kinds of music get played at what time of day, what gets
/// played on which day of the week, and who and what gets played most.
///
/// <para>Two halves. The arithmetic is tested against plain rows with no mirror on the disk, the same
/// arrangement <c>MoodShelfTests</c> uses, because that is where the mistakes are. Then the reads are
/// tested end to end, because a group-by that gathers the wrong column is a mistake no amount of
/// arithmetic testing can see.</para>
/// </summary>
public class ListeningProfileTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    /// <summary>Fixed local clocks, so "half past eleven at night" and "a Saturday" mean the same thing
    /// in every run and on every machine the tests run on. SaturdayNight is a Saturday and
    /// TuesdayMorning is a Tuesday.</summary>
    private static readonly DateTimeOffset SaturdayNight =
        new(2026, 8, 22, 23, 30, 0, TimeSpan.FromHours(2));

    private static readonly DateTimeOffset TuesdayMorning =
        new(2026, 8, 25, 8, 15, 0, TimeSpan.FromHours(2));

    /// <summary>Later than either, so a window measured back from here holds both.</summary>
    private static readonly long Now = new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero).UtcTicks;

    public ListeningProfileTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmprofile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    // ── where the day is cut ──

    [Theory]
    [InlineData(23, DayPart.Night)]
    [InlineData(0, DayPart.Night)]
    [InlineData(4, DayPart.Night)]
    [InlineData(5, DayPart.Morning)]
    [InlineData(11, DayPart.Morning)]
    [InlineData(12, DayPart.Afternoon)]
    [InlineData(17, DayPart.Afternoon)]
    [InlineData(18, DayPart.Evening)]
    [InlineData(22, DayPart.Evening)]
    public void The_hour_lands_in_the_part_of_the_day_it_belongs_to(int hour, DayPart part)
    {
        Assert.Equal(part, DayParts.Of(hour));
    }

    /// <summary>Night is the one part that wraps midnight, and the edges are the listening rules' own,
    /// the same hours "Late night" means by night. If those move, this is what says so.</summary>
    [Fact]
    public void Night_is_the_same_night_the_late_night_list_means()
    {
        Assert.Equal(SmartPlaylists.NightFromHour, DayParts.NightFrom);
        Assert.Equal(SmartPlaylists.NightUntilHour, DayParts.NightUntil);
    }

    [Fact]
    public void An_hour_that_is_not_an_hour_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DayParts.Of(24));
        Assert.Throws<ArgumentOutOfRangeException>(() => DayParts.Of(-1));
    }

    /// <summary>Every one of the four is reachable, and the day is covered exactly once, no hour in two
    /// parts, no hour in none.</summary>
    [Fact]
    public void The_four_parts_cover_the_day_between_them()
    {
        var seen = Enumerable.Range(0, 24).Select(DayParts.Of).ToList();

        Assert.Equal(24, seen.Count);
        Assert.Equal(4, seen.Distinct().Count());
        Assert.Equal([.. DayParts.AcrossTheDay.Order()], [.. seen.Distinct().Order()]);
    }

    // ── the arithmetic, against plain rows ──

    private static ListenedRecord Record(
        string folder, int hour, DayOfWeek day, bool counted, int listens, double seconds = 200) =>
        new(folder, $"{folder} the record", $"{folder} the band",
            hour, (int)day, counted, listens, seconds, LastAtUtcTicks: hour);

    private static ListeningProfile Built(
        IReadOnlyList<ListenedRecord> records,
        IReadOnlyList<ListenedCredit>? credits = null,
        IReadOnlyDictionary<string, string>? genres = null,
        ArtistCanonicalizer? artists = null) =>
        ListeningProfile.Build(
            ListeningWindow.Everything,
            records,
            credits ?? [],
            genres ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            artists ?? ArtistCanonicalizer.Empty);

    [Fact]
    public void The_shares_of_one_part_of_the_day_add_up_to_one()
    {
        var genres = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["jazz-record"] = "Jazz",
            ["rock-record"] = "Rock",
        };

        var profile = Built(
            [
                Record("jazz-record", hour: 23, DayOfWeek.Saturday, counted: true, listens: 30),
                Record("rock-record", hour: 23, DayOfWeek.Saturday, counted: true, listens: 10),
            ],
            genres: genres);

        var night = profile.GenresAt(DayPart.Night);

        // Most played first, and the shares are of the genred plays in that part.
        Assert.Equal(["Jazz", "Rock"], night.Select(g => g.Genre));
        Assert.Equal(0.75, night[0].Share, 6);
        Assert.Equal(0.25, night[1].Share, 6);
        Assert.Equal(1.0, night.Sum(g => g.Share), 6);
    }

    /// <summary>A record whose tags name no genre the app recognises is not a kind of music, so it is
    /// out of the genre roll-up, and it WAS listened to, so the part of the day it happened in is
    /// bigger for it. Both halves, because either one alone is a different bug.</summary>
    [Fact]
    public void A_record_the_app_cannot_file_is_out_of_the_genres_and_still_in_the_totals()
    {
        var genres = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["jazz-record"] = "Jazz",
        };

        var profile = Built(
            [
                Record("jazz-record", hour: 23, DayOfWeek.Saturday, counted: true, listens: 30),
                Record("untagged", hour: 23, DayOfWeek.Saturday, counted: true, listens: 70),
            ],
            genres: genres);

        Assert.Equal(["Jazz"], profile.GenresAt(DayPart.Night).Select(g => g.Genre));

        // A share of its own genred plays, not of everything played, so the one genre that IS filed
        // reads as all of what could be filed rather than as a third of the evening.
        Assert.Equal(1.0, profile.GenresAt(DayPart.Night)[0].Share, 6);

        Assert.Equal(100, profile.At(DayPart.Night).Plays);
        Assert.Equal(100, profile.Whole.Plays);
    }

    /// <summary>A skip is in the tally and out of the share. What somebody keeps skipping is taste, and
    /// it is the opposite of the taste a share is being read as.</summary>
    [Fact]
    public void A_skip_is_counted_but_is_not_a_preference()
    {
        var genres = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["jazz-record"] = "Jazz",
            ["rock-record"] = "Rock",
        };

        var profile = Built(
            [
                Record("jazz-record", hour: 23, DayOfWeek.Saturday, counted: true, listens: 10),
                Record("rock-record", hour: 23, DayOfWeek.Saturday, counted: false, listens: 40),
            ],
            genres: genres);

        var night = profile.GenresAt(DayPart.Night);

        Assert.Equal("Jazz", night[0].Genre);
        Assert.Equal(1.0, night[0].Share, 6);

        var rock = night.Single(g => g.Genre == "Rock");
        Assert.Equal(0, rock.Share);
        Assert.Equal(0, rock.Tally.Plays);
        Assert.Equal(40, rock.Tally.Skips);
        Assert.Equal(50, profile.At(DayPart.Night).Listens);
    }

    /// <summary>The weekday column is written on every listen (since v43). This reads it back.</summary>
    [Fact]
    public void The_weekday_is_read_back_as_the_weekday_it_was_written_as()
    {
        var profile = Built(
        [
            Record("a", hour: 23, DayOfWeek.Saturday, counted: true, listens: 30),
            Record("a", hour: 8, DayOfWeek.Tuesday, counted: true, listens: 5),
        ]);

        Assert.Equal(30, profile.On(DayOfWeek.Saturday).Plays);
        Assert.Equal(5, profile.On(DayOfWeek.Tuesday).Plays);
        Assert.Equal(0, profile.On(DayOfWeek.Wednesday).Plays);
    }

    [Fact]
    public void A_weekday_that_is_not_a_weekday_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Built(
            [new ListenedRecord("a", null, null, 12, 7, true, 1, 200, 1)]));
    }

    /// <summary>One band spelled four ways in the tags is one act here. Without this every one of them
    /// carries a quarter of the plays and none of them is in anybody's top ten, which is the whole
    /// reason ArtistCanonicalizer exists.</summary>
    [Fact]
    public void Two_spellings_of_one_act_are_one_line()
    {
        var artists = ArtistCanonicalizer.From(["Sigur Rós"]);

        var profile = Built(
            [],
            credits:
            [
                new ListenedCredit("Sigur Rós", Counted: true, Listens: 12, Seconds: 2400, LastAtUtcTicks: 20),
                new ListenedCredit("Sigur Ros", Counted: true, Listens: 8, Seconds: 1600, LastAtUtcTicks: 40),
            ],
            artists: artists);

        var act = Assert.Single(profile.Artists);
        Assert.Equal("Sigur Rós", act.Name);
        Assert.Equal(20, act.Tally.Plays);

        // The most recent listen of either spelling, so "who are they into now" is answerable off one
        // line rather than off whichever spelling happened to be used last.
        Assert.Equal(40, act.LastAtUtcTicks);
    }

    /// <summary>A credit nothing carries is not an act called nothing.</summary>
    [Fact]
    public void A_listen_with_no_credit_on_it_is_nobody()
    {
        var profile = Built(
            [],
            credits: [new ListenedCredit("   ", Counted: true, Listens: 9, Seconds: 100, LastAtUtcTicks: 1)]);

        Assert.Empty(profile.Artists);
    }

    /// <summary>Nothing listened to answers safely everywhere, so no caller has to test for it first.</summary>
    [Fact]
    public void A_listener_who_has_played_nothing_answers_everywhere()
    {
        var profile = ListeningProfile.Nothing;

        Assert.Equal(ListeningTally.None, profile.Whole);
        Assert.Equal(ListeningTally.None, profile.At(DayPart.Night));
        Assert.Equal(ListeningTally.None, profile.On(DayOfWeek.Friday));
        Assert.Empty(profile.GenresAt(DayPart.Evening));
        Assert.Empty(profile.GenresOn(DayOfWeek.Monday));
        Assert.Empty(profile.Artists);
        Assert.Empty(profile.Albums);
    }

    // ── and end to end, through the reads ──

    private void Add(string relativePath, string genre, string artist, string album)
    {
        var slash = relativePath.LastIndexOf('/');
        _db.Upsert(new LibraryTrack
        {
            RelativePath = relativePath,
            Folder = slash < 0 ? "" : relativePath[..slash],
            FileName = slash < 0 ? relativePath : relativePath[(slash + 1)..],
            Extension = Path.GetExtension(relativePath),
            SizeBytes = 5_000_000,
            ModifiedUtcTicks = 1,
            DurationSeconds = 200,
            Title = Path.GetFileNameWithoutExtension(relativePath),
            Genre = genre,
            Artist = artist,
            AlbumArtist = artist,
            Album = album,
        });
    }

    private void Played(DateTimeOffset when, string path, int times)
    {
        for (int i = 0; i < times; i++)
            _db.NoteListen(ListenEntry.At(
                when.AddMinutes(i), path, heardSeconds: 200, lengthSeconds: 200)!);
    }

    /// <summary>
    /// The whole path, end to end: listens go in at known hours on known weekdays, and the profile comes
    /// back saying what this person plays at night and what they play in the morning. This is the join
    /// through <c>local_hour</c> and the fold through the folder's genre, neither of which the
    /// arithmetic tests above can reach.
    /// </summary>
    [Fact]
    public void What_gets_played_at_night_is_not_what_gets_played_in_the_morning()
    {
        Add("jazz/01.flac", "Jazz", "Bill Evans", "Waltz for Debby");
        Add("rock/01.flac", "Rock", "Television", "Marquee Moon");

        Played(SaturdayNight, "jazz/01.flac", times: 6);
        Played(TuesdayMorning, "rock/01.flac", times: 4);

        var profile = _db.ReadListeningProfile(ListeningWindow.Everything, Now);

        Assert.Equal("Jazz", profile.GenresAt(DayPart.Night).Single().Genre);
        Assert.Equal("Rock", profile.GenresAt(DayPart.Morning).Single().Genre);
        Assert.Empty(profile.GenresAt(DayPart.Afternoon));

        Assert.Equal(6, profile.At(DayPart.Night).Plays);
        Assert.Equal(4, profile.At(DayPart.Morning).Plays);
        Assert.Equal(10, profile.Whole.Plays);
    }

    /// <summary>And the same rows read along the other axis. The weekday is stamped in the listener's
    /// own clock, so a listen at half past eleven on a Saturday night is a Saturday here whatever the
    /// machine reading it is set to.</summary>
    [Fact]
    public void The_week_is_read_off_the_same_listens_as_the_day()
    {
        Add("jazz/01.flac", "Jazz", "Bill Evans", "Waltz for Debby");
        Add("rock/01.flac", "Rock", "Television", "Marquee Moon");

        Played(SaturdayNight, "jazz/01.flac", times: 6);
        Played(TuesdayMorning, "rock/01.flac", times: 4);

        var profile = _db.ReadListeningProfile(ListeningWindow.Everything, Now);

        Assert.Equal(6, profile.On(DayOfWeek.Saturday).Plays);
        Assert.Equal(4, profile.On(DayOfWeek.Tuesday).Plays);
        Assert.Equal(0, profile.On(DayOfWeek.Sunday).Listens);
        Assert.Equal("Jazz", profile.GenresOn(DayOfWeek.Saturday).Single().Genre);
    }

    [Fact]
    public void The_acts_and_the_records_come_back_most_played_first()
    {
        Add("jazz/01.flac", "Jazz", "Bill Evans", "Waltz for Debby");
        Add("rock/01.flac", "Rock", "Television", "Marquee Moon");

        Played(SaturdayNight, "jazz/01.flac", times: 6);
        Played(TuesdayMorning, "rock/01.flac", times: 4);

        var profile = _db.ReadListeningProfile(ListeningWindow.Everything, Now);

        Assert.Equal(["Bill Evans", "Television"], profile.Artists.Select(a => a.Name));
        Assert.Equal([6, 4], profile.Artists.Select(a => a.Tally.Plays));

        Assert.Equal(["jazz", "rock"], profile.Albums.Select(a => a.Folder));
        Assert.Equal("Waltz for Debby", profile.Albums[0].Title);
        Assert.Equal("Bill Evans", profile.Albums[0].Artist);
        Assert.Equal(6, profile.Albums[0].Tally.Plays);
    }

    /// <summary>A window is what makes this a description of who somebody is NOW rather than of
    /// everything they have ever done, so what falls behind it has to actually be left out.</summary>
    [Fact]
    public void A_window_leaves_out_what_is_behind_it()
    {
        Add("jazz/01.flac", "Jazz", "Bill Evans", "Waltz for Debby");
        Add("rock/01.flac", "Rock", "Television", "Marquee Moon");

        // A year before the rest, which is inside the log's retention and outside a month's window.
        Played(SaturdayNight.AddYears(-1), "jazz/01.flac", times: 6);
        Played(TuesdayMorning, "rock/01.flac", times: 4);

        var everything = _db.ReadListeningProfile(ListeningWindow.Everything, Now);
        var lastMonth = _db.ReadListeningProfile(ListeningWindow.LastMonth, Now);

        Assert.Equal(10, everything.Whole.Plays);
        Assert.Equal(2, everything.Artists.Count);

        Assert.Equal(4, lastMonth.Whole.Plays);
        Assert.Equal("Television", Assert.Single(lastMonth.Artists).Name);
        Assert.Empty(lastMonth.GenresAt(DayPart.Night));

        // And the profile says which window it is a description of, so nothing downstream can lose the
        // one fact that makes the numbers mean something.
        Assert.Equal(ListeningWindow.LastMonth, lastMonth.Window);
    }

    /// <summary>A skip goes into the log and must not go into the plays. The two are one row apart in
    /// the same table, which is exactly how they get confused.</summary>
    [Fact]
    public void A_skip_reaches_the_profile_as_a_skip()
    {
        Add("rock/01.flac", "Rock", "Television", "Marquee Moon");

        // Long enough to be worth writing down, far too short to count.
        _db.NoteListen(ListenEntry.At(
            TuesdayMorning, "rock/01.flac", heardSeconds: 10, lengthSeconds: 200)!);

        var profile = _db.ReadListeningProfile(ListeningWindow.Everything, Now);

        Assert.Equal(0, profile.Whole.Plays);
        Assert.Equal(1, profile.Whole.Skips);
        Assert.Equal(0, profile.GenresAt(DayPart.Morning).Single().Share);
        Assert.Equal(1, Assert.Single(profile.Artists).Tally.Skips);
    }

    /// <summary>An empty log answers with a profile rather than with nothing, so nothing reading one has
    /// to handle a first evening as a special case.</summary>
    [Fact]
    public void A_mirror_nobody_has_listened_to_yet_still_answers()
    {
        var profile = _db.ReadListeningProfile(ListeningWindow.Everything, Now);

        Assert.Equal(0, profile.Whole.Listens);
        Assert.Empty(profile.Artists);
        Assert.Empty(profile.Albums);
        Assert.Empty(profile.GenresAt(DayPart.Night));
    }
}
