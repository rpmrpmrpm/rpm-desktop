using System.Diagnostics;
using RPM.Core.Splitting;
using RPM.Fingerprint;

namespace RPM.Core.Recording;

/// <summary>The concatenated album ready to split: the single file all recorded sides were joined into,
/// its total length, and the timeline offsets where one side meets the next (guaranteed track
/// boundaries).</summary>
public sealed record VinylAlbum(string FilePath, string Extension, double TotalSeconds, IReadOnlyList<double> SideJoinSeconds);

/// <summary>
/// Turns recorded vinyl sides into one album file and feeds the existing split pipeline. Sides are
/// concatenated (lossless stream-copy, since they share a format) into a single FLAC; the offsets where
/// sides join are returned so a track can never be allowed to bridge two sides.
/// </summary>
public static class VinylImporter
{
    /// <summary>Override to point at a specific ffmpeg; defaults to the bundled-then-PATH
    /// lookup.</summary>
    public static string FfmpegPath { get; set; } = ToolPaths.Ffmpeg;

    /// <summary>The joined album inside a session folder. The split step works on this file.</summary>
    public const string AlbumFileName = "album.flac";

    private const string SidePrefix = "side";
    private const string SideExtension = ".flac";

    /// <summary>How far a joined album's length may drift from the sum of its sides before it is
    /// treated as not being the whole album and joined again.</summary>
    private const double RejoinToleranceSeconds = 2.0;

    /// <summary>Where a side is recorded to. Sides live at the session's top level while recording so
    /// an interrupted session is visible to <c>PendingRips</c> rather than an invisible
    /// orphan.</summary>
    public static string SidePath(string sessionDir, int oneBasedIndex) =>
        Path.Combine(sessionDir, $"{SidePrefix}{oneBasedIndex}{SideExtension}");

    /// <summary>Side files in a folder, in recorded order: side1, side2, and so on. The order is
    /// numeric, so a tenth side does not sort before the second.</summary>
    public static IReadOnlyList<string> SidesIn(string folder)
    {
        if (!Directory.Exists(folder))
            return [];
        return Directory.EnumerateFiles(folder, $"{SidePrefix}*{SideExtension}")
            .Select(p => (Path: p, Index: SideIndexOf(p)))
            .Where(x => x.Index > 0)
            .OrderBy(x => x.Index)
            .Select(x => x.Path)
            .ToList();
    }

    /// <summary>
    /// The restored album in <paramref name="sessionDir"/>, or null when there is none to use.
    ///
    /// <para>A render made before the settings last changed has the old curve baked in. Cutting from it
    /// would silently produce the old sound. The audio does not say which settings made it, so the file
    /// timestamps decide. A stale render is deleted, so the next run cannot pick it up.</para>
    /// </summary>
    public static string? RestoredAlbumIn(string sessionDir)
    {
        string restored = Path.Combine(sessionDir, VinylRestorer.RestoredFileName);
        string settings = Path.Combine(sessionDir, RestoreSettings.FileName);
        if (!File.Exists(restored))
            return null;

        if (!File.Exists(settings)
            || File.GetLastWriteTimeUtc(restored) < File.GetLastWriteTimeUtc(settings))
        {
            try { File.Delete(restored); } catch { /* it will be re-rendered over anyway */ }
            return null;
        }
        return restored;
    }

    private static int SideIndexOf(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        return stem.StartsWith(SidePrefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(stem.AsSpan(SidePrefix.Length), out var n) ? n : 0;
    }

    /// <summary>
    /// Brings a session folder to the state the split step needs: one <see cref="AlbumFileName"/> at
    /// the top level, with the sides it was joined from kept in <c>Originals/</c>.
    ///
    /// <para>This is idempotent. A vinyl session needs no manifest on disk, because the audio files
    /// <i>are</i> the session. When the album already exists, the side-join offsets are recovered by
    /// reading the kept sides' own durations. That is why the sides are kept and not deleted. Resuming
    /// after a crash and re-splitting a finished recording both take this same path.</para>
    /// </summary>
    public static async Task<VinylAlbum> PrepareSessionAsync(
        string sessionDir, Func<string, double>? durationOf = null, CancellationToken ct = default)
    {
        durationOf ??= DurationOf;

        // Sessions split by older builds keep their sides in "_originals". Rename that folder to the
        // current name before anything reads it, so that only one folder is ever looked in.
        Splitting.AlbumFileSplitter.MigrateLegacyOriginals(sessionDir);

        // Drop a render that no longer matches the settings, before anything can cut from it.
        RestoredAlbumIn(sessionDir);

        string albumPath = Path.Combine(sessionDir, AlbumFileName);
        string originalsDir = Path.Combine(sessionDir, Splitting.AlbumFileSplitter.OriginalsFolderName);

        // The album was already split once, which moved it into Originals. Move it back to the top
        // level, so the session is unsplit again. Re-splitting with different boundaries needs this. It
        // is also why the album is kept, rather than thrown away with the sides.
        string keptAlbum = Path.Combine(originalsDir, AlbumFileName);
        if (!File.Exists(albumPath) && File.Exists(keptAlbum))
            File.Move(keptAlbum, albumPath);

        var keptSides = SidesIn(originalsDir);

        if (File.Exists(albumPath))
        {
            var offsets = JoinOffsets(keptSides.Select(durationOf).ToList());
            double albumSeconds = durationOf(albumPath);

            // An album must be as long as the sides it was joined from. If it is not, it is not a whole
            // album. Earlier builds stream-copied the join. That produces a file whose header describes
            // only its first side, so everything after the join cannot be reached. This cannot be
            // repaired in place. The sides are still here, so the album is deleted and joined again.
            double expected = keptSides.Sum(durationOf);
            bool wholeAlbum = keptSides.Count == 0 || Math.Abs(albumSeconds - expected) <= RejoinToleranceSeconds;
            if (wholeAlbum)
                return new VinylAlbum(albumPath, SideExtension, albumSeconds, offsets);

            File.Delete(albumPath);
        }

        // Join from whichever copy of the sides we have: the top level while a session is fresh, or
        // Originals when we are rebuilding an album that was already joined once.
        var sides = SidesIn(sessionDir);
        bool rebuilding = sides.Count == 0 && keptSides.Count > 0;
        if (rebuilding)
            sides = keptSides;
        if (sides.Count == 0)
            throw new InvalidOperationException("this vinyl session has no recorded sides");

        var recorded = sides.Select((p, i) => new RecordedSide(i + 1, p, durationOf(p))).ToList();
        var album = await ConcatSidesAsync(recorded, albumPath, ct);

        // The sides are now inside the album. Move them out of the top level, or the import would pick
        // them up as tracks next to the cut ones. Keep them, though. Their durations are the side-join
        // offsets, and nothing else records where one side ends. Sides that are already kept are where
        // they belong.
        if (!rebuilding)
        {
            Directory.CreateDirectory(originalsDir);
            foreach (var side in sides)
            {
                try { File.Move(side, Path.Combine(originalsDir, Path.GetFileName(side)), overwrite: true); }
                catch { /* a side we cannot move is not worth failing a finished join over */ }
            }
        }

        return album;
    }

    /// <summary>Cumulative side boundaries: every side end but the last.</summary>
    public static IReadOnlyList<double> JoinOffsets(IReadOnlyList<double> sideDurations)
    {
        var joins = new List<double>();
        double cumulative = 0;
        for (int i = 0; i < sideDurations.Count - 1; i++)
        {
            cumulative += sideDurations[i];
            joins.Add(cumulative);
        }
        return joins;
    }

    private static double DurationOf(string path)
    {
        try
        {
            using var f = TagLib.File.Create(path);
            return f.Properties?.Duration.TotalSeconds ?? 0;
        }
        catch { return 0; }
    }

    /// <summary>Concatenates <paramref name="sides"/> (in list order) into one FLAC at
    /// <paramref name="outputPath"/>. A single side is copied straight across. Returns the album plus
    /// the cumulative side-join offsets.</summary>
    public static async Task<VinylAlbum> ConcatSidesAsync(
        IReadOnlyList<RecordedSide> sides, string outputPath, CancellationToken ct = default)
    {
        if (sides.Count == 0)
            throw new InvalidOperationException("no sides recorded");

        var joins = JoinOffsets(sides.Select(s => s.DurationSeconds).ToList());
        double total = sides.Sum(s => s.DurationSeconds);
        string ext = Path.GetExtension(outputPath);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        if (sides.Count == 1)
        {
            File.Copy(sides[0].FilePath, outputPath, overwrite: true);
            return new VinylAlbum(outputPath, ext, total, joins);
        }

        // ffmpeg concat demuxer: a list file of the sides, joined end to end and RE-ENCODED.
        //
        // Not -c copy. Stream-copying FLAC through the concat demuxer produces a file that lies. The
        // output keeps the FIRST side's STREAMINFO, so its header declares that the stream ends where
        // side one ended, and side two's timestamps restart at zero. The frames are all there, and a
        // straight decode plays them. That is what makes the failure so quiet. But every seek past the
        // join lands beyond the declared end and comes back with nothing. That breaks previewing any
        // track on side two, and it breaks cutting them, since a cut is a seek.
        //
        // Re-encoding costs CPU and nothing else. FLAC to FLAC is lossless, and the samples out are
        // bit-identical to the sides going in.
        string listPath = Path.Combine(Path.GetTempPath(), $"vinyl-concat-{Guid.NewGuid():N}.txt");
        var lines = sides.Select(s => $"file '{s.FilePath.Replace("'", "'\\''")}'");
        await File.WriteAllTextAsync(listPath, string.Join('\n', lines), ct);
        try
        {
            await RunFfmpegAsync(
                ["-f", "concat", "-safe", "0", "-i", listPath,
                 "-c:a", "flac", "-compression_level", "5", outputPath], ct);
        }
        finally
        {
            try { File.Delete(listPath); } catch { }
        }

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            throw new InvalidOperationException("ffmpeg produced no concatenated album file");

        return new VinylAlbum(outputPath, ext, total, joins);
    }

    private static async Task RunFfmpegAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-y");
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start ffmpeg");
        var errorTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        string err = await errorTask;
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed: {FfmpegSaid.Short(err)}");
    }
}
