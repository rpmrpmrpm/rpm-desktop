using RPM.Core.Analysis;
using RPM.Core.Library;
using RPM.Core.Metadata;

namespace RPM.Core.Tests;

/// <summary>
/// Getting what the analyser measured out of the app's own cache and into the file: which tracks are
/// offered for it, what actually lands in the tags, and the bookkeeping that stops the write throwing
/// away the analysis it used.
/// </summary>
public class AnalysisTagWritingTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public AnalysisTagWritingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmtags-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>A minimal valid MPEG-1 Layer III frame (silence): enough file structure for TagLib to
    /// accept it, and small enough to write a dozen of.</summary>
    private string CreateMp3(string name = "test.mp3")
    {
        var path = Path.Combine(_root, name);
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;   // 128kbps 44.1kHz
        File.WriteAllBytes(path, frame);
        return path;
    }

    private static TrackAnalysis Confident(string path, double bpm = 128.4, double keyConfidence = 0.8) => new()
    {
        FilePath = path,
        Bpm = bpm,
        BpmConfidence = 0.9,
        BeatTimes = [.. Enumerable.Range(0, 64).Select(i => i * 0.5)],
        Key = new MusicalKey(9, IsMinor: true),   // A minor
        KeyConfidence = keyConfidence,
        // A reading with a grid and no bands is not one this analyser produces, and is read back as a
        // row to be re-analysed (see LibraryDatabase.Whole).
        BassCurve = [.. Enumerable.Range(0, 64).Select(i => (float)(i / 128.0))],
        MidCurve = [.. Enumerable.Range(0, 64).Select(i => (float)(i / 160.0))],
        TopCurve = [.. Enumerable.Range(0, 64).Select(i => (float)(i / 320.0))],
        VocalCurve = [.. Enumerable.Range(0, 64).Select(i => 0.4f)],
        Version = TrackAnalysis.AnalyzerVersion,
    };

    private static LibraryTrack Row(string relativePath, long size, long modified) => new()
    {
        RelativePath = relativePath,
        Folder = "",
        FileName = relativePath,
        Extension = Path.GetExtension(relativePath).ToLowerInvariant(),
        SizeBytes = size,
        ModifiedUtcTicks = modified,
    };

    // ── what lands in the file ────────────────────────────────────────────────

    [Fact]
    public void The_measured_tempo_and_key_are_written_into_the_file()
    {
        var path = CreateMp3();

        var written = TagReader.WriteAnalysisTags(path, Confident(path));

        Assert.Equal(128u, written.Bpm);
        Assert.Equal("Am", written.Key);

        var back = TagReader.Read(path);
        Assert.Equal(128u, back.Bpm);
        Assert.Equal("Am", back.MusicalKey);
    }

    [Fact]
    public void Nothing_else_in_the_file_is_touched()
    {
        var path = CreateMp3();
        TagReader.WriteAll(path, new Models.TrackInfo
        {
            FilePath = path,
            Extension = ".mp3",
            Title = "Hysteria",
            Artist = "Muse",
            Album = "Absolution",
            Comment = "left alone",
        });

        TagReader.WriteAnalysisTags(path, Confident(path));

        var back = TagReader.Read(path);
        Assert.Equal("Hysteria", back.Title);
        Assert.Equal("Muse", back.Artist);
        Assert.Equal("Absolution", back.Album);
        Assert.Equal("left alone", back.Comment);
        Assert.Equal(128u, back.Bpm);
    }

    [Fact]
    public void A_file_that_already_says_it_is_not_rewritten()
    {
        var path = CreateMp3();
        TagReader.WriteAnalysisTags(path, Confident(path));

        var before = new FileInfo(path).LastWriteTimeUtc;
        var written = TagReader.WriteAnalysisTags(path, Confident(path, bpm: 128.4));

        Assert.False(written.Any);
        // The mtime is what the analysis cache and the library sync are keyed on: a pass over a
        // collection that rewrote every already-tagged file would hand the next sync the whole
        // collection to read again.
        Assert.Equal(before, new FileInfo(path).LastWriteTimeUtc);
    }

    [Fact]
    public void What_somebody_else_tagged_survives_the_pass()
    {
        var path = CreateMp3();
        TagReader.WriteAll(path, new Models.TrackInfo
        {
            FilePath = path, Extension = ".mp3", Bpm = 87, MusicalKey = "Fm",
        });

        Assert.False(TagReader.WriteAnalysisTags(path, Confident(path)).Any);

        var back = TagReader.Read(path);
        Assert.Equal(87u, back.Bpm);
        Assert.Equal("Fm", back.MusicalKey);
    }

    [Fact]
    public void The_overwrite_pass_replaces_it()
    {
        var path = CreateMp3();
        TagReader.WriteAll(path, new Models.TrackInfo
        {
            FilePath = path, Extension = ".mp3", Bpm = 87, MusicalKey = "Fm",
        });

        Assert.True(TagReader.WriteAnalysisTags(path, Confident(path), overwrite: true).Any);

        var back = TagReader.Read(path);
        Assert.Equal(128u, back.Bpm);
        Assert.Equal("Am", back.MusicalKey);
    }

    [Fact]
    public void A_file_that_is_not_here_is_not_opened()
    {
        var missing = Path.Combine(_root, "gone.mp3");

        Assert.False(TagReader.WriteAnalysisTags(missing, Confident(missing)).Any);
    }

    // ── which tracks are offered ──────────────────────────────────────────────

    [Fact]
    public void An_analysed_track_waits_until_its_file_has_been_written()
    {
        _db.SaveTrackAnalysis("a.flac", 100, 1, Confident("a.flac"));

        Assert.Equal(1, _db.CountTracksNeedingAnalysisTags());
        Assert.Equal("a.flac", Assert.Single(_db.TracksNeedingAnalysisTags(10)).RelativePath);

        _db.NoteAnalysisTagsWritten("a.flac", 120, 2);

        Assert.Equal(0, _db.CountTracksNeedingAnalysisTags());
        Assert.Empty(_db.TracksNeedingAnalysisTags(10));
    }

    [Fact]
    public void A_track_with_nothing_worth_writing_is_never_offered()
    {
        // No tempo the DJ would match to, and no key the detector is confident in: opening the file
        // could only discover there was nothing to put in it.
        _db.SaveTrackAnalysis("quiet.flac", 100, 1, new TrackAnalysis
        {
            FilePath = "quiet.flac",
            Bpm = 0,
            BpmConfidence = 0,
            Key = MusicalKey.Unknown,
            KeyConfidence = 0,
        });

        Assert.Equal(0, _db.CountTracksNeedingAnalysisTags());
    }

    [Fact]
    public void A_key_alone_is_reason_enough_to_offer_a_track()
    {
        _db.SaveTrackAnalysis("tonal.flac", 100, 1, new TrackAnalysis
        {
            FilePath = "tonal.flac",
            Bpm = 0,
            BpmConfidence = 0,
            Key = new MusicalKey(0, IsMinor: false),
            KeyConfidence = 0.9,
        });

        Assert.Equal(1, _db.CountTracksNeedingAnalysisTags());
    }

    [Fact]
    public void Written_tracks_come_back_when_the_pass_is_told_to_overwrite()
    {
        _db.SaveTrackAnalysis("a.flac", 100, 1, Confident("a.flac"));
        _db.NoteAnalysisTagsWritten("a.flac", 120, 2);

        Assert.Equal(0, _db.CountTracksNeedingAnalysisTags());
        Assert.Equal(1, _db.CountTracksNeedingAnalysisTags(overwrite: true));
    }

    [Fact]
    public void Marking_a_track_written_re_stamps_the_row_against_what_the_file_became()
    {
        _db.SaveTrackAnalysis("a.flac", 100, 1, Confident("a.flac"));

        // The write moved the file: new size, new mtime.
        _db.NoteAnalysisTagsWritten("a.flac", 137, 9);

        // Still the same analysis, still current. It is not thrown away as stale by the write that
        // used it, which would make the track be decoded again on the next set.
        var back = _db.GetTrackAnalysis("a.flac", 137, 9, "/music/a.flac");
        Assert.NotNull(back);
        Assert.Equal(128.4, back!.Bpm, 3);
        Assert.Null(_db.GetTrackAnalysis("a.flac", 100, 1, "/music/a.flac"));
    }

    [Fact]
    public void A_fresh_analysis_is_offered_again_even_where_the_old_one_was_written()
    {
        _db.SaveTrackAnalysis("a.flac", 100, 1, Confident("a.flac"));
        _db.NoteAnalysisTagsWritten("a.flac", 100, 1);
        Assert.Equal(0, _db.CountTracksNeedingAnalysisTags());

        // The file changed and was analysed again: the numbers in its tags are now the old reading's.
        _db.SaveTrackAnalysis("a.flac", 200, 2, Confident("a.flac", bpm: 174.2));

        Assert.Equal(1, _db.CountTracksNeedingAnalysisTags());
    }

    [Fact]
    public void Re_stamping_a_row_keeps_the_sync_from_reading_the_whole_collection_again()
    {
        _db.Upsert(Row("a.flac", size: 100, modified: 1));

        _db.NoteFileStamp("a.flac", 137, 9);

        var row = _db.FindByRelativePath("a.flac");
        Assert.Equal(137, row!.SizeBytes);
        Assert.Equal(9, row.ModifiedUtcTicks);
    }
}
