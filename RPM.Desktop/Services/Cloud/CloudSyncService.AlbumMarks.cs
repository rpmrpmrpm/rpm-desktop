using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Desktop.Services.Cloud;

/// <summary>
/// The marks the listener has put on whole RECORDS, kept in step between their machines.
///
/// <para>
/// The songs' own hearts travel on the catalogue rows (see <see cref="CloudSyncPlan"/>). A track already
/// has a row there, and one listing brings back the whole library's worth. A record has no such row. An
/// album in the cloud is a grouping of tracks and nothing else. So its mark is stored on its own, under the
/// same album hash that the stored sleeve is filed under.
/// </para>
///
/// <para>
/// Who is right is decided exactly as it is for a track, for the same reason. This machine's edit stamp is
/// compared with its own last agreement. The store's stamp is compared with the store's stamp as this
/// machine last accepted it. No two computers' clocks are ever compared. A record changed on both sides
/// within one interval goes to the machine in front of the user. This is the same tie-break the track rows
/// use.
/// </para>
/// </summary>
public sealed partial class CloudSyncService
{
    /// <summary>The most marks one run may send. A listener marks a handful of records in a sitting. The
    /// rest are sent on the next pass.</summary>
    private const int AlbumMarksPerRun = 50;

    /// <summary>
    /// One pass over the album marks. It takes in what other machines have said, then sends what this
    /// machine has to say.
    ///
    /// <para>Pull first. A record taken in is a record this machine has nothing more to say about. If it
    /// sent first, it would push the older answer up and read it straight back down.</para>
    /// </summary>
    private async Task SyncAlbumMarksAsync(LibraryDatabase db, CancellationToken ct)
    {
        IReadOnlyList<CloudAlbumMark> theirs;
        try
        {
            theirs = await _cloud.ListAlbumMarksAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "[Cloud] Could not read the album marks");
            return;
        }

        // Each album key stands for a folder on THIS machine. The map is built from the mirror's own names.
        // The key is a hash of the album artist and the album, and a folder name is only a sanitised
        // spelling of those two things. So the folder itself cannot be trusted for the key.
        var folderOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var keyOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var album in db.QueryAlbumFacts())
        {
            var key = AlbumArt.KeyFor(album.AlbumArtist, album.Album);
            keyOf[album.Folder] = key;
            // The first folder wins where two folders hold the same record. The mark is about the music,
            // and marking either copy is what the user would expect.
            folderOf.TryAdd(key, album.Folder);
        }

        var known = new Dictionary<string, CloudAlbumMark>(StringComparer.Ordinal);
        foreach (var mark in theirs)
            known[mark.AlbumKey] = mark;

        var states = db.AlbumMarkSyncStates()
            .ToDictionary(s => s.Folder, StringComparer.OrdinalIgnoreCase);

        // ── in ──
        var taken = 0;
        foreach (var mark in theirs)
        {
            if (!folderOf.TryGetValue(mark.AlbumKey, out var folder))
                continue;   // This machine does not have the record, so there is nothing to mark.

            var state = states.TryGetValue(folder, out var standing)
                ? standing
                : new AlbumMarkSync(folder, false, 0, 0, 0);

            // Only where the store has moved on since this machine last accepted it. This machine must also
            // have nothing outstanding of its own. Everything else is this library's word, and the push
            // below sends it.
            if (state.ChangedHere || mark.UpdatedAt <= state.CloudUpdatedAt || mark.Favorite == state.IsFavorite)
                continue;

            db.AcceptAlbumFavorite(folder, mark.Favorite, mark.UpdatedAt);
            taken++;
        }

        // ── out ──
        var sent = 0;
        foreach (var state in states.Values)
        {
            if (ct.IsCancellationRequested || sent >= AlbumMarksPerRun)
                break;
            if (!state.ChangedHere || !keyOf.TryGetValue(state.Folder, out var key))
                continue;

            // The store already holds this value. This machine made a mark that another machine happened to
            // make first. Nothing needs sending. The row simply stops being outstanding.
            if (known.TryGetValue(key, out var mark) && mark.Favorite == state.IsFavorite)
            {
                db.NoteAlbumMarkSent(state.Folder, mark.UpdatedAt);
                continue;
            }

            try
            {
                var stamp = await _cloud.PutAlbumMarkAsync(
                    key, state.IsFavorite, state.EditedUtcTicks, ct);
                db.NoteAlbumMarkSent(state.Folder, stamp);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug(ex, "[Cloud] Could not send the mark on {Folder}", state.Folder);
            }
        }

        var (classicalTaken, classicalSent) = await SyncClassicalMarksAsync(db, theirs, folderOf, keyOf, known, ct);

        if (taken > 0 || sent > 0 || classicalTaken > 0 || classicalSent > 0)
            _log.LogInformation(
                "[Cloud] Album marks: {Taken} taken in, {Sent} sent; classical: {ClassicalTaken} taken in, {ClassicalSent} sent",
                taken, sent, classicalTaken, classicalSent);

        if (taken > 0 || classicalTaken > 0)
            AlbumMarksChanged?.Invoke();
    }

    /// <summary>
    /// The classical mark's half of the pass: the same pull, then push, over the same listing.
    ///
    /// <para>One rule differs from the favourite's. A record that is classical here and has NO answer in
    /// the store is sent whether or not it was changed here. Marks that were made before syncing existed
    /// have no edit stamp. A record whose album key changed (a re-file, or an artist corrected) has no row
    /// under its new key. Without this rule, both kinds would never reach the phone. "Not classical" is
    /// never sent onto an absent answer, because an absent answer already reads as not classical
    /// everywhere.</para>
    /// </summary>
    private async Task<(int Taken, int Sent)> SyncClassicalMarksAsync(
        LibraryDatabase db, IReadOnlyList<CloudAlbumMark> theirs,
        Dictionary<string, string> folderOf, Dictionary<string, string> keyOf,
        Dictionary<string, CloudAlbumMark> known, CancellationToken ct)
    {
        var states = db.AlbumClassicalSyncStates()
            .ToDictionary(s => s.Folder, StringComparer.OrdinalIgnoreCase);

        // ── in ──
        var taken = 0;
        foreach (var mark in theirs)
        {
            // Null means no machine has said anything about this record. That is not an answer to take.
            if (mark.Classical is not { } said || !folderOf.TryGetValue(mark.AlbumKey, out var folder))
                continue;

            var state = states.TryGetValue(folder, out var standing)
                ? standing
                : new AlbumClassicalSync(folder, null, 0, 0, 0);

            if (!AlbumClassicalPlan.Takes(state, mark))
                continue;

            // False means the mark was changed in the editor after the states were read. That edit stands,
            // and it goes out on the next pass.
            if (db.AcceptAlbumClassical(state, said, mark.UpdatedAt))
                taken++;
        }

        // ── out ──
        var sent = 0;
        foreach (var state in states.Values)
        {
            if (ct.IsCancellationRequested || sent >= AlbumMarksPerRun)
                break;
            if (!keyOf.TryGetValue(state.Folder, out var key))
                continue;

            var stored = known.GetValueOrDefault(key);

            switch (AlbumClassicalPlan.Out(state, stored))
            {
                case AlbumClassicalPlan.Outbound.Leave:
                    continue;
                case AlbumClassicalPlan.Outbound.Agreed:
                    db.NoteAlbumClassicalSent(state, stored?.UpdatedAt ?? state.CloudUpdatedAt);
                    continue;
            }

            try
            {
                var stamp = await _cloud.PutAlbumClassicalAsync(key, state.IsClassical, ct);
                db.NoteAlbumClassicalSent(state, stamp);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Stop at the first refusal. Being offline, or a server too old for the route (it answers
                // 404), refuses every record alike, and each refusal is a thrown exception. Under a
                // debugger, a run of thrown exceptions stalls the whole process, so the run stops here. The
                // rest go out on the next pass.
                _log.LogDebug(ex, "[Cloud] Could not send the classical mark on {Folder}", state.Folder);
                break;
            }
        }

        return (taken, sent);
    }

    /// <summary>Raised when a record's favourite or classical mark came in from another machine, so the
    /// shelf on screen can redraw without waiting for anything else to change.</summary>
    public event Action? AlbumMarksChanged;
}
