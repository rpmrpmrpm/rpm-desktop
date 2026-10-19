using System.Diagnostics;
using RPM.Fingerprint.Chroma;
using RPM.Fingerprint.External;

if (args.Length == 0) { PrintUsage(); return 1; }

string dbPath = Environment.GetEnvironmentVariable("MDFP_DB")
    ?? Path.Combine(Environment.CurrentDirectory, "testDatabase", "fingerprints.db");

var cfg = new ChromaFingerprinterConfig();

try
{
    return args[0].ToLowerInvariant() switch
    {
        "ingest"        => Ingest(args.Skip(1).ToArray(), dbPath, cfg),
        "ingest-dir"    => IngestDir(args.Skip(1).ToArray(), dbPath, cfg),
        "identify"      => Identify(args.Skip(1).ToArray(), dbPath, cfg),
        "identify-clip" => IdentifyClip(args.Skip(1).ToArray(), dbPath, cfg),
        "verify-all"    => VerifyAll(dbPath, cfg),
        "list"          => List(dbPath),
        "info"          => Info(args.Skip(1).ToArray(), cfg),
        "stats"         => Stats(dbPath),
        "lookup"        => RunLookup(args.Skip(1).ToArray()).GetAwaiter().GetResult(),
        "help" or "--help" or "-h" => PrintUsageReturn(),
        _ => PrintUsageReturn(),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}

static int Ingest(string[] args, string dbPath, ChromaFingerprinterConfig cfg)
{
    if (args.Length == 0) { Console.Error.WriteLine("usage: mdfp ingest <file> [--title \"...\"]"); return 1; }
    string file = args[0];
    string title = ParseFlag(args, "--title") ?? Path.GetFileNameWithoutExtension(file);

    if (!File.Exists(file)) { Console.Error.WriteLine($"file not found: {file}"); return 1; }

    var sw = Stopwatch.StartNew();
    Console.WriteLine($"fingerprinting {Path.GetFileName(file)}...");
    var fp = ChromaFingerprinter.FingerprintFile(file, cfg);
    sw.Stop();

    int blobBytes = fp.Fingerprint.Serialize().Length;
    Console.WriteLine($"  duration : {fp.DurationSeconds:F1}s");
    Console.WriteLine($"  sub-fps  : {fp.Fingerprint.Length}  (Chromaprint format via fpcalc)");
    Console.WriteLine($"  packed   : {blobBytes} bytes");
    Console.WriteLine($"  elapsed  : {sw.ElapsedMilliseconds} ms");

    using var store = new ChromaStore(dbPath);
    long songId = store.AddSong(title, Path.GetFullPath(file), fp.DurationSeconds, fp.Fingerprint);
    Console.WriteLine($"stored as song #{songId} '{title}'");
    return 0;
}

static int IngestDir(string[] args, string dbPath, ChromaFingerprinterConfig cfg)
{
    if (args.Length == 0) { Console.Error.WriteLine("usage: mdfp ingest-dir <folder> [--recursive]"); return 1; }
    string dir = args[0];
    if (!Directory.Exists(dir)) { Console.Error.WriteLine($"folder not found: {dir}"); return 1; }

    var search = args.Contains("--recursive") ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
    string[] extensions = { ".mp3", ".mp4", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wav", ".wma", ".alac" };
    var files = Directory.EnumerateFiles(dir, "*.*", search)
        .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    if (files.Length == 0) { Console.WriteLine("no audio files found"); return 0; }
    Console.WriteLine($"found {files.Length} audio file(s) in {dir}");
    Console.WriteLine();

    using var store = new ChromaStore(dbPath);

    int ok = 0, failed = 0;
    long totalBlobBytes = 0;
    var totalSw = Stopwatch.StartNew();

    for (int i = 0; i < files.Length; i++)
    {
        string file = files[i];
        string title = Path.GetFileNameWithoutExtension(file);
        Console.Write($"[{i + 1,3}/{files.Length}] {Truncate(title, 60),-60} ");
        try
        {
            var sw = Stopwatch.StartNew();
            var fp = ChromaFingerprinter.FingerprintFile(file, cfg);
            int blobBytes = fp.Fingerprint.Serialize().Length;
            store.AddSong(title, Path.GetFullPath(file), fp.DurationSeconds, fp.Fingerprint);
            sw.Stop();
            Console.WriteLine($" {fp.DurationSeconds,5:F0}s  {fp.Fingerprint.Length,5} sub-fps  {blobBytes,5} B  {sw.ElapsedMilliseconds,5} ms");
            ok++;
            totalBlobBytes += blobBytes;
        }
        catch (Exception ex)
        {
            Console.WriteLine($" FAILED ({ex.GetType().Name}: {ex.Message})");
            failed++;
        }
    }

    totalSw.Stop();
    Console.WriteLine();
    Console.WriteLine($"done: {ok} ok, {failed} failed in {totalSw.Elapsed.TotalSeconds:F1}s");
    if (ok > 0)
        Console.WriteLine($"avg fingerprint size: {totalBlobBytes / (double)ok:F0} bytes/song ({totalBlobBytes / 1024.0:F0} KB total)");
    return failed == 0 ? 0 : 4;
}

static int Identify(string[] args, string dbPath, ChromaFingerprinterConfig cfg)
{
    if (args.Length == 0) { Console.Error.WriteLine("usage: mdfp identify <file>"); return 1; }
    string file = args[0];
    if (!File.Exists(file)) { Console.Error.WriteLine($"file not found: {file}"); return 1; }

    var sw = Stopwatch.StartNew();
    Console.WriteLine($"fingerprinting {Path.GetFileName(file)}...");
    var fp = ChromaFingerprinter.FingerprintFile(file, cfg);
    Console.WriteLine($"  sub-fps: {fp.Fingerprint.Length}");

    using var store = new ChromaStore(dbPath);
    var matcher = store.OpenMatcher(debugTiming: true);
    var indexLoadMs = sw.ElapsedMilliseconds;
    var matchSw = Stopwatch.StartNew();
    var result = matcher.IdentifyAsync(fp.Fingerprint).GetAwaiter().GetResult();
    matchSw.Stop();
    sw.Stop();

    Console.WriteLine($"  fingerprint+load: {indexLoadMs} ms,  match: {matchSw.ElapsedMilliseconds} ms,  total: {sw.ElapsedMilliseconds} ms");
    PrintMatchWithMargin(result);
    return result.SongId is null ? 3 : 0;
}

static int IdentifyClip(string[] args, string dbPath, ChromaFingerprinterConfig cfg)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("usage: mdfp identify-clip <file> <startSec> <lengthSec>");
        return 1;
    }
    string file = args[0];
    if (!File.Exists(file)) { Console.Error.WriteLine($"file not found: {file}"); return 1; }
    if (!double.TryParse(args[1], System.Globalization.CultureInfo.InvariantCulture, out double startSec) ||
        !double.TryParse(args[2], System.Globalization.CultureInfo.InvariantCulture, out double lengthSec))
    {
        Console.Error.WriteLine("startSec and lengthSec must be numbers (seconds)");
        return 1;
    }

    var sw = Stopwatch.StartNew();
    Console.WriteLine($"slicing {Path.GetFileName(file)} [{startSec:F1}s..{startSec + lengthSec:F1}s]");
    var fp = ChromaFingerprinter.FingerprintFileSlice(file, cfg, startSec, lengthSec);
    Console.WriteLine($"  decoded : {fp.DurationSeconds:F1}s");
    Console.WriteLine($"  sub-fps : {fp.Fingerprint.Length}");

    using var store = new ChromaStore(dbPath);
    var matcher = store.OpenMatcher();
    var top = matcher.IdentifyBruteForceTopKAsync(fp.Fingerprint, 5).GetAwaiter().GetResult();
    sw.Stop();
    Console.WriteLine($"  elapsed : {sw.ElapsedMilliseconds} ms");
    if (top.Count == 0) { Console.WriteLine("no match"); return 3; }
    Console.WriteLine();
    Console.WriteLine("top 5 candidates (lower bpf is better):");
    for (int i = 0; i < top.Count; i++)
        Console.WriteLine($"  {i + 1}. bpf={top[i].BitsPerFrame,5:F2}  ovl={top[i].OverlapFrames,4}  #{top[i].SongId} {top[i].Title}");
    return 0;
}

static int VerifyAll(string dbPath, ChromaFingerprinterConfig cfg)
{
    if (!File.Exists(dbPath)) { Console.Error.WriteLine($"db not found: {dbPath}"); return 1; }

    using var store = new ChromaStore(dbPath);
    var matcher = store.OpenMatcher();
    var songs = store.ListSongs();

    Console.WriteLine($"verifying {songs.Count} song(s)");
    Console.WriteLine();

    int correct = 0, wrong = 0, missing = 0;
    var totalSw = Stopwatch.StartNew();
    var wrongList = new List<string>();

    for (int i = 0; i < songs.Count; i++)
    {
        var (id, title, path, _, _) = songs[i];
        if (path is null || !File.Exists(path))
        {
            Console.WriteLine($"[{i + 1,3}/{songs.Count}] {Truncate(title, 60),-60}  MISSING SOURCE");
            missing++;
            continue;
        }

        try
        {
            var sw = Stopwatch.StartNew();
            var fp = ChromaFingerprinter.FingerprintFile(path, cfg);
            var result = matcher.IdentifyAsync(fp.Fingerprint).GetAwaiter().GetResult();
            sw.Stop();

            bool ok = result.SongId == id;
            string mark = ok ? "OK   " : "WRONG";
            Console.WriteLine($"[{i + 1,3}/{songs.Count}] {Truncate(title, 60),-60}  {mark}  bpf={result.BitsPerFrame,5:F2}  ovl={result.OverlapFrames,5}  {sw.ElapsedMilliseconds,5}ms");
            if (ok) correct++;
            else
            {
                wrong++;
                wrongList.Add($"  '{title}' → #{result.SongId} '{result.Title}' (bpf {result.BitsPerFrame:F2})");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{i + 1,3}/{songs.Count}] {Truncate(title, 60),-60}  ERROR: {ex.Message}");
            missing++;
        }
    }

    totalSw.Stop();
    Console.WriteLine();
    Console.WriteLine($"done: {correct} correct, {wrong} wrong, {missing} missing/error in {totalSw.Elapsed.TotalSeconds:F1}s");
    if (songs.Count > 0)
        Console.WriteLine($"avg per identify: {totalSw.Elapsed.TotalMilliseconds / songs.Count:F0} ms");
    if (wrong > 0)
    {
        Console.WriteLine();
        Console.WriteLine("misidentified:");
        foreach (var line in wrongList) Console.WriteLine(line);
    }
    return wrong == 0 && missing == 0 ? 0 : 4;
}

static int List(string dbPath)
{
    if (!File.Exists(dbPath)) { Console.WriteLine("(empty — no db yet)"); return 0; }
    using var store = new ChromaStore(dbPath);
    var songs = store.ListSongs();
    if (songs.Count == 0) { Console.WriteLine("(empty)"); return 0; }
    Console.WriteLine($"{"id",4}  {"duration",9}  {"sub-fps",8}  title");
    foreach (var (id, title, _, dur, len) in songs)
        Console.WriteLine($"{id,4}  {dur,8:F1}s  {len,8}  {title}");
    return 0;
}

static int Info(string[] args, ChromaFingerprinterConfig cfg)
{
    if (args.Length == 0) { Console.Error.WriteLine("usage: mdfp info <file>"); return 1; }
    string file = args[0];
    if (!File.Exists(file)) { Console.Error.WriteLine($"file not found: {file}"); return 1; }
    var fp = ChromaFingerprinter.FingerprintFile(file, cfg);
    int blobBytes = fp.Fingerprint.Serialize().Length;
    Console.WriteLine($"file       : {file}");
    Console.WriteLine($"duration   : {fp.DurationSeconds:F2}s");
    Console.WriteLine($"sub-fps    : {fp.Fingerprint.Length}  (Chromaprint format via fpcalc)");
    Console.WriteLine($"packed     : {blobBytes} bytes");
    Console.WriteLine($"bytes/sec  : {blobBytes / Math.Max(0.001, fp.DurationSeconds):F1}");
    return 0;
}

static async Task<int> RunLookup(string[] args)
{
    if (args.Length == 0) { Console.Error.WriteLine("usage: mdfp lookup <file>"); return 1; }
    string file = args[0];
    if (!File.Exists(file)) { Console.Error.WriteLine($"file not found: {file}"); return 1; }

    string? apiKey = Environment.GetEnvironmentVariable("ACOUSTID_API_KEY");
    if (string.IsNullOrEmpty(apiKey))
    {
        Console.Error.WriteLine("error: set ACOUSTID_API_KEY env var (get one at https://acoustid.org/new-application)");
        return 1;
    }

    var sw = Stopwatch.StartNew();
    Console.WriteLine($"[1/3] fingerprinting {Path.GetFileName(file)} (Chromaprint compressed format)...");
    var fp = FpcalcRunner.RunCompressed(file);
    long fingerprintMs = sw.ElapsedMilliseconds;
    Console.WriteLine($"      duration {fp.DurationSeconds:F1}s, fingerprint {fp.Base64Fingerprint.Length} chars  ({fingerprintMs} ms)");

    Console.WriteLine($"[2/3] querying AcoustID...");
    var acoustId = new AcoustIdClient(apiKey);
    var match = await acoustId.LookupAsync(fp.Base64Fingerprint, fp.DurationSeconds);
    long acoustidMs = sw.ElapsedMilliseconds - fingerprintMs;
    if (match is null)
    {
        Console.WriteLine("      no match found in AcoustID");
        Console.WriteLine($"      ({acoustidMs} ms)");
        return 3;
    }
    Console.WriteLine($"      score {match.Score:P1}, MB recording id {match.RecordingId}  ({acoustidMs} ms)");
    Console.WriteLine($"      AcoustID says: {string.Join(", ", match.Artists)} — {match.Title}");

    Console.WriteLine($"[3/3] querying MusicBrainz for full metadata...");
    var mb = new MusicBrainzClient();
    var details = await mb.LookupRecordingAsync(match.RecordingId);
    long mbMs = sw.ElapsedMilliseconds - fingerprintMs - acoustidMs;
    sw.Stop();
    if (details is null)
    {
        Console.WriteLine($"      MusicBrainz returned nothing for {match.RecordingId}  ({mbMs} ms)");
        return 0;
    }
    Console.WriteLine($"      ({mbMs} ms)");
    Console.WriteLine();
    Console.WriteLine("=== identified ===");
    Console.WriteLine($"  title       : {details.Title}");
    Console.WriteLine($"  artist(s)   : {string.Join(", ", details.Artists)}");
    if (!string.IsNullOrEmpty(details.PrimaryReleaseTitle))
        Console.WriteLine($"  release     : {details.PrimaryReleaseTitle}");
    if (!string.IsNullOrEmpty(details.FirstReleaseDate))
        Console.WriteLine($"  first date  : {details.FirstReleaseDate}");
    if (!string.IsNullOrEmpty(details.Disambiguation))
        Console.WriteLine($"  note        : {details.Disambiguation}");
    Console.WriteLine($"  MB id       : {details.Id}");
    if (!string.IsNullOrEmpty(details.PrimaryReleaseGroupId))
        Console.WriteLine($"  art (CAA)   : https://coverartarchive.org/release-group/{details.PrimaryReleaseGroupId}/front");
    Console.WriteLine();
    Console.WriteLine($"total wall time: {sw.ElapsedMilliseconds} ms (fp {fingerprintMs} + acoustid {acoustidMs} + mb {mbMs})");
    return 0;
}

static int Stats(string dbPath)
{
    if (!File.Exists(dbPath)) { Console.Error.WriteLine($"db not found: {dbPath}"); return 1; }
    var fileInfo = new FileInfo(dbPath);
    using var store = new ChromaStore(dbPath);
    int songs = store.SongCount();
    long fpBytes = store.TotalFingerprintBytes();
    Console.WriteLine($"db file       : {fileInfo.FullName}");
    Console.WriteLine($"db size       : {fileInfo.Length / 1024.0:F1} KB ({fileInfo.Length} bytes)");
    Console.WriteLine($"songs         : {songs}");
    Console.WriteLine($"fingerprint   : {fpBytes / 1024.0:F1} KB total, {(songs == 0 ? 0 : fpBytes / songs)} avg bytes/song");
    Console.WriteLine($"db / song     : {(songs == 0 ? 0 : fileInfo.Length / songs)} bytes (incl. index + overhead)");
    if (songs > 0)
    {
        long projected50M = (fileInfo.Length / songs) * 50_000_000L;
        Console.WriteLine($"projected 50M : {projected50M / (1024.0 * 1024 * 1024):F1} GB");
    }
    return 0;
}

static void PrintMatchWithMargin(ChromaMatchResult result)
{
    if (result.SongId is null)
    {
        Console.WriteLine("no match");
        return;
    }
    string flag = result.IsConfident ? "CONFIDENT" : "uncertain (would trigger paid identification)";
    Console.WriteLine();
    Console.WriteLine($"best match : #{result.SongId} '{result.Title}'  [{flag}]");
    Console.WriteLine($"  bits/frame : {result.BitsPerFrame:F2}  (lower is better)");
    Console.WriteLine($"  margin     : {result.MarginToRunnerUp:F2} bpf to runner-up");
    Console.WriteLine($"  overlap    : {result.OverlapFrames} frames");
    Console.WriteLine($"  candidates : {result.CandidatesEvaluated} evaluated");
}

static int PrintUsageReturn() { PrintUsage(); return 0; }
static void PrintUsage()
{
    Console.WriteLine("RPM fingerprint POC (chroma pipeline)");
    Console.WriteLine();
    Console.WriteLine("commands:");
    Console.WriteLine("  ingest <file> [--title \"...\"]            fingerprint and store");
    Console.WriteLine("  ingest-dir <folder> [--recursive]         fingerprint every audio file in a folder");
    Console.WriteLine("  identify <file>                           match against the db");
    Console.WriteLine("  identify-clip <file> <startSec> <lenSec>  match a slice of a file");
    Console.WriteLine("  verify-all                                re-fingerprint every song in the db");
    Console.WriteLine("  list                                      list ingested songs");
    Console.WriteLine("  info <file>                               print fingerprint stats only");
    Console.WriteLine("  stats                                     show db size and bytes/song");
    Console.WriteLine();
    Console.WriteLine("env:");
    Console.WriteLine("  MDFP_DB    path to sqlite db (default: ./fingerprints.db)");
}

static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";

static string? ParseFlag(string[] args, string flag)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == flag) return args[i + 1];
    return null;
}
