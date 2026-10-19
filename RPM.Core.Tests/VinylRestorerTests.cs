using RPM.Core.Audio;
using RPM.Core.Recording;

namespace RPM.Core.Tests;

/// <summary>Runs real ffmpeg. Slow, so keep this class small and filter to it when working here.</summary>
public class VinylRestorerTests
{
    private static string TempFolder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rpm-restorer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Writes a FLAC of a 440 Hz tone using ffmpeg's own generator.</summary>
    private static async Task<string> MakeToneFlacAsync(string dir, double seconds)
    {
        string path = Path.Combine(dir, "album.flac");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = RPM.Fingerprint.ToolPaths.Ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=44100:duration={seconds}",
            "-ac", "2", "-c:a", "flac", path,
        }) psi.ArgumentList.Add(a);

        using var p = System.Diagnostics.Process.Start(psi)!;
        await p.WaitForExitAsync();
        Assert.Equal(0, p.ExitCode);
        return path;
    }

    private static double DurationOf(string path)
    {
        using var f = TagLib.File.Create(path);
        return f.Properties!.Duration.TotalSeconds;
    }

    [Fact]
    public async Task Renders_an_album_the_same_length_as_its_source()
    {
        string dir = TempFolder();
        string source = await MakeToneFlacAsync(dir, 5);
        string dest = Path.Combine(dir, VinylRestorer.RestoredFileName);

        var gains = new double[Equalizer.Bands.Length];
        gains[0] = 6;
        await VinylRestorer.RenderAsync(
            source, RestoreSettings.Off with { EqOn = true, Gains = gains }, dest, null, default);

        Assert.True(File.Exists(dest));
        Assert.InRange(DurationOf(dest), DurationOf(source) - 0.02, DurationOf(source) + 0.02);
    }

    [Fact]
    public async Task The_gate_does_not_shift_the_album_in_time()
    {
        string dir = TempFolder();
        string source = await MakeToneFlacAsync(dir, 5);
        string dest = Path.Combine(dir, VinylRestorer.RestoredFileName);

        var profile = NoiseProfile.Measure(new float[8192], 2, 44100);
        await VinylRestorer.RenderAsync(
            source, RestoreSettings.Off with { NoiseOn = true, Profile = profile }, dest, null, default);

        // The renderer compensates the gate's latency, so the cut boundaries still land where the
        // waveform said they did.
        Assert.InRange(DurationOf(dest), DurationOf(source) - 0.02, DurationOf(source) + 0.02);
    }

    [Fact]
    public async Task Measures_a_profile_off_a_stretch_of_the_album()
    {
        string dir = TempFolder();
        string source = await MakeToneFlacAsync(dir, 5);

        var profile = await VinylRestorer.MeasureProfileAsync(source, 1.0, 2.0, default);

        Assert.Equal(44100, profile.SampleRate);
        Assert.Equal(NoiseProfile.DefaultFftSize, profile.FftSize);
        // A 440 Hz tone: the bin nearest 440 Hz stands well above the rest of the spectrum.
        int bin = (int)Math.Round(440.0 / (44100.0 / profile.FftSize));
        double body = profile.Magnitudes.Skip(bin + 20).Take(200).Average();
        Assert.True(profile.Magnitudes[bin] > body * 50);
    }

    [Fact]
    public async Task Renders_a_preview_of_just_the_span_asked_for()
    {
        string dir = TempFolder();
        string source = await MakeToneFlacAsync(dir, 10);

        var preview = await VinylRestorer.RenderPreviewAsync(
            source, 2.0, 3.0, RestoreSettings.Off with { EqOn = true, Preamp = -3 }, default);

        try
        {
            Assert.InRange(DurationOf(preview.Path), 2.9, 3.1);
            Assert.Equal(0, preview.Repairs);   // not asked for, so nothing mended
        }
        finally { try { File.Delete(preview.Path); } catch { } }
    }

    /// <summary>
    /// The click repair runs and says what it mended, end to end.
    ///
    /// <para>A tone with a spike driven into it every tenth of a second. A click is a few samples
    /// that the signal around them does not predict, and a pure sine is the most predictable signal
    /// there is, so this is the clearest possible case. Sixty clicks go in. The assertion is that
    /// most of them are found and that the repair stays small, because a repair that covers the
    /// record is not a repair.</para>
    /// </summary>
    [Fact]
    public async Task The_click_repair_reports_what_it_mended()
    {
        string dir = TempFolder();
        string source = Path.Combine(dir, "clicky.flac");
        await MakeClickyFlacAsync(source, seconds: 6);

        var preview = await VinylRestorer.RenderPreviewAsync(
            source, 1.0, 4.0,
            RestoreSettings.Off with { DeclickOn = true, Clicks = new ClickRepairSettings() },
            default);

        try
        {
            Assert.InRange(preview.Repairs, 20, 200);
            Assert.InRange(preview.RepairedPercent, 0.0001, 10.0);
        }
        finally { try { File.Delete(preview.Path); } catch { } }
    }

    [Fact]
    public async Task The_click_repair_is_silent_when_it_is_off()
    {
        string dir = TempFolder();
        string source = Path.Combine(dir, "clicky.flac");
        await MakeClickyFlacAsync(source, seconds: 3);

        var preview = await VinylRestorer.RenderPreviewAsync(
            source, 0.5, 2.0, RestoreSettings.Off with { DeclickOn = false }, default);

        try { Assert.Equal(0, preview.Repairs); }
        finally { try { File.Delete(preview.Path); } catch { } }
    }

    /// <summary>A 440 Hz tone with a periodic impulse mixed into it, which is what a click is.</summary>
    private static async Task MakeClickyFlacAsync(string path, double seconds)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = RPM.Fingerprint.ToolPaths.Ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=44100:duration={seconds}",
            // A very short, very loud pulse ten times a second, summed with the tone.
            "-f", "lavfi", "-i",
            $"sine=frequency=8000:sample_rate=44100:duration={seconds},"
            + "atrim=0:0.0005,apad=whole_dur=0.1,aloop=loop=-1:size=4410,"
            + $"atrim=0:{seconds},volume=8",
            "-filter_complex", "[0:a][1:a]amix=inputs=2:normalize=0[out]",
            "-map", "[out]", "-ac", "2", "-c:a", "flac", path,
        }) psi.ArgumentList.Add(a);

        using var p = System.Diagnostics.Process.Start(psi)!;
        string said = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, said);
    }

    [Fact]
    public async Task Cancelling_leaves_no_partial_file()
    {
        string dir = TempFolder();
        string source = await MakeToneFlacAsync(dir, 30);
        string dest = Path.Combine(dir, VinylRestorer.RestoredFileName);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            VinylRestorer.RenderAsync(source, RestoreSettings.Off with { EqOn = true, Preamp = 3 },
                dest, null, cts.Token));

        Assert.False(File.Exists(dest));
    }
}
