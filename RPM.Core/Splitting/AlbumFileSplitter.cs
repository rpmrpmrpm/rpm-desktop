using System.Diagnostics;
using System.Globalization;
using System.Text;
using RPM.Core.Metadata;
using RPM.Core.Models;
using RPM.Core.Import;
using RPM.Core.Ripping;
using RPM.Fingerprint;

namespace RPM.Core.Splitting;

/// <summary>The result of a successful split: the new per-track files (tagged, in the source's folder), where the
/// original went, and the generated cue sheet beside it.</summary>
public sealed record SplitExecution(
    IReadOnlyList<TrackInfo> NewTracks,
    string OriginalMovedTo,
    string? CueWrittenTo);

/// <summary>
/// Cuts an album file into per-track files with ffmpeg. Lossy sources are stream-copied. There is no re-encode,
/// and the cuts land on frame boundaries within about 30 ms. That is inaudible, because the boundaries sit in
/// silence. Lossless sources are re-encoded to the same codec, so the cuts are exact to the sample.
///
/// <para>All-or-nothing. Every cut is first written to a hidden temp folder. If any cut fails, everything is
/// rolled back and the original is left untouched. On success the original is moved to <c>Originals/</c>, with a
/// generated cue sheet beside it. So the split can always be reversed.</para>
/// </summary>
public static class AlbumFileSplitter
{
    /// <summary>Set this to point at a specific ffmpeg. By default, it looks in the bundled copy and then on the
    /// PATH.</summary>
    public static string FfmpegPath { get; set; } = ToolPaths.Ffmpeg;

    /// <summary>
    /// The folder that the original file is kept in. Its name is plain and visible. A split that went wrong is
    /// fixed by throwing away the cut tracks and splitting the original again. Neither is possible if the one file
    /// that matters is hidden from the import tree.
    /// </summary>
    public const string OriginalsFolderName = "Originals";

    /// <summary>The name that the folder had in older builds, when it was hidden from scans. It is still
    /// recognised, so a library split by an older build keeps working. <see cref="MigrateLegacyOriginals"/>
    /// renames it on sight.</summary>
    public const string LegacyOriginalsFolderName = "_originals";

    /// <summary>The originals folder inside <paramref name="folder"/> that actually exists. The current name is
    /// checked first, then the older build's name. Null when nothing was kept there.</summary>
    public static string? FindOriginalsDir(string folder)
    {
        var current = Path.Combine(folder, OriginalsFolderName);
        if (Directory.Exists(current))
            return current;
        var legacy = Path.Combine(folder, LegacyOriginalsFolderName);
        return Directory.Exists(legacy) ? legacy : null;
    }

    public static bool IsOriginalsFolder(string? folderName) =>
        folderName is not null
        && (folderName.Equals(OriginalsFolderName, StringComparison.OrdinalIgnoreCase)
            || folderName.Equals(LegacyOriginalsFolderName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Renames every <c>_originals</c> folder under <paramref name="root"/> to <c>Originals</c>. This way, folders
    /// left by an older build show the name that the tree now uses. When a folder with the new name is already
    /// there, its contents are moved one by one. Anything that will not move stays exactly where it is. A rename
    /// that only partly worked must never lose the one copy of a record.
    /// </summary>
    public static void MigrateLegacyOriginals(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return;

        IEnumerable<string> legacy;
        try
        {
            legacy = Directory.EnumerateDirectories(root, LegacyOriginalsFolderName, SearchOption.AllDirectories)
                .ToList();
        }
        catch { return; }

        foreach (var dir in legacy)
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent is null)
                continue;
            var target = Path.Combine(parent, OriginalsFolderName);
            try
            {
                if (!Directory.Exists(target))
                {
                    Directory.Move(dir, target);
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    var to = UniquePath(Path.Combine(target, Path.GetFileName(file)));
                    File.Move(file, to);
                }
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch { /* In use, read-only, or a case-insensitive name clash: leave it where it is. */ }
        }
    }

    private static readonly HashSet<string> LosslessExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".flac", ".wav", ".aif", ".aiff", ".alac" };

    /// <param name="targetFormat">When set, every track is re-encoded to this format. The vinyl wizard uses this.
    /// Its lossless FLAC intermediate is cut into the user's chosen output format. When null, the source codec is
    /// kept. That is the behaviour of an in-place album split.</param>
    /// <param name="layout">When set, the cut tracks are placed under the source folder using this naming
    /// template, with folders. A vinyl session then lands in the queue with the library's default arrangement.
    /// When null, the tracks keep the flat "NN - Title" name beside the original. That is the in-place album split
    /// behaviour. The file is already filed, so splitting must not move it.</param>
    public static SplitExecution Split(
        SplitPlan plan, TrackInfo source,
        IProgress<SplitProgress>? progress = null, CancellationToken ct = default,
        RipFormat? targetFormat = null, NamingTemplate? layout = null)
    {
        if (plan.Tracks.Count < 2)
            throw new InvalidOperationException("split plan has fewer than two tracks");

        string sourceDir = Path.GetDirectoryName(plan.SourcePath)
            ?? throw new InvalidOperationException("source file has no parent folder");

        // The album is already the preserved one. This is the second cut, after a first one went wrong. It stays
        // where it is. The tracks go to the folder it was preserved out of, not into Originals beside it. The
        // tracks belong with the album, and nothing in Originals is ever imported.
        bool fromOriginals = IsOriginalsFolder(Path.GetFileName(sourceDir))
            && Path.GetDirectoryName(sourceDir) is not null;
        string outputDir = fromOriginals ? Path.GetDirectoryName(sourceDir)! : sourceDir;

        string tempDir = Path.Combine(outputDir, $".mdsplit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        // The extension for the new track files. It is the target format's when re-encoding. Otherwise it is the
        // source's, because a preserved split keeps the album's own codec.
        string outExtension = targetFormat is { } tf ? RipEncoders.ExtensionOf(tf) : plan.Extension;

        // The file's own art wins. It is the thing being cut, and a picture in it belongs to this album. The plan's
        // cover is used only when there is nothing to win. That is a recording of a record, which never had tags to
        // carry art. Only then is a full-size scan written beside the tracks. Art taken out of the source file is
        // all the size that file ever had.
        var art = TagReader.LoadArt(plan.SourcePath);
        (byte[] Bytes, string Mime)? folderScan = null;
        if (art is null && plan.Art is { } chosen)
        {
            art = (chosen.Embedded.Bytes, chosen.Embedded.Mime);
            folderScan = (chosen.Folder.Bytes, chosen.Folder.Mime);
        }
        bool artistsDiffer = plan.Tracks.Select(t => t.Artist ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;

        try
        {
            var cutFiles = new List<(SplitTrack Track, string TempPath, string FileName)>();
            for (int i = 0; i < plan.Tracks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var track = plan.Tracks[i];
                string fileName = BuildFileName(track, artistsDiffer, outExtension);
                string tempPath = Path.Combine(tempDir, fileName);
                progress?.Report(new SplitProgress(i, plan.Tracks.Count, fileName));
                RunFfmpegCut(plan.SourcePath, plan.Extension, track, tempPath, ct, targetFormat);
                cutFiles.Add((track, tempPath, fileName));
            }
            progress?.Report(new SplitProgress(plan.Tracks.Count, plan.Tracks.Count, ""));

            // Point of no return: all the cuts succeeded. The original is moved aside first. Then the track files
            // can take its folder without name juggling. An album already in Originals (its second cut) is not moved
            // at all. It is already where it belongs.
            string movedOriginal;
            if (fromOriginals)
            {
                movedOriginal = plan.SourcePath;
            }
            else
            {
                string originalsDir = Path.Combine(outputDir, OriginalsFolderName);
                Directory.CreateDirectory(originalsDir);
                movedOriginal = UniquePath(Path.Combine(originalsDir, Path.GetFileName(plan.SourcePath)));
                File.Move(plan.SourcePath, movedOriginal);
            }

            string? cuePath = null;
            try
            {
                cuePath = Path.ChangeExtension(movedOriginal, ".cue");
                File.WriteAllText(cuePath, BuildCueSheet(plan, Path.GetFileName(movedOriginal)));
            }
            catch { cuePath = null; /* The cue sheet is a convenience, not part of the contract. */ }

            var newTracks = new List<TrackInfo>(cutFiles.Count);
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (track, tempPath, fileName) in cutFiles)
            {
                var info = new TrackInfo
                {
                    FilePath = "",
                    Extension = outExtension,
                    DurationSeconds = track.DurationSeconds,
                    Title = track.Title ?? $"Track {track.TrackNumber:00}",
                    Artist = track.Artist ?? plan.AlbumArtist ?? source.Artist,
                    AlbumArtist = plan.AlbumArtist ?? source.AlbumArtist,
                    Album = plan.Album ?? source.Album,
                    TrackNumber = track.TrackNumber,
                    TrackCount = (uint)plan.Tracks.Count,
                    DiscNumber = plan.DiscNumber,
                    DiscCount = plan.DiscCount,
                    Year = plan.Year != 0 ? plan.Year : source.Year,
                    Genre = source.Genre,
                    ReleaseCountry = plan.Country ?? source.ReleaseCountry,
                    PendingArt = art?.Bytes,
                    PendingArtMime = art?.Mime,
                    Source = track.Source == BoundarySource.CueSheet ? MetadataSource.Tags : MetadataSource.Fingerprint,
                    IdentificationScore = track.Confidence,
                };

                // Everything the picked pressing said about this row. The source file is one long recording with at
                // most one set of tags on it. Without this, the cut tracks would inherit the album's credits and none
                // of their own.
                if (track.Catalogue is { } catalogued)
                    info = info with
                    {
                        Advisory = catalogued.Advisory,
                        ArtistSort = catalogued.ArtistSort ?? info.ArtistSort,
                        Isrc = catalogued.Isrc ?? info.Isrc,
                        Work = catalogued.Work ?? info.Work,
                        MovementName = catalogued.MovementName ?? info.MovementName,
                        MovementNumber = catalogued.MovementNumber != 0
                            ? catalogued.MovementNumber : info.MovementNumber,
                        Composers = catalogued.Composers ?? info.Composers,
                        ComposerSort = catalogued.ComposerSort ?? info.ComposerSort,
                        Conductor = catalogued.Conductor ?? info.Conductor,
                        Ensemble = catalogued.Ensemble ?? info.Ensemble,
                        Soloists = catalogued.Soloists ?? info.Soloists,
                        Lyricist = catalogued.Lyricist ?? info.Lyricist,
                        Arranger = catalogued.Arranger ?? info.Arranger,
                        MusicBrainzRecordingId = catalogued.RecordingId ?? info.MusicBrainzRecordingId,
                        MusicBrainzArtistId = catalogued.ArtistId ?? info.MusicBrainzArtistId,
                    };

                // A vinyl session (layout set) lands under the library's default folders. An in-place album split
                // keeps the flat name beside the original, because the file is already filed.
                string finalPath = layout is null
                    ? UniquePath(Path.Combine(outputDir, fileName))
                    : StagingLayout.ResolvePath(outputDir, layout, info, claimed);
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                File.Move(tempPath, finalPath);
                info = info with { FilePath = finalPath };

                try { TagReader.WriteAll(finalPath, info); } catch { /* Tags are best-effort. The file is cut. */ }
                newTracks.Add(info with { PendingArt = null, PendingArtMime = null, HasArt = art is not null });
            }

            // The scan goes to the folders that the tracks actually landed in. A vinyl session run through a naming
            // layout can spread one plan across more than one folder.
            if (folderScan is { } scan)
                foreach (var folder in newTracks.Select(t => Path.GetDirectoryName(t.FilePath))
                             .Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.OrdinalIgnoreCase))
                    Import.FolderArtWriter.WriteFolderCover(folder!, scan.Bytes, scan.Mime);

            Directory.Delete(tempDir, recursive: true);
            return new SplitExecution(newTracks, movedOriginal, cuePath);
        }
        catch
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
            throw;
        }
    }

    private static string BuildFileName(SplitTrack track, bool artistsDiffer, string extension)
    {
        string title = PathSanitizer.SanitizeSegment(track.Title, fallback: $"Track {track.TrackNumber:00}");
        string stem = artistsDiffer && !string.IsNullOrWhiteSpace(track.Artist)
            ? $"{PathSanitizer.SanitizeSegment(track.Artist)} - {track.TrackNumber:00} {title}"
            : $"{track.TrackNumber:00} - {title}";
        return stem + extension;
    }

    private static void RunFfmpegCut(string sourcePath, string extension, SplitTrack track, string outPath,
        CancellationToken ct, RipFormat? targetFormat = null)
    {
        var inv = CultureInfo.InvariantCulture;
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

        if (targetFormat is { } target)
        {
            // Re-encode to the requested output format. Seeking after -i is accurate to the decoded sample. The
            // source is always the lossless vinyl intermediate, so every cut is exact to the sample.
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(sourcePath);
            psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add(track.StartSeconds.ToString("F3", inv));
            psi.ArgumentList.Add("-t"); psi.ArgumentList.Add(track.DurationSeconds.ToString("F3", inv));
            psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0:a:0");
            psi.ArgumentList.Add("-map_metadata"); psi.ArgumentList.Add("-1");
            foreach (var argument in RipEncoders.ArgsFor(target))
                psi.ArgumentList.Add(argument);
        }
        else if (IsLossless(sourcePath, extension))
        {
            // Exact to the sample: seek after -i, which decodes up to the cut point. Then re-encode to the same codec.
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(sourcePath);
            psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add(track.StartSeconds.ToString("F3", inv));
            psi.ArgumentList.Add("-t"); psi.ArgumentList.Add(track.DurationSeconds.ToString("F3", inv));
            psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0:a:0");
            psi.ArgumentList.Add("-map_metadata"); psi.ArgumentList.Add("-1");
            psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add(LosslessCodec(sourcePath, extension));
        }
        else
        {
            // Stream copy: a fast seek before -i, with no re-encode. The cut lands on the previous frame boundary,
            // within 30 ms. That is inside the silence where the boundary was placed.
            psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add(track.StartSeconds.ToString("F3", inv));
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(sourcePath);
            psi.ArgumentList.Add("-t"); psi.ArgumentList.Add(track.DurationSeconds.ToString("F3", inv));
            psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0:a:0");
            psi.ArgumentList.Add("-map_metadata"); psi.ArgumentList.Add("-1");
            psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("copy");
        }
        psi.ArgumentList.Add(outPath);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start ffmpeg for split");
        string err = proc.StandardError.ReadToEnd();
        while (!proc.WaitForExit(200))
        {
            if (ct.IsCancellationRequested)
            {
                try { proc.Kill(); } catch { }
                ct.ThrowIfCancellationRequested();
            }
        }
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"ffmpeg split failed for '{Path.GetFileName(outPath)}': {FfmpegSaid.Short(err)}");
        if (!File.Exists(outPath) || new FileInfo(outPath).Length == 0)
            throw new InvalidOperationException($"ffmpeg produced no output for '{Path.GetFileName(outPath)}'");

        // A cut past the end of the file can exit with code 0 and a file that holds only a header. So reject any
        // output without real audio.
        double written = 0;
        try
        {
            using var f = TagLib.File.Create(outPath);
            written = f.Properties?.Duration.TotalSeconds ?? 0;
        }
        catch { /* An unreadable output counts as empty. */ }
        if (written < 0.1)
            throw new InvalidOperationException($"ffmpeg produced an empty cut for '{Path.GetFileName(outPath)}'");
    }

    private static bool IsLossless(string sourcePath, string extension) =>
        LosslessExtensions.Contains(extension) ||
        (extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase) && IsAlac(sourcePath));

    /// <summary>ALAC is stored inside .m4a. TagLib's codec description tells it apart from AAC.</summary>
    private static bool IsAlac(string sourcePath)
    {
        try
        {
            using var f = TagLib.File.Create(sourcePath);
            return f.Properties?.Codecs?.Any(c =>
                c?.Description?.Contains("ALAC", StringComparison.OrdinalIgnoreCase) == true ||
                c?.Description?.Contains("Apple Lossless", StringComparison.OrdinalIgnoreCase) == true) == true;
        }
        catch { return false; }
    }

    private static string LosslessCodec(string sourcePath, string extension) => extension.ToLowerInvariant() switch
    {
        ".flac" => "flac",
        ".wav" => PcmCodec(sourcePath, bigEndian: false),
        ".aif" or ".aiff" => PcmCodec(sourcePath, bigEndian: true),
        _ => "alac", // .alac and ALAC-in-.m4a
    };

    private static string PcmCodec(string sourcePath, bool bigEndian)
    {
        int bits = 16;
        try
        {
            using var f = TagLib.File.Create(sourcePath);
            if (f.Properties?.BitsPerSample is > 0 and var b) bits = b;
        }
        catch { }
        string suffix = bigEndian ? "be" : "le";
        return bits switch
        {
            8 => "pcm_u8",
            24 => $"pcm_s24{suffix}",
            32 => $"pcm_s32{suffix}",
            _ => $"pcm_s16{suffix}",
        };
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!;
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>Writes a standard single-FILE cue sheet that describes the cut. It is written beside the preserved
    /// original, so the split can be reproduced with any tool that reads cue sheets.</summary>
    public static string BuildCueSheet(SplitPlan plan, string audioFileName)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(plan.AlbumArtist))
            sb.AppendLine($"PERFORMER \"{CueEscape(plan.AlbumArtist)}\"");
        if (!string.IsNullOrWhiteSpace(plan.Album))
            sb.AppendLine($"TITLE \"{CueEscape(plan.Album)}\"");
        sb.AppendLine($"FILE \"{CueEscape(audioFileName)}\" WAVE");
        foreach (var t in plan.Tracks)
        {
            sb.AppendLine($"  TRACK {t.TrackNumber.ToString("00", inv)} AUDIO");
            if (!string.IsNullOrWhiteSpace(t.Title))
                sb.AppendLine($"    TITLE \"{CueEscape(t.Title)}\"");
            if (!string.IsNullOrWhiteSpace(t.Artist))
                sb.AppendLine($"    PERFORMER \"{CueEscape(t.Artist)}\"");
            int totalFrames = (int)Math.Round(t.StartSeconds * 75);
            int mm = totalFrames / (75 * 60);
            int ss = totalFrames / 75 % 60;
            int ff = totalFrames % 75;
            sb.AppendLine($"    INDEX 01 {mm.ToString("00", inv)}:{ss.ToString("00", inv)}:{ff.ToString("00", inv)}");
        }
        return sb.ToString();
    }

    private static string CueEscape(string value) => value.Replace('"', '\'');
}
