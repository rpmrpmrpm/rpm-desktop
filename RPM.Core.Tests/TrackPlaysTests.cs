using Microsoft.Data.Sqlite;
using RPM.Core.Library;
using RPM.Core.Models;

namespace RPM.Core.Tests;

/// <summary>
/// The play rows the mirror keeps per device, and what they do to the track's own counter.
///
/// <para><c>tracks.plays</c> is the MERGED total: everything this machine has played plus everything
/// the listener's other devices have reported. That is what makes every existing read of it right
/// without being changed, and these tests are what says so.</para>
/// </summary>
public class TrackPlaysTests : IDisposable
{
    private const string Mine = "this-install";

    private readonly string _root;
    private LibraryDatabase _db;

    public TrackPlaysTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmplays-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private void AddTrack(string relativePath, string? rpmId)
    {
        var slash = relativePath.LastIndexOf('/');
        _db.Upsert(new LibraryTrack
        {
            RelativePath = relativePath,
            Folder = slash < 0 ? "" : relativePath[..slash],
            FileName = slash < 0 ? relativePath : relativePath[(slash + 1)..],
            Extension = Path.GetExtension(relativePath).ToLowerInvariant(),
            SizeBytes = 100,
            ModifiedUtcTicks = 1,
            RpmId = rpmId,
            Artist = "Somebody",
            Album = "A record",
        });
    }

    /// <summary>A counted play at a given instant. The listen log is the only way to change the
    /// totals.</summary>
    private void Play(string relativePath, long atUtcTicks)
    {
        var when = new DateTimeOffset(new DateTime(atUtcTicks, DateTimeKind.Utc));
        _db.NoteListen(ListenEntry.At(when, relativePath, heardSeconds: 200, lengthSeconds: 200)!);
    }

    private int PlaysOf(string relativePath) =>
        _db.Query().Single(t => t.RelativePath == relativePath).Plays;

    private long LastPlayedOf(string relativePath) =>
        _db.Query().Single(t => t.RelativePath == relativePath).LastPlayedUtcTicks;

    /// <summary>Closes the mirror and opens it again, as a later launch does. The rows stay.</summary>
    private void ReopenKeepingTheRows()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    /// <summary>Puts the mirror back to how a build before schema v67 left it, and opens it again so
    /// the migration runs over it: the play rows gone and the schema stamp back one.</summary>
    private void ReopenAsOlderMirror()
    {
        _db.Dispose();

        var path = Path.Combine(_root, LibraryDatabase.DataFolderName, LibraryDatabase.DataFileName);
        using (var raw = new SqliteConnection($"Data Source={path}"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "DROP TABLE IF EXISTS track_plays; PRAGMA user_version = 66";
            cmd.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    [Fact]
    public void An_existing_play_count_is_seeded_as_this_machines_own()
    {
        // A mirror written before v67 carries plays and no device rows. Every one of them is this
        // machine's, which is why the seed is correct.
        AddTrack("a/1.flac", rpmId: "t1");
        Play("a/1.flac", 500);
        ReopenAsOlderMirror();

        _db.ClaimSeededPlays(Mine);

        var mine = Assert.Single(_db.TrackPlaysOf("t1"));
        Assert.Equal(Mine, mine.Device);
        Assert.Equal(1, mine.Plays);
        Assert.Equal(500, mine.LastPlayedUtcTicks);
        Assert.Equal(0, mine.SentPlays);
    }

    [Fact]
    public void A_seeded_row_is_the_whole_of_what_this_machine_has_to_send()
    {
        // The point of the seed: a machine with years of listening behind it publishes all of it, once.
        AddTrack("a/1.flac", rpmId: "t1");
        Play("a/1.flac", 500);
        ReopenAsOlderMirror();
        _db.ClaimSeededPlays(Mine);

        var due = Assert.Single(_db.PlaysToSend(Mine, limit: 50));
        Assert.Equal("t1", due.RpmId);
        Assert.Equal(1, due.Plays);
    }

    [Fact]
    public void A_foreign_row_adds_to_the_tracks_own_count()
    {
        AddTrack("a/1.flac", rpmId: "t1");
        Play("a/1.flac", 100);
        Play("a/1.flac", 200);

        var applied = _db.ApplyForeignPlays(Mine, [new("t1", "phone", 3, 900, 0)]);

        Assert.Equal(1, applied);
        Assert.Equal(5, PlaysOf("a/1.flac"));
        Assert.Equal(900, LastPlayedOf("a/1.flac"));
    }

    [Fact]
    public void The_same_foreign_row_applied_again_moves_nothing()
    {
        AddTrack("a/1.flac", rpmId: "t1");
        Play("a/1.flac", 100);
        Play("a/1.flac", 200);
        _db.ApplyForeignPlays(Mine, [new("t1", "phone", 3, 900, 0)]);

        var applied = _db.ApplyForeignPlays(Mine, [new("t1", "phone", 3, 900, 0)]);

        Assert.Equal(0, applied);
        Assert.Equal(5, PlaysOf("a/1.flac"));
    }

    [Fact]
    public void A_foreign_row_that_goes_backwards_takes_its_difference_back()
    {
        AddTrack("a/1.flac", rpmId: "t1");
        Play("a/1.flac", 100);
        Play("a/1.flac", 200);
        _db.ApplyForeignPlays(Mine, [new("t1", "phone", 5, 900, 0)]);

        _db.ApplyForeignPlays(Mine, [new("t1", "phone", 1, 900, 0)]);

        // Two of this machine's and one of the phone's. A device that was reinstalled reports less than
        // before, and the total is what is there now, not a high-water mark.
        Assert.Equal(3, PlaysOf("a/1.flac"));
    }

    [Fact]
    public void A_last_played_that_goes_backwards_never_moves_the_track()
    {
        // The stamp is the newest of anybody's, so an older device reporting late must not make a record
        // look unplayed since. Recently listened is ordered on this.
        AddTrack("a/1.flac", rpmId: "t1");
        Play("a/1.flac", 900);

        _db.ApplyForeignPlays(Mine, [new("t1", "phone", 1, 100, 0)]);

        Assert.Equal(900, LastPlayedOf("a/1.flac"));
    }

    [Fact]
    public void This_machines_own_row_coming_back_from_the_cloud_is_ignored()
    {
        // The listing hands back every row a listener has, this machine's included. Applied as
        // foreign it would double this machine's own count on every pull.
        AddTrack("a/1.flac", rpmId: "t1");
        Play("a/1.flac", 100);
        Play("a/1.flac", 200);

        var applied = _db.ApplyForeignPlays(Mine, [new("t1", Mine, 2, 100, 0)]);

        Assert.Equal(0, applied);
        Assert.Equal(2, PlaysOf("a/1.flac"));
    }

    [Fact]
    public void A_row_for_a_track_this_machine_does_not_have_counts_against_nothing()
    {
        // A listener's other device has music this one does not, and there is no counter here to put
        // those plays on. The row itself is kept: see
        // A_foreign_row_for_a_track_that_arrives_later_is_not_lost.
        _db.ApplyForeignPlays(Mine, [new("nothing-here", "phone", 3, 900, 0)]);

        Assert.Empty(_db.Query().Where(t => t.Plays > 0));
    }

    [Fact]
    public void Two_other_devices_both_count()
    {
        AddTrack("a/1.flac", rpmId: "t1");

        _db.ApplyForeignPlays(Mine, [new("t1", "phone", 2, 300, 0), new("t1", "laptop", 3, 900, 0)]);

        Assert.Equal(5, PlaysOf("a/1.flac"));
        Assert.Equal(900, LastPlayedOf("a/1.flac"));
        Assert.Equal(2, _db.TrackPlaysOf("t1").Count);
    }

    [Fact]
    public void A_foreign_row_is_never_offered_to_the_cloud_as_this_machines()
    {
        AddTrack("a/1.flac", rpmId: "t1");
        _db.ApplyForeignPlays(Mine, [new("t1", "phone", 3, 900, 0)]);

        Assert.Empty(_db.PlaysToSend(Mine, limit: 50));
    }

    [Fact]
    public void Only_rows_with_something_unsent_are_offered_to_the_cloud()
    {
        AddTrack("a/1.flac", rpmId: "t1");
        Play("a/1.flac", 500);
        ReopenAsOlderMirror();
        _db.ClaimSeededPlays(Mine);

        var due = _db.PlaysToSend(Mine, limit: 50);
        Assert.Single(due);

        _db.NotePlaysSent(Mine, due);

        Assert.Empty(_db.PlaysToSend(Mine, limit: 50));
    }

    [Fact]
    public void What_is_recorded_as_sent_is_the_value_that_was_sent()
    {
        // A listen counted between the read and the write leaves the row higher than what went up.
        // Recording the row's CURRENT plays instead of the value sent would lose that play permanently.
        AddTrack("a/1.flac", rpmId: "t1");
        Play("a/1.flac", 500);
        ReopenAsOlderMirror();
        _db.ClaimSeededPlays(Mine);

        var due = _db.PlaysToSend(Mine, limit: 50);
        _db.ApplyForeignPlays(Mine, [new("t1", "phone", 4, 600, 0)]);
        _db.NotePlaysSent(Mine, due);

        var mine = _db.TrackPlaysOf("t1").Single(r => r.Device == Mine);
        Assert.Equal(1, mine.SentPlays);
        Assert.Equal(1, mine.Plays);
    }

    [Fact]
    public void A_counted_listen_writes_this_machines_own_row()
    {
        AddTrack("a/1.flac", rpmId: "t1");
        _db.ClaimSeededPlays(Mine);

        Play("a/1.flac", 500);

        Assert.Equal(1, PlaysOf("a/1.flac"));
        var mine = Assert.Single(_db.TrackPlaysOf("t1"));
        Assert.Equal(Mine, mine.Device);
        Assert.Equal(1, mine.Plays);
        Assert.Equal(500, mine.LastPlayedUtcTicks);
    }

    [Fact]
    public void A_listen_that_did_not_count_writes_no_row()
    {
        // Nine seconds of a five-minute track is a skip. It is worth a row in the log but it is not a
        // play, and these rows are about plays.
        AddTrack("a/1.flac", rpmId: "t1");
        _db.ClaimSeededPlays(Mine);

        var when = new DateTimeOffset(new DateTime(500, DateTimeKind.Utc));
        _db.NoteListen(ListenEntry.At(when, "a/1.flac", heardSeconds: 9, lengthSeconds: 300)!);

        Assert.Empty(_db.TrackPlaysOf("t1"));
    }

    [Fact]
    public void A_play_of_a_track_the_cloud_has_never_seen_counts_here_and_travels_nowhere()
    {
        // No rpm_id: the file was never uploaded, so there is nothing for a row to be keyed on. The
        // play still counts here, which is what the listener sees on this machine.
        AddTrack("a/2.flac", rpmId: null);
        _db.ClaimSeededPlays(Mine);

        Play("a/2.flac", 500);

        Assert.Equal(1, PlaysOf("a/2.flac"));
        Assert.Empty(_db.PlaysToSend(Mine, limit: 50));
    }

    [Fact]
    public void A_listen_before_this_install_has_been_named_is_claimed_with_the_seeded_rows()
    {
        // The counter runs from the moment the app opens, and the sync pass is what names the install,
        // so the first plays of a session can land before the claim. They are filed under the seed name
        // and renamed with everything else, so they are not left under a name that is never sent.
        AddTrack("a/1.flac", rpmId: "t1");

        Play("a/1.flac", 500);
        _db.ClaimSeededPlays(Mine);

        var mine = Assert.Single(_db.TrackPlaysOf("t1"));
        Assert.Equal(Mine, mine.Device);
        Assert.Equal(1, mine.Plays);
    }

    [Fact]
    public void Claiming_again_in_a_later_session_keeps_what_this_machine_has_counted()
    {
        // The claim runs on every sync pass, not only the first, and a listen counted before the pass
        // has named this install lands under the seed name. Renaming that row onto the row already
        // there would throw away everything this machine had counted, and the push would then tell every
        // other device to take those plays off too.
        AddTrack("a/1.flac", rpmId: "t1");
        _db.ClaimSeededPlays(Mine);
        Play("a/1.flac", 100);
        Play("a/1.flac", 200);
        _db.NotePlaysSent(Mine, _db.PlaysToSend(Mine, limit: 50));

        // A later session: the mirror is opened again and a play is counted before the pass runs.
        ReopenKeepingTheRows();
        Play("a/1.flac", 300);
        _db.ClaimSeededPlays(Mine);

        var mine = Assert.Single(_db.TrackPlaysOf("t1"));
        Assert.Equal(Mine, mine.Device);
        Assert.Equal(3, mine.Plays);
        Assert.Equal(3, PlaysOf("a/1.flac"));

        // And what was already agreed with the cloud is still agreed: only the new play is unsent.
        Assert.Equal(2, mine.SentPlays);
    }

    [Fact]
    public void A_machine_that_has_been_named_counts_under_that_name_from_the_next_launch()
    {
        // The name is the mirror's own, so a listen counted at the top of a session lands under it
        // without waiting for the first cloud pass.
        AddTrack("a/1.flac", rpmId: "t1");
        _db.ClaimSeededPlays(Mine);

        ReopenKeepingTheRows();
        Play("a/1.flac", 100);

        var mine = Assert.Single(_db.TrackPlaysOf("t1"));
        Assert.Equal(Mine, mine.Device);
    }

    [Fact]
    public void A_foreign_row_for_a_track_that_arrives_later_is_not_lost()
    {
        // A second computer holds part of the library. It pulls rows for music it does not have, and the
        // cursor moves past them. A row dropped here would never be asked for again, so the plays would
        // be missing permanently once that album was downloaded.
        _db.ApplyForeignPlays(Mine, [new("t1", "phone", 3, 900, 0)]);

        var kept = Assert.Single(_db.TrackPlaysOf("t1"));
        Assert.Equal(3, kept.Plays);
    }

    [Fact]
    public void The_tracks_own_count_is_what_its_rows_add_up_to()
    {
        // The invariant the whole design rests on. Written as a recompute rather than a delta, so a row
        // applied twice, a clamp, or a row that arrived before its track cannot leave the total and the
        // breakdown saying different things.
        AddTrack("a/1.flac", rpmId: "t1");
        Play("a/1.flac", 100);
        _db.ApplyForeignPlays(Mine, [new("t1", "phone", 3, 900, 0)]);
        _db.ApplyForeignPlays(Mine, [new("t1", "laptop", 2, 500, 0)]);

        Assert.Equal(_db.TrackPlaysOf("t1").Sum(r => r.Plays), PlaysOf("a/1.flac"));
    }

    [Fact]
    public void A_play_after_a_send_is_offered_again_as_the_new_total()
    {
        AddTrack("a/1.flac", rpmId: "t1");
        Play("a/1.flac", 500);
        ReopenAsOlderMirror();
        _db.ClaimSeededPlays(Mine);
        _db.NotePlaysSent(Mine, _db.PlaysToSend(Mine, limit: 50));

        Play("a/1.flac", 600);

        var row = Assert.Single(_db.PlaysToSend(Mine, limit: 50));
        Assert.Equal(2, row.Plays);
    }
}
