using RPM.Fingerprint.Chroma;

namespace RPM.Fingerprint.Tests;

/// <summary>
/// The fingerprint database: what it keeps, and the index that decides which songs are even looked
/// at when something is identified.
/// </summary>
public class ChromaStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"chromastore-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>A path inside a folder that does not exist yet. The store is expected to make it.</summary>
    private string DbPath(string name = "chroma.db") => Path.Combine(_root, "nested", name);

    private static uint[] Frames(int count, int seed = 11)
    {
        var random = new Random(seed);
        var frames = new uint[count];
        frames[0] = (uint)random.Next();
        for (var i = 1; i < count; i++)
        {
            var flipped = frames[i - 1];
            for (var bit = 0; bit < 3; bit++)
                flipped ^= 1u << random.Next(32);
            frames[i] = flipped;
        }
        return frames;
    }

    [Fact]
    public void A_song_comes_back_out_of_the_database_bit_for_bit()
    {
        var frames = Frames(2000);
        using var store = new ChromaStore(DbPath());

        var id = store.AddSong("Test Tone", "/music/tone.flac", 123.5, new ChromaFingerprint(frames));

        Assert.Equal(frames, store.LoadFingerprint(id).SubFingerprints);
        Assert.Equal("Test Tone", store.GetSongTitle(id));
        Assert.Equal(1, store.SongCount());
    }

    /// <summary>SQLite makes files, not folders. A store asked for a path in a folder nobody has
    /// created yet is the first run on a new machine.</summary>
    [Fact]
    public void Opening_a_database_makes_the_folder_it_is_asked_for()
    {
        var path = DbPath("deep.db");
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));

        using var store = new ChromaStore(path);

        Assert.True(File.Exists(path));
        Assert.Equal(0, store.SongCount());
    }

    [Fact]
    public void The_song_list_carries_what_was_stored_with_each_one()
    {
        using var store = new ChromaStore(DbPath());
        var first = store.AddSong("First", "/music/first.flac", 200.5, new ChromaFingerprint(Frames(300)));
        var second = store.AddSong("Second", sourcePath: null, durationSec: 0, new ChromaFingerprint(Frames(120, seed: 2)));

        var songs = store.ListSongs();

        Assert.Equal([first, second], songs.Select(s => s.Id));
        Assert.Equal("First", songs[0].Title);
        Assert.Equal("/music/first.flac", songs[0].SourcePath);
        Assert.Equal(200.5, songs[0].DurationSec, 3);
        Assert.Equal(300, songs[0].FpLength);

        // A song with no file behind it, such as an imported fingerprint, keeps the null rather than an
        // empty string, because "we never had the file" is not "the file is at ''".
        Assert.Null(songs[1].SourcePath);
        Assert.Equal(0, songs[1].DurationSec, 3);
    }

    [Fact]
    public void The_stored_size_is_the_compressed_size_not_the_frame_count()
    {
        var frames = Frames(4000);
        using var store = new ChromaStore(DbPath());
        store.AddSong("Only", null, 0, new ChromaFingerprint(frames));

        var bytes = store.TotalFingerprintBytes();

        Assert.True(bytes > 0);
        Assert.True(bytes < frames.Length * 4, $"{bytes} bytes stored for {frames.Length} frames — nothing was compressed");
    }

    [Fact]
    public void Every_song_indexes_the_anchors_it_was_told_to()
    {
        var scheme = ChromaKeySchemes.Default;
        using var store = new ChromaStore(DbPath(), indexAnchorsPerSong: 10);
        store.AddSong("Indexed", null, 0, new ChromaFingerprint(Frames(1000)));

        var index = store.LoadIndexIntoMemory();

        Assert.Equal(10 * scheme.KeysPerAnchor, index.Values.Sum(hits => hits.Count));
    }

    /// <summary>
    /// The index is derived data and the fingerprints are not, so a build that reads keys one way
    /// can always rewrite an index written the other way. It has to: keys from two schemes do not
    /// collide, so reading one with the other finds no candidates at all, silently, and the only
    /// symptom would be an identification rate that quietly went to zero.
    /// </summary>
    [Fact]
    public void An_index_written_under_another_scheme_is_rebuilt_rather_than_read()
    {
        var path = DbPath("scheme-change.db");
        using (var old = new ChromaStore(path, ChromaKeySchemes.ByteWindows, indexAnchorsPerSong: 10))
        {
            old.AddSong("Filed the old way", null, 0, new ChromaFingerprint(Frames(1000)));
            Assert.Equal(10 * ChromaKeySchemes.ByteWindows.KeysPerAnchor,
                         old.LoadIndexIntoMemory().Values.Sum(hits => hits.Count));
        }

        using var reopened = new ChromaStore(path, ChromaKeySchemes.HalfWords, indexAnchorsPerSong: 10);

        Assert.Equal(10 * ChromaKeySchemes.HalfWords.KeysPerAnchor,
                     reopened.LoadIndexIntoMemory().Values.Sum(hits => hits.Count));
        Assert.Equal(1, reopened.SongCount());
    }

    /// <summary>
    /// An all-zero or all-ones frame is silence or clipping, and every song has some. Indexing them
    /// would put every song in the library in the same bucket, which is a candidate list that costs
    /// the price of a full scan and says nothing.
    /// </summary>
    [Fact]
    public void Frames_that_say_nothing_are_kept_out_of_the_index()
    {
        var silence = new uint[500];                       // every frame zero
        var clipping = new uint[500];
        Array.Fill(clipping, uint.MaxValue);

        using var store = new ChromaStore(DbPath());
        var silent = store.AddSong("Silence", null, 0, new ChromaFingerprint(silence));
        var loud = store.AddSong("Clipping", null, 0, new ChromaFingerprint(clipping));

        Assert.Empty(store.LoadIndexIntoMemory());

        // The songs are still stored in full. Only the index turns them away.
        Assert.Equal(2, store.SongCount());
        Assert.Equal(silence, store.LoadFingerprint(silent).SubFingerprints);
        Assert.Equal(clipping, store.LoadFingerprint(loud).SubFingerprints);
    }

    [Fact]
    public void A_song_shorter_than_the_anchor_count_indexes_every_frame_it_has()
    {
        using var store = new ChromaStore(DbPath(), indexAnchorsPerSong: 60);
        store.AddSong("Short", null, 0, new ChromaFingerprint(Frames(5)));

        Assert.Equal(5 * ChromaKeySchemes.Default.KeysPerAnchor,
                     store.LoadIndexIntoMemory().Values.Sum(hits => hits.Count));
    }

    [Fact]
    public void Loading_every_fingerprint_gives_back_each_song_under_its_own_id()
    {
        var first = Frames(400);
        var second = Frames(250, seed: 5);
        using var store = new ChromaStore(DbPath());
        var firstId = store.AddSong("First", null, 0, new ChromaFingerprint(first));
        var secondId = store.AddSong("Second", null, 0, new ChromaFingerprint(second));

        var all = store.LoadAllFingerprintsIntoMemory();

        Assert.Equal(2, all.Count);
        Assert.Equal(first, all[firstId].SubFingerprints);
        Assert.Equal(second, all[secondId].SubFingerprints);
    }

    [Fact]
    public void Asking_for_a_song_that_is_not_there_is_answered_rather_than_thrown()
    {
        using var store = new ChromaStore(DbPath());

        Assert.Equal(0, store.LoadFingerprint(404).Length);
        Assert.Null(store.GetSongTitle(404));
        Assert.Equal(0, store.TotalFingerprintBytes());
    }

    /// <summary>The database is a file on disk, and the point of it is that a second run does not
    /// have to fingerprint the library again.</summary>
    [Fact]
    public void What_was_stored_is_still_there_after_the_store_is_closed()
    {
        var path = DbPath();
        var frames = Frames(600);
        long id;
        using (var store = new ChromaStore(path))
            id = store.AddSong("Kept", "/music/kept.flac", 42, new ChromaFingerprint(frames));

        using var reopened = new ChromaStore(path);

        Assert.Equal(1, reopened.SongCount());
        Assert.Equal(frames, reopened.LoadFingerprint(id).SubFingerprints);
        Assert.NotEmpty(reopened.LoadIndexIntoMemory());
    }
}
