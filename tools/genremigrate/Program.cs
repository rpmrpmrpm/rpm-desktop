using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Core.Models;
using RPM.Core.Scanning;

// Fills in a library's EMPTY genre and subgenre tags from what the catalogues said about each record.
//
// Genre and subgenre are the user's fields: one value each, stored in the file's own tags. This tool
// fills a field only when it is blank and never changes one that has a value. A catalogue answer is
// better than nothing, but a value the user chose is better than a catalogue answer.
//
// It never rewrites an existing genre tag. The generated lists match on substrings of those tags, so
// changing them (for example "Rock | Pop | Folk Rock" to "Rock") would break those lists. It also
// never reads the words in a tag to choose a value: the answers come only from the catalogues.
//
// It writes FILE TAGS and never the database. The tag is the source of truth and the tracks table is a
// mirror of it, so a change made only in the database would be undone by the next scan.
//
//   dotnet run --project tools/genremigrate -- <library root>            what would change
//   dotnet run --project tools/genremigrate -- <library root> --apply    change it

var roots = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
var apply = args.Contains("--apply", StringComparer.Ordinal);

if (roots.Count != 1)
{
    Console.Error.WriteLine("usage: genremigrate <library root> [--apply]");
    return 2;
}

var root = roots[0];
if (!Directory.Exists(root))
{
    Console.Error.WriteLine($"not a directory: {root}");
    return 2;
}

// The library's own database, opened where the app keeps it. Read for what the catalogues said, never
// written: the tag is the source of truth and the mirror follows it.
// Where the app keeps the database of a library in a network folder: LocalData.Libraries in the app.
var thisComputer = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RPM", MirrorLocation.LibrariesFolderName);
using var db = new LibraryDatabase(MirrorLocation.For(root, thisComputer));

var coverage = db.CountReleaseCoverage();
var facts = db.CatalogueFactsByFolder();

Console.WriteLine($"{coverage.Albums} albums, {coverage.Linked} linked to a release, " +
                  $"{coverage.Asked} asked, {coverage.WithStyles} with a style we know");
Console.WriteLine($"{facts.Count} album folders have an answer");
Console.WriteLine();

if (facts.Count == 0)
{
    Console.WriteLine("No catalogue has been asked about anything yet. Let the styles sweep run first.");
    return 0;
}

// What each distinct answer becomes, and how many files take it. Keyed by what will be WRITTEN, which
// is what a reader checks before letting this run.
var plans = new Dictionary<string, Plan>(StringComparer.Ordinal);
var files = new List<(string Path, string? Genre, string? Subgenre)>();

int read = 0, unreadable = 0, unknownFolder = 0, alreadyFilled = 0;

Console.WriteLine($"reading {root}");
foreach (var path in Walk(root))
{
    read++;

    var folder = FolderOf(root, path);
    if (!facts.TryGetValue(folder, out var answer))
    {
        unknownFolder++;
        continue;
    }

    string? genre, subgenre;
    try
    {
        var info = TagReader.Read(path);
        genre = info.Genre;
        subgenre = info.Subgenre;
    }
    catch
    {
        // A file that cannot be read is counted and skipped, so one bad file does not stop a run over
        // a large library.
        unreadable++;
        continue;
    }

    // Only where the field is blank. A value in either field was set by someone (the user, an importer
    // or another tagger), and this tool fills blanks and corrects nothing.
    var wantGenre = string.IsNullOrWhiteSpace(genre) ? answer.Genre : null;
    var wantStyle = string.IsNullOrWhiteSpace(subgenre) ? answer.Styles.FirstOrDefault() : null;

    if (wantGenre is null && wantStyle is null)
    {
        alreadyFilled++;
        continue;
    }

    var key = $"{wantGenre ?? "-"}|{wantStyle ?? "-"}";
    if (!plans.TryGetValue(key, out var plan))
        plans[key] = plan = new Plan(wantGenre, wantStyle);
    plan.Count++;
    files.Add((path, wantGenre, wantStyle));
}

Console.WriteLine();
Console.WriteLine($"{read} files read, {unreadable} unreadable, " +
                  $"{unknownFolder} on albums no catalogue has described, {alreadyFilled} already filled in");
Console.WriteLine($"{files.Count} files would change, across {plans.Count} distinct answers");
Console.WriteLine();

if (plans.Count == 0)
{
    Console.WriteLine("nothing to do.");
    return 0;
}

Console.WriteLine($"{"genre (only if empty)",-26} {"subgenre (only if empty)",-30} files");
Console.WriteLine(new string('-', 70));
foreach (var plan in plans.Values.OrderByDescending(p => p.Count).ThenBy(p => p.Genre, StringComparer.Ordinal))
    Console.WriteLine($"{Fit(plan.Genre ?? "", 26)} {Fit(plan.Subgenre ?? "", 30)} {plan.Count}");
Console.WriteLine();

if (!apply)
{
    Console.WriteLine("dry run. Nothing was written. Pass --apply to write these tags.");
    return 0;
}

int written = 0, failed = 0;
foreach (var (path, genre, subgenre) in files)
{
    try
    {
        // The two fields and NOTHING else. A partial write leaves every field this info does not set
        // exactly as the file had it. Setting only these two is what keeps the art, the ids and all
        // other fields unchanged.
        TagReader.Write(path, new TrackInfo
        {
            FilePath = path,
            Extension = Path.GetExtension(path),
            Genre = genre,
            Subgenre = subgenre,
        });
        written++;
        if (written % 500 == 0)
            Console.WriteLine($"  {written} written...");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"  could not write {path}: {ex.Message}");
    }
}

Console.WriteLine();
Console.WriteLine($"{written} files written, {failed} failed.");
Console.WriteLine("The database is a mirror of the tags and picks these up on the next scan.");
return failed > 0 ? 1 : 0;

// The album folder a file belongs to, spelled the way the mirror spells it: the relative path's own
// directory, forward slashes, empty for a file sitting at the root.
static string FolderOf(string root, string path)
{
    var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
    var slash = relative.LastIndexOf('/');
    return slash < 0 ? "" : relative[..slash];
}

static IEnumerable<string> Walk(string root)
{
    var options = new EnumerationOptions
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System,
    };

    foreach (var path in Directory.EnumerateFiles(root, "*", options))
        if (AudioFileScanner.AudioExtensions.Contains(Path.GetExtension(path)))
            yield return path;
}

static string Fit(string value, int width)
    => value.Length <= width ? value.PadRight(width) : value[..(width - 1)] + "…";

/// <summary>One answer a set of files would take, and how many take it.</summary>
internal sealed class Plan(string? genre, string? subgenre)
{
    public string? Genre { get; } = genre;
    public string? Subgenre { get; } = subgenre;
    public int Count { get; set; }
}
