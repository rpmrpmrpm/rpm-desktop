using Microsoft.Data.Sqlite;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// A song's star rating, 0 to 10 (half stars). For now it is written only by an import from another
/// music app, sent to the cloud by its own pass, and taken from the cloud when nothing here is unsent.
/// </summary>
public class TrackRatingTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public TrackRatingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmrating-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private LibraryTrack Add(string name, string? rpmId = null)
    {
        _db.Upsert(new LibraryTrack
        {
            RelativePath = $"a/{name}.mp3",
            Folder = "a",
            FileName = $"{name}.mp3",
            Extension = ".mp3",
            SizeBytes = 1000,
            ModifiedUtcTicks = 1,
            Title = name,
            RpmId = rpmId ?? Guid.NewGuid().ToString("D"),
        });
        return _db.FindByRelativePath($"a/{name}.mp3")!;
    }

    private void Raw(string sql)
    {
        using var raw = new SqliteConnection($"Data Source={MirrorLocation.InFolder(_root).DatabasePath};Pooling=False");
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private int RatingOf(string name) => _db.FindByRelativePath($"a/{name}.mp3")!.Rating;

    [Fact]
    public void Rating_columns_are_added_on_open_without_a_version_step()
    {
        var path = MirrorLocation.InFolder(_root).DatabasePath;
        _db.Dispose();
        long before;
        using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "ALTER TABLE tracks DROP COLUMN rating; ALTER TABLE tracks DROP COLUMN rating_sent; PRAGMA user_version;";
            before = Convert.ToInt64(cmd.ExecuteScalar());
        }

        using var reopened = new LibraryDatabase(MirrorLocation.InFolder(_root));

        using var check = new SqliteConnection($"Data Source={path};Pooling=False");
        check.Open();
        using var columns = check.CreateCommand();
        columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('tracks') WHERE name IN ('rating', 'rating_sent')";
        Assert.Equal(2L, Convert.ToInt64(columns.ExecuteScalar()));
        using var version = check.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        Assert.Equal(before, Convert.ToInt64(version.ExecuteScalar()));
    }

    [Fact]
    public void Rating_is_read_into_the_track_and_kept_by_a_rescan()
    {
        var track = Add("one");
        Assert.True(_db.SetImportedRating(track.Id, 7));

        _db.Upsert(track with { Title = "One, retagged" });

        Assert.Equal(7, RatingOf("one"));
    }

    [Fact]
    public void Imported_rating_does_not_replace_an_existing_one()
    {
        var track = Add("one");
        Assert.True(_db.SetImportedRating(track.Id, 7));

        Assert.False(_db.SetImportedRating(track.Id, 3));

        Assert.Equal(7, RatingOf("one"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public void Imported_rating_must_be_0_to_10(int rating) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => _db.SetImportedRating(Add("one").Id, rating));

    [Fact]
    public void Ratings_to_send_skip_tracks_not_in_the_cloud()
    {
        var local = Add("local");
        var synced = Add("synced");
        _db.SeedSynced(synced.Id, "hash", 1000, 5);
        _db.SetImportedRating(local.Id, 4);
        _db.SetImportedRating(synced.Id, 8);

        var due = _db.RatingsToSend(200);

        Assert.Equal([(synced.Id, synced.RpmId!, 8)], due);
    }

    [Fact]
    public void A_rating_changed_after_sending_stays_unsent()
    {
        var track = Add("one");
        _db.SeedSynced(track.Id, "hash", 1000, 5);
        _db.SetImportedRating(track.Id, 8);
        var due = _db.RatingsToSend(200);

        // A later change, as the rating editor will make one: the row says 6 and is unsent again.
        Raw($"UPDATE tracks SET rating = 6, rating_sent = 0 WHERE id = {track.Id}");
        _db.NoteRatingsSent([.. due.Select(d => (d.Id, d.Rating))]);

        Assert.Equal([(track.Id, track.RpmId!, 6)], _db.RatingsToSend(200));
    }

    [Fact]
    public void Sent_ratings_are_not_sent_again()
    {
        var track = Add("one");
        _db.SeedSynced(track.Id, "hash", 1000, 5);
        _db.SetImportedRating(track.Id, 8);

        _db.NoteRatingsSent([.. _db.RatingsToSend(200).Select(d => (d.Id, d.Rating))]);

        Assert.Empty(_db.RatingsToSend(200));
    }

    [Fact]
    public void Inbound_rating_waits_for_unsent_local_rating()
    {
        var track = Add("one");
        _db.SetImportedRating(track.Id, 8);

        Assert.Equal(0, _db.AcceptTrackRatings([(track.RpmId!, 2)]));

        Assert.Equal(8, RatingOf("one"));
    }

    [Fact]
    public void Inbound_rating_is_taken_when_nothing_is_unsent()
    {
        var track = Add("one");

        Assert.Equal(1, _db.AcceptTrackRatings([(track.RpmId!, 9), (Guid.NewGuid().ToString("D"), 3)]));

        Assert.Equal(9, RatingOf("one"));
        Assert.Empty(_db.RatingsToSend(200));
    }
}
