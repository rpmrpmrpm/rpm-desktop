using RPM.Core.Analysis;
using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// How the mirror stores each track's CLAP embedding, and how the sound pass avoids reading a track twice.
///
/// <para>The pass goes through the whole collection over as many launches as it takes. These tests cover
/// the ways it can go wrong: handing out the same track for ever because its answer was never stored,
/// keeping an answer about bytes that have since changed, and offering tracks it cannot read.</para>
/// </summary>
public class LibraryDatabaseSoundTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public LibraryDatabaseSoundTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmsound-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static LibraryTrack Track(string path, long size = 100, long mtime = 1, double seconds = 240) => new()
    {
        RelativePath = path,
        Folder = "",
        FileName = path,
        Extension = ".flac",
        SizeBytes = size,
        ModifiedUtcTicks = mtime,
        DurationSeconds = seconds,
    };

    private static float[] Embedding(int seed)
    {
        var random = new Random(seed);
        var v = new float[SoundEmbedding.Dimensions];
        for (int i = 0; i < v.Length; i++)
            v[i] = (float)(random.NextDouble() * 2 - 1);
        return SoundEmbedding.Average([v])!;
    }

    [Fact]
    public void An_embedding_survives_the_round_trip()
    {
        _db.Upsert(Track("a.flac"));
        var embedding = Embedding(1);

        _db.SaveTrackSound("a.flac", 100, 1, embedding);

        var stored = Assert.Single(_db.QuerySoundEmbeddings());
        Assert.Equal("a.flac", stored.Key);
        Assert.True(SoundEmbedding.Cosine(embedding, stored.Value) > 0.99999);
    }

    [Fact]
    public void A_measured_track_is_not_handed_out_again()
    {
        _db.Upsert(Track("a.flac"));
        _db.Upsert(Track("b.flac"));
        Assert.Equal(2, _db.CountTracksNeedingSound());

        _db.SaveTrackSound("a.flac", 100, 1, Embedding(1));

        var next = Assert.Single(_db.QueryTracksNeedingSound(10));
        Assert.Equal("b.flac", next.RelativePath);
        Assert.Equal(240, next.DurationSeconds);
        Assert.Equal(1, _db.CountTracksNeedingSound());
    }

    [Fact]
    public void A_file_that_gave_nothing_is_not_tried_again_until_it_changes()
    {
        _db.Upsert(Track("broken.flac"));

        _db.SaveTrackSound("broken.flac", 100, 1, null);

        Assert.Empty(_db.QueryTracksNeedingSound(10));
        Assert.Empty(_db.QuerySoundEmbeddings());

        // A new file at the same path is a new question.
        _db.Upsert(Track("broken.flac", size: 200, mtime: 2));
        Assert.Single(_db.QueryTracksNeedingSound(10));
    }

    [Fact]
    public void A_reading_of_bytes_that_have_changed_is_not_kept()
    {
        _db.Upsert(Track("a.flac", size: 200, mtime: 2));

        // Measured from the old file, written after the new one arrived.
        _db.SaveTrackSound("a.flac", 100, 1, Embedding(1));

        Assert.Empty(_db.QuerySoundEmbeddings());
        Assert.Single(_db.QueryTracksNeedingSound(10));
    }

    [Fact]
    public void A_reading_goes_stale_when_the_file_is_replaced()
    {
        _db.Upsert(Track("a.flac"));
        _db.SaveTrackSound("a.flac", 100, 1, Embedding(1));

        _db.Upsert(Track("a.flac", size: 300, mtime: 5));

        Assert.Empty(_db.QuerySoundEmbeddings());
        Assert.Single(_db.QueryTracksNeedingSound(10));
    }

    [Fact]
    public void Tracks_too_short_to_be_songs_are_not_handed_out()
    {
        _db.Upsert(Track("interlude.flac", seconds: Moods.LeastSeconds - 1));
        _db.Upsert(Track("song.flac", seconds: Moods.LeastSeconds));

        var next = Assert.Single(_db.QueryTracksNeedingSound(10));
        Assert.Equal("song.flac", next.RelativePath);
    }

    [Fact]
    public void A_track_with_no_bytes_here_is_not_handed_out()
    {
        _db.Upsert(Track("cloud.flac") with { CloudState = CloudState.CloudOnly });
        Assert.Empty(_db.QueryTracksNeedingSound(10));
        Assert.Equal(0, _db.CountTracksNeedingSound());
    }

    [Fact]
    public void Readings_of_tracks_that_left_the_library_are_forgotten()
    {
        _db.Upsert(Track("a.flac"));
        _db.Upsert(Track("b.flac"));
        _db.SaveTrackSound("a.flac", 100, 1, Embedding(1));
        _db.SaveTrackSound("b.flac", 100, 1, Embedding(2));

        _db.Remove("b.flac");

        Assert.Equal(1, _db.ForgetSoundOfMissingTracks());
        Assert.Equal("a.flac", Assert.Single(_db.QuerySoundEmbeddings()).Key);
    }

    [Fact]
    public void A_reading_for_a_path_the_library_does_not_hold_is_not_written()
    {
        _db.SaveTrackSound("nowhere.flac", 100, 1, Embedding(1));
        Assert.Equal(0, _db.ForgetSoundOfMissingTracks());
    }

    /// <summary>The scores the mood cut reads are the scores of the stored embedding, and a track
    /// measured as having nothing to hear has none.</summary>
    [Fact]
    public void The_feelings_are_the_scores_of_what_was_stored()
    {
        _db.Upsert(Track("a.flac"));
        _db.Upsert(Track("silent.flac"));
        var embedding = Embedding(3);
        _db.SaveTrackSound("a.flac", 100, 1, embedding);
        _db.SaveTrackSound("silent.flac", 100, 1, null);

        var feelings = _db.QuerySoundFeelings();

        var stored = Assert.Single(_db.QuerySoundEmbeddings()).Value;
        var scores = Assert.Single(feelings).Value;
        Assert.Equal(SoundPrompts.ScoreAll(stored), scores);
        Assert.Equal(SoundPrompts.Score(embedding, Feeling.Angry), scores.Angry, 3);
    }

    /// <summary>The holiday songs are found by their title, their album or their genre.</summary>
    [Fact]
    public void The_holiday_songs_are_found_by_their_words()
    {
        _db.Upsert(Track("carol.flac") with { Title = "Silent Night" });
        _db.Upsert(Track("album.flac") with { Title = "Track 1", Album = "Merry Christmas" });
        _db.Upsert(Track("genre.flac") with { Title = "Track 2", Genre = "Christmas" });
        _db.Upsert(Track("summer.flac") with { Title = "Dreadlock Holiday" });
        _db.Upsert(Track("plain.flac") with { Title = "Yesterday" });
        _db.Upsert(Track("abba.flac") with { Title = "Happy New Year" });

        var (holiday, newYear) = _db.QueryHolidayTracks();

        Assert.Equal(
            new[] { "abba.flac", "album.flac", "carol.flac", "genre.flac" },
            holiday.Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "abba.flac" }, newYear);
    }

    /// <summary>A holiday word is found in any case, accented letters included, and a place named after a
    /// saint is not a song about Father Christmas.</summary>
    [Fact]
    public void The_holiday_words_are_found_in_any_case()
    {
        _db.Upsert(Track("noel.flac") with { Title = "JOYEUX NOËL" });
        _db.Upsert(Track("nuevo.flac") with { Title = "Feliz Año Nuevo" });
        _db.Upsert(Track("nuevo-caps.flac") with { Title = "FELIZ AÑO NUEVO" });
        _db.Upsert(Track("jingle.flac") with { Title = "jingle bell rock" });
        _db.Upsert(Track("barbara.flac") with { Title = "Santa Barbara" });

        var (holiday, newYear) = _db.QueryHolidayTracks();

        Assert.Equal(
            new[] { "jingle.flac", "noel.flac", "nuevo-caps.flac", "nuevo.flac" },
            holiday.Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "nuevo-caps.flac", "nuevo.flac" }, newYear.Order(StringComparer.Ordinal));
    }
}
