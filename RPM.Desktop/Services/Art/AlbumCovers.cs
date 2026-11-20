using RPM.Core.Import;
using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Core.Models;
using RPM.Desktop.Services.Sharing;

namespace RPM.Desktop.Services.Art;

/// <summary>
/// Where a record's cover comes from, in the order the sources are tried.
///
/// <para>First, the folder's own cover image. Import writes cover.jpg into every album folder it
/// creates. Second, the embedded art of the first track in the folder, for folders that import did
/// not create. For a record this machine does not hold, the picture comes from the computer on the
/// network that has the files, and then from the cloud. An adopted album has no folder and no files,
/// so neither local source can answer. Without the remote sources, these records would show blank
/// squares.</para>
///
/// <para>The logic lives here because there is more than one wall. The library window's wall and its
/// list both read covers this way. The collection over the visualizer must read all four sources. Reading only the first
/// one finds no cover for any record stored somewhere else. A record shows the same cover on every
/// screen.</para>
/// </summary>
public static class AlbumCovers
{
    /// <summary>
    /// The cover for one album folder, cut so that it still has <paramref name="edge"/> pixels across
    /// its SHORT side. A surface that draws the cover in a square needs this.
    ///
    /// <para>Every wall, list, drawer and shelf in the app crops its cover with <c>UniformToFill</c>.
    /// A crop shows the short side. <see cref="LoadAsync"/> caps the LONG side. That is the right rule
    /// for a picture shown whole, but the wrong one for a picture shown square. For example, a
    /// landscape sleeve capped at 384 comes back as 384 by 274. Filling a 384 square from it enlarges
    /// the picture by about 1.4 times, so it looks soft. Square sleeves, which are most of them, cost
    /// one header read and take the normal path. That is why only some covers looked soft when the
    /// wall was zoomed in.</para>
    ///
    /// <para>This is a second method, not a flag on the first one. Which side a surface needs depends
    /// on the surface, not on the record. Every caller that draws a square wants this method.</para>
    /// </summary>
    public static async Task<Asked<byte[]?>> LoadForSquareAsync(
        string folder, CancellationToken ct, int edge, bool ownCollection = false)
    {
        var asked = await LoadAsync(folder, ct, edge, ownCollection);
        if (!asked.Answers(out var bytes, out _) || bytes is not { Length: > 0 })
            return asked;

        var (w, h) = ArtScaler.BoundsOf(bytes);
        if (Math.Min(w, h) >= edge || Math.Max(w, h) <= 0)
            return asked;

        // The wider read is only a sharper copy of a picture already in hand. If it fails, the first copy
        // is still the answer.
        var wider = ArtScaler.LongEdgeForShortSide(w, h, edge);
        return wider > edge
               && (await LoadAsync(folder, ct, wider, ownCollection)).Answers(out var fuller, out _)
               && fuller is { Length: > 0 }
            ? Asked.Answer<byte[]?>(fuller)
            : asked;
    }

    /// <summary>A cover that takes longer than this is said on the ring, with what it waited for.</summary>
    private const double SlowMs = 1000;

    /// <summary>
    /// The cover for one album folder.
    ///
    /// <para>Answered with null when every source that was asked says there is no picture. Unanswered when
    /// a source that might have one could not be read: no library open, a picture in the folder that could
    /// not be read, a tag that could not be read, or a computer or cloud that did not answer. A caller must
    /// not draw a made sleeve, or keep "no cover", for an unanswered read.</para>
    /// </summary>
    /// <param name="maxEdge">The longest side the caller can use. Use 0 for the picture as it is.
    /// When this is above 0, a cover already on disk is taken from <see cref="ThumbCache"/> by its
    /// path, and the scan itself is never opened. This is the difference between a wall that fills
    /// quickly and a wall that reads a few hundred megabytes to fill. Callers that pass the bytes on
    /// to code that needs the whole picture leave this at 0.</param>
    /// <param name="ownCollection">Which mirror the folder belongs to. The window's wall shows the
    /// shelf, which is a borrowed collection while one is open, and it reads covers from the mirror it
    /// listed. The visualizer's wall always lists the user's own collection, so it must read against
    /// the user's own root. A borrowed shelf does not know those folders.</param>
    public static async Task<Asked<byte[]?>> LoadAsync(
        string folder, CancellationToken ct, int maxEdge = 0, bool ownCollection = false)
    {
        var library = App.Services.GetRequiredService<LibraryService>();
        if (!library.IsOpen)
            return Asked.Failed<byte[]?>("no library is open");

        var mirror = ownCollection ? library.Db : library.Shelf;
        if ((ownCollection ? library.Root : library.ShelfRoot) is not { } root)
            return Asked.Failed<byte[]?>("the collection has no folder");

        // What a slow cover spent its time on, said on the ring when it is slow. A cover that takes
        // seconds at startup holds up whatever the screen is waiting for, and from outside the four
        // places it can wait (a pool thread, the folder, the library, the track's tag) look the same.
        var asked = System.Diagnostics.Stopwatch.GetTimestamp();
        double queuedMs = 0, folderMs = 0, mirrorMs = 0, tagMs = 0;
        var where = "folder";

        var (bytes, cloudTrack, failed) = await Task.Run<(byte[]? Bytes, LibraryTrack? Track, string? Failed)>(() =>
        {
            queuedMs = System.Diagnostics.Stopwatch.GetElapsedTime(asked).TotalMilliseconds;
            var step = System.Diagnostics.Stopwatch.StartNew();
            double Lap()
            {
                var ms = step.Elapsed.TotalMilliseconds;
                step.Restart();
                return ms;
            }

            var directory = folder.Length == 0
                ? root
                : Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));

            try
            {
                if (FolderArtWriter.FindFolderImage(directory) is { } image)
                {
                    var found = maxEdge > 0 ? ThumbCache.GetFile(image, maxEdge) : File.ReadAllBytes(image);
                    folderMs = Lap();

                    // The folder has a picture. One that could not be read (locked, half written, gone
                    // between the two calls) is a failure, not a record with no cover.
                    return found is { Length: > 0 }
                        ? (found, (LibraryTrack?)null, (string?)null)
                        : (null, null, "the folder's picture could not be read");
                }
            }
            catch (Exception ex)
            {
                return (null, null, $"the folder's picture could not be read: {ex.Message}");
            }

            folderMs = Lap();
            where = "tag";

            // The mirror lookup is required. If it cannot answer, the card is blank. If it is refused,
            // LibraryService.Read shows the problem screen over the app.
            if (!library.Read(mirror, "a record's first track for its cover",
                        m => m.QueryFolderPage(folder, 0, 1).FirstOrDefault())
                    .Answers(out var first, out var why))
                return (null, null, why);

            // A folder with no tracks in the library has no tag to read and nothing to ask anyone about.
            if (first is null)
                return (null, null, null);

            mirrorMs = Lap();

            var file = first.ToTrackInfo(root).FilePath;
            try
            {
                if (TagReader.LoadArt(file)?.Bytes is { Length: > 0 } embedded)
                {
                    var cut = Cut(embedded);
                    tagMs = Lap();
                    return (cut, null, null);
                }
            }
            catch (Exception ex)
            {
                // A locked or half-written file. Its tag may well hold a picture.
                return (null, null, $"the track's tag could not be read: {ex.Message}");
            }

            return SleeveElsewhere(first, fileHere: File.Exists(file))
                ? (null, first, null)
                : (null, null, null);
        }, ct);

        var tookMs = System.Diagnostics.Stopwatch.GetElapsedTime(asked).TotalMilliseconds;
        if (tookMs >= SlowMs && RPM.Core.Diagnostics.Scope.On)
            RPM.Core.Diagnostics.Scope.Said(
                "art",
                $"slow cover {folder} ({where}, edge {maxEdge}): {tookMs:F0} ms, waiting for a thread "
                + $"{queuedMs:F0}, folder {folderMs:F0}, library {mirrorMs:F0}, tag and cut {tagMs:F0}");

        if (failed is not null)
            return Asked.Failed<byte[]?>(failed);
        if (bytes is { Length: > 0 })
            return Asked.Answer<byte[]?>(bytes);
        if (cloudTrack is null)
            return Asked.Answer<byte[]?>(null);

        // Try the computer that holds the record first, then the cloud. The order is set in one place,
        // RemoteSleeve, so every surface uses the same order.
        return (await RemoteSleeve.ForAsync(cloudTrack, ct: ct))
            .Then(remote => remote is { Length: > 0 } ? Cut(remote) : null);

        byte[] Cut(byte[] raw) => maxEdge > 0 ? ThumbCache.Get(raw, maxEdge) : raw;
    }

    /// <summary>
    /// Whether a record with no picture on this disk may have one somewhere else. Two places can still
    /// answer for a record this machine does not hold: the cloud, for a record that was offloaded or
    /// adopted from the catalogue, and the computer that holds the files, for a record borrowed over the
    /// network.
    ///
    /// <para>Whether the file is here is asked of the disk, not of the row. A file deleted while the app
    /// runs keeps a Synced row until the next library sync marks it cloud-only. Until then the row says
    /// the file is here, but only the cloud has its cover. The album page, the track pane and the art
    /// display ask the same question of the disk.</para>
    /// </summary>
    /// <param name="first">The first track of the record.</param>
    /// <param name="fileHere">Whether that track's file is on this disk now.</param>
    internal static bool SleeveElsewhere(LibraryTrack first, bool fileHere)
        => first.IsOffloaded || first.PeerAdopted || !fileHere;
}
