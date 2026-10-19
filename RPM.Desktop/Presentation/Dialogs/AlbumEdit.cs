using RPM.Core.Editing;
using RPM.Core.Library;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>
/// Edits a whole record at once: opens the album editor over every track on it, then writes the
/// changes to all of them.
///
/// <para>The write is the same as the mass edit's. Tags are laid over each file's own tags. The file
/// is moved by the naming template when a field that sets its path has changed. The library's mirror
/// row is updated, empty folders are removed, and the player is put back where it was. What is
/// different here is which rows are passed in (the whole record, never a subset), and that a disc's
/// subtitle is written per disc rather than once across the set.</para>
/// </summary>
/// <summary>
/// The two answers the album editor collects that are not written into the files: what kind of record
/// this is, and whether it is levelled with the rest of the collection.
///
/// <para>Each one carries whether it changed, as well as its value. Null is a real answer for the
/// levelling, and it means "let the settings decide". So null cannot stand for "not changed". False is
/// a real answer for the kind of record.</para>
/// </summary>
internal readonly record struct AlbumLibraryFacts(
    bool NormalizeChanged, bool? Normalize,
    bool ClassicalChanged, bool Classical);

internal static class AlbumEdit
{
    /// <summary>Opens the editor over <paramref name="rows"/> and writes what comes back. Returns every
    /// folder the edit touched: the one it left and any it moved into. Returns nothing when the window
    /// was closed without a change.</summary>
    /// <param name="normalize">Whether this record is levelled, as the library currently stores it.
    /// Null means nobody has chosen, so the settings decide.</param>
    /// <param name="classical">Whether this is a classical record, as the library currently stores it.
    /// False, shown as an ordinary record, until someone changes it here.</param>
    /// <param name="onLibraryFacts">Called with the changed values, when either one changed. The window
    /// never writes to the library database. These two values are the only edits that are not written
    /// into the files, so the database write happens here, and the editor does not need database
    /// access.</param>
    public static async Task<IReadOnlyList<string>> EditAsync(
        XamlRoot root, string folder, IReadOnlyList<TrackRow> rows,
        Func<ArtAsk, Task<MassEditLookup?>>? lookup = null,
        bool? normalize = null, bool? classical = null,
        Action<AlbumLibraryFacts>? onLibraryFacts = null)
    {
        if (rows.Count == 0)
            return [];
        if (await AlbumEditDialog.ShowAsync(root, folder, rows, lookup, normalize, classical) is not { } result)
            return [];

        if (result.NormalizeChanged || result.ClassicalChanged)
            onLibraryFacts?.Invoke(new AlbumLibraryFacts(
                result.NormalizeChanged, result.Normalize,
                result.ClassicalChanged, result.Classical));

        var touched = new List<string>();

        // The record's own fields and its pictures, written in one pass over every track.
        if (result.Fields.Count > 0 || result.ArtBytes is not null || result.RemoveArt
            || result.DiscArtBytes is not null || result.RemoveDiscArt)
        {
            touched.AddRange(await MassEdit.WriteToLibraryAsync(rows, new MassEditResult(
                result.Fields, result.ArtBytes, result.ArtMime,
                result.FolderArtBytes, result.DiscArtBytes, result.RemoveDiscArt, result.RemoveArt)));
        }

        // A disc's own values, such as its name and how many tracks it has, go only to that disc's
        // tracks. There is one pass per changed disc, not one pass over everything. Two discs with
        // different names cannot be written in one edit.
        foreach (var (disc, fields) in result.Discs)
        {
            var onDisc = rows.Where(r => r.Disc.Trim() == disc.ToString()).ToList();
            if (onDisc.Count == 0 || fields.Count == 0)
                continue;
            touched.AddRange(await MassEdit.WriteToLibraryAsync(
                onDisc, new MassEditResult(fields, null, null)));
        }

        return [.. touched.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The same editor over a record that is still in the import queue.
    ///
    /// <para>Nothing is written to a file here. The queue's job is to settle what the record will be
    /// before it is filed. So the tag fields are set on the rows, and the import writes them. The two
    /// album-level answers and the disc's printed label are returned as an intent. The store holds that
    /// intent against the group. There is no album folder to write them to yet.</para>
    ///
    /// <para>The cover is set on every row as pending art. This is how a queue row carries a picture
    /// through the move. The disc label has no meaning for one track, so it has to wait for the
    /// folder.</para>
    /// </summary>
    /// <param name="folder">Where the record's files are now, so the editor can show the cover and the
    /// disc label that are already beside them.</param>
    /// <param name="discArtHeld">The disc label already held for this record, when the window is opened
    /// a second time. It has no file on disk to be read back from, so it cannot be kept on a row.</param>
    /// <returns>What to hold against the record, or null when the window was closed without a
    /// change.</returns>
    public static async Task<QueueAlbumIntent?> EditQueueAlbumAsync(
        XamlRoot root, string folder, IReadOnlyList<TrackRow> rows,
        Func<ArtAsk, Task<MassEditLookup?>>? lookup = null,
        bool? normalize = null, bool? classical = null, byte[]? discArtHeld = null)
    {
        if (rows.Count == 0)
            return null;
        if (await AlbumEditDialog.ShowAsync(root, folder, rows, lookup, normalize, classical, discArtHeld)
            is not { } result)
            return null;

        // The record's own fields go onto every track, the same way the mass edit sets them for a
        // selection.
        foreach (var row in rows)
        {
            foreach (var (kind, value) in result.Fields)
                TagFields.Get(kind).Set(row, value);
            if (result.ArtBytes is { Length: > 0 })
                row.SetPendingArt(result.ArtBytes, result.ArtMime);
            else if (result.RemoveArt)
                row.SetPendingArt([], null);
        }

        // A disc's own values go only to that disc's rows.
        foreach (var (disc, fields) in result.Discs)
        {
            foreach (var row in rows.Where(r => r.Disc.Trim() == disc.ToString()))
                foreach (var (kind, value) in fields)
                    TagFields.Get(kind).Set(row, value);
        }

        return new QueueAlbumIntent(
            ClassicalSet: result.ClassicalChanged,
            Classical: result.Classical,
            NormalizeSet: result.NormalizeChanged,
            Normalize: result.Normalize,
            DiscArt: result.DiscArtBytes,
            RemoveDiscArt: result.RemoveDiscArt,
            ReleaseProvider: result.Release?.Provider,
            ReleaseId: result.Release?.Id);
    }
}
