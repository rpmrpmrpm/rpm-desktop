using System.Diagnostics;
using System.Globalization;
using RPM.Core.Metadata;
using RPM.Core.Models;
using RPM.Core.Import;
using RPM.Fingerprint;

namespace RPM.Core.Ripping;

/// <summary>
/// Rips the audio out of a video DVD, one file per chapter. On a concert disc a chapter is a song,
/// so the chapter marks already on the disc are the track boundaries. There is nothing to guess.
///
/// ffmpeg reads the VOBs directly as MPEG program streams. The concat protocol joins a title's VOBs
/// into one stream, and ffmpeg decodes the chosen audio track. No libdvdread and no libdvdnav are
/// used, because both are GPL.
///
/// The rip is all or nothing, into a staging folder, the same as <see cref="CdRipper"/>. The folder it
/// leaves behind goes to the same import queue.
/// </summary>
public sealed class DvdRipper
{
    /// <summary>Override to point at a specific ffmpeg; defaults to the bundled-then-PATH lookup.</summary>
    public static string FfmpegPath { get; set; } = ToolPaths.Ffmpeg;

    /// <param name="hooks">Where the rip reports to and how it is steered. This is the same object that
    /// <see cref="CdRipper"/> takes, so the app watches a DVD rip the same way as a CD rip: progress, each
    /// chapter as it lands, each chapter given up on, stop and skip.</param>
    public async Task<RipResult> RipAsync(DvdRipPlan plan, string stagingRoot, NamingTemplate layout,
        IProgress<RipProgress>? progress = null, CancellationToken ct = default, RipHooks? hooks = null)
    {
        if (plan.Tracks.Count == 0)
            throw new InvalidOperationException("nothing selected to rip");

        progress ??= hooks?.Progress;
        var control = hooks?.Control ?? new RipControl();

        var staging = Path.Combine(stagingRoot, $"rip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The rip is written down before the first chapter is read. A rip that stops halfway is then a
        // named item in the pending list, not a GUID. The disc's volume name is the fallback title,
        // because a DVD carries no album tag.
        new RipManifest(
            RipSource.Dvd,
            Title: plan.Tracks[0].Album ?? plan.Disc.VolumeName,
            Artist: plan.Tracks[0].AlbumArtist ?? plan.Tracks[0].Artist,
            TrackCount: plan.Tracks.Count,
            CreatedUtc: DateTime.UtcNow,
            Device: plan.Disc.Drive.DisplayName).WriteTo(staging);

        // A CSS title is descrambled through libdvdcss on the raw device. One decryptor serves the whole
        // rip, because libdvdcss keeps the title keys once they are found. The steps are the same on macOS
        // and Windows. Only the raw device name and the sector mapping from the file system to the disc
        // differ, and both are handled inside CssDecryptor.
        CssDecryptor? css = null;
        if (plan.Title.Scrambled)
        {
            if (plan.Disc.RawDevice is null)
                throw new DvdEncryptedException(
                    "this DVD is copy-protected and its drive could not be opened for descrambling");
            css = CssDecryptor.Open(plan.Disc.RawDevice);
        }

        try
        {
            var tracks = new List<TrackInfo>(plan.Tracks.Count);
            var outcomes = new List<RipTrackOutcome>(plan.Tracks.Count);

            for (var i = 0; i < plan.Tracks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (control.StopRequested)
                    break;

                var trackPlan = plan.Tracks[i];
                var chapter = plan.Title.Chapters.FirstOrDefault(c => c.Number == trackPlan.Number)
                    ?? throw new InvalidOperationException($"chapter {trackPlan.Number} is not in this title");

                // Lands the chapter under the library's default names and folders, the same as CdRipper.
                var path = StagingLayout.ResolvePath(staging, layout, ProbeFor(trackPlan, plan.Format), claimed);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                var index = i;
                var report = new Progress<double>(fraction =>
                    progress?.Report(new RipProgress(index, plan.Tracks.Count, trackPlan.Title, fraction)));

                // Each chapter has its own error budget. A skip gives up only that chapter, so a damaged
                // disc loses one song, not the whole concert (see CdRipper for the full reasoning).
                var log = new RipReadLog(hooks?.Policy);
                var trackCts = control.BeginTrack(ct);
                try
                {
                    await ExtractChapterAsync(plan, chapter, path, css, report, log, trackCts.Token);

                    var tagged = TagRippedTrack(path, plan, trackPlan, chapter.Duration);
                    tracks.Add(tagged);

                    // The art is written with the first chapter that lands, not at the end. If the app is closed
                    // before the rip finishes, the rip can still be offered later. The lookup that fetched the art
                    // does not run again (see CdRipper).
                    WriteFolderImages(Path.GetDirectoryName(path)!);
                    outcomes.Add(new RipTrackOutcome(
                        trackPlan.Number, trackPlan.Title, RipTrackResult.Ripped, log.BadBlocks));
                    hooks?.TrackDone?.Report(tagged);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Discard(path);

                    if (control.StopRequested)
                        break;

                    Skipped(new RipTrackOutcome(trackPlan.Number, trackPlan.Title, RipTrackResult.SkippedByHand));
                }
                catch (DiscTrackUnreadableException ex)
                {
                    Discard(path);
                    Skipped(new RipTrackOutcome(trackPlan.Number, trackPlan.Title,
                        ex.Stalled ? RipTrackResult.SkippedStalled : RipTrackResult.SkippedUnreadable,
                        log.BadBlocks));
                }
                finally
                {
                    control.EndTrack(trackCts);
                }

                void Skipped(RipTrackOutcome outcome)
                {
                    outcomes.Add(outcome);
                    hooks?.TrackSkipped?.Report(outcome);
                }
            }

            if (tracks.Count == 0)
            {
                if (control.StopRequested)
                    throw new OperationCanceledException("the rip was stopped before any chapter finished");

                throw new DiscReadException(
                    "nothing on this disc could be read — it is too badly damaged to rip");
            }

            // A final pass covers any folder that the per-chapter writes did not reach. Both writers skip
            // an image that is already there, so this costs little when the folders already have art.
            foreach (var folder in tracks.Select(t => Path.GetDirectoryName(t.FilePath))
                         .Where(d => !string.IsNullOrEmpty(d))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                WriteFolderImages(folder!);

            progress?.Report(new RipProgress(plan.Tracks.Count, plan.Tracks.Count, "", 1));
            return new RipResult(staging, tracks, outcomes, control.StopRequested, plan.Tracks.Count);

            // The sleeve is written at full size, beside the tracks. The copy inside the tracks is scaled, so
            // the folder image cannot be read back out of them. The disc's own label is written beside it.
            // The importer carries both to the final album folder.
            void WriteFolderImages(string folder)
            {
                if (plan.Cover?.Folder is { } scan)
                    Import.FolderArtWriter.WriteFolderCover(folder, scan.Bytes, scan.Mime);
                if (plan.DiscArt is { Length: > 0 })
                    Import.FolderArtWriter.WriteDisc(folder, plan.DiscArt);
            }
        }
        catch
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
            throw;
        }
        finally
        {
            css?.Dispose();
        }
    }

    /// <summary>
    /// Feeds ffmpeg exactly the sectors the chapter occupies, and nothing else. A CSS title is decrypted
    /// first.
    ///
    /// Seeking ffmpeg to the chapter's start time is the obvious approach, and it is WRONG on a DVD. The
    /// failure is quiet. A VOB's presentation timestamps restart at each cell, so the byte stream has no
    /// single timeline to seek along, and <c>-ss</c> lands in an arbitrary place. The IFO's cell table is
    /// the real map of the disc, and it addresses content by SECTOR. So we read the chapter's cells
    /// ourselves and pipe those bytes in. ffmpeg only demuxes what it is given.
    /// </summary>
    private async Task ExtractChapterAsync(DvdRipPlan plan, DvdChapter chapter, string path,
        CssDecryptor? css, IProgress<double>? progress, RipReadLog log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-y");

        // The cells' bytes are an MPEG program stream, sent to ffmpeg on stdin.
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("mpeg");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("pipe:0");

        psi.ArgumentList.Add("-map"); psi.ArgumentList.Add($"0:a:{plan.Audio.Index}");
        psi.ArgumentList.Add("-map_metadata"); psi.ArgumentList.Add("-1");
        psi.ArgumentList.Add("-vn");

        foreach (var argument in EncoderArguments(plan.Format))
            psi.ArgumentList.Add(argument);

        psi.ArgumentList.Add(path);

        using var process = Process.Start(psi)
            ?? throw new DiscReadException("could not start ffmpeg to extract the chapter");

        var errorTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            var vobs = new VobSectorStream(plan.Title.VobPaths, css);
            var read = CopyAndCloseAsync(vobs, chapter, process.StandardInput.BaseStream, progress, log, ct);

            // The read is abandoned rather than awaited when the chapter is skipped. A drive stuck on a damaged
            // sector answers no cancel token, and skip must work on that chapter above all others. Killing
            // ffmpeg below is what breaks the abandoned read free (see CdRipper).
            try
            {
                await read.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Forget(read);
                throw;
            }

            process.StandardInput.Close(); // End of input. Tells ffmpeg to flush and finish.
        }
        catch (IOException)
        {
            // ffmpeg exited before the write finished. Its stderr says why. The broken pipe error does not.
            await process.WaitForExitAsync(ct);
            var earlyError = await errorTask;
            throw new DiscReadException(
                $"ffmpeg failed extracting chapter {chapter.Number}: {FfmpegSaid.Short(earlyError)}");
        }
        catch
        {
            try { process.Kill(); } catch { }
            throw;
        }

        await process.WaitForExitAsync(ct);
        var error = await errorTask;

        if (process.ExitCode != 0)
            throw new DiscReadException(
                $"ffmpeg failed extracting chapter {chapter.Number}: {FfmpegSaid.Short(error)}");
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new DiscReadException($"ffmpeg produced no audio for chapter {chapter.Number}");

        progress?.Report(1);
    }

    /// <summary>The sector copy. It owns the VOB file handles for exactly as long as it runs. If the copy
    /// is abandoned, it closes its own files when it finally stops. A <c>using</c> in the caller would
    /// instead dispose handles that the abandoned copy is still reading from.</summary>
    private static async Task CopyAndCloseAsync(VobSectorStream vobs, DvdChapter chapter, Stream destination,
        IProgress<double>? progress, RipReadLog log, CancellationToken ct)
    {
        await using (vobs)
            await vobs.CopySectorsAsync(chapter.Cells, destination, progress, ct, log);
    }

    /// <summary>Lets an abandoned copy fail quietly. It writes into a pipe that is closed under it, so
    /// observing the fault here keeps it off the unobserved-exception path.</summary>
    private static void Forget(Task task) =>
        _ = task.ContinueWith(t => _ = t.Exception,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>Deletes a chapter's partly written file.</summary>
    private static void Discard(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static IEnumerable<string> EncoderArguments(RipFormat format) => format switch
    {
        RipFormat.Flac => ["-c:a", "flac", "-compression_level", "8"],
        RipFormat.Alac => ["-c:a", "alac"],
        RipFormat.Mp3 => ["-c:a", "libmp3lame", "-b:a", "320k"],
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static TrackInfo TagRippedTrack(string path, DvdRipPlan plan, RipTrackPlan trackPlan, TimeSpan duration)
    {
        var info = trackPlan.ToTrackInfo(
            path, duration, plan.Cover?.Embedded.Bytes, plan.Cover?.Embedded.Mime);

        try { TagReader.WriteAll(path, info); }
        catch { /* Tagging is best effort. The audio is already on disk and is correct. */ }

        return info with { PendingArt = null, PendingArtMime = null, HasArt = plan.Cover is not null };
    }

    /// <summary>The metadata the naming template builds a path from: the chapter's plan plus the extension
    /// of the chosen format (see <see cref="CdRipper"/>).</summary>
    private static TrackInfo ProbeFor(RipTrackPlan track, RipFormat format) => new()
    {
        FilePath = "",
        Extension = RipEncoders.ExtensionOf(format),
        Title = string.IsNullOrWhiteSpace(track.Title) ? $"Track {track.Number:00}" : track.Title,
        Artist = track.Artist,
        AlbumArtist = track.AlbumArtist,
        Album = track.Album,
        TrackNumber = (uint)track.Number,
        TrackCount = (uint)track.TrackCount,
        Year = track.Year,
        Genre = track.Genre,
        Subgenre = track.Subgenre,
    };
}
