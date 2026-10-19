// Reads a real music folder's tags once and writes them to a flat file, so the tree grid's harness
// can use a real collection instead of generated strings. Generated data is uniform (same name
// lengths, same album sizes, one artist per thirty tracks), and uniform data hides the expensive
// cases: thousands of one-track artists, empty tags that all fall into one group, and names long
// enough to change a column's width.
//
// Usage: dotnet run -- <music folder> <output.tsv>

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using RPM.Core.Metadata;
using RPM.Core.Scanning;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: musicdump <music folder> [output.tsv]");
    Environment.ExitCode = 1;
    return;
}

var root = args[0];
var output = args.Length > 1 ? args[1] : "music.tsv";

var sw = Stopwatch.StartNew();
Console.WriteLine($"[dump] scanning {root}");
var paths = AudioFileScanner.Scan(root).Files;
Console.WriteLine($"[dump] {paths.Count} audio files in {sw.ElapsedMilliseconds}ms");

var rows = new ConcurrentBag<string>();
var done = 0;

Parallel.ForEach(paths, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, path =>
{
    var t = TagReader.Read(path);

    // Tabs and newlines out of tag text: the format is one row per line, columns split on tab.
    static string F(string? s) => string.IsNullOrEmpty(s)
        ? ""
        : s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    rows.Add(string.Join('\t',
        F(Path.GetRelativePath(root, path)),
        F(t.Artist),
        F(t.AlbumArtist),
        F(t.Album),
        F(t.Title),
        t.TrackNumber,
        t.DiscNumber,
        t.Year,
        F(t.Genre),
        t.DurationSeconds.ToString("F1", CultureInfo.InvariantCulture),
        t.BitrateKbps ?? 0,
        t.SampleRate ?? 0,
        t.BitsPerSample ?? 0,
        t.Channels ?? 0,
        F(t.CodecDescription),
        new FileInfo(path).Length,
        (int)t.Availability));

    var n = Interlocked.Increment(ref done);
    if (n % 2000 == 0)
    {
        Console.WriteLine($"[dump] {n}/{paths.Count} ({sw.ElapsedMilliseconds}ms)");
    }
});

var sorted = rows.ToList();
sorted.Sort(StringComparer.OrdinalIgnoreCase);
File.WriteAllLines(output, sorted);

Console.WriteLine($"[dump] wrote {sorted.Count} rows to {output} in {sw.Elapsed.TotalSeconds:F1}s");
