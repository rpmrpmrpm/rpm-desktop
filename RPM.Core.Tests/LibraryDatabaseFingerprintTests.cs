using RPM.Core.Library;

namespace RPM.Core.Tests;

public class LibraryDatabaseFingerprintTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public LibraryDatabaseFingerprintTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mdlib-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static LibraryTrack Track(string relativePath, string? artist = null, string? title = null,
        long size = 100, long modified = 1)
    {
        var slash = relativePath.LastIndexOf('/');
        return new LibraryTrack
        {
            RelativePath = relativePath,
            Folder = slash < 0 ? "" : relativePath[..slash],
            FileName = slash < 0 ? relativePath : relativePath[(slash + 1)..],
            Extension = Path.GetExtension(relativePath).ToLowerInvariant(),
            SizeBytes = size,
            ModifiedUtcTicks = modified,
            Artist = artist,
            Title = title,
        };
    }

    /// <summary>Offloading applies only to a synced track, so sync first, as in the cloud tests.</summary>
    private void Offload(long trackId)
    {
        _db.SeedSynced(trackId, "hash", 1, 1);
        _db.MarkOffloaded(trackId);
    }

    [Fact]
    public void Save_then_get_roundtrips_and_stale_stamps_read_as_missing()
    {
        _db.SaveTrackFingerprint("a.mp3", 100, 1, 180.5, [1, 2, 3]);

        Assert.Equal(new byte[] { 1, 2, 3 }, _db.GetTrackFingerprint("a.mp3", 100, 1));

        // The file changed: if either stamp differs, the row does not describe this file any more.
        Assert.Null(_db.GetTrackFingerprint("a.mp3", 101, 1));
        Assert.Null(_db.GetTrackFingerprint("a.mp3", 100, 2));
    }

    [Fact]
    public void Query_returns_tracks_without_a_current_fingerprint_and_skips_offloaded_ones()
    {
        _db.Upsert(Track("done.mp3"));
        _db.Upsert(Track("todo.mp3"));
        _db.Upsert(Track("changed.mp3", size: 200, modified: 9));
        _db.Upsert(Track("gone.mp3"));

        _db.SaveTrackFingerprint("done.mp3", 100, 1, 60, [1]);
        _db.SaveTrackFingerprint("changed.mp3", 100, 1, 60, [1]);   // made for an older file
        Offload(_db.Query("gone")[0].Id);                           // bytes are not on this machine

        var work = _db.QueryTracksNeedingFingerprint(10);

        Assert.Equal(["changed.mp3", "todo.mp3"], work.Select(w => w.RelativePath).OrderBy(p => p).ToArray());
    }

    [Fact]
    public void Counts_feed_the_progress_readout()
    {
        _db.Upsert(Track("a.mp3"));
        _db.Upsert(Track("b.mp3"));
        _db.Upsert(Track("off.mp3"));
        Offload(_db.Query("off")[0].Id);
        _db.SaveTrackFingerprint("a.mp3", 100, 1, 60, [1]);

        Assert.Equal(1, _db.CountTrackFingerprints());
        Assert.Equal(2, _db.CountFingerprintableTracks());
    }

    [Fact]
    public void Submission_queue_carries_the_tags_and_requires_them()
    {
        _db.Upsert(Track("tagged.mp3", "Muse", "Hysteria"));
        _db.Upsert(Track("untitled.mp3"));                          // nothing to claim, never offered
        _db.SaveTrackFingerprint("tagged.mp3", 100, 1, 180, [1, 2]);
        _db.SaveTrackFingerprint("untitled.mp3", 100, 1, 180, [1, 2]);

        var pending = _db.QueryFingerprintsNeedingSubmission(10);

        var report = Assert.Single(pending);
        Assert.Equal("tagged.mp3", report.RelativePath);
        Assert.Equal("Muse", report.Artist);
        Assert.Equal("Hysteria", report.Title);
        Assert.Equal(180, report.DurationSeconds);
        Assert.Equal(new byte[] { 1, 2 }, report.Fingerprint);
    }

    [Fact]
    public void Marking_submitted_clears_the_queue_and_a_new_fingerprint_requeues()
    {
        _db.Upsert(Track("a.mp3", "Muse", "Hysteria"));
        _db.SaveTrackFingerprint("a.mp3", 100, 1, 180, [1]);

        _db.MarkFingerprintSubmitted("a.mp3", 42);
        Assert.Empty(_db.QueryFingerprintsNeedingSubmission(10));

        // The file changed and was fingerprinted again: what was sent before was a different
        // fingerprint, so the row is offered again.
        _db.Upsert(Track("a.mp3", "Muse", "Hysteria", size: 200, modified: 5));
        _db.SaveTrackFingerprint("a.mp3", 200, 5, 180, [9]);
        Assert.Single(_db.QueryFingerprintsNeedingSubmission(10));
    }

    [Fact]
    public void An_unreadable_file_is_remembered_but_never_offered()
    {
        _db.Upsert(Track("broken.mp3", "Muse", "Hysteria"));
        _db.SaveTrackFingerprint("broken.mp3", 100, 1, 0, []);     // fpcalc could not read it

        // Remembered: not offered for fingerprinting again while the file is unchanged.
        Assert.Empty(_db.QueryTracksNeedingFingerprint(10));
        // But an empty fingerprint identifies nothing, so it is never submitted.
        Assert.Empty(_db.QueryFingerprintsNeedingSubmission(10));
        // Present-but-empty reads back as empty, distinct from null (missing/stale).
        Assert.Equal(Array.Empty<byte>(), _db.GetTrackFingerprint("broken.mp3", 100, 1));
    }
}
