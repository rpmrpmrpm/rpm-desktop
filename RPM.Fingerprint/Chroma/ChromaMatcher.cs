namespace RPM.Fingerprint.Chroma;

/// <summary>
/// Two-stage matcher:
///   1. Use the inverted index to find candidate songs (which songs share at least one key with
///      the query?). Vote by alignment offset to rank candidates.
///   2. For the top-K candidates, open the full fingerprint and compute the best Hamming distance
///      via sliding alignment. Lowest bits-per-frame wins, and the runner-up gives the margin.
///
/// Stage 1 is what makes this work at scale: no fingerprint is opened for a song that shares no
/// key with the query.
///
/// <para><b>Nothing here holds the corpus.</b> The index does not keep fingerprints. It keeps a
/// song ordinal and a position, eight bytes an entry. Stage 2 asks <see cref="IChromaFingerprints"/>
/// for the candidates it is about to score. A host that has everything in hand passes
/// <see cref="ResidentFingerprints"/> and pays nothing for the indirection. A host whose store is too
/// large to hold passes one that fetches. That is the whole difference between a matcher that fits in
/// a function and one that stops fitting as the collection grows.</para>
/// </summary>
public sealed class ChromaMatcher
{
    private readonly ChromaIndex _index;
    private readonly IChromaFingerprints _fingerprints;

    /// <summary>Song ids and titles by ordinal. One row per song rather than one per index entry,
    /// which is why the index can store four-byte ordinals instead of eight-byte ids.</summary>
    private readonly List<long> _songIdByOrdinal;
    private readonly List<string?> _titleByOrdinal;
    private readonly Dictionary<long, int> _ordinalOf;

    private readonly int _indexAnchorsPerSong;

    /// <summary>
    /// How an anchor becomes keys. Fixed when the index is built and not settable afterwards: the
    /// query has to derive keys the same way the index did, and a mismatch does not fail, it
    /// returns no candidates, which reads exactly like a store that has never heard the recording.
    /// </summary>
    public ChromaKeyScheme Scheme { get; }

    /// <summary>How many top candidates from stage 1 get the detailed Hamming check.</summary>
    public int CandidateLimit { get; init; } = 150;

    /// <summary>Width of the alignment search around the offset suggested by stage 1, in frames.</summary>
    public int AlignmentSearchRadius { get; init; } = 16;

    /// <summary>
    /// How many positions of the query stage 1 looks up. The query is sub-sampled to about this
    /// many, so what a lookup costs is bounded by the store rather than by how long the recording is.
    ///
    /// <para>A fixed stride of 4 is right for a four-minute track and wrong at both ends. A clip is the
    /// case that matters. Twenty seconds of audio contains about five of a recording's sixty anchors.
    /// Looking up every fourth frame throws away three quarters of the few chances to hit one. Bounding
    /// the number of samples instead keeps a four-minute query sampled as before, and looks up every
    /// frame of a clip. That costs little, because a clip has only about a hundred and sixty frames.
    /// Measured clip recall: 79 of 100 with a fixed stride of 4, and 100 of 100 with the bound.</para>
    /// </summary>
    public int MaxQuerySamples { get; init; } = 500;

    /// <summary>Set to true to print stage-level timing per Identify call.</summary>
    public bool DebugTiming { get; init; } = false;

    private ChromaMatcher(
        ChromaIndex index,
        IChromaFingerprints fingerprints,
        List<long> songIdByOrdinal,
        List<string?> titleByOrdinal,
        ChromaKeyScheme scheme,
        int indexAnchorsPerSong)
    {
        _index = index;
        _fingerprints = fingerprints;
        _songIdByOrdinal = songIdByOrdinal;
        _titleByOrdinal = titleByOrdinal;
        _ordinalOf = new Dictionary<long, int>(songIdByOrdinal.Count);
        for (int i = 0; i < songIdByOrdinal.Count; i++) _ordinalOf[songIdByOrdinal[i]] = i;
        Scheme = scheme;
        _indexAnchorsPerSong = indexAnchorsPerSong;
    }

    /// <summary>
    /// Collects a corpus into an index without keeping it.
    ///
    /// <para>Fed one recording at a time, so the caller can stream them. The function reads 13,332 rows
    /// from DynamoDB at cold start, and it must not still hold them when it is done. The fingerprint
    /// handed to <see cref="Add"/> is read and dropped.</para>
    /// </summary>
    public sealed class Builder(ChromaKeyScheme? scheme = null, int indexAnchorsPerSong = 60)
    {
        private readonly ChromaIndex.Builder _index = new(scheme ?? ChromaKeySchemes.Default);
        private readonly List<long> _songIds = new();
        private readonly List<string?> _titles = new();

        public ChromaKeyScheme Scheme { get; } = scheme ?? ChromaKeySchemes.Default;

        /// <inheritdoc cref="ChromaMatcher.MaxQuerySamples"/>
        public int MaxQuerySamples { get; init; } = 500;

        /// <inheritdoc cref="ChromaMatcher.CandidateLimit"/>
        public int CandidateLimit { get; init; } = 150;

        /// <inheritdoc cref="ChromaMatcher.DebugTiming"/>
        public bool DebugTiming { get; init; }

        public void Add(long songId, string? title, ChromaFingerprint fingerprint)
        {
            int ordinal = _songIds.Count;
            _songIds.Add(songId);
            _titles.Add(title);
            _index.Add(ordinal, fingerprint.SubFingerprints, indexAnchorsPerSong);
        }

        /// <summary>Files one index entry directly, for a caller reading an index somebody already
        /// wrote rather than deriving one from fingerprints.</summary>
        public int AddSongWithoutIndexing(long songId, string? title)
        {
            _songIds.Add(songId);
            _titles.Add(title);
            return _songIds.Count - 1;
        }

        /// <inheritdoc cref="AddSongWithoutIndexing"/>
        public void AddEntry(uint key, int songOrdinal, int position) => _index.AddEntry(key, songOrdinal, position);

        public ChromaMatcher Build(IChromaFingerprints fingerprints)
            => new(_index.Build(), fingerprints, _songIds, _titles, Scheme, indexAnchorsPerSong)
            {
                MaxQuerySamples = MaxQuerySamples,
                CandidateLimit = CandidateLimit,
                DebugTiming = DebugTiming,
            };
    }

    /// <summary>A matcher over a corpus small enough to hold, which is what the tests and the POC
    /// have and what a desktop has.</summary>
    public static ChromaMatcher OverResident(
        IReadOnlyDictionary<long, ChromaFingerprint> fingerprints,
        IReadOnlyDictionary<long, string> titles,
        ChromaKeyScheme? scheme = null,
        int indexAnchorsPerSong = 60)
    {
        var builder = new Builder(scheme, indexAnchorsPerSong);
        foreach (var (songId, fp) in fingerprints)
            builder.Add(songId, titles.GetValueOrDefault(songId), fp);
        return builder.Build(new ResidentFingerprints(fingerprints));
    }

    /// <summary>
    /// Files one more song into the live index, without rebuilding it.
    ///
    /// <para>For hosts that learn songs while they run. The Lambda stores back every fingerprint nobody
    /// has reported before, and the next report in the same batch has to be able to find it. Throwing the
    /// matcher away and rebuilding on the next lookup gives the right answer, but it costs one full
    /// rebuild per stored song. A batch of twenty-five new tracks would rebuild the index over the whole
    /// store twenty-five times, and that puts the request over the function's timeout as the store grows.
    /// This adds one song to the index with the same result, without a rebuild.</para>
    ///
    /// <para>In-memory only, and not thread-safe against a lookup in flight. The durable write is the
    /// caller's business, and the caller is expected to hold whatever lock guards the matcher.</para>
    /// </summary>
    public void Add(long songId, string? title, ChromaFingerprint fingerprint)
    {
        if (!_ordinalOf.TryGetValue(songId, out int ordinal))
        {
            ordinal = _songIdByOrdinal.Count;
            _songIdByOrdinal.Add(songId);
            _titleByOrdinal.Add(title);
            _ordinalOf[songId] = ordinal;
        }
        else
        {
            _titleByOrdinal[ordinal] = title;
        }

        // Handed to the source as well as the index: a candidate whose fingerprint cannot be
        // opened loses to recordings that are merely older.
        _fingerprints.Remember(songId, fingerprint);
        _index.Add(ordinal, fingerprint.SubFingerprints, Scheme, _indexAnchorsPerSong);
    }

    public int SongCount => _songIdByOrdinal.Count;

    /// <inheritdoc cref="ChromaIndex.Entries"/>
    public int IndexEntries => _index.Entries;

    /// <inheritdoc cref="ChromaIndex.Buckets"/>
    public int IndexBuckets => _index.Buckets;

    /// <inheritdoc cref="ChromaIndex.OverlayEntries"/>
    public int IndexOverlayEntries => _index.OverlayEntries;

    public async Task<ChromaMatchResult> IdentifyAsync(ChromaFingerprint query, CancellationToken ct = default)
    {
        if (query.Length == 0) return new ChromaMatchResult(null, null, double.MaxValue, 0, 0);

        var swStage = System.Diagnostics.Stopwatch.StartNew();

        // Stage 1: gather candidate songs from the index. For each sampled query frame, look up its
        // keys; any song carrying one is a candidate. Votes are bucketed by (song, offset) so a real
        // match concentrates at one offset while chance hits scatter.
        var votes = new Dictionary<(int Ordinal, int Offset), int>();
        Span<uint> keyBuf = stackalloc uint[Scheme.KeysPerAnchor];
        int totalHits = 0;
        int stride = Math.Max(1, query.Length / MaxQuerySamples);
        for (int qPos = 0; qPos < query.Length; qPos += stride)
        {
            int written = Scheme.Extract(query.SubFingerprints, qPos, keyBuf);
            for (int k = 0; k < written; k++)
            {
                var flat = _index.Flat(keyBuf[k]);
                totalHits += flat.Length;
                for (int h = 0; h < flat.Length; h++)
                {
                    var key = (flat.Songs[h], flat.Positions[h] - qPos);
                    votes.TryGetValue(key, out int prev);
                    votes[key] = prev + 1;
                }

                var overlay = _index.Overlay(keyBuf[k]);
                if (overlay is null) continue;
                totalHits += overlay.Count;
                foreach (var (song, pos) in overlay)
                {
                    var key = (song, pos - qPos);
                    votes.TryGetValue(key, out int prev);
                    votes[key] = prev + 1;
                }
            }
        }
        long stage1Ms = swStage.ElapsedMilliseconds;
        swStage.Restart();

        if (votes.Count == 0)
            return new ChromaMatchResult(null, null, double.MaxValue, 0, 0);

        // Best vote per song (collapse all offsets into the song's strongest single offset).
        var bestPerSong = new Dictionary<int, (int Offset, int Votes)>();
        foreach (var kv in votes)
        {
            var (ordinal, offset) = kv.Key;
            int v = kv.Value;
            if (!bestPerSong.TryGetValue(ordinal, out var cur) || v > cur.Votes)
                bestPerSong[ordinal] = (offset, v);
        }

        var candidates = bestPerSong
            .OrderByDescending(kv => kv.Value.Votes)
            .Take(CandidateLimit)
            .ToList();

        long stage15Ms = swStage.ElapsedMilliseconds;
        swStage.Restart();

        // The only place a fingerprint is opened, and the only place the store is asked for one:
        // every candidate at once, so a source that fetches makes one round trip rather than a
        // hundred and fifty.
        var wanted = candidates.Select(c => _songIdByOrdinal[c.Key]).ToList();
        var opened = await _fingerprints.GetAsync(wanted, ct).ConfigureAwait(false);

        // Stage 1 named recordings and the store answered for none of them. That is the store
        // failing, not an answer, and the two must not look alike. "No match" is what the caller acts on
        // by filing the query as a recording nobody has reported, so a store that has gone quiet would
        // have it duplicate the corpus one lookup at a time. This cannot happen while the fingerprints
        // are kept in memory, because a candidate exists only if its fingerprint does.
        if (opened.Count == 0)
            throw new ChromaStoreUnavailableException(wanted.Count);

        long fetchMs = swStage.ElapsedMilliseconds;
        swStage.Restart();

        // Stage 2: search a small window around the offset stage 1 predicted for the best Hamming
        // alignment. Best and runner-up are found in one pass. The runner-up is only needed for the margin.
        // Scoring the field twice to find it would double the most expensive part of the lookup.
        int bestOrdinal = -1;
        double bestBpf = double.MaxValue, secondBestBpf = double.MaxValue;
        int bestOverlap = 0, bestLongest = 0;

        foreach (var c in candidates)
        {
            if (!opened.TryGetValue(_songIdByOrdinal[c.Key], out var dbFp) || dbFp.Length == 0) continue;

            var (searchStart, searchEnd) = SearchRange(query.Length, dbFp.Length, c.Value.Offset, AlignmentSearchRadius);
            var (_, bpf, overlap) = ChromaFingerprint.BestAlignment(
                query.SubFingerprints, dbFp.SubFingerprints, searchStart, searchEnd);

            if (bpf < bestBpf)
            {
                secondBestBpf = bestBpf;
                bestBpf = bpf;
                bestOrdinal = c.Key;
                bestOverlap = overlap;
                bestLongest = Math.Max(query.Length, dbFp.Length);
            }
            else if (bpf < secondBestBpf)
            {
                secondBestBpf = bpf;
            }
        }

        long stage2Ms = swStage.ElapsedMilliseconds;
        if (DebugTiming)
        {
            Console.Error.WriteLine($"  [matcher] stage1(votes)={stage1Ms}ms hits={totalHits} votes={votes.Count}, " +
                                    $"stage1.5(rank)={stage15Ms}ms cands={candidates.Count}, " +
                                    $"fetch={fetchMs}ms, stage2(top-K hamming + margin)={stage2Ms}ms");
        }

        if (bestOrdinal < 0)
            return new ChromaMatchResult(null, null, double.MaxValue, 0, candidates.Count);

        double margin = secondBestBpf == double.MaxValue ? double.MaxValue : secondBestBpf - bestBpf;
        return new ChromaMatchResult(
            _songIdByOrdinal[bestOrdinal], _titleByOrdinal[bestOrdinal],
            bestBpf, bestOverlap, candidates.Count, margin, bestLongest);
    }

    /// <summary>
    /// Offsets worth searching: those where the query fits entirely inside the stored recording, or
    /// the other way round when the stored one is shorter. Without the constraint
    /// <see cref="ChromaFingerprint.BestAlignment"/> finds tiny edge overlaps with an accidentally
    /// low Hamming distance and scores them best.
    /// </summary>
    private static (int Start, int End) SearchRange(int queryLength, int dbLength, int predictedOffset, int radius)
    {
        int safeStart, safeEnd;
        if (dbLength >= queryLength) { safeStart = 0; safeEnd = dbLength - queryLength; }
        else { safeStart = -(queryLength - dbLength); safeEnd = 0; }

        if (radius < 0) return (safeStart, safeEnd);

        int start = Math.Max(safeStart, predictedOffset - radius);
        int end = Math.Min(safeEnd, predictedOffset + radius);
        // The predicted offset can fall outside the safe range; scan all of it rather than nothing.
        return end < start ? (safeStart, safeEnd) : (start, end);
    }

    /// <summary>
    /// Brute-force matcher: opens every recording and finds the best Hamming alignment.
    /// O(songs × query frames × search radius). Slow at scale but unambiguous about whether the
    /// fingerprint algorithm is producing correct results. It is used to check the indexed answer
    /// independently of the index.
    /// </summary>
    public async Task<ChromaMatchResult> IdentifyBruteForceAsync(ChromaFingerprint query, CancellationToken ct = default)
    {
        var ranked = await IdentifyBruteForceTopKAsync(query, k: 2, ct).ConfigureAwait(false);
        if (ranked.Count == 0) return new ChromaMatchResult(null, null, double.MaxValue, 0, SongCount);

        var best = ranked[0];
        double margin = ranked.Count > 1 ? ranked[1].BitsPerFrame - best.BitsPerFrame : double.MaxValue;
        return best with { CandidatesEvaluated = SongCount, MarginToRunnerUp = margin };
    }

    /// <summary>Top-K candidates from a brute-force scan, ordered best first.</summary>
    public async Task<List<ChromaMatchResult>> IdentifyBruteForceTopKAsync(
        ChromaFingerprint query, int k = 5, CancellationToken ct = default)
    {
        var all = new List<ChromaMatchResult>();
        if (query.Length == 0) return all;

        // In chunks, because the point of the source is that the whole corpus need not be in hand
        // at once, including here, where every recording is wanted eventually.
        const int Chunk = 256;
        for (int from = 0; from < _songIdByOrdinal.Count; from += Chunk)
        {
            var ids = _songIdByOrdinal.Skip(from).Take(Chunk).ToList();
            var opened = await _fingerprints.GetAsync(ids, ct).ConfigureAwait(false);
            for (int i = 0; i < ids.Count; i++)
            {
                if (!opened.TryGetValue(ids[i], out var dbFp) || dbFp.Length == 0) continue;

                var (start, end) = SearchRange(query.Length, dbFp.Length, 0, radius: -1);
                var (_, bpf, overlap) = ChromaFingerprint.BestAlignment(
                    query.SubFingerprints, dbFp.SubFingerprints, start, end);

                all.Add(new ChromaMatchResult(
                    ids[i], _titleByOrdinal[from + i], bpf, overlap, _songIdByOrdinal.Count,
                    LongestFrames: Math.Max(query.Length, dbFp.Length)));
            }
        }
        return all.OrderBy(r => r.BitsPerFrame).Take(k).ToList();
    }
}

/// <summary>
/// Stage 1 named candidates and the store could not open any of them.
///
/// <para>Separate from a lookup that found nothing, because the caller does opposite things with
/// them: nothing found means file this recording as one nobody has reported, and doing that
/// because the store was unreachable would duplicate the corpus.</para>
/// </summary>
public sealed class ChromaStoreUnavailableException(int candidates)
    : Exception($"The fingerprint store answered for none of the {candidates} candidates stage 1 named.")
{
    /// <summary>How many recordings were asked for.</summary>
    public int Candidates { get; } = candidates;
}

/// <summary>
/// Result of a chroma match.
/// </summary>
/// <param name="SongId">winning song id, or null if no candidates were found</param>
/// <param name="Title">winning song title</param>
/// <param name="BitsPerFrame">average Hamming distance per frame in the best alignment. Lower = better.
///   For 32-bit sub-fingerprints, expect &lt; ~10 for a real match, &gt; ~14 for noise.</param>
/// <param name="OverlapFrames">how many frames overlapped at the best alignment</param>
/// <param name="CandidatesEvaluated">how many candidate songs we Hamming-scored</param>
/// <param name="MarginToRunnerUp">runner_up_bpf - winner_bpf. Larger = more confident.
///   Use this (not raw bpf) as the cache-miss decision threshold.</param>
/// <param name="LongestFrames">the longer of the two fingerprints compared. With
///   <paramref name="OverlapFrames"/> this says how much of the pair the agreement actually
///   covers, which <see cref="ChromaMatchResult.IsConfident"/> needs and bits-per-frame cannot say.</param>
public sealed record ChromaMatchResult(
    long? SongId,
    string? Title,
    double BitsPerFrame,
    int OverlapFrames,
    int CandidatesEvaluated,
    double MarginToRunnerUp = 0.0,
    int LongestFrames = 0)
{
    /// <summary>
    /// How much of the longer of the two recordings the agreement covers, 0..1.
    ///
    /// <para>The alignment slides the shorter fingerprint along the longer one and keeps the best
    /// position, so a short recording is scored on its best window anywhere in a long one. That is
    /// the right way to find a track inside a set, and the wrong way to decide two recordings are
    /// the same record.</para>
    /// </summary>
    public double Coverage => LongestFrames > 0 ? (double)OverlapFrames / LongestFrames : 1;

    /// <summary>
    /// How much of the pair a match has to cover before it is a claim about the whole recording.
    ///
    /// <para>Measured. Over one 742-track library, a whole-record comparison separates cleanly. Two copies
    /// of one recording score 0.00 bits per frame. Unrelated records sit at 13 to 16, against a gate of
    /// 12. There were zero false matches in 199 comparisons. Short references do not separate at all,
    /// because the alignment picks their best window out of an entire song. A short intro of 18.9 seconds
    /// scored under the gate against <b>155 of those 742 tracks</b>, one library track in five, and its id
    /// was handed to every one of them. Downstream, that id is a claim that they are all one record. The
    /// shared analysis pool then fused 58 unrelated recordings and served one reading for all of them.</para>
    ///
    /// <para>Half is the loosest value that rules out such matches. Two rips of one CD overlap almost
    /// completely. A radio edit against an album version is the shortest thing that is honestly the same
    /// recording, and those cover about seventy percent. A short intro covers about three percent.</para>
    /// </summary>
    public const double MinCoverage = 0.5;

    /// <summary>
    /// True if the match is confident enough to trust without falling back to a paid identification
    /// call. Threshold tuned to: clearly winning (margin ≥ ~0.5 bpf) AND not pure noise (bpf ≤ ~12).
    ///
    /// <para>This answers "what is this?" about a clip: a few seconds from a microphone, or a snatch of a
    /// set. So it must not ask about coverage. A clip covers almost none of the record it came from, and
    /// that is the whole point of asking. For the other question, whether two whole recordings are the
    /// same record, see <see cref="IsSameRecording"/>.</para>
    /// </summary>
    public bool IsConfident => SongId is not null && BitsPerFrame <= 12.0 && MarginToRunnerUp >= 0.5;

    /// <summary>
    /// True when this is a match between two whole recordings rather than a moment found inside
    /// one: everything <see cref="IsConfident"/> asks, and enough of both fingerprints covered.
    ///
    /// <para>The two questions have to be told apart, because their answers are used for opposite
    /// things. A confident clip match names a track for somebody. A same-recording match mints the
    /// song id, and that id is a claim to the whole system that two files are one record. It is the key
    /// the shared analysis pool joins on, so a wrong one does not merely mislabel a track. It hands the
    /// track another record's grid, tempo, key and cues.</para>
    /// </summary>
    public bool IsSameRecording => IsConfident && Coverage >= MinCoverage;
}
