using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Desktop.Services.Cloud;

/// <summary>
/// The tracks that are in the cloud and not on this disk, and bringing them back down.
///
/// <para>
/// rpm never removes a file by itself. A track ends up cloud-only in one of two ways. Either the user
/// deleted the file to make room, in which case <see cref="LibrarySynchronizer"/> keeps the row and marks
/// it. The recording is still in the store, and the row is the only thing that knows about it. Or another
/// of the user's machines uploaded the track, and this one has never had it.
/// </para>
///
/// <para>
/// Either way the music is still in the library, still on the wall, and still playable. This is what puts
/// the files back: a whole album from its own menu, one song from its right-click menu, or everything this
/// machine gave up, from the cloud settings page.
/// </para>
/// </summary>
public sealed partial class CloudSyncService
{
    /// <summary>The tracks this machine had and no longer has. These are the ones it can offer to bring
    /// back.</summary>
    public TrackTally OffloadedHere() =>
        _library.IsOpen ? _library.Db.OffloadedHereTally() : TrackTally.Nothing;

    /// <summary>What the library shows that has never been on this disk: another install's records, adopted
    /// from the catalogue. These are not this machine's to bring back, and they are not what it
    /// freed.</summary>
    public TrackTally FromElsewhere() =>
        _library.IsOpen ? _library.Db.FromElsewhereTally() : TrackTally.Nothing;

    /// <summary>True while a bring-back is running, so the settings page can show it and does not start a
    /// second one.</summary>
    public bool RestoreRunning { get; private set; }

    /// <summary>
    /// Brings back every track that <em>this</em> machine had, and nothing else.
    ///
    /// <para>
    /// Once a user has two computers, the cloud-only rows in a library are two different things. Some are
    /// files this install no longer has. Others are files it never had, which the catalogue put on the
    /// wall. Downloading the second set would make this laptop quietly take the desktop's entire
    /// collection. Only the first set comes back. See <see cref="LibraryDatabase.QueryOffloadedHere"/>.
    /// </para>
    /// </summary>
    public async Task<(int Restored, int Failed)> RestoreOffloadedHereAsync(CancellationToken ct = default)
    {
        if (!_library.IsOpen || !_auth.IsLoggedIn) return (0, 0);
        if (RestoreRunning) return (0, 0);

        var db = _library.Db;
        var wanted = db.QueryOffloadedHere()
            .Where(t => RPM.Core.Metadata.RpmTag.IsId(t.RpmId))
            .ToList();

        if (wanted.Count == 0) return (0, 0);

        RestoreRunning = true;
        Changed?.Invoke();

        int restored, failed;
        try
        {
            // This uses the same entry point as a record's menu: one batch, several files at a time. One
            // track that will not come down does not stop the other nine hundred. It stays cloud-only,
            // which is where it already was, and it is offered again next time.
            (restored, failed) = await RestoreAsync(wanted, ct);
        }
        finally
        {
            RestoreRunning = false;
            Changed?.Invoke();
        }

        _status = _status with
        {
            LastError = failed == 0
                ? null
                : new CloudTrouble.BringBackFailed(failed, wanted.Count),
        };

        Changed?.Invoke();
        return (restored, failed);
    }

    /// <summary>
    /// The store's answer for a page of tracks. An empty answer, from an old API or a machine that is
    /// offline, vouches for nothing.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, CloudVerification>> VerifyAsync(
        IReadOnlyList<LibraryTrack> tracks, CancellationToken ct)
    {
        try
        {
            return await _cloud.VerifyAsync(tracks.Select(t => t.RpmId!).ToList(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "[Cloud] Could not have the store vouch for these tracks");
            return new Dictionary<string, CloudVerification>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Whether the store has just said that it can give back this exact track. It needs a row, a blob whose
    /// stored checksum matches the hash it is filed under, and a sidecar.
    ///
    /// <para>The hash is checked here too. If the store has filed a different hash under this id, the two
    /// disagree about which recording this is. The file on this disk is the one the user can actually
    /// hear.</para>
    /// </summary>
    private bool Vouched(IReadOnlyDictionary<string, CloudVerification> verdicts, LibraryTrack track)
    {
        verdicts.TryGetValue(track.RpmId!, out var verdict);

        if (CloudVouch.Vouches(verdict, track.ContentHash))
            return true;

        _log.LogWarning(
            "[Cloud] The store will not vouch for {Path} — it said {Reason}",
            track.RelativePath,
            verdict is not { Ok: true } ? verdict?.Reason ?? "nothing"
                                        : "it holds a different recording under this id");
        return false;
    }
}
