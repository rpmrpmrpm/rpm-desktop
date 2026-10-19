using RPM.Core.Analysis;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// How the mirror stores the arousal (mood) measurement, and how it avoids decoding a track twice.
///
/// <para>The measurement runs inside the loudness sweep, which goes through the whole collection one
/// track at a time, over as many launches as needed. These tests cover the two ways such a pass can
/// fail: returning the same track for ever because its result was never stored, and discarding a
/// result that cost a full decode.</para>
/// </summary>
public class LibraryDatabaseMoodTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public LibraryDatabaseMoodTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmmood-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static LibraryTrack Track(
        string path, long size = 100, long mtime = 1, double seconds = 240, string folder = "") => new()
    {
        RelativePath = path,
        Folder = folder,
        FileName = path,
        Extension = ".flac",
        SizeBytes = size,
        ModifiedUtcTicks = mtime,
        DurationSeconds = seconds,
    };

    [Fact]
    public void What_the_meter_found_survives_the_round_trip()
    {
        _db.Upsert(Track("a.flac"));
        _db.SaveTrackMood("a.flac", 100, 1, new MoodReading(0.31, 4.2, 12.5));

        var row = _db.QueryMeasuredMoods().Single();

        Assert.Equal("a.flac", row.RelativePath);
        Assert.Equal(0.31, row.Brightness, 5);
        Assert.Equal(4.2, row.Onsets, 5);
        Assert.Equal(12.5, row.Dynamics, 5);
    }

    // ── the generated lists that are built from the shelf rather than from the rows ──

    /// <summary>
    /// The full path, end to end: readings are stored, and a mood playlist returns the calm end and not
    /// the busy one. This tests the join through <c>json_each</c> that the shelf rule builds, which no
    /// other list kind uses.
    /// </summary>
    [Fact]
    public void A_mood_playlist_comes_back_off_the_shelf()
    {
        for (int i = 0; i < 100; i++)
        {
            // Start at one hundredth rather than zero: all three values at zero is the sentinel for
            // "measured, and there was nothing to describe", and QueryMeasuredMoods drops those rows.
            double at = (i + 1) / 100.0;
            string path = $"rock/{i:D3}.flac";
            // One album PER TRACK. A list holds one song from each album, so a hundred-track folder
            // would return one song and test nothing about the query. A fixture with two tracks per
            // album would change meaning whenever the per-album cap changes.
            _db.Upsert(Track(path, folder: $"rock-{i:D3}") with { Genre = "Rock" });
            _db.SaveTrackMood(path, 100, 1, new MoodReading(at, at * 10, at * 20));
        }

        var calm = _db.QuerySmartPlaylist(SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm), nowUtcTicks: 0, advance: true);
        var energetic = _db.QuerySmartPlaylist(SmartPlaylistId.WholeShelf(SmartPlaylistKind.Energetic), nowUtcTicks: 0, advance: true);

        Assert.NotEmpty(calm);
        Assert.NotEmpty(energetic);

        // The quietest track is in one list and the loudest in the other, and neither is in both.
        Assert.Contains(calm, t => t.RelativePath == "rock/000.flac");
        Assert.Contains(energetic, t => t.RelativePath == "rock/099.flac");
        Assert.DoesNotContain(calm, t => t.RelativePath == "rock/099.flac");
    }

    /// <summary>
    /// The list is returned in the order the rule built, not in table order. <c>json_each</c> numbers
    /// the array and the rule orders by that number; without it SQLite would return the rows in any
    /// order and the arrangement would be lost.
    ///
    /// <para><b>That order is not the shelf's ranking.</b> The rule builds the list with
    /// <c>MoodPool.InFlow</c>, which orders the chosen songs so that each sounds like the one before
    /// it (see <c>MoodFlow</c>). The test is about the SQL: whatever order the rule produces is kept by
    /// the query. So the expected list is built with the same method the rule uses, not from the
    /// ranked pool.</para>
    /// </summary>
    [Fact]
    public void A_mood_playlist_keeps_the_order_the_rule_composed()
    {
        for (int i = 0; i < 100; i++)
        {
            // Start at one hundredth rather than zero: all three values at zero is the sentinel for
            // "measured, and there was nothing to describe", and QueryMeasuredMoods drops those rows.
            double at = (i + 1) / 100.0;
            string path = $"rock/{i:D3}.flac";
            // One album PER TRACK. A list holds one song from each album, so a hundred-track folder
            // would return one song and test nothing about the query. A fixture with two tracks per
            // album would change meaning whenever the per-album cap changes.
            _db.Upsert(Track(path, folder: $"rock-{i:D3}") with { Genre = "Rock" });
            _db.SaveTrackMood(path, 100, 1, new MoodReading(at, at * 10, at * 20));
        }

        var calm = _db.QuerySmartPlaylist(SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm), nowUtcTicks: 0, advance: true);
        var got = calm.Select(t => t.RelativePath).ToList();

        // The window the call above used. MoodWindow(advance: true) returns the value it moved to, so
        // reading it again without advancing gives the same window the rule used.
        var window = _db.MoodWindow(SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm), advance: false);
        var expected = _db.SessionMoods.Calm.WholeList
            .Allowed(RecommendRefusals.None, SmartPlaylistKind.Calm)
            .InFlow(SmartPlaylists.MostSongs(SmartPlaylistKind.Calm), readings: 1, window, _db.MusicalKeysOf);

        Assert.NotEmpty(got);
        Assert.Equal(expected, got);

        // The same songs either way. The two MoodPool methods pick the same songs and differ only in
        // order. If the songs depended on the method, the pane's count and the pane's list would
        // disagree.
        //
        // A difference in ORDER is NOT asserted here, on purpose. This fixture is a smooth ramp with
        // one song per album, so the ranking is already in sound-alike order, and the flow order can
        // be identical. MoodFlowTests checks the reordering with a fixture that needs it.
        var ranked = _db.SessionMoods.Calm.WholeList
            .Allowed(RecommendRefusals.None, SmartPlaylistKind.Calm)
            .Window(SmartPlaylists.MostSongs(SmartPlaylistKind.Calm), window);

        Assert.Equal(ranked.OrderBy(p => p, StringComparer.Ordinal), got.OrderBy(p => p, StringComparer.Ordinal));
    }

    /// <summary>The count the pane shows and the list the row opens agree. Both are built from the
    /// same rule for this reason. A shelf rule binds two hundred paths where the other rules bind a
    /// time, so it is the case most likely to disagree.</summary>
    [Fact]
    public void The_subtitle_counts_what_the_row_opens()
    {
        for (int i = 0; i < 100; i++)
        {
            // Start at one hundredth rather than zero: all three values at zero is the sentinel for
            // "measured, and there was nothing to describe", and QueryMeasuredMoods drops those rows.
            double at = (i + 1) / 100.0;
            string path = $"rock/{i:D3}.flac";
            // One album PER TRACK. A list holds one song from each album, so a hundred-track folder
            // would return one song and test nothing about the query. A fixture with two tracks per
            // album would change meaning whenever the per-album cap changes.
            _db.Upsert(Track(path, folder: $"rock-{i:D3}") with { Genre = "Rock" });
            _db.SaveTrackMood(path, 100, 1, new MoodReading(at, at * 10, at * 20));
        }

        var list = _db.QuerySmartPlaylist(SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm), nowUtcTicks: 0, advance: true);
        var (tracks, seconds, _) = _db.SmartPlaylistTotals(SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm), nowUtcTicks: 0);

        Assert.Equal(list.Count, tracks);
        Assert.Equal(list.Sum(t => t.DurationSeconds), seconds, 3);
    }

    /// <summary>
    /// The set of readings is taken once per session and kept. A reading stored after the session has
    /// built its first list is NOT added to it. This is intended: a list that changed while the user
    /// did nothing could not be explained by anything on screen.
    /// </summary>
    [Fact]
    public void A_reading_that_lands_after_the_cut_waits_for_the_next_launch()
    {
        for (int i = 0; i < 100; i++)
        {
            // Start at one hundredth rather than zero: all three values at zero is the sentinel for
            // "measured, and there was nothing to describe", and QueryMeasuredMoods drops those rows.
            double at = (i + 1) / 100.0;
            string path = $"rock/{i:D3}.flac";
            // One album PER TRACK. A list holds one song from each album, so a hundred-track folder
            // would return one song and test nothing about the query. A fixture with two tracks per
            // album would change meaning whenever the per-album cap changes.
            _db.Upsert(Track(path, folder: $"rock-{i:D3}") with { Genre = "Rock" });
            _db.SaveTrackMood(path, 100, 1, new MoodReading(at, at * 10, at * 20));
        }

        int before = _db.QuerySmartPlaylist(SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm), nowUtcTicks: 0, advance: true).Count;

        // Measured now, and quieter than every track already on the shelf.
        _db.Upsert(Track("rock/late.flac", folder: "rock") with { Genre = "Rock" });
        _db.SaveTrackMood("rock/late.flac", 100, 1, new MoodReading(0, 0, 0.001));

        var after = _db.QuerySmartPlaylist(SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm), nowUtcTicks: 0, advance: true);

        Assert.Equal(before, after.Count);
        Assert.DoesNotContain(after, t => t.RelativePath == "rock/late.flac");
    }

    // ── whether a list can give a SECOND list, which the "generate a new list" button needs ──

    /// <summary>A list built from the track rows is complete: the ordering puts the best songs first
    /// and the limit ends it, with no deeper pool. Asking again returns the same songs, so the button
    /// must not be offered.</summary>
    [Fact]
    public void A_list_asked_of_the_track_rows_has_no_second_list_in_it()
    {
        Assert.False(_db.SmartPlaylistCanRegenerate(
            SmartPlaylistId.WholeShelf(SmartPlaylistKind.FavoriteTracks)));
        Assert.False(_db.SmartPlaylistCanRegenerate(
            SmartPlaylistId.WholeShelf(SmartPlaylistKind.MostPlayed)));
    }

    /// <summary>A mood list taken from a pool larger than itself can give another list. Two calls
    /// show it: the window moves on each one, so the second list has different songs, not the same
    /// songs in a different order.</summary>
    [Fact]
    public void A_mood_list_over_a_deeper_pool_hands_back_a_different_list()
    {
        // Six hundred tracks, one album per track. The calm band is a share of the genre and a list
        // holds one song from each album, so the pool size is the number of calm ALBUMS, and it must
        // be over fifty for a second list to exist. With several tracks per album the pool would
        // depend on the per-album cap, and the test would not be about pool depth.
        for (int i = 0; i < 600; i++)
        {
            double at = (i + 1) / 600.0;
            string path = $"rock/{i:D3}.flac";
            _db.Upsert(Track(path, folder: $"rock-{i:D3}") with { Genre = "Rock" });
            _db.SaveTrackMood(path, 100, 1, new MoodReading(at, at * 10, at * 20));
        }

        var calm = SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm);
        Assert.True(_db.SmartPlaylistCanRegenerate(calm));

        var first = _db.QuerySmartPlaylist(calm, nowUtcTicks: 0, advance: true).Select(t => t.RelativePath).ToList();
        var second = _db.QuerySmartPlaylist(calm, nowUtcTicks: 0, advance: true).Select(t => t.RelativePath).ToList();

        Assert.Equal(first.Count, second.Count);
        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// Refreshing one list does not change any other list.
    ///
    /// <para>Each list has its own window. With ONE window for the whole collection, refreshing the calm
    /// list would move every other mood list too. On the desktop that is hard to see, because only the
    /// opened list is visible. But the phone is sent every list (<c>MoodListPush.Build</c>), so one
    /// click of "generate a new list" would change every list there.</para>
    /// </summary>
    [Fact]
    public void Generating_one_mood_list_does_not_move_another()
    {
        Ramp(600);

        var calm = SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm);
        var busy = SmartPlaylistId.WholeShelf(SmartPlaylistKind.Energetic);

        // Both can give a new list; otherwise this test proves nothing.
        Assert.True(_db.SmartPlaylistCanRegenerate(calm));
        Assert.True(_db.SmartPlaylistCanRegenerate(busy));

        var before = Songs(busy, advance: false);
        Assert.NotEmpty(before);

        // Two clicks of the button on the calm list. That list changes...
        Assert.NotEqual(Songs(calm, advance: true), Songs(calm, advance: true));

        // ...and the other list is unchanged, song for song and in the same order.
        Assert.Equal(before, Songs(busy, advance: false));

        // It is not frozen: it changes when IT is the list being refreshed.
        Assert.NotEqual(before, Songs(busy, advance: true));
    }

    /// <summary>
    /// Reading a list does not create a new one.
    ///
    /// <para>Opening a mood list and playing from its row must not move the window. A list changes
    /// only on app start or when the refresh button is clicked. Otherwise the list would be a
    /// different fifty songs every time it was opened, and the songs that played would not be the
    /// songs on screen.</para>
    /// </summary>
    [Fact]
    public void Reading_a_mood_list_hands_back_the_list_that_is_there()
    {
        Ramp(600);

        var calm = SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm);
        Assert.True(_db.SmartPlaylistCanRegenerate(calm));

        var opened = Songs(calm, advance: false);
        Assert.NotEmpty(opened);

        // Opened again, played from its row, counted for a subtitle: the same songs every time.
        Assert.Equal(opened, Songs(calm, advance: false));
        Assert.Equal(opened, Songs(calm, advance: false));

        // The refresh button still gives a new list.
        Assert.NotEqual(opened, Songs(calm, advance: true));
    }

    /// <summary>
    /// Starting the app is the other event that gives a list different songs.
    ///
    /// <para>The window a list starts at is stored in the library (<c>mood_launch</c>), not counted in
    /// memory. In memory it would start at zero on every launch, and every app start would give the
    /// same fifty songs.</para>
    /// </summary>
    [Fact]
    public void Starting_the_app_again_hands_back_a_different_list()
    {
        Ramp(600);

        var calm = SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm);
        var firstLaunch = Songs(calm, advance: false);
        Assert.NotEmpty(firstLaunch);

        _db.Dispose();
        using var next = new LibraryDatabase(MirrorLocation.InFolder(_root));

        var secondLaunch = next
            .QuerySmartPlaylist(calm, nowUtcTicks: 0, advance: false)
            .Select(t => t.RelativePath)
            .ToList();

        Assert.Equal(firstLaunch.Count, secondLaunch.Count);
        Assert.NotEqual(firstLaunch, secondLaunch);
    }

    /// <summary>A ramp of measured albums, one song each. Both ends have a pool larger than the list
    /// taken from it, which a test about the window needs.</summary>
    private void Ramp(int records)
    {
        for (var i = 0; i < records; i++)
        {
            double at = (i + 1) / (double)records;
            string path = $"rock/{i:D3}.flac";
            _db.Upsert(Track(path, folder: $"rock-{i:D3}") with { Genre = "Rock" });
            _db.SaveTrackMood(path, 100, 1, new MoodReading(at, at * 10, at * 20));
        }
    }

    /// <summary>The songs a list holds, in order.</summary>
    private List<string> Songs(SmartPlaylistId id, bool advance) =>
        _db.QuerySmartPlaylist(id, nowUtcTicks: 0, advance).Select(t => t.RelativePath).ToList();

    /// <summary>A pool no larger than its list has nothing more to give: the window would wrap and
    /// return the same songs from a different start, so a "new list" would only be a rotation.</summary>
    [Fact]
    public void A_mood_list_that_is_its_whole_pool_has_no_second_list_in_it()
    {
        // One album per track, so the pool and the list are the same length (the test above relies on
        // the same fact). With two tracks per album, the one-song-per-album cap would drop half of the
        // calm songs, the pool would be twice the list, and the fixture would not test what it says.
        for (int i = 0; i < 40; i++)
        {
            double at = (i + 1) / 40.0;
            string path = $"rock/{i:D3}.flac";
            _db.Upsert(Track(path, folder: $"rock-{i:D3}") with { Genre = "Rock" });
            _db.SaveTrackMood(path, 100, 1, new MoodReading(at, at * 10, at * 20));
        }

        Assert.False(_db.SmartPlaylistCanRegenerate(
            SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm)));
    }

    /// <summary>
    /// The button depends on the LIST that is shown, not on the maximum list length.
    ///
    /// <para>A shelf whose calm band is on few albums shows a short list (one song per album), while
    /// the band still has songs that were not shown. The pool has fewer than fifty songs, so "is the
    /// pool larger than fifty" would say no, while the user sees a list of ten with thirty more
    /// available.</para>
    /// </summary>
    [Fact]
    public void A_short_list_over_a_deeper_pool_still_has_a_second_list_in_it()
    {
        // Two hundred tracks on twenty albums. The calm band is a share of the genre, so it has about
        // thirty songs (well under the fifty a list may show), and they are on a few albums, so the
        // list itself is even shorter.
        for (int i = 0; i < 200; i++)
        {
            double at = (i + 1) / 200.0;
            string path = $"rock/{i:D3}.flac";
            _db.Upsert(Track(path, folder: $"rock-{i % 20:D2}") with { Genre = "Rock" });
            _db.SaveTrackMood(path, 100, 1, new MoodReading(at, at * 10, at * 20));
        }

        var calm = SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm);
        var shown = _db.QuerySmartPlaylist(calm, nowUtcTicks: 0, advance: true).Count;

        Assert.InRange(shown, 1, SmartPlaylists.MostSongs(SmartPlaylistKind.Calm) - 1);
        Assert.True(_db.SmartPlaylistCanRegenerate(calm));
    }

    /// <summary>
    /// An album is limited to one song PER LIST, not across lists. This rule applies to every mood
    /// list.
    ///
    /// <para>One song per album stops a list from being one album; it does not mean the album's other
    /// songs are unsuitable. A shelf with few albums and many tracks on each therefore shows a SHORT
    /// list, and every refresh should give a different short list from the same albums, using tracks
    /// the last list did not use. If the cap were applied to the pool instead, those tracks would be
    /// removed for good and every refresh would give the same songs in a different order. Preventing
    /// that is the reason <see cref="MoodPool"/> exists.</para>
    ///
    /// <para>The fixture has several albums with many tracks each, so only the cap decides the length:
    /// each list must have one song per album, and the two lists must differ.</para>
    /// </summary>
    [Theory]
    [InlineData(SmartPlaylistKind.Calm)]
    [InlineData(SmartPlaylistKind.Energetic)]
    [InlineData(SmartPlaylistKind.Danceable)]
    [InlineData(SmartPlaylistKind.Focus)]
    [InlineData(SmartPlaylistKind.Sleep)]
    public void A_record_is_capped_per_reading_and_not_across_them(SmartPlaylistKind kind)
    {
        Deep();

        var id = SmartPlaylistId.WholeShelf(kind);

        var first = _db.QuerySmartPlaylist(id, nowUtcTicks: 0, advance: true).Select(t => t.RelativePath).ToList();
        var second = _db.QuerySmartPlaylist(id, nowUtcTicks: 0, advance: true).Select(t => t.RelativePath).ToList();

        Assert.NotEmpty(first);

        // One song per album, within each list.
        Assert.Equal(first.Count, first.Select(RecordOf).Distinct().Count());
        Assert.Equal(second.Count, second.Select(RecordOf).Distinct().Count());

        // The same albums both times: the shelf has not changed.
        Assert.Equal(
            first.Select(RecordOf).OrderBy(f => f).ToList(),
            second.Select(RecordOf).OrderBy(f => f).ToList());

        // …and different songs from them, which is what "per list" means. There is a new list to give,
        // and the button agrees.
        Assert.NotEqual(first, second);
        Assert.NotEmpty(second.Except(first));
        Assert.True(_db.SmartPlaylistCanRegenerate(id));
    }

    /// <summary>
    /// Two hundred tracks on albums of five to eleven tracks, measured so that each of the five lists
    /// has songs and each uses several albums with several tracks each.
    ///
    /// <para><b>Brightness and onsets increase together</b>, which fills the arousal bands at both
    /// ends. <b>Dynamics is highest in the MIDDLE</b>, so it is even at both ends of the onsets. This
    /// lets both busy and sparse lists exist: danceable wants many events over a steady level, focused
    /// wants few over the same. A flat column gives neither, because a tie ranks at the midpoint of its
    /// run (see MoodBands.Rank) and every track would read 0.5.</para>
    ///
    /// <para><b>Tracks are grouped by album, not assigned round-robin</b>, which is also how a real
    /// album behaves (its tracks sound alike). This matters for Lights out: it requires a THIRD of an
    /// album to be sleep music (MoodBands.SleepShareOfRecord) before it uses any of it, and only a
    /// fifth of a collection is quiet enough. Assigned round-robin, that fifth spreads evenly and no
    /// album reaches a third, so the list is empty and the test proves nothing about the one mood
    /// whose pool is built by its own code path.</para>
    ///
    /// <para><b>Albums of DIFFERENT lengths, on purpose.</b> The window moves by a whole list each time
    /// (SmartPlaylists.MostSongs, fifty), so the offsets a pool sees are the multiples of fifty modulo
    /// its size. If every album had the same number of tracks, those offsets would fall on album
    /// boundaries: every list would start each album at its first track and return the same songs in
    /// a different order, a rotation and not a new list. Real shelves have albums of five, nine and
    /// eleven tracks, so this one does too.</para>
    ///
    /// <para>Two hundred rows is over MoodBands.LeastForOwnCut, so the genre's bands are computed from
    /// its own distribution, not the collection's. The album is in the PATH as well as in the folder,
    /// because <see cref="RecordOf"/> reads it from the path.</para>
    /// </summary>
    private void Deep()
    {
        int[] lengths = [5, 7, 9, 11];
        int record = 0, left = lengths[0];

        for (int i = 0; i < 200; i++)
        {
            if (left == 0)
                left = lengths[++record % lengths.Length];
            left--;

            double at = (i + 1) / 200.0;
            string folder = $"rock-{record:D2}";
            string path = $"{folder}/{i:D3}.flac";

            _db.Upsert(Track(path, folder: folder) with { Genre = "Rock" });
            _db.SaveTrackMood(path, 100, 1, new MoodReading(at, at * 10, 100 - Math.Abs(i - 100)));
        }
    }

    private static string RecordOf(string path) => path[..path.LastIndexOf('/')];

    /// <summary>The reading includes its album folder. Without the folder there is no join to the
    /// album, and the band computation cannot work per album.</summary>
    [Fact]
    public void A_reading_says_which_record_it_came_off()
    {
        _db.Upsert(Track("Miles/Kind of Blue/01.flac", folder: "Miles/Kind of Blue"));
        _db.SaveTrackMood("Miles/Kind of Blue/01.flac", 100, 1, new MoodReading(0.2, 2, 14));

        Assert.Equal("Miles/Kind of Blue", _db.QueryMeasuredMoods().Single().Folder);
    }

    /// <summary>The reading is stored with the stamps of the file it was measured from. When a full
    /// decode finishes, the file may have been replaced, and then the result describes bytes that are
    /// not on disk any more.</summary>
    [Fact]
    public void A_reading_for_bytes_that_have_moved_on_is_not_written()
    {
        _db.Upsert(Track("b.flac", size: 100, mtime: 1));
        _db.SaveTrackMood("b.flac", sizeBytes: 999, modifiedUtcTicks: 1, new MoodReading(0.5, 3, 9));

        Assert.Empty(_db.QueryMeasuredMoods());
    }

    // ── what stops the pass from looping ──

    /// <summary>A track that could not be measured is stored as such. If it were left null it would be
    /// returned, decoded, fail, and be returned again on every pass for ever. A background sweep must
    /// not do that.</summary>
    [Fact]
    public void A_track_that_could_not_be_described_is_not_handed_out_again()
    {
        _db.Upsert(Track("short.flac"));
        Assert.Single(_db.QueryTracksNeedingLoudness(10));

        _db.SaveTrackMood("short.flac", 100, 1, MoodReading.Unknown);
        _db.SaveTrackLoudness("short.flac", 100, 1, LoudnessReading.Unknown);

        Assert.Empty(_db.QueryTracksNeedingLoudness(10));
    }

    /// <summary>It is also left out of the distribution, rather than counted as the quietest track on
    /// the shelf. Zero for all three values means "measured, nothing to describe", and the bands are
    /// computed only from music.</summary>
    [Fact]
    public void A_track_with_nothing_to_describe_is_not_in_the_distribution()
    {
        _db.Upsert(Track("silent.flac"));
        _db.SaveTrackMood("silent.flac", 100, 1, MoodReading.Unknown);

        Assert.Empty(_db.QueryMeasuredMoods());
    }

    // ── which tracks the pass takes ──

    /// <summary>Most tracks in a well-tagged library already have a ReplayGain tag. If only tracks with
    /// no level were taken, a mood would be measured only for libraries that need levelling.</summary>
    [Fact]
    public void A_track_that_has_a_level_but_no_mood_is_still_work()
    {
        _db.Upsert(Track("levelled.flac"));
        _db.SaveTrackLoudness("levelled.flac", 100, 1, new LoudnessReading(-14, 0.9));

        var work = _db.QueryTracksNeedingLoudness(10);

        Assert.Single(work);
        Assert.True(work[0].NeedsMood);
    }

    /// <summary>A track that has both values is complete. Two values, one decode, and the pass stops
    /// taking the track only when it has both.</summary>
    [Fact]
    public void A_track_with_both_is_done()
    {
        _db.Upsert(Track("done.flac"));
        _db.SaveTrackLoudness("done.flac", 100, 1, new LoudnessReading(-9, 0.99));
        _db.SaveTrackMood("done.flac", 100, 1, new MoodReading(0.2, 2, 8));

        Assert.Empty(_db.QueryTracksNeedingLoudness(10));
        Assert.Equal(0, _db.CountTracksNeedingLoudness());
    }

    /// <summary>A track that needs only a level says so. This lets the pass take the level from the
    /// recording pool without opening the file. A mood cannot be taken from the pool (nobody publishes
    /// one), so a row that needs a mood must be decoded.</summary>
    [Fact]
    public void A_track_that_needs_only_a_level_says_so()
    {
        _db.Upsert(Track("needslevel.flac"));
        _db.SaveTrackMood("needslevel.flac", 100, 1, new MoodReading(0.4, 3, 10));

        var work = _db.QueryTracksNeedingLoudness(10);

        Assert.Single(work);
        Assert.False(work[0].NeedsMood);
    }

    // ── what is not a song ──

    /// <summary>
    /// An interlude never gets a mood measurement.
    ///
    /// <para>A shelf has many of them (skits, intros, vinyl noise, the applause between two live
    /// tracks), and each one would be a decode for a value nobody needs. It is also harmful: the values
    /// would be part of the distribution the bands are computed from, and would move its quiet end
    /// towards the sound of a room.</para>
    /// </summary>
    [Fact]
    public void A_track_too_short_to_be_a_song_is_never_asked_for_a_mood()
    {
        _db.Upsert(Track("interlude.flac", seconds: 40));
        _db.SaveTrackLoudness("interlude.flac", 100, 1, new LoudnessReading(-16, 0.7));

        Assert.Empty(_db.QueryTracksNeedingLoudness(10));
        Assert.Equal(0, _db.CountTracksNeedingLoudness());
        Assert.Equal(0, _db.CountTracksNeedingMeasurement().Mood);
    }

    /// <summary>But it is still levelled. It is played between songs, so it should be at the same level
    /// as the songs around it. It is returned with NeedsMood false, so its decode measures only the
    /// level.</summary>
    [Fact]
    public void An_interlude_is_still_levelled()
    {
        _db.Upsert(Track("skit.flac", seconds: 30));

        var work = _db.QueryTracksNeedingLoudness(10);

        Assert.Single(work);
        Assert.True(work[0].NeedsLevel);
        Assert.False(work[0].NeedsMood);
    }

    /// <summary>The limit is one minute, and a real short song is above it. Punk singles and short
    /// radio edits are songs; forty seconds of applause is not.</summary>
    [Theory]
    [InlineData(59, false)]
    [InlineData(60, true)]
    [InlineData(150, true)]
    public void The_line_falls_at_a_minute(double seconds, bool described)
    {
        _db.Upsert(Track("x.flac", seconds: seconds));

        Assert.Equal(described, _db.QueryTracksNeedingLoudness(10).Single().NeedsMood);
    }

    /// <summary>A short track that already has a mood reading is left out of the distribution, so a
    /// shelf that was measured before the length limit needs no migration and no second decode. For
    /// this reason the length check is on the read as well as on the selection.</summary>
    [Fact]
    public void A_short_track_measured_before_the_rule_does_not_count()
    {
        _db.Upsert(Track("early.flac", seconds: 20));
        _db.SaveTrackMood("early.flac", 100, 1, new MoodReading(0.9, 8, 3));

        Assert.Empty(_db.QueryMeasuredMoods());
    }

    /// <summary>
    /// A measurement costs a full decode, and a retag must not discard it. The level follows the same
    /// rule.
    ///
    /// <para><b>The test uses a REAL retag.</b> Writing a tag changes both the size and the mtime, so
    /// a test that keeps both the same is not a retag. A rule that discarded readings on any size or
    /// mtime change would pass such a test and decode files again that were never re-encoded. See
    /// <c>TheAudioChanged</c>.</para>
    /// </summary>
    [Fact]
    public void A_retag_keeps_the_reading()
    {
        _db.Upsert(Track("tagged.flac", size: 100, mtime: 1));
        _db.SaveTrackMood("tagged.flac", 100, 1, new MoodReading(0.6, 5, 11));

        // A tag written into the file: it is larger and its modified time has changed, but no sample
        // of the audio is different.
        _db.Upsert(Track("tagged.flac", size: 4_096, mtime: 999));

        Assert.Single(_db.QueryMeasuredMoods());
    }

    /// <summary>A re-encode does discard it, correctly: the reading described the old audio.</summary>
    [Fact]
    public void A_re_encode_asks_again()
    {
        _db.Upsert(Track("c.flac", size: 100, mtime: 1, seconds: 240));
        _db.SaveTrackMood("c.flac", 100, 1, new MoodReading(0.6, 5, 11));

        // Different audio: it has a different duration from the one that was measured.
        _db.Upsert(Track("c.flac", size: 200, mtime: 2, seconds: 186));

        Assert.Empty(_db.QueryMeasuredMoods());
        Assert.Single(_db.QueryTracksNeedingLoudness(10));
    }

    /// <summary>A second read of a container that reports a slightly different length is not a
    /// re-encode. The comparison is to the whole second, so a rounding difference does not cause a
    /// decode.</summary>
    [Fact]
    public void A_rounding_difference_in_the_duration_is_not_a_re_encode()
    {
        _db.Upsert(Track("round.flac", size: 100, mtime: 1, seconds: 240.0));
        _db.SaveTrackMood("round.flac", 100, 1, new MoodReading(0.6, 5, 11));

        _db.Upsert(Track("round.flac", size: 100, mtime: 2, seconds: 240.4));

        Assert.Single(_db.QueryMeasuredMoods());
    }
}
