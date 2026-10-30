using RPM.Core.Library;

namespace RPM.Core.Import;

/// <summary>
/// Writes what the import queue was told about a RECORD onto the folder its files just landed in.
///
/// <para>Scoped twice over, on purpose. <b>Per folder</b>: an intent is applied to a folder, once, with
/// the folder named by the caller. It is never looked up by name, so it can only ever touch music that
/// is actually there. <b>Per run</b>: the caller passes the folders THIS import wrote to, worked out from
/// the files that actually landed. So an intent can never reach a folder that no file of its record went
/// into. A set filed one disc at a time gets its answers on the first import and again on the second,
/// each time only where that disc went. The writes are idempotent either way.</para>
///
/// <para>Only what was SET is written. An album already on the shelf keeps every answer nobody gave
/// in the queue. Importing a second disc into a record you rated last month does not un-rate it.</para>
/// </summary>
public static class AlbumIntentApplier
{
    /// <summary>
    /// Applies one intent to one library-relative folder. <paramref name="root"/> is the library root,
    /// needed only to reach the folder on disk for the disc picture. The metadata is keyed by the
    /// relative folder, which is how the library keys every album.
    /// </summary>
    public static void Apply(LibraryDatabase db, string root, string relativeFolder, QueueAlbumIntent intent)
    {
        if (intent.ClassicalSet)
            db.SetAlbumClassical(relativeFolder, intent.Classical);
        if (intent.NormalizeSet)
            db.SetAlbumNormalize(relativeFolder, intent.Normalize);
        if (intent.SeriesSet)
            db.SetAlbumSeries(relativeFolder, intent.Series);

        // The intent carries the record the user settled on in the queue, because it is what named the
        // tracks. Store it as the album's own link too. Otherwise an album identified by hand would
        // reach the shelf linked to nothing.
        if (intent.ReleaseId is { Length: > 0 } release)
            db.SetAlbumPressing(relativeFolder, intent.ReleaseProvider, release);

        if (intent.DiscArt is not { Length: > 0 } && !intent.RemoveDiscArt)
            return;

        var folder = Path.Combine(root, relativeFolder.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(folder))
            return;

        if (intent.DiscArt is { Length: > 0 } art)
            FolderArtWriter.ReplaceDisc(folder, art);
        else
            FolderArtWriter.DeleteDisc(folder);
    }

    /// <summary>
    /// The library-relative folders one import run wrote into, per intent. This is the whole of the run
    /// scoping, kept here so the rule lives beside what it protects.
    ///
    /// <para><paramref name="landed"/> is the successful results of THIS run paired with the intent id
    /// of the queue row behind each. Anything with no intent, or that did not land, contributes
    /// nothing.</para>
    /// </summary>
    public static Dictionary<long, List<string>> FoldersPerIntent(
        IEnumerable<(long IntentId, string DestinationPath)> landed, string root)
    {
        var map = new Dictionary<long, List<string>>();
        foreach (var (intentId, destination) in landed)
        {
            if (intentId == 0)
                continue;
            var folder = RelativeFolder(destination, root);
            if (folder is null)
                continue;
            if (!map.TryGetValue(intentId, out var folders))
                map[intentId] = folders = [];
            if (!folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                folders.Add(folder);
        }
        return map;
    }

    /// <summary>A filed track's folder as the library names it: relative to the root, forward slashes,
    /// "" for the root itself. Null when the file did not land under the root at all.</summary>
    public static string? RelativeFolder(string destinationPath, string root)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (dir is null)
            return null;

        var relative = Path.GetRelativePath(Path.GetFullPath(root), dir);
        if (relative == ".")
            return "";
        return relative.StartsWith("..", StringComparison.Ordinal)
            ? null
            : relative.Replace(Path.DirectorySeparatorChar, '/');
    }
}
