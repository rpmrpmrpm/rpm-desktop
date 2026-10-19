using RPM.Core.Cloud;

namespace RPM.Core.Library;

/// <summary>What <see cref="LibraryDatabase.ForgetCloudAccount"/> did.</summary>
/// <param name="TracksReset">Tracks that are on this disk and were in the cloud, queued for it, or
/// marked in some other way as the account's. They are plain local tracks now.</param>
/// <param name="TracksDropped">Tracks whose only copy was in the cloud: offloaded here and not
/// brought back, or adopted from the account's catalogue. Their music went with the account, so the
/// rows were removed too.</param>
public sealed record CloudAccountForgotten(int TracksReset, int TracksDropped);

public sealed partial class LibraryDatabase
{
    /// <summary>
    /// The account this library was syncing with has been deleted, and everything it held in the
    /// cloud is going. Every mark this library keeps about that account is removed in one write, so
    /// the library reads as one that has never been in the cloud.
    /// </summary>
    /// <remarks>
    /// <para><b>Why it is needed.</b> A row left saying Synced would never be uploaded again, by any
    /// account: the queue only promotes Local rows (<see cref="QueueForUpload"/>). A playlist left with
    /// its last-published copy would never be published again, and favourites and plays already marked
    /// as sent would never be sent. Signing in with a new account later starts from nothing either way.
    /// This method makes the library agree.</para>
    ///
    /// <para><b>What stays.</b> The music on this disk, every edit to it, its <c>rpm_id</c> (also
    /// written in the file's own tags, and only an identity), the play counts and favourites
    /// themselves, and the playlists themselves. Only the bookkeeping of what was sent, and to which
    /// cloud copy, is removed.</para>
    ///
    /// <para><b>Call it after the upload run has stopped</b> (<c>CloudSyncService</c>), so that nothing
    /// it resets is being written while it runs. As a second safeguard, <see cref="MarkSynced"/> only
    /// applies while the row is still Uploading, so a late upload cannot mark a reset row as in the
    /// cloud.</para>
    /// </remarks>
    public CloudAccountForgotten ForgetCloudAccount()
    {
        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();

            int Run(string sql)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                return cmd.ExecuteNonQuery();
            }

            // No file on this disk, and the cloud copy is being erased: offloaded here and never
            // brought back, or adopted from the account's catalogue. Sign-out drops the adopted rows too
            // (ForgetAdopted), but this method does not rely on sign-out having run first.
            var dropped = Run(
                $"DELETE FROM tracks WHERE cloud_state = {(int)CloudState.CloudOnly} OR cloud_adopted = 1");

            var reset = Run(
                $"""
                UPDATE tracks SET
                    cloud_state = {(int)CloudState.Local}, cloud_selected = 0, cloud_pinned = 0,
                    cloud_error = NULL, cloud_partial_error = NULL,
                    cloud_attempts = 0, cloud_next_attempt_utc_ticks = 0,
                    content_hash = NULL, canonical_size_bytes = 0,
                    cloud_synced_utc_ticks = 0, cloud_updated_at = 0, renditions_utc_ticks = 0
                WHERE cloud_state <> {(int)CloudState.Local} OR cloud_selected <> 0 OR cloud_pinned <> 0
                   OR content_hash IS NOT NULL OR cloud_synced_utc_ticks <> 0 OR cloud_updated_at <> 0
                   OR renditions_utc_ticks <> 0 OR cloud_error IS NOT NULL OR cloud_partial_error IS NOT NULL
                """);

            // Playlists that came down from the account and were never this machine's are removed, as at
            // sign-out (ForgetAdoptedPlaylists), and for the same reason as the adopted tracks.
            Run("DELETE FROM pl_item WHERE playlist_id IN (SELECT id FROM pl_node WHERE cloud_adopted = 1)");
            Run("DELETE FROM pl_node WHERE cloud_adopted = 1");

            // This machine's playlists stay, but what was published, and when, does not. The cloud id
            // stays too: it is only an identity, and a new account that is sent the playlist creates its
            // own copy under it.
            Run(
                """
                UPDATE pl_node SET
                    cloud_selected = 0, cloud_synced_utc_ticks = 0, cloud_updated_at = 0,
                    cloud_sent = NULL, cloud_sent_meta = NULL
                """);
            Run("DELETE FROM pl_cloud_gone");

            Run("UPDATE album_meta SET favorite_synced_utc_ticks = 0, favorite_cloud_updated_at = 0");
            Run("UPDATE album_meta SET classical_synced_utc_ticks = 0, classical_cloud_updated_at = 0");

            // The counts stay, merged as they are: they are this library's listening history. What was
            // sent, and where the listing was read up to, belong to the deleted account.
            Run("UPDATE track_plays SET sent_plays = 0");
            Run($"DELETE FROM library_meta WHERE key = '{PlaysCursorKey}'");

            Run("DELETE FROM cloud_activity");

            tx.Commit();
            return new CloudAccountForgotten(reset, dropped);
        }
    }
}
