using RPM.Core.Models;

namespace RPM.Core.Library;

/// <summary>
/// One row of the import queue, as it is stored in the database (<see cref="LibraryDatabase"/>'s
/// <c>queue</c> temp table). It holds the file's identity, its CURRENT metadata (what the scan read) and
/// its WANTED metadata. WANTED is the editable proposal. Recognition and hand edits make it differ from
/// CURRENT. The row also holds the tick and the album-file split flag. A row can go round the review
/// workbench without loss, because CURRENT and WANTED are each a full <see cref="TrackInfo"/> that share
/// the immutable file facts.
/// </summary>
public sealed record QueueTrack
{
    /// <summary>Store row id (0 for a not-yet-inserted row).</summary>
    public long Id { get; init; }

    /// <summary>The source folder the file was added from. This is the root label of the queue
    /// tree.</summary>
    public required string Source { get; init; }

    /// <summary>The file's folder inside the queue tree (source name plus relative directory). The skeleton
    /// and paged reads group on it, in the same way as the <c>folder</c> column of the library.</summary>
    public required string Folder { get; init; }

    /// <summary>The file's own name. It is the label of the tree leaf, and the sort key for
    /// pages.</summary>
    public required string FileName { get; init; }

    /// <summary>Ticked in the queue. Recognize and Import act on the ticked set.</summary>
    public bool IsChecked { get; init; }

    /// <summary>Bit flags for splitting an album file (0 = not a candidate). They mirror
    /// <see cref="Splitting.AlbumFileHint"/>. With the cue path and the duration, they rebuild the
    /// candidate.</summary>
    public int SplitHint { get; init; }

    public string? SplitCue { get; init; }

    public double SplitDuration { get; init; }

    /// <summary>The file's size on disk. When two encodes of the same track have the same codec and
    /// bitrate, the size decides which one is better.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Whether this track is already somewhere else, in the library or elsewhere in this queue.
    /// The store computes it over the whole queue (see
    /// <see cref="LibraryDatabase.QueueRefreshDuplicates"/>). The caller never sets it.</summary>
    public QueueDuplicate Duplicate { get; init; }

    /// <summary>The album intent this row belongs to (0 = none). It holds what was said about the record
    /// this track is on, until the folder of that record is written. See
    /// <see cref="QueueAlbumIntent"/>.</summary>
    public long AlbumIntentId { get; init; }

    /// <summary>What the scan found on disk (immutable).</summary>
    public required TrackInfo Current { get; init; }

    /// <summary>The editable proposal. It starts equal to Current, and changes when recognition or a hand
    /// edit alters it.</summary>
    public required TrackInfo Wanted { get; init; }

    public string Path => Current.FilePath;
}

/// <summary>
/// Whether a queued file is a copy of a track that already exists, and if so, how its quality compares.
/// This is one value, not a set. The most serious case wins, because that is the one the user must act on.
/// A file that the library already holds counts as a duplicate, however good the other queued copies are.
/// </summary>
public enum QueueDuplicate
{
    /// <summary>Nothing else holds this track.</summary>
    None = 0,

    /// <summary>The library already holds this track, in a copy at least as good as this one.</summary>
    InLibrary = 1,

    /// <summary>The library holds this track, and this file is the better encode. Importing it replaces the
    /// worse copy instead of duplicating a good one.</summary>
    Upgrade = 2,

    /// <summary>Another file in this same queue holds the track in equal or better quality.</summary>
    InQueue = 3,

    /// <summary>
    /// The collection lists this track, but only as a cloud record: there is no file for it on this
    /// computer.
    ///
    /// <para><b>Not a duplicate, which is why it is its own state.</b> Importing it is the one thing that
    /// gives this machine the actual file. So it must not be swept by "Remove duplicates", and it must not
    /// be hidden by the duplicates filter. Both of those match only <see cref="InQueue"/> and
    /// <see cref="InLibrary"/>, so this state is not included.</para>
    /// </summary>
    InCloud = 4,
}

/// <summary>
/// One album group of the import tree, as the store returns it for the skeleton. It gives the artist, the
/// album, the track count, and the number of ticked tracks. The tick state is sent with the group. The
/// alternative, where each group row asks for its own state, would cost one query per visible row.
/// </summary>
/// <param name="Artist">The artist key; empty is the Unknown Artist bucket.</param>
/// <param name="Album">The album key; empty is the Unknown Album bucket.</param>
/// <param name="Tickable">How many rows under the group can be ticked. This is every row except an album
/// file that a split kept whole. That file is listed under the record, but it can never be filed. The
/// group's tick box shows the fraction of Tickable that is ticked, not the fraction of
/// <paramref name="TrackCount"/>. Otherwise a record whose album file is still present could never look
/// fully ticked.</param>
/// <param name="DiscCount">The highest disc number under the group, or 0 when no row has a disc number. A
/// value above one means the record is a multi-disc set, and the queue must show that. Otherwise the tree
/// lists track 1 twice with nothing to tell them apart, and a two-disc album looks like an error.</param>
/// <param name="AlbumIntentId">The album-level answers held for this record (0 = none). See
/// <see cref="QueueAlbumIntent"/>. It is part of the skeleton, so the tree can mark a record that has
/// answers. This is the only way an answer given before import can be seen.</param>
public sealed record QueueGroup(
    string Artist, string Album, int TrackCount, int Checked, int Tickable, int DiscCount = 0,
    long AlbumIntentId = 0);
