using RPM.Core.Library;

namespace RPM.Desktop.Services.Cloud;

public sealed partial class CloudSyncService
{
    /// <summary>
    /// Sends what the catalogues said about this library's albums (the release each album is linked to, and what
    /// that release was filed under) when it has changed since the store last took it. The store's list builds take
    /// it in, so a list cut for the phone places each record in the same genre and narrows it by the same styles as
    /// this computer does. Sent whole, one copy per library.
    /// </summary>
    private async Task SyncAlbumCatalogueAsync(LibraryDatabase db, CancellationToken ct)
    {
        var catalogue = db.AlbumCatalogueToSend();
        var fingerprint = catalogue.Fingerprint;
        if (db.GetMeta(LibraryDatabase.AlbumCatalogueSentKey) == fingerprint)
            return;

        try
        {
            // Under this library's device id: the store keeps one copy per library, so two computers with different
            // albums do not overwrite each other's.
            await _cloud.PutAlbumCatalogueAsync(db.PlaysDevice, catalogue, ct);
            db.SetMeta(LibraryDatabase.AlbumCatalogueSentKey, fingerprint);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Sent again on the next pass.
            _log.LogDebug(ex, "[Cloud] Could not send the album catalogue");
        }
    }
}
