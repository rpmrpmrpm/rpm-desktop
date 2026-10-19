using System.Diagnostics;
using RPM.Core.Metadata;
using RPM.Core.Models;

namespace RPM.Core.Tests;

/// <summary>
/// The RPM id has to survive in every container the scanner accepts, and each one carries it
/// differently (ID3v2 UFID, Vorbis comment, MP4 freeform atom, APE item). These tests use real
/// encoded files, because a hand-built header would not prove that TagLib can round-trip the frame.
/// They need ffmpeg and are skipped without it.
/// </summary>
public class RpmTagTests : IDisposable
{
    private readonly string _root;

    public RpmTagTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"gidtag-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static bool FfmpegAvailable
    {
        get
        {
            try
            {
                var psi = new ProcessStartInfo("ffmpeg")
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi)!;
                p.WaitForExit();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>One second of a sine tone in the requested container, or null when this ffmpeg
    /// build cannot encode it (wma and wavpack are not in every build).</summary>
    private string? Encode(string extension, string? codec = null)
    {
        var path = Path.Combine(_root, $"tone{extension}");
        var args = $"-y -f lavfi -i sine=frequency=440:duration=1 "
                 + (codec is null ? "" : $"-c:a {codec} ")
                 + $"\"{path}\"";
        var psi = new ProcessStartInfo("ffmpeg", args)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode == 0 && File.Exists(path) ? path : null;
    }

    [Theory]
    [InlineData(".mp3", null)]     // ID3v2 UFID
    [InlineData(".wav", null)]     // ID3v2 UFID in a RIFF chunk
    [InlineData(".aiff", null)]    // ID3v2 UFID
    [InlineData(".flac", null)]        // Vorbis comment
    [InlineData(".ogg", "libvorbis")]  // Vorbis comment, via the Ogg GroupedComment
    [InlineData(".opus", "libopus")]   // same, and the codec ffmpeg builds actually ship
    [InlineData(".m4a", "aac")]        // MP4 freeform atom
    [InlineData(".wv", "wavpack")] // APE item
    [InlineData(".wma", "wmav2")]  // ASF descriptor
    public void Id_roundtrips_through_every_container(string extension, string? codec)
    {
        if (!FfmpegAvailable) return;
        var path = Encode(extension, codec);
        if (path is null) return; // this ffmpeg build cannot make the container, nothing to test

        var stamped = TagReader.StampRpmId(path);

        Assert.False(string.IsNullOrWhiteSpace(stamped), $"{extension}: no carrier for the id");
        Assert.Equal(stamped, TagReader.Read(path).RpmId);
    }

    [Fact]
    public void Stamping_twice_keeps_the_first_id()
    {
        if (!FfmpegAvailable) return;
        var path = Encode(".mp3")!;

        var first = TagReader.StampRpmId(path);
        var second = TagReader.StampRpmId(path);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Forced_stamp_mints_a_fresh_id()
    {
        if (!FfmpegAvailable) return;
        var path = Encode(".mp3")!;

        var first = TagReader.StampRpmId(path);
        var forced = TagReader.StampRpmId(path, force: true);

        Assert.NotNull(forced);
        Assert.NotEqual(first, forced);
        Assert.Equal(forced, TagReader.Read(path).RpmId);
    }

    [Fact]
    public void Writing_tags_leaves_the_file_with_an_id()
    {
        if (!FfmpegAvailable) return;
        var path = Encode(".mp3")!;

        // The plain tag write path never mentions the id, but it still must not leave a file without
        // one, because a file with no id has no durable link back to its library row.
        TagReader.Write(path, new TrackInfo
        {
            FilePath = path,
            Extension = ".mp3",
            Title = "Hysteria",
            Artist = "Muse",
        });

        Assert.False(string.IsNullOrWhiteSpace(TagReader.Read(path).RpmId));
    }

    [Fact]
    public void Writing_tags_preserves_an_existing_id()
    {
        if (!FfmpegAvailable) return;
        var path = Encode(".mp3")!;
        var id = TagReader.StampRpmId(path);

        TagReader.WriteAll(path, TagReader.Read(path) with { Title = "Retagged" });

        var after = TagReader.Read(path);
        Assert.Equal(id, after.RpmId);
        Assert.Equal("Retagged", after.Title);
    }

    [Fact]
    public void Unreadable_file_stamps_nothing()
    {
        var path = Path.Combine(_root, "not-audio.mp3");
        File.WriteAllText(path, "this is not an mp3");

        Assert.Null(TagReader.StampRpmId(path));
    }

    [Fact]
    public void Ogg_flac_cannot_be_stamped_at_all()
    {
        // TagLib does not know the FLAC-in-Ogg codec and throws when opening the file, so such a
        // track can carry no id (and no tags of any kind). Sync marks it NoCarrier and does not retry
        // it forever. This test shows that the null it keys off is real.
        if (!FfmpegAvailable) return;
        var path = Encode(".ogg", "flac"); // what ffmpeg produces for .ogg by default
        if (path is null) return;

        Assert.Null(TagReader.StampRpmId(path));
        Assert.Null(TagReader.Read(path).RpmId);
    }
}
