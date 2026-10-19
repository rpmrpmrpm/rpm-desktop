using RPM.Core.Library;
using RPM.Core.Models;

namespace RPM.Core.Cloud;

/// <summary>What one correction did, for the caller's log and counters.</summary>
public enum PullOutcome
{
    /// <summary>Taken in: the file, its place and the row all say what the other machine says.</summary>
    Applied,

    /// <summary>The file could not be written. Nothing is stamped, so the same correction is offered
    /// again on the next run and is neither lost nor half applied.</summary>
    TagsRefused,

    /// <summary>The mirror would not take the row. Nothing is stamped.</summary>
    RowRefused,
}

/// <summary>
/// Taking a correction made on another of the user's machines and applying it to this one.
///
/// <para>
/// This is the second direction of sync. The library is the record and the cloud is a copy of it, so
/// an edit made on a second install would reach the store and stop there. Worse, this machine's next
/// catalogue pass would push its own older wording back over it. With two installs that is not a
/// backup arrangement but two libraries that disagree.
/// </para>
///
/// <para>
/// The order is essential and is not an implementation detail. First the file's own tags (the file
/// is the record, and a row corrected without it is reverted by the next sync, which re-reads
/// files). Then the row. Then the stamps, written after the file write, so that the modified time
/// the write produced does not read as an edit made here and get pushed straight back.
/// </para>
///
/// <para>
/// <b>Nothing here moves a file.</b> Where a file sits is decided by this machine's naming
/// template. If a pull re-filed the album to match the cloud's path, the cloud's memory of where a
/// file was would override the template that has since moved it. The re-filer would put the album
/// where the user asked, the pull would drag it back, one row per track, on every run. So the path
/// that a correction carries is not used.
/// </para>
///
/// <para>This lives here and not in the sync service because it writes the user's files from data
/// that came from another machine, which is the last thing in the app to trust blindly.</para>
/// </summary>
public sealed class CloudPullApplier
{
    private readonly LibraryDatabase _db;
    private readonly string _root;

    /// <summary>Writes the corrected tags into the file. Throwing means the file could not be written,
    /// and then the row is left alone.</summary>
    private readonly Action<string, TrackInfo> _writeTags;

    public CloudPullApplier(
        LibraryDatabase db,
        string root,
        Action<string, TrackInfo> writeTags)
    {
        _db = db;
        _root = root;
        _writeTags = writeTags;
    }

    /// <summary>Where a relative path lands on this disk.</summary>
    public string FullPath(string relativePath) =>
        Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>One correction: tags, then the row, then the stamps.</summary>
    public PullOutcome Apply(CloudPull pull)
    {
        var row = pull.Row;
        var updated = CloudSyncPlan.Apply(row, pull.Cloud);
        var path = FullPath(row.RelativePath);

        // A track that is only in the cloud has no file here, so for it the row is everything.
        if (File.Exists(path))
        {
            try
            {
                _writeTags(path, updated.ToTrackInfo(_root));
            }
            catch
            {
                return PullOutcome.TagsRefused;
            }

            // The row's own facts about the FILE stay this machine's: a size and a modified time describe
            // the file on this disk, and the tag write above has just changed the second of them.
            var info = new FileInfo(path);
            if (info.Exists)
                updated = updated with { SizeBytes = info.Length, ModifiedUtcTicks = info.LastWriteTimeUtc.Ticks };
        }

        // The row keeps the path it had, so this is an update in place and not a re-file. Move still
        // does the writing, because it is the one statement that puts a whole track row back.
        if (!_db.Move(row.RelativePath, updated))
            return PullOutcome.RowRefused;

        // The mark that the other machine made is written on its own. A move carries what the FILE says,
        // and a favourite is not something a file says: it belongs to the listener, and it must survive
        // every retag that goes through the same path. It is accepted and not set, so this machine does
        // not push the store's own value back at it.
        _db.AcceptTrackFavorite(updated.RelativePath, updated.IsFavorite, updated.FavoritedUtcTicks);

        _db.NoteCloudInStep(row.Id, pull.Cloud.UpdatedAt, DateTime.UtcNow.Ticks);

        return PullOutcome.Applied;
    }
}
