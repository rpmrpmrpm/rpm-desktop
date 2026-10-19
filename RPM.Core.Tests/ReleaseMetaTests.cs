using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Core.Metadata.Providers;

namespace RPM.Core.Tests;

/// <summary>
/// What a catalogue says about a release, kept against the release and not against the folder that
/// happens to hold it, and mapped onto RPM's own style list without losing the provider's own words.
/// </summary>
public class ReleaseMetaTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public ReleaseMetaTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmrel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── the mapper ──

    [Fact]
    public void MapAll_keeps_every_style_where_PickPair_keeps_one()
    {
        var mapped = Genres.MapAll(["Rock"], ["Soft Rock", "Folk Rock", "Art Rock"]);

        Assert.Equal("Rock", mapped.Genre);
        Assert.Equal(["Soft Rock", "Folk Rock", "Art Rock"], mapped.Styles.Select(s => s.Name));
    }

    [Fact]
    public void PickPair_still_answers_exactly_what_it_did_before()
    {
        // PickPair calls MapAll, so this guards the two-value answer that MapAll must keep giving.
        var pair = Genres.PickPair(["Rock"], ["Soft Rock", "Folk Rock"]);

        Assert.Equal("Rock", pair.Genre);
        Assert.Equal("Soft Rock", pair.Subgenre);
    }

    [Fact]
    public void A_style_names_the_shelf_when_the_genres_do_not()
    {
        // A provider that gives only "Deep House" has told us the record is House.
        var mapped = Genres.MapAll([], ["Deep House"]);

        Assert.Equal("House", mapped.Genre);
        Assert.Equal(["Deep House"], mapped.Styles.Select(s => s.Name));
    }

    [Fact]
    public void A_recognised_genre_is_never_overruled_by_a_style()
    {
        // Discogs files plenty of rock records with an electronic style on them.
        var mapped = Genres.MapAll(["Rock"], ["Synthwave"]);

        Assert.Equal("Rock", mapped.Genre);
        Assert.Equal(["Synthwave"], mapped.Styles.Select(s => s.Name));
    }

    [Fact]
    public void Words_that_are_not_styles_are_dropped_rather_than_shelved()
    {
        // "Dance" and "Hip-Hop" are shelf names, and a bare modifier is not a style at all. The
        // vocabulary is closed: only values in the list count.
        var mapped = Genres.MapAll(["Electronic"], ["Dance", "Deep House", "Abstract"]);

        Assert.Equal(["Deep House"], mapped.Styles.Select(s => s.Name));
    }

    [Fact]
    public void One_style_reached_twice_is_one_style()
    {
        var mapped = Genres.MapAll(["Deep House"], ["Deep House"]);

        Assert.Single(mapped.Styles);
    }

    [Fact]
    public void Nothing_recognised_is_an_answer_and_not_a_crash()
    {
        var mapped = Genres.MapAll(["Zzzz"], ["Qqqq"]);

        Assert.Null(mapped.Genre);
        Assert.Empty(mapped.Styles);
    }

    // ── the row ──

    [Fact]
    public void A_release_keeps_the_providers_own_words_beside_our_reading_of_them()
    {
        _db.SetReleaseMeta(DiscogsJson.ProviderId, "12345", ["Rock"], ["Soft Rock", "Abstract"]);

        var row = _db.ReadReleaseMeta(DiscogsJson.ProviderId, "12345")!;

        Assert.Equal("Rock", row.RawGenres);
        Assert.Equal("Soft Rock; Abstract", row.RawStyles);   // as Discogs said it, "Abstract" included
        Assert.Equal("Rock", row.MappedGenre);
        Assert.Equal(["Soft Rock"], row.Styles);              // as RPM reads it, "Abstract" dropped
        Assert.Equal(Subgenres.Version, row.VocabVersion);
    }

    [Fact]
    public void Asking_a_release_twice_replaces_its_row_rather_than_adding_one()
    {
        _db.SetReleaseMeta(DiscogsJson.ProviderId, "1", ["Rock"], ["Soft Rock"]);
        _db.SetReleaseMeta(DiscogsJson.ProviderId, "1", ["Pop"], ["Synth-Pop"]);

        var row = _db.ReadReleaseMeta(DiscogsJson.ProviderId, "1")!;
        Assert.Equal("Pop", row.MappedGenre);
        Assert.Equal(["Synth-Pop"], row.Styles);
    }

    [Fact]
    public void The_same_id_under_two_providers_is_two_releases()
    {
        // A Discogs release number and an MBID share no namespace, which is why the key is a pair.
        _db.SetReleaseMeta(DiscogsJson.ProviderId, "430456", ["Rock"], []);
        _db.SetReleaseMeta(LibraryDatabase.ReleaseGroupProvider, "430456", ["Jazz"], []);

        Assert.Equal("Rock", _db.ReadReleaseMeta(DiscogsJson.ProviderId, "430456")!.MappedGenre);
        Assert.Equal("Jazz", _db.ReadReleaseMeta(LibraryDatabase.ReleaseGroupProvider, "430456")!.MappedGenre);
    }

    [Fact]
    public void A_provider_that_filed_it_under_nothing_still_leaves_a_row()
    {
        // Asked and answered. Without the row, the sweep would ask again forever.
        _db.SetReleaseMeta(DiscogsJson.ProviderId, "9", [], []);

        var row = _db.ReadReleaseMeta(DiscogsJson.ProviderId, "9")!;
        Assert.NotNull(row);
        Assert.Null(row.MappedGenre);
        Assert.Empty(row.Styles);
    }

    [Fact]
    public void Nobody_asked_reads_back_as_null()
        => Assert.Null(_db.ReadReleaseMeta(DiscogsJson.ProviderId, "never"));

    // ── re-mapping without re-fetching ──

    [Fact]
    public void A_row_mapped_by_an_older_list_is_found_and_run_again()
    {
        _db.SetReleaseMeta(DiscogsJson.ProviderId, "7", ["Rock"], ["Soft Rock"]);
        StampOldVocabulary("7");

        Assert.Single(_db.ReleasesToRemap(100));
        Assert.Equal(1, _db.RemapReleases());

        var row = _db.ReadReleaseMeta(DiscogsJson.ProviderId, "7")!;
        Assert.Equal(Subgenres.Version, row.VocabVersion);
        Assert.Equal(["Soft Rock"], row.Styles);      // the provider's words are not fetched again
        Assert.Empty(_db.ReleasesToRemap(100));
    }

    [Fact]
    public void A_row_the_current_list_already_mapped_is_left_alone()
    {
        _db.SetReleaseMeta(DiscogsJson.ProviderId, "8", ["Rock"], ["Soft Rock"]);

        Assert.Empty(_db.ReleasesToRemap(100));
        Assert.Equal(0, _db.RemapReleases());
    }

    // ── what an album folder ends up knowing ──

    [Fact]
    public void An_albums_facts_come_from_the_release_it_is_linked_to()
    {
        _db.SetAlbumPressing("Muse/Absolution", DiscogsJson.ProviderId, "555");
        _db.SetReleaseMeta(DiscogsJson.ProviderId, "555", ["Rock"], ["Alternative Rock", "Art Rock"]);

        var facts = _db.CatalogueFactsByFolder()["Muse/Absolution"];

        Assert.Equal("Rock", facts.Genre);
        Assert.Equal(["Alternative Rock", "Art Rock"], facts.Styles);
    }

    [Fact]
    public void Relinking_an_album_takes_its_facts_with_it()
    {
        // This is why the answer is keyed by the release: an answer keyed by folder would go stale,
        // and nothing on screen could show that.
        _db.SetAlbumPressing("A/B", DiscogsJson.ProviderId, "1");
        _db.SetReleaseMeta(DiscogsJson.ProviderId, "1", ["Rock"], ["Soft Rock"]);
        _db.SetReleaseMeta(DiscogsJson.ProviderId, "2", ["Jazz"], ["Vocal Jazz"]);

        _db.SetAlbumPressing("A/B", DiscogsJson.ProviderId, "2");

        var facts = _db.CatalogueFactsByFolder()["A/B"];
        Assert.Equal("Jazz", facts.Genre);
        Assert.Equal(["Vocal Jazz"], facts.Styles);
    }

    [Fact]
    public void Two_folders_holding_one_release_read_the_same_answer()
    {
        _db.SetAlbumPressing("flac/Muse - Absolution", DiscogsJson.ProviderId, "555");
        _db.SetAlbumPressing("mp3/Muse - Absolution", DiscogsJson.ProviderId, "555");
        _db.SetReleaseMeta(DiscogsJson.ProviderId, "555", ["Rock"], ["Art Rock"]);

        var byFolder = _db.CatalogueFactsByFolder();

        Assert.Equal(["Art Rock"], byFolder["flac/Muse - Absolution"].Styles);
        Assert.Equal(["Art Rock"], byFolder["mp3/Muse - Absolution"].Styles);
    }

    [Fact]
    public void MusicBrainz_answers_where_Discogs_had_nothing_to_say()
    {
        _db.SetAlbumPressing("C/D", DiscogsJson.ProviderId, "3");
        _db.SetAlbumReleaseGroup("C/D", "mbid-1");
        _db.SetReleaseMeta(DiscogsJson.ProviderId, "3", [], []);              // asked, filed under nothing
        _db.SetReleaseMeta(LibraryDatabase.ReleaseGroupProvider, "mbid-1", ["Jazz"], ["Soul Jazz"]);

        var facts = _db.CatalogueFactsByFolder()["C/D"];

        Assert.Equal("Jazz", facts.Genre);
        Assert.Equal(["Soul Jazz"], facts.Styles);
    }

    [Fact]
    public void Discogs_wins_where_both_answered()
    {
        // Narrower is what a mood list needs, and Discogs is the one that draws a styles distinction.
        _db.SetAlbumPressing("E/F", DiscogsJson.ProviderId, "4");
        _db.SetAlbumReleaseGroup("E/F", "mbid-2");
        _db.SetReleaseMeta(DiscogsJson.ProviderId, "4", ["House"], ["Deep House"]);
        _db.SetReleaseMeta(LibraryDatabase.ReleaseGroupProvider, "mbid-2", ["Jazz"], ["Soul Jazz"]);

        Assert.Equal(["Deep House"], _db.CatalogueFactsByFolder()["E/F"].Styles);
    }

    [Fact]
    public void An_album_nobody_asked_about_is_simply_not_in_the_table()
    {
        _db.SetAlbumPressing("G/H", DiscogsJson.ProviderId, "unfetched");

        Assert.False(_db.CatalogueFactsByFolder().ContainsKey("G/H"));
    }

    [Fact]
    public void Coverage_counts_the_albums_linked_the_asked_and_the_answered()
    {
        _db.SetAlbumPressing("one", DiscogsJson.ProviderId, "1");
        _db.SetAlbumPressing("two", DiscogsJson.ProviderId, "2");
        _db.SetAlbumFavorite("three", true);                       // a row, no link
        _db.SetReleaseMeta(DiscogsJson.ProviderId, "1", ["Rock"], ["Soft Rock"]);
        _db.SetReleaseMeta(DiscogsJson.ProviderId, "2", ["Rock"], []);   // asked, no style

        var coverage = _db.CountReleaseCoverage();

        Assert.Equal(3, coverage.Albums);
        Assert.Equal(2, coverage.Linked);
        Assert.Equal(2, coverage.Asked);
        Assert.Equal(1, coverage.WithStyles);
    }

    /// <summary>Ages a row's mapping without touching its raw words, which is what a change to the
    /// style list does. There is no API for it, because only a test ever needs one.</summary>
    private void StampOldVocabulary(string releaseId)
    {
        var path = Path.Combine(_root, ".rpm", "library.db");
        if (!File.Exists(path))
            path = Directory.GetFiles(_root, "*.db", SearchOption.AllDirectories)[0];

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE release_meta SET vocab_version = 1 WHERE release_id = $id";
        cmd.Parameters.AddWithValue("$id", releaseId);
        cmd.ExecuteNonQuery();
    }
}
