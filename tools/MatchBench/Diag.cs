using RPM.Fingerprint.Chroma;

namespace MatchBench;

/// <summary>What the real corpus contains. Run this before trusting a recall number: a matcher
/// scored against recordings that are mostly silence is measuring the silence.</summary>
public static class Diag
{
    public static void Describe(Corpus corpus)
    {
        var real = corpus.RealIds.Select(id => corpus.Fingerprints[id]).ToList();
        if (real.Count == 0) return;

        var lengths = real.Select(f => f.Length).Order().ToList();
        long frames = lengths.Sum(l => (long)l);
        long degenerate = real.Sum(f => f.SubFingerprints.Count(v => v == 0 || v == uint.MaxValue));
        var distinct = new HashSet<uint>();
        foreach (var f in real) foreach (uint v in f.SubFingerprints) distinct.Add(v);

        Console.WriteLine($"  frames per recording: min {lengths[0]}, median {lengths[lengths.Count / 2]}, max {lengths[^1]}");
        Console.WriteLine($"  degenerate frames (all-zero or all-ones): {degenerate:N0} of {frames:N0} " +
                          $"({100.0 * degenerate / frames:F1} %)");
        Console.WriteLine($"  distinct sub-fingerprint values: {distinct.Count:N0} of {frames:N0}");

        // The distribution of the bytes, because the key space is only as large as the values used.
        for (int shift = 0; shift <= 24; shift += 8)
        {
            var counts = new int[256];
            foreach (var f in real) foreach (uint v in f.SubFingerprints) counts[(v >> shift) & 0xFF]++;
            int used = counts.Count(c => c > 0);
            var top = counts.Select((c, i) => (Count: c, Value: i)).OrderByDescending(t => t.Count).First();
            Console.WriteLine($"  byte at bit {shift,2}: {used,3}/256 values used, " +
                              $"commonest 0x{top.Value:X2} takes {100.0 * top.Count / frames:F1} %");
        }
    }
}
