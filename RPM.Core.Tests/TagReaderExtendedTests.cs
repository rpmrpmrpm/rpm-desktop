using RPM.Core.Import;
using RPM.Core.Metadata;
using RPM.Core.Models;

namespace RPM.Core.Tests;

/// <summary>
/// Round-trip tests for the extended tag surface, on a real minimal MP3 written to disk (TagLib
/// needs a genuine file structure; a 1-frame silent MP3 payload keeps it tiny).
/// </summary>
public class TagReaderExtendedTests : IDisposable
{
    private readonly string _root;

    public TagReaderExtendedTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mdtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>A minimal valid MPEG-1 Layer III frame (silence), enough for TagLib to accept the
    /// file.</summary>
    private static byte[] MinimalMp3()
    {
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00; // 128kbps 44.1kHz
        return frame;
    }

    private string CreateMp3(string name = "test.mp3")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, MinimalMp3());
        return path;
    }

    [Fact]
    public void WriteAll_then_Read_roundtrips_every_field()
    {
        var path = CreateMp3();
        var info = new TrackInfo
        {
            FilePath = path,
            Extension = ".mp3",
            Title = "Hysteria",
            Artist = "Muse",
            AlbumArtist = "Muse",
            Album = "Absolution",
            Genre = "Rock",
            Composers = "Matt Bellamy",
            Conductor = "Nobody",
            Comment = "test comment",
            Lyrics = "line one\nline two",
            Grouping = "Group A",
            Copyright = "2003 WMG",
            Publisher = "Warner",
            RemixedBy = "Someone",
            Isrc = "GBAHT0300123",
            ReleaseCountry = "GB",
            TrackNumber = 8,
            TrackCount = 14,
            DiscNumber = 1,
            DiscCount = 2,
            DiscSubtitle = "The Studio Album",
            Year = 2003,
            Bpm = 94,
            TagsEdited = true,
        };

        TagReader.WriteAll(path, info);
        var read = TagReader.Read(path);

        Assert.Equal("Hysteria", read.Title);
        Assert.Equal("Muse", read.Artist);
        Assert.Equal("Muse", read.AlbumArtist);
        Assert.Equal("Absolution", read.Album);
        Assert.Equal("Rock", read.Genre);
        Assert.Equal("Matt Bellamy", read.Composers);
        Assert.Equal("Nobody", read.Conductor);
        Assert.Equal("test comment", read.Comment);
        Assert.Equal("line one\nline two", read.Lyrics!.Replace("\r\n", "\n"));
        Assert.Equal("Group A", read.Grouping);
        Assert.Equal("2003 WMG", read.Copyright);
        Assert.Equal("Warner", read.Publisher);
        Assert.Equal("Someone", read.RemixedBy);
        Assert.Equal("GBAHT0300123", read.Isrc);
        Assert.Equal("GB", read.ReleaseCountry);
        Assert.Equal(8u, read.TrackNumber);
        Assert.Equal(14u, read.TrackCount);
        Assert.Equal(1u, read.DiscNumber);
        Assert.Equal(2u, read.DiscCount);
        Assert.Equal("The Studio Album", read.DiscSubtitle);
        Assert.Equal(2003u, read.Year);
        Assert.Equal(94u, read.Bpm);
    }

    /// <summary>The disc subtitle has no first-class TagLib property; it rides in the TSST frame. So
    /// prove that it round-trips like the rest, and clears when the user empties it.</summary>
    [Fact]
    public void Disc_subtitle_roundtrips_and_clears()
    {
        var path = CreateMp3();
        TagReader.WriteAll(path, new TrackInfo
        {
            FilePath = path, Extension = ".mp3", Title = "T", Artist = "A",
            DiscNumber = 2, DiscSubtitle = "B-Sides",
        });
        Assert.Equal("B-Sides", TagReader.Read(path).DiscSubtitle);

        TagReader.WriteAll(path, new TrackInfo
        {
            FilePath = path, Extension = ".mp3", Title = "T", Artist = "A",
            DiscNumber = 2, DiscSubtitle = null,
        });
        Assert.Null(TagReader.Read(path).DiscSubtitle);
    }

    [Fact]
    public void WriteAll_clears_fields_left_empty()
    {
        var path = CreateMp3();
        TagReader.WriteAll(path, new TrackInfo
        {
            FilePath = path, Extension = ".mp3",
            Title = "T", Artist = "A", Comment = "junk comment",
        });

        // User clears the comment in the wanted state
        TagReader.WriteAll(path, new TrackInfo
        {
            FilePath = path, Extension = ".mp3",
            Title = "T", Artist = "A", Comment = null,
        });

        var read = TagReader.Read(path);
        Assert.Equal("T", read.Title);
        Assert.Null(read.Comment);
    }

    [Fact]
    public void Merge_Write_keeps_existing_values_for_missing_fields()
    {
        var path = CreateMp3();
        TagReader.WriteAll(path, new TrackInfo
        {
            FilePath = path, Extension = ".mp3",
            Title = "T", Artist = "A", Album = "Old Album",
        });

        // Recognition merge: no album in the result, existing album must survive
        TagReader.Write(path, new TrackInfo
        {
            FilePath = path, Extension = ".mp3",
            Title = "New T", Artist = "New A",
        });

        var read = TagReader.Read(path);
        Assert.Equal("New T", read.Title);
        Assert.Equal("New A", read.Artist);
        Assert.Equal("Old Album", read.Album);
    }

    [Fact]
    public void PendingArt_roundtrips_and_sets_HasArt()
    {
        var path = CreateMp3();
        var art = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4 }; // jpeg-ish bytes

        TagReader.WriteAll(path, new TrackInfo
        {
            FilePath = path, Extension = ".mp3",
            Title = "T", Artist = "A",
            PendingArt = art, PendingArtMime = "image/jpeg",
        });

        var read = TagReader.Read(path);
        Assert.True(read.HasArt);
        var loaded = TagReader.LoadArt(path);
        Assert.NotNull(loaded);
        Assert.Equal(art, loaded!.Value.Bytes);
    }

    [Fact]
    public void PendingArt_empty_removes_art()
    {
        var path = CreateMp3();
        TagReader.WriteAll(path, new TrackInfo
        {
            FilePath = path, Extension = ".mp3", Title = "T", Artist = "A",
            PendingArt = new byte[] { 1, 2, 3 }, PendingArtMime = "image/jpeg",
        });
        TagReader.WriteAll(path, new TrackInfo
        {
            FilePath = path, Extension = ".mp3", Title = "T", Artist = "A",
            PendingArt = Array.Empty<byte>(),
        });

        Assert.False(TagReader.Read(path).HasArt);
        Assert.Null(TagReader.LoadArt(path));
    }

    [Fact]
    public void Applier_writes_edited_tags_for_in_place_files()
    {
        var path = CreateMp3("keep.mp3");
        var track = TagReader.Read(path) with { Title = "Edited", Artist = "Editor", TagsEdited = true };
        var suggestion = new TrackSuggestion
        {
            Track = track,
            ProposedRelativePath = "keep.mp3",
            IsAlreadyInPlace = true,
        };

        var result = OrganizeApplier.Apply(suggestion, _root, OrganizeMode.Move);

        Assert.True(result.Success);
        var read = TagReader.Read(path);
        Assert.Equal("Edited", read.Title);
        Assert.Equal("Editor", read.Artist);
    }

    [Fact]
    public void FolderArtWriter_writes_cover_from_first_track_with_art()
    {
        var sub = Path.Combine(_root, "Album");
        Directory.CreateDirectory(sub);
        var track = Path.Combine(sub, "01.mp3");
        File.WriteAllBytes(track, MinimalMp3());
        TagReader.WriteAll(track, new TrackInfo
        {
            FilePath = track, Extension = ".mp3", Title = "T", Artist = "A",
            PendingArt = new byte[] { 9, 9, 9 }, PendingArtMime = "image/jpeg",
        });

        var written = FolderArtWriter.WriteCovers(new[]
        {
            new ApplyResult { SourcePath = track, DestinationPath = track, Success = true },
        });

        Assert.Equal(1, written);
        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(Path.Combine(sub, "cover.jpg")));

        // Second run: cover exists, nothing rewritten
        Assert.Equal(0, FolderArtWriter.WriteCovers(new[]
        {
            new ApplyResult { SourcePath = track, DestinationPath = track, Success = true },
        }));
    }

    // ── nothing here to read ──
    //
    // A track the account holds but this machine has never had (CloudState.CloudOnly) still carries a
    // full path built from the library root, and no part of that path exists here: not the file, not
    // its album folder. Such a path must not reach TagLib, which would throw
    // DirectoryNotFoundException from inside the library, and ffmpeg must not be started on the same
    // missing path for a second opinion.

    [Fact]
    public void Read_of_a_path_whose_folder_does_not_exist_is_dataless()
    {
        // The shape a cloud-only row produces: library root + an album folder that was never created here.
        var path = Path.Combine(_root, "Never Downloaded", "01 Track.mp3");

        var info = TagReader.Read(path);

        Assert.Equal(FileAvailability.Dataless, info.Availability);
        Assert.Equal(path, info.FilePath);
        Assert.Equal(".mp3", info.Extension);
        // Nothing was read and nothing was probed: a duration here would mean ffmpeg had been run.
        Assert.Equal(0, info.DurationSeconds);
    }

    [Fact]
    public void Read_of_a_missing_file_in_an_existing_folder_is_dataless()
    {
        var path = Path.Combine(_root, "gone.mp3");

        Assert.Equal(FileAvailability.Dataless, TagReader.Read(path).Availability);
    }

    [Fact]
    public void Read_of_a_stream_url_is_dataless_rather_than_a_download()
    {
        // A radio station's "path" is its stream URL. A tag reader pointed at one either throws or
        // starts pulling the broadcast down looking for an ID3 header.
        var info = TagReader.Read("http://stream.example.com/live.mp3");

        Assert.Equal(FileAvailability.Dataless, info.Availability);
    }

    [Fact]
    public void LoadArt_of_a_path_whose_folder_does_not_exist_is_null()
    {
        var path = Path.Combine(_root, "Never Downloaded", "01 Track.mp3");

        Assert.Null(TagReader.LoadArt(path));
    }

    [Fact]
    public void Read_and_LoadArt_still_work_on_a_file_that_is_here()
    {
        // The guard must not swallow the ordinary case.
        var path = CreateMp3("present.mp3");
        TagReader.WriteAll(path, new TrackInfo
        {
            FilePath = path, Extension = ".mp3", Title = "Here", Artist = "A",
            PendingArt = new byte[] { 1, 2, 3 }, PendingArtMime = "image/jpeg",
        });

        var info = TagReader.Read(path);

        Assert.NotEqual(FileAvailability.Dataless, info.Availability);
        Assert.Equal("Here", info.Title);
        Assert.Equal(new byte[] { 1, 2, 3 }, TagReader.LoadArt(path)!.Value.Bytes);
    }
}
