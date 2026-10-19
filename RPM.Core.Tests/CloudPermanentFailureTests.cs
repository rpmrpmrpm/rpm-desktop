using Microsoft.Data.Sqlite;
using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// A track the cloud cannot finish is recorded and then not tried again.
///
/// <para>Both halves matter. Without the record, the catch-up pass finds the same file on every run
/// and reads it through ffmpeg again at every launch (for example a thirty-channel master). The
/// record is a sentence a person can read. The track is not tried again until the file itself
/// changes.</para>
/// </summary>
public class CloudPermanentFailureTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public CloudPermanentFailureTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cloudfail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>ffmpeg's real words when the rendition transcode meets a surround master.</summary>
    private const string Rematrix =
        "[auto_aresample_0 @ 0000029d74737080] [SWR @ 0000029d750e2680] Rematrix is needed between " +
        "30 channels and stereo but there is not enough information to do it\n" +
        "[af#0:0 @ 0000029d73271340] Error reinitializing filters!\n" +
        "Error while filtering: Invalid argument\n";

    private static LibraryTrack Track(string relativePath, long size = 5_000_000, long mtime = 1)
    {
        var slash = relativePath.LastIndexOf('/');
        return new LibraryTrack
        {
            RelativePath = relativePath,
            Folder = slash < 0 ? "" : relativePath[..slash],
            FileName = slash < 0 ? relativePath : relativePath[(slash + 1)..],
            Extension = Path.GetExtension(relativePath),
            SizeBytes = size,
            ModifiedUtcTicks = mtime,
        };
    }

    private LibraryTrack Add(string relativePath)
    {
        _db.Upsert(Track(relativePath));
        return _db.Query(Path.GetFileNameWithoutExtension(relativePath))[0];
    }

    /// <summary>A track in the cloud with no head clip: what the catch-up pass looks for.</summary>
    private LibraryTrack Synced(string relativePath)
    {
        var track = Add(relativePath);
        _db.Upsert(Track(relativePath) with { RpmId = "01ARZ3NDEKTSV4RRFFQ69G5FAV" });
        track = _db.Query(Path.GetFileNameWithoutExtension(relativePath))[0];
        _db.SeedSynced(track.Id, "hash-" + track.Id, 4_000_000, DateTime.UtcNow.Ticks);
        return _db.Query(Path.GetFileNameWithoutExtension(relativePath))[0];
    }

    // ── what the user is told ──

    /// <summary>The thirty-channel case.</summary>
    [Fact]
    public void A_surround_master_is_named_as_one_rather_than_as_ffmpeg_said_it()
    {
        var failure = CloudFailure.Classify($"ffmpeg could not transcode to 128k: {Rematrix}");

        Assert.Equal(CloudFailure.Channels, failure.MessageKey);
        Assert.True(failure.Permanent);
    }

    /// <summary>
    /// The one that must NOT be permanent. A network that was briefly down is the case the retry
    /// schedule exists for. Marking it as never-again would stop a whole library's upload because of
    /// one short outage.
    /// </summary>
    [Fact]
    public void A_network_failure_is_still_worth_trying_again()
    {
        foreach (var said in new[]
                 {
                     "The operation has timed out.",
                     "No such host is known.",
                     "The SSL connection could not be established.",
                     "",
                 })
        {
            var failure = CloudFailure.Classify(said);
            Assert.Equal(CloudFailure.Unknown, failure.MessageKey);
            Assert.False(failure.Permanent, $"'{said}' should not be permanent");
        }
    }

    /// <summary>
    /// ffmpeg could not open the file at all. The canonicalisation wrapper puts "ffmpeg" in front of
    /// every failure, so a rule that matches that word alone would mark these permanent, and a file
    /// the import is still writing would be refused for good on the first try.
    /// </summary>
    [Fact]
    public void A_file_that_could_not_be_opened_is_tried_again()
    {
        foreach (var said in new[]
                 {
                     "ffmpeg could not canonicalize '01 - Act I.flac': [in#0 @ 000002e351f9eb40] Error opening input: Permission denied",
                     "ffmpeg could not canonicalize '01 - Act I.flac': Error opening input file x.flac.; Error opening input files: No such file or directory",
                     "ffmpeg could not canonicalize '01 - Act I.flac': av_interleaved_write_frame(): No space left on device",
                     "The process cannot access the file 'x.flac' because it is being used by another process.",
                 })
        {
            var failure = CloudFailure.Classify(said);
            Assert.Equal(CloudFailure.Unknown, failure.MessageKey);
            Assert.False(failure.Permanent, $"'{said}' should not be permanent");
        }
    }

    /// <summary>A file the decoder cannot read is the file's fault and will read the same next time.</summary>
    [Fact]
    public void A_file_that_will_not_decode_is_permanent()
    {
        var failure = CloudFailure.Classify(
            "ffmpeg returned no audio for 'x.m4a': [aac] Invalid data found when processing input");

        Assert.Equal(CloudFailure.Unreadable, failure.MessageKey);
        Assert.True(failure.Permanent);
    }

    /// <summary>
    /// A damaged AAC. ffmpeg names the corruption before it names the failure, so the message begins
    /// with text no rule here matches, and the phrase that decides it comes four clauses later.
    /// </summary>
    [Fact]
    public void A_damaged_aac_is_permanent_however_ffmpeg_opens()
    {
        var failure = CloudFailure.Classify(
            "ffmpeg could not transcode to 128k: [aac] Number of bands (27) exceeds limit (13).; " +
            "[aist#0:0/aac] Error submitting packet to decoder: Invalid data found when processing " +
            "input; [aac] channel element 2.11 is not allocated");

        Assert.Equal(CloudFailure.Unreadable, failure.MessageKey);
        Assert.True(failure.Permanent);
    }

    /// <summary>
    /// The message must reach the caller. <c>MakeRenditionsAsync</c> catches the encoder's refusal and
    /// returns an outcome. The outcome must carry what ffmpeg said, or the caller has nothing to
    /// classify and treats a file ffmpeg can never read as a short network outage.
    /// </summary>
    [Fact]
    public void A_refusal_carries_what_was_said()
    {
        var result = new RenditionResult(RenditionOutcome.Failed,
            "ffmpeg could not transcode to 128k: Invalid data found when processing input");

        Assert.True(CloudFailure.Classify(result.Said).Permanent);

        // And a success says nothing, because nothing was refused.
        Assert.Null(new RenditionResult(RenditionOutcome.Made).Said);
    }

    // ── and then let go of ──

    /// <summary>
    /// The main case. Without the mark, the catch-up query returns the same track for ever; the mark
    /// takes it off.
    /// </summary>
    [Fact]
    public void A_refused_rendition_is_not_asked_for_again()
    {
        var track = Synced("Artist/Album/surround.m4a");
        Assert.Single(_db.CloudTracksWithLocalFiles(10));

        _db.MarkCloudPartial(track.Id, "Too many audio channels");

        Assert.Empty(_db.CloudTracksWithLocalFiles(10));
    }

    /// <summary>The music is untouched. A yellow cloud is not a failed upload, and everything that
    /// counts what is backed up must go on counting this one.</summary>
    [Fact]
    public void The_track_is_still_in_the_cloud()
    {
        var track = Synced("Artist/Album/surround2.m4a");
        _db.MarkCloudPartial(track.Id, "Too many audio channels");

        var after = _db.Query("surround2")[0];
        Assert.Equal(CloudState.Synced, after.CloudState);
        Assert.Equal("Too many audio channels", after.CloudPartialError);
        Assert.Null(after.CloudError);
    }

    /// <summary>The yellow cloud, and the red one, from the facts on the row.</summary>
    [Fact]
    public void The_mark_says_which_of_the_two_it_is()
    {
        Assert.Equal(AlbumCloudBadge.Incomplete, TrackCloudBadge.For(
            CloudState.Synced, selected: true, partialError: "Too many audio channels"));

        Assert.Equal(AlbumCloudBadge.Failed, TrackCloudBadge.For(
            CloudState.Error, selected: true, attempts: CloudRetrySchedule.MaxAttempts));

        // Still being retried: the outline cloud. The queue has not given up, so neither does the
        // mark.
        Assert.Equal(AlbumCloudBadge.Partial, TrackCloudBadge.For(
            CloudState.Error, selected: true, attempts: 1));
    }

    // ── until the file changes ──

    /// <summary>
    /// A re-ripped or re-encoded file is a different question. Both refusals clear, and the attempt
    /// count with them. A retry that kept its five spent attempts would not retry.
    /// </summary>
    [Fact]
    public void New_bytes_earn_a_fresh_go()
    {
        var track = Synced("Artist/Album/rerip.m4a");
        _db.MarkCloudPartial(track.Id, "Too many audio channels");
        _db.MarkCloudAttemptFailed(track.Id, "Upload failed", CloudRetrySchedule.NeverAgainTicks(DateTime.UtcNow));

        // The file was replaced: sync upserts it with the size and time it now has.
        _db.Upsert(Track("Artist/Album/rerip.m4a", size: 9_000_000, mtime: 999) with
        {
            RpmId = "01ARZ3NDEKTSV4RRFFQ69G5FAV",
        });

        var after = _db.Query("rerip")[0];
        Assert.Null(after.CloudPartialError);
        Assert.Null(after.CloudError);
        Assert.Equal(0, after.CloudAttempts);
    }

    /// <summary>
    /// And a retag is NOT that. The upsert runs several times a session over untouched audio, and a
    /// file that cannot be encoded must not rejoin the queue every time somebody fixes a spelling.
    /// </summary>
    [Fact]
    public void A_retag_leaves_the_refusal_where_it_is()
    {
        var track = Synced("Artist/Album/retag.m4a");
        _db.MarkCloudPartial(track.Id, "Too many audio channels");

        // Same bytes, new title.
        _db.Upsert(Track("Artist/Album/retag.m4a") with
        {
            RpmId = "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            Title = "A Better Spelling",
        });

        var after = _db.Query("retag")[0];
        Assert.Equal("Too many audio channels", after.CloudPartialError);
        Assert.Empty(_db.CloudTracksWithLocalFiles(10));
    }

    /// <summary>The Retry the user presses. A track that never got up there goes back in the line.</summary>
    [Fact]
    public void Asking_again_puts_a_failed_upload_back_in_the_queue()
    {
        var track = Add("Artist/Album/asked.m4a");
        _db.SetCloudSelected([track.Id], selected: true);
        _db.MarkCloudAttemptFailed(track.Id, "Audio could not be converted",
            CloudRetrySchedule.NeverAgainTicks(DateTime.UtcNow));

        Assert.Equal(CloudState.Error, _db.Query("asked")[0].CloudState);

        _db.ClearCloudFailure(track.Id);

        var after = _db.Query("asked")[0];
        Assert.Equal(CloudState.Queued, after.CloudState);
        Assert.Null(after.CloudError);
        Assert.Equal(0, after.CloudAttempts);
        Assert.Contains(_db.QueryDueForUpload(DateTime.UtcNow.Ticks, 10), t => t.Id == track.Id);
    }

    /// <summary>The same button on a yellow cloud leaves the track in the cloud, where it is.</summary>
    [Fact]
    public void Asking_again_does_not_un_upload_a_synced_track()
    {
        var track = Synced("Artist/Album/asked2.m4a");
        _db.MarkCloudPartial(track.Id, "Too many audio channels");

        _db.ClearCloudFailure(track.Id);

        var after = _db.Query("asked2")[0];
        Assert.Equal(CloudState.Synced, after.CloudState);
        Assert.Null(after.CloudPartialError);
        Assert.Single(_db.CloudTracksWithLocalFiles(10));
    }

    // ── every write that stamps the row, not only the scan ──

    /// <summary>A tag edit rewrites the file and updates the row with its new size and date. The
    /// refusal must clear here, because the scan after it sees nothing left to compare.</summary>
    [Fact]
    public void A_retag_that_rewrote_the_file_clears_the_refusal()
    {
        var track = Add("Artist/Album/edited.flac");
        _db.MarkCloudAttemptFailed(track.Id, "Audio could not be converted",
            CloudRetrySchedule.NeverAgainTicks(DateTime.UtcNow));

        _db.MoveMany([("Artist/Album/edited.flac", Track("Artist/Album/edited.flac", size: 5_000_100, mtime: 2))]);

        var after = _db.Query("edited")[0];
        Assert.Null(after.CloudError);
        Assert.Equal(0, after.CloudAttempts);
        Assert.Contains(_db.QueryDueForUpload(DateTime.UtcNow.Ticks, 10), t => t.Id == track.Id);
    }

    [Fact]
    public void A_move_of_the_same_bytes_keeps_the_refusal()
    {
        var track = Add("Artist/Album/moved.flac");
        _db.MarkCloudAttemptFailed(track.Id, "Audio could not be converted",
            CloudRetrySchedule.NeverAgainTicks(DateTime.UtcNow));

        _db.MoveMany([("Artist/Album/moved.flac", Track("Artist/Album/moved2.flac"))]);

        Assert.Equal("Audio could not be converted", _db.Query("moved2")[0].CloudError);
    }

    /// <summary>The analysis-tag writer's stamp, another write that must clear the refusal.</summary>
    [Fact]
    public void A_file_stamp_that_moved_clears_the_refusal()
    {
        var track = Add("Artist/Album/tagged.flac");
        _db.MarkCloudAttemptFailed(track.Id, "Audio could not be converted",
            CloudRetrySchedule.NeverAgainTicks(DateTime.UtcNow));

        _db.NoteFileStamp("Artist/Album/tagged.flac", 5_000_200, 3);

        Assert.Null(_db.Query("tagged")[0].CloudError);
    }

    // ── a file that is being written ──

    [Fact]
    public void A_deferred_upload_waits_without_counting_a_failure()
    {
        var track = Add("Artist/Album/busy.flac");
        _db.MarkCloudAttemptFailed(track.Id, "Upload failed", 0);
        var later = DateTime.UtcNow.AddMinutes(1).Ticks;

        _db.DeferCloudUpload(track.Id, later);

        var after = _db.Query("busy")[0];
        Assert.Equal(CloudState.Queued, after.CloudState);
        Assert.Null(after.CloudError);
        Assert.Equal(1, after.CloudAttempts);
        Assert.DoesNotContain(_db.QueryDueForUpload(DateTime.UtcNow.Ticks, 10), t => t.Id == track.Id);
        Assert.Contains(_db.QueryDueForUpload(later, 10), t => t.Id == track.Id);
        Assert.Equal(later, _db.NextCloudRetryTicks(DateTime.UtcNow.Ticks));
    }

    [Fact]
    public void A_never_again_date_is_not_a_retry_to_wake_for()
    {
        var track = Add("Artist/Album/never.flac");
        _db.MarkCloudAttemptFailed(track.Id, "Audio could not be converted",
            CloudRetrySchedule.NeverAgainTicks(DateTime.UtcNow));

        Assert.Null(_db.NextCloudRetryTicks(DateTime.UtcNow.Ticks));
    }

    /// <summary>
    /// Error rows sort first and the run reads twelve at a time. A page made only of tracks with no
    /// attempts left must not be read as an empty queue, or the queued tracks behind it never go up.
    /// </summary>
    [Fact]
    public void Tracks_out_of_attempts_do_not_hide_the_queue_behind_them()
    {
        var waiting = Add("Artist/Album/waiting.flac");
        _db.SetCloudState(waiting.Id, CloudState.Queued);

        foreach (var name in new[] { "spent1", "spent2", "spent3" })
        {
            var spent = Add($"Artist/Album/{name}.flac");
            for (var i = 0; i < CloudRetrySchedule.MaxAttempts; i++)
                _db.MarkCloudAttemptFailed(spent.Id, "Upload failed", 0);
        }

        var due = _db.QueryDueForUpload(DateTime.UtcNow.Ticks, limit: 2);

        Assert.Equal(waiting.Id, Assert.Single(due).Id);
    }

    // ── a refusal is final only after a second attempt ──

    [Fact]
    public void A_refusal_made_final_on_one_attempt_gets_a_second()
    {
        var track = Add("Artist/Album/otello.flac");
        _db.MarkCloudAttemptFailed(track.Id, "Audio could not be converted",
            CloudRetrySchedule.NeverAgainTicks(DateTime.UtcNow));

        Assert.Equal(1, _db.ReleaseUnconfirmedRefusals(DateTime.UtcNow.Ticks));

        var after = _db.Query("otello")[0];
        Assert.Equal(CloudState.Queued, after.CloudState);
        Assert.Contains(_db.QueryDueForUpload(DateTime.UtcNow.Ticks, 10), t => t.Id == track.Id);

        // The error and the attempt stay, so the same refusal again is final.
        Assert.Equal("Audio could not be converted", after.CloudError);
        Assert.Equal(1, after.CloudAttempts);
    }

    [Fact]
    public void A_refusal_that_repeated_stays_final()
    {
        var track = Add("Artist/Album/broken.flac");
        _db.MarkCloudAttemptFailed(track.Id, "Audio could not be converted", 0);
        _db.MarkCloudAttemptFailed(track.Id, "Audio could not be converted",
            CloudRetrySchedule.NeverAgainTicks(DateTime.UtcNow));

        Assert.Equal(0, _db.ReleaseUnconfirmedRefusals(DateTime.UtcNow.Ticks));
        Assert.Equal(CloudState.Error, _db.Query("broken")[0].CloudState);
    }

    /// <summary>
    /// A mirror without the column gains it on open, whatever its version stamp says.
    ///
    /// <para>Both numbers matter. 40 is the ordinary upgrade. 41 is the trap: the stamp is written at
    /// the end of Migrate and records that the steps ran, not that they all worked. A mirror written
    /// by a build that had the READER for this column but not the step that adds it is stamped at a
    /// version whose guard never runs again. Then every query that names the column fails for good,
    /// including the one the cloud sync runs on every pass.</para>
    /// </summary>
    [Theory]
    [InlineData(40)]
    [InlineData(41)]
    public void A_mirror_without_the_column_gains_it_on_open(int stamped)
    {
        var older = Path.Combine(_root, $"older-{stamped}");
        Directory.CreateDirectory(older);

        using (var db = new LibraryDatabase(MirrorLocation.InFolder(older)))
            db.Upsert(Track("Artist/Album/old.mp3"));

        var file = Directory.GetFiles(older, "*.db", SearchOption.AllDirectories).First();
        using (var connection = new SqliteConnection($"Data Source={file}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                $"ALTER TABLE tracks DROP COLUMN cloud_partial_error; PRAGMA user_version = {stamped};";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var reopened = new LibraryDatabase(MirrorLocation.InFolder(older));

        var track = reopened.Query("old")[0];
        Assert.Null(track.CloudPartialError);

        // The read path that crashed the window, and the query that stopped the sync.
        _ = reopened.CloudTracksWithLocalFiles(5);

        reopened.MarkCloudPartial(track.Id, "Too many audio channels");
        Assert.Equal("Too many audio channels", reopened.Query("old")[0].CloudPartialError);
    }
}
