using RPM.Core.Analysis;
using RPM.Core.Library;
using RPM.Core.Metadata;

namespace RPM.Core.Tests;

/// <summary>
/// The listener's own answer about how a record is counted, and the one thing that makes it worth
/// storing at all: that it survives everything which throws the measurement away.
/// </summary>
public class BpmOctaveTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public BpmOctaveTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmoctave-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static TrackAnalysis Reading(string path, double bpm = 64) => new()
    {
        FilePath = path,
        Bpm = bpm,
        BpmConfidence = 0.9,
        DurationSeconds = 60,
        BeatTimes = [.. Enumerable.Range(0, 64).Select(i => i * (60.0 / bpm))],
        BeatsPerBar = 4,
        Key = new MusicalKey(9, IsMinor: true),
        KeyConfidence = 0.8,
        BassCurve = [.. Enumerable.Range(0, 64).Select(i => (float)(i / 128.0))],
        MidCurve = [.. Enumerable.Range(0, 64).Select(i => (float)(i / 160.0))],
        TopCurve = [.. Enumerable.Range(0, 64).Select(i => (float)(i / 320.0))],
        VocalCurve = [.. Enumerable.Range(0, 64).Select(_ => 0.4f)],
        Version = TrackAnalysis.AnalyzerVersion,
    };

    private static LibraryTrack Row(string relativePath, long size = 100, long modified = 1) => new()
    {
        RelativePath = relativePath,
        Folder = "",
        FileName = relativePath,
        Extension = Path.GetExtension(relativePath).ToLowerInvariant(),
        SizeBytes = size,
        ModifiedUtcTicks = modified,
    };

    [Fact]
    public void A_track_nobody_has_corrected_is_counted_as_it_was_measured()
    {
        _db.Upsert(Row("a.flac"));
        _db.SaveTrackAnalysis("a.flac", 100, 1, Reading("a.flac"));

        Assert.Equal(1, _db.BpmOctave("a.flac"), 9);

        var back = _db.GetTrackAnalysis("a.flac", 100, 1, "/music/a.flac");
        Assert.NotNull(back);
        Assert.Equal(64, back.Bpm, 3);
        Assert.False(back.IsEdited);
    }

    [Fact]
    public void Counting_it_the_other_way_moves_the_tempo_and_the_grid_together()
    {
        _db.Upsert(Row("a.flac"));
        _db.SaveTrackAnalysis("a.flac", 100, 1, Reading("a.flac"));
        _db.SetBpmOctave("a.flac", 2);

        var back = _db.GetTrackAnalysis("a.flac", 100, 1, "/music/a.flac");
        Assert.NotNull(back);
        Assert.Equal(128, back.Bpm, 3);

        // The grid has to move with it, or the tempo in the table and the beats a changeover lands on
        // would be a factor of two apart, which is the failure the number is being corrected for.
        var gaps = back.BeatTimes.Zip(back.BeatTimes.Skip(1), (a, b) => b - a).ToArray();
        Assert.All(gaps, gap => Assert.Equal(60.0 / 128, gap, 6));

        // And it is marked as somebody's opinion, which is what keeps it out of the shared pool.
        Assert.True(back.IsEdited);
    }

    [Fact]
    public void The_correction_survives_the_file_being_retagged()
    {
        _db.Upsert(Row("a.flac"));
        _db.SaveTrackAnalysis("a.flac", 100, 1, Reading("a.flac"));
        _db.SetBpmOctave("a.flac", 2);

        // A retag changes the file's size and mtime, which throws the analysis row away and re-reads
        // it. The person's answer is on the track, so it is still there afterwards.
        _db.Upsert(Row("a.flac", size: 137, modified: 9));
        Assert.Null(_db.GetTrackAnalysis("a.flac", 137, 9, "/music/a.flac"));

        _db.SaveTrackAnalysis("a.flac", 137, 9, Reading("a.flac"));
        var back = _db.GetTrackAnalysis("a.flac", 137, 9, "/music/a.flac");
        Assert.NotNull(back);
        Assert.Equal(128, back.Bpm, 3);
    }

    [Fact]
    public void It_travels_with_the_audio_when_the_reading_is_found_by_its_sound()
    {
        const string hash = "abc123";
        _db.Upsert(Row("a.flac"));
        _db.SaveTrackAnalysis("a.flac", 100, 1, Reading("a.flac"), hash);
        _db.SetBpmOctave("a.flac", 0.5);

        var back = _db.GetTrackAnalysisBySampleHash(hash, "/music/a.flac");
        Assert.NotNull(back);
        Assert.Equal(32, back.Bpm, 3);
    }

    [Fact]
    public void Setting_it_back_to_one_returns_what_was_measured()
    {
        _db.Upsert(Row("a.flac"));
        _db.SaveTrackAnalysis("a.flac", 100, 1, Reading("a.flac"));
        _db.SetBpmOctave("a.flac", 2);
        _db.SetBpmOctave("a.flac", 1);

        var back = _db.GetTrackAnalysis("a.flac", 100, 1, "/music/a.flac");
        Assert.NotNull(back);
        Assert.Equal(64, back.Bpm, 3);
        Assert.False(back.IsEdited);
    }

    [Fact]
    public void A_factor_that_is_not_a_factor_is_refused()
    {
        _db.Upsert(Row("a.flac"));
        _db.SaveTrackAnalysis("a.flac", 100, 1, Reading("a.flac"));

        _db.SetBpmOctave("a.flac", 0);
        _db.SetBpmOctave("a.flac", -2);

        Assert.Equal(1, _db.BpmOctave("a.flac"), 9);
    }

    [Fact]
    public void A_track_the_mirror_has_never_heard_of_counts_as_measured()
        => Assert.Equal(1, _db.BpmOctave("nowhere.flac"), 9);
}
