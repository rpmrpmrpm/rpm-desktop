using RPM.Core.Import;
using RPM.Core.Models;
using RPM.Core.Scanning;

namespace RPM.Core.Library;

/// <summary>How far a re-file has got: <paramref name="Done"/> of <paramref name="Total"/> tracks
/// considered, and the library-relative path of the one being handled.</summary>
public readonly record struct RefileProgress(int Done, int Total, string Current);

/// <summary>What a re-file did. <paramref name="AlreadyInPlace"/> counts the files the template put exactly
/// where they already were. This is the common case when only some records are affected.</summary>
public sealed record RefileOutcome(
    int Moved, int AlreadyInPlace, int Refused, IReadOnlyList<string> Refusals)
{
    public static readonly RefileOutcome Empty = new(0, 0, 0, []);
}

/// <summary>Re-files a whole library under a naming template. Every track that the template would put
/// somewhere else is moved there, and the mirror follows it.
///
/// <para>Tags are not changed. A template decides where a file lives and what it is called, not what is
/// written inside it. So a re-file is a move and nothing more, and a file keeps its bytes exactly. The row
/// is moved with <see cref="LibraryDatabase.Move"/>, not removed and added again. Its id is kept, so
/// everything that hangs off it (play history, cloud state, playlist references) is kept too.</para>
///
/// <para>A record's folder is more than its tracks. The cover that the wall paints from, and the row in
/// <c>album_meta</c> that holds a rating and a review, are both keyed by the folder. When every track of
/// one folder lands in one new folder, those follow the tracks. Otherwise a re-file would silently remove
/// every cover and review the user has.</para>
/// </summary>
public static class LibraryRefiler
{
    /// <summary>Moves every track that the template would file elsewhere. Cancelling stops between files.
    /// Files that have already moved stay moved, and the mirror already agrees with them.</summary>
    public static RefileOutcome Refile(
        LibraryDatabase db, string root, NamingTemplate template,
        IProgress<RefileProgress>? progress = null, CancellationToken ct = default)
    {
        var tracks = db.Query();
        if (tracks.Count == 0)
            return RefileOutcome.Empty;

        // Plan keeps input order, so suggestion i belongs to tracks[i]. That pairing is how the old row of
        // a moved file is found again.
        var suggestions = RenamePlanner.Plan(tracks.Select(t => t.ToTrackInfo(root)), template, root);

        int moved = 0, inPlace = 0;
        var refusals = new List<string>();
        // Source folder to the folders its tracks ended up in. One destination means the record moved as a
        // whole, so its cover and review can follow it. More than one means it was split up. Guessing which
        // half owns them would be worse than leaving them where they are.
        var folderMoves = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < suggestions.Count; i++)
        {
            var old = tracks[i];
            var suggestion = suggestions[i];
            // The progress report comes before the file is touched, and the cancellation check comes after
            // it. The bar then shows what is about to happen, and cancelling at that point stops this file
            // from moving.
            progress?.Report(new RefileProgress(i, suggestions.Count, old.RelativePath));
            ct.ThrowIfCancellationRequested();

            if (suggestion.IsAlreadyInPlace)
            {
                inPlace++;
                continue;
            }

            var result = OrganizeApplier.Apply(suggestion, root, OrganizeMode.Move, writeTags: false);
            if (!result.Success)
            {
                // A locked or clashing file keeps its place and its row. Nothing moved, so nothing needs
                // fixing.
                refusals.Add($"{old.RelativePath} → {suggestion.ProposedRelativePath}: {result.Error}");
                continue;
            }

            var relative = Relative(root, result.DestinationPath);
            var slash = relative.LastIndexOf('/');
            var folder = slash < 0 ? "" : relative[..slash];

            db.Move(old.RelativePath, old with
            {
                RelativePath = relative,
                Folder = folder,
                FileName = slash < 0 ? relative : relative[(slash + 1)..],
            });
            // Playlists find a song by its RPM id first and by its path second. Songs with no id yet would
            // lose their playlist entry on a move, so the path is re-pointed as well.
            db.RepointPlaylistPath(old.RelativePath, relative);

            if (!folderMoves.TryGetValue(old.Folder, out var destinations))
                folderMoves[old.Folder] = destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            destinations.Add(folder);
            moved++;
        }

        progress?.Report(new RefileProgress(suggestions.Count, suggestions.Count, ""));

        if (moved > 0)
        {
            FollowFolders(db, root, folderMoves);
            OrganizeApplier.CleanupEmptyFolders(root);
        }

        return new RefileOutcome(moved, inPlace, refusals.Count, refusals);
    }

    /// <summary>Moves a record's non-audio files (the cover, a log, a cue sheet) and its <c>album_meta</c>
    /// row to the folder its tracks moved to, when they all moved to one folder.
    ///
    /// <para>This is public because the bulk re-file is not the only thing that moves a record. Editing an
    /// album's name or artist also re-files it, one window at a time. That path moves only the tracks
    /// unless it calls this method too. Without this step, the rating, review, heart, linked pressing and
    /// RPM id would stay keyed to a folder that no longer holds any music.
    /// <c>MassEdit.WriteToLibraryAsync</c> calls this method.</para>
    /// </summary>
    /// <param name="folderMoves">Old album folder to every folder its tracks landed in. A record whose
    /// tracks scattered across more than one is left alone, because there is no one folder to
    /// follow.</param>
    public static void FollowFolders(
        LibraryDatabase db, string root, Dictionary<string, HashSet<string>> folderMoves)
    {
        foreach (var (source, destinations) in folderMoves)
        {
            if (destinations.Count != 1)
                continue;
            var destination = destinations.First();
            if (destination.Equals(source, StringComparison.OrdinalIgnoreCase))
                continue;

            var sourceDir = FullPath(root, source);
            var destinationDir = FullPath(root, destination);

            // A record whose bytes are all in the cloud has no folder on this disk to move files from. It
            // is still re-filed in the mirror, exactly as a local record is. The mirror half must follow
            // anyway. Otherwise the record's rating, review, heart and pressing stay keyed to a folder that
            // none of its tracks is in.
            if (!Directory.Exists(sourceDir) || !Directory.Exists(destinationDir))
            {
                db.MoveAlbumMeta(source, destination);
                continue;
            }

            string[] leftovers;
            try { leftovers = Directory.GetFiles(sourceDir); }
            catch { continue; }

            // Music still in the folder means the folder is not this record's alone. The folder holds
            // something the library does not know about, or a track that refused to move. Leave everything.
            if (leftovers.Any(f => AudioFileScanner.AudioExtensions.Contains(Path.GetExtension(f))))
                continue;

            foreach (var file in leftovers)
            {
                var target = Path.Combine(destinationDir, Path.GetFileName(file));
                if (File.Exists(target))
                    continue;
                try { File.Move(file, target); }
                catch { /* a locked sidecar is left where it is; the tracks still moved */ }
            }

            db.MoveAlbumMeta(source, destination);
        }
    }

    private static string FullPath(string root, string relativeFolder) =>
        relativeFolder.Length == 0
            ? root
            : Path.Combine(root, relativeFolder.Replace('/', Path.DirectorySeparatorChar));

    private static string Relative(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
}
