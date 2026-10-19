using RPM.Fingerprint.Chroma;

namespace RPM.Fingerprint.Tests;

/// <summary>
/// Identification: a few seconds of audio against a library, and the confidence that decides whether
/// the answer is trusted or a paid lookup is made instead.
///
/// <para>The fingerprints here are synthetic. Their frames drift a few bits at a time, which is the
/// shape real chroma has. So these tests say what the matcher does with a library, not how well
/// Chromaprint hears. That distinction matters: a real-audio test would fail for a new ffmpeg build,
/// while these fail only when the matching itself changes.</para>
/// </summary>
public class ChromaMatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"chromamatch-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>A song's worth of frames: a random start, drifting three bits a frame.</summary>
    private static uint[] Song(int frames, int seed)
    {
        var random = new Random(seed);
        var song = new uint[frames];
        song[0] = (uint)random.Next(int.MinValue, int.MaxValue);
        for (var i = 1; i < frames; i++)
        {
            var next = song[i - 1];
            for (var bit = 0; bit < 3; bit++)
                next ^= 1u << random.Next(32);
            song[i] = next;
        }
        return song;
    }

    /// <summary>A clip off a song, with one bit a frame flipped. It is a recording heard again, not the
    /// same bytes read twice.</summary>
    private static uint[] ClipWithNoise(uint[] song, int from, int length, int seed = 91)
    {
        var clip = song[from..(from + length)];
        var noise = new Random(seed);
        for (var i = 0; i < clip.Length; i++)
            clip[i] ^= 1u << noise.Next(32);
        return clip;
    }

    private static (Dictionary<long, ChromaFingerprint> Fingerprints, Dictionary<long, string> Titles) Library(int songs = 8)
    {
        var fingerprints = new Dictionary<long, ChromaFingerprint>();
        var titles = new Dictionary<long, string>();
        for (var i = 1; i <= songs; i++)
        {
            fingerprints[i] = new ChromaFingerprint(Song(1200, seed: i * 17));
            titles[i] = $"Track {i}";
        }
        return (fingerprints, titles);
    }

    [Fact]
    public async Task A_clip_is_identified_as_the_song_it_was_taken_from()
    {
        var (fingerprints, titles) = Library();
        var matcher = ChromaMatcher.OverResident(fingerprints, titles);
        var clip = ClipWithNoise(fingerprints[5].SubFingerprints, from: 400, length: 250);

        var result = await matcher.IdentifyAsync(new ChromaFingerprint(clip));

        Assert.Equal(5, result.SongId);
        Assert.Equal("Track 5", result.Title);
        Assert.Equal(250, result.OverlapFrames);
        Assert.True(result.IsConfident, $"a clip of its own song scored {result.BitsPerFrame:F2} bpf, margin {result.MarginToRunnerUp:F2}");
    }

    /// <summary>
    /// The answer that costs money. A clip of something the library has never heard still lands in
    /// index buckets by chance, because eight-bit windows collide. So the matcher does name a nearest
    /// song. What has to hold is that it says it is not sure, because that flag is what stands
    /// between a wrong title and a paid identification call.
    /// </summary>
    /// <summary>
    /// A store that answers for none of the candidates is a store that is down. It must not read as
    /// "nothing matched". The caller files a recording nobody has reported when it hears that, so a
    /// quiet store would make it duplicate the corpus one lookup at a time. This failure only happens
    /// when the fingerprints are read from the store. While they are kept in memory, a candidate
    /// exists only if its fingerprint does.
    /// </summary>
    [Fact]
    public async Task A_store_that_answers_for_nothing_is_not_the_same_as_nothing_matching()
    {
        var (fingerprints, titles) = Library();
        var builder = new ChromaMatcher.Builder();
        foreach (var (songId, fp) in fingerprints)
            builder.Add(songId, titles[songId], fp);
        // The index knows every recording; the store has gone quiet.
        var matcher = builder.Build(new ResidentFingerprints(new Dictionary<long, ChromaFingerprint>()));

        var clip = new ChromaFingerprint(ClipWithNoise(fingerprints[5].SubFingerprints, from: 400, length: 250));

        await Assert.ThrowsAsync<ChromaStoreUnavailableException>(() => matcher.IdentifyAsync(clip));
    }

    /// <summary>A query nothing in the index carries is still answered with nothing, rather than
    /// with the exception above: stage 1 named no candidates, so the store was never asked.</summary>
    [Fact]
    public async Task A_query_the_index_does_not_carry_is_answered_with_nothing()
    {
        var (fingerprints, titles) = Library(songs: 2);
        var builder = new ChromaMatcher.Builder();
        foreach (var (songId, fp) in fingerprints)
            builder.Add(songId, titles[songId], fp);
        var matcher = builder.Build(new ResidentFingerprints(fingerprints));

        var result = await matcher.IdentifyAsync(new ChromaFingerprint([]));

        Assert.Null(result.SongId);
    }

    /// <summary>
    /// Counts what stage 2 asks the store for. The whole reason a matcher can outgrow the memory it
    /// runs in is that this is the candidate set and not the corpus.
    /// </summary>
    private sealed class CountingFingerprints(IReadOnlyDictionary<long, ChromaFingerprint> held) : IChromaFingerprints
    {
        private readonly ResidentFingerprints _inner = new(held);
        public int Calls { get; private set; }
        public int Asked { get; private set; }

        public ValueTask<IReadOnlyDictionary<long, ChromaFingerprint>> GetAsync(
            IReadOnlyList<long> songIds, CancellationToken ct = default)
        {
            Calls++;
            Asked += songIds.Count;
            return _inner.GetAsync(songIds, ct);
        }

        public void Remember(long songId, ChromaFingerprint fingerprint) => _inner.Remember(songId, fingerprint);
    }

    /// <summary>
    /// A lookup opens the candidates stage 1 named, in one ask, and never the whole corpus. This is
    /// the property that lets the store outgrow the memory the matcher runs in: at 13,332 songs the
    /// candidates are 150 fingerprints rather than 13,332, and at 200,000 they are still 150.
    /// </summary>
    [Fact]
    public async Task A_lookup_opens_the_candidates_and_not_the_corpus()
    {
        var (fingerprints, titles) = Library(songs: 60);
        var source = new CountingFingerprints(fingerprints);
        var builder = new ChromaMatcher.Builder { CandidateLimit = 5 };
        foreach (var (songId, fp) in fingerprints)
            builder.Add(songId, titles[songId], fp);
        var matcher = builder.Build(source);

        var result = await matcher.IdentifyAsync(
            new ChromaFingerprint(ClipWithNoise(fingerprints[5].SubFingerprints, from: 400, length: 250)));

        Assert.Equal(5, result.SongId);
        Assert.Equal(1, source.Calls);
        Assert.InRange(source.Asked, 1, 5);
    }

    /// <summary>
    /// A song stored back mid-batch is a candidate for the rest of that batch, and its row has not
    /// necessarily been written yet. Filing it has to hand the fingerprint to the source as well as
    /// to the index, or it is a candidate nothing can open and it loses to recordings that are
    /// merely older.
    /// </summary>
    [Fact]
    public async Task A_song_filed_while_running_can_be_scored_before_its_row_is_written()
    {
        var (fingerprints, titles) = Library();
        // The source is given the standing corpus and never learns anything else on its own.
        var source = new CountingFingerprints(fingerprints);
        var builder = new ChromaMatcher.Builder();
        foreach (var (songId, fp) in fingerprints)
            builder.Add(songId, titles[songId], fp);
        var matcher = builder.Build(source);

        var newcomer = Song(1200, seed: 909);
        matcher.Add(99, "Filed while running", new ChromaFingerprint(newcomer));

        var result = await matcher.IdentifyAsync(
            new ChromaFingerprint(ClipWithNoise(newcomer, from: 300, length: 250)));

        Assert.Equal(99, result.SongId);
        Assert.True(result.IsConfident,
                    $"a song filed a moment ago scored {result.BitsPerFrame:F2} bpf, margin {result.MarginToRunnerUp:F2}");
    }

    /// <summary>
    /// Every scheme has to answer this, because a scheme is only ever judged on whether a lookup
    /// finds what the index holds. A mismatch between the two does not fail: it returns no
    /// candidates, which is what a store that has never heard the recording looks like.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_clip_is_identified_under_every_key_scheme(int schemeId)
    {
        var scheme = ChromaKeySchemes.ById(schemeId);
        var (fingerprints, titles) = Library();
        var matcher = ChromaMatcher.OverResident(fingerprints, titles, scheme);
        var clip = ClipWithNoise(fingerprints[5].SubFingerprints, from: 400, length: 250);

        var result = await matcher.IdentifyAsync(new ChromaFingerprint(clip));

        Assert.Same(scheme, matcher.Scheme);
        Assert.Equal(5, result.SongId);
        Assert.True(result.IsConfident,
                    $"{scheme} scored its own song at {result.BitsPerFrame:F2} bpf, margin {result.MarginToRunnerUp:F2}");
    }

    /// <summary>
    /// A song filed into the standing index has to be findable by the very next lookup. The service
    /// stores back every fingerprint nobody has reported before and then matches the rest of the
    /// same batch against a store that now contains it.
    /// </summary>
    [Fact]
    public async Task A_song_filed_into_the_live_index_is_found_by_the_next_lookup()
    {
        var (fingerprints, titles) = Library();
        var matcher = ChromaMatcher.OverResident(fingerprints, titles);
        var newcomer = Song(1200, seed: 909);

        var before = await matcher.IdentifyAsync(new ChromaFingerprint(newcomer));
        Assert.False(before.SongId == 99, "the newcomer was already known, so this proves nothing");
        matcher.Add(99, "Filed while running", new ChromaFingerprint(newcomer));

        var result = await matcher.IdentifyAsync(new ChromaFingerprint(ClipWithNoise(newcomer, from: 300, length: 250)));

        Assert.Equal(99, result.SongId);
        Assert.True(result.IsConfident,
                    $"a song filed a moment ago scored {result.BitsPerFrame:F2} bpf, margin {result.MarginToRunnerUp:F2}");
    }

    /// <summary>Filing one song must leave the index in the state a rebuild over the whole store
    /// would have produced, or entries filed while running drift from everything around them.</summary>
    [Fact]
    public async Task Filing_a_song_leaves_the_index_a_rebuild_would_have_produced()
    {
        var (fingerprints, titles) = Library();
        var newcomer = new ChromaFingerprint(Song(1200, seed: 909));

        var filed = ChromaMatcher.OverResident(fingerprints, titles);
        filed.Add(99, "Filed while running", newcomer);

        var rebuilt = ChromaMatcher.OverResident(
            fingerprints.Append(new KeyValuePair<long, ChromaFingerprint>(99, newcomer)).ToDictionary(kv => kv.Key, kv => kv.Value),
            titles.Append(new KeyValuePair<long, string>(99, "Filed while running")).ToDictionary(kv => kv.Key, kv => kv.Value));

        Assert.Equal(rebuilt.IndexEntries, filed.IndexEntries);
        Assert.Equal(rebuilt.IndexBuckets, filed.IndexBuckets);
    }

    [Fact]
    public async Task A_recording_the_library_has_never_heard_is_not_answered_confidently()
    {
        var (fingerprints, titles) = Library();
        var matcher = ChromaMatcher.OverResident(fingerprints, titles);

        var stranger = await matcher.IdentifyAsync(new ChromaFingerprint(Song(250, seed: 4242)));

        Assert.False(stranger.IsConfident,
                     $"a stranger was claimed at {stranger.BitsPerFrame:F2} bpf, margin {stranger.MarginToRunnerUp:F2}");
    }

    [Fact]
    public async Task Nothing_to_identify_is_answered_with_nothing()
    {
        var (fingerprints, titles) = Library(songs: 2);
        var matcher = ChromaMatcher.OverResident(fingerprints, titles);

        var nothing = await matcher.IdentifyAsync(new ChromaFingerprint([]));

        Assert.Null(nothing.SongId);
        Assert.False(nothing.IsConfident);
        Assert.Equal(double.MaxValue, nothing.BitsPerFrame);
    }

    /// <summary>
    /// The confidence rule, stated where it can be read. Both halves are load-bearing: a low distance
    /// with no margin is two pressings of the same recording, and a large margin at a high distance is
    /// the best of a field of strangers.
    /// </summary>
    [Theory]
    [InlineData(4.0, 3.0, true)]     // close, and clearly ahead
    [InlineData(4.0, 0.2, false)]    // close, but something else is just as close
    [InlineData(13.0, 5.0, false)]   // clearly ahead of a field of noise, and noise itself
    [InlineData(12.0, 0.5, true)]    // exactly on both thresholds
    public async Task Confidence_needs_both_a_close_match_and_a_clear_winner(double bitsPerFrame, double margin, bool confident)
    {
        var result = new ChromaMatchResult(7, "Track 7", bitsPerFrame, 250, 4, margin);

        Assert.Equal(confident, result.IsConfident);
    }

    [Fact]
    public async Task A_match_with_no_song_behind_it_is_never_confident()
    {
        Assert.False(new ChromaMatchResult(null, null, 0.0, 500, 0, double.MaxValue).IsConfident);
    }

    /// <summary>
    /// A short intro can match a long recording on distance alone. This test pins the coverage rule
    /// that prevents it.
    ///
    /// <para>The alignment slides a stored fingerprint along the query and keeps its best position. A
    /// short clip can then be scored against the best window of a long song, and it can pass the
    /// confidence gate. The shared analysis pool joins recordings on the song id, so a false match
    /// makes the wrong recording reuse another recording's grid, tempo, key and cues. The distance and
    /// the margin cannot detect this. Only the share of the song that was covered can.</para>
    /// </summary>
    [Theory]
    [InlineData(4000, 131, false)]    // an 18-second intro against a four-minute record: 3%
    [InlineData(4000, 1200, false)]   // a passage that lines up, not a recording: 30%
    [InlineData(4000, 2000, true)]    // a radio edit against the album version: 50%
    [InlineData(4000, 2800, true)]    // the same, with a shorter fade: 70%
    [InlineData(4000, 4000, true)]    // two rips of one CD
    public async Task A_song_id_needs_the_match_to_cover_the_record(int longest, int overlap, bool same)
    {
        // Close and clearly ahead on both thresholds, so only coverage decides.
        var result = new ChromaMatchResult(2209, "Katie Melua — Intro", 4.0, overlap, 4, 3.0, longest);

        Assert.True(result.IsConfident, "the clip question is unchanged and must stay unchanged");
        Assert.Equal(same, result.IsSameRecording);
    }

    [Fact]
    public async Task A_result_that_never_said_how_long_the_records_were_is_not_held_against_them()
    {
        // Results saved without a length must still be accepted. Reading a missing length as
        // no coverage would refuse all of them.
        var old = new ChromaMatchResult(7, "Track 7", 4.0, 250, 4, 3.0);

        Assert.True(old.IsSameRecording);
    }

    /// <summary>
    /// The index exists to keep the matcher off songs that share nothing with the query, so the two
    /// paths have to agree about who wins. Otherwise the fast path is fast and wrong, and the only
    /// way anyone would find out is a user reporting the wrong title.
    /// </summary>
    [Fact]
    public async Task The_indexed_answer_is_the_answer_a_full_scan_gives()
    {
        var (fingerprints, titles) = Library();
        var matcher = ChromaMatcher.OverResident(fingerprints, titles);
        var clip = new ChromaFingerprint(ClipWithNoise(fingerprints[3].SubFingerprints, from: 700, length: 300));

        var indexed = await matcher.IdentifyAsync(clip);
        var scanned = await matcher.IdentifyBruteForceAsync(clip);

        Assert.Equal(scanned.SongId, indexed.SongId);
        Assert.Equal(scanned.BitsPerFrame, indexed.BitsPerFrame, 6);
    }

    [Fact]
    public async Task The_full_scan_ranks_the_field_best_first()
    {
        var (fingerprints, titles) = Library();
        var matcher = ChromaMatcher.OverResident(fingerprints, titles);
        var clip = new ChromaFingerprint(ClipWithNoise(fingerprints[2].SubFingerprints, from: 100, length: 300));

        var top = await matcher.IdentifyBruteForceTopKAsync(clip, k: 3);

        Assert.Equal(3, top.Count);
        Assert.Equal(2, top[0].SongId);
        Assert.Equal(top.OrderBy(r => r.BitsPerFrame).Select(r => r.SongId), top.Select(r => r.SongId));
    }

    [Fact]
    public async Task A_full_scan_of_an_empty_query_ranks_nothing()
    {
        var (fingerprints, titles) = Library(songs: 3);
        var matcher = ChromaMatcher.OverResident(fingerprints, titles);

        Assert.Empty(await matcher.IdentifyBruteForceTopKAsync(new ChromaFingerprint([]), k: 5));
        Assert.Null((await matcher.IdentifyBruteForceAsync(new ChromaFingerprint([]))).SongId);
    }

    /// <summary>
    /// The same identification, over the store the desktop actually keeps. The in-memory constructor
    /// builds its own index; this one reads the index the store wrote, and the two schemes have to be
    /// the same scheme. A matcher that indexed windows differently from the writer would find no
    /// candidates at all and report every song as unknown.
    /// </summary>
    [Fact]
    public async Task A_clip_is_identified_against_the_database_on_disk_too()
    {
        var path = Path.Combine(_root, "library.db");
        var wanted = Song(1500, seed: 71);

        using var store = new ChromaStore(path);
        store.AddSong("Decoy A", null, 0, new ChromaFingerprint(Song(1500, seed: 12)));
        var wantedId = store.AddSong("The Wanted One", "/music/wanted.flac", 60, new ChromaFingerprint(wanted));
        store.AddSong("Decoy B", null, 0, new ChromaFingerprint(Song(1500, seed: 33)));

        var matcher = store.OpenMatcher();
        var result = await matcher.IdentifyAsync(new ChromaFingerprint(ClipWithNoise(wanted, from: 600, length: 300)));

        Assert.Equal(wantedId, result.SongId);
        Assert.Equal("The Wanted One", result.Title);
        Assert.True(result.IsConfident, $"scored {result.BitsPerFrame:F2} bpf, margin {result.MarginToRunnerUp:F2}");
    }

    [Fact]
    public async Task An_empty_database_identifies_nothing_rather_than_failing()
    {
        using var store = new ChromaStore(Path.Combine(_root, "empty.db"));
        var matcher = store.OpenMatcher();

        var result = await matcher.IdentifyAsync(new ChromaFingerprint(Song(200, seed: 5)));

        Assert.Null(result.SongId);
        Assert.False(result.IsConfident);
    }
}
