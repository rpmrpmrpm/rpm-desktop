using RPM.Core.Analysis;
using RPM.Core.Library;
using RPM.Core.Metadata.Providers;

namespace RPM.Core.Tests;

/// <summary>
/// Who a song on a compilation is by, for the per-artist cap and for a refusal.
///
/// <para><b>Why these exist.</b> A generated list holds at most five songs by any one act. If
/// "Various Artists" is read as an act, every compilation in a collection shares one place in that
/// count. On a real shelf, 45 compilations with 883 different acts become two credits, and the
/// compilations almost disappear from the generated lists.</para>
/// </summary>
public class CompilationCreditTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public CompilationCreditTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmvarious-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── the rule itself ──

    [Theory]
    [InlineData("Various Artists")]
    [InlineData("Various")]
    [InlineData("Various Artist")]
    [InlineData("various artists")]
    [InlineData("VARIOUS ARTISTS")]
    public void A_compilation_credit_falls_through_to_the_track(string albumArtist)
    {
        Assert.Equal("Nina Simone", RecommendRefusals.CreditOf(albumArtist, "Nina Simone"));
    }

    [Fact]
    public void A_record_with_one_act_still_uses_the_album_credit()
    {
        Assert.Equal("Fleetwood Mac", RecommendRefusals.CreditOf("Fleetwood Mac", "Lindsey Buckingham"));
    }

    /// <summary>A compilation track with no artist of its own names nobody. Empty, not the marker,
    /// because the cap and the refusal both read an empty credit as "not one act", which is correct
    /// here.</summary>
    [Fact]
    public void A_compilation_track_with_no_artist_names_nobody()
    {
        Assert.Equal("", RecommendRefusals.CreditOf("Various Artists", ""));
        Assert.Equal("", RecommendRefusals.CreditOf("Various Artists", null));
    }

    // ── what it does to a list ──

    private static LibraryTrack Track(string path, string folder, string? albumArtist, string artist) => new()
    {
        RelativePath = path,
        Folder = folder,
        FileName = path,
        Extension = ".flac",
        SizeBytes = 100,
        ModifiedUtcTicks = 1,
        DurationSeconds = 240,
        AlbumArtist = albumArtist,
        Artist = artist,
        Genre = "Rock",
    };

    /// <summary>
    /// The full case, end to end: a shelf of only compilations, and a list that holds more than ten
    /// of them.
    ///
    /// <para>Two hundred records, one act each, all credited to "Various Artists". The calm band is
    /// the quietest <c>MoodBands.CalmAt</c> of them, so about thirty songs are eligible. If the marker
    /// counted as one act, the list would hold only five.
    /// </para>
    ///
    /// <para>The shelf must be this big for the test to mean anything. At thirty records the band
    /// itself is five songs, which is also the cap, and a test that cannot tell those two apart passes
    /// whatever the code does.</para>
    /// </summary>
    [Fact]
    public void A_shelf_of_compilations_fills_a_list()
    {
        Ramp(200, i => $"act {i:D3}");

        var calm = _db.QuerySmartPlaylist(
            SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm), nowUtcTicks: 0, advance: true);

        Assert.True(
            calm.Count > SmartPlaylists.MostFromOneArtist,
            $"only {calm.Count} songs came back, which is the per-artist cap treating every " +
            "compilation as one act");
        Assert.True(calm.Count >= 20, $"the band should be about thirty songs, got {calm.Count}");
    }

    /// <summary>A shelf of compilations, one record and one song each, spread evenly across the range
    /// so the bands have something to cut.</summary>
    private void Ramp(int records, Func<int, string> artist)
    {
        for (var i = 0; i < records; i++)
        {
            var path = $"comp-{i:D3}/01.flac";
            _db.Upsert(Track(path, $"comp-{i:D3}", "Various Artists", artist(i)));
            var at = (i + 1) / (records + 1.0);
            _db.SaveTrackMood(path, 100, 1, new MoodReading(at, at * 10, at * 20));
        }
    }

    /// <summary>The cap still works, on the act and not on the marker: one artist who appears on many
    /// compilations is still limited to five.</summary>
    [Fact]
    public void The_cap_still_holds_one_act_across_many_compilations()
    {
        Ramp(200, _ => "The Same Band");

        var calm = _db.QuerySmartPlaylist(
            SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm), nowUtcTicks: 0, advance: true);

        Assert.Equal(SmartPlaylists.MostFromOneArtist, calm.Count);
    }

    // ── the two readings of the credit ──

    /// <summary>
    /// The SQL clause and <see cref="RecommendRefusals.CreditOf"/> have to name the same act, or a
    /// refusal written from a mood list does nothing in a favourites list.
    ///
    /// <para>Checked against the database, not the string, because what matters is how SQLite
    /// evaluates the expression, not how it looks.</para>
    /// </summary>
    [Theory]
    [InlineData("Various Artists", "Nina Simone", "Nina Simone")]
    [InlineData("various artists", "Nina Simone", "Nina Simone")]
    [InlineData("Various-Artists", "Nina Simone", "Nina Simone")]
    [InlineData("Fleetwood Mac", "Lindsey Buckingham", "Fleetwood Mac")]
    [InlineData("", "Nina Simone", "Nina Simone")]
    [InlineData("Various Artists", "", "")]
    public void The_sql_and_the_code_name_the_same_act(string albumArtist, string artist, string expected)
    {
        _db.Upsert(Track("a/1.flac", "a", albumArtist, artist));

        Assert.Equal(expected, RecommendRefusals.CreditOf(albumArtist, artist));

        // A refusal written under the name the code composes must remove the row from a list the SQL
        // narrows. This is the only way to find out which name the SQL produced.
        if (expected.Length == 0)
            return;

        _db.SetRecommendRefusal(RecommendScope.Artist, expected, SmartPlaylistKind.FavoriteTracks, true, 1);
        _db.SetTrackFavorite("a/1.flac", true, 1);

        var favourites = _db.QuerySmartPlaylist(
            SmartPlaylistId.WholeShelf(SmartPlaylistKind.FavoriteTracks), nowUtcTicks: 0, advance: true);

        Assert.DoesNotContain(favourites, t => t.RelativePath == "a/1.flac");
    }

    /// <summary>The marker's spellings are one list, read by the code and by the clause built from it.
    /// A spelling added to <see cref="ArtistCredit.VariousSpellings"/> has to reach both.</summary>
    [Fact]
    public void Every_spelling_of_the_marker_is_read_as_one()
    {
        foreach (var spelling in ArtistCredit.VariousSpellings)
        {
            Assert.True(ArtistCredit.IsVariousArtists(spelling), spelling);
            Assert.Equal("act", RecommendRefusals.CreditOf(spelling, "act"));
        }
    }
}
