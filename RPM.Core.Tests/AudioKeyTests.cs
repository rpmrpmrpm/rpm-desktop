using RPM.Core.Analysis;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The name an analysis is filed under. What has to be true of it: that the same audio gets the same
/// name however it arrived, that different audio never does, and that the things which legitimately
/// change the samples (the rate, the decoder, the key's own version) change the name rather than
/// quietly sharing one.
/// </summary>
public class AudioKeyTests
{
    private static float[] Tone(int samples, double hz = 440, int rate = 22050, double amplitude = 0.5)
    {
        var mono = new float[samples];
        for (int i = 0; i < samples; i++)
            mono[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate));
        return mono;
    }

    [Fact]
    public void The_same_samples_get_the_same_key()
    {
        var a = AudioKey.OfSamples(Tone(5000), 22050, AudioKey.Ffmpeg);
        var b = AudioKey.OfSamples(Tone(5000), 22050, AudioKey.Ffmpeg);

        Assert.NotNull(a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void A_key_is_lower_case_hex_of_a_sha256()
    {
        var key = AudioKey.OfSamples(Tone(1000), 22050, AudioKey.Ffmpeg);

        Assert.NotNull(key);
        Assert.Equal(64, key!.Length);
        Assert.All(key, c => Assert.Contains(c, "0123456789abcdef"));
    }

    [Fact]
    public void Different_audio_gets_a_different_key()
    {
        var a = AudioKey.OfSamples(Tone(5000, hz: 440), 22050, AudioKey.Ffmpeg);
        var b = AudioKey.OfSamples(Tone(5000, hz: 441), 22050, AudioKey.Ffmpeg);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void A_longer_record_is_not_a_prefix_of_a_shorter_one()
    {
        // The length goes into the hash, so a track and its own first half cannot collide even where
        // every sample they share is identical.
        var whole = AudioKey.OfSamples(Tone(5000), 22050, AudioKey.Ffmpeg);
        var half = AudioKey.OfSamples(Tone(2500), 22050, AudioKey.Ffmpeg);

        Assert.NotEqual(whole, half);
    }

    [Fact]
    public void The_same_samples_read_at_two_rates_are_two_keys()
    {
        var a = AudioKey.OfSamples(Tone(5000), 22050, AudioKey.Ffmpeg);
        var b = AudioKey.OfSamples(Tone(5000), 44100, AudioKey.Ffmpeg);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Two_decode_chains_never_share_a_key()
    {
        // The reason for hashing the chain: ffmpeg and AVFoundation reading one file give two answers,
        // and that is allowed. What they must not do is look like one machine contradicting another
        // about the same key.
        var a = AudioKey.OfSamples(Tone(5000), 22050, AudioKey.Ffmpeg);
        var b = AudioKey.OfSamples(Tone(5000), 22050, AudioKey.AvFoundation);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void A_sample_that_lands_in_the_same_step_lands_on_the_same_key()
    {
        // What the quantiser is and is not. It rounds two nearby floats onto one integer, so a value
        // clear of a step boundary tolerates a small change, but a hash is all or nothing, and a sample
        // sitting ON a boundary does not. This is why a mismatched key is a miss and never an
        // argument: two decode chains are hashed apart rather than trusted to agree.
        var flat = Enumerable.Repeat(0.25f, 1000).ToArray();
        var nudged = flat.Select(s => s + 1e-7f).ToArray();

        Assert.Equal(
            AudioKey.OfSamples(flat, 22050, AudioKey.Ffmpeg),
            AudioKey.OfSamples(nudged, 22050, AudioKey.Ffmpeg));
    }

    [Fact]
    public void A_difference_the_quantiser_can_see_is_a_different_key()
    {
        var quiet = Enumerable.Repeat(0.25f, 1000).ToArray();
        var louder = Enumerable.Repeat(0.25f + 1f / 32767f, 1000).ToArray();

        Assert.NotEqual(
            AudioKey.OfSamples(quiet, 22050, AudioKey.Ffmpeg),
            AudioKey.OfSamples(louder, 22050, AudioKey.Ffmpeg));
    }

    [Fact]
    public void A_sample_past_full_scale_is_clamped_rather_than_wrapped()
    {
        // Decoders overshoot -1..1 legitimately, and a wrap would turn the loudest moment of a record
        // into its quietest, silently, and only for whoever's decoder overshot.
        var hot = new float[] { 2f, 2f, 2f, 2f };
        var full = new float[] { 1f, 1f, 1f, 1f };

        Assert.Equal(
            AudioKey.OfSamples(full, 22050, AudioKey.Ffmpeg),
            AudioKey.OfSamples(hot, 22050, AudioKey.Ffmpeg));

        // And the negative side, where a wrap would be a sign flip rather than a level change.
        Assert.Equal(
            AudioKey.OfSamples([-1.5f, -1.5f], 22050, AudioKey.Ffmpeg),
            AudioKey.OfSamples([-2f, -2f], 22050, AudioKey.Ffmpeg));
    }

    [Fact]
    public void Nothing_to_hash_is_not_a_key()
    {
        Assert.Null(AudioKey.OfSamples([], 22050, AudioKey.Ffmpeg));
        Assert.Null(AudioKey.OfSamples(Tone(100), 0, AudioKey.Ffmpeg));
        Assert.Null(AudioKey.OfSamples(Tone(100), 22050, ""));
    }

    [Fact]
    public void The_key_survives_a_chunk_boundary()
    {
        // Hashing runs in blocks; a track longer than one block must hash the same as if it did not.
        var length = 16384 * 2 + 7;
        Assert.Equal(
            AudioKey.OfSamples(Tone(length), 22050, AudioKey.Ffmpeg),
            AudioKey.OfSamples(Tone(length), 22050, AudioKey.Ffmpeg));

        Assert.NotEqual(
            AudioKey.OfSamples(Tone(length), 22050, AudioKey.Ffmpeg),
            AudioKey.OfSamples(Tone(length - 1), 22050, AudioKey.Ffmpeg));
    }
}

/// <summary>
/// Finding an analysis by what the record sounds like rather than by where the file is: the lookup a
/// re-tagged file, a second copy and a moved library all need, and the one the cloud pool will use.
/// </summary>
public class AnalysisBySampleHashTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public AnalysisBySampleHashTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmhash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static TrackAnalysis Reading(string path, double bpm = 128.4, int? version = null) => new()
    {
        FilePath = path,
        DurationSeconds = 214,
        Bpm = bpm,
        BpmConfidence = 0.9,
        BeatTimes = [.. Enumerable.Range(0, 64).Select(i => i * 0.5)],
        FirstDownbeat = 2,
        BeatsPerBar = 4,
        Key = new MusicalKey(9, IsMinor: true),
        KeyConfidence = 0.8,
        // With a grid and no bands this reads back as a row to be analysed again rather than as a
        // stored answer (see LibraryDatabase.Whole, which repairs a reading taken from the pool that
        // has no bands).
        BassCurve = [.. Enumerable.Range(0, 64).Select(i => (float)(i / 128.0))],
        MidCurve = [.. Enumerable.Range(0, 64).Select(i => (float)(i / 160.0))],
        TopCurve = [.. Enumerable.Range(0, 64).Select(i => (float)(i / 320.0))],
        VocalCurve = [.. Enumerable.Range(0, 64).Select(_ => 0.4f)],
        Version = version ?? TrackAnalysis.AnalyzerVersion,
    };

    private const string Hash = "aa11bb22cc33dd44ee55ff6600778899aabbccddeeff00112233445566778899";
    private const string Other = "1100000000000000000000000000000000000000000000000000000000000011";

    [Fact]
    public void A_re_tagged_file_finds_its_own_analysis_again()
    {
        _db.SaveTrackAnalysis("a.flac", 100, 1, Reading("a.flac"), Hash);

        // Something outside the app wrote a genre in: new size, new mtime, same music.
        Assert.Null(_db.GetTrackAnalysis("a.flac", 137, 9, "/music/a.flac"));

        var back = _db.GetTrackAnalysisBySampleHash(Hash, "/music/a.flac");
        Assert.NotNull(back);
        Assert.Equal(128.4, back!.Bpm, 3);
        Assert.Equal("/music/a.flac", back.FilePath);
    }

    [Fact]
    public void A_second_copy_of_a_record_reads_the_first_ones_analysis()
    {
        _db.SaveTrackAnalysis("albums/a.flac", 100, 1, Reading("albums/a.flac"), Hash);

        var back = _db.GetTrackAnalysisBySampleHash(Hash, "/music/singles/a.flac");

        Assert.NotNull(back);
        Assert.Equal(2, back!.FirstDownbeat);
        // The row it came from describes another file; the analysis is handed over pointing at this one.
        Assert.Equal("/music/singles/a.flac", back.FilePath);
    }

    [Fact]
    public void Audio_nobody_has_heard_has_no_answer()
    {
        _db.SaveTrackAnalysis("a.flac", 100, 1, Reading("a.flac"), Hash);

        Assert.Null(_db.GetTrackAnalysisBySampleHash(Other, "/music/b.flac"));
        Assert.Null(_db.GetTrackAnalysisBySampleHash("", "/music/b.flac"));
    }

    [Fact]
    public void A_row_from_an_older_analyser_is_not_offered()
    {
        _db.SaveTrackAnalysis("a.flac", 100, 1, Reading("a.flac", version: TrackAnalysis.AnalyzerVersion - 1), Hash);

        Assert.Null(_db.GetTrackAnalysisBySampleHash(Hash, "/music/a.flac"));
    }

    [Fact]
    public void The_modelled_reading_wins_where_two_rows_describe_one_record()
    {
        _db.SaveTrackAnalysis("dsp.flac", 100, 1, Reading("dsp.flac", bpm: 64.2), Hash);
        _db.SaveTrackAnalysis(
            "model.flac", 200, 2,
            Reading("model.flac", bpm: 128.4, version: TrackAnalysis.ModelledVersion), Hash);

        var back = _db.GetTrackAnalysisBySampleHash(Hash, "/music/new.flac");

        Assert.NotNull(back);
        Assert.True(back!.Modelled);
        Assert.Equal(128.4, back.Bpm, 3);
    }

    [Fact]
    public void A_save_that_decoded_nothing_leaves_the_hash_where_it_was()
    {
        _db.SaveTrackAnalysis("a.flac", 100, 1, Reading("a.flac"), Hash);

        // The tagging pass, a repair, anything that rewrites the row without having read the audio.
        _db.SaveTrackAnalysis("a.flac", 137, 9, Reading("a.flac", bpm: 130));

        var back = _db.GetTrackAnalysisBySampleHash(Hash, "/music/a.flac");
        Assert.NotNull(back);
        Assert.Equal(130, back!.Bpm, 3);
    }

    [Fact]
    public void A_row_that_was_never_hashed_is_not_found_by_a_missing_hash()
    {
        _db.SaveTrackAnalysis("old.flac", 100, 1, Reading("old.flac"));

        Assert.Null(_db.GetTrackAnalysisBySampleHash(Hash, "/music/old.flac"));
        // And the ordinary lookup still works for it, which is every row in an upgraded library.
        Assert.NotNull(_db.GetTrackAnalysis("old.flac", 100, 1, "/music/old.flac"));
    }

    // ── what this machine can say about which names are one record ────────────

    private void Track(string relativePath, string? rpmId = null, string? contentHash = null)
    {
        _db.Upsert(new LibraryTrack
        {
            RelativePath = relativePath,
            Folder = "",
            FileName = relativePath,
            Extension = Path.GetExtension(relativePath).ToLowerInvariant(),
            SizeBytes = 100,
            ModifiedUtcTicks = 1,
            RpmId = rpmId,
            ContentHash = contentHash,
        });
    }

    private const string Canonical = "cc11bb22cc33dd44ee55ff6600778899aabbccddeeff00112233445566778899";
    private const string Id = "6f9619ff-8b86-d011-b42d-00cf4fc964ff";

    [Fact]
    public void An_uploaded_track_has_two_names_without_anything_decoding_it()
    {
        // The purpose of the one-off pass: most of a library can say something useful having never
        // been analysed, because the upload already worked out the hash of its own bytes.
        Track("a.flac", rpmId: Id, contentHash: Canonical);

        var row = Assert.Single(_db.TracksWithSeveralNames(null, 10));
        Assert.Equal("a.flac", row.RelativePath);
        Assert.Equal(Id, row.RpmId);
        Assert.Equal(Canonical, row.CanonicalSha);
        Assert.Null(row.SampleHash);
    }

    [Fact]
    public void An_analysed_track_brings_the_third_name_along()
    {
        Track("a.flac", rpmId: Id, contentHash: Canonical);
        _db.SaveTrackAnalysis("a.flac", 100, 1, Reading("a.flac"), Hash);

        var row = Assert.Single(_db.TracksWithSeveralNames(null, 10));
        Assert.Equal(Hash, row.SampleHash);
    }

    [Fact]
    public void A_track_with_one_name_is_not_offered()
    {
        // Never uploaded, never analysed: an id and nothing to relate it to.
        Track("lonely.flac", rpmId: Id);
        // And the same the other way round.
        Track("nameless.flac", contentHash: Canonical);

        Assert.Empty(_db.TracksWithSeveralNames(null, 10));
    }

    [Fact]
    public void An_analysed_track_that_was_never_uploaded_still_has_two()
    {
        Track("b.flac", rpmId: Id);
        _db.SaveTrackAnalysis("b.flac", 100, 1, Reading("b.flac"), Hash);

        Assert.Single(_db.TracksWithSeveralNames(null, 10));
    }

    [Fact]
    public void The_pass_walks_the_library_by_path()
    {
        Track("a.flac", rpmId: Id, contentHash: Canonical);
        Track("b.flac", rpmId: Id, contentHash: Canonical);
        Track("c.flac", rpmId: Id, contentHash: Canonical);

        var first = _db.TracksWithSeveralNames(null, 2);
        Assert.Equal(["a.flac", "b.flac"], first.Select(r => r.RelativePath));

        var next = _db.TracksWithSeveralNames(first[^1].RelativePath, 2);
        Assert.Equal(["c.flac"], next.Select(r => r.RelativePath));

        Assert.Empty(_db.TracksWithSeveralNames("c.flac", 2));
    }

    [Fact]
    public void What_the_app_notes_about_a_library_stays_with_it()
    {
        Assert.Null(_db.GetMeta(LibraryDatabase.RecordingKeysPublishedKey));
        Assert.Equal(0, _db.GetMetaVersion(LibraryDatabase.RecordingKeysPublishedKey));

        _db.SetMeta(LibraryDatabase.RecordingKeysPublishedKey, "1");
        Assert.Equal(1, _db.GetMetaVersion(LibraryDatabase.RecordingKeysPublishedKey));

        // Raised rather than added again when the pass gains a generation.
        _db.SetMeta(LibraryDatabase.RecordingKeysPublishedKey, "2");
        Assert.Equal(2, _db.GetMetaVersion(LibraryDatabase.RecordingKeysPublishedKey));

        // A note that is not a number reads as never done, rather than throwing at startup.
        _db.SetMeta(LibraryDatabase.RecordingKeysPublishedKey, "yes");
        Assert.Equal(0, _db.GetMetaVersion(LibraryDatabase.RecordingKeysPublishedKey));
    }

    /// <summary>
    /// The fourth name, and the only one that crosses between two people who ripped the same CD.
    ///
    /// <para>The other three name this copy (its bytes, its decoded samples, the id in its tags), and
    /// two independent rips share none of them. Without this the second machine to meet a record
    /// analyses one the first has already read.</para>
    /// </summary>
    [Fact]
    public void The_fingerprints_name_counts_as_a_name()
    {
        Track("rip.flac", rpmId: Id);
        _db.SaveTrackFingerprint("rip.flac", 100, 1, 214.5, [1, 2, 3, 4]);
        _db.MarkFingerprintSubmitted("rip.flac", 1234, songId: 4242);

        var keys = _db.RecordingKeysFor("rip.flac");
        Assert.Equal(4242, keys.SongId);

        // An id and a song id is two names, which is a bundle worth publishing: a track that has
        // never been uploaded and never been analysed can now say something.
        var row = Assert.Single(_db.TracksWithSeveralNames(null, 10));
        Assert.Equal(4242, row.SongId);
    }

    /// <summary>"The service did not say" must not wipe an id the row already has.</summary>
    [Fact]
    public void A_silent_answer_leaves_the_name_alone()
    {
        Track("rip.flac", rpmId: Id);
        _db.SaveTrackFingerprint("rip.flac", 100, 1, 214.5, [1, 2, 3, 4]);
        _db.MarkFingerprintSubmitted("rip.flac", 1234, songId: 99);
        _db.MarkFingerprintSubmitted("rip.flac", 2345, songId: 0);

        Assert.Equal(99, _db.RecordingKeysFor("rip.flac").SongId);
    }
}
