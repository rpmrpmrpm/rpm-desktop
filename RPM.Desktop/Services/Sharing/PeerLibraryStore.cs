using RPM.Core.Library;
using RPM.Core.Scanning;
using RPM.Core.Sharing;

namespace RPM.Desktop.Services.Sharing;

/// <summary>
/// Somebody else's collection, kept where it can be looked at without being mixed into this one.
///
/// <para>
/// Each peer has its own mirror, under this app's data folder and not inside anybody's music. This
/// separation is the key design point. A peer's records are not rows of this library. So nothing that
/// walks the collection can reach a row it must never write. That includes sync, import, re-filing,
/// mass edit and the cloud uploader. They cannot reach these rows, because the rows are not in the
/// file those passes open.
/// </para>
///
/// <para>
/// The same pass that adopts another of the user's own computers writes the rows
/// (<see cref="LibraryDatabase.MirrorPeerLibrary"/>). So a borrowed shelf is deduplicated, ordered and
/// drawn by the same code as the real one. The only difference is the database the rows go into.
/// </para>
/// </summary>
public sealed partial class PeerLibraryStore : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LibraryDatabase> _open = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where one peer's mirror lives. A folder of its own, so forgetting a computer is a
    /// folder to delete rather than rows to find.</summary>
    public static string RootFor(string deviceId) =>
        Path.Combine(LocalData.Dir, "peers", deviceId, "library");

    /// <summary>Whether anything has ever been taken from this computer.</summary>
    public static bool Exists(string deviceId) =>
        LibraryDatabase.ExistsUnder(RootFor(deviceId));

    /// <summary>
    /// Whose shelf a file belongs to, read off its path, or null when it is not on one.
    ///
    /// <para>This answers "which collection is this record in?" for a track that is playing, rather than
    /// one on screen. A borrowed record keeps playing after the user has switched back to their own wall.
    /// The player still needs the artist, the album, the cover and the lyrics for it. Those are in a
    /// mirror that nothing is currently showing.</para>
    /// </summary>
    public static string? DeviceIdFor(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        var peers = Path.Combine(LocalData.Dir, "peers");
        var relative = Path.GetRelativePath(peers, path);

        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return null;

        var deviceId = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        return string.IsNullOrEmpty(deviceId) || deviceId == "." ? null : deviceId;
    }

    /// <summary>
    /// The mirror for one peer, opened (and created) on first use and kept for the life of the app, or
    /// null when its file cannot be opened.
    ///
    /// <para>It is kept open rather than opened on each read. The wall asks for it once per tile while
    /// it scrolls. Opening and closing a SQLite file that often would make the library stutter.</para>
    ///
    /// <para><b>Null is an answer that every caller must handle.</b> A shelf written by a newer build of
    /// rpm (after a downgrade) throws <see cref="LibraryTooNewException"/>. A damaged file throws
    /// SQLite's error. This method catches both and returns null, so callers must handle null. The
    /// file is left as it is, because the borrower's own favourites on that shelf are stored in it, and
    /// a newer build can still open it. The failure is logged each time this is asked. It is not
    /// cached, so a file that is repaired or replaced opens on the next ask.</para>
    /// </summary>
    public LibraryDatabase? Opened(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        lock (_gate)
        {
            if (_open.TryGetValue(deviceId, out var known))
                return known;

            if (OpenAt(RootFor(deviceId), deviceId) is not { } db)
                return null;

            _open[deviceId] = db;
            return db;
        }
    }

    /// <summary>Opens the shelf in <paramref name="root"/>, or says on the ring why it will not open
    /// and returns null. Internal so a test can point it at a folder of its own.</summary>
    internal static LibraryDatabase? OpenAt(string root, string deviceId)
    {
        LibraryDatabase? db = null;
        try
        {
            Directory.CreateDirectory(root);

            // In the folder: a borrowed shelf is under this app's data folder, on this computer.
            db = new LibraryDatabase(MirrorLocation.InFolder(root));

            // A borrowed shelf holds borrowed rows and nothing else. Any other row arrived by mistake.
            // An older build let a pass walk "the library" while a borrowed shelf was on screen. That put
            // a row from the user's own collection into a file named after another computer. Such rows
            // are cleared when the shelf is opened.
            db.ForgetLocalRows();
            return db;
        }
        catch (Exception e) when (e is LibraryTooNewException || MirrorFaults.Of(e) is MirrorFault.Broken)
        {
            db?.Dispose();
            RPM.Core.Diagnostics.Faults.Note(
                "sharing", $"borrowed shelf {deviceId[..Math.Min(8, deviceId.Length)]} will not open", e);
            return null;
        }
    }

    /// <summary>
    /// Writes what a peer says it holds into that peer's own mirror.
    ///
    /// <para>Every record that the peer's cloud has a copy of is written as a cloud row. That includes
    /// records the peer has removed from its own disk. These cloud rows stay on the shelf when the peer's
    /// computer is off. The account grant made at pairing covers the whole of the peer's storage. A
    /// narrower reading would hide records that this account may hear.</para>
    /// </summary>
    /// <returns>What changed, so a caller can tell a shelf that moved from one that did not.</returns>
    public PeerMirrorResult Apply(string deviceId, PeerCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (Opened(deviceId) is not { } shelf)
            return default;

        return shelf.MirrorPeerLibrary(
            deviceId,
            PeerLibraryMirror.ToCatalogRows(catalog.Tracks),
            prune: true,
            cloudOnly: PeerLibraryMirror.CloudBackedIds(catalog.Tracks));
    }

    /// <summary>
    /// What that computer's owner has said about their albums, as far as they have said it to us:
    /// which pressing each record is, and whether it is published as works and movements. Merged into
    /// the mirror kept for that computer, never into this collection.
    ///
    /// <para>The favourite mark is the exception, and it is stated when the data arrives rather than
    /// assumed. A guest's listing carries no favourite in either direction. So the mark on one of these
    /// rows belongs to the BORROWER, the person at this machine. It shows what they think of a record on
    /// another computer. If it were kept as part of the row, the owner's next album edit would clear it,
    /// silently and for good. See <see cref="ArrivingAlbumMarks"/>.</para>
    ///
    /// <para>Everything else on the row belongs to the owner only. The album page has no control that
    /// would change it. So the rest of the merge is the owner's own answer arriving. The stamp only ever
    /// moves forwards.</para>
    /// </summary>
    /// <returns>How many rows were taken, so a caller can tell a shelf that changed from one that did
    /// not.</returns>
    public int ApplyAlbums(string deviceId, IReadOnlyList<AlbumMetaEdit> albums)
    {
        ArgumentNullException.ThrowIfNull(albums);

        return albums.Count == 0 || Opened(deviceId) is not { } shelf
            ? 0
            : shelf.ApplyAlbumMetaEdits(albums, ArrivingAlbumMarks.NotTheHeart);
    }

    /// <summary>
    /// That computer's folders and playlists, into the mirror kept for it.
    ///
    /// <para>Replaced whole, because none of it is this machine's: every row in there arrived from
    /// that peer. What it holds belongs to that peer. Nothing on this side can rename, refile or add to
    /// a borrowed list. So there is no local edit that a merge would need to protect. See
    /// <see cref="LibraryDatabase.MirrorPlaylistTree"/>, which leaves an unchanged tree alone rather
    /// than rewriting it under the panel.</para>
    /// </summary>
    /// <param name="tree">The owner's own folders and playlists (<see cref="PeerCatalogBuilder.To"/>)
    /// and their generated lists (<see cref="BorrowedMoodLists.AsTree"/>) together, because the mirror
    /// holds one tree and replaces it whole.</param>
    /// <returns>How many rows were written, and zero for a tree that had not moved.</returns>
    public int ApplyPlaylists(string deviceId, IReadOnlyList<PlaylistTreeRow> tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        return Opened(deviceId) is { } shelf ? shelf.MirrorPlaylistTree(tree) : 0;
    }

    /// <summary>The row for one track in one peer's shelf, or null. What playback asks when the
    /// track being played is not in the collection currently on screen.</summary>
    public LibraryTrack? Find(string deviceId, string rpmId)
    {
        if (!Exists(deviceId))
            return null;

        try
        {
            return Opened(deviceId)?.FindByRpmId(rpmId);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Everything of one computer's, gone: the mirror and the folder around it. What "stop
    /// sharing" does on this side.</summary>
    /// <param name="libraryRoot">The user's own library, so that music which has no business being
    /// in here is carried out before the folder goes. See <see cref="Rescue"/>.</param>
    public void Forget(string deviceId, string? libraryRoot = null)
    {
        lock (_gate)
        {
            if (_open.Remove(deviceId, out var db))
                db.Dispose();
        }

        if (libraryRoot is { Length: > 0 } root)
            Rescue(root);

        try
        {
            Directory.Delete(Path.Combine(LocalData.Dir, "peers", deviceId), recursive: true);
        }
        catch
        {
            // Nothing is there, or a file is still open. Either way this is a cache of another
            // computer's catalogue. Its rows cannot be reached once the peer is removed from the book.
        }
    }

    /// <summary>
    /// Carries any actual music out of the peer folders and into the user's inbox.
    ///
    /// <para>
    /// No music should ever be in these folders. A borrowed shelf holds rows and cached covers. The audio
    /// stays on the other computer's disk. But an older build could point the import destination at a
    /// borrowed shelf. A record imported while a peer's collection was on screen was then filed into this
    /// folder. This folder is deleted, recursively and without a recycle bin, when that peer is forgotten.
    /// So such a file could be the only copy.
    /// </para>
    ///
    /// <para>Run this before any of these folders is deleted, and once at startup. Files are moved to the
    /// inbox, not filed. The app does not know what these files are, and the inbox is where music of
    /// unknown origin goes.</para>
    /// </summary>
    /// <returns>How many files were carried out.</returns>
    public static int Rescue(string libraryRoot) =>
        Rescue(libraryRoot, Path.Combine(LocalData.Dir, "peers"));

    /// <summary>The same, over a given folder of peer mirrors. It is separate so that a test can run it
    /// without reaching into the user's own app data.</summary>
    internal static int Rescue(string libraryRoot, string peers)
    {
        if (!Directory.Exists(peers) || string.IsNullOrEmpty(libraryRoot))
            return 0;

        var rescued = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(peers, "*", SearchOption.AllDirectories))
            {
                if (!AudioFileScanner.AudioExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                    continue;

                try
                {
                    var inbox = LibraryInbox.Ensure(libraryRoot);
                    var target = Path.Combine(inbox, Path.GetFileName(file));

                    // A name already taken is not a reason to leave a file where it will be deleted.
                    for (int n = 2; File.Exists(target); n++)
                        target = Path.Combine(
                            inbox,
                            $"{Path.GetFileNameWithoutExtension(file)} ({n}){Path.GetExtension(file)}");

                    File.Move(file, target);
                    rescued++;
                }
                catch
                {
                    // Locked, or a volume that said no. Left where it is, which is no worse than
                    // before and better than half a move.
                }
            }
        }
        catch
        {
            // The folder went away underneath the walk. Nothing to rescue.
        }

        return rescued;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var db in _open.Values)
                db.Dispose();
            _open.Clear();
        }
    }
}
