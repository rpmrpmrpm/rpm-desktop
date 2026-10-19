using System.Diagnostics;
using MatchBench;
using RPM.Fingerprint.Chroma;

// What one own-DB lookup costs at a given store size, and whether it still gives the right answer.
//
// The live function is limited by memory more than by time, and neither number is visible from
// outside it, so both are measured here: stage-1 hits, the size of the vote tally, the peak managed
// heap during a query, and (on real recordings only) whether the recording finds itself, whether
// a clip finds its source, and whether anything confidently matches the wrong record.
//
//   dotnet run -c Release --project tools/MatchBench -- [songs] [queries] [maxQuerySamples] [schemeId]
//
// env:
//   RPM_LIBRARY   path to a desktop library.db (<music folder>/.rpm/library.db) whose
//                 fingerprints become the real corpus. Required.

int songs = args.Length > 0 ? int.Parse(args[0]) : 13_332;
int queries = args.Length > 1 ? int.Parse(args[1]) : 200;
int samples = args.Length > 2 ? int.Parse(args[2]) : 500;
var scheme = args.Length > 3
    ? ChromaKeySchemes.ById(int.Parse(args[3]))
    : ChromaKeySchemes.Default;
const int SyntheticFrames = 2_000;   // ~4 minutes at ~8 frames a second

string? libraryDb = Environment.GetEnvironmentVariable("RPM_LIBRARY");
if (string.IsNullOrEmpty(libraryDb))
{
    Console.Error.WriteLine("set RPM_LIBRARY to a library.db (<music folder>/.rpm/library.db)");
    return 1;
}

Console.WriteLine($"corpus: {songs} songs, {samples} query samples, key scheme {scheme}");
var sw = Stopwatch.StartNew();
var corpus = Corpus.Build(libraryDb, songs, SyntheticFrames);
long frames = corpus.Fingerprints.Values.Sum(f => (long)f.Length);
Console.WriteLine($"  {corpus.Count} songs ({corpus.RealIds.Count} real), " +
                  $"{frames * sizeof(uint) / (1024 * 1024)} MB of fingerprints, built in {sw.ElapsedMilliseconds} ms");

Diag.Describe(corpus);

// Built the same way as the function builds it: one recording at a time, with the fingerprint
// indexed and then released. Afterwards only the index remains, which is what is measured. The
// corpus dictionary below takes the place of the DynamoDB table that stage 2 fetches from.
GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
long beforeIndex = GC.GetTotalMemory(forceFullCollection: true);

sw.Restart();
var builder = new ChromaMatcher.Builder(scheme) { MaxQuerySamples = samples };
foreach (var (songId, fp) in corpus.Fingerprints)
    builder.Add(songId, corpus.Titles.GetValueOrDefault(songId), fp);
var matcher = builder.Build(new ResidentFingerprints(corpus.Fingerprints));
long buildMs = sw.ElapsedMilliseconds;

GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
long indexBytes = GC.GetTotalMemory(forceFullCollection: true) - beforeIndex;

Console.WriteLine($"index: {matcher.IndexEntries:N0} entries over {matcher.IndexBuckets:N0} buckets " +
                  $"({matcher.IndexEntries / Math.Max(1, matcher.IndexBuckets):N0} per bucket, " +
                  $"{scheme.KeySpace:N0} possible), built in {buildMs} ms");
Console.WriteLine($"resident: index {indexBytes / (1024 * 1024)} MB, " +
                  $"corpus {frames * sizeof(uint) / (1024 * 1024)} MB " +
                  $"(the function holds the index and asks the table for the corpus)");
Console.WriteLine();

// Which real recordings to query. A sample, not all of them, so a run takes minutes.
var rng = new Random(4242);
var asked = corpus.RealIds.OrderBy(_ => rng.Next()).Take(queries).ToList();
if (asked.Count == 0)
{
    Console.WriteLine("no real recordings in the corpus — nothing to score. Set RPM_LIBRARY.");
    return 1;
}

Console.WriteLine($"scoring {asked.Count} real recordings");

var cost = new Cost();
int ranked = 0, identified = 0, wrongRecord = 0;
int clipRanked = 0, clipConfident = 0, clipsAsked = 0;
var offenders = new List<string>();
var misses = new List<string>();

foreach (long id in asked)
{
    var truth = corpus.Fingerprints[id].SubFingerprints;

    // 1. The same recording fingerprinted again. This is what /fingerprints asks, and the answer
    //    creates a song id, so this answer must be correct.
    var again = new ChromaFingerprint(Corpus.WithNoise(truth, seed: (int)id));
    var result = await cost.MeasureAsync(() => matcher.IdentifyAsync(again));
    bool right = IsSameAudio(id, result.SongId);

    if (right) ranked++;
    if (right && result.IsSameRecording) identified++;
    if (!right && result.IsSameRecording && result.SongId is { } other)
    {
        wrongRecord++;
        if (offenders.Count < 20)
            offenders.Add($"#{id} {corpus.Titles[id]}  ->  #{other} {corpus.Titles.GetValueOrDefault(other, "?")} " +
                          $"(bpf {result.BitsPerFrame:F1}, margin {result.MarginToRunnerUp:F1}, cover {result.Coverage:F2})");
    }
    if (!right && misses.Count < 15)
    {
        string why = result.SongId is null ? "no candidates" : $"ranked #{result.SongId} first, which is other audio";
        misses.Add($"#{id} {corpus.Titles[id]}: {why} " +
                   $"(bpf {result.BitsPerFrame:F1}, margin {result.MarginToRunnerUp:F1}, " +
                   $"cover {result.Coverage:F2}, frames {truth.Length}, cands {result.CandidatesEvaluated})");
    }

    // 2. Twenty seconds from the middle. This is what /identify-clip asks, which needs
    //    confidence without full coverage.
    if (truth.Length >= 400)
    {
        clipsAsked++;
        var clip = new ChromaFingerprint(Corpus.Clip(truth, truth.Length / 2 - 80, 160, seed: (int)id + 7));
        var clipResult = await cost.MeasureAsync(() => matcher.IdentifyAsync(clip));
        bool clipRight = IsSameAudio(id, clipResult.SongId);
        if (clipRight) clipRanked++;
        if (clipRight && clipResult.IsConfident) clipConfident++;
    }
}

Console.WriteLine();
// The first number is the one a key scheme changes: whether stage 1 put the right audio in the
// candidate set and stage 2 ranked it best. The second is that number after the confidence gates,
// which a key scheme does not affect and which fails when a library has duplicate copies: a recording
// stored twice has its other copy as the second-best match, so the margin is zero and it is never
// identified, however well it was found.
Console.WriteLine($"stage 1 found the right audio:  {ranked}/{asked.Count}");
Console.WriteLine($"  ...and it passed the gates:   {identified}/{asked.Count}");
Console.WriteLine($"clip found its source:          {clipRanked}/{clipsAsked}");
Console.WriteLine($"  ...and it passed the gates:   {clipConfident}/{clipsAsked}");
Console.WriteLine($"confidently wrong:              {wrongRecord}");
foreach (var line in offenders) Console.WriteLine($"    {line}");
if (misses.Count > 0) Console.WriteLine("stage-1 misses:");
foreach (var line in misses) Console.WriteLine($"    {line}");
Console.WriteLine();
cost.Report();
// The bench holds the corpus, in place of the table that the function fetches from. The function
// itself holds only the index, plus the data of the lookup that is running.
Console.WriteLine($"the function would hold: {indexBytes / (1024 * 1024)} MB of index " +
                  $"plus up to {cost.WorstAllocatedBytes / (1024 * 1024)} MB in flight, of 1769 MB");
return wrongRecord == 0 && ranked == asked.Count ? 0 : 1;

// Whether two rows have the same audio, which is different from whether they are the same row. A
// library can have the same recording more than once (the same track on an album and on a
// compilation, a FLAC and an MP3 of one file), and a matcher that returns the other copy has not
// made a mistake. So results are scored by alignment, not by id.
bool IsSameAudio(long asked, long? answered)
{
    if (answered is not { } other) return false;
    if (other == asked) return true;
    if (!corpus.Fingerprints.TryGetValue(other, out var theirs)) return false;

    var mine = corpus.Fingerprints[asked].SubFingerprints;
    var them = theirs.SubFingerprints;
    int start, end;
    if (them.Length >= mine.Length) { start = 0; end = them.Length - mine.Length; }
    else { start = -(mine.Length - them.Length); end = 0; }

    var (_, bpf, overlap) = ChromaFingerprint.BestAlignment(mine, them, start, end);
    // Two copies of one recording differ by almost zero bits per frame. The pair must also cover
    // each other, or a track found inside a longer set would count.
    return bpf <= 2.0 && overlap >= Math.Max(mine.Length, them.Length) / 2;
}

/// <summary>Times a lookup and samples the heap while it runs, because the tally that uses the
/// function's memory is garbage by the time the lookup returns, and a collection afterwards cannot
/// see it.</summary>
sealed class Cost
{
    private readonly List<double> _ms = new();
    private long _worstAlloc;
    private long _worstPeak;

    public async Task<ChromaMatchResult> MeasureAsync(Func<Task<ChromaMatchResult>> lookup)
    {
        long peak = 0;
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false));
                await Task.Delay(2);
            }
        });

        long before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        var result = await lookup();
        sw.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        sampling.Cancel();
        try { sampler.Wait(); } catch { /* the sampler only reads counters */ }

        _ms.Add(sw.Elapsed.TotalMilliseconds);
        _worstAlloc = Math.Max(_worstAlloc, allocated);
        _worstPeak = Math.Max(_worstPeak, peak);
        return result;
    }

    public long WorstAllocatedBytes => _worstAlloc;

    public void Report()
    {
        var sorted = _ms.Order().ToList();
        Console.WriteLine($"lookups:             {sorted.Count}");
        Console.WriteLine($"  median             {sorted[sorted.Count / 2]:F0} ms");
        Console.WriteLine($"  worst              {sorted[^1]:F0} ms");
        Console.WriteLine($"  worst allocated    {_worstAlloc / (1024 * 1024)} MB");
        Console.WriteLine($"  peak managed heap  {_worstPeak / (1024 * 1024)} MB " +
                          $"(the function has 1769 MB for everything)");
        // The desktop sends eight reports in one request, and one request must finish within the
        // function's sixty seconds.
        Console.WriteLine($"  a batch of 8       {sorted.Sum() / sorted.Count * 8 / 1000:F1} s of lookups at the median");
    }
}
