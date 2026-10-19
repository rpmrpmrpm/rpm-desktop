using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>Setting rows up in a cloud state for a test.</summary>
internal static class CloudTestRows
{
    /// <summary>
    /// Puts a row where a finished upload leaves it. <see cref="LibraryDatabase.MarkSynced"/> only
    /// writes over a row that is Uploading, as the upload's own row is, so the row is put there first.
    /// </summary>
    public static void SeedSynced(this LibraryDatabase db, long trackId, string contentHash, long canonicalSizeBytes, long syncedUtcTicks)
    {
        db.SetCloudState(trackId, CloudState.Uploading);
        db.MarkSynced(trackId, contentHash, canonicalSizeBytes, syncedUtcTicks);
    }
}
