namespace RPM.Fingerprint.Chroma;

/// <summary>
/// How an anchor frame becomes stage-1 index keys.
///
/// <para>The index and the query have to derive keys the same way, or nothing matches. Nothing reports
/// that nothing matched: a matcher built one way and queried another returns "no candidates". That is
/// the same result as a store that has never heard the recording. The rule is a type, and it is the
/// only way to reach key extraction. One instance is handed to the builder and to the lookup.
/// <see cref="ChromaStore"/> writes its <see cref="Id"/> beside a persisted index, and refuses an index
/// built under a different scheme.</para>
///
/// <para>A scheme is judged on two numbers that pull against each other. <b>Selectivity</b> is how
/// many buckets the key space has: entries divide across them, and stage 1 walks every entry in
/// every bucket a query touches. So a wider key is less work per lookup and less memory for the
/// tally. <b>Survival</b> is the chance a key still matches when the recording is heard again
/// rather than read again. A wider key spans more bits, and more bits means more chance that the
/// noise landed inside it. See <see cref="ChromaKeySchemes"/> for what each one measured.</para>
/// </summary>
public abstract class ChromaKeyScheme
{
    /// <summary>Written beside a persisted index so one built under another scheme is refused
    /// rather than silently matching nothing. Never reused for a different scheme.</summary>
    public abstract int Id { get; }

    public abstract string Name { get; }

    /// <summary>Keys <see cref="Extract"/> writes for an anchor that has room for all of them.</summary>
    public abstract int KeysPerAnchor { get; }

    /// <summary>
    /// Keys for the anchor at <paramref name="pos"/>, written into <paramref name="keys"/>, which
    /// must have room for <see cref="KeysPerAnchor"/>.
    /// </summary>
    /// <returns>How many were written. Fewer than <see cref="KeysPerAnchor"/> when the anchor sits
    /// too near the end of the recording for a key that needs a later frame.</returns>
    public abstract int Extract(ReadOnlySpan<uint> frames, int pos, Span<uint> keys);

    /// <summary>Buckets the key space can hold, which is what entries divide across.</summary>
    public abstract int KeySpace { get; }

    /// <summary>
    /// Which frames of a recording get indexed: evenly spaced through it, with degenerate frames
    /// skipped.
    ///
    /// <para>This is here rather than at each call site, because the index and the query have to agree
    /// about it as much as they have to agree about the keys. If they differ, the same recording filed by
    /// two paths gets different anchor sets. The in-memory build steps by <c>pos += stride</c> and indexes
    /// every frame it lands on. The SQLite build skips all-zero and all-ones frames and counts only the
    /// anchors it keeps.</para>
    /// </summary>
    public static int[] AnchorPositions(ReadOnlySpan<uint> frames, int anchorsPerSong)
    {
        if (frames.Length == 0 || anchorsPerSong <= 0) return Array.Empty<int>();

        int wanted = Math.Min(anchorsPerSong, frames.Length);
        int step = Math.Max(1, frames.Length / wanted);
        var positions = new List<int>(wanted);
        for (int i = 0; i < frames.Length && positions.Count < wanted; i += step)
        {
            // An all-zero or all-ones frame is silence or clipping, and it collides with every
            // other recording's silence.
            if (frames[i] == 0 || frames[i] == uint.MaxValue) continue;
            positions.Add(i);
        }
        return positions.ToArray();
    }

    public override string ToString() => Name;
}

/// <summary>
/// The schemes, and what each one is for.
///
/// <para>Numbers below are measured by <c>tools/MatchBench</c> on a corpus shaped like the live store:
/// 13,332 songs, of which 844 are real recordings from a desktop library, and the rest are synthetic
/// padding. Recall is scored on the real ones only. Synthetic frames are uniform random, and real chroma
/// bytes are not. A matcher scored against uniform distractors looks better than it is.</para>
/// </summary>
public static class ChromaKeySchemes
{
    /// <summary>
    /// Four bytes of the sub-fingerprint, indexed separately. This scheme does not scale to large
    /// collections.
    ///
    /// <para>1,024 buckets, and that is the whole problem. The key is a window id and one byte, so the
    /// count is fixed however large the collection gets. Every song added makes the same buckets longer.
    /// At 13,332 songs one lookup walked 6.35M entries and turned them into 5.9M distinct (song, offset)
    /// votes. The tally alone peaked over 1.8 GB against the function's 1,769 MB, and that is why the
    /// function ran out of memory. It is kept so that a measurement can be compared against it, and so an
    /// index written under it can still be read.</para>
    /// </summary>
    public static readonly ChromaKeyScheme ByteWindows = new ByteWindowScheme();

    /// <summary>
    /// Overlapping 16-bit windows of the sub-fingerprint.
    ///
    /// <para>The same anchor, the same number of entries per song, a key space 256 times wider. It
    /// costs survival: one flipped bit lands inside a 16-bit window half the time rather than a
    /// quarter of the time, so fewer of an anchor's keys survive a re-encode. There are more of
    /// them per anchor to compensate.</para>
    /// </summary>
    public static readonly ChromaKeyScheme HalfWords = new HalfWordScheme();

    /// <summary>The scheme new indexes are built under.</summary>
    public static ChromaKeyScheme Default => HalfWords;

    public static ChromaKeyScheme ById(int id) => id switch
    {
        1 => ByteWindows,
        2 => HalfWords,
        _ => throw new ArgumentOutOfRangeException(
            nameof(id), id, "No chroma key scheme by that id. An index written by a newer build cannot be read."),
    };

    private sealed class ByteWindowScheme : ChromaKeyScheme
    {
        public override int Id => 1;
        public override string Name => "byte-windows";
        public override int KeysPerAnchor => 4;
        public override int KeySpace => 4 * 256;

        public override int Extract(ReadOnlySpan<uint> frames, int pos, Span<uint> keys)
        {
            uint frame = frames[pos];
            keys[0] = 0u << 8 | (frame & 0xFF);
            keys[1] = 1u << 8 | ((frame >> 8) & 0xFF);
            keys[2] = 2u << 8 | ((frame >> 16) & 0xFF);
            keys[3] = 3u << 8 | ((frame >> 24) & 0xFF);
            return 4;
        }
    }

    private sealed class HalfWordScheme : ChromaKeyScheme
    {
        /// <summary>Where each 16-bit window starts. Overlapping, so a flipped bit spoils some of
        /// an anchor's keys rather than all of them.</summary>
        private static readonly int[] Shifts = { 0, 4, 8, 12, 16 };

        public override int Id => 2;
        public override string Name => "half-words";
        public override int KeysPerAnchor => Shifts.Length;
        public override int KeySpace => Shifts.Length * 65_536;

        public override int Extract(ReadOnlySpan<uint> frames, int pos, Span<uint> keys)
        {
            uint frame = frames[pos];
            for (int i = 0; i < Shifts.Length; i++)
                keys[i] = (uint)i << 16 | ((frame >> Shifts[i]) & 0xFFFF);
            return Shifts.Length;
        }
    }
}
