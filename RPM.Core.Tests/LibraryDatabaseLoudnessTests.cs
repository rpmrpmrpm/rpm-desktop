using RPM.Core.Analysis;
using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>
/// Where each track's loudness is stored, and the two rules that make the column reliable: a reading
/// belongs to the exact bytes it was measured from, and "cannot be measured" is stored as a result,
/// not left empty.
/// </summary>
public class LibraryDatabaseLoudnessTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public LibraryDatabaseLoudnessTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmlib-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static LibraryTrack Track(string relativePath, long size = 100, long modified = 1,
        double seconds = 200, double? lufs = null, double? peak = null)
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
            DurationSeconds = seconds,
            LoudnessLufs = lufs,
            TruePeak = peak,
        };
    }

    private LibraryTrack? Row(string relativePath) =>
        _db.FindByRelativePath(relativePath);

    [Fact]
    public void A_reading_is_stored_and_read_back()
    {
        _db.Upsert(Track("a.flac"));
        _db.SaveTrackLoudness("a.flac", 100, 1, new LoudnessReading(-13.4, 0.98));

        var row = Row("a.flac");
        Assert.Equal(-13.4, row!.LoudnessLufs!.Value, 6);
        Assert.Equal(0.98, row.TruePeak!.Value, 6);
    }

    /// <summary>
    /// A measurement needs a full decode, and the file can be replaced while it runs. The result then
    /// describes bytes that are not on disk any more. Storing it would leave the row wrong, which is
    /// worse than leaving it unmeasured, because nothing would measure it again.
    /// </summary>
    [Fact]
    public void A_reading_for_bytes_that_have_since_changed_is_refused()
    {
        _db.Upsert(Track("a.flac", size: 100, modified: 1));

        _db.SaveTrackLoudness("a.flac", 999, 1, new LoudnessReading(-13.4, 0.98));
        Assert.Null(Row("a.flac")!.LoudnessLufs);

        _db.SaveTrackLoudness("a.flac", 100, 999, new LoudnessReading(-13.4, 0.98));
        Assert.Null(Row("a.flac")!.LoudnessLufs);
    }

    /// <summary>
    /// "This file cannot be measured" must be storable. Otherwise a corrupt track is decoded in full
    /// on every pass, for as long as it is in the library.
    /// </summary>
    [Fact]
    public void A_track_that_cannot_be_measured_is_remembered_as_such()
    {
        _db.Upsert(Track("broken.mp3"));
        _db.SaveTrackLoudness("broken.mp3", 100, 1, LoudnessReading.Unknown);

        var row = Row("broken.mp3");
        Assert.NotNull(row!.LoudnessLufs);
        Assert.True(double.IsNegativeInfinity(row.LoudnessLufs!.Value));

        // It is not offered again.
        Assert.DoesNotContain(_db.QueryTracksNeedingLoudness(10), w => w.RelativePath == "broken.mp3");
    }

    /// <summary>
    /// A track is offered while EITHER value is missing, and one decode measures both.
    ///
    /// <para>The pass also takes an arousal measurement from the same decode (see
    /// Analysis/MoodMeter), and most tracks in a well-tagged library already have a ReplayGain tag. If
    /// only tracks with no loudness were offered, the mood would be measured only for libraries that
    /// need levelling.</para>
    /// </summary>
    [Fact]
    public void A_track_is_offered_while_either_answer_is_missing()
    {
        _db.Upsert(Track("measured.flac", lufs: -14, peak: 0.9));
        _db.Upsert(Track("fresh.flac"));

        var work = _db.QueryTracksNeedingLoudness(10);

        // Both: one has neither value, the other has a level but no mood.
        Assert.Contains(work, w => w.RelativePath == "fresh.flac");
        Assert.Contains(work, w => w.RelativePath == "measured.flac");
        Assert.Equal(2, _db.CountTracksNeedingLoudness());

        // The levelled one says which value it still needs. This lets the pass take a level from the
        // recording pool without opening a file; a mood is never taken from the pool.
        Assert.True(work.Single(w => w.RelativePath == "measured.flac").NeedsMood);

        // With a mood too, it is complete.
        _db.SaveTrackMood("measured.flac", 100, 1, new RPM.Core.Analysis.MoodReading(0.3, 3, 9));
        Assert.DoesNotContain(_db.QueryTracksNeedingLoudness(10), w => w.RelativePath == "measured.flac");
    }

    /// <summary>A cloud-only row has no bytes on this disk to decode. It is measured by the machine
    /// that has the file, and arrives with the value already set.</summary>
    [Fact]
    public void A_cloud_only_track_is_not_offered_for_measuring()
    {
        _db.Upsert(Track("gone.flac"));
        var id = Row("gone.flac")!.Id;
        _db.SeedSynced(id, "hash", 1, 1);
        _db.MarkOffloaded(id);

        Assert.DoesNotContain(_db.QueryTracksNeedingLoudness(10), w => w.RelativePath == "gone.flac");
    }

    /// <summary>
    /// An album lent by another of the user's computers is not measured on this machine, and it must
    /// be excluded explicitly, not by the cloud check above: its bytes are on that computer's disk, so
    /// the row arrives here marked Local. There is no file under this root to open, so offering it
    /// would cause a decode that fails on every pass while the pairing exists, and a queue on the
    /// settings page that never reaches zero. Its owner measures it and the value arrives with the
    /// next catalogue.
    /// </summary>
    [Fact]
    public void A_track_on_another_of_the_users_computers_is_not_offered_for_measuring()
    {
        _db.MirrorPeerLibrary("LAPTOP1", PeerLibraryMirror.ToCatalogRows([
            new PeerTrack
            {
                RpmId = "gid-1",
                RelativePath = "Band/Record/01 One.flac",
                Title = "One",
                Artist = "Band",
                AlbumArtist = "Band",
                Album = "Record",
                TrackNumber = 1,
                DurationSeconds = 200,
                SizeBytes = 5_000_000,
            },
        ]));

        var lent = _db.FindByRelativePath("Band/Record/01 One.flac");
        Assert.True(lent!.PeerAdopted);
        Assert.Null(lent.LoudnessLufs);
        Assert.Equal(CloudState.Local, lent.CloudState);

        Assert.Empty(_db.QueryTracksNeedingLoudness(10));
        Assert.Equal(0, _db.CountTracksNeedingLoudness());
    }

    /// <summary>
    /// The measurement is kept after a retag.
    ///
    /// <para>Import and sync rebuild a row from the file, and a file with no ReplayGain tag has no
    /// loudness value. If that empty value were stored, every measurement would be lost as soon as
    /// somebody corrected a spelling, and then measured again, for ever.</para>
    /// </summary>
    [Fact]
    public void An_upsert_that_knows_nothing_about_loudness_does_not_erase_it()
    {
        _db.Upsert(Track("a.flac", size: 100, modified: 1));
        _db.SaveTrackLoudness("a.flac", 100, 1, new LoudnessReading(-13.4, 0.98));

        // The same audio read again after a tag was written into it (larger by the tag's bytes and
        // with a new modified time, which is what a retag looks like), by a caller that found no
        // ReplayGain tag in it.
        _db.Upsert(Track("a.flac", size: 4_096, modified: 999) with { Title = "Renamed" });

        var row = Row("a.flac");
        Assert.Equal(-13.4, row!.LoudnessLufs!.Value, 6);
        Assert.Equal(0.98, row.TruePeak!.Value, 6);
    }

    /// <summary>A re-encode removes it. For loudness, different audio is a different recording, and
    /// keeping the old value would level the new file by the old file's mastering.</summary>
    [Fact]
    public void An_upsert_of_changed_audio_drops_the_reading()
    {
        _db.Upsert(Track("a.flac", size: 100, modified: 1, seconds: 200));
        _db.SaveTrackLoudness("a.flac", 100, 1, new LoudnessReading(-13.4, 0.98));

        // Not only larger: a different recording, which the old value does not describe.
        _db.Upsert(Track("a.flac", size: 200, modified: 2, seconds: 143));

        Assert.Null(Row("a.flac")!.LoudnessLufs);
        Assert.Contains(_db.QueryTracksNeedingLoudness(10), w => w.RelativePath == "a.flac");
    }

    /// <summary>A file that already has a ReplayGain tag needs no loudness measurement: the tag reader
    /// fills the value, and the upsert keeps it.</summary>
    [Fact]
    public void A_file_arriving_with_a_tagged_reading_keeps_it()
    {
        _db.Upsert(Track("tagged.flac", lufs: -11.2, peak: 1.01));

        var row = Row("tagged.flac");
        Assert.Equal(-11.2, row!.LoudnessLufs!.Value, 6);
        Assert.Equal(1.01, row.TruePeak!.Value, 6);

        // Its LEVEL needs no decode. It is still offered to the pass for the arousal values, which no
        // tag stores, and NeedsMood says so.
        var work = _db.QueryTracksNeedingLoudness(10);
        Assert.Single(work);
        Assert.True(work[0].NeedsMood);
    }

    [Fact]
    public void An_album_reports_the_readings_and_the_loudest_peak_it_has()
    {
        _db.Upsert(Track("Sides/a.flac", lufs: -10, peak: 0.9, seconds: 300));
        _db.Upsert(Track("Sides/b.flac", lufs: -20, peak: 1.02, seconds: 300));
        _db.Upsert(Track("Sides/c.flac", seconds: 300));                       // never measured
        _db.Upsert(Track("Other/d.flac", lufs: -3, peak: 1.5, seconds: 300));  // another album

        var readings = _db.QueryAlbumLoudness("Sides");

        Assert.Equal(2, readings.Count);
        Assert.Equal(1.02, _db.QueryAlbumTruePeak("Sides"), 6);

        // The album is nearly as loud as its loud track, not the average of the two.
        double album = ReplayGain.AlbumLufs(readings);
        Assert.True(album > -13.5 && album < -12.5, $"album read {album:F2} LUFS");
    }

    [Fact]
    public void An_album_nobody_measured_reports_nothing_rather_than_zero()
    {
        _db.Upsert(Track("Quiet/a.flac"));

        Assert.Empty(_db.QueryAlbumLoudness("Quiet"));
        Assert.Equal(0, _db.QueryAlbumTruePeak("Quiet"));
    }

    [Fact]
    public void Clearing_puts_the_whole_library_back_in_the_queue()
    {
        _db.Upsert(Track("a.flac", lufs: -14, peak: 0.9));
        _db.Upsert(Track("b.flac", lufs: -12, peak: 0.8));

        _db.ClearTrackLoudness();

        Assert.Equal(2, _db.CountTracksNeedingLoudness());
        Assert.Null(Row("a.flac")!.TruePeak);
    }
}
