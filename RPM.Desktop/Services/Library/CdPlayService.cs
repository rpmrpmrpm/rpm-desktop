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
/// from the catalogue's answer, and handed to the player: the first one played, every one after it added to
/// the queue as it lands. A drive reads many times faster than the music plays, so the queue is always
/// ahead of the listener after the first few seconds, and next, previous and seeking work as they do for
/// any record.</para>
///
/// <para>The files live under the app's own data, one folder per disc, and the folder of the disc before is
/// cleared when another is played. Nothing here touches the library, and a rip, which owns the drive, stops
/// the reading.</para>
/// </summary>
public sealed class CdPlayService
{
    private readonly RipperService _ripper;
    private readonly RPaMpService _player;
    private readonly RipJobService _rips;
    private CancellationTokenSource? _reading;

    public CdPlayService(RipperService ripper, RPaMpService player, RipJobService rips)
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
    }

    private static string Root => Path.Combine(LocalData.Dir, "cd-play");

    /// <summary>Starts playing <paramref name="disc"/>: the first track as soon as it is read, the rest
    /// queued behind it.</summary>
    /// <param name="listing">What the catalogue knows about the disc, or null for one it does not know: the
    /// tracks are then called by their numbers.</param>
    /// <param name="cover">The sleeve, where one was found. It is kept beside the files, where everything
    /// that draws a record looks for a folder's picture.</param>
    public async Task PlayAsync(DiscInfo disc, CatalogDisc? listing, byte[]? cover)
    {
        _reading?.Cancel();
        var reading = new CancellationTokenSource();
        _reading = reading;
        var ct = reading.Token;

        var folder = Path.Combine(Root, Safe(disc.DiscId));
        try
        {
            ClearOthers(folder);
            Directory.CreateDirectory(folder);
            // Play can be pressed before the card's sleeve has come: it is then asked for here, by the same
            // names, rather than leaving the disc without one.
            if (cover is not { Length: > 0 } && listing is not null)
                cover = await SleeveAsync(listing, ct);
            if (cover is { Length: > 0 })
                await File.WriteAllBytesAsync(Path.Combine(folder, "cover.jpg"), cover, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Said.Line("cd", $"could not make a place for the disc's tracks: {ex.Message}");
            return;
        }

        var audio = disc.Toc.Tracks.Where(t => !t.IsData).ToList();
        var first = true;
        foreach (var track in audio)
        {
            if (ct.IsCancellationRequested)
                return;

            var info = Describe(disc, track, listing, audio.Count, Path.Combine(folder, $"{track.Number:00}.wav"));
            try
            {
                if (!File.Exists(info.FilePath))
                {
                    var part = info.FilePath + ".part";
                    await CdWav.WriteAsync(part, into => _ripper.ReadTrackPcmAsync(disc, track, into, ct),
                        _ripper.PcmIsBigEndian, ct);
                    File.Move(part, info.FilePath, overwrite: true);
                    try { TagReader.Write(info.FilePath, info); }
                    catch (Exception ex) { Said.Line("cd", $"track {track.Number} plays untagged: {ex.Message}"); }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // A scratch the drive cannot read past, or the disc taken out: what has been read plays on.
                Said.Line("cd", $"stopped reading the disc at track {track.Number}: {ex.Message}");
                return;
            }

            if (ct.IsCancellationRequested)
                return;

            if (first)
                _player.PlayTracksFrom([info]);
            else
                _player.AddTracks([info], play: false);
            first = false;
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
