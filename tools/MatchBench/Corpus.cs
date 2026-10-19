using Microsoft.Data.Sqlite;
using RPM.Fingerprint.Chroma;

namespace MatchBench;

/// <summary>
/// The set of recordings a measurement runs against: real fingerprints from a library, and
/// synthetic ones that increase it to the size of the live store.
///
/// <para>The two are not interchangeable, and they are kept separate on purpose. Recall and
/// precision are measured only on the real ones, because synthetic frames are uniform random and
/// real chroma bytes are not: a matcher scored against uniform distractors gets a score that is too
/// good. Cost is measured against the whole set, because what a lookup costs depends on how many
/// entries are in the buckets it reads, not on where they came from.</para>
/// </summary>
public sealed record Corpus(
    Dictionary<long, ChromaFingerprint> Fingerprints,
    Dictionary<long, string> Titles,
    IReadOnlyList<long> RealIds)
{
    public int Count => Fingerprints.Count;

    /// <summary>Reads every stored fingerprint from a desktop library's <c>library.db</c>.</summary>
    public static (Dictionary<long, ChromaFingerprint> Fingerprints, Dictionary<long, string> Titles)
        ReadLibrary(string dbPath)
    {
        var fingerprints = new Dictionary<long, ChromaFingerprint>();
        var titles = new Dictionary<long, string>();

        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT relative_path, fingerprint
            FROM track_fingerprint
            WHERE fingerprint IS NOT NULL
            ORDER BY relative_path";

        long id = 1;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var blob = (byte[])reader["fingerprint"];
            var fp = ChromaFingerprint.Deserialize(blob);
            if (fp.Length == 0) continue;
            fingerprints[id] = fp;
            titles[id] = (string)reader["relative_path"];
            id++;
        }
        return (fingerprints, titles);
    }

    /// <summary>Real recordings first, then synthetic padding up to <paramref name="totalSongs"/>.</summary>
    public static Corpus Build(string? libraryDb, int totalSongs, int syntheticFrames)
    {
        Dictionary<long, ChromaFingerprint> fingerprints;
        Dictionary<long, string> titles;

        if (libraryDb is not null && File.Exists(libraryDb))
        {
            (fingerprints, titles) = ReadLibrary(libraryDb);
            Console.WriteLine($"  {fingerprints.Count} real recordings from {libraryDb}");
        }
        else
        {
            fingerprints = new Dictionary<long, ChromaFingerprint>();
            titles = new Dictionary<long, string>();
            Console.WriteLine("  no library given, corpus is synthetic throughout");
        }

        var realIds = fingerprints.Keys.ToList();

        long next = realIds.Count == 0 ? 1 : realIds.Max() + 1;
        for (long i = next; fingerprints.Count < totalSongs; i++)
        {
            fingerprints[i] = new ChromaFingerprint(Synthetic(syntheticFrames, seed: (int)(i * 17)));
            titles[i] = $"Synthetic {i}";
        }

        return new Corpus(fingerprints, titles, realIds);
    }

    /// <summary>The frames for one song: a random start, changing by three bits per frame. The same
    /// generator as the matcher tests, so both corpora have the same shape.</summary>
    public static uint[] Synthetic(int frames, int seed)
    {
        var random = new Random(seed);
        var song = new uint[frames];
        song[0] = (uint)random.Next(int.MinValue, int.MaxValue);
        for (int i = 1; i < frames; i++)
        {
            uint next = song[i - 1];
            for (int bit = 0; bit < 3; bit++)
                next ^= 1u << random.Next(32);
            song[i] = next;
        }
        return song;
    }

    /// <summary>The same recording fingerprinted again, not the same bytes read twice: one bit per
    /// frame flipped, which is about what a re-encode changes in a chroma fingerprint.</summary>
    public static uint[] WithNoise(uint[] song, int seed)
    {
        var copy = (uint[])song.Clone();
        var noise = new Random(seed);
        for (int i = 0; i < copy.Length; i++)
            copy[i] ^= 1u << noise.Next(32);
        return copy;
    }

    /// <summary>A part of a recording, with noise added: what /identify-clip is asked to identify.</summary>
    public static uint[] Clip(uint[] song, int from, int length, int seed)
    {
        length = Math.Min(length, song.Length - from);
        return WithNoise(song[from..(from + length)], seed);
    }
}
