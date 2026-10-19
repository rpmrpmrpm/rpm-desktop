using System.Diagnostics;
using RPM.Core.Metadata;
using RPM.Core.Models;
using RPM.Core.Scanning;
using RPM.Core.Splitting;

namespace RPM.Core.Tests;

/// <summary>
/// The whole offline split pipeline against real audio: synthesise a 3-track "album" wav (tones
/// separated by 2 s of silence), detect boundaries with the REAL ffmpeg envelope reader, cut with the
/// real splitter, and verify the resulting files and the preserved original. Skipped when ffmpeg is
/// not installed.
/// </summary>
public class SplitPipelineEndToEndTests : IDisposable
{
    private readonly string _root;
    private static readonly bool FfmpegAvailable = Probe();

    public SplitPipelineEndToEndTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mdtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static bool Probe()
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-version");
            using var p = Process.Start(psi);
            p!.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static void Ffmpeg(params string[] args)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-y");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"fixture ffmpeg failed: {err}");
    }

    [Fact]
    public async Task Album_wav_is_detected_split_and_originals_preserved()
    {
        if (!FfmpegAvailable) return; // environment without ffmpeg, nothing to exercise

        // 3 x 70 s tones with 2 s of silence between them give one 214 s "album" file.
        string album = Path.Combine(_root, "the album.wav");
        Ffmpeg("-f", "lavfi", "-i",
            "sine=frequency=220:duration=70 [a]; " +
            "anullsrc=r=44100:cl=mono:d=2 [s1]; " +
            "sine=frequency=440:duration=70 [b]; " +
            "anullsrc=r=44100:cl=mono:d=2 [s2]; " +
            "sine=frequency=660:duration=70 [c]; " +
            "[a][s1][b][s2][c] concat=n=5:v=0:a=1",
            album);

        var info = TagReader.Read(album);
        Assert.InRange(info.DurationSeconds, 210, 218);

        // Detect (real envelope decode). The long-duration threshold is 20 min, so the candidate is
        // forced the way the dialog does it for any file.
        var candidate = new AlbumFileCandidate(album, info.DurationSeconds, null, AlbumFileHint.None);
        var analyzer = new AlbumSplitAnalyzer(null, null);
        var analysis = await analyzer.AnalyzeOfflineAsync(candidate);

        Assert.Equal(3, analysis.Plan.Tracks.Count);
        Assert.InRange(analysis.Plan.Tracks[1].StartSeconds, 69, 73);
        Assert.InRange(analysis.Plan.Tracks[2].StartSeconds, 141, 145);

        // Split for real.
        var plan = analysis.Plan with
        {
            Album = "E2E Album",
            AlbumArtist = "E2E Artist",
            Tracks = analysis.Plan.Tracks.Select((t, i) => t with { Title = $"Part {i + 1}", Artist = "E2E Artist" }).ToList(),
        };
        var result = AlbumFileSplitter.Split(plan, info);

        Assert.Equal(3, result.NewTracks.Count);
        Assert.False(File.Exists(album));
        Assert.True(File.Exists(result.OriginalMovedTo));
        Assert.True(File.Exists(result.CueWrittenTo!));
        foreach (var (t, i) in result.NewTracks.Select((t, i) => (t, i)))
        {
            Assert.True(File.Exists(t.FilePath));
            var read = TagReader.Read(t.FilePath);
            Assert.Equal($"Part {i + 1}", read.Title);
            Assert.Equal("E2E Album", read.Album);
            Assert.InRange(read.DurationSeconds, 60, 80);
        }

        // A rescan sees the 3 new tracks AND the preserved original: the album a split was made from
        // stays in the tree, in its own Originals folder, so a bad cut can be thrown away and redone.
        var rescan = AudioFileScanner.Scan(_root).Files;
        Assert.Equal(4, rescan.Count);
        Assert.Contains(rescan, p =>
            Path.GetFileName(Path.GetDirectoryName(p)) == AlbumFileSplitter.OriginalsFolderName);
    }
}
