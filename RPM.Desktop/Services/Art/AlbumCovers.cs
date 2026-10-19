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
    public static async Task<byte[]?> LoadForSquareAsync(
        string folder, CancellationToken ct, int edge, bool ownCollection = false)
    {
        if (await LoadAsync(folder, ct, edge, ownCollection) is not { Length: > 0 } bytes)
            return null;

        var (w, h) = ArtScaler.BoundsOf(bytes);
        if (Math.Min(w, h) >= edge || Math.Max(w, h) <= 0)
            return bytes;

        var wider = ArtScaler.LongEdgeForShortSide(w, h, edge);
        return wider > edge && await LoadAsync(folder, ct, wider, ownCollection) is { Length: > 0 } fuller
            ? fuller
            : bytes;
    }

    /// <summary>
    /// The cover for one album folder, or null if none of the four sources can answer.
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
    public static async Task<byte[]?> LoadAsync(
        string folder, CancellationToken ct, int maxEdge = 0, bool ownCollection = false)
    {
        var library = App.Services.GetRequiredService<LibraryService>();
        if (!library.IsOpen)
            return null;

        var mirror = ownCollection ? library.Db : library.Shelf;
        if ((ownCollection ? library.Root : library.ShelfRoot) is not { } root)
            return null;

        var (bytes, cloudTrack) = await Task.Run<(byte[]? Bytes, LibraryTrack? Track)>(() =>
        {
            var directory = folder.Length == 0
                ? root
                : Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));

            try
            {
                if (FolderArtWriter.FindFolderImage(directory) is { } image)
                    return (maxEdge > 0 ? ThumbCache.GetFile(image, maxEdge) : File.ReadAllBytes(image),
                            (LibraryTrack?)null);
            }
            catch
            {
                return (null, null);   // Optional source. A locked or half-written file gives a blank card.
            }

            // The mirror lookup is required. If it cannot answer, the card is blank. If it is refused,
            // LibraryService.Read shows the problem screen over the app.
            if (!library.Read(mirror, "a record's first track for its cover",
                        m => m.QueryFolderPage(folder, 0, 1).FirstOrDefault())
                    .Answers(out var first, out _)
                || first is null)
                return (null, null);

            try
            {
                if (TagReader.LoadArt(first.ToTrackInfo(root).FilePath)?.Bytes is { Length: > 0 } embedded)
                    return (Cut(embedded), null);
            }
            catch
            {
                return (null, null);   // Optional source. A locked or half-written file gives a blank card.
            }

            // No picture on this disk. Two places can still answer for a record this machine does not
            // hold: the cloud, for a record that was offloaded or adopted from the catalogue, and the
            // computer that holds the files, for a record borrowed over the network.
            return first.IsOffloaded || first.PeerAdopted ? (null, first) : (null, null);
        }, ct);

        if (bytes is { Length: > 0 })
            return bytes;
        if (cloudTrack is null)
            return null;

        // Try the computer that holds the record first, then the cloud. The order is set in one place,
        // RemoteSleeve, so every surface uses the same order.
        return await RemoteSleeve.ForAsync(cloudTrack, ct: ct) is { Length: > 0 } remote
            ? Cut(remote)
            : null;

        byte[] Cut(byte[] raw) => maxEdge > 0 ? ThumbCache.Get(raw, maxEdge) : raw;
    }
}
