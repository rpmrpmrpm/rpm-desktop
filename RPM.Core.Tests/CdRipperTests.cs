using System.Diagnostics;
using RPM.Core.Art;
using RPM.Core.Metadata;
using RPM.Core.Import;
using RPM.Core.Ripping;

namespace RPM.Core.Tests;

/// <summary>
/// Exercises the ripper end to end with a fake drive: a disc reader that synthesises PCM instead of
/// spinning a CD. Everything downstream of the sectors is the real thing (real ffmpeg, real
/// encoding, real tagging, real staging and rollback), so the only part left unproven is the
/// platform-specific sector read itself.
/// </summary>
public class CdRipperTests : IDisposable
{
    private readonly string _staging = Path.Combine(Path.GetTempPath(), $"rpm-rip-test-{Guid.NewGuid():N}");

    public CdRipperTests() => Directory.CreateDirectory(_staging);

    public void Dispose()
    {
        try { Directory.Delete(_staging, recursive: true); } catch { }
    }

    /// <summary>A drive with a two-track disc in it. Each track is a tone, so a track's audio is
    /// deterministic and two reads of it are byte-identical, which is what the verify pass checks.</summary>
    private sealed class FakeDiscReader : IDiscReader
    {
        /// <summary>Corrupt this track's audio on the second and later reads, the way a scratched
        /// disc yields different samples every time you read it.</summary>
        public int? UnstableTrack { get; init; }

        /// <summary>Blow up on this track, to prove a mid-rip failure leaves nothing behind.</summary>
        public int? FailingTrack { get; init; }

        /// <summary>Report this track as past its error budget, the way a reader does once a disc has
        /// refused it too many times: the ripper should skip it and rip the rest.</summary>
        public int? UnreadableTrack { get; init; }

        /// <summary>Never finish this track, like a drive stuck on a scratch. It unwinds only when the
        /// track's own token is cancelled, which is exactly what skip and stop do.</summary>
        public int? HangingTrack { get; init; }

        /// <summary>Set once <see cref="HangingTrack"/> is being read, so a test can press skip at the
        /// moment the rip is actually stuck rather than guess at a delay.</summary>
        public TaskCompletionSource Hanging { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly Dictionary<int, int> _reads = new();

        public bool PcmIsBigEndian => false;

        public Task<IReadOnlyList<DiscDrive>> ListDrivesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DiscDrive>>([new DiscDrive("fake", "Fake Drive")]);

        public Task<DiscInfo?> ReadDiscAsync(DiscDrive drive, CancellationToken ct = default) =>
            Task.FromResult<DiscInfo?>(null);

        /// <summary>There is always a disc in this drive; the tests hand the ripper its TOC directly.</summary>
        public bool AudioDiscPresent() => true;

        public Task EjectAsync(DiscDrive drive, CancellationToken ct = default) => Task.CompletedTask;

        public async Task ReadTrackPcmAsync(DiscInfo disc, DiscTrack track, Stream destination,
            IProgress<double>? progress = null, CancellationToken ct = default, RipReadLog? log = null)
        {
            if (track.Number == FailingTrack)
                throw new DiscReadException($"track {track.Number} is unreadable");

            if (track.Number == UnreadableTrack)
                throw new DiscTrackUnreadableException($"9 unreadable blocks on track {track.Number}");

            if (track.Number == HangingTrack)
            {
                Hanging.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }

            _reads.TryGetValue(track.Number, out var reads);
            _reads[track.Number] = reads + 1;

            var sectors = disc.Toc.SectorCountOf(track);
            var unstable = track.Number == UnstableTrack && reads > 0;

            // 2352 bytes per CDDA sector: 588 stereo frames of 16-bit samples.
            var sector = new byte[2352];
            for (var s = 0; s < sectors; s++)
            {
                ct.ThrowIfCancellationRequested();
                for (var i = 0; i < sector.Length; i += 2)
                {
                    var sample = (short)(Math.Sin((s * 588.0 + i / 4.0) * 0.05) * 8000)
                                 + (unstable ? (short)(i % 251) : (short)0);
                    sector[i] = (byte)(sample & 0xFF);
                    sector[i + 1] = (byte)((sample >> 8) & 0xFF);
                }
                await destination.WriteAsync(sector, ct);
                progress?.Report((s + 1) / (double)sectors);
            }
        }
    }

    /// <summary>A two-track disc: track 1 runs 2 seconds, track 2 runs 3.</summary>
    private static DiscInfo FakeDisc()
    {
        const int oneSecond = 75; // frames
        var tracks = new List<DiscTrack> { new(1, 0, false), new(2, 2 * oneSecond, false) };
        var toc = new DiscToc(1, 2, tracks, LeadoutLba: 5 * oneSecond);
        return new DiscInfo(new DiscDrive("fake", "Fake Drive"), toc, MusicBrainzDiscId.Compute(toc), MountPath: null);
    }

    /// <summary>A flat "NN - Title" staging layout, so the filename assertions read the rip directly
    /// rather than a folder tree. The folder-honouring behaviour has its own test.</summary>
    private static readonly NamingTemplate Layout = new("test", "{track:00} - {title}");

    private static RipPlan Plan(DiscInfo disc, RipFormat format = RipFormat.Flac) => new(
        disc,
        [
            new RipTrackPlan(1, "Opening", "The Band", "The Album", "The Band", 1999, "Rock", 2),
            new RipTrackPlan(2, "Closing", "The Band", "The Album", "The Band", 1999, "Rock", 2),
        ],
        format);

    /// <summary>The audio a rip left in a folder: everything but the sidecars it writes beside it
    /// (the manifest, the folder art), which are not tracks and must not be counted as any.</summary>
    private static string[] AudioIn(string folder) => Directory.GetFiles(folder)
        .Where(f => Scanning.AudioFileScanner.AudioExtensions.Contains(Path.GetExtension(f)))
        .ToArray();

    private static bool FfmpegAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            process!.WaitForExit();
            return process.ExitCode == 0;
        }
        catch { return false; }
    }

    [Fact]
    public async Task Rips_every_selected_track_and_tags_it_from_the_disc()
    {
        if (!FfmpegAvailable()) return; // ffmpeg is the encoder; without it there is nothing to test

        var disc = FakeDisc();
        var ripper = new CdRipper(new FakeDiscReader());

        var result = await ripper.RipAsync(Plan(disc), _staging, Layout);

        Assert.Equal(2, result.Tracks.Count);
        Assert.All(result.Tracks, track => Assert.True(File.Exists(track.FilePath)));

        // The file names carry the track order, so the import queue sees the album in sequence.
        Assert.Equal("01 - Opening.flac", Path.GetFileName(result.Tracks[0].FilePath));
        Assert.Equal("02 - Closing.flac", Path.GetFileName(result.Tracks[1].FilePath));

        // Tags round-trip through the file, not just the record we returned.
        var written = TagReader.Read(result.Tracks[0].FilePath);
        Assert.Equal("Opening", written.Title);
        Assert.Equal("The Album", written.Album);
        Assert.Equal(1999u, written.Year);
        Assert.Equal(1u, written.TrackNumber);
        Assert.Equal(2u, written.TrackCount);

        // Track 1 is the 2-second one; a rip that silently truncated would show up here.
        Assert.Equal(2.0, written.DurationSeconds, precision: 1);
    }

    [Fact]
    public async Task Reports_progress_across_the_whole_disc()
    {
        if (!FfmpegAvailable()) return; // ffmpeg is the encoder; without it there is nothing to test

        // Progress<T> hands its callbacks to the thread pool, so two can arrive at once. The queue is
        // concurrent for the same reason the UI marshals these through the dispatcher.
        var reports = new System.Collections.Concurrent.ConcurrentQueue<RipProgress>();
        var ripper = new CdRipper(new FakeDiscReader());

        await ripper.RipAsync(Plan(FakeDisc()), _staging, Layout,
            progress: new Progress<RipProgress>(reports.Enqueue));

        // The last few reports are still in flight when RipAsync returns.
        await Task.Delay(100);

        Assert.NotEmpty(reports);
        Assert.All(reports, r => Assert.Equal(2, r.TrackTotal));
        Assert.Contains(reports, r => r.CurrentTrack == "Opening");
        Assert.Contains(reports, r => r.CurrentTrack == "Closing");
    }

    [Fact]
    public async Task Reports_each_track_the_moment_it_finishes()
    {
        if (!FfmpegAvailable()) return; // ffmpeg is the encoder; without it there is nothing to test

        // The dialog fills its "ripped so far" list off this callback, so it must fire once per track,
        // in order, each carrying a file that already exists and is playable, and not wait for the end.
        var done = new System.Collections.Concurrent.ConcurrentQueue<Models.TrackInfo>();
        var ripper = new CdRipper(new FakeDiscReader());

        var result = await ripper.RipAsync(Plan(FakeDisc()), _staging, Layout,
            hooks: new RipHooks { TrackDone = new Progress<Models.TrackInfo>(done.Enqueue) });

        // Progress<T> posts to the thread pool, so the last report can still be in flight on return.
        await Task.Delay(100);

        Assert.Equal(2, done.Count);
        var reported = done.ToArray();
        Assert.Equal("Opening", reported[0].Title);
        Assert.Equal("Closing", reported[1].Title);
        Assert.All(reported, t => Assert.True(File.Exists(t.FilePath)));

        // Same tracks, same order as the final result: the list the user played from is the album
        // that gets imported.
        Assert.Equal(
            result.Tracks.Select(t => t.FilePath),
            reported.Select(t => t.FilePath));
    }

    [Fact]
    public async Task Verify_catches_a_track_that_reads_differently_twice()
    {
        if (!FfmpegAvailable()) return; // ffmpeg is the encoder; without it there is nothing to test

        var ripper = new CdRipper(new FakeDiscReader { UnstableTrack = 2 }) { Verify = true };

        var ex = await Assert.ThrowsAsync<DiscReadException>(
            () => ripper.RipAsync(Plan(FakeDisc()), _staging, Layout));

        Assert.Contains("read differently twice", ex.Message);

        // ...and the half-ripped album is gone rather than left for the import queue to find.
        Assert.Empty(Directory.GetDirectories(_staging));
    }

    [Fact]
    public async Task Verify_passes_on_a_disc_that_reads_the_same_twice()
    {
        if (!FfmpegAvailable()) return; // ffmpeg is the encoder; without it there is nothing to test

        var ripper = new CdRipper(new FakeDiscReader()) { Verify = true };

        var result = await ripper.RipAsync(Plan(FakeDisc()), _staging, Layout);

        Assert.Equal(2, result.Tracks.Count);
        Assert.All(result.Tracks, track => Assert.True(File.Exists(track.FilePath)));

        // The verify pass re-rips each track to a scratch file; none of them may survive into the
        // folder we hand to the importer, or the album would import twice over.
        Assert.Equal(2, AudioIn(result.StagingFolder).Length);
    }

    [Fact]
    public async Task A_failure_part_way_through_leaves_nothing_behind()
    {
        if (!FfmpegAvailable()) return; // ffmpeg is the encoder; without it there is nothing to test

        var ripper = new CdRipper(new FakeDiscReader { FailingTrack = 2 });

        await Assert.ThrowsAsync<DiscReadException>(() => ripper.RipAsync(Plan(FakeDisc()), _staging, Layout));

        Assert.Empty(Directory.GetDirectories(_staging));
    }

    [Fact]
    public async Task Cancelling_mid_rip_leaves_nothing_behind()
    {
        if (!FfmpegAvailable()) return; // ffmpeg is the encoder; without it there is nothing to test

        using var cts = new CancellationTokenSource();
        var ripper = new CdRipper(new FakeDiscReader());

        // Cancel as soon as the first track starts moving.
        var progress = new Progress<RipProgress>(_ => cts.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ripper.RipAsync(Plan(FakeDisc()), _staging, Layout, progress, cts.Token));

        Assert.Empty(Directory.GetDirectories(_staging));
    }

    // ── Stopping, skipping, and a disc that will not read ────────────────────────────────────
    //
    // A damaged disc needs more than one way out. These three endings are deliberately different:
    // cancel keeps nothing, stop keeps what was written, skip loses one track. Everything below is
    // about which files survive each of them.

    [Fact]
    public async Task Stopping_keeps_the_tracks_that_already_landed()
    {
        if (!FfmpegAvailable()) return; // ffmpeg is the encoder; without it there is nothing to test

        var control = new RipControl();
        var ripper = new CdRipper(new FakeDiscReader());

        // Stop once the first track is done, which is what a user watching the list does.
        var trackDone = new Progress<Models.TrackInfo>(_ => control.Stop());

        var result = await ripper.RipAsync(Plan(FakeDisc()), _staging, Layout,
            hooks: new RipHooks { Control = control, TrackDone = trackDone });

        Assert.True(result.Stopped);
        Assert.Single(result.Tracks);
        Assert.Equal("Opening", result.Tracks[0].Title);

        // The purpose of stop: the folder is still there, holding exactly what was ripped, ready
        // for the import queue.
        Assert.True(Directory.Exists(result.StagingFolder));
        Assert.Single(AudioIn(result.StagingFolder));
    }

    [Fact]
    public async Task Stopping_before_anything_landed_leaves_nothing_behind()
    {
        if (!FfmpegAvailable()) return; // ffmpeg is the encoder; without it there is nothing to test

        var control = new RipControl();
        var ripper = new CdRipper(new FakeDiscReader());

        // Stopped in the first track: there is no album to keep, so it ends exactly as a cancel does.
        var progress = new Progress<RipProgress>(_ => control.Stop());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ripper.RipAsync(
            Plan(FakeDisc()), _staging, Layout, hooks: new RipHooks { Control = control, Progress = progress }));

        Assert.Empty(Directory.GetDirectories(_staging));
    }

    [Fact]
    public async Task Skipping_a_track_the_drive_will_never_finish_rips_the_rest()
    {
        if (!FfmpegAvailable()) return; // ffmpeg is the encoder; without it there is nothing to test

        var reader = new FakeDiscReader { HangingTrack = 1 };
        var control = new RipControl();
        var skipped = new List<RipTrackOutcome>();

        // Press skip the moment the rip is genuinely stuck, not after a guessed delay. The read never
        // returns on its own, so this also proves the skip does not wait for it.
        _ = reader.Hanging.Task.ContinueWith(_ => control.SkipTrack(), TaskScheduler.Default);

        var result = await new CdRipper(reader).RipAsync(Plan(FakeDisc()), _staging, Layout,
            hooks: new RipHooks
            {
                Control = control,
                TrackSkipped = new Progress<RipTrackOutcome>(skipped.Add),
            });

        Assert.False(result.Stopped);
        Assert.Single(result.Tracks);
        Assert.Equal("Closing", result.Tracks[0].Title);

        // The skipped track is reported rather than silently missing, and its half file is gone.
        Assert.Equal(2, result.TrackOutcomes.Count);
        Assert.Equal(RipTrackResult.SkippedByHand, result.TrackOutcomes[0].Result);
        Assert.Equal(RipTrackResult.Ripped, result.TrackOutcomes[1].Result);
        Assert.Single(AudioIn(result.StagingFolder));

        // Progress<T> posts to the thread pool, so the report can still be in flight on return.
        await Task.Delay(100);
        Assert.Single(skipped);
        Assert.Equal(1, skipped[0].Number);
    }

    [Fact]
    public async Task A_track_past_its_error_budget_is_skipped_and_the_disc_carries_on()
    {
        if (!FfmpegAvailable()) return; // ffmpeg is the encoder; without it there is nothing to test

        // No button pressed: the reader reports the track as spent and the rip moves on by itself.
        var ripper = new CdRipper(new FakeDiscReader { UnreadableTrack = 1 });

        var result = await ripper.RipAsync(Plan(FakeDisc()), _staging, Layout);

        Assert.Single(result.Tracks);
        Assert.Equal("Closing", result.Tracks[0].Title);
        Assert.Equal(RipTrackResult.SkippedUnreadable, result.TrackOutcomes[0].Result);
        Assert.True(Directory.Exists(result.StagingFolder));
    }

    [Fact]
    public async Task A_disc_where_every_track_is_unreadable_fails_and_leaves_nothing()
    {
        if (!FfmpegAvailable()) return; // ffmpeg is the encoder; without it there is nothing to test

        // Skipping every track is not a rip. There is nothing to import, so this is a failure with a
        // message rather than an empty album handed to the queue.
        var reader = new FakeDiscReader { UnreadableTrack = 1, HangingTrack = 2 };
        var control = new RipControl();
        _ = reader.Hanging.Task.ContinueWith(_ => control.SkipTrack(), TaskScheduler.Default);

        var ex = await Assert.ThrowsAsync<DiscReadException>(() => new CdRipper(reader).RipAsync(
            Plan(FakeDisc()), _staging, Layout, hooks: new RipHooks { Control = control }));

        Assert.Contains("nothing on this disc could be read", ex.Message);
        Assert.Empty(Directory.GetDirectories(_staging));
    }

    [Fact]
    public async Task Cancelling_still_throws_the_whole_rip_away_even_after_a_track_landed()
    {
        if (!FfmpegAvailable()) return; // ffmpeg is the encoder; without it there is nothing to test

        // The line between cancel and stop: same moment in the rip, opposite outcome for the files.
        using var cts = new CancellationTokenSource();
        var ripper = new CdRipper(new FakeDiscReader());
        var trackDone = new Progress<Models.TrackInfo>(_ => cts.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ripper.RipAsync(
            Plan(FakeDisc()), _staging, Layout, ct: cts.Token,
            hooks: new RipHooks { TrackDone = trackDone }));

        Assert.Empty(Directory.GetDirectories(_staging));
    }

    [Fact]
    public async Task A_stopped_multi_disc_rip_keeps_the_earlier_disc_it_was_added_to()
    {
        if (!FfmpegAvailable()) return; // ffmpeg is the encoder; without it there is nothing to test

        // Disc one, ripped whole.
        var first = await new CdRipper(new FakeDiscReader()).RipAsync(Plan(FakeDisc()), _staging, Layout);

        // Disc two of the same set, stopped before it writes anything. It ends as a cancel, but the
        // folder is disc one's, so nothing of disc one may go with it.
        var control = new RipControl();
        var progress = new Progress<RipProgress>(_ => control.Stop());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CdRipper(new FakeDiscReader())
            .RipAsync(Plan(FakeDisc()), _staging, Layout, stagingFolder: first.StagingFolder,
                hooks: new RipHooks { Control = control, Progress = progress }));

        Assert.True(Directory.Exists(first.StagingFolder));
        Assert.All(first.Tracks, track => Assert.True(File.Exists(track.FilePath)));
    }

    [Fact]
    public async Task Embeds_cover_art_and_release_country_on_every_track()
    {
        if (!FfmpegAvailable()) return; // ffmpeg is the encoder; without it there is nothing to test

        var art = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 5, 6, 7, 8 }; // jpeg-ish header
        var plan = new RipPlan(
            FakeDisc(),
            [
                new RipTrackPlan(1, "Opening", "The Band", "The Album", "The Band", 1999, "Rock", 2, Country: "GB"),
                new RipTrackPlan(2, "Closing", "The Band", "The Album", "The Band", 1999, "Rock", 2, Country: "GB"),
            ],
            RipFormat.Flac,
            Cover: Sleeve.Of(new AlbumArt(art, "image/jpeg")));

        var result = await new CdRipper(new FakeDiscReader()).RipAsync(plan, _staging, Layout);

        // Country and sleeve are the two things a disc cannot supply from itself: they come off the
        // lookup, so the rip has to carry them onto the files or they are lost.
        foreach (var track in result.Tracks)
        {
            var read = TagReader.Read(track.FilePath);
            Assert.Equal("GB", read.ReleaseCountry);
            Assert.True(read.HasArt);
        }

        var cover = TagReader.LoadArt(result.Tracks[0].FilePath);
        Assert.NotNull(cover);
        Assert.Equal(art, cover!.Value.Bytes);
    }

    [Fact]
    public async Task A_rip_writes_down_what_the_disc_was_before_it_reads_a_sector()
    {
        if (!FfmpegAvailable()) return; // ffmpeg is the encoder; without it there is nothing to test

        var result = await new CdRipper(new FakeDiscReader()).RipAsync(Plan(FakeDisc()), _staging, Layout);

        // The folder is a guid and the files may never get tagged. What the lookup knew is written
        // beside them, so a rip found again weeks later is still "The Album" and not a guid.
        var manifest = RipManifest.Read(result.StagingFolder);
        Assert.NotNull(manifest);
        Assert.Equal(RipSource.Cd, manifest!.Source);
        Assert.Equal("The Album", manifest.Title);
        Assert.Equal("The Band", manifest.Artist);
        Assert.Equal(2, manifest.TrackCount);
        Assert.Equal("Fake Drive", manifest.Device);
        Assert.NotEqual(default, manifest.CreatedUtc);
    }

    [Fact]
    public async Task A_rip_that_stops_short_keeps_the_art_the_lookup_fetched()
    {
        if (!FfmpegAvailable()) return; // ffmpeg is the encoder; without it there is nothing to test

        var art = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 5, 6, 7, 8 };   // jpeg-ish header
        var disc = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 };  // png-ish header
        var plan = Plan(FakeDisc()) with { Cover = Sleeve.Of(new AlbumArt(art, "image/jpeg")), DiscArt = disc };

        var control = new RipControl();
        var trackDone = new Progress<Models.TrackInfo>(_ => control.Stop());

        var result = await new CdRipper(new FakeDiscReader()).RipAsync(plan, _staging, Layout,
            hooks: new RipHooks { Control = control, TrackDone = trackDone });

        // The sleeve and the label were fetched once, by a lookup that will not run again for a rip
        // sitting in staging. Writing them only after the last track would mean a stopped rip (or one
        // the app never got to finish) is imported later with no art at all.
        Assert.True(result.Stopped);
        var folder = Path.GetDirectoryName(result.Tracks[0].FilePath)!;
        Assert.NotNull(Import.FolderArtWriter.FindFolderImage(folder));
        Assert.NotNull(Import.FolderArtWriter.FindDiscImage(folder));
    }

    [Fact]
    public async Task Mp3_rips_land_as_mp3_files()
    {
        if (!FfmpegAvailable()) return; // ffmpeg is the encoder; without it there is nothing to test

        var ripper = new CdRipper(new FakeDiscReader());

        var result = await ripper.RipAsync(Plan(FakeDisc(), RipFormat.Mp3), _staging, Layout);

        Assert.All(result.Tracks, track => Assert.Equal(".mp3", Path.GetExtension(track.FilePath)));
        Assert.All(result.Tracks, track => Assert.True(new FileInfo(track.FilePath).Length > 0));
    }

    [Fact]
    public async Task Lands_the_rip_under_the_naming_template_folders()
    {
        if (!FfmpegAvailable()) return; // ffmpeg is the encoder; without it there is nothing to test

        var ripper = new CdRipper(new FakeDiscReader());

        // A folder-shaped template must file the rip into folders in staging, so the import queue
        // shows the arrangement it will keep rather than a flat pile.
        var result = await ripper.RipAsync(
            Plan(FakeDisc()), _staging, NamingTemplate.ArtistAlbumNumberedTitle);

        var relative = Path.GetRelativePath(result.StagingFolder, result.Tracks[0].FilePath);
        Assert.Equal(Path.Combine("The Band", "The Album", "01 - Opening.flac"), relative);
        Assert.True(File.Exists(result.Tracks[0].FilePath));
    }
}
