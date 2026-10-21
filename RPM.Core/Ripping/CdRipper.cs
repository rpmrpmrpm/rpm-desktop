using System.Diagnostics;
using System.Security.Cryptography;
using RPM.Core.Files;
using RPM.Core.Metadata;
using RPM.Core.Models;
using RPM.Core.Import;
using RPM.Fingerprint;

namespace RPM.Core.Ripping;

/// <summary>What a finished rip left behind: the staging folder holding the new files, and the
/// tracks themselves. The staging folder is handed to the normal import queue, so a ripped disc
/// goes through the same analyse, plan and organise steps as a dragged-in folder.</summary>
/// <param name="Outcomes">One entry per track the rip was asked for, in order, saying whether it
/// landed or was skipped. Empty when the caller did not ask for outcomes.</param>
/// <param name="Stopped">True when the user ended the rip early and kept what had landed, so the
/// caller knows the album is deliberately short rather than complete.</param>
/// <param name="Planned">How many tracks the rip was asked for. On a stopped rip this differs from
/// the outcomes, because the tracks it never reached have no outcome. Do not show "3 of 3" for a disc
/// the user stopped three tracks into eleven.</param>
public sealed record RipResult(
    string StagingFolder,
    IReadOnlyList<TrackInfo> Tracks,
    IReadOnlyList<RipTrackOutcome>? Outcomes = null,
    bool Stopped = false,
    int Planned = 0)
{
    public IReadOnlyList<RipTrackOutcome> TrackOutcomes => Outcomes ?? [];

    /// <summary>The denominator to show: what was asked for, falling back to what arrived.</summary>
    public int TrackTotal => Planned > 0 ? Planned : Tracks.Count;

    public int SkippedCount => TrackOutcomes.Count(o => o.IsSkipped);
}

/// <summary>
/// Rips an audio CD: pulls raw PCM off the disc with an <see cref="IDiscReader"/>, encodes it with
/// ffmpeg, tags it, and leaves the files in a staging folder.
///
/// A failure or a cancel is all-or-nothing, the same as <c>AlbumFileSplitter</c>: everything lands in
/// a temp folder and the folder is deleted, so no half album is left for the importer.
///
/// A damaged disc is handled differently on purpose. A scratch is usually local, so a disc that gives
/// up nine of eleven tracks still gives nine tracks. A track the drive will not deliver is skipped (by
/// policy, see <see cref="RipReadPolicy"/>, or by hand through <see cref="RipControl"/>) and the rip
/// carries on. Only a disc that yields nothing at all fails.
/// </summary>
public sealed class CdRipper(IDiscReader reader)
{
    /// <summary>Override to point at a specific ffmpeg; defaults to the bundled-then-PATH lookup.</summary>
    public static string FfmpegPath { get; set; } = ToolPaths.Ffmpeg;

    /// <summary>Reads every track twice and compares the results. The OS-level CD reads have no error
    /// correction of their own, so a scratched disc can return wrong samples without any error. Two
    /// identical reads are strong evidence that the samples are right. It takes roughly twice as long.</summary>
    public bool Verify { get; set; }

    /// <param name="stagingFolder">The exact folder to rip into. Pass null for a fresh
    /// <c>rip-&lt;guid&gt;/</c> (the normal one-disc case); pass an existing rip folder to add the next
    /// disc of a multi-disc set to it, so both discs import as one album.</param>
    /// <param name="hooks">Where the rip reports to and how it is steered: progress, each track as it
    /// lands, each track it gives up on, the stop/skip handle, and how hard to try on a bad disc.</param>
    /// <exception cref="OperationCanceledException">The rip was cancelled, or stopped by hand before a
    /// single track had landed. Either way there is nothing to import, and the folder is deleted.</exception>
    public async Task<RipResult> RipAsync(RipPlan plan, string stagingRoot, NamingTemplate layout,
        IProgress<RipProgress>? progress = null, CancellationToken ct = default, string? stagingFolder = null,
        RipHooks? hooks = null)
    {
        if (plan.Tracks.Count == 0)
            throw new InvalidOperationException("nothing selected to rip");

        progress ??= hooks?.Progress;
        var control = hooks?.Control ?? new RipControl();

        // A fresh disc gets its own staging folder. A "rip next disc" passes the previous disc's folder
        // back in, so the whole set lands together. Only a folder created here is deleted on failure.
        // A shared folder already holds an earlier disc, and that disc must not be deleted.
        bool ownsFolder = stagingFolder is null;
        var staging = stagingFolder ?? Path.Combine(stagingRoot, $"rip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

        // The disc lookup result is written to the folder before any sector is read. If the rip stops
        // halfway, the files still say which album they belong to. The first disc of a set names the
        // folder. A "rip next disc" adds to it and leaves that name alone.
        if (ownsFolder)
            ManifestFor(plan).WriteTo(staging);

        try
        {
            var tracks = new List<TrackInfo>(plan.Tracks.Count);
            var outcomes = new List<RipTrackOutcome>(plan.Tracks.Count);
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < plan.Tracks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (control.StopRequested)
                    break;

                var trackPlan = plan.Tracks[i];
                var track = plan.Disc.Toc.Tracks.FirstOrDefault(t => t.Number == trackPlan.Number)
                    ?? throw new DiscReadException($"track {trackPlan.Number} is not on this disc");

                // The rip uses the library's default names and folders, so the queue already shows the
                // layout it will keep. Import re-renders the names from the final metadata.
                var path = StagingLayout.ResolvePath(staging, layout, ProbeFor(trackPlan, plan.Format), claimed);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                var index = i;
                var report = new Progress<double>(fraction =>
                    progress?.Report(new RipProgress(index, plan.Tracks.Count, trackPlan.Title, fraction)));

                // One log per track. The error budget belongs to the track, not the disc, so a rough
                // track early on does not reduce the retries given to the rest.
                var log = new RipReadLog(hooks?.Policy);
                var trackCts = control.BeginTrack(ct);
                try
                {
                    await RipTrackAsync(plan, track, path, report, log, trackCts.Token);

                    var tagged = TagRippedTrack(path, plan, trackPlan, plan.Disc.Toc.DurationOf(track));
                    tracks.Add(tagged);

                    // The sleeve and disc art are written with the first track that lands in a folder,
                    // not at the end. If the app is closed, crashes or the disc is unplugged, the rip can
                    // still be offered later under Add music. The lookup that fetched the art is not run
                    // again, so art written only on the last track would never be saved for that rip.
                    WriteFolderImages(Path.GetDirectoryName(path)!);
                    outcomes.Add(new RipTrackOutcome(
                        trackPlan.Number, trackPlan.Title, RipTrackResult.Ripped, log.BadBlocks));
                    hooks?.TrackDone?.Report(tagged);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // The track's own token fired, not the rip's. That is a skip, or a stop that caught
                    // this track mid-read. Either way the partly written file is not an album track.
                    Discard(path);

                    if (control.StopRequested)
                        break;

                    Skipped(new RipTrackOutcome(trackPlan.Number, trackPlan.Title, RipTrackResult.SkippedByHand));
                }
                catch (DiscTrackUnreadableException ex)
                {
                    // The disc is damaged at this track. The rest of the disc usually is not, so this
                    // track is dropped and the rip carries on.
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

            // Nothing playable came off the disc. A stop is the user's choice, so it ends as a cancel:
            // the same empty result and the same cleanup. A disc that cannot be read at all is an
            // error that is worth showing.
            if (tracks.Count == 0)
            {
                if (control.StopRequested)
                    throw new OperationCanceledException("the rip was stopped before any track finished");

                throw new DiscReadException(
                    "nothing on this disc could be read — it is too badly damaged to rip");
            }

            // A final pass writes images for any folder the per-track writes did not reach, for example
            // a folder whose first track landed before the art was ready. Both writers skip an image
            // that is already there, so this costs little when the folders already have art.
            foreach (var folder in tracks.Select(t => Path.GetDirectoryName(t.FilePath))
                         .Where(d => !string.IsNullOrEmpty(d))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                WriteFolderImages(folder!);

            progress?.Report(new RipProgress(plan.Tracks.Count, plan.Tracks.Count, "", 1));
            return new RipResult(staging, tracks, outcomes, control.StopRequested, plan.Tracks.Count);

            // The folder images. Both are carried in the plan as bytes and written into each album
            // folder. The importer then moves them to the final folder. The cover is written at full
            // size, because the tracks got a scaled copy and the folder must not be filled from those.
            // Disc art is written when the CD named itself and no release was picked by hand.
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
            if (ownsFolder)
                try { Directory.Delete(staging, recursive: true); } catch { }
            throw;
        }
    }

    /// <summary>What the disc is, for the folder's <c>rip.json</c>: the album from the plan's own
    /// tags, the drive it came from, and how many tracks were meant to land.</summary>
    private static RipManifest ManifestFor(RipPlan plan)
    {
        var first = plan.Tracks[0];
        return new RipManifest(
            RipSource.Cd,
            Title: first.Album,
            Artist: first.AlbumArtist ?? first.Artist,
            TrackCount: plan.Tracks.Count,
            CreatedUtc: DateTime.UtcNow,
            Device: plan.Disc.Drive.DisplayName,
            DiscId: plan.Disc.DiscId);
    }

    /// <summary>Deletes a track's partly written file. Does nothing if the file is not there. A read
    /// that fails before ffmpeg writes any byte leaves no file at all.</summary>
    private static void Discard(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private async Task RipTrackAsync(RipPlan plan, DiscTrack track, string path,
        IProgress<double> progress, RipReadLog log, CancellationToken ct)
    {
        await EncodeAsync(plan, track, path, progress, log, ct);

        if (!Verify)
            return;

        // Reads the disc again into a scratch file and compares the encoded bytes. Both passes run the
        // same encoder on the same samples, so identical output means both reads from the disc agreed.
        //
        // The scratch file keeps the real extension. ffmpeg chooses the output format from it, and it
        // refuses to write a file whose format it cannot work out. The file is placed outside the
        // staging folder, so a crash during verify cannot leave it for the importer to find.
        var scratch = Path.Combine(Path.GetTempPath(),
            $"rpm-verify-{Guid.NewGuid():N}{Path.GetExtension(path)}");
        try
        {
            // The verify read has its own error budget. A track that reads badly twice is caught by the
            // comparison below. Sharing one budget across both passes would halve the retries that the
            // policy promises.
            await EncodeAsync(plan, track, scratch, progress: null, new RipReadLog(log.Policy), ct);
            if (!await FilesMatchAsync(path, scratch, ct))
            {
                throw new DiscReadException(
                    $"track {track.Number} read differently twice — the disc is scratched or dirty " +
                    "and the rip would not be accurate");
            }
        }
        finally
        {
            try { File.Delete(scratch); } catch { }
        }
    }

    /// <summary>Pipes the disc's PCM straight into ffmpeg's stdin. ffmpeg never opens the drive. Its CD
    /// demuxer is GPL-only and is not in the builds we ship, so ffmpeg only sees a raw PCM stream to
    /// encode.</summary>
    private async Task EncodeAsync(RipPlan plan, DiscTrack track, string path,
        IProgress<double>? progress, RipReadLog log, CancellationToken ct)
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

        // Red Book CDDA: 16-bit stereo at 44.1 kHz. Only the byte order varies by platform.
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(reader.PcmIsBigEndian ? "s16be" : "s16le");
        psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add("44100");
        psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add("2");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("pipe:0");

        foreach (var argument in RipEncoders.ArgsFor(plan.Format))
            psi.ArgumentList.Add(argument);

        psi.ArgumentList.Add(path);

        using var process = Process.Start(psi)
            ?? throw new DiscReadException("could not start ffmpeg to encode the rip");

        // Read stderr at the same time. A full pipe would stop ffmpeg in the middle of a track.
        var errorTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            // WaitAsync, not a plain await. A drive stuck on a scratch waits in a kernel read that no
            // cancel token can interrupt, and skip must still work on that track. So the read is
            // abandoned instead of waited for. Killing ffmpeg below breaks the pipe under the read,
            // and that is how the abandoned read finishes.
            var read = reader.ReadTrackPcmAsync(
                plan.Disc, track, process.StandardInput.BaseStream, progress, ct, log);
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
            // ffmpeg exited before the write finished, so the pipe broke. "Broken pipe" says nothing
            // useful. ffmpeg's stderr says what went wrong, so report that instead.
            await process.WaitForExitAsync(ct);
            var earlyError = await errorTask;
            throw new DiscReadException(
                $"ffmpeg failed encoding track {track.Number}: {FfmpegSaid.Short(earlyError)}");
        }
        catch
        {
            // Waited for, not only killed. The caller deletes the partly written track, and Windows does not
            // delete a file that ffmpeg still holds open.
            ChildProcess.StopAndWait(process);
            throw;
        }

        await process.WaitForExitAsync(ct);
        var error = await errorTask;

        if (process.ExitCode != 0)
            throw new DiscReadException(
                $"ffmpeg failed encoding track {track.Number}: {FfmpegSaid.Short(error)}");
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new DiscReadException($"ffmpeg produced no output for track {track.Number}");
    }

    /// <summary>Lets an abandoned read fail quietly. It writes into a pipe that is closed under it, so
    /// it WILL fault. Observing the exception here keeps it off the unobserved-exception path.</summary>
    private static void Forget(Task task) =>
        _ = task.ContinueWith(t => _ = t.Exception,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static async Task<bool> FilesMatchAsync(string first, string second, CancellationToken ct)
    {
        await using var a = File.OpenRead(first);
        await using var b = File.OpenRead(second);
        if (a.Length != b.Length)
            return false;

        var hashA = await SHA256.HashDataAsync(a, ct);
        var hashB = await SHA256.HashDataAsync(b, ct);
        return hashA.AsSpan().SequenceEqual(hashB);
    }

    /// <summary>Writes the disc's metadata onto the ripped file, through <see cref="TagReader"/>. The file
    /// also gets its RPM id. Without one, the library cannot detect that the file was moved.</summary>
    private static TrackInfo TagRippedTrack(string path, RipPlan plan, RipTrackPlan trackPlan, TimeSpan duration)
    {
        // The disc ID belongs to the disc, not to a track, so it is set here rather than carried
        // through every track plan. It lets this rip be matched exactly to the same pressing that
        // someone else ripped, without anyone typing anything.
        var info = trackPlan.ToTrackInfo(
            path, duration, plan.Cover?.Embedded.Bytes, plan.Cover?.Embedded.Mime)
            with { MusicBrainzDiscId = plan.Disc.DiscId };

        try { TagReader.WriteAll(path, info); }
        catch { /* Tagging is best effort. The audio is already on disk and is correct. */ }

        return info with { PendingArt = null, PendingArtMime = null, HasArt = plan.Cover is not null };
    }

    /// <summary>The metadata the naming template builds a path from: the disc's plan, plus the
    /// extension of the chosen format. A track with no title uses "Track NN", so a template that uses
    /// {title} still gives a usable name.</summary>
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
        DiscNumber = (uint)track.DiscNumber,
        DiscCount = (uint)track.DiscCount,
        DiscSubtitle = track.DiscSubtitle,
        Year = track.Year,
        Genre = track.Genre,
        Subgenre = track.Subgenre,
    };
}
