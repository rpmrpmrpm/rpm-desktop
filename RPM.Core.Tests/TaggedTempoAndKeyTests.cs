using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The tempo and the key that the FILE carries, kept where a query can reach them.
///
/// <para>The app reads both on every scan and keeps them in columns, so a collection tagged by DJ
/// software arrives with its answer. These cases pin the two things that matter about those
/// columns: a value survives the round trip through the mirror, and what a file stops claiming, the
/// row stops claiming.</para>
/// </summary>
public class TaggedTempoAndKeyTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public TaggedTempoAndKeyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmtags-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static LibraryTrack Track(string path, double bpm = 0, string? key = null) => new()
    {
        RelativePath = path,
        Folder = "",
        FileName = path,
        Extension = ".flac",
        SizeBytes = 100,
        ModifiedUtcTicks = 1,
        TagBpm = bpm,
        TagKey = key,
    };

    [Fact]
    public void What_the_file_claims_survives_the_round_trip()
    {
        _db.Upsert(Track("a.flac", bpm: 128, key: "8A"));

        var row = _db.Query().Single();

        Assert.Equal(128, row.TagBpm);
        Assert.Equal("8A", row.TagKey);
    }

    /// <summary>A file that says nothing is a row that says nothing. Zero and null are the answer
    /// here, not a read that is still pending: there is nothing for a later pass to come back
    /// for.</summary>
    [Fact]
    public void A_file_that_claims_nothing_leaves_the_row_claiming_nothing()
    {
        _db.Upsert(Track("plain.flac"));

        var row = _db.Query().Single();

        Assert.Equal(0, row.TagBpm);
        Assert.Null(row.TagKey);
    }

    /// <summary>And a tempo taken OUT of a file is taken out of the row. These columns hold what the
    /// music says, so remembering a value that the file has stopped carrying would be the app holding
    /// an opinion of its own in a column that is not for one. This is deliberately unlike the loudness
    /// beside them, which is a reading that this app paid a decode for and that a retag must not
    /// erase.</summary>
    [Fact]
    public void A_tempo_cleared_out_of_a_file_is_cleared_out_of_the_row()
    {
        _db.Upsert(Track("b.flac", bpm: 174, key: "4A"));
        _db.Upsert(Track("b.flac"));

        var row = _db.Query().Single();

        Assert.Equal(0, row.TagBpm);
        Assert.Null(row.TagKey);
    }

    /// <summary>The key is kept in whatever form its tagger wrote it. Parsing belongs to whoever reads
    /// it, and a form this app does not recognise today is one it might recognise tomorrow.</summary>
    [Theory]
    [InlineData("Am")]
    [InlineData("8A")]
    [InlineData("A minor")]
    [InlineData("F#m")]
    public void A_key_is_kept_in_the_form_it_arrived_in(string written)
    {
        _db.Upsert(Track("c.flac", key: written));

        Assert.Equal(written, _db.Query().Single().TagKey);
    }
}
