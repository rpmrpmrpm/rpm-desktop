using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// Whether a song FITS where a generated list put it, and what that does.
///
/// <para><b>This is NOT the heart.</b> A favourite is a statement about the song; a verdict is a
/// statement about the pick. If they were folded together, pressing "like" would silently heart
/// the record, which is a different claim from the one the listener made. Half the tests below
/// exist to keep them apart.</para>
///
/// <para>Also: a pick can never be liked and turned down at once, which is why
/// <see cref="TrackVerdict"/> is one value and not two flags, and a song turned down is never
/// offered by a list the library writes itself.</para>
/// </summary>
public class TrackVerdictTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    private const long Now = 638_000_000_000_000_000L;

    public TrackVerdictTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmverdict-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private void Add(string path)
    {
        var slash = path.LastIndexOf('/');
        _db.Upsert(new LibraryTrack
        {
            RelativePath = path,
            Folder = slash < 0 ? "" : path[..slash],
            FileName = slash < 0 ? path : path[(slash + 1)..],
            Extension = Path.GetExtension(path),
            SizeBytes = 5_000_000,
            ModifiedUtcTicks = 1,
            DurationSeconds = 200,
        });
    }

    // ── the one value ──

    [Fact]
    public void A_song_nobody_has_judged_reads_as_nothing_said()
    {
        Add("a/01.flac");

        Assert.Equal(TrackVerdict.None, _db.TrackVerdictOf("a/01.flac", SmartPlaylistKind.Calm));
    }

    [Theory]
    [InlineData(TrackVerdict.Liked)]
    [InlineData(TrackVerdict.Disliked)]
    [InlineData(TrackVerdict.None)]
    public void A_verdict_survives_the_round_trip(TrackVerdict verdict)
    {
        Add("a/01.flac");

        Assert.True(_db.SetTrackVerdict("a/01.flac", SmartPlaylistKind.Calm, verdict, Now));
        Assert.Equal(verdict, _db.TrackVerdictOf("a/01.flac", SmartPlaylistKind.Calm));
    }

    // ── the heart and the verdict are different facts ──

    /// <summary>Liking a pick must not put a heart on the record: a listener saying "this belonged in
    /// the calm list" has not said "this is one of my favourite songs".</summary>
    [Fact]
    public void Liking_a_song_does_not_heart_it()
    {
        Add("a/01.flac");

        _db.SetTrackVerdict("a/01.flac", SmartPlaylistKind.Calm, TrackVerdict.Liked, Now);

        Assert.Equal(TrackVerdict.Liked, _db.TrackVerdictOf("a/01.flac", SmartPlaylistKind.Calm));
        Assert.False(_db.IsTrackFavorite("a/01.flac"));
    }

    /// <summary>And the other way: hearting a record says nothing about where it belongs.</summary>
    [Fact]
    public void Hearting_a_song_does_not_judge_the_pick()
    {
        Add("a/01.flac");

        _db.SetTrackFavorite("a/01.flac", true, Now);

        Assert.True(_db.IsTrackFavorite("a/01.flac"));
        Assert.Equal(TrackVerdict.None, _db.TrackVerdictOf("a/01.flac", SmartPlaylistKind.Calm));
    }

    /// <summary>A favourite can be a bad fit, which one value could not carry. Both facts stand, and
    /// neither disturbs the other.</summary>
    [Fact]
    public void A_favourite_can_be_turned_down_as_a_pick()
    {
        Add("a/01.flac");

        _db.SetTrackFavorite("a/01.flac", true, Now);
        _db.SetTrackVerdict("a/01.flac", SmartPlaylistKind.Calm, TrackVerdict.Disliked, Now);

        Assert.True(_db.IsTrackFavorite("a/01.flac"));
        Assert.Equal(TrackVerdict.Disliked, _db.TrackVerdictOf("a/01.flac", SmartPlaylistKind.Calm));
    }

    /// <summary>Taking the heart off leaves the verdict where it was. Each fact has its own write.</summary>
    [Fact]
    public void Un_hearting_leaves_the_verdict_alone()
    {
        Add("a/01.flac");

        _db.SetTrackVerdict("a/01.flac", SmartPlaylistKind.Calm, TrackVerdict.Liked, Now);
        _db.SetTrackFavorite("a/01.flac", true, Now);
        _db.SetTrackFavorite("a/01.flac", false, Now);

        Assert.Equal(TrackVerdict.Liked, _db.TrackVerdictOf("a/01.flac", SmartPlaylistKind.Calm));
    }

    /// <summary>A heart arriving from the store is still only a heart.</summary>
    [Fact]
    public void A_heart_from_the_store_leaves_the_verdict_alone()
    {
        Add("a/01.flac");
        _db.SetTrackVerdict("a/01.flac", SmartPlaylistKind.Calm, TrackVerdict.Disliked, Now);

        _db.AcceptTrackFavorite("a/01.flac", true, Now);

        Assert.Equal(TrackVerdict.Disliked, _db.TrackVerdictOf("a/01.flac", SmartPlaylistKind.Calm));
        Assert.True(_db.IsTrackFavorite("a/01.flac"));
    }

    // ── the one value ──

    /// <summary>The invariant the type exists for. Two flags could say a pick is both; one value
    /// cannot, and the write is one statement so there is no instant in which it is.</summary>
    [Fact]
    public void A_pick_cannot_be_liked_and_turned_down_at_once()
    {
        Add("a/01.flac");

        _db.SetTrackVerdict("a/01.flac", SmartPlaylistKind.Calm, TrackVerdict.Liked, Now);
        _db.SetTrackVerdict("a/01.flac", SmartPlaylistKind.Calm, TrackVerdict.Disliked, Now);
        Assert.Equal(TrackVerdict.Disliked, _db.TrackVerdictOf("a/01.flac", SmartPlaylistKind.Calm));

        _db.SetTrackVerdict("a/01.flac", SmartPlaylistKind.Calm, TrackVerdict.Liked, Now);
        Assert.Equal(TrackVerdict.Liked, _db.TrackVerdictOf("a/01.flac", SmartPlaylistKind.Calm));
    }

    [Fact]
    public void A_path_the_mirror_does_not_carry_takes_no_verdict()
    {
        Assert.False(_db.SetTrackVerdict("nothing/here.flac", SmartPlaylistKind.Calm, TrackVerdict.Liked, Now));
        Assert.Equal(TrackVerdict.None, _db.TrackVerdictOf("nothing/here.flac", SmartPlaylistKind.Calm));
    }

    // ── what a refusal does ──

    /// <summary>The whole of what a dislike is for: the song goes from the list it was refused in, and
    /// it does not come back there.</summary>
    [Fact]
    public void A_song_turned_down_is_never_offered_by_that_list_again()
    {
        // NeverPlayed rather than MostPlayed: a play count is written by NoteListen and never by
        // Upsert, so a shelf built here has none. What is being tested is the narrowing, and this rule
        // takes every row in the mirror.
        for (var i = 0; i < 20; i++)
            Add($"a/{i:D2}.flac");

        var id = SmartPlaylistId.WholeShelf(SmartPlaylistKind.NeverPlayed);
        Assert.Contains(_db.QuerySmartPlaylist(id, Now, advance: true), t => t.RelativePath == "a/00.flac");

        _db.SetTrackVerdict("a/00.flac", SmartPlaylistKind.NeverPlayed, TrackVerdict.Disliked, Now);

        Assert.DoesNotContain(_db.QuerySmartPlaylist(id, Now, advance: true), t => t.RelativePath == "a/00.flac");
    }

    /// <summary>
    /// And it is offered by every OTHER list exactly as before.
    ///
    /// <para>A dislike means "this is not a good fit for this list", not "never play this". The song
    /// can still fit one of the other lists. A refusal must therefore be stored per list, not as one
    /// stamp on the track row.</para>
    /// </summary>
    [Fact]
    public void A_song_turned_down_in_one_list_is_still_offered_by_the_others()
    {
        for (var i = 0; i < 20; i++)
            Add($"a/{i:D2}.flac");

        _db.SetTrackVerdict("a/00.flac", SmartPlaylistKind.Calm, TrackVerdict.Disliked, Now);

        var elsewhere = SmartPlaylistId.WholeShelf(SmartPlaylistKind.NeverPlayed);
        Assert.Contains(
            _db.QuerySmartPlaylist(elsewhere, Now, advance: true), t => t.RelativePath == "a/00.flac");
    }

    /// <summary>Two lists, two opinions, and neither reads the other's. A listener who refuses a song
    /// from one row and keeps it in another has said two true things.
    /// </summary>
    [Fact]
    public void A_verdict_in_one_list_is_not_a_verdict_in_another()
    {
        Add("a/01.flac");

        _db.SetTrackVerdict("a/01.flac", SmartPlaylistKind.Calm, TrackVerdict.Disliked, Now);
        _db.SetTrackVerdict("a/01.flac", SmartPlaylistKind.Danceable, TrackVerdict.Liked, Now);

        Assert.Equal(TrackVerdict.Disliked, _db.TrackVerdictOf("a/01.flac", SmartPlaylistKind.Calm));
        Assert.Equal(TrackVerdict.Liked, _db.TrackVerdictOf("a/01.flac", SmartPlaylistKind.Danceable));
        Assert.Equal(TrackVerdict.None, _db.TrackVerdictOf("a/01.flac", SmartPlaylistKind.Focus));
    }

    /// <summary>Taking one back leaves the other standing: the same rule, one press further on.</summary>
    [Fact]
    public void Taking_a_verdict_back_leaves_the_other_lists_alone()
    {
        Add("a/01.flac");

        _db.SetTrackVerdict("a/01.flac", SmartPlaylistKind.Calm, TrackVerdict.Disliked, Now);
        _db.SetTrackVerdict("a/01.flac", SmartPlaylistKind.Focus, TrackVerdict.Disliked, Now);
        _db.SetTrackVerdict("a/01.flac", SmartPlaylistKind.Calm, TrackVerdict.None, Now);

        Assert.Equal(TrackVerdict.None, _db.TrackVerdictOf("a/01.flac", SmartPlaylistKind.Calm));
        Assert.Equal(TrackVerdict.Disliked, _db.TrackVerdictOf("a/01.flac", SmartPlaylistKind.Focus));
    }

    /// <summary>And the subtitle agrees with the list. The two are composed separately, so a narrowing
    /// added to one and forgotten at the other would leave a pane announcing more songs than it can
    /// play. That is why the clause is one property and not two appended strings.</summary>
    [Fact]
    public void The_count_and_the_list_agree_about_a_refusal()
    {
        // NeverPlayed rather than MostPlayed: a play count is written by NoteListen and never by
        // Upsert, so a shelf built here has none. What is being tested is the narrowing, and this rule
        // takes every row in the mirror.
        for (var i = 0; i < 20; i++)
            Add($"a/{i:D2}.flac");

        var id = SmartPlaylistId.WholeShelf(SmartPlaylistKind.NeverPlayed);
        _db.SetTrackVerdict("a/00.flac", SmartPlaylistKind.NeverPlayed, TrackVerdict.Disliked, Now);
        _db.SetTrackVerdict("a/01.flac", SmartPlaylistKind.NeverPlayed, TrackVerdict.Disliked, Now);

        var list = _db.QuerySmartPlaylist(id, Now, advance: true);
        var (counted, _, _) = _db.SmartPlaylistTotals(id, Now);

        Assert.Equal(list.Count, counted);
        Assert.Equal(18, counted);
    }

    /// <summary>Taking the refusal back puts the song back. Nothing is destroyed by a verdict.</summary>
    [Fact]
    public void Taking_a_refusal_back_puts_the_song_back()
    {
        // NeverPlayed rather than MostPlayed: a play count is written by NoteListen and never by
        // Upsert, so a shelf built here has none. What is being tested is the narrowing, and this rule
        // takes every row in the mirror.
        for (var i = 0; i < 20; i++)
            Add($"a/{i:D2}.flac");

        var id = SmartPlaylistId.WholeShelf(SmartPlaylistKind.NeverPlayed);
        _db.SetTrackVerdict("a/00.flac", SmartPlaylistKind.Calm, TrackVerdict.Disliked, Now);
        _db.SetTrackVerdict("a/00.flac", SmartPlaylistKind.Calm, TrackVerdict.None, Now);

        Assert.Contains(_db.QuerySmartPlaylist(id, Now, advance: true), t => t.RelativePath == "a/00.flac");
    }

    /// <summary>A refusal does not reach the lists made of hearts. Those ask about favourites and
    /// answer with favourites, and a verdict is not one.</summary>
    [Fact]
    public void A_refusal_does_not_empty_the_favourites()
    {
        Add("a/01.flac");
        _db.SetTrackFavorite("a/01.flac", true, Now);
        _db.SetTrackVerdict("a/01.flac", SmartPlaylistKind.Calm, TrackVerdict.Disliked, Now);

        // It is out of the generated lists like anything refused (the heart's own list included, since
        // the refusal is a narrowing over every one of them), but the HEART is untouched, which is what
        // the album page and the track line draw.
        Assert.True(_db.IsTrackFavorite("a/01.flac"));
    }

    // ── migrating verdicts made before a verdict named a list ──

    /// <summary>
    /// A refusal made when there was no way to say anything narrower means every list.
    ///
    /// <para>Reading it as "only Chill" would put music back in front of a listener who had already
    /// turned it down. That is the one direction this must not fail in: the stamp was made when a
    /// refusal could not name a list, so it meant every list.</para>
    ///
    /// <para>The old columns are zeroed on the way through. They stay on the table (dropping them is
    /// a schema change, and a schema change can break a database that is already installed; see the
    /// entries above <c>SchemaVersion</c>), but a column that no longer decides anything must not hold
    /// an answer that somebody could read and believe.
    /// </para>
    /// </summary>
    [Fact]
    public void A_verdict_stamped_before_v62_is_carried_into_every_list()
    {
        var older = Path.Combine(_root, "before-62");
        Directory.CreateDirectory(older);

        using (var db = new LibraryDatabase(MirrorLocation.InFolder(older)))
            db.Upsert(new LibraryTrack
            {
                RelativePath = "a/01.flac",
                Folder = "a",
                FileName = "01.flac",
                Extension = ".flac",
                SizeBytes = 1,
                ModifiedUtcTicks = 1,
                DurationSeconds = 200,
            });

        var file = Directory.GetFiles(older, "*.db", SearchOption.AllDirectories).First();
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                $"""
                DROP TABLE IF EXISTS track_verdict;
                UPDATE tracks SET disliked_utc_ticks = {Now} WHERE relative_path = 'a/01.flac';
                PRAGMA user_version = 61;
                """;
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using var reopened = new LibraryDatabase(MirrorLocation.InFolder(older));

        foreach (var kind in Enum.GetValues<SmartPlaylistKind>())
            Assert.Equal(TrackVerdict.Disliked, reopened.TrackVerdictOf("a/01.flac", kind));
    }

    // ── a mirror stamped v56 that has only one of the two columns ──

    /// <summary>
    /// A mirror that is stamped 56 but is missing a column must still open.
    ///
    /// <para>The state: version 56 is recorded, but only <c>disliked_utc_ticks</c> exists.
    /// <c>liked_utc_ticks</c> was added to the same guarded step later, and that step never runs again
    /// because the mirror already says 56. The mirror is then one column short of what the code
    /// selects, and the first read of the playlist pane would throw "no such column: liked_utc_ticks"
    /// out of <c>QueryMeasuredMoods</c>, before the window has drawn.</para>
    ///
    /// <para>The state below is stamped 56 with one column present and the other missing. What proves
    /// the repair is not the column appearing but the READ working, which is what the user hits.</para>
    /// </summary>
    [Fact]
    public void A_mirror_stamped_56_without_the_liked_column_is_repaired_on_open()
    {
        var older = Path.Combine(_root, "half-applied");
        Directory.CreateDirectory(older);

        using (var db = new LibraryDatabase(MirrorLocation.InFolder(older)))
            db.Upsert(new LibraryTrack
            {
                RelativePath = "a/01.flac",
                Folder = "a",
                FileName = "01.flac",
                Extension = ".flac",
                SizeBytes = 1,
                ModifiedUtcTicks = 1,
                DurationSeconds = 200,
            });

        var file = Directory.GetFiles(older, "*.db", SearchOption.AllDirectories).First();
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "ALTER TABLE tracks DROP COLUMN liked_utc_ticks; PRAGMA user_version = 56;";
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using var reopened = new LibraryDatabase(MirrorLocation.InFolder(older));

        // The read that threw, on the path the window takes before it has drawn anything.
        _ = reopened.QueryMeasuredMoods();

        // And the column is a working column, not merely a present one.
        Assert.True(reopened.SetTrackVerdict("a/01.flac", SmartPlaylistKind.Calm, TrackVerdict.Liked, Now));
        Assert.Equal(TrackVerdict.Liked, reopened.TrackVerdictOf("a/01.flac", SmartPlaylistKind.Calm));
    }

    /// <summary>A song nobody has judged is offered as usual. Nought in the column is nothing said,
    /// not a refusal: the difference the third state exists for.</summary>
    [Fact]
    public void Nothing_said_is_not_a_refusal()
    {
        // NeverPlayed rather than MostPlayed: a play count is written by NoteListen and never by
        // Upsert, so a shelf built here has none. What is being tested is the narrowing, and this rule
        // takes every row in the mirror.
        for (var i = 0; i < 20; i++)
            Add($"a/{i:D2}.flac");

        Assert.Equal(
            20, _db.QuerySmartPlaylist(SmartPlaylistId.WholeShelf(SmartPlaylistKind.NeverPlayed), Now, advance: true).Count);
    }
}
