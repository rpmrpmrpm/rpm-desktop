using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// "Don't recommend this artist" and "Don't recommend this album": what the refusal means, and that
/// the two places it is applied agree about it.
///
/// <para>The rule is applied in two places on purpose: the mood lists apply it to their POOL, and
/// every other generated list applies it in SQL. So these tests come in pairs. See
/// <c>LibraryDatabase.Offerable</c>, where the split is written out.</para>
/// </summary>
public class RecommendRefusalTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public RecommendRefusalTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mdrefuse-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private const long Now = 638_000_000_000_000_000;

    // ── the reading, on its own ──

    [Fact]
    public void Nothing_refused_allows_everything()
    {
        Assert.False(RecommendRefusals.None.Any);
        Assert.True(RecommendRefusals.None.Allows(SmartPlaylistKind.Danceable, "rock/abc", "Abc"));
    }

    [Fact]
    public void A_refused_act_takes_their_records_with_them()
    {
        var said = new RecommendRefusals([(RecommendScope.Artist, "Abc", SmartPlaylistKind.Danceable)]);

        Assert.False(said.Allows(SmartPlaylistKind.Danceable, "rock/one", "Abc"));
        Assert.False(said.Allows(SmartPlaylistKind.Danceable, "rock/two", "Abc"));
    }

    /// <summary>A record that is refused is one record. The act keeps the rest of its shelf, which is
    /// the difference between the two entries and the reason there are two.</summary>
    [Fact]
    public void A_refused_record_does_not_take_the_act()
    {
        var said = new RecommendRefusals([(RecommendScope.Album, "rock/one", SmartPlaylistKind.Danceable)]);

        Assert.False(said.Allows(SmartPlaylistKind.Danceable, "rock/one", "Abc"));
        Assert.True(said.Allows(SmartPlaylistKind.Danceable, "rock/two", "Abc"));
    }

    /// <summary>The whole of what a refusal is scoped to. Somebody who does not want an act at a party
    /// may want exactly that act to fall asleep to.</summary>
    [Fact]
    public void A_refusal_is_said_of_one_kind_only()
    {
        var said = new RecommendRefusals([(RecommendScope.Artist, "Abc", SmartPlaylistKind.Danceable)]);

        Assert.False(said.Allows(SmartPlaylistKind.Danceable, "rock/one", "Abc"));
        Assert.True(said.Allows(SmartPlaylistKind.Calm, "rock/one", "Abc"));
        Assert.True(said.Allows(SmartPlaylistKind.Sleep, "rock/one", "Abc"));
    }

    /// <summary>A credit is a tag somebody typed, and a folder is a path. Both are matched in the same
    /// way.</summary>
    [Fact]
    public void The_spelling_is_folded()
    {
        var said = new RecommendRefusals([
            (RecommendScope.Artist, "  Abc  ", SmartPlaylistKind.Focus),
            (RecommendScope.Album, "Rock/One", SmartPlaylistKind.Focus)]);

        Assert.False(said.Allows(SmartPlaylistKind.Focus, "other/x", "abc"));
        Assert.False(said.Allows(SmartPlaylistKind.Focus, "rock/one", "nobody"));
    }

    /// <summary>An empty credit is nobody, not one act with a hundred songs. The per-artist cap reads
    /// it the same way. A shelf of untagged files must not vanish because one of them was
    /// refused.</summary>
    [Fact]
    public void An_untagged_song_names_no_act()
    {
        var said = new RecommendRefusals([(RecommendScope.Artist, "", SmartPlaylistKind.Energetic)]);

        Assert.True(said.Allows(SmartPlaylistKind.Energetic, "rock/one", ""));
        Assert.True(said.Allows(SmartPlaylistKind.Energetic, "rock/one", null));
    }

    /// <summary>The record's credit where it has one: a compilation credits each track to its own act
    /// and the record to somebody else, and refusing "this artist" on it must mean the record's.</summary>
    [Fact]
    public void The_credit_is_the_records_where_it_has_one()
    {
        Assert.Equal("Album Act", RecommendRefusals.CreditOf("Album Act", "Track Act"));
        Assert.Equal("Track Act", RecommendRefusals.CreditOf("", "Track Act"));
        Assert.Equal("Track Act", RecommendRefusals.CreditOf(null, "Track Act"));
        Assert.Equal("", RecommendRefusals.CreditOf(null, null));
    }

    // ── the pool ──

    /// <summary>
    /// Taken off the POOL and not off the list drawn from it: a refused act stops using window slots,
    /// so the songs behind it move up and the list does not come back short.
    /// </summary>
    [Fact]
    public void The_pool_drops_a_refused_act_and_the_rest_move_up()
    {
        // Danceable is a percentile within the genre, so the cut needs a population to be a percentile
        // OF: fifty sparse and swinging records for the band to be busy and even against, and enough of
        // them for the genre to earn a cut of its own (MoodBands.LeastForOwnCut).
        var rows = new List<MeasuredMood>();
        for (var n = 0; n < 50; n++)
            rows.Add(new MeasuredMood(
                $"rock/filler/{n}.flac", "rock/filler", 0.5, 0.1, 0.8 + n * 0.001, false, "Filler", 2000,
                null, null));

        // Eight records, one song each, every one of them busy and even. The first three are one act.
        for (var n = 0; n < 8; n++)
            rows.Add(new MeasuredMood(
                $"rock/{n}/song.flac", $"rock/{n}", 0.5, 0.9 - n * 0.001, 0.05, false,
                n < 3 ? "Refused" : $"Act {n}", 2000, null, null));

        var filed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
            filed[row.Folder] = "Rock";

        var whole = MoodShelf.Build(rows, filed, NoCatalogue, Likes.None).Danceable.WholeList;

        var before = whole.Allowed(RecommendRefusals.None, SmartPlaylistKind.Danceable);
        var after = whole.Allowed(
            new RecommendRefusals([(RecommendScope.Artist, "Refused", SmartPlaylistKind.Danceable)]),
            SmartPlaylistKind.Danceable);

        Assert.Equal(8, before.Count);
        Assert.Equal(5, after.Count);
        Assert.DoesNotContain(after.Songs, p => p.StartsWith("rock/0/", StringComparison.Ordinal));
        Assert.DoesNotContain(after.Songs, p => p.StartsWith("rock/2/", StringComparison.Ordinal));

        // And the window is spent on songs that can appear: five asked for, five given, where a filter
        // applied AFTER the window would have handed back two.
        Assert.Equal(5, after.Window(5, 0).Count);
    }

    [Fact]
    public void The_pool_is_untouched_where_nothing_is_refused()
    {
        var rows = new List<MeasuredMood>
        {
            new("rock/a/1.flac", "rock/a", 0.5, 0.9, 0.0, false, "Act", 2000, null, null),
        };
        var filed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["rock/a"] = "Rock" };

        var pool = MoodShelf.Build(rows, filed, NoCatalogue, Likes.None).Danceable.WholeList;

        Assert.Same(pool, pool.Allowed(RecommendRefusals.None, SmartPlaylistKind.Danceable));
    }

    // ── the table, and the SQL every other list is narrowed by ──

    [Fact]
    public void A_refusal_is_written_read_back_and_taken_away()
    {
        _db.SetRecommendRefusal(RecommendScope.Artist, "Abc", SmartPlaylistKind.Danceable, true, Now);
        _db.SetRecommendRefusal(RecommendScope.Album, "rock/one", SmartPlaylistKind.Danceable, true, Now);

        var said = _db.RecommendRefusalsSaid();
        Assert.True(said.Refused(RecommendScope.Artist, "abc", SmartPlaylistKind.Danceable));
        Assert.True(said.Refused(RecommendScope.Album, "ROCK/ONE", SmartPlaylistKind.Danceable));
        Assert.False(said.Refused(RecommendScope.Artist, "Abc", SmartPlaylistKind.Calm));

        _db.SetRecommendRefusal(RecommendScope.Artist, "Abc", SmartPlaylistKind.Danceable, false, Now);
        Assert.False(_db.RecommendRefusalsSaid().Refused(
            RecommendScope.Artist, "Abc", SmartPlaylistKind.Danceable));
    }

    /// <summary>Saying it twice is one refusal, not two rows that have to be removed twice.</summary>
    [Fact]
    public void Saying_it_twice_says_it_once()
    {
        _db.SetRecommendRefusal(RecommendScope.Artist, "Abc", SmartPlaylistKind.Focus, true, Now);
        _db.SetRecommendRefusal(RecommendScope.Artist, "abc", SmartPlaylistKind.Focus, true, Now + 1);

        _db.SetRecommendRefusal(RecommendScope.Artist, "Abc", SmartPlaylistKind.Focus, false, Now + 2);

        Assert.False(_db.RecommendRefusalsSaid().Any);
    }

    /// <summary>Nobody is not an act, and no folder is not a record: a blank subject would refuse every
    /// untagged song in the collection.</summary>
    [Fact]
    public void A_blank_subject_is_not_a_refusal()
    {
        _db.SetRecommendRefusal(RecommendScope.Artist, "   ", SmartPlaylistKind.Focus, true, Now);

        Assert.False(_db.RecommendRefusalsSaid().Any);
    }

    /// <summary>
    /// The other half of the rule: a list that IS a query is narrowed in SQL, and it must agree with
    /// what the pool does. Favourites is one of those: it reads the track rows whole.
    /// </summary>
    [Fact]
    public void A_query_list_stops_offering_a_refused_act()
    {
        _db.Upsert(Track("rock/one/a.flac", artist: "Refused", album: "One"));
        _db.Upsert(Track("rock/two/b.flac", artist: "Kept", album: "Two"));
        _db.SetTrackFavorite("rock/one/a.flac", true, Now);
        _db.SetTrackFavorite("rock/two/b.flac", true, Now);

        var id = SmartPlaylistId.WholeShelf(SmartPlaylistKind.FavoriteTracks);
        Assert.Equal(2, _db.QuerySmartPlaylist(id, Now, advance: true).Count);

        _db.SetRecommendRefusal(
            RecommendScope.Artist, "Refused", SmartPlaylistKind.FavoriteTracks, true, Now);

        var left = _db.QuerySmartPlaylist(id, Now, advance: true);
        Assert.Equal("rock/two/b.flac", Assert.Single(left).RelativePath);
    }

    [Fact]
    public void A_query_list_stops_offering_a_refused_record()
    {
        _db.Upsert(Track("rock/one/a.flac", artist: "Act", album: "One"));
        _db.Upsert(Track("rock/two/b.flac", artist: "Act", album: "Two"));
        _db.SetTrackFavorite("rock/one/a.flac", true, Now);
        _db.SetTrackFavorite("rock/two/b.flac", true, Now);

        _db.SetRecommendRefusal(
            RecommendScope.Album, "rock/one", SmartPlaylistKind.FavoriteTracks, true, Now);

        var left = _db.QuerySmartPlaylist(
            SmartPlaylistId.WholeShelf(SmartPlaylistKind.FavoriteTracks), Now, advance: true);

        Assert.Equal("rock/two/b.flac", Assert.Single(left).RelativePath);
    }

    /// <summary>And it is still one kind only, asked through the door the lists actually go
    /// through.</summary>
    [Fact]
    public void A_query_list_of_another_kind_still_offers_them()
    {
        _db.Upsert(Track("rock/one/a.flac", artist: "Refused", album: "One"));
        _db.SetTrackFavorite("rock/one/a.flac", true, Now);

        _db.SetRecommendRefusal(
            RecommendScope.Artist, "Refused", SmartPlaylistKind.RecentlyAdded, true, Now);

        Assert.Single(_db.QuerySmartPlaylist(
            SmartPlaylistId.WholeShelf(SmartPlaylistKind.FavoriteTracks), Now, advance: true));
    }

    /// <summary>The album credit is what a refusal on a compilation names, so the SQL has to compose
    /// the credit the same way <see cref="RecommendRefusals.CreditOf"/> does.</summary>
    [Fact]
    public void The_query_reads_the_album_credit_before_the_track_one()
    {
        _db.Upsert(Track("various/one/a.flac", artist: "Track Act", album: "One") with
        {
            AlbumArtist = "Album Act",
        });
        _db.SetTrackFavorite("various/one/a.flac", true, Now);

        var id = SmartPlaylistId.WholeShelf(SmartPlaylistKind.FavoriteTracks);

        // The track's own credit is not what the record is filed under, so it refuses nothing.
        _db.SetRecommendRefusal(
            RecommendScope.Artist, "Track Act", SmartPlaylistKind.FavoriteTracks, true, Now);
        Assert.Single(_db.QuerySmartPlaylist(id, Now, advance: true));

        _db.SetRecommendRefusal(
            RecommendScope.Artist, "Album Act", SmartPlaylistKind.FavoriteTracks, true, Now);
        Assert.Empty(_db.QuerySmartPlaylist(id, Now, advance: true));
    }

    /// <summary>
    /// The whole way through, on the lists the entry is actually drawn on: a mood list is composed in
    /// code and fetched by path, so nothing SQL does would catch a pool that had forgotten.
    ///
    /// <para>Refused AFTER the shelf has been cut and read once, which is the case that matters: the
    /// cut is held for the session (<c>LibraryDatabase.SessionMoods</c>) and the refusals are not, so
    /// a refusal made by the user must show up in the very next list without the measurements being
    /// taken again.</para>
    /// </summary>
    [Fact]
    public void A_mood_list_stops_offering_a_refused_act_without_recutting_the_shelf()
    {
        // Fifty sparse records to be a percentile against, then eight busy and even ones on records of
        // their own: the same shape the pool test builds, written through the mirror this time.
        for (var n = 0; n < 50; n++)
            Measured($"rock/filler/{n}.flac", "Filler", onsets: 0.1, dynamics: 0.8 + n * 0.001);
        for (var n = 0; n < 8; n++)
            Measured($"rock/{n}/song.flac", n < 3 ? "Refused" : $"Act {n}",
                onsets: 0.9 - n * 0.001, dynamics: 0.05);

        var id = SmartPlaylistId.WholeShelf(SmartPlaylistKind.Danceable);
        var before = _db.QuerySmartPlaylist(id, Now, advance: true);
        Assert.Equal(8, before.Count);

        _db.SetRecommendRefusal(
            RecommendScope.Artist, "Refused", SmartPlaylistKind.Danceable, true, Now);

        var after = _db.QuerySmartPlaylist(id, Now, advance: true);
        Assert.Equal(5, after.Count);
        Assert.DoesNotContain(after, t => t.RelativePath.StartsWith("rock/0/", StringComparison.Ordinal));

        // And it comes back when the listener says so.
        _db.SetRecommendRefusal(
            RecommendScope.Artist, "Refused", SmartPlaylistKind.Danceable, false, Now);
        Assert.Equal(8, _db.QuerySmartPlaylist(id, Now, advance: true).Count);
    }

    /// <summary>A track row with a mood on it: long enough to be a song, filed under a genre, and
    /// measured. Everything a mood list needs before it can hold an opinion.</summary>
    private void Measured(string relativePath, string artist, double onsets, double dynamics)
    {
        var track = Track(relativePath, artist: artist) with
        {
            DurationSeconds = 200,
            Genre = "Rock",
        };
        _db.Upsert(track);
        _db.SaveTrackMood(
            relativePath, track.SizeBytes, track.ModifiedUtcTicks,
            new Analysis.MoodReading(0.5, onsets, dynamics));
    }

    private static readonly Dictionary<string, CatalogueFacts> NoCatalogue =
        new(StringComparer.OrdinalIgnoreCase);

    private static LibraryTrack Track(string relativePath, string? artist = null, string? album = null)
    {
        var slash = relativePath.LastIndexOf('/');
        return new LibraryTrack
        {
            RelativePath = relativePath,
            Folder = slash < 0 ? "" : relativePath[..slash],
            FileName = slash < 0 ? relativePath : relativePath[(slash + 1)..],
            Extension = Path.GetExtension(relativePath).ToLowerInvariant(),
            SizeBytes = 100,
            ModifiedUtcTicks = 1,
            Artist = artist,
            Album = album,
        };
    }
}
