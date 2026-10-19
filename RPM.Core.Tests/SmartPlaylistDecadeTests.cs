using RPM.Fingerprint.Catalog;
using RPM.Core.Library;
using RPM.Core.Metadata.Providers;

namespace RPM.Core.Tests;

/// <summary>
/// The decade lists, and what the world plays.
///
/// <para>There is one list per decade, ranked by <c>tracks.world_plays</c>, the figure that the album
/// page's star fetches. It is worth being strict about the difference between zero and null: zero is
/// a song the world was asked about and does not play, null is a song nobody has asked about. A sweep
/// that mixed them up would either ask the collection again forever or claim an answer it does not
/// have.</para>
/// </summary>
public class SmartPlaylistDecadeTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public SmartPlaylistDecadeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmdecade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private void Add(string folder, int at, int year, string title = "Song", uint disc = 0)
    {
        _db.Upsert(new LibraryTrack
        {
            RelativePath = disc > 0 ? $"{folder}/{disc}-{at:D2}.flac" : $"{folder}/{at:D2}.flac",
            Folder = folder,
            FileName = $"{at:D2}.flac",
            Extension = ".flac",
            SizeBytes = 5_000_000,
            ModifiedUtcTicks = 1,
            DurationSeconds = 200,
            TrackNumber = (uint)at,
            DiscNumber = disc,
            Year = (uint)year,
            Title = title,
            Artist = "An Act",
            Album = folder,
        });
    }

    /// <summary>Listens on disc one, by position.</summary>
    private static Dictionary<TrackPlace, long> OnDiscOne(params (int At, long Listens)[] plays)
        => plays.ToDictionary(p => new TrackPlace(1, p.At), p => p.Listens);

    // ── how a decade list is named ──

    [Fact]
    public void A_decade_is_a_kind_of_its_own_rather_than_a_mood_with_a_date_on_it()
    {
        var id = SmartPlaylistId.In(1970);

        Assert.Equal(SmartPlaylistKind.Decade, id.Kind);
        Assert.Equal(1970, id.Decade);
        Assert.True(id.IsWithinDecade);
        Assert.Null(id.Genre);
    }

    [Theory]
    [InlineData(1955)]
    [InlineData(1971)]
    [InlineData(2020)]
    [InlineData(0)]
    public void A_decade_the_shelf_does_not_offer_is_refused(int decade)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SmartPlaylistId.In(decade));
    }

    [Fact]
    public void The_decades_are_the_six_that_were_asked_for()
    {
        Assert.Equal([1960, 1970, 1980, 1990, 2000, 2010], SmartPlaylists.Decades);
    }

    /// <summary>A decade is not cut within a genre and not cut by mood: it is the whole of the period,
    /// ranked by what the world plays. It is its own kind, so nothing about the mood shelf reaches
    /// it.</summary>
    [Fact]
    public void A_decade_is_not_broken_out_any_further()
    {
        Assert.DoesNotContain(SmartPlaylistKind.Decade, SmartPlaylists.BrokenOutByGenre);
        Assert.DoesNotContain(SmartPlaylistKind.Decade, SmartPlaylists.BrokenOutByEra);
    }

    // ── zero is not null ──

    [Fact]
    public void A_song_nobody_has_asked_about_is_in_no_decade()
    {
        for (var i = 1; i <= 12; i++)
            Add("unasked", i, 1975);

        Assert.Empty(_db.QuerySmartPlaylist(SmartPlaylistId.In(1970), nowUtcTicks: 0, advance: true));
    }

    /// <summary>Asked, and the world does not play it. That is an answer and it is written down, but a
    /// chart of songs nobody listens to is not a chart, so zero stays out of the list.</summary>
    [Fact]
    public void A_song_the_world_does_not_play_is_written_down_and_left_out()
    {
        for (var i = 1; i <= 12; i++)
            Add("quiet", i, 1975);

        _db.SaveWorldPlays("quiet", new Dictionary<TrackPlace, long>());

        Assert.Empty(_db.QuerySmartPlaylist(SmartPlaylistId.In(1970), nowUtcTicks: 0, advance: true));
        Assert.Empty(_db.NextRecordsWithoutWorldPlays(10));
    }

    /// <summary>The central case: the songs of a decade in the order the world plays them.</summary>
    [Fact]
    public void A_decade_comes_back_most_played_first()
    {
        Add("hits", 1, 1975, "Least");
        Add("hits", 2, 1975, "Most");
        Add("hits", 3, 1975, "Middle");

        _db.SaveWorldPlays("hits", OnDiscOne((1, 10), (2, 5_000_000), (3, 900)));

        var decade = _db.QuerySmartPlaylist(SmartPlaylistId.In(1970), nowUtcTicks: 0, advance: true);

        Assert.Equal(["Most", "Middle", "Least"], decade.Select(t => t.Title));
    }

    [Fact]
    public void A_decade_holds_only_its_own_years()
    {
        Add("sixties", 1, 1969);
        Add("seventies", 1, 1970);
        Add("seventies", 2, 1979);
        Add("eighties", 1, 1980);

        foreach (var folder in new[] { "sixties", "seventies", "eighties" })
            _db.SaveWorldPlays(folder, OnDiscOne((1, 100), (2, 100)));

        var seventies = _db.QuerySmartPlaylist(SmartPlaylistId.In(1970), nowUtcTicks: 0, advance: true);

        Assert.Equal(2, seventies.Count);
        Assert.All(seventies, t => Assert.StartsWith("seventies/", t.RelativePath, StringComparison.Ordinal));
    }

    /// <summary>
    /// The records take turns, so the top of a chart is not one album with gaps in it.
    ///
    /// <para>Without this, a decade list repeats too many songs from the same album (for example
    /// sixteen of fifty songs from one record), because the songs on one record are played about as
    /// much as each other and so arrive together. The ranking is right, but the list reads
    /// badly.</para>
    /// </summary>
    [Fact]
    public void The_records_take_turns_at_the_top_of_a_decade()
    {
        // One record the world plays a lot, and two that it plays less. Every song of the first
        // outranks every song of the others, which is the shape that makes the list unreadable.
        for (var i = 1; i <= 4; i++)
            Add("famous", i, 1975, $"Famous {i}");
        for (var i = 1; i <= 2; i++)
            Add("lesser", i, 1975, $"Lesser {i}");
        for (var i = 1; i <= 2; i++)
            Add("rarer", i, 1975, $"Rarer {i}");

        _db.SaveWorldPlays("famous", OnDiscOne((1, 9_000), (2, 8_000), (3, 7_000), (4, 6_000)));
        _db.SaveWorldPlays("lesser", OnDiscOne((1, 500), (2, 400)));
        _db.SaveWorldPlays("rarer", OnDiscOne((1, 50), (2, 40)));

        var decade = _db.QuerySmartPlaylist(SmartPlaylistId.In(1970), nowUtcTicks: 0, advance: true);
        var records = decade.Select(t => t.RelativePath.Split('/')[0]).ToList();

        // A turn each before anybody gets a second one, and the most played record still leads.
        Assert.Equal(["famous", "lesser", "rarer", "famous", "lesser", "rarer", "famous", "famous"], records);

        // Nothing is dropped: this is a reordering, not the mood lists' cap.
        Assert.Equal(8, decade.Count);
        Assert.Equal("Famous 1", decade[0].Title);
    }

    /// <summary>A decade list is a chart and stops where a chart stops.</summary>
    [Fact]
    public void A_decade_stops_at_fifty()
    {
        var plays = new Dictionary<TrackPlace, long>();
        for (var i = 1; i <= 80; i++)
        {
            Add("many", i, 1985);
            plays[new TrackPlace(1, i)] = 1000 - i;
        }
        _db.SaveWorldPlays("many", plays);

        Assert.Equal(SmartPlaylists.SongsInADecade,
            _db.QuerySmartPlaylist(SmartPlaylistId.In(1980), nowUtcTicks: 0, advance: true).Count);
    }

    /// <summary>A decade is deterministic: it is a chart, not a sample, so nothing offers to give a
    /// different one.</summary>
    [Fact]
    public void A_decade_is_not_regenerated()
    {
        Assert.False(_db.SmartPlaylistCanRegenerate(SmartPlaylistId.In(1970)));
    }

    // ── what the sweep sees ──

    /// <summary>A record that has been asked about never comes back, whatever the answer was. Without
    /// that, the unfindable albums would be offered on every run and the rest of the shelf would
    /// never be reached.</summary>
    [Fact]
    public void A_record_that_has_been_asked_about_is_not_offered_again()
    {
        for (var i = 1; i <= 3; i++)
            Add("asked", i, 1985);
        for (var i = 1; i <= 3; i++)
            Add("waiting", i, 1985);

        _db.SaveWorldPlays("asked", OnDiscOne((1, 5)));

        var next = _db.NextRecordsWithoutWorldPlays(10);

        Assert.Equal("waiting", Assert.Single(next).Folder);
    }

    /// <summary>Including the tracks that the catalogue said nothing about. They are stamped zero
    /// because the RECORD was asked. Otherwise the sweep would return to the same album forever over
    /// one missing track.</summary>
    [Fact]
    public void A_track_the_catalogue_missed_is_still_marked_asked()
    {
        for (var i = 1; i <= 3; i++)
            Add("partial", i, 1985);

        _db.SaveWorldPlays("partial", OnDiscOne((2, 700)));

        Assert.Empty(_db.NextRecordsWithoutWorldPlays(10));
        Assert.Single(_db.QuerySmartPlaylist(SmartPlaylistId.In(1980), nowUtcTicks: 0, advance: true));
    }

    [Fact]
    public void A_record_no_catalogue_has_heard_of_is_marked_and_dropped()
    {
        for (var i = 1; i <= 3; i++)
            Add("unknown", i, 1985);

        _db.NoteWorldPlaysUnknown("unknown");

        Assert.Empty(_db.NextRecordsWithoutWorldPlays(10));
        Assert.Equal((1, 0), _db.WorldPlaysProgress());
    }

    /// <summary>The sweep is told what to search with: a record with no artist or no title is a lookup
    /// that cannot be made, and offering it would spend three requests to fail.</summary>
    [Fact]
    public void A_record_with_no_name_is_not_offered_to_the_sweep()
    {
        _db.Upsert(new LibraryTrack
        {
            RelativePath = "nameless/01.flac",
            Folder = "nameless",
            FileName = "01.flac",
            Extension = ".flac",
            SizeBytes = 1,
            ModifiedUtcTicks = 1,
            DurationSeconds = 200,
        });

        Assert.Empty(_db.NextRecordsWithoutWorldPlays(10));
    }

    [Fact]
    public void The_sweep_is_handed_what_the_catalogue_is_searched_by()
    {
        for (var i = 1; i <= 4; i++)
            Add("a record", i, 1985);

        var record = Assert.Single(_db.NextRecordsWithoutWorldPlays(10));

        Assert.Equal("a record", record.Folder);
        Assert.Equal("An Act", record.Artist);
        Assert.Equal("a record", record.Album);
        Assert.Equal(4, record.Tracks);
    }

    /// <summary>
    /// An album somebody has linked gives the sweep its own release id, which turns the catalogue's
    /// fuzzy search into a lookup. Across a library that saves a rate-limited second per record, and
    /// removes one chance of a search answering confidently with the wrong pressing.
    /// </summary>
    [Fact]
    public void A_linked_record_is_handed_the_id_it_was_linked_to()
    {
        for (var i = 1; i <= 4; i++)
            Add("linked", i, 1985);

        _db.SetAlbumPressing("linked", "rpm", "9fdbb485-a8f2-453d-9449-bfc7fc2101fe");

        Assert.Equal("9fdbb485-a8f2-453d-9449-bfc7fc2101fe",
            Assert.Single(_db.NextRecordsWithoutWorldPlays(10)).ReleaseMbid);
    }

    /// <summary>A link to another catalogue is a real link to the wrong place. Discogs release numbers
    /// are not MBIDs, and passing one as if it were would look up a stranger.</summary>
    [Fact]
    public void A_record_linked_to_another_catalogue_hands_over_nothing()
    {
        for (var i = 1; i <= 4; i++)
            Add("discogs", i, 1985);

        _db.SetAlbumPressing("discogs", "discogs", "12345");

        Assert.Null(Assert.Single(_db.NextRecordsWithoutWorldPlays(10)).ReleaseMbid);
    }

    /// <summary>And the ordinary case, which is most of a shelf: nobody has linked it, so there is
    /// nothing to give and the search is still how it gets asked about.</summary>
    [Fact]
    public void An_unlinked_record_hands_over_nothing()
    {
        for (var i = 1; i <= 4; i++)
            Add("unlinked", i, 1985);

        Assert.Null(Assert.Single(_db.NextRecordsWithoutWorldPlays(10)).ReleaseMbid);
    }

    // ── the join the counts arrive through ──

    /// <summary>By position, never by title: the catalogue spells a title in its own way, and a match
    /// on the words drops the tracks it is least sure about, which on a famous record are the famous
    /// ones.</summary>
    [Fact]
    public void The_counts_are_joined_to_the_record_by_position()
    {
        var tracks = new List<ReleaseTrack>
        {
            new(1, "Opener (2011 Remaster)", "mbid-a"),
            new(2, "The Hit", "mbid-b"),
        };
        var plays = new List<RecordingPlays>
        {
            new("mbid-b", 1_646_993, 90_000),
            new("mbid-a", 43, 7),
        };

        var byPlace = TrackPopularity.PlaysByPlace(tracks, plays);

        Assert.Equal(43, byPlace[new TrackPlace(1, 1)].Listens);
        Assert.Equal(1_646_993, byPlace[new TrackPlace(1, 2)].Listens);
    }

    /// <summary>The listing counts each disc from one. A second disc's track 2 is its own place, not
    /// the first disc's: if matching used position alone, the demo's count would stand in for the
    /// hit's.</summary>
    [Fact]
    public void A_second_disc_is_counted_from_the_listing()
    {
        var byPlace = TrackPopularity.PlaysByPlace(
            [new(1, "Opener", "a"), new(2, "The Hit", "b"), new(1, "Opener (Demo)", "c"), new(2, "The Hit (Demo)", "d")],
            [new("b", 5_000_000, 90_000), new("d", 300, 20)]);

        Assert.Equal(5_000_000, byPlace[new TrackPlace(1, 2)].Listens);
        Assert.Equal(300, byPlace[new TrackPlace(2, 2)].Listens);
    }

    /// <summary>Each disc's rows take their own disc's figure.</summary>
    [Fact]
    public void The_counts_are_written_to_the_rows_of_their_own_disc()
    {
        Add("deluxe", 1, 1975, "The Hit", disc: 1);
        Add("deluxe", 1, 1975, "The Hit (Demo)", disc: 2);

        _db.SaveWorldPlays("deluxe", new Dictionary<TrackPlace, long>
        {
            [new TrackPlace(1, 1)] = 5_000_000,
            [new TrackPlace(2, 1)] = 300,
        });

        var decade = _db.QuerySmartPlaylist(SmartPlaylistId.In(1970), nowUtcTicks: 0, advance: true);

        Assert.Equal(["The Hit", "The Hit (Demo)"], decade.Select(t => t.Title));
    }

    /// <summary>A record whose rows carry no disc number is read as disc one.</summary>
    [Fact]
    public void A_row_with_no_disc_number_is_on_disc_one()
    {
        Add("plain", 1, 1975, "The Hit");

        _db.SaveWorldPlays("plain", OnDiscOne((1, 42)));

        Assert.Single(_db.QuerySmartPlaylist(SmartPlaylistId.In(1970), nowUtcTicks: 0, advance: true));
    }

    /// <summary>Records with a second disc that were stamped by position alone are asked again, once.
    /// Single-disc records keep what they have.</summary>
    [Fact]
    public void Records_with_a_second_disc_are_asked_again_once()
    {
        Add("single", 1, 1975);
        Add("double", 1, 1975, disc: 1);
        Add("double", 1, 1975, disc: 2);
        _db.SaveWorldPlays("single", OnDiscOne((1, 10)));
        _db.SaveWorldPlays("double", OnDiscOne((1, 10)));

        Assert.Equal(2, _db.AskAgainAboutDiscsMatchedByPosition());
        Assert.Equal("double", Assert.Single(_db.NextRecordsWithoutWorldPlays(10)).Folder);

        _db.SaveWorldPlays("double", OnDiscOne((1, 10)));
        Assert.Equal(0, _db.AskAgainAboutDiscsMatchedByPosition());
        Assert.Empty(_db.NextRecordsWithoutWorldPlays(10));
    }

    /// <summary>A track the counts say nothing about is absent and not zero: nobody asked and nobody
    /// answered, and zero is a claim the caller would write down.</summary>
    [Fact]
    public void A_recording_with_no_count_is_absent_rather_than_nought()
    {
        var byPlace = TrackPopularity.PlaysByPlace(
            [new(1, "Known", "mbid-a"), new(2, "Unknown", "mbid-b")],
            [new("mbid-a", 500, 30)]);

        Assert.True(byPlace.ContainsKey(new TrackPlace(1, 1)));
        Assert.False(byPlace.ContainsKey(new TrackPlace(1, 2)));
    }

    // ── the classical list ──

    private void AddClassical(string folder, int at, string genre)
    {
        _db.Upsert(new LibraryTrack
        {
            RelativePath = $"{folder}/{at:D2}.flac",
            Folder = folder,
            FileName = $"{at:D2}.flac",
            Extension = ".flac",
            SizeBytes = 1,
            ModifiedUtcTicks = 1,
            DurationSeconds = 600,
            TrackNumber = (uint)at,
            Genre = genre,
        });
    }

    /// <summary>Classical is not a mood row: its band would be only six songs on a real shelf. It is a
    /// plain question of the track rows instead.</summary>
    [Fact]
    public void The_classical_list_holds_the_classical()
    {
        AddClassical("sym", 1, "Classical");
        AddClassical("sym", 2, "Classical Crossover");
        AddClassical("rock", 1, "Rock");

        var classical = _db.QuerySmartPlaylist(
            SmartPlaylistId.WholeShelf(SmartPlaylistKind.Classical), nowUtcTicks: 0, advance: true);

        // Both, including the crossover that the folded shelf label would have discarded, and in the
        // record's own order, which is what a work needs and what a mood band cannot give it.
        Assert.Equal(["sym/01.flac", "sym/02.flac"], classical.Select(t => t.RelativePath));
    }

    /// <summary>A whole work, not one movement of it. The mood lists take one song off any record; this
    /// is the reason classical is not one of them.</summary>
    [Fact]
    public void The_classical_list_takes_a_whole_work_rather_than_one_movement()
    {
        for (var i = 1; i <= 8; i++)
            AddClassical("sym", i, "Classical");

        Assert.Equal(8, _db.QuerySmartPlaylist(
            SmartPlaylistId.WholeShelf(SmartPlaylistKind.Classical), nowUtcTicks: 0, advance: true).Count);
    }
}
