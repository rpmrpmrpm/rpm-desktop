namespace RPM.Core.Library;

/// <summary>
/// What the user has said about an album while it is still in the import queue. These answers belong
/// to the album, not to any one of its files, so they cannot be stored in a track's tags.
///
/// <para>The import screen prepares files for the library. Some answers are about the whole album:
/// whether it is a classical record, whether it is normalised with the rest of the collection, and
/// what the printed label on the disc looks like. They are stored as <c>album_meta</c> and as folder
/// art. Both are keyed by the album folder, and that folder does not exist until the files are
/// written. So the intent is held against the album's group in the queue tree, and it is applied as
/// soon as the files reach a folder.</para>
///
/// <para>Each answer records whether it was set, as well as its value. The values alone cannot show
/// "not touched", because "not classical" and "let the settings decide" are real answers too. Only
/// the answers that were set are written at import. So when intent joins a record that is already on
/// the shelf, it never overwrites what is already there.</para>
/// </summary>
/// <param name="Id">The store's own id. It is written on every queue row of the album, so the intent
/// follows the rows, not the names. Renaming the album, filling in its album artist, merging two groups
/// or splitting one keeps the intent on the same music.</param>
/// <param name="DiscArt">The label printed on the disc, written to the album folder as disc.png. It is
/// the only picture with no per-track meaning, which is why it is stored at album level.</param>
/// <param name="ReleaseProvider">The catalogue the record was matched against, and
/// <paramref name="ReleaseId"/> the release in it. These are kept so that naming the tracks later does
/// not ask the same question again. Choosing a pressing is the slow part of tagging an album, so asking
/// twice would waste the user's time.</param>
public sealed record QueueAlbumIntent(
    long Id = 0,
    bool ClassicalSet = false,
    bool? Classical = null,
    bool NormalizeSet = false,
    bool? Normalize = null,
    byte[]? DiscArt = null,
    bool RemoveDiscArt = false,
    string? ReleaseProvider = null,
    string? ReleaseId = null)
{
    /// <summary>No answers were given. The store deletes the row instead of keeping an empty one.</summary>
    public bool IsEmpty
        => !ClassicalSet && !NormalizeSet && DiscArt is not { Length: > 0 } && !RemoveDiscArt
           && ReleaseId is not { Length: > 0 };

    /// <summary>This intent with the answers in <paramref name="update"/> applied on top. Only the values
    /// that the update SET replace the current ones. The album editor reports untouched fields as
    /// untouched, so a second visit that changes the disc art keeps the classical answer from the
    /// first.</summary>
    public QueueAlbumIntent MergedWith(QueueAlbumIntent update) => this with
    {
        ClassicalSet = update.ClassicalSet || ClassicalSet,
        Classical = update.ClassicalSet ? update.Classical : Classical,
        NormalizeSet = update.NormalizeSet || NormalizeSet,
        Normalize = update.NormalizeSet ? update.Normalize : Normalize,
        DiscArt = update.DiscArt is { Length: > 0 } ? update.DiscArt
            : update.RemoveDiscArt ? null
            : DiscArt,
        RemoveDiscArt = update.DiscArt is { Length: > 0 } ? false
            : update.RemoveDiscArt || RemoveDiscArt,
        ReleaseProvider = update.ReleaseId is { Length: > 0 } ? update.ReleaseProvider : ReleaseProvider,
        ReleaseId = update.ReleaseId is { Length: > 0 } ? update.ReleaseId : ReleaseId,
    };
}
