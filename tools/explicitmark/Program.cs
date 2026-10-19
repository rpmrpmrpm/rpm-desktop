using System.Diagnostics;
using System.Text.Json;
using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Core.Models;

// Sets the explicit flag on tracks that have no lyrics flag, from a scan made by deezer_scan.py in
// this folder.
//
// Only fills a flag that is empty, unless --replace-clean is passed: then a track flagged Clean that the
// scan found explicit is changed too. Some downloaders write Clean when they mean "not explicit".
// Either way a track is changed only if its file and row still have the flag they had when scanned, so
// a flag set after the scan is not changed.
//
// Tracks with a file here: the flag is written into the file's tags (only that field). The library
// database is a copy of the tags and gets the change on the next scan, as with genremigrate.
// The app's cloud sync then sends the flag to the catalogue and the sidecar, without re-uploading audio.
//
// Tracks that are only in the cloud: there is no file, so the flag is set on the library row and the
// row is marked as edited here. The app's cloud sync sends it the same way.
//
// --apply refuses to run while rpm.exe is running. The app keeps the database open, and its sync must
// be the version that keeps owed sidecars (see CloudSyncPlan.OwesSidecar), not an older one.
//
//   dotnet run --project tools/explicitmark -- <library root> <scan.jsonl>            what would change
//   dotnet run --project tools/explicitmark -- <library root> <scan.jsonl> --apply    change it
//   add --replace-clean to either to include the tracks flagged Clean

var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
var apply = args.Contains("--apply", StringComparer.Ordinal);
var replaceClean = args.Contains("--replace-clean", StringComparer.Ordinal);

if (positional.Count != 2)
{
    Console.Error.WriteLine("usage: explicitmark <library root> <scan.jsonl> [--apply] [--replace-clean]");
    return 2;
}

var (root, scanPath) = (positional[0], positional[1]);
if (!Directory.Exists(root) || !File.Exists(scanPath))
{
    Console.Error.WriteLine($"not found: {(Directory.Exists(root) ? scanPath : root)}");
    return 2;
}

if (apply && Process.GetProcessesByName("rpm").Length > 0)
{
    Console.Error.WriteLine("rpm is running. Close it, then run this again with --apply.");
    return 3;
}

// The last line per track is its result: the scan appends, and retries a miss on a rerun.
var answers = new Dictionary<long, ScanLine>();
foreach (var line in File.ReadLines(scanPath))
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    var answer = JsonSerializer.Deserialize<ScanLine>(line);
    if (answer is not null) answers[answer.Id] = answer;
}

// The flag each track had when it was scanned. A track is changed only if it still has that flag.
static ContentAdvisory AtScan(ScanLine answer) => answer.Adv == 2 ? ContentAdvisory.Clean : ContentAdvisory.Unknown;

var found = answers.Values
    .Where(a => a.Verdict == "explicit" && (a.Adv == 0 || (replaceClean && a.Adv == 2)))
    .ToList();
Console.WriteLine($"{answers.Count} tracks in the scan, {found.Count} found explicit " +
                  $"({found.Count(a => a.Adv == 0)} unflagged, {found.Count(a => a.Adv == 2)} flagged Clean when scanned)");

// Where the app keeps the database of a library in a network folder: LocalData.Libraries in the app.
var thisComputer = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RPM", MirrorLocation.LibrariesFolderName);
using var db = new LibraryDatabase(MirrorLocation.For(root, thisComputer));

var files = new List<(string Path, string? RpmId, string Label)>();
var cloudOnly = new List<(string RpmId, ContentAdvisory Was, string Label)>();
int gone = 0, flaggedSince = 0, missingFile = 0, unreadable = 0;

foreach (var answer in found)
{
    // By path, which is unique. An RPM id is not: two files can have the same id (a record imported
    // twice into one folder), and finding by id would flag one copy twice and the other not at all. The
    // id is used only to find a track moved since the scan, and only when it is the same row the scan
    // read (a move keeps the row).
    var row = db.FindByRelativePath(answer.Path);
    if (row is null && RpmTag.IsId(answer.Rpm) && db.FindByRpmId(answer.Rpm!) is { } moved && moved.Id == answer.Id)
        row = moved;
    if (row is null) { gone++; continue; }
    var was = AtScan(answer);
    if ((row.Advisory ?? ContentAdvisory.Unknown) != was) { flaggedSince++; continue; }

    var label = $"{row.Artist ?? row.AlbumArtist} - {row.Title}";

    if (row.IsOffloaded)
    {
        if (RpmTag.IsId(row.RpmId)) cloudOnly.Add((row.RpmId!, was, label));
        else gone++;
        continue;
    }

    var path = Path.Combine(root, row.RelativePath.Replace('/', Path.DirectorySeparatorChar));
    if (!File.Exists(path)) { missingFile++; continue; }

    TrackInfo tags;
    try { tags = TagReader.Read(path); }
    catch { unreadable++; continue; }

    // The file is the source of truth for a track that has one. The row may not be updated yet.
    if (tags.Advisory != was) { flaggedSince++; continue; }

    // Keep the file's own id. Only a file with no id gets the row's, so the write does not create a
    // new id that the cloud does not know.
    files.Add((path, RpmTag.IsId(tags.RpmId) ? tags.RpmId : row.RpmId, label));
}

Console.WriteLine();
Console.WriteLine($"{files.Count} files to flag, {cloudOnly.Count} cloud-only tracks to flag");
Console.WriteLine($"{flaggedSince} flagged since the scan, {gone} no longer in the library, " +
                  $"{missingFile} with no file on disk, {unreadable} unreadable");
Console.WriteLine();

foreach (var group in files.Select(f => f.Label).Concat(cloudOnly.Select(c => c.Label))
                           .GroupBy(l => l[..Math.Max(0, l.IndexOf(" - ", StringComparison.Ordinal))])
                           .OrderByDescending(g => g.Count()).Take(40))
    Console.WriteLine($"  {group.Count(),4}  {group.Key}");
Console.WriteLine();

if (!apply)
{
    Console.WriteLine("Dry run. Nothing was written. Pass --apply to write these flags.");
    return 0;
}

int written = 0, failed = 0;
foreach (var (path, rpmId, _) in files)
{
    try
    {
        // The flag and nothing else. A partial write keeps every field that this info does not set
        // as it was in the file.
        TagReader.Write(path, new TrackInfo
        {
            FilePath = path,
            Extension = Path.GetExtension(path),
            RpmId = rpmId,
            Advisory = ContentAdvisory.Explicit,
        });
        written++;
        if (written % 250 == 0)
            Console.WriteLine($"  {written} files written...");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"  could not write {path}: {ex.Message}");
    }
}

var at = DateTime.UtcNow.Ticks;
var rows = cloudOnly.GroupBy(c => c.Was)
    .Sum(g => db.ReplaceCloudOnlyAdvisory(g.Select(c => c.RpmId), g.Key, ContentAdvisory.Explicit, at));

Console.WriteLine();
Console.WriteLine($"{written} files written, {failed} failed. {rows} cloud-only rows flagged.");
Console.WriteLine("Start rpm. Its library scan reads the new tags, and its cloud sync sends every flag up.");
return failed > 0 ? 1 : 0;

/// <summary>One line of deezer_scan.py's output, with only the fields this tool reads.</summary>
internal sealed record ScanLine
{
    [System.Text.Json.Serialization.JsonPropertyName("id")] public long Id { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("rpm")] public string? Rpm { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("path")] public string Path { get; init; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("adv")] public int Adv { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("verdict")] public string Verdict { get; init; } = "";
}
