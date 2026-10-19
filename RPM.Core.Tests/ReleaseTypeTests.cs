using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Core.Models;

namespace RPM.Core.Tests;

/// <summary>A record's release type: MusicBrainz's words, the title guess it falls back to, and the round trip
/// through a file and the library. See <see cref="ReleaseType"/>.</summary>
public class ReleaseTypeTests : IDisposable
{
    private readonly string _root;

    public ReleaseTypeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmreleasetype-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(new[] { "album" }, ReleaseType.Album)]
    [InlineData(new[] { "ep" }, ReleaseType.Ep)]
    [InlineData(new[] { "single" }, ReleaseType.Single)]
    [InlineData(new[] { "album", "live" }, ReleaseType.Live)]
    [InlineData(new[] { "album; compilation" }, ReleaseType.Compilation)]
    [InlineData(new[] { "Album", "Soundtrack" }, ReleaseType.Soundtrack)]
    [InlineData(new[] { "album", "mixtape/street" }, ReleaseType.Mixtape)]
    [InlineData(new[] { "single", "live" }, ReleaseType.Single)]
    [InlineData(new[] { "ep", "live" }, ReleaseType.Live)]
    public void MusicBrainz_words_name_the_type(string[] values, ReleaseType expected)
    {
        Assert.Equal(expected, ReleaseTypes.FromTagValues(values));
    }

    [Fact]
    public void Words_that_name_no_type_this_app_files_by_are_no_type()
    {
        Assert.Null(ReleaseTypes.FromTagValues(["broadcast"]));
        Assert.Null(ReleaseTypes.FromTagValues(["other"]));
        Assert.Null(ReleaseTypes.FromTagValues([]));
    }

    [Fact]
    public void Every_type_reads_back_from_the_words_it_writes()
    {
        foreach (var type in ReleaseTypes.All)
            Assert.Equal(type, ReleaseTypes.FromTagValues(ReleaseTypes.ToTagValues(type)));
    }

    [Fact]
    public void A_set_type_wins_over_the_title_and_the_title_only_guesses_when_nothing_is_set()
    {
        Assert.Equal(ReleaseType.Album, ReleaseTypes.ShelfOf(ReleaseType.Album, "Live Through This"));
        Assert.Equal(ReleaseType.Live, ReleaseTypes.ShelfOf(null, "Live Through This"));
        Assert.Equal(ReleaseType.Album, ReleaseTypes.ShelfOf(null, "Nevermind"));
        Assert.Null(ReleaseTypes.FromTitle("Nevermind"));
    }

    [Fact]
    public void The_stored_name_reads_back_and_nonsense_reads_as_no_type()
    {
        foreach (var type in ReleaseTypes.All)
            Assert.Equal(type, ReleaseTypes.FromStored(ReleaseTypes.ToStored(type)));
        Assert.Null(ReleaseTypes.FromStored("Bootleg"));
        Assert.Null(ReleaseTypes.FromStored(null));
    }

    [Fact]
    public void The_type_is_written_into_an_mp3_and_read_back()
    {
        var path = Mp3("live.mp3");

        TagReader.WriteAll(path, new TrackInfo
        {
            FilePath = path, Extension = ".mp3", Title = "Song", ReleaseType = ReleaseType.Live,
        });

        Assert.Equal(ReleaseType.Live, TagReader.Read(path).ReleaseType);
    }

    [Fact]
    public void Clearing_the_type_takes_it_out_of_the_file()
    {
        var path = Mp3("cleared.mp3");
        TagReader.WriteAll(path, new TrackInfo { FilePath = path, Extension = ".mp3", ReleaseType = ReleaseType.Ep });

        TagReader.WriteAll(path, new TrackInfo { FilePath = path, Extension = ".mp3", ReleaseType = null });

        Assert.Null(TagReader.Read(path).ReleaseType);
    }

    [Fact]
    public void The_library_keeps_the_type_and_hands_back_the_one_most_of_a_records_tracks_carry()
    {
        using var db = new LibraryDatabase(MirrorLocation.InFolder(_root));
        db.Upsert(Track("Band/EP/1.flac", "Band/EP", ReleaseType.Ep));
        db.Upsert(Track("Band/EP/2.flac", "Band/EP", ReleaseType.Ep));
        db.Upsert(Track("Band/EP/3.flac", "Band/EP", ReleaseType.Single));
        db.Upsert(Track("Band/Untyped/1.flac", "Band/Untyped", null));

        Assert.Equal(ReleaseType.Ep, db.FindByRelativePath("Band/EP/1.flac")!.ReleaseType);

        var types = db.QueryReleaseTypesByFolder();
        Assert.Equal(ReleaseType.Ep, types["Band/EP"]);
        Assert.False(types.ContainsKey("Band/Untyped"));
    }

    private string Mp3(string name)
    {
        // One MPEG frame: enough for TagLib to open the file and write an ID3 tag in front of it.
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, frame);
        return path;
    }

    private static LibraryTrack Track(string path, string folder, ReleaseType? type) => new()
    {
        RelativePath = path,
        Folder = folder,
        FileName = Path.GetFileName(path),
        Extension = ".flac",
        SizeBytes = 100,
        ModifiedUtcTicks = 1,
        DurationSeconds = 200,
        ReleaseType = type,
    };
}
