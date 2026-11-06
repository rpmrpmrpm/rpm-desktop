using System.Diagnostics;
using RPM.Core.Metadata;
using RPM.Core.Metadata.Providers;
using RPM.Core.Models;
using RPM.Core.Ripping;
using RPM.Desktop.Services.Platform;
using RPM.Desktop.Services.Shell;
using RPM.Desktop.Strings;
using Said = RPM.Core.Diagnostics.Said;
using RPM.Fingerprint.Catalog;

namespace RPM.Desktop.Services.Library;

/// <summary>
/// Plays the CD in the drive, as it is, without adding it to the collection.
///
/// <para>Each track is read off the disc into a WAV of its own (see <see cref="CdWav"/>), named and tagged
/// from the catalogue's answer. The player is given a track as soon as its first seconds have arrived, while
/// the rest is still being read: the first one to play, every one after it added to the queue. A drive reads
/// several times faster than the music plays, so the read pulls away from the listener, and next, previous
/// and seeking work as they do for any record. A jump past the read takes the read with it, and lands when the
/// first seconds there have arrived (see <see cref="CdTrackFill"/>).</para>
///
/// <para>The files live under the app's own data, one folder per disc, and the folder of the disc before is
/// cleared when another is played. Nothing here touches the library, and a rip, which owns the drive, stops
/// the reading. The track being read then ends where the read stopped.</para>
/// </summary>
public sealed class CdPlayService
{
    /// <summary>How many seconds of a track must have arrived before the player gets it. The decoder and the
    /// sound device together take about two seconds of a file at once when a track starts. The rest is
    /// margin for a drive that pauses in the first moments of a read.</summary>
    private const double LeadSeconds = 5;

    /// <summary>How many seconds must have arrived where a jump lands. The decoder takes about two seconds of
    /// a file at once, here too. An audio drive reads this much in well under a second.</summary>
    private const double JumpLeadSeconds = 3;

    /// <summary>How long the first track waits for a sleeve still on its way. The player looks for a
    /// record's picture when the record starts, so a sleeve that lands just before the music is seen and one
    /// that lands after it is not. The music does not wait longer than this for a picture.</summary>
    private static readonly TimeSpan SleeveWait = TimeSpan.FromSeconds(2);

    private readonly RipperService _ripper;
    private readonly RPaMpService _player;
    private readonly RipJobService _rips;
    private CancellationTokenSource? _reading;

    /// <summary>The run in progress. A new run waits for it, because it may hold a file open.</summary>
    private Task _run = Task.CompletedTask;

    /// <summary>The track being read now.</summary>
    private volatile PartTrack? _part;

    private sealed record PartTrack(string Path, CdTrackFill Fill);

    public CdPlayService(RipperService ripper, RPaMpService player, RipJobService rips, PlaybackService playback)
    {
        _ripper = ripper;
        _player = player;
        _rips = rips;

        // The drive is the rip's while it runs: reading the disc for playback at the same time would make
        // both slow, and the rip's reads the ones that matter.
        _rips.Changed += () =>
        {
            if (_rips.IsRunning)
                _reading?.Cancel();
        };

        playback.BeforeSeek = BeforeSeek;
    }

    /// <summary>Takes the read to where the listener is going, when they are in the track being read.</summary>
    private Task? BeforeSeek(string path, double seconds)
    {
        if (_part is not { } part || !string.Equals(part.Path, path, StringComparison.Ordinal))
            return null;

        var ready = part.Fill.Jump(seconds);
        if (!ready.IsCompleted)
            _ = NoteJumpAsync(ready, seconds);
        return ready;
    }

    private static async Task NoteJumpAsync(Task ready, double seconds)
    {
        var waited = Stopwatch.StartNew();
        await ready;
        Said.Line("cd", $"a jump to {seconds:0.0} s waited {waited.Elapsed.TotalSeconds:0.0} s for the disc");
    }

    private static string Root => Path.Combine(LocalData.Dir, "cd-play");

    /// <summary>Starts playing <paramref name="disc"/>: the first track a few seconds into its read, the rest
    /// queued behind it the same way.</summary>
    /// <param name="listing">What the catalogue knows about the disc, or null for one it does not know: the
    /// tracks are then called by their numbers.</param>
    /// <param name="cover">The sleeve, where one was found. It is kept beside the files, where everything
    /// that draws a record looks for a folder's picture.</param>
    public Task PlayAsync(DiscInfo disc, CatalogDisc? listing, byte[]? cover)
    {
        _reading?.Cancel();
        var reading = new CancellationTokenSource();
        _reading = reading;
        var ct = reading.Token;

        var folder = Path.Combine(Root, Safe(disc.DiscId));
        var audio = disc.Toc.Tracks.Where(t => !t.IsData).ToList();
        var tracks = audio
            .Select(t => (Track: t, Info: Describe(disc, t, listing, audio.Count, Path.Combine(folder, $"{t.Number:00}.wav"))))
            .ToList();

        // On the thread pool, because laying a file out and tagging it are blocking work.
        var before = _run;
        return _run = Task.Run(async () =>
        {
            await before;
            await ReadDiscAsync(disc, folder, tracks, listing, cover, ct);
        });
    }

    /// <summary>Reads the disc from its first track to its last, handing each track to the player as soon as
    /// it can play. This never throws, so the run after it can wait for it.</summary>
    private async Task ReadDiscAsync(DiscInfo disc, string folder, IReadOnlyList<(DiscTrack Track, TrackInfo Info)> tracks,
        CatalogDisc? listing, byte[]? cover, CancellationToken ct)
    {
        Task sleeve;
        try
        {
            ClearOthers(folder);
            Directory.CreateDirectory(folder);
            sleeve = KeepSleeveAsync(folder, listing, cover, ct);
        }
        catch (Exception ex)
        {
            Said.Line("cd", $"could not make a place for the disc's tracks: {ex.Message}");
            return;
        }

        var pressed = Stopwatch.StartNew();
        var first = true;
        foreach (var (track, info) in tracks)
        {
            var read = Task.CompletedTask;
            try
            {
                ct.ThrowIfCancellationRequested();

                // A swapped sample cannot be played until the read has ended, so such a track never has
                // enough until then.
                var lead = _ripper.PcmIsBigEndian ? double.PositiveInfinity : LeadSeconds;
                var fill = new CdTrackFill(disc.Toc.SectorCountOf(track), lead, JumpLeadSeconds);
                if (!CdWav.IsWhole(info.FilePath))
                    read = ReadTrackAsync(disc, track, info, fill, ct);

                // A whole track has nothing to wait for, and a read that failed this early has nothing worth
                // playing.
                await Task.WhenAny(fill.Enough, read);
                if (read.IsFaulted || read.IsCanceled)
                    await read;

                if (first)
                {
                    await Task.WhenAny(sleeve, Task.Delay(SleeveWait, ct));
                    ct.ThrowIfCancellationRequested();
                    _player.PlayTracksFrom([info]);
                    Said.Line("cd", $"track {track.Number} given to the player {pressed.Elapsed.TotalSeconds:0.0} s "
                        + "after play");
                }
                else
                {
                    _player.AddTracks([info], play: false);
                }
                first = false;

                await read;
            }
            catch (OperationCanceledException)
            {
                // The read may still be stopping, and it holds its file until it has. The next run needs
                // that file, and it waits only for this method.
                await Task.WhenAny(read);
                return;
            }
            catch (Exception ex)
            {
                // A scratch the drive cannot read past, or the disc taken out: what has been read plays on.
                Said.Line("cd", $"stopped reading the disc at track {track.Number}: {ex.Message}");
                return;
            }
        }
    }

    /// <summary>Reads one track into its file, going wherever <paramref name="fill"/> sends the drive.</summary>
    private async Task ReadTrackAsync(DiscInfo disc, DiscTrack track, TrackInfo info, CdTrackFill fill, CancellationToken ct)
    {
        var bytes = (long)disc.Toc.SectorCountOf(track) * CdWav.BytesPerSector;
        var audioStart = CdWav.Begin(info.FilePath, bytes, path =>
        {
            try { TagReader.Write(path, info); }
            catch (Exception ex) { Said.Line("cd", $"track {track.Number} plays untagged: {ex.Message}"); }
        });

        _part = new PartTrack(info.FilePath, fill);
        var reading = Stopwatch.StartNew();
        try
        {
            await CdWav.FillAsync(info.FilePath, audioStart, bytes, async into =>
            {
                try
                {
                    await ReadGapsAsync(disc, track, into, audioStart, fill, ct);
                }
                catch
                {
                    into.Position = audioStart + (long)fill.Furthest * CdWav.BytesPerSector;
                    throw;
                }
            }, _ripper.PcmIsBigEndian, ct);
        }
        finally
        {
            _part = null;
            fill.Stop();
        }

        var seconds = reading.Elapsed.TotalSeconds;
        Said.Line("cd", $"track {track.Number} read in {seconds:0.0} s, at {info.DurationSeconds / seconds:0.0}x");
    }

    /// <summary>Reads whatever <paramref name="fill"/> says is missing until nothing is. A jump stops the read
    /// in progress, and this then asks again where to go.</summary>
    private async Task ReadGapsAsync(DiscInfo disc, DiscTrack track, Stream into, long audioStart, CdTrackFill fill,
        CancellationToken ct)
    {
        while (true)
        {
            using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (fill.Next(run) is not { } gap)
                return;

            into.Position = audioStart + (long)gap.From * CdWav.BytesPerSector;
            try
            {
                await _ripper.ReadTrackPcmAsync(disc, track, into, fill.ProgressOf(gap.From, gap.Count), run.Token,
                    gap.From, gap.Count);

                // A reader that ran out early has told us all it can. The rest of the gap stays silent.
                fill.Arrived(gap.From, gap.Count);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
        }
    }

    /// <summary>Puts the sleeve beside the tracks. Play can be pressed before the card's sleeve has come: it
    /// is then asked for here, by the same names, rather than leaving the disc without one. A disc plays
    /// with or without its picture, so a failure here stops nothing.</summary>
    private static async Task KeepSleeveAsync(string folder, CatalogDisc? listing, byte[]? cover, CancellationToken ct)
    {
        var asked = Stopwatch.StartNew();
        var hadIt = cover is { Length: > 0 };
        try
        {
            if (!hadIt && listing is not null)
                cover = await SleeveAsync(listing, ct);
            if (cover is { Length: > 0 })
                await File.WriteAllBytesAsync(Path.Combine(folder, "cover.jpg"), cover, ct);

            Said.Line("cd", cover is { Length: > 0 }
                ? $"sleeve kept, {cover.Length} bytes, {(hadIt ? "from the card" : $"found in {asked.Elapsed.TotalSeconds:0.0} s")}"
                : listing is null ? "no sleeve to look for: the disc has no name" : $"no sleeve found for {listing.ReleaseId}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Said.Line("cd", $"the disc plays without its sleeve: {ex.Message}");
        }
    }

    /// <summary>The sleeve of the disc the catalogue named, or null. It is found as Import from CD finds it:
    /// the art archive's scan of that very pressing first, then the artwork service by artist and album. The
    /// service alone has nothing for a great many discs, a box of Mozart's sonatas among them, that the
    /// archive has a scan of.</summary>
    public static async Task<byte[]?> SleeveAsync(CatalogDisc listing, CancellationToken ct)
    {
        var release = new ReleaseSearchResult(
            new ReleaseRef(RpmMetadataProvider.ProviderId, listing.ReleaseId),
            listing.Title, listing.Artist, listing.Year,
            Date: null, Country: null, Format: null, TrackCount: listing.Tracks.Count);
        try
        {
            if (await App.Services.GetRequiredService<Catalog.CatalogService>().SleeveAsync(release, ct) is { } sleeve)
                return sleeve.Folder.Bytes;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Said.Line("cd", $"the archive had no sleeve for {listing.ReleaseId}: {ex.Message}");
        }

        return (await App.Services.GetRequiredService<Art.ArtworkService>()
            .CoverBytesAsync(listing.Artist, listing.Title, ct))?.Bytes;
    }

    /// <summary>One track as the player shows it: the catalogue's names where it has them, the track's number
    /// where it does not.</summary>
    private static TrackInfo Describe(DiscInfo disc, DiscTrack track, CatalogDisc? listing, int count, string path)
    {
        var listed = listing?.Tracks.FirstOrDefault(t => t.Position == (uint)track.Number);
        var album = listing?.Title is { Length: > 0 } title ? title : Loc.Get("Rip_OfferTitle");
        var albumArtist = listing?.Artist is { Length: > 0 } artist ? artist : Loc.Get("Queue_UnknownArtist");
        return new TrackInfo
        {
            FilePath = path,
            Extension = ".wav",
            DurationSeconds = disc.Toc.DurationOf(track).TotalSeconds,
            Title = listed?.Title is { Length: > 0 } name ? name : Loc.Format("AddMusic_TrackN", track.Number),
            Artist = listed?.Artist is { Length: > 0 } by ? by : albumArtist,
            AlbumArtist = albumArtist,
            Album = album,
            TrackNumber = (uint)track.Number,
            TrackCount = (uint)count,
            DiscNumber = listing?.DiscNumber ?? 1,
            DiscCount = listing?.DiscCount ?? 1,
            Year = listing?.Year ?? 0,
            Genre = listing?.Genre,
        };
    }

    /// <summary>Clears every disc's folder but this one's. A file the player still has open is left for next
    /// time.</summary>
    private static void ClearOthers(string keep)
    {
        if (!Directory.Exists(Root))
            return;
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            if (string.Equals(dir, keep, StringComparison.OrdinalIgnoreCase))
                continue;
            try { Directory.Delete(dir, recursive: true); }
            catch { /* still playing from it: the next disc clears it */ }
        }
    }

    /// <summary>A disc id as a folder name: MusicBrainz's ids use '.', '_' and '-', which are all safe, but
    /// nothing from a drive is trusted to be.</summary>
    private static string Safe(string id) => new([.. id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')]);
}
