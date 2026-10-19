namespace RPM.Fingerprint.Chroma;

/// <summary>
/// The stage-1 inverted index: for a key, which recordings carry it and where.
///
/// <para><b>Flat arrays, not a dictionary of lists.</b> A key produced by
/// <see cref="ChromaKeyScheme"/> is already a number below <see cref="ChromaKeyScheme.KeySpace"/>.
/// So it is its own bucket number, and nothing needs hashing. <see cref="_start"/> holds where each
/// bucket begins, and the entries sit sorted beside it. That costs eight bytes an entry, against
/// roughly forty for a <c>Dictionary&lt;uint, List&lt;(long, int)&gt;&gt;</c>. At 50,000 songs that is
/// the difference between 120 MB and 350 MB, and the function holding it has 1,769 MB in total.</para>
///
/// <para><b>Songs are ordinals here, not ids.</b> An id is eight bytes and there are fifteen
/// million entries. The caller keeps the ordinal-to-id table, which has one row per song rather
/// than one per entry.</para>
///
/// <para><b>Songs filed while running go into an overlay.</b> A flat array cannot take an
/// insertion. A lookup reads both, and when the overlay grows past a tenth of the flat side,
/// <see cref="Compact"/> merges it in. The merge needs no fingerprints, only the entries already
/// held, which matters because the fingerprints are not resident.</para>
/// </summary>
public sealed class ChromaIndex
{
    private readonly int _keySpace;

    /// <summary>Where each bucket begins in <see cref="_song"/>/<see cref="_pos"/>. One longer than
    /// the key space, so a bucket is <c>_start[key] .. _start[key + 1]</c> and the last one ends.</summary>
    private int[] _start;
    private int[] _song;
    private int[] _pos;

    private readonly Dictionary<uint, List<(int Song, int Pos)>> _added = new();
    private int _addedEntries;

    /// <summary>Keys dropped for carrying too much of the collection to narrow anything down. Kept
    /// so a song filed afterwards does not put them back one entry at a time.</summary>
    private readonly HashSet<uint> _stopWords = new();

    /// <summary>
    /// How many times the average bucket a key may carry before it is dropped as a stop word.
    ///
    /// <para>Real chroma is not spread evenly over the key space. A few values dominate, and walking
    /// them costs a lot and says nothing. Ten times the mean works at any store size and under any key
    /// scheme. It cannot empty an index the way an absolute figure can, because only a few buckets can
    /// be ten times the average. A share of the whole index would sound selective, but it does not work:
    /// at a real store size it can be far above the average bucket and still prune nothing.</para>
    /// </summary>
    public const int StopWordMultipleOfMean = 10;

    private ChromaIndex(int keySpace, int[] start, int[] song, int[] pos, IEnumerable<uint> stopWords)
    {
        _keySpace = keySpace;
        _start = start;
        _song = song;
        _pos = pos;
        foreach (uint key in stopWords) _stopWords.Add(key);
    }

    /// <summary>One recording's anchors, as the builder collects them.</summary>
    public sealed class Builder(ChromaKeyScheme scheme)
    {
        private uint[] _key = new uint[1 << 16];
        private int[] _song = new int[1 << 16];
        private int[] _pos = new int[1 << 16];
        private int _count;

        public ChromaKeyScheme Scheme { get; } = scheme;
        public int Count => _count;

        /// <summary>Files one index entry directly, for a caller reading an index somebody else
        /// wrote rather than deriving one from fingerprints.</summary>
        public void AddEntry(uint key, int songOrdinal, int position)
        {
            if (_count == _key.Length) Grow();
            _key[_count] = key;
            _song[_count] = songOrdinal;
            _pos[_count] = position;
            _count++;
        }

        /// <summary>Files one recording's anchors. The fingerprint is not kept.</summary>
        public void Add(int songOrdinal, ReadOnlySpan<uint> frames, int anchorsPerSong)
        {
            if (frames.Length == 0) return;

            Span<uint> keys = stackalloc uint[Scheme.KeysPerAnchor];
            foreach (int pos in ChromaKeyScheme.AnchorPositions(frames, anchorsPerSong))
            {
                int written = Scheme.Extract(frames, pos, keys);
                for (int k = 0; k < written; k++)
                {
                    if (_count == _key.Length) Grow();
                    _key[_count] = keys[k];
                    _song[_count] = songOrdinal;
                    _pos[_count] = pos;
                    _count++;
                }
            }
        }

        private void Grow()
        {
            Array.Resize(ref _key, _key.Length * 2);
            Array.Resize(ref _song, _song.Length * 2);
            Array.Resize(ref _pos, _pos.Length * 2);
        }

        /// <summary>
        /// Sorts what was collected into buckets and drops the stop words.
        ///
        /// <para>A counting sort, because a key is its own bucket number: count what each key
        /// carries, turn the counts into starts, then place each entry once. Linear, and the only
        /// pass that has to see every entry twice.</para>
        /// </summary>
        public ChromaIndex Build()
        {
            int keySpace = Scheme.KeySpace;
            var counts = new int[keySpace];
            for (int i = 0; i < _count; i++) counts[_key[i]]++;

            // Stop words are decided from the counts, before anything is placed, so their entries
            // are never written rather than written and then removed.
            int used = 0, total = 0;
            for (int k = 0; k < keySpace; k++)
                if (counts[k] > 0) { used++; total += counts[k]; }
            int threshold = used == 0 ? int.MaxValue : Math.Max(200, total / used * StopWordMultipleOfMean);

            var stopWords = new List<uint>();
            for (int k = 0; k < keySpace; k++)
                if (counts[k] > threshold) { stopWords.Add((uint)k); counts[k] = 0; }

            var start = new int[keySpace + 1];
            int running = 0;
            for (int k = 0; k < keySpace; k++)
            {
                start[k] = running;
                running += counts[k];
            }
            start[keySpace] = running;

            var song = new int[running];
            var pos = new int[running];
            var cursor = (int[])start.Clone();
            for (int i = 0; i < _count; i++)
            {
                uint key = _key[i];
                if (counts[key] == 0) continue;      // a stop word, or a bucket with nothing in it
                int at = cursor[key]++;
                song[at] = _song[i];
                pos[at] = _pos[i];
            }

            // The collection buffers are the largest thing held during a build and are not wanted
            // afterwards. Dropped here rather than left to the builder's lifetime.
            _key = Array.Empty<uint>();
            _song = Array.Empty<int>();
            _pos = Array.Empty<int>();
            _count = 0;

            return new ChromaIndex(keySpace, start, song, pos, stopWords);
        }
    }

    /// <summary>The entries a key carries in the flat side, as spans into it.</summary>
    public readonly ref struct Bucket(ReadOnlySpan<int> songs, ReadOnlySpan<int> positions)
    {
        public readonly ReadOnlySpan<int> Songs = songs;
        public readonly ReadOnlySpan<int> Positions = positions;
        public int Length => Songs.Length;
    }

    public Bucket Flat(uint key)
    {
        if (key >= (uint)_keySpace) return default;
        int from = _start[key], to = _start[key + 1];
        return new Bucket(_song.AsSpan(from, to - from), _pos.AsSpan(from, to - from));
    }

    /// <summary>The entries a key carries among the songs filed since the flat side was built.</summary>
    public List<(int Song, int Pos)>? Overlay(uint key)
        => _added.GetValueOrDefault(key);

    /// <summary>Files one recording into the overlay. Not thread-safe against a lookup in flight:
    /// the caller is expected to hold whatever lock guards the index.</summary>
    public void Add(int songOrdinal, ReadOnlySpan<uint> frames, ChromaKeyScheme scheme, int anchorsPerSong)
    {
        if (frames.Length == 0) return;

        Span<uint> keys = stackalloc uint[scheme.KeysPerAnchor];
        foreach (int pos in ChromaKeyScheme.AnchorPositions(frames, anchorsPerSong))
        {
            int written = scheme.Extract(frames, pos, keys);
            for (int k = 0; k < written; k++)
            {
                if (_stopWords.Contains(keys[k])) continue;
                if (!_added.TryGetValue(keys[k], out var list))
                    _added[keys[k]] = list = new List<(int, int)>();
                list.Add((songOrdinal, pos));
                _addedEntries++;
            }
        }

        if (_addedEntries > Math.Max(1024, _song.Length / 10)) Compact();
    }

    /// <summary>
    /// Merges the overlay into the flat side, so what a container has learned costs eight bytes an
    /// entry like everything else. It needs no fingerprints, only the entries already held. That is
    /// what makes it possible at all when the fingerprints are not kept in memory.
    /// </summary>
    public void Compact()
    {
        if (_addedEntries == 0) return;

        var counts = new int[_keySpace];
        for (int k = 0; k < _keySpace; k++) counts[k] = _start[k + 1] - _start[k];
        foreach (var (key, list) in _added) counts[key] += list.Count;

        var start = new int[_keySpace + 1];
        int running = 0;
        for (int k = 0; k < _keySpace; k++)
        {
            start[k] = running;
            running += counts[k];
        }
        start[_keySpace] = running;

        var song = new int[running];
        var pos = new int[running];
        for (int k = 0; k < _keySpace; k++)
        {
            int from = _start[k], length = _start[k + 1] - from;
            Array.Copy(_song, from, song, start[k], length);
            Array.Copy(_pos, from, pos, start[k], length);

            if (!_added.TryGetValue((uint)k, out var list)) continue;
            int at = start[k] + length;
            foreach (var (s, p) in list)
            {
                song[at] = s;
                pos[at] = p;
                at++;
            }
        }

        _start = start;
        _song = song;
        _pos = pos;
        _added.Clear();
        _addedEntries = 0;
    }

    /// <summary>Entries standing in the index, flat side and overlay together.</summary>
    public int Entries => _song.Length + _addedEntries;

    /// <summary>Buckets carrying at least one entry. Read together with <see cref="Entries"/>:
    /// stage 1 walks every entry in every bucket a query touches, so the work per lookup is
    /// roughly entries divided by buckets, and that only stays flat while a growing collection
    /// makes more buckets rather than longer ones.</summary>
    public int Buckets
    {
        get
        {
            int used = 0;
            for (int k = 0; k < _keySpace; k++)
                if (_start[k + 1] > _start[k] || _added.ContainsKey((uint)k)) used++;
            return used;
        }
    }

    /// <summary>Entries waiting in the overlay rather than merged into the flat side.</summary>
    public int OverlayEntries => _addedEntries;

    /// <summary>Keys dropped as stop words when the index was built.</summary>
    public int StopWords => _stopWords.Count;
}
