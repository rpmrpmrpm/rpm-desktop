using RPM.Core.Models;
using RPM.Core.Splitting;

namespace RPM.Core.Tests;

public class AlbumFileDetectorTests : IDisposable
{
    private readonly string _root;

    public AlbumFileDetectorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mdtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Touch(string relative, string content = "x")
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static TrackInfo Track(string path, double duration, uint track = 0, uint trackCount = 0) => new()
    {
        FilePath = path,
        Extension = Path.GetExtension(path),
        DurationSeconds = duration,
        TrackNumber = track,
        TrackCount = trackCount,
    };

    [Fact]
    public void Long_file_is_candidate()
    {
        var path = Touch("rip.mp3");
        var c = AlbumFileDetector.Detect(Track(path, 45 * 60));

        Assert.NotNull(c);
        Assert.True(c!.Hint.HasFlag(AlbumFileHint.LongDuration));
        Assert.Null(c.CueSheetPath);
    }

    [Fact]
    public void Normal_track_is_not_candidate()
    {
        var path = Touch("song.mp3");
        Assert.Null(AlbumFileDetector.Detect(Track(path, 4 * 60)));
    }

    [Fact]
    public void Filename_hint_alone_is_not_enough()
    {
        var path = Touch("Pink Floyd Full Album.mp3");
        Assert.Null(AlbumFileDetector.Detect(Track(path, 4 * 60)));
    }

    [Fact]
    public void Named_side_is_a_candidate_once_it_runs_long_enough()
    {
        var path = Touch("Sides/Abbey Road - Side A.flac");

        Assert.Null(AlbumFileDetector.Detect(Track(path, 5 * 60)));

        var c = AlbumFileDetector.Detect(Track(path, 12 * 60));
        Assert.NotNull(c);
        Assert.True(c!.Hint.HasFlag(AlbumFileHint.FilenameHint));
    }

    [Fact]
    public void Long_song_among_an_albums_worth_of_tracks_is_not_a_candidate()
    {
        // The long one on somebody's record. Badging it asks the user to split a song into itself.
        for (var i = 1; i <= 7; i++)
            Touch($"Prog/{i:00} filler.flac");
        var epic = Touch("Prog/08 The Long One.flac");

        Assert.Null(AlbumFileDetector.Detect(Track(epic, 25 * 60)));
    }

    [Fact]
    public void Long_file_alone_in_its_folder_is_a_candidate()
    {
        var path = Touch("Rip/side.flac");
        Assert.NotNull(AlbumFileDetector.Detect(Track(path, 22 * 60)));
    }

    [Fact]
    public void A_file_that_says_which_track_it_is_stays_a_track()
    {
        var path = Touch("Live/epic.flac");

        // Track 4 of something. Length does not change what it says it is.
        Assert.Null(AlbumFileDetector.Detect(Track(path, 50 * 60, track: 4)));
        // Track 1 of twelve: still one track of a record.
        Assert.Null(AlbumFileDetector.Detect(Track(path, 50 * 60, track: 1, trackCount: 12)));
        // Track 1 of nothing is what a tagger writes on a whole side.
        Assert.NotNull(AlbumFileDetector.Detect(Track(path, 50 * 60, track: 1)));
    }

    [Fact]
    public void A_cue_sheet_beats_every_other_rule()
    {
        // Short, numbered, with other tracks around it, and still an album file, because the cue sheet
        // is the track list of this exact file.
        for (var i = 1; i <= 7; i++)
            Touch($"Cued/{i:00} filler.flac");
        var path = Touch("Cued/album.flac");
        Touch("Cued/album.cue", "TRACK 01 AUDIO\n  INDEX 01 00:00:00");

        var c = AlbumFileDetector.Detect(Track(path, 6 * 60, track: 3, trackCount: 12));
        Assert.NotNull(c);
        Assert.True(c!.Hint.HasFlag(AlbumFileHint.CueSheet));
    }

    [Fact]
    public void Same_stem_cue_makes_short_file_candidate()
    {
        var path = Touch("album.flac");
        Touch("album.cue", "TRACK 01 AUDIO\n  INDEX 01 00:00:00");

        var c = AlbumFileDetector.Detect(Track(path, 10 * 60));

        Assert.NotNull(c);
        Assert.True(c!.Hint.HasFlag(AlbumFileHint.CueSheet));
        Assert.EndsWith("album.cue", c.CueSheetPath);
    }

    [Fact]
    public void Single_folder_cue_matches_via_file_line()
    {
        var path = Touch("rip.flac");
        Touch("different-name.cue", "FILE \"rip.flac\" WAVE\nTRACK 01 AUDIO\n  INDEX 01 00:00:00");

        var c = AlbumFileDetector.Detect(Track(path, 10 * 60));

        Assert.NotNull(c);
        Assert.EndsWith("different-name.cue", c!.CueSheetPath);
    }

    [Fact]
    public void Folder_cue_for_other_file_is_ignored()
    {
        var path = Touch("rip.flac");
        Touch("other.cue", "FILE \"other.flac\" WAVE\nTRACK 01 AUDIO\n  INDEX 01 00:00:00");

        Assert.Null(AlbumFileDetector.Detect(Track(path, 10 * 60)));
    }

    [Fact]
    public void Batched_index_matches_single_file_detect_across_folders()
    {
        // Three folders, the same three cue scenarios the single-file tests cover, plus a plain track.
        var sameStem = Touch("A/album.flac");
        Touch("A/album.cue", "TRACK 01 AUDIO\n  INDEX 01 00:00:00");
        var fileLine = Touch("B/rip.flac");
        Touch("B/different-name.cue", "FILE \"rip.flac\" WAVE\nTRACK 01 AUDIO\n  INDEX 01 00:00:00");
        var otherCue = Touch("C/rip.flac");
        Touch("C/other.cue", "FILE \"other.flac\" WAVE\nTRACK 01 AUDIO\n  INDEX 01 00:00:00");
        var plain = Touch("C/song.mp3");

        var tracks = new[]
        {
            Track(sameStem, 10 * 60),
            Track(fileLine, 10 * 60),
            Track(otherCue, 10 * 60),
            Track(plain, 4 * 60),
        };

        var index = FolderScanIndex.Build(tracks.Select(t => t.FilePath));

        foreach (var t in tracks)
        {
            var single = AlbumFileDetector.Detect(t);
            var batched = AlbumFileDetector.Detect(t, index);
            Assert.Equal(single?.CueSheetPath, batched?.CueSheetPath);
            Assert.Equal(single?.Hint, batched?.Hint);
        }

        // And the specific expectations, so a regression in BOTH paths can't pass silently.
        Assert.EndsWith("album.cue", AlbumFileDetector.Detect(tracks[0], index)!.CueSheetPath);
        Assert.EndsWith("different-name.cue", AlbumFileDetector.Detect(tracks[1], index)!.CueSheetPath);
        Assert.Null(AlbumFileDetector.Detect(tracks[2], index));
        Assert.Null(AlbumFileDetector.Detect(tracks[3], index));
    }

    [Fact]
    public void Batched_index_counts_siblings_the_same_way_the_single_file_probe_does()
    {
        // One folder is an album (the long track is a song); the other holds one long file (a record).
        var album = new List<TrackInfo>();
        for (var i = 1; i <= 7; i++)
            album.Add(Track(Touch($"Album/{i:00} track.flac"), 4 * 60));
        album.Add(Track(Touch("Album/08 The Long One.flac"), 25 * 60));
        var lone = Track(Touch("Rip/whole side.flac"), 25 * 60);

        var index = FolderScanIndex.Build(album.Select(t => t.FilePath).Append(lone.FilePath));

        Assert.Null(AlbumFileDetector.Detect(album[^1], index));
        Assert.Null(AlbumFileDetector.Detect(album[^1]));
        Assert.NotNull(AlbumFileDetector.Detect(lone, index));
        Assert.NotNull(AlbumFileDetector.Detect(lone));
    }
}
