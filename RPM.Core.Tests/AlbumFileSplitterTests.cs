using System.Diagnostics;
using RPM.Core.Art;
using RPM.Core.Metadata;
using RPM.Core.Models;
using RPM.Core.Splitting;

namespace RPM.Core.Tests;

/// <summary>Integration-level splitter tests. Skipped silently when ffmpeg isn't installed
/// (CI without ffmpeg still passes; dev machines exercise the real cut).</summary>
public class AlbumFileSplitterTests : IDisposable
{
    private readonly string _root;
    private static readonly bool FfmpegAvailable = ProbeFfmpeg();

    public AlbumFileSplitterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mdtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static bool ProbeFfmpeg()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-version");
            using var p = Process.Start(psi);
            p!.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>Concatenated three-tone "album": 4s + 4s + 4s with distinct pitches.</summary>
    private string MakeAlbumFile(string name)
    {
        string path = Path.Combine(_root, name);
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("lavfi");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add("sine=frequency=220:duration=12");
        if (Path.GetExtension(name) == ".mp3")
        {
            psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("libmp3lame");
            psi.ArgumentList.Add("-b:a"); psi.ArgumentList.Add("128k");
        }
        psi.ArgumentList.Add(path);
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"fixture ffmpeg failed: {err}");
        return path;
    }

    private static SplitPlan ThreeTrackPlan(string path, string ext) => new(
        path, ext, TotalSeconds: 12,
        Album: "Test Album", AlbumArtist: "Tester", Year: 2020, ReleaseMbid: null,
        new[]
        {
            new SplitTrack(0, 4, "Alpha", "Tester", 1, 1.0, BoundarySource.CueSheet),
            new SplitTrack(4, 8, "Beta", "Tester", 2, 1.0, BoundarySource.CueSheet),
            new SplitTrack(8, 12, "Gamma", "Tester", 3, 1.0, BoundarySource.CueSheet),
        });

    /// <summary>Stand-in sleeve. TagLib stores whatever bytes it is handed, so what these assert is
    /// that the right picture reached the file, not that it is a decodable image.</summary>
    private static byte[] ArtBytes(byte marker) => [.. Enumerable.Repeat(marker, 512)];

    /// <summary>A plan cover at one size. The fetcher handles the two size tiers; the splitter only
    /// has to embed one of them and write the other.</summary>
    private static Sleeve PlanCover(byte marker, string mime = "image/png") =>
        Sleeve.Of(new AlbumArt(ArtBytes(marker), mime));

    /// <summary>
    /// The reason <see cref="SplitPlan.Art"/> exists. A recorded record has no tags and so no art of
    /// its own, and everything downstream (folder images, the art display, the library) reads the
    /// picture from the track files. If the sleeve is not embedded at the cut, the album never has one.
    /// </summary>
    [Fact]
    public void A_source_with_no_art_takes_the_cover_from_the_plan()
    {
        if (!FfmpegAvailable) return; // no ffmpeg here, so nothing to test
        string source = MakeAlbumFile("bare.mp3");
        Assert.Null(TagReader.LoadArt(source));   // a recording starts with nothing, as vinyl does

        var plan = ThreeTrackPlan(source, ".mp3") with { Art = PlanCover(0xA1) };
        var result = AlbumFileSplitter.Split(plan, new TrackInfo { FilePath = source, Extension = ".mp3", DurationSeconds = 12 });

        Assert.All(result.NewTracks, t => Assert.True(t.HasArt));
        foreach (var track in result.NewTracks)
        {
            var art = TagReader.LoadArt(track.FilePath);
            Assert.NotNull(art);
            Assert.Equal(ArtBytes(0xA1), art!.Value.Bytes);
            Assert.Equal("image/png", art.Value.Mime);
        }
    }

    /// <summary>The file being cut is the album, so a picture inside it is this album's picture. The
    /// plan's cover is a fallback and must not overrule one.</summary>
    [Fact]
    public void The_sources_own_art_beats_the_plans()
    {
        if (!FfmpegAvailable) return; // no ffmpeg here, so nothing to test
        string source = MakeAlbumFile("tagged.mp3");
        TagReader.WriteAll(source, new TrackInfo
        {
            FilePath = source,
            Extension = ".mp3",
            DurationSeconds = 12,
            Title = "Album",
            PendingArt = ArtBytes(0xEE),
            PendingArtMime = "image/jpeg",
        });

        var plan = ThreeTrackPlan(source, ".mp3") with { Art = PlanCover(0xA1) };
        var result = AlbumFileSplitter.Split(plan, new TrackInfo { FilePath = source, Extension = ".mp3", DurationSeconds = 12 });

        var art = TagReader.LoadArt(result.NewTracks[0].FilePath);
        Assert.NotNull(art);
        Assert.Equal(ArtBytes(0xEE), art!.Value.Bytes);
    }

    /// <summary>No art anywhere is the ordinary case, not a failure: the split still happens and the
    /// tracks simply say they have no picture.</summary>
    [Fact]
    public void No_art_at_all_still_splits()
    {
        if (!FfmpegAvailable) return; // no ffmpeg here, so nothing to test
        string source = MakeAlbumFile("none.mp3");

        var result = AlbumFileSplitter.Split(ThreeTrackPlan(source, ".mp3"),
            new TrackInfo { FilePath = source, Extension = ".mp3", DurationSeconds = 12 });

        Assert.Equal(3, result.NewTracks.Count);
        Assert.All(result.NewTracks, t => Assert.False(t.HasArt));
    }

    /// <summary>
    /// The second cut, after the first came out wrong: the user deleted the tracks and split the
    /// album again, directly from the Originals folder it was kept in. The new tracks belong beside
    /// the album's other files, NOT inside Originals (nothing in there is ever imported), and the
    /// album itself does not move, because it is already where it is kept.
    /// </summary>
    [Fact]
    public void Re_splitting_a_preserved_album_puts_the_tracks_back_where_the_album_lives()
    {
        if (!FfmpegAvailable) return;
        string album = MakeAlbumFile("album.wav");
        string originals = Path.Combine(_root, AlbumFileSplitter.OriginalsFolderName);
        Directory.CreateDirectory(originals);
        string preserved = Path.Combine(originals, "album.wav");
        File.Move(album, preserved);

        var info = new TrackInfo { FilePath = preserved, Extension = ".wav", DurationSeconds = 12 };
        var result = AlbumFileSplitter.Split(ThreeTrackPlan(preserved, ".wav"), info);

        Assert.Equal(preserved, result.OriginalMovedTo);
        Assert.True(File.Exists(preserved));
        Assert.All(result.NewTracks, t =>
            Assert.Equal(_root, Path.GetDirectoryName(t.FilePath)));
        Assert.All(result.NewTracks, t => Assert.True(File.Exists(t.FilePath)));

        // No Originals folder inside the Originals folder.
        Assert.False(Directory.Exists(Path.Combine(originals, AlbumFileSplitter.OriginalsFolderName)));
    }

    [Fact]
    public void Splits_wav_into_tagged_tracks_and_preserves_original()
    {
        if (!FfmpegAvailable) return; // no ffmpeg here, so nothing to test
        string source = MakeAlbumFile("album.wav");
        var info = new TrackInfo { FilePath = source, Extension = ".wav", DurationSeconds = 12 };

        var result = AlbumFileSplitter.Split(ThreeTrackPlan(source, ".wav"), info);

        Assert.Equal(3, result.NewTracks.Count);
        Assert.All(result.NewTracks, t => Assert.True(File.Exists(t.FilePath)));
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(result.OriginalMovedTo));
        Assert.Contains(AlbumFileSplitter.OriginalsFolderName, result.OriginalMovedTo);
        Assert.NotNull(result.CueWrittenTo);
        Assert.True(File.Exists(result.CueWrittenTo!));

        // Durations near 4s each (wav re-encode = sample-exact).
        foreach (var t in result.NewTracks)
        {
            var read = TagReader.Read(t.FilePath);
            Assert.InRange(read.DurationSeconds, 3.8, 4.2);
        }

        // Tags stamped.
        var first = TagReader.Read(result.NewTracks[0].FilePath);
        Assert.Equal("Alpha", first.Title);
        Assert.Equal("Test Album", first.Album);
        Assert.Equal(1u, first.TrackNumber);
        Assert.Equal(3u, first.TrackCount);
        Assert.Equal(2020u, first.Year);

        // File naming: same artist everywhere → "NN - Title.ext".
        Assert.EndsWith("01 - Alpha.wav", result.NewTracks[0].FilePath);

        // No leftover temp dir.
        Assert.Empty(Directory.GetDirectories(_root, ".mdsplit-*"));
    }

    [Fact]
    public void Splits_mp3_with_stream_copy()
    {
        if (!FfmpegAvailable) return; // no ffmpeg here, so nothing to test
        string source = MakeAlbumFile("album.mp3");
        var info = new TrackInfo { FilePath = source, Extension = ".mp3", DurationSeconds = 12 };

        var result = AlbumFileSplitter.Split(ThreeTrackPlan(source, ".mp3"), info);

        Assert.Equal(3, result.NewTracks.Count);
        foreach (var t in result.NewTracks)
        {
            var read = TagReader.Read(t.FilePath);
            Assert.InRange(read.DurationSeconds, 3.5, 4.5); // frame-boundary tolerance
        }
    }

    [Fact]
    public void Splits_to_target_format_reencodes_lossless_source_to_mp3()
    {
        if (!FfmpegAvailable) return; // no ffmpeg here, so nothing to test
        string source = MakeAlbumFile("side.flac");
        var info = new TrackInfo { FilePath = source, Extension = ".flac", DurationSeconds = 12 };

        // The vinyl wizard's path: a lossless FLAC intermediate cut into the user's chosen format.
        var result = AlbumFileSplitter.Split(ThreeTrackPlan(source, ".flac"), info,
            targetFormat: RPM.Core.Ripping.RipFormat.Mp3);

        Assert.Equal(3, result.NewTracks.Count);
        Assert.All(result.NewTracks, t => Assert.EndsWith(".mp3", t.FilePath));
        Assert.All(result.NewTracks, t => Assert.Equal(".mp3", t.Extension));
        foreach (var t in result.NewTracks)
        {
            Assert.True(File.Exists(t.FilePath));
            var read = TagReader.Read(t.FilePath);
            Assert.InRange(read.DurationSeconds, 3.7, 4.3);
        }
    }

    [Fact]
    public void Failure_rolls_back_and_leaves_original_untouched()
    {
        if (!FfmpegAvailable) return; // no ffmpeg here, so nothing to test
        string source = MakeAlbumFile("album.wav");
        var info = new TrackInfo { FilePath = source, Extension = ".wav", DurationSeconds = 12 };

        // Second track starts past EOF, so ffmpeg produces no output and the whole split must fail.
        var badPlan = new SplitPlan(source, ".wav", 12, "A", "B", 0, null, new[]
        {
            new SplitTrack(0, 4, "Ok", "B", 1, 1.0, BoundarySource.Manual),
            new SplitTrack(500, 504, "PastEnd", "B", 2, 1.0, BoundarySource.Manual),
        });

        Assert.ThrowsAny<Exception>(() => AlbumFileSplitter.Split(badPlan, info));

        Assert.True(File.Exists(source));
        Assert.False(Directory.Exists(Path.Combine(_root, AlbumFileSplitter.OriginalsFolderName)));
        Assert.Empty(Directory.GetDirectories(_root, ".mdsplit-*"));
    }

    [Fact]
    public void Rejects_single_track_plan()
    {
        var plan = new SplitPlan("/x/a.mp3", ".mp3", 100, null, null, 0, null,
            new[] { new SplitTrack(0, 100, "Only", null, 1, 1.0, BoundarySource.Manual) });
        Assert.Throws<InvalidOperationException>(() =>
            AlbumFileSplitter.Split(plan, new TrackInfo { FilePath = "/x/a.mp3", Extension = ".mp3" }));
    }

    [Fact]
    public void Generated_cue_round_trips_through_parser()
    {
        var plan = ThreeTrackPlan("/x/album.flac", ".flac");
        string cueText = AlbumFileSplitter.BuildCueSheet(plan, "album.flac");

        var parsed = CueSheetParser.Parse(cueText)!;

        Assert.Equal("Test Album", parsed.Title);
        Assert.Equal("Tester", parsed.Performer);
        Assert.Equal("album.flac", parsed.AudioFileName);
        Assert.Equal(3, parsed.Tracks.Count);
        Assert.Equal(4.0, parsed.Tracks[1].Index01Seconds, 2);
        Assert.Equal(8.0, parsed.Tracks[2].Index01Seconds, 2);
    }
}
