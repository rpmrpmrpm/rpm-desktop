using System.Text.Json;
using System.Text.RegularExpressions;

namespace RPM.Core.Tests;

/// <summary>
/// Guards the one-way contract between the UI and scripts/strings.json (the single source the .resw files
/// are generated from): every key the app asks for at runtime, {loc:Str Key=…} in XAML, Loc.Get/Loc.Format
/// in code, has to exist there, or the string renders as its own key. The reverse direction is NOT checked:
/// plenty of keys are looked up through variables (menu builders, per-state captions), so an unused-looking
/// key is not necessarily dead.
/// </summary>
public class LocalizationKeyTests
{
    [Fact]
    public void EveryKeyTheUiAsksForExistsInStringsJson()
    {
        var root = RepoRoot();
        var strings = SharedFiles.Path("strings.json");
        Assert.True(File.Exists(strings), $"strings.json not found at {strings}");

        using var doc = JsonDocument.Parse(File.ReadAllText(strings));
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        // The desktop head only. The phone app reads its own table, and scripts/gen_locales.py checks
        // its keys: that script fails on a Loc.get("Key") that names a key this file does not hold.
        var sources = AppSources(Path.Combine(root, "RPM.Desktop"));

        var missing = new List<string>();
        foreach (var file in sources)
        {
            var text = File.ReadAllText(file);
            var used = file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                ? Regex.Matches(text, @"Key=([A-Za-z0-9_]+)").Select(m => m.Groups[1].Value)
                : Regex.Matches(text, @"Loc\.(?:Get|Format)\(""([A-Za-z0-9_]+)""").Select(m => m.Groups[1].Value);

            foreach (var key in used)
                if (!keys.Contains(key))
                    missing.Add($"{Path.GetFileName(file)}: {key}");
        }

        Assert.True(missing.Count == 0, "Keys used by the UI but absent from strings.json:\n" + string.Join("\n", missing));
    }

    private static IEnumerable<string> AppSources(string appDir) =>
        Directory.EnumerateFiles(appDir, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    /// <summary>Walks up from the test binaries to the folder holding RPM.sln.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RPM.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
