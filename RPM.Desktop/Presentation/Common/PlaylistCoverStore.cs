using System.Security.Cryptography;
using System.Text;
using RPM.Desktop.Services.Platform;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// The picture of a playlist, stored on the disk.
///
/// <para><b>Why it is a file and not an in-memory cache.</b> Other code needs this picture and cannot
/// draw it: the phone has no Skia, a borrowed shelf shows a tree that it cannot flatten, and an older
/// build does not know about the picker. The picture is drawn once here and the bytes are handed out,
/// so all of them show what the person chose.</para>
///
/// <para><b>Why not <see cref="Services.Art.ThumbCache"/>.</b> The sweep of that cache deletes
/// anything that has not been used for a long time, because everything in it can be made again. Three
/// of the four kinds of cover here cannot be made again: nothing in the app can rebuild an image that
/// a person picked from their own disk. So this store holds the source images, beside the settings, and
/// every smaller size goes through the thumb cache.</para>
///
/// <para><b>Written by rename.</b> A reader can be the sync pass, the sharing server or a surface that
/// is being created. None of them runs on this thread or takes a lock, and a write in place could give
/// one of them half a JPEG. The thumb cache writes in the same way for the same reason.</para>
/// </summary>
internal static class PlaylistCoverStore
{
    /// <summary>A folder that a test sets, in place of <see cref="LocalData.Dir"/>. It is always null in
    /// the running app. Without it, a test would use the real folder of the machine and, on
    /// <c>Dispose</c>, delete the covers that the person chose. Every test that sets this must be in
    /// the same xunit collection, with the same name, so that two test classes never use it at the
    /// same time.</summary>
    internal static string? RootOverride { get; set; }

    /// <summary>Where the masters are stored. It is beside the settings and not in the library,
    /// because the masters belong to this installation's view of the tree, and a library folder can be
    /// moved between machines.</summary>
    internal static string Root => RootOverride ?? Path.Combine(LocalData.Dir, "playlist-covers");

    /// <summary>
    /// The folder segment that the masters of one tree are stored under. It is present whenever the
    /// question can be answered, so this store never writes at the bare root.
    ///
    /// <para>Every library numbers its playlists from one. <see cref="PlaylistCovers"/> documents this
    /// for a peer's tree and for the listener's own tree sharing a node id. It is also true for a second
    /// local library that is opened later. <see cref="Root"/> is beside the settings, so there is one
    /// per installation and not one per library. Without a segment for the own tree, opening library B
    /// would show the cover chosen in library A for playlist 3 of library B.</para>
    ///
    /// <para>The segment of an own tree is a hash of the root of the open library, not the path
    /// itself. The hash is short, and it does not change when the case or the trailing separator of the
    /// path changes between runs. <see cref="SoftFigure.Seed"/> avoids <see cref="string.GetHashCode()"/>
    /// for the same reason (it is salted for each process): the name must be the same on the next
    /// run.</para>
    /// </summary>
    /// <param name="ownLibraryRoot">The library of the user that is open now
    /// (<see cref="Services.Library.LibraryService.Root"/>), never the mirror path of a borrowed shelf.
    /// The masters of a borrowed tree are named after the peer, not after the place where this machine
    /// keeps a copy of the catalogue.</param>
    /// <param name="viewingPeer">Who is being borrowed from, for <see cref="PlaylistShelf.Borrowed"/>.
    /// Ignored for <see cref="PlaylistShelf.Own"/>.</param>
    internal static string? SegmentOf(PlaylistShelf shelf, string? ownLibraryRoot, string? viewingPeer)
        => shelf is PlaylistShelf.Own
            ? ownLibraryRoot is { Length: > 0 } root ? Path.Combine("own", LibrarySegment(root)) : null
            : viewingPeer is { Length: > 0 } peer && Safe(peer) is { Length: > 0 } safe
                ? Path.Combine("peers", safe)
                : null;

    /// <summary>A library root reduced to a folder name: short, and the same every time this library
    /// is opened. It does not use <see cref="string.GetHashCode()"/>, because that hash is salted for
    /// each process and the same library would get a new folder on every restart (see
    /// <see cref="SoftFigure.Seed"/>). The case and the trailing separator are normalised first,
    /// because a root with different casing in another session is the same library.</summary>
    private static string LibrarySegment(string root)
    {
        var canonical = root.Trim()
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Safe(Convert.ToHexString(hash.AsSpan(0, 8)));
    }

    private static string? FolderFor(string? segment, bool create)
    {
        // A question that cannot be answered means "no picture", never the picture of another tree.
        // This is also the guard for a peer id that is empty after cleaning. Every caller comes
        // through here.
        if (segment is not { Length: > 0 })
            return null;

        var dir = Path.Combine(Root, segment);
        if (!create)
            return dir;
        try
        {
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A peer's id as a folder name. Peer ids are hex already, but the id comes from the
    /// network and is used in a path, so it is cleaned.</summary>
    private static string Safe(string peer)
        => new([.. peer.Where(char.IsLetterOrDigit).Take(64)]);

    /// <summary>
    /// How old a <c>.tmp</c> file left by <see cref="WriteFile"/> must be before <see cref="Sweep"/>
    /// deletes it.
    ///
    /// <para>A normal write (write the temp file, rename it into place) takes well under a second.
    /// Five minutes is much longer than one cover write can take, even on a slow disk. A <c>.tmp</c>
    /// file that is younger may still be a write in progress, and deleting it would lose a cover,
    /// which this store must not do. A <c>.tmp</c> file that is older is an orphan: nothing keeps one
    /// open that long, so a process must have stopped between the write and the rename.</para>
    /// </summary>
    internal static readonly TimeSpan TmpAgeGate = TimeSpan.FromMinutes(5);

    /// <summary>Deletes every <c>.tmp</c> file in a folder that is old enough to be an orphan (see
    /// <see cref="TmpAgeGate"/>). It deletes one file at a time and ignores errors, like every other
    /// delete in <see cref="Sweep"/>. A file whose time cannot be read, or that cannot be deleted, is
    /// left for the next sweep.</summary>
    private static void DeleteStaleTmp(string dir)
    {
        var cutoff = DateTime.UtcNow - TmpAgeGate;
        foreach (var file in Directory.EnumerateFiles(dir, "*.tmp"))
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
            catch { /* best effort */ }
    }

    /// <summary>The master and its wash, or nulls. It never throws. A picture that cannot be read
    /// means that the playlist is drawn automatically, which is the normal case anyway.
    ///
    /// <para>It does not separate "nothing chosen" from "something chosen that cannot be read". Both
    /// give null, because every caller draws in both cases. A caller that acts on null instead of
    /// drawing must use <see cref="ReadForPush"/>, and this method must not change.</para>
    /// </summary>
    internal static (byte[]? Cover, byte[]? Wash) Read(long nodeId, string? segment)
    {
        var dir = FolderFor(segment, create: false);
        if (dir is null)
            return (null, null);
        return (ReadFile(Path.Combine(dir, $"{nodeId}.jpg")),
                ReadFile(Path.Combine(dir, $"{nodeId}-wash.jpg")));
    }

    private static byte[]? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// What was found when the master was requested. It makes the one difference that <see cref="Read"/>
    /// does not make: there is no picture, or there is a picture that could not be read this time.
    ///
    /// <para><b>Why the difference matters here.</b> Every consumer of <see cref="Read"/> draws the
    /// automatic figure on null, so a missing cover and an unreadable cover are the same to them. A
    /// push to the cloud acts on null instead: <c>Absent</c> means delete what the cloud holds,
    /// because the user must be able to revert to the automatic cover. If the push used the single
    /// null of <see cref="Read"/>, a short I/O fault (a permission error or a disk problem that has
    /// nothing to do with the choice of the user) would look like a revert and delete a picture that
    /// the user did not touch. The temp-then-rename writes in <see cref="WriteFile"/> make a write in
    /// progress an unlikely cause, but not an impossible one. A cloud copy that stays one sync pass
    /// after a real revert is a much smaller fault than a cloud copy that disappears after the user
    /// chose it.</para>
    ///
    /// <para>So there are three answers. <c>Present</c> means upload, <c>Absent</c> means delete, and
    /// <c>Unreadable</c> means do neither and try again on the next pass.</para>
    /// </summary>
    internal abstract record MasterRead
    {
        /// <summary>The master exists. These are its bytes, ready to upload.</summary>
        internal sealed record Present(byte[] Cover) : MasterRead;

        /// <summary>The folder was read and there is no file: nothing was chosen, or the master was
        /// removed with <see cref="Forget"/>. A caller that pushes to the cloud makes the cloud match
        /// this.</summary>
        internal sealed record Absent : MasterRead;

        /// <summary>A file exists and could not be read, or the question could not be asked at all.
        /// This is not an answer either way. A caller that pushes to the cloud does nothing and waits
        /// for the next pass.</summary>
        internal sealed record Unreadable : MasterRead;
    }

    /// <summary>The master as a <see cref="MasterRead"/> and not as a nullable pair. See that type for
    /// why a caller that pushes to the cloud needs the three answers that <see cref="Read"/> does not
    /// give.</summary>
    internal static MasterRead ReadForPush(long nodeId, string? segment)
    {
        // With no segment the answer is the same as in Read: no picture. A question that cannot be
        // answered (no library open, a peer id that cannot be used) is not a fault to retry.
        if (FolderFor(segment, create: false) is not { } dir)
            return new MasterRead.Absent();

        var path = Path.Combine(dir, $"{nodeId}.jpg");

        // File.Exists hides its own I/O errors and returns false when it cannot check. So a file that
        // is not there and a folder that cannot be listed both reach this point, and both are
        // correctly Absent.
        if (!File.Exists(path))
            return new MasterRead.Absent();

        try
        {
            var bytes = File.ReadAllBytes(path);

            // Zero bytes is not Absent. Absent means there is no file, and File.Exists found one. Write
            // never makes an empty file (it checks cover.Length == 0 before it opens a file), so this
            // state has an outside cause: a crash between allocation and flush, a quarantine or a
            // change by another program. That is what Unreadable is for. It is not a second way to
            // say that nothing was chosen.
            return bytes.Length > 0 ? new MasterRead.Present(bytes) : new MasterRead.Unreadable();
        }
        catch
        {
            // The file existed a moment ago but cannot be read now (a lock, a permission change or a
            // disk fault). This method exists to tell this case apart from "nothing chosen".
            return new MasterRead.Unreadable();
        }
    }

    /// <summary>Stores a master and replaces any master that exists. A null wash keeps any existing
    /// wash. The wash only decorates a page, and a caller that has a cover but no wash is not asking
    /// to remove the wash.</summary>
    internal static void Write(long nodeId, string? segment, byte[] cover, byte[]? wash)
    {
        if (cover.Length == 0 || FolderFor(segment, create: true) is not { } dir)
            return;

        WriteFile(Path.Combine(dir, $"{nodeId}.jpg"), cover);
        if (wash is { Length: > 0 })
            WriteFile(Path.Combine(dir, $"{nodeId}-wash.jpg"), wash);
    }

    private static void WriteFile(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* if the process stops here, the *.tmp pass of Sweep deletes the file later */ }
        }
    }

    /// <summary>The picture under the frame of a mood playlist, when it is the file of the listener.
    /// It is stored without the frame beside the master, so that a rename can frame it again
    /// (<see cref="MoodCovers"/>). Only the mood window reads it, and it is never pushed: the cloud
    /// receives the master. <see cref="Sweep"/> deletes it with the master, using the node id that
    /// starts its name.</summary>
    internal static void WriteUnder(long nodeId, string? segment, byte[] picture)
    {
        if (picture.Length == 0 || FolderFor(segment, create: true) is not { } dir)
            return;
        WriteFile(Path.Combine(dir, $"{nodeId}-under.jpg"), picture);
    }

    /// <summary>What <see cref="WriteUnder"/> kept for <paramref name="nodeId"/>, or null.</summary>
    internal static byte[]? ReadUnder(long nodeId, string? segment)
        => FolderFor(segment, create: false) is { } dir ? ReadFile(Path.Combine(dir, $"{nodeId}-under.jpg")) : null;

    /// <summary>Removes the master of a playlist. This is what "use the automatic cover" means. All
    /// the files are removed, so that a wash does not stay after its square is gone.</summary>
    internal static void Forget(long nodeId, string? segment)
    {
        if (FolderFor(segment, create: false) is not { } dir)
            return;
        foreach (var name in new[] { $"{nodeId}.jpg", $"{nodeId}-wash.jpg", $"{nodeId}-under.jpg" })
            try { File.Delete(Path.Combine(dir, name)); } catch { /* best effort */ }
    }

    /// <summary>
    /// Deletes the masters of playlists of the library that was just opened which no longer exist, and
    /// the masters of peers that are no longer in the book.
    ///
    /// <para>This is a sweep and not a check when a playlist is deleted. The write happens on a worker
    /// and the delete happens on the dispatcher, so avoiding an overlap would need a lock held during
    /// a Skia composite. An orphan is a few kilobytes in the folder of the app, so the cheap solution is
    /// enough.</para>
    ///
    /// <para>Only the tree named by <paramref name="libraryRoot"/> is checked against
    /// <paramref name="living"/>. Other own segments in this store are not checked. A second local
    /// library that was opened earlier is, for this call, the same kind of case as the tree of a peer:
    /// this machine cannot know whether it is still in use, and no registry lists every own library
    /// that was ever opened, so it is left alone. The playlists of a peer get the same treatment: the
    /// tree belongs to the peer, this machine holds a copy of what it was last shown, and a node that
    /// is missing from that copy means the shelf was closed, not that the playlist was deleted.</para>
    /// </summary>
    /// <param name="libraryRoot">The library that was just opened
    /// (<see cref="Services.Library.LibraryService.Root"/>), read at the same moment as
    /// <paramref name="living"/>, so that both describe the same tree.</param>
    internal static void Sweep(
        IReadOnlyCollection<long> living, IReadOnlyCollection<string> peers, string libraryRoot)
    {
        try
        {
            if (libraryRoot is { Length: > 0 }
                && FolderFor(Path.Combine("own", LibrarySegment(libraryRoot)), create: false) is { } ownDir
                && Directory.Exists(ownDir))
            {
                var keep = new HashSet<long>(living);
                foreach (var file in Directory.EnumerateFiles(ownDir, "*.jpg"))
                    if (NodeOf(file) is { } id && !keep.Contains(id))
                        try { File.Delete(file); } catch { /* best effort */ }

                DeleteStaleTmp(ownDir);
            }

            var peersDir = Path.Combine(Root, "peers");
            if (!Directory.Exists(peersDir))
                return;

            // On macOS and Windows, CreateDirectory reuses an existing folder whatever the case of the
            // name. A peer id with different casing in another session is therefore the same folder
            // on disk. An ordinal comparison here would treat that folder as unknown and delete it
            // while the peer is still known.
            var known = new HashSet<string>(peers.Select(Safe), StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directory.EnumerateDirectories(peersDir))
            {
                if (!known.Contains(Path.GetFileName(dir)))
                {
                    try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
                    continue;
                }

                // The list of a known peer is never checked against living (see above). A write that
                // was cut short by a crash is a leftover of this machine, whoever owns the tree, so the
                // folder of a known peer gets the same .tmp clean-up as the own tree.
                DeleteStaleTmp(dir);
            }
        }
        catch
        {
            // If a folder cannot be listed, the sweep does not run. Nothing depends on the sweep. It
            // only keeps the folder small.
        }
    }

    /// <summary>The node that the file name of a master names, or null for any other file, such as a
    /// .tmp file left by a crash or a file that someone put there.</summary>
    private static long? NodeOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.EndsWith("-wash", StringComparison.Ordinal))
            name = name[..^"-wash".Length];
        else if (name.EndsWith("-under", StringComparison.Ordinal))
            name = name[..^"-under".Length];
        return long.TryParse(name, out var id) ? id : null;
    }
}
