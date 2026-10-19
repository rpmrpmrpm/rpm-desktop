using RPM.Core.Recording;
using RPM.Core.Splitting;

namespace RPM.Core.Tests;

public class VinylImporterTests
{
    [Fact]
    public void Join_offsets_are_cumulative_side_ends_excluding_the_last()
    {
        Assert.Equal(new[] { 600.0, 1140.0 }, VinylImporter.JoinOffsets(new[] { 600.0, 540.0, 500.0 }));
        Assert.Empty(VinylImporter.JoinOffsets(new[] { 600.0 }));
        Assert.Empty(VinylImporter.JoinOffsets(Array.Empty<double>()));
    }

    [Fact]
    public void Sides_sort_numerically_not_lexically()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"vinyltest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // A tenth side must not sort before the second.
            foreach (var n in new[] { 1, 2, 10 })
                File.WriteAllBytes(VinylImporter.SidePath(dir, n), new byte[] { 0 });
            File.WriteAllText(Path.Combine(dir, "notaside.flac"), "");

            var sides = VinylImporter.SidesIn(dir).Select(Path.GetFileName).ToArray();
            Assert.Equal(new[] { "side1.flac", "side2.flac", "side10.flac" }, sides);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>The no-manifest contract: a session's files are its whole state. Joining preserves the
    /// sides in _originals, and a later run recovers the join offsets from their durations.</summary>
    [Fact]
    public async Task Prepare_session_joins_keeps_sides_and_recovers_offsets_on_reopen()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"vinyltest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // One side: the concat is a copy, so this needs no ffmpeg.
            await File.WriteAllBytesAsync(VinylImporter.SidePath(dir, 1), new byte[] { 1, 2, 3, 4 });

            var album = await VinylImporter.PrepareSessionAsync(dir, durationOf: _ => 600);

            Assert.Equal(Path.Combine(dir, VinylImporter.AlbumFileName), album.FilePath);
            Assert.True(File.Exists(album.FilePath));
            Assert.Empty(VinylImporter.SidesIn(dir));                      // top level is just the album
            string originals = Path.Combine(dir, RPM.Core.Splitting.AlbumFileSplitter.OriginalsFolderName);
            Assert.Single(VinylImporter.SidesIn(originals));               // side kept, not deleted

            // Reopening finds the album and rebuilds state from the kept sides. No manifest is involved.
            var again = await VinylImporter.PrepareSessionAsync(dir, durationOf: _ => 600);
            Assert.Equal(album.FilePath, again.FilePath);
            Assert.Equal(600, again.TotalSeconds, 3);
            Assert.Empty(again.SideJoinSeconds);                           // one side → no internal join
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Prepare_session_without_sides_fails()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"vinyltest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => VinylImporter.PrepareSessionAsync(dir, durationOf: _ => 0));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    private static readonly bool FfmpegAvailable = ProbeFfmpeg();

    private static bool ProbeFfmpeg()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-version");
            using var p = System.Diagnostics.Process.Start(psi);
            p!.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static void MakeFlac(string path, double seconds, int hz)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi",
                                  "-i", $"sine=frequency={hz}:duration={seconds}", "-c:a", "flac", path })
            psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"fixture ffmpeg failed: {err}");
    }

    private static double DurationOf(string path)
    {
        using var f = TagLib.File.Create(path);
        return f.Properties?.Duration.TotalSeconds ?? 0;
    }

    /// <summary>
    /// The joined album must be the WHOLE album, seekable end to end. Stream-copying FLAC through the
    /// concat demuxer fails this without any error: the frames are all present and a straight decode
    /// plays them, but the header keeps the first side's STREAMINFO. The file then declares that it
    /// ends where side one ended, and every seek past the seam returns nothing (no preview, no cut)
    /// for every track on side two.
    /// </summary>
    [Fact]
    public async Task A_joined_album_is_as_long_as_its_sides_and_seekable_past_the_seam()
    {
        if (!FfmpegAvailable) return;

        string dir = Path.Combine(Path.GetTempPath(), $"vinylseam-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            MakeFlac(VinylImporter.SidePath(dir, 1), seconds: 6, hz: 440);
            MakeFlac(VinylImporter.SidePath(dir, 2), seconds: 6, hz: 880);

            var album = await VinylImporter.PrepareSessionAsync(dir);

            Assert.Equal(12, DurationOf(album.FilePath), 1);          // the header knows about both sides
            Assert.Equal(12, album.TotalSeconds, 1);
            Assert.Equal(new[] { 6.0 }, album.SideJoinSeconds.Select(j => Math.Round(j)));

            // Cut a slice from PAST the seam, the way previewing or splitting a side-two track does.
            string cut = Path.Combine(dir, "past-seam.flac");
            var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg")
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", album.FilePath,
                                      "-ss", "8", "-t", "3", "-c:a", "flac", cut })
                psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit();

            Assert.Equal(0, p.ExitCode);
            Assert.InRange(DurationOf(cut), 2.5, 3.5);   // real audio, not a header-only stub
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>An album already on disk that is shorter than the sides it came from was joined by
    /// stream-copying the seam. It cannot be repaired, and the sides are still here.</summary>
    [Fact]
    public async Task A_truncated_album_is_rebuilt_from_the_kept_sides()
    {
        if (!FfmpegAvailable) return;

        string dir = Path.Combine(Path.GetTempPath(), $"vinylrejoin-{Guid.NewGuid():N}");
        string originals = Path.Combine(dir, RPM.Core.Splitting.AlbumFileSplitter.OriginalsFolderName);
        Directory.CreateDirectory(originals);
        try
        {
            // The state of an older session: sides kept, and an "album" only as long as side one.
            MakeFlac(Path.Combine(originals, "side1.flac"), seconds: 6, hz: 440);
            MakeFlac(Path.Combine(originals, "side2.flac"), seconds: 6, hz: 880);
            MakeFlac(Path.Combine(dir, VinylImporter.AlbumFileName), seconds: 6, hz: 440);

            var album = await VinylImporter.PrepareSessionAsync(dir);

            Assert.Equal(12, DurationOf(album.FilePath), 1);   // rejoined, not accepted as-is
            Assert.Equal(2, VinylImporter.SidesIn(originals).Count);   // sides still kept
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Single_side_concat_copies_the_file_and_reports_total()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"vinyltest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string sidePath = Path.Combine(dir, "side1.flac");
            await File.WriteAllBytesAsync(sidePath, new byte[] { 1, 2, 3, 4 });   // opaque; single-side path just copies
            var sides = new[] { new RecordedSide(1, sidePath, 1234.5) };

            string outPath = Path.Combine(dir, "album.flac");
            var album = await VinylImporter.ConcatSidesAsync(sides, outPath);

            Assert.True(File.Exists(outPath));
            Assert.Equal(1234.5, album.TotalSeconds, 3);
            Assert.Empty(album.SideJoinSeconds);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void A_restored_album_older_than_the_settings_is_not_offered()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rpm-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        string restored = Path.Combine(dir, VinylRestorer.RestoredFileName);
        File.WriteAllText(restored, "not really audio");
        File.SetLastWriteTimeUtc(restored, DateTime.UtcNow.AddMinutes(-5));

        string settings = Path.Combine(dir, RestoreSettings.FileName);
        File.WriteAllText(settings, "{}");
        File.SetLastWriteTimeUtc(settings, DateTime.UtcNow);

        // Cutting from it would use a curve the user has since changed, and nothing would say so.
        Assert.Null(VinylImporter.RestoredAlbumIn(dir));
        Assert.False(File.Exists(restored));
    }

    [Fact]
    public void A_restored_album_newer_than_the_settings_is_offered()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rpm-fresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        File.WriteAllText(Path.Combine(dir, RestoreSettings.FileName), "{}");
        File.SetLastWriteTimeUtc(Path.Combine(dir, RestoreSettings.FileName), DateTime.UtcNow.AddMinutes(-5));

        string restored = Path.Combine(dir, VinylRestorer.RestoredFileName);
        File.WriteAllText(restored, "not really audio");

        Assert.Equal(restored, VinylImporter.RestoredAlbumIn(dir));
    }

    [Fact]
    public void No_settings_means_no_restored_album()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rpm-none-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Assert.Null(VinylImporter.RestoredAlbumIn(dir));
    }
}
