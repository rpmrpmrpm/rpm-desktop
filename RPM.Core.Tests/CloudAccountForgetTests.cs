using Microsoft.Data.Sqlite;
using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The account this library synced with was deleted. Every mark the library keeps about it goes, so
/// the library reads as one that has never been in the cloud, and the music, the edits, the counts and
/// the playlists stay.
/// </summary>
public class CloudAccountForgetTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public CloudAccountForgetTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mdforget-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private LibraryTrack Add(string name)
    {
        _db.Upsert(new LibraryTrack
        {
            RelativePath = $"Band/Record/{name}.flac", Folder = "Band/Record", FileName = $"{name}.flac",
            Extension = ".flac", SizeBytes = 5_000_000, ModifiedUtcTicks = 1,
        });
        return _db.Query(name)[0];
    }

    private SqliteConnection Raw()
    {
        var raw = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = MirrorLocation.InFolder(_root).DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString());
        raw.Open();
        return raw;
    }

    private long Scalar(string sql)
    {
        using var raw = Raw();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private void Exec(string sql)
    {
        using var raw = Raw();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Tracks_on_this_disk_go_back_to_never_uploaded_and_the_cloud_only_ones_go()
    {
        var synced = Add("synced");
        _db.SeedSynced(synced.Id, "hash", 4_000_000, 7);
        var queued = Add("queued");
        _db.SetCloudState(queued.Id, CloudState.Queued);
        var failed = Add("failed");
        _db.SetCloudState(failed.Id, CloudState.Error, "no room");
        var offloaded = Add("offloaded");
        _db.SetCloudState(offloaded.Id, CloudState.CloudOnly);
        var local = Add("local");
        Exec($"UPDATE tracks SET cloud_adopted = 1, cloud_state = {(int)CloudState.CloudOnly} WHERE id = {Add("adopted").Id}");

        var forgotten = _db.ForgetCloudAccount();

        Assert.Equal(3, forgotten.TracksReset);
        Assert.Equal(2, forgotten.TracksDropped);
        foreach (var name in new[] { "synced", "queued", "failed", "local" })
        {
            var row = _db.Query(name)[0];
            Assert.Equal(CloudState.Local, row.CloudState);
            Assert.Null(row.ContentHash);
            Assert.False(row.CloudSelected);
        }
        Assert.Equal(0, Scalar($"SELECT cloud_synced_utc_ticks + canonical_size_bytes FROM tracks WHERE id = {synced.Id}"));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM tracks WHERE cloud_error IS NOT NULL"));
        Assert.Empty(_db.Query("offloaded"));
        Assert.Empty(_db.Query("adopted"));
        Assert.Equal(local.Id, _db.Query("local")[0].Id);
    }

    /// <summary>A reset row is not marked as in the cloud by an upload that answers after it.</summary>
    [Fact]
    public void An_upload_landing_after_the_reset_does_not_mark_the_track_uploaded()
    {
        var track = Add("late");
        _db.SetCloudState(track.Id, CloudState.Uploading);

        _db.ForgetCloudAccount();
        var written = _db.MarkSynced(track.Id, "hash", 4_000_000, 7);

        Assert.False(written);
        Assert.Equal(CloudState.Local, _db.Query("late")[0].CloudState);
    }

    [Fact]
    public void An_upload_still_in_flight_is_marked_as_before()
    {
        var track = Add("normal");
        _db.SetCloudState(track.Id, CloudState.Uploading);

        Assert.True(_db.MarkSynced(track.Id, "hash", 4_000_000, 7));
        Assert.Equal(CloudState.Synced, _db.Query("normal")[0].CloudState);
    }

    [Fact]
    public void Playlists_stay_without_what_was_published_and_the_accounts_own_go()
    {
        var mine = _db.CreatePlaylist(null, "Mine");
        var adopted = _db.CreatePlaylist(null, "From the phone");
        Exec($"UPDATE pl_node SET cloud_id = 'c1', cloud_selected = 1, cloud_synced_utc_ticks = 5, cloud_updated_at = 6, cloud_sent = '[1]', cloud_sent_meta = 'm' WHERE id = {mine}");
        Exec($"UPDATE pl_node SET cloud_adopted = 1 WHERE id = {adopted}");
        Exec("INSERT INTO pl_cloud_gone (cloud_id) VALUES ('c-old')");

        _db.ForgetCloudAccount();

        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM pl_node WHERE id = {mine} AND cloud_selected = 0 AND cloud_synced_utc_ticks = 0 AND cloud_updated_at = 0 AND cloud_sent IS NULL AND cloud_sent_meta IS NULL"));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM pl_node WHERE id = {adopted}"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM pl_cloud_gone"));
    }

    [Fact]
    public void Favourites_and_plays_stay_and_what_was_sent_of_them_is_forgotten()
    {
        _db.SetAlbumFavorite("Band/Record", true);
        Exec("UPDATE album_meta SET favorite_synced_utc_ticks = 9, favorite_cloud_updated_at = 9");
        Exec("INSERT INTO track_plays (rpm_id, device, plays, last_played_utc_ticks, sent_plays) VALUES ('r1', 'desk', 12, 1, 12)");
        Exec($"INSERT OR REPLACE INTO library_meta (key, value) VALUES ('{LibraryDatabase.PlaysCursorKey}', '99')");
        Exec("INSERT INTO cloud_activity (op, title, outcome, at_utc_ticks) VALUES (0, 'One', 0, 1)");

        _db.ForgetCloudAccount();

        Assert.Equal(1, Scalar("SELECT is_favorite FROM album_meta WHERE folder = 'Band/Record'"));
        Assert.Equal(0, Scalar("SELECT favorite_synced_utc_ticks + favorite_cloud_updated_at FROM album_meta WHERE folder = 'Band/Record'"));
        Assert.Equal(12, Scalar("SELECT plays FROM track_plays WHERE rpm_id = 'r1'"));
        Assert.Equal(0, Scalar("SELECT sent_plays FROM track_plays WHERE rpm_id = 'r1'"));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM library_meta WHERE key = '{LibraryDatabase.PlaysCursorKey}'"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM cloud_activity"));
    }
}
