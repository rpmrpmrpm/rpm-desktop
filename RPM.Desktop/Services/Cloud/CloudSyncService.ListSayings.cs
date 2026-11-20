using RPM.Core.Library;

namespace RPM.Desktop.Services.Cloud;

public sealed partial class CloudSyncService
{
    /// <summary>
    /// Sends what the listener said about the generated lists here (verdicts on songs, refusals of acts and records)
    /// when it has changed since the store last took it. The store's list builds keep to it, so a list cut for the
    /// phone does not offer what was turned down here. Sent whole, one copy per library, and only by a computer: the
    /// other apps have no way to say these things.
    /// </summary>
    private async Task SyncListSayingsAsync(LibraryDatabase db, CancellationToken ct)
    {
        var said = db.ListSayingsToSend();
        var fingerprint = said.Fingerprint;
        if (db.GetMeta(LibraryDatabase.ListSayingsSentKey) == fingerprint)
            return;

        try
        {
            // Under this library's device id: the store keeps one copy per library, so two computers do not
            // overwrite what the other said.
            await _cloud.PutListSayingsAsync(db.PlaysDevice, said, ct);
            db.SetMeta(LibraryDatabase.ListSayingsSentKey, fingerprint);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Sent again on the next pass.
            _log.LogDebug(ex, "[Cloud] Could not send the verdicts and refusals");
        }
    }
}
